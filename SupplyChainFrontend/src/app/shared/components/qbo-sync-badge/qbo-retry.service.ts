import { Injectable, Injector, ProviderToken, inject } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { Observable, forkJoin, of } from 'rxjs';
import { catchError, map, switchMap } from 'rxjs/operators';

import { SyncKind, SyncState } from '../../../models/quickbooks-integration.models';
import { QuickBooksIntegrationService, qboErrorMessage } from '../../../services/quickbooks-integration.service';
import { FinancialPostingService, PostingLogRow, PostingTargetStatus, isActionable } from '../../../services/financial-posting.service';
import { AuthService } from '../../../pages/service/auth.service';
import { PERM_MANAGE, PERM_SYNC } from './qbo-sync-state';

/** Kinds that are master data: they have no A44 posting, so the posting log is not asked. */
const MASTER_DATA: SyncKind[] = ['Customer', 'Vendor', 'Item'];

/** States the inline button offers "Retry" for; NotSynced offers "Send to QuickBooks". */
export const RETRY_STATES: SyncState[] = ['Failed', 'Blocked', 'NeedsResolution'];

export type QboRetryResult = 'queued' | 'skippedInMatching' | 'notFound' | 'forbidden' | 'error';

export interface QboRetryOutcome {
  result: QboRetryResult;
  /** Posting targets retried (internal GL / QuickBooks). */
  postingsRetried: number;
  /** `POST sync/push` was made (it needs INTEGRATION_SYNC). Absent = true. */
  pushed?: boolean;
  /** The server's words for an error. */
  message?: string;
}

/**
 * REV-20 — the QuickBooks kind a posting's FinDocType was sent as. POSSale / POSReturn (and anything else) are not
 * decisive (a POS sale can be a receipt or an invoice): null.
 */
export function syncKindForPosting(documentType: string | null | undefined): SyncKind | null {
  switch (documentType) {
    case 'SalesInvoice':  return 'SalesInvoice';
    case 'SalesReceipt':  return 'SalesReceipt';
    case 'RefundReceipt': return 'RefundReceipt';
    case 'CreditNote':    return 'CreditMemo';
    case 'Payment':       return 'Payment';
    case 'BillPayment':   return 'BillPayment';
    case 'PurchaseBill':  return 'Bill';
    case 'VendorCredit':  return 'VendorCredit';
    default:              return null;
  }
}

/** The id is in a push answer's list (ids compared without regard to case). */
export function isListed(list: string[] | null | undefined, id: string): boolean {
  const want = id.toLowerCase();
  return (list ?? []).some(x => (x ?? '').toLowerCase() === want);
}

/** The inline button's label for a state, or null when there is nothing to do. */
export function retryActionLabel(state: SyncState | null | undefined, held = false): string | null {
  if (!state) return null;
  if (state === 'NotSynced') return held ? null : 'Send to QuickBooks';
  return RETRY_STATES.includes(state) ? 'Retry' : null;
}

/** The toast for an outcome. */
export function retryToast(o: QboRetryOutcome, label?: string | null): { severity: 'success' | 'info' | 'warn' | 'error'; summary: string; detail: string } {
  const what = label || 'The record';
  const n = o.postingsRetried;
  const postings = `${n} failed ${n === 1 ? 'posting was' : 'postings were'}`;
  const gl = n ? ` ${postings} retried first.` : '';
  switch (o.result) {
    case 'queued':
      return o.pushed === false
        ? { severity: 'success', summary: 'Retried', detail: `${what}: ${postings} queued again.` }
        : { severity: 'success', summary: 'Queued for QuickBooks', detail: `${what} is queued to be sent to QuickBooks.${gl}` };
    case 'notFound':
      return { severity: 'info', summary: 'Nothing to send', detail: 'Nothing to send for this document.' + gl };
    case 'skippedInMatching':
      return { severity: 'warn', summary: 'Not sent',
        detail: `${what} was skipped in QuickBooks matching, so it is not sent. Change its decision under Match existing first.${gl}` };
    case 'forbidden':
      return { severity: 'error', summary: 'No permission', detail: `You do not have permission to send records to QuickBooks.${gl}` };
    default:
      return { severity: 'error', summary: 'Not sent', detail: (o.message || 'The record could not be sent to QuickBooks.') + gl };
  }
}

