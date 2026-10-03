import { Injectable, Injector, ProviderToken, inject } from '@angular/core';
import { BehaviorSubject, Observable, of } from 'rxjs';
import { catchError, distinctUntilChanged, map, shareReplay } from 'rxjs/operators';

import { ConnectionStatusModel, SyncKind, SyncStatus } from '../../../models/quickbooks-integration.models';
import { QuickBooksIntegrationService } from '../../../services/quickbooks-integration.service';
import { AuthService } from '../../../pages/service/auth.service';
import { TenantService } from '../../../pages/service/tenant.service';
import { PERM_SYNC, PERM_VIEW, QBO_FEATURE } from './qbo-sync-state';

interface Entry {
  kind: SyncKind;
  /** As the first badge asked for it; ids are compared without regard to case. */
  externalId: string;
  /** undefined while it is being looked up, null when it cannot be shown. */
  subject: BehaviorSubject<SyncStatus | null | undefined>;
  fetchedAt: number;
  inFlight: boolean;
}

/**
 * Sync status for the QuickBooks badges on SCM pages.
 *
 * Badges ask for one record each; the store collects every id asked for within a short window and
 * makes ONE POST /status/lookup per kind for all of them, so a list page with fifty rows costs one
 * call per kind. Answers are kept for a while so paging back and forth does not ask again.
 *
 * It never fails a page: when the feature is off, the user may not view integrations, the
 * organization has no QuickBooks connection, or a call fails, the answer is null and the badge shows
 * nothing. Its own dependencies are resolved defensively for the same reason — a host page whose
 * test bed lacks them still renders.
 */
@Injectable({ providedIn: 'root' })
export class QboSyncStatusStore {
  /** How long ids are collected before the lookups go out. */
  static readonly BATCH_WINDOW_MS = 20;
  /** How long an answer is reused before it is looked up again. */
  static readonly STATUS_TTL_MS = 30_000;
  /** How long "is there a connection?" is reused. */
  static readonly CONNECTION_TTL_MS = 60_000;

  private readonly injector = inject(Injector);
  private readonly tenant = this.optional(TenantService);
  private readonly auth = this.optional(AuthService);

  private readonly entries = new Map<string, Entry>();
  private readonly pending = new Map<SyncKind, Entry[]>();
  private flushTimer: ReturnType<typeof setTimeout> | null = null;

  private connected$: Observable<boolean> | null = null;
  private connectedAt = 0;

  /**
   * The feature is on for the organization and the user may view integrations. Reads the tenant
   * signal, so a computed() around it follows the tenant arriving after the page has rendered.
   */
  isAvailable(): boolean {
    try {
      if (!this.tenant || !this.auth) return false;
      if (!this.tenant.hasFeature(QBO_FEATURE)) return false;
      return this.auth.hasPermission(PERM_VIEW) === true;
    } catch {
      return false;
    }
  }

  /** The user may use "Push now". */
  canPush(): boolean {
    try {
      return this.auth?.hasPermission(PERM_SYNC) === true;
    } catch {
      return false;
    }
  }

  /** One record's status: null until it is known, and null for good when it cannot be shown. */
  status(kind: SyncKind | null | undefined, externalId: string | null | undefined): Observable<SyncStatus | null> {
    if (!kind || !externalId || !this.isAvailable()) return of(null);

    const entry = this.entryFor(kind, externalId);
    const stale = entry.fetchedAt === 0 || Date.now() - entry.fetchedAt > QboSyncStatusStore.STATUS_TTL_MS;
    if (!entry.inFlight && stale) this.enqueue(entry);

    return entry.subject.pipe(map(v => v ?? null), distinctUntilChanged());
  }

  /** Looks this record up again (after "Push now"), keeping what is shown until the answer arrives. */
  refresh(kind: SyncKind, externalId: string): void {
    const entry = this.entries.get(this.key(kind, externalId));
    if (!entry || entry.inFlight) return;
    entry.fetchedAt = 0;
    this.enqueue(entry);
  }

  // ── Batching ──────────────────────────────────────────────────────────────────────────────

  private entryFor(kind: SyncKind, externalId: string): Entry {
    const key = this.key(kind, externalId);
    let entry = this.entries.get(key);
    if (!entry) {
      entry = {
        kind, externalId, fetchedAt: 0, inFlight: false,
        subject: new BehaviorSubject<SyncStatus | null | undefined>(undefined)
      };
      this.entries.set(key, entry);
    }
    return entry;
  }

  private enqueue(entry: Entry): void {
    entry.inFlight = true;
    const list = this.pending.get(entry.kind) ?? [];
    list.push(entry);
    this.pending.set(entry.kind, list);

    if (!this.flushTimer) {
      this.flushTimer = setTimeout(() => this.flush(), QboSyncStatusStore.BATCH_WINDOW_MS);
    }
  }

  private flush(): void {
    this.flushTimer = null;
    const batches = [...this.pending.entries()];
    this.pending.clear();
    if (!batches.length) return;

    this.hasConnection().subscribe(connected => {
      for (const [kind, entries] of batches) {
        if (connected) this.lookup(kind, entries);
        else entries.forEach(e => this.settle(e, null));
      }
    });
  }

  private lookup(kind: SyncKind, entries: Entry[]): void {
    const api = this.optional(QuickBooksIntegrationService);
    if (!api) {
      entries.forEach(e => this.settle(e, null));
      return;
    }

    api.lookupStatus(kind, entries.map(e => e.externalId)).subscribe({
      next: (res) => {
        const items = res?.items ?? [];
        for (const e of entries) {
          const id = e.externalId.toLowerCase();
          const found = items.find(s => (s?.externalId ?? '').toLowerCase() === id && (!s.kind || s.kind === kind));
          // A record the gateway has never been given is simply not in QuickBooks yet.
          this.settle(e, found ?? { kind, externalId: e.externalId, state: 'NotSynced' });
        }
      },
      error: () => entries.forEach(e => this.settle(e, null))
    });
  }

  private settle(entry: Entry, value: SyncStatus | null): void {
    entry.inFlight = false;
    entry.fetchedAt = Date.now();
    entry.subject.next(value);
  }

  /**
   * Whether the organization has (or had) a QuickBooks connection — badges stay hidden for an
   * organization that never connected. Revoked and expired connections still show what was synced.
   */
  private hasConnection(): Observable<boolean> {
    const fresh = this.connected$ !== null && Date.now() - this.connectedAt < QboSyncStatusStore.CONNECTION_TTL_MS;
    if (fresh) return this.connected$!;

    const api = this.optional(QuickBooksIntegrationService);
    if (!api) return of(false);

    this.connectedAt = Date.now();
    this.connected$ = api.getConnection().pipe(
      map((c: ConnectionStatusModel) => !!c && (c.isConnected || c.status === 'Revoked' || c.status === 'Expired')),
      catchError(() => of(false)),
      shareReplay(1)
    );
    return this.connected$;
  }

  // ── Plumbing ──────────────────────────────────────────────────────────────────────────────

  private key(kind: SyncKind, externalId: string): string {
    return `${kind}|${externalId.toLowerCase()}`;
  }

  /** A dependency, or null when it (or something it needs) is not provided. */
  private optional<T>(token: ProviderToken<T>): T | null {
    try {
      return this.injector.get(token, null);
    } catch {
      return null;
    }
  }
}
