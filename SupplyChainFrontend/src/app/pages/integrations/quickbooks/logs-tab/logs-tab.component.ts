import { Component, EventEmitter, Input, OnDestroy, Output, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { SelectModule } from 'primeng/select';
import { TableLazyLoadEvent, TableModule } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { ToggleSwitchModule } from 'primeng/toggleswitch';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';

import {
  QuickBooksIntegrationService, SYNC_KINDS, SYNC_LOG_OPERATIONS, SYNC_LOG_OUTCOMES, SyncKind, SyncLogItemModel, qboErrorMessage
} from '../../../../services/quickbooks-integration.service';
import { QboRetryService, RETRY_STATES, retryToast } from '../../../../shared/components/qbo-sync-badge/qbo-retry.service';
import { KIND_LABEL, Option, prettyJson, sourceRoute, stateLabel } from '../quickbooks.shared';

/** Outcome chip colour: success / refusal / unknown-outcome families (same reading as the Sync tab's attempt log). */
export function outcomeSeverity(outcome: string | null | undefined): 'success' | 'danger' | 'warn' | 'info' | 'secondary' {
  // ProviderOutcomeKind names (+ Exception, DryRun) — matched exactly ("AuthRevoked" contains "ok").
  switch ((outcome || '').trim().toLowerCase()) {
    case 'dryrun': return 'info';
    case 'succeeded': case 'success': return 'success';
    case 'refused': case 'duplicate': case 'notfound': case 'authrevoked': case 'exception': return 'danger';
    case 'transient': case 'throttled': case 'staleobject': return 'warn';
    default: return 'secondary';
  }
}

/**
 * QA-7 — the "Recovered" chip (shown with "Hide recovered" off): a failed call whose record's current state is Synced
 * or Voided. A succeeded call is never "recovered" — nothing failed.
 */
export function isRecovered(r: Pick<SyncLogItemModel, 'state' | 'outcome'>): boolean {
  const ok = ['succeeded', 'success', 'dryrun'].includes((r.outcome ?? '').toLowerCase());
  return !ok && (r.state === 'Synced' || r.state === 'Voided');
}

/** What the Logs tab asks the Sync tab to show when "Sync item" is clicked. */
export interface SyncItemFocus { kind: SyncKind | null; search: string; }

/**
 * A44 D-34 — every call made to QuickBooks (integration.SyncLog): failed only by default, filters, the redacted request /
 * response of a call in its expanded row, Retry (the D-33 action) and Open in QuickBooks.
 */
@Component({
  selector: 'app-qbo-logs-tab',
  standalone: true,
  imports: [
    CommonModule, FormsModule, RouterModule, ButtonModule, InputTextModule, SelectModule, TableModule, TagModule, ToggleSwitchModule,
    TooltipModule
  ],
  templateUrl: './logs-tab.component.html',
  styleUrls: ['../quickbooks-tab.scss', './logs-tab.component.scss']
})
export class LogsTabComponent implements OnDestroy {
  /** Kept for the page's inputs; Retry follows REV-19 (push needs INTEGRATION_SYNC, or a posting target with canAct). */
  @Input() canManage = false;
  @Output() openSyncItem = new EventEmitter<SyncItemFocus>();

  private readonly service = inject(QuickBooksIntegrationService);
  private readonly retryService = inject(QboRetryService);
  private readonly messages = inject(MessageService);

  readonly kindOptions: Option<SyncKind | null>[] = [
    { label: 'Every kind', value: null }, ...SYNC_KINDS.map(k => ({ label: KIND_LABEL[k], value: k as SyncKind | null }))
  ];
  readonly operationOptions: Option<string | null>[] = [
    { label: 'Every operation', value: null }, ...SYNC_LOG_OPERATIONS.map(o => ({ label: o, value: o as string | null }))
  ];
  readonly outcomeOptions: Option<string | null>[] = [
    { label: 'Every outcome', value: null }, ...SYNC_LOG_OUTCOMES.map(o => ({ label: o, value: o as string | null }))
  ];
  readonly outcomeSeverity = outcomeSeverity;
  readonly isRecovered = isRecovered;
  readonly stateLabel = stateLabel;
  readonly prettyJson = prettyJson;

  failedOnly = true;
  /** QA-7 — on by default: calls whose record has since synced (or been voided) are left out. */
  hideRecovered = true;
  kind: SyncKind | null = null;
  operation: string | null = null;
  outcome: string | null = null;
  from = '';
  to = '';
  search = '';

  rows: SyncLogItemModel[] = [];
  total = 0;
  page = 1;
  pageSize = 50;
  isLoading = false;
  loadError = '';

  expanded: Record<string, boolean> = {};
  details: Record<string, SyncLogItemModel | 'loading' | 'error'> = {};
  retrying: Record<string, boolean> = {};
  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  ngOnDestroy(): void { if (this.searchTimer) clearTimeout(this.searchTimer); }

  get hasFilters(): boolean {
    return !!(this.kind || this.operation || this.outcome || this.from || this.to || this.search.trim());
  }

  load(page = 1): void {
    this.isLoading = true;
    this.loadError = '';
    this.service.getCallLog({
      failedOnly: this.failedOnly, unresolvedOnly: this.hideRecovered, kind: this.kind, operation: this.operation, outcome: this.outcome,
      from: this.from || null, to: this.to || null, search: this.search, page, pageSize: this.pageSize
    }).subscribe({
      next: (res) => {
        this.isLoading = false;
        // The server applies unresolvedOnly (D-69a); the page shows what it answers.
        this.rows = res?.data ?? [];
        this.total = res?.totalRecords ?? 0;
        this.page = page;
        this.probeActable();
      },
      error: (err) => {
        this.isLoading = false;
        this.rows = [];
        this.total = 0;
        this.loadError = qboErrorMessage(err, 'The QuickBooks logs could not be loaded.');
      }
    });
  }

  onLazyLoad(e: TableLazyLoadEvent): void {
    this.pageSize = e.rows ?? this.pageSize;
    this.load(Math.floor((e.first ?? 0) / this.pageSize) + 1);
  }

  reload(): void { this.load(1); }

  onSearch(): void {
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => this.reload(), 400);
  }

  clearFilters(): void {
    this.kind = null;
    this.operation = null;
    this.outcome = null;
    this.from = '';
    this.to = '';
    this.search = '';
    this.reload();
  }

  // ── Row ─────────────────────────────────────────────────────────────────────

  docRoute(r: SyncLogItemModel): string[] | null {
    return r.kind && r.externalId ? sourceRoute(r.kind, r.externalId, r.sourceSystem) : null;
  }

  kindLabel(k: string | null | undefined): string { return k ? (KIND_LABEL[k as SyncKind] ?? k) : ''; }

  /** An SCM record whose state is not sent / failed: the D-33 action applies. */
  private retryable(r: SyncLogItemModel): boolean {
    if (!r.kind || !r.externalId) return false;
    if (r.sourceSystem && r.sourceSystem.toUpperCase() !== 'SCM') return false;
    return !r.state || r.state === 'NotSynced' || RETRY_STATES.includes(r.state);
  }

  /** REV-19: shown when the user may push (INTEGRATION_SYNC) or retry one of the document's postings (by-source canAct). */
  canRetry(r: SyncLogItemModel): boolean {
    if (!this.retryable(r)) return false;
    return this.retryService.canPush() || this.actable[r.externalId!] === true;
  }

  /** By-source `canAct` per document, asked only without INTEGRATION_SYNC (once per document id). */
  actable: Record<string, boolean> = {};

  private probeActable(): void {
    if (this.retryService.canPush()) return;
    const ids = [...new Set(this.rows
      .filter(r => this.retryable(r) && this.retryService.mayActOnPostings(r.kind) && this.actable[r.externalId!] === undefined)
      .map(r => `${r.kind}|${r.externalId}`))];
    for (const key of ids) {
      const [kind, id] = key.split('|');
      this.actable = { ...this.actable, [id]: false };
      this.retryService.hasActableTarget(kind as SyncKind, id).subscribe(yes => { this.actable = { ...this.actable, [id]: yes }; });
    }
  }

  retry(r: SyncLogItemModel): void {
    if (!this.canRetry(r) || this.retrying[r.id]) return;
    this.retrying = { ...this.retrying, [r.id]: true };
    this.retryService.run(r.kind!, r.externalId!).subscribe(outcome => {
      this.retrying = { ...this.retrying, [r.id]: false };
      this.messages.add({ ...retryToast(outcome, r.displayLabel), life: 5000 });
      if (outcome.result === 'queued') this.load(this.page);
    });
  }

  showItem(r: SyncLogItemModel): void {
    this.openSyncItem.emit({ kind: r.kind ?? null, search: r.externalId || r.displayLabel || '' });
  }

  /** Expands the row and loads the call's bodies once. */
  toggle(r: SyncLogItemModel): void {
    const open = !this.expanded[r.id];
    this.expanded = { ...this.expanded, [r.id]: open };
    if (!open || (this.details[r.id] && this.details[r.id] !== 'error')) return;
    this.details = { ...this.details, [r.id]: 'loading' };
    this.service.getCallLogEntry(r.id).subscribe({
      next: (d) => { this.details = { ...this.details, [r.id]: d }; },
      error: () => { this.details = { ...this.details, [r.id]: 'error' }; }
    });
  }

  detailOf(r: SyncLogItemModel): SyncLogItemModel | null {
    const d = this.details[r.id];
    return d && d !== 'loading' && d !== 'error' ? d : null;
  }

  isLoadingDetail(r: SyncLogItemModel): boolean { return this.details[r.id] === 'loading'; }
  detailFailed(r: SyncLogItemModel): boolean { return this.details[r.id] === 'error'; }

  copy(text: string | null | undefined): void {
    const value = prettyJson(text);
    if (!value) return;
    const done = () => this.messages.add({ severity: 'success', summary: 'Copied', detail: 'The JSON is on the clipboard.', life: 2000 });
    try {
      navigator.clipboard?.writeText(value).then(done, () => this.copyFailed());
      if (!navigator.clipboard) this.copyFailed();
    } catch {
      this.copyFailed();
    }
  }

  private copyFailed(): void {
    this.messages.add({ severity: 'warn', summary: 'Not copied', detail: 'Select the text and copy it by hand.', life: 3000 });
  }
}
