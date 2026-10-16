import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable, map } from 'rxjs';
import { environment } from '../../environments/environment';

// A44 — Financial Posting Gateway (docs/financial-posting/API-CONTRACT.md §4, CORE): the tenant's internal-GL switches
// (D-8) and the posting log across targets (D-12). GL_VIEW reads, GL_SETUP_MANAGE writes (D-31).
// Written against the contract before the endpoints existed: answers are read tolerantly (see "Details fixed by the
// frontend" in API-CONTRACT.md).

export const POSTING_ROUTE = '/portal/pages/finance/gl/posting';

/** FSD FinDocType (Shared contract §1). */
export type FinDocType =
  | 'SalesInvoice' | 'CreditNote' | 'Payment' | 'PurchaseBill' | 'BillPayment' | 'VendorCredit' | 'POSSale' | 'POSReturn'
  | 'SalesReceipt' | 'RefundReceipt' | 'SalesReturn' | 'PurchaseReturn' | 'InventoryAdjustment' | 'ProductionCost'
  | 'ProductionCompletion' | 'ManualJournal' | 'BankFee' | 'Expense' | 'DepositSlip' | 'OpeningBalance';

/** The document types A44 actually posts (the rest are deferred, D-28). */
export const POSTED_DOC_TYPES: FinDocType[] = [
  'SalesInvoice', 'CreditNote', 'Payment', 'SalesReceipt', 'RefundReceipt', 'POSSale', 'POSReturn',
  'PurchaseBill', 'BillPayment', 'VendorCredit'
];

export const DOC_TYPE_LABEL: Partial<Record<FinDocType, string>> = {
  SalesInvoice: 'Sales invoice', CreditNote: 'Credit note', Payment: 'Customer payment', PurchaseBill: 'Bill',
  BillPayment: 'Bill payment', VendorCredit: 'Vendor credit', POSSale: 'POS sale', POSReturn: 'POS return',
  SalesReceipt: 'Sales receipt', RefundReceipt: 'Refund receipt', SalesReturn: 'Sales return', PurchaseReturn: 'Purchase return',
  InventoryAdjustment: 'Inventory adjustment', ProductionCost: 'Production cost', ProductionCompletion: 'Production completion',
  ManualJournal: 'Manual journal', BankFee: 'Bank fee', Expense: 'Expense', DepositSlip: 'Deposit slip', OpeningBalance: 'Opening balance'
};

export type TargetCode = 'internal_gl' | 'quickbooks';

export const TARGET_LABEL: Record<string, string> = { internal_gl: 'General ledger', quickbooks: 'QuickBooks' };

/**
 * TargetPostingStatus (Posted | Queued | Held | Failed | Skipped) plus the GL queue's own Pending / DeadLetter (D-9).
 */
export type TargetStatus = 'Posted' | 'Queued' | 'Held' | 'Failed' | 'Skipped' | 'Pending' | 'DeadLetter';

export const TARGET_STATUSES: TargetStatus[] = ['Posted', 'Queued', 'Pending', 'Held', 'Failed', 'DeadLetter', 'Skipped'];

/** GET/PUT api/finance/posting/config (API-CONTRACT Details, CORE). */
export interface TenantFinanceConfig {
  internalAccountingEnabled: boolean;
  /** True: journals are posted; false: created as drafts for review. */
  autoPostToGL: boolean;
  /** Read-only: the stored value, else the org's sale base, else the fiscal year's reporting currency. */
  defaultCurrencyCode?: string | null;
  fiscalYearStartMonth?: number | null;
  /** FEATURE_GENERAL_LEDGER is on for the organization. */
  glFeatureEnabled?: boolean | null;
  /** A42 setup status (NotStarted … Complete). */
  setupStatus?: string | null;
  /** Feature on and setup Complete. */
  glReady?: boolean | null;
  /** glReady and internalAccountingEnabled: the GL target takes postings. */
  glActive?: boolean | null;
  notReadyReason?: 'FEATURE_OFF' | 'SETUP_INCOMPLETE' | string | null;
  modifiedAt?: string | null;
}

/** All optional on the server (omitted = keep). */
export interface UpdateTenantFinanceConfig {
  internalAccountingEnabled?: boolean;
  autoPostToGL?: boolean;
  fiscalYearStartMonth?: number;
}

export interface PostingTargetStatus {
  targetCode: TargetCode | string;
  status: TargetStatus | string;
  attempts?: number | null;
  nextAttemptAt?: string | null;
  lastAttemptAt?: string | null;
  lastError?: string | null;
  /** GL: the journal; QuickBooks: the payload ExternalId. */
  internalId?: string | null;
  /** QuickBooks id when known. */
  externalId?: string | null;
  postedAt?: string | null;
  /** by-source only (D-33): open, and the caller holds the target's manage code. Null in list / detail answers. */
  canAct?: boolean | null;
}