/**
 * A44 D-33 (REV-19) — "Send to QuickBooks" / "Retry" from an SCM page, shared by the badge and the Logs tab. Each step
 * runs only when the user may take it:
 *  1. posting retry — every by-source target with `canAct` (the server's per-target rule: internal_gl → GL_SETUP_MANAGE,
 *     external → INTEGRATION_MANAGE). Asked only with one of those codes, and never for master data;
 *  2. `POST sync/push` — only with INTEGRATION_SYNC (SyncController). It also covers documents that never reached the
 *     gateway.
 * The button shows only when at least one step is possible (`canPush()` or `hasActableTarget()`). Never throws.
 */
@Injectable({ providedIn: 'root' })
export class QboRetryService {
  private readonly injector = inject(Injector);

  /** The user may use `sync/push` (INTEGRATION_SYNC). */
  canPush(): boolean { return this.has(PERM_SYNC); }

  /** The user holds a code that can act on some posting target (worth asking by-source). */
  mayActOnPostings(kind: SyncKind | null | undefined): boolean {
    return !!kind && !MASTER_DATA.includes(kind) && (this.has('GL_SETUP_MANAGE') || this.has(PERM_MANAGE));
  }

  /** The document has a posting target the user may retry (by-source `canAct`). False on any error. */
  hasActableTarget(kind: SyncKind, externalId: string): Observable<boolean> {
    if (!this.mayActOnPostings(kind)) return of(false);
    return this.bySource(externalId).pipe(map(rows => rows.some(r => r.targets.some(t => this.actable(t)))));
  }

  /**
   * REV-20 — the kind a document was posted as: the `Post` posting's documentType (by-source), else null (no posting,
   * an undecisive type, or the posting log is not readable).
   */
  postedKind(externalId: string): Observable<SyncKind | null> {
    return this.bySource(externalId).pipe(map(rows => {
      const post = rows.find(r => (r.kind ?? 'Post') === 'Post') ?? rows[0];
      return post ? syncKindForPosting(post.documentType) : null;
    }));
  }

  run(kind: SyncKind, externalId: string): Observable<QboRetryOutcome> {
    const api = this.optional(QuickBooksIntegrationService);
    const push = this.canPush();

    return this.retryPostings(kind, externalId).pipe(
      switchMap(postingsRetried => {
        if (!push || !api) {
          return of<QboRetryOutcome>(postingsRetried
            ? { result: 'queued', postingsRetried, pushed: false }
            : { result: 'forbidden', postingsRetried: 0, pushed: false });
        }
        return api.pushNow(kind, [externalId]).pipe(
          map(res => ({
            result: (isListed(res?.notFound, externalId) ? 'notFound'
              : res?.skippedInMatching?.length ? 'skippedInMatching' : 'queued') as QboRetryResult,
            postingsRetried, pushed: true
          })),
          catchError((err: unknown) => of<QboRetryOutcome>({
            result: (err as HttpErrorResponse)?.status === 403 ? 'forbidden' : 'error',
            postingsRetried, pushed: true,
            message: qboErrorMessage(err, 'The record could not be sent to QuickBooks.')
          })));
      }));
  }

  /** How many posting targets were retried (0 when none, none allowed, or the posting log is not there). */
  private retryPostings(kind: SyncKind, externalId: string): Observable<number> {
    const postings = this.optional(FinancialPostingService);
    if (!postings || !this.mayActOnPostings(kind)) return of(0);
    return this.bySource(externalId).pipe(
      switchMap(rows => {
        const calls = rows.flatMap(r => r.targets
          .filter(t => this.actable(t))
          .map(t => postings.retry(r.uuid, { targetCode: t.targetCode }).pipe(map(() => 1), catchError(() => of(0)))));
        return calls.length ? forkJoin(calls).pipe(map(ns => ns.reduce((a, b) => a + b, 0))) : of(0);
      }));
  }

  /** The server's canAct decides; without it (older server) an open target and the target's manage code. */
  private actable(t: PostingTargetStatus): boolean {
    if (t.canAct === true) return true;
    if (t.canAct === false || !isActionable(t.status)) return false;
    return (t.targetCode === 'internal_gl' && this.has('GL_SETUP_MANAGE')) || (t.targetCode === 'quickbooks' && this.has(PERM_MANAGE));
  }

  private bySource(externalId: string): Observable<PostingLogRow[]> {
    const postings = this.optional(FinancialPostingService);
    if (!postings) return of([]);
    return postings.getBySource(externalId).pipe(catchError(() => of([] as PostingLogRow[])));
  }

  private has(code: string): boolean {
    try { return this.optional(AuthService)?.hasPermission(code) === true; } catch { return false; }
  }

  private optional<T>(token: ProviderToken<T>): T | null {
    try { return this.injector.get(token, null); } catch { return null; }
  }
}