export type PostingKind = 'Post' | 'Void' | 'Reverse';

export interface PostingLogRow {
  uuid: string;
  idempotencyKey?: string | null;
  kind?: PostingKind | string | null;
  documentType: FinDocType | string;
  sourceModule?: string | null;
  /** The SCM document's uuid. */
  sourceDocumentUuid?: string | null;
  sourceDocumentNumber: string;
  transactionDate?: string | null;
  partnerUuid?: string | null;
  partnerName?: string | null;
  currencyCode?: string | null;
  /** Σ debit of the lines, document currency. */
  totalAmount?: number | null;
  /** 1 + updates (a changed payload, D-13). */
  version?: number | null;
  /** The worst of its targets: DeadLetter > Failed > Pending > Held > Queued > Posted > Skipped. */
  overallStatus?: TargetStatus | string | null;
  targets: PostingTargetStatus[];
  createdAt?: string | null;
  modifiedAt?: string | null;
}

export interface PostingLogQuery {
  /** A target status: postings having any target in it (`targetCode` narrows which). */
  status?: string | null;
  targetCode?: string | null;
  docType?: string | null;
  sourceModule?: string | null;
  kind?: string | null;
  from?: string | null;
  to?: string | null;
  search?: string | null;
  page?: number;
  pageSize?: number;
}

export interface PostingLogPage {
  data: PostingLogRow[];
  totalRecords: number;
  page: number;
  pageSize: number;
}

/** Counts per target, as the server sends them (flat, camelCase). */
export interface PostingSummaryTarget {
  targetCode: TargetCode | string;
  pending?: number;
  posted?: number;
  queued?: number;
  held?: number;
  failed?: number;
  deadLetter?: number;
  skipped?: number;
  total?: number;
  /** Normalised by the service from the flat fields (or a `counts` map). */
  counts: Partial<Record<TargetStatus, number>>;
}

export interface PostingSummary {
  targets: PostingSummaryTarget[];
  postings?: number | null;
  /** Postings with any Failed or DeadLetter target. */
  needsAttention?: number | null;
  lastPostingAt?: string | null;
}

/** A line of the posting as the caller sent it (document currency; role or explicit code). */
export interface PostingLine {
  lineNumber: number;
  accountRole?: string | null;
  accountCode?: string | null;
  debit: number;
  credit: number;
  label?: string | null;
  taxCode?: string | null;
  taxAmount?: number | null;
}

/** A GL journal line (base currency), when the server sends them with the journal. */
export interface PostingJournalLine {
  lineNumber?: number | null;
  accountCode?: string | null;
  accountName?: string | null;
  /** Memo / description of the line. */
  description?: string | null;
  memo?: string | null;
  debit: number;
  credit: number;
}

export interface PostingJournal {
  uuid: string;
  entryNumber: string;
  entryType: string;
  entryDate: string;
  /** Draft | Posted */
  status: string;
  totalDebit: number;
  totalCredit: number;
  currencyCode?: string | null;
  /** This journal reverses that one. */
  reversesEntryNumber?: string | null;
  isReversed?: boolean | null;
  /** Not in CORE's model yet (see API-CONTRACT FE note); shown when present. */
  lines?: PostingJournalLine[] | null;
}

/** GET log/{uuid}. */
export interface PostingDetail extends PostingLogRow {
  description?: string | null;
  exchangeRate?: number | null;
  partnerType?: string | null;
  documentPayloadType?: string | null;
  lines: PostingLine[];
  journals: PostingJournal[];
}

/** Optional bodies of retry / skip: omitted = every failed (/ pending) target. */
export interface RetryPostingRequest { targetCode?: string | null; }
export interface SkipPostingRequest { targetCode?: string | null; reason?: string | null; }

interface Envelope<T> { success?: boolean; message?: string; result: T; }

function unwrap<T>() {
  return map((r: Envelope<T> | T) => {
    if (r && typeof r === 'object' && 'result' in (r as object)) {
      const e = r as Envelope<T>;
      if (e.success === false) throw Object.assign(new Error(e.message || 'The request did not succeed.'), { error: { message: e.message } });
      return e.result;
    }
    return r as T;
  });
}

function params(values: Record<string, string | number | boolean | null | undefined>): HttpParams {
  let p = new HttpParams();
  for (const [k, v] of Object.entries(values)) {
    if (v === null || v === undefined || v === '') continue;
    p = p.set(k, String(v));
  }
  return p;
}

/**
 * Counts keyed by status, compared without regard to case: the server's flat fields (`posted`, `deadLetter`…) or a
 * `counts` map. Other fields (targetCode, total) are ignored.
 */
function normaliseCounts(source: Record<string, unknown> | null | undefined): Partial<Record<TargetStatus, number>> {
  const out: Partial<Record<TargetStatus, number>> = {};
  const read = (obj: Record<string, unknown> | null | undefined) => {
    for (const [k, v] of Object.entries(obj ?? {})) {
      if (typeof v !== 'number' && typeof v !== 'string') continue;
      const s = TARGET_STATUSES.find(x => x.toLowerCase() === k.toLowerCase().replace(/[\s_]/g, ''));
      if (s) out[s] = (out[s] ?? 0) + (Number(v) || 0);
    }
  };
  const counts = source?.['counts'];
  if (counts && typeof counts === 'object') read(counts as Record<string, unknown>);
  else read(source);
  return out;
}

/** Leaves out blank fields so an empty body is `{}`. */
function clean<T extends object>(body: T): Partial<T> {
  return Object.fromEntries(Object.entries(body ?? {}).filter(([, v]) => v !== null && v !== undefined && v !== '')) as Partial<T>;
}

function normaliseRow(r: PostingLogRow): PostingLogRow {
  return { ...r, targets: Array.isArray(r?.targets) ? r.targets : [] };
}

@Injectable({ providedIn: 'root' })
export class FinancialPostingService {
  private readonly base = `${environment.apiUrl}/finance/posting`;

  constructor(private http: HttpClient) {}

  getConfig(): Observable<TenantFinanceConfig> {
    return this.http.get<Envelope<TenantFinanceConfig>>(`${this.base}/config`).pipe(unwrap<TenantFinanceConfig>());
  }

  updateConfig(body: UpdateTenantFinanceConfig): Observable<TenantFinanceConfig> {
    return this.http.put<Envelope<TenantFinanceConfig>>(`${this.base}/config`, body).pipe(unwrap<TenantFinanceConfig>());
  }

  getLog(q: PostingLogQuery = {}): Observable<PostingLogPage> {
    const p = params({
      status: q.status, targetCode: q.targetCode, docType: q.docType, sourceModule: q.sourceModule, kind: q.kind,
      from: q.from, to: q.to, search: q.search?.trim(), page: q.page ?? 1, pageSize: q.pageSize ?? 25
    });
    return this.http.get<Envelope<PostingLogPage | PostingLogRow[]>>(`${this.base}/log`, { params: p }).pipe(
      unwrap<PostingLogPage | PostingLogRow[]>(),
      map(r => Array.isArray(r)
        ? { data: r.map(normaliseRow), totalRecords: r.length, page: q.page ?? 1, pageSize: q.pageSize ?? 25 }
        : { ...r, data: (r?.data ?? []).map(normaliseRow), totalRecords: r?.totalRecords ?? (r?.data?.length ?? 0) }));
  }

  /**
   * A44 D-33 — every posting of one SCM document (Post and its `…:void`), by the document's uuid. An array, a page or a
   * single posting are all read as a list; 404 (none yet) is the caller's to treat as empty.
   */
  getBySource(sourceUuid: string): Observable<PostingLogRow[]> {
    return this.http.get<Envelope<PostingLogRow[] | PostingLogPage | PostingLogRow | null>>(
      `${this.base}/by-source/${encodeURIComponent(sourceUuid)}`).pipe(
        unwrap<PostingLogRow[] | PostingLogPage | PostingLogRow | null>(),
        map(r => {
          if (!r) return [];
          if (Array.isArray(r)) return r.map(normaliseRow);
          if ('data' in r && Array.isArray((r as PostingLogPage).data)) return (r as PostingLogPage).data.map(normaliseRow);
          if ('uuid' in r) return [normaliseRow(r as PostingLogRow)];
          return [];
        }));
  }

  getEntry(uuid: string): Observable<PostingDetail> {
    return this.http.get<Envelope<PostingDetail>>(`${this.base}/log/${encodeURIComponent(uuid)}`).pipe(
      unwrap<PostingDetail>(),
      map(d => ({ ...normaliseRow(d), lines: d?.lines ?? [], journals: d?.journals ?? [] } as PostingDetail)));
  }

  /** Retries one target, or (no targetCode) every Failed / DeadLetter / Pending one. 409 when nothing is retryable. */
  retry(uuid: string, body: RetryPostingRequest = {}): Observable<PostingLogRow> {
    return this.http.post<Envelope<PostingLogRow>>(`${this.base}/log/${encodeURIComponent(uuid)}/retry`, clean(body))
      .pipe(unwrap<PostingLogRow>(), map(normaliseRow));
  }

  /** Failed / DeadLetter / Pending targets become Skipped ("Skipped by {user}: {reason}"). */
  skip(uuid: string, body: SkipPostingRequest = {}): Observable<PostingLogRow> {
    return this.http.post<Envelope<PostingLogRow>>(`${this.base}/log/${encodeURIComponent(uuid)}/skip`, clean(body))
      .pipe(unwrap<PostingLogRow>(), map(normaliseRow));
  }

  getSummary(): Observable<PostingSummary> {
    return this.http.get<Envelope<PostingSummary | PostingSummaryTarget[]>>(`${this.base}/summary`).pipe(
      unwrap<PostingSummary | PostingSummaryTarget[]>(),
      map(r => {
        const list = Array.isArray(r) ? r : (r?.targets ?? []);
        const head = Array.isArray(r) ? {} : r;
        return {
          ...head,
          targets: list.map(t => ({ ...t, targetCode: t.targetCode, counts: normaliseCounts(t as unknown as Record<string, unknown>) }))
        };
      }));
  }
}

// ── Pure helpers ──────────────────────────────────────────────────────────────

export type PostingTile = 'Posted' | 'Queued' | 'Held' | 'Failed' | 'DeadLetter';

/** The five summary tiles, summed over the targets (Pending counts as queued). */
export function summaryTiles(s: PostingSummary | null | undefined): Record<PostingTile, number> {
  const t: Record<PostingTile, number> = { Posted: 0, Queued: 0, Held: 0, Failed: 0, DeadLetter: 0 };
  for (const target of s?.targets ?? []) {
    const c = target.counts ?? {};
    t.Posted += c.Posted ?? 0;
    t.Queued += (c.Queued ?? 0) + (c.Pending ?? 0);
    t.Held += c.Held ?? 0;
    t.Failed += c.Failed ?? 0;
    t.DeadLetter += c.DeadLetter ?? 0;
  }
  return t;
}

/** sf-pill tone of a target status. */
export function targetTone(status: string | null | undefined): 'ok' | 'in' | 'wn' | 'er' | '' {
  switch ((status ?? '').toLowerCase()) {
    case 'posted': return 'ok';
    case 'queued': case 'pending': return 'in';
    case 'held': return 'wn';
    case 'failed': case 'deadletter': return 'er';
    default: return '';
  }
}

export function targetStatusLabel(status: string | null | undefined): string {
  return (status ?? '').toLowerCase() === 'deadletter' ? 'Dead letter' : (status ?? '—');
}

/** A target that can be retried or skipped by a person. */
export function isActionable(status: string | null | undefined): boolean {
  return ['failed', 'deadletter'].includes((status ?? '').toLowerCase());
}

export function docTypeLabel(t: string | null | undefined): string {
  return DOC_TYPE_LABEL[t as FinDocType] ?? t ?? '';
}

// ── D-36 — journals in the posting detail ─────────────────────────────────────

export interface JournalView extends PostingJournal {
  /** The journal that reverses this one, when it is in the list. */
  reversedBy: string | null;
  lines: PostingJournalLine[];
}

/** Oldest first (as the server sends them), each with "reversed by" worked out from the others' reversesEntryNumber. */
export function journalViews(journals: PostingJournal[] | null | undefined): JournalView[] {
  const list = journals ?? [];
  return list.map(j => ({
    ...j,
    lines: j.lines ?? [],
    reversedBy: list.find(x => x.reversesEntryNumber && x.reversesEntryNumber === j.entryNumber)?.entryNumber ?? null
  }));
}

export interface LineTotals { debit: number; credit: number; balanced: boolean; }

/** Σ debit, Σ credit (2 dp) and whether they agree within the server's 0.001 (BR-01). */
export function lineTotals(lines: { debit: number; credit: number }[] | null | undefined): LineTotals {
  let dr = 0, cr = 0;
  for (const l of lines ?? []) { dr += Number(l.debit) || 0; cr += Number(l.credit) || 0; }
  const debit = Math.round(dr * 100) / 100, credit = Math.round(cr * 100) / 100;
  return { debit, credit, balanced: Math.abs(dr - cr) < 0.001 };
}

/** "SALES_INVOICE" → "Sales invoice". */
export function entryTypeLabel(t: string | null | undefined): string {
  const s = (t ?? '').replace(/_/g, ' ').trim().toLowerCase();
  return s ? s.charAt(0).toUpperCase() + s.slice(1) : '';
}

/** Where the source document lives in SCM, when it has a page. */
export function sourceDocRoute(row: Pick<PostingLogRow, 'documentType' | 'sourceDocumentUuid'>): string[] | null {
  const id = row.sourceDocumentUuid;
  if (!id) return null;
  switch (row.documentType) {
    case 'SalesInvoice': case 'CreditNote': case 'POSSale': case 'POSReturn': case 'SalesReceipt': case 'RefundReceipt':
      return ['/portal/pages/finance/sales-invoices', id];
    case 'Payment': return ['/portal/pages/finance/customer-payments', id];
    case 'PurchaseBill': return ['/portal/pages/finance/invoices', id];
    case 'BillPayment': return ['/portal/pages/finance/payments', id];
    default: return null;
  }
}
