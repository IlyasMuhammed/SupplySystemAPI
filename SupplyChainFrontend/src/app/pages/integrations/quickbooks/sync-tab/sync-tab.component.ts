import { Component, EventEmitter, Input, OnDestroy, OnInit, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { DialogModule } from 'primeng/dialog';
import { DrawerModule } from 'primeng/drawer';
import { IconFieldModule } from 'primeng/iconfield';
import { InputIconModule } from 'primeng/inputicon';
import { InputTextModule } from 'primeng/inputtext';
import { SelectModule } from 'primeng/select';
import { TableLazyLoadEvent, TableModule } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';
import { ConfirmationService, MessageService } from 'primeng/api';

import {
  IntegrationSettingsModel, PreflightResultModel, QuickBooksIntegrationService, ResolveAction, SYNC_KINDS, SyncItemModel,
  SyncKind, SyncKindSummaryModel, SyncLogModel, SyncMode, SyncState, SyncSummaryModel, qboErrorMessage
} from '../../../../services/quickbooks-integration.service';
import {
  KIND_ICON, KIND_LABEL, KIND_PLURAL, Option, STATE_DISPLAY_ORDER, liveBlockers, prettyJson, sourceRoute, stateLabel,
  stateSeverity, stateTagClass
} from '../quickbooks.shared';

interface KindCard {
  kind: SyncKind;
  total: number;
  /** Non-zero counts, problems first. */
  states: { state: SyncState; count: number }[];
}

/**
 * Every kind, whether or not the server listed it. CountsByState is a dictionary keyed by state
 * name; its keys are read without regard to case in case the API's JSON options camel-case them.
 */
function buildCards(summary: SyncSummaryModel | null): KindCard[] {
  return SYNC_KINDS.map(kind => {
    const k: SyncKindSummaryModel | undefined = summary?.kinds?.find(x => (x.kind ?? '').toLowerCase() === kind.toLowerCase());
    const counts = new Map(Object.entries(k?.countsByState ?? {}).map(([key, n]) => [key.toLowerCase(), Number(n) || 0]));
    return {
      kind,
      total: k?.total ?? 0,
      states: STATE_DISPLAY_ORDER
        .map(state => ({ state, count: counts.get(state.toLowerCase()) ?? 0 }))
        .filter(x => x.count > 0)
    };
  });
}

/** States a retry makes sense for. NeedsResolution is left to a person: a blind retry could create a duplicate. */
const RETRYABLE: SyncState[] = ['Failed', 'Blocked', 'WaitingOnDependency'];
const RESOLVABLE: SyncState[] = ['NeedsResolution', 'Failed'];

/** The sync dashboard: counts per kind, the Dry run ↔ Live switch, every record with its state and attempts. */
@Component({
  selector: 'app-qbo-sync-tab',
  standalone: true,
  imports: [
    CommonModule, FormsModule, RouterModule,
    ButtonModule, ConfirmDialogModule, DialogModule, DrawerModule, IconFieldModule, InputIconModule, InputTextModule,
    SelectModule, TableModule, TagModule, TooltipModule
  ],
  templateUrl: './sync-tab.component.html',
  styleUrls: ['../quickbooks-tab.scss', './sync-tab.component.scss'],
  providers: [ConfirmationService]
})
export class SyncTabComponent implements OnInit, OnDestroy {
  @Input() canManage = false;
  @Input() canSync = false;
  @Input() settings: IntegrationSettingsModel | null = null;
  @Output() settingsChange = new EventEmitter<IntegrationSettingsModel>();
  @Output() summaryChange = new EventEmitter<SyncSummaryModel>();

  readonly kindLabel = KIND_LABEL;
  readonly kindPlural = KIND_PLURAL;
  readonly kindIcon = KIND_ICON;
  readonly stateLabel = stateLabel;
  readonly stateSeverity = stateSeverity;
  readonly stateTagClass = stateTagClass;

  readonly kindOptions: Option<SyncKind | null>[] = [
    { label: 'Every kind', value: null },
    ...SYNC_KINDS.map(k => ({ label: KIND_LABEL[k], value: k as SyncKind | null }))
  ];
  readonly stateOptions: Option<SyncState | null>[] = [
    { label: 'Every state', value: null },
    ...STATE_DISPLAY_ORDER.map(s => ({ label: stateLabel(s), value: s as SyncState | null }))
  ];
  readonly resolveActions: (Option<ResolveAction> & { description: string })[] = [
    { value: 'LinkRemote', label: 'Link to a QuickBooks record', description: 'It is already in QuickBooks: link this record to it by its QuickBooks id.' },
    { value: 'MarkResolved', label: 'Mark as resolved', description: 'QuickBooks is already right. Accept it as synced without sending anything.' },
    { value: 'Requeue', label: 'Send it again', description: 'You checked QuickBooks and it is not there. Queue it to be sent again.' }
  ];

  // ── Summary & mode ──────────────────────────────────────────────────────────
  summary: SyncSummaryModel | null = null;
  isLoadingSummary = false;
  summaryFailed = false;

  preflight: PreflightResultModel | null = null;
  isLoadingPreflight = false;
  isSwitchingMode = false;
  syncingAll: Partial<Record<SyncKind, boolean>> = {};

  // ── Records ─────────────────────────────────────────────────────────────────
  items: SyncItemModel[] = [];
  total = 0;
  page = 1;
  pageSize = 25;
  isLoadingItems = false;
  itemsFailed = false;
  kindFilter: SyncKind | null = null;
  stateFilter: SyncState | null = null;
  search = '';
  busy: Record<string, boolean> = {};
  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  // ── Resolve ─────────────────────────────────────────────────────────────────
  resolveVisible = false;
  resolveItem: SyncItemModel | null = null;
  resolveAction: ResolveAction = 'Requeue';
  resolveRemoteId = '';
  isResolving = false;

  // ── Attempt log ─────────────────────────────────────────────────────────────
  logVisible = false;
  logItem: SyncItemModel | null = null;
  logs: SyncLogModel[] = [];
  isLoadingLog = false;
  logFailed = false;
  readonly prettyJson = prettyJson;

  constructor(
    private service: QuickBooksIntegrationService,
    private messages: MessageService,
    private confirmation: ConfirmationService
  ) {}

  ngOnInit(): void {
    this.loadSummary();
    this.loadPreflight();
  }

  ngOnDestroy(): void {
    if (this.searchTimer) clearTimeout(this.searchTimer);
  }

  // ── Mode ────────────────────────────────────────────────────────────────────

  get mode(): SyncMode { return this.settings?.mode ?? this.summary?.mode ?? 'DryRun'; }
  get isLive(): boolean { return this.mode === 'Live'; }

  /** Live needs the preflight passed and matching confirmed — the server's canGoLive says both. */
  get canGoLive(): boolean { return !!this.preflight?.canGoLive; }

  get liveBlockers(): string[] {
    return this.isLoadingPreflight ? [] : liveBlockers(this.preflight, this.settings);
  }

  loadPreflight(): void {
    this.isLoadingPreflight = true;
    this.service.getPreflight().subscribe({
      next: (p) => { this.isLoadingPreflight = false; this.preflight = p; },
      error: () => { this.isLoadingPreflight = false; this.preflight = null; }
    });
  }

  requestMode(target: SyncMode): void {
    if (!this.canManage || this.isSwitchingMode || target === this.mode) return;
    if (target === 'Live' && !this.canGoLive) return;

    const toLive = target === 'Live';
    this.confirmation.confirm({
      key: 'qbo-sync',
      header: toLive ? 'Switch to Live?' : 'Switch back to dry run?',
      icon: toLive ? 'pi pi-bolt' : 'pi pi-eye',
      message: toLive
        ? 'From now on records are sent to QuickBooks for real: customers, vendors and items as they are needed, and invoices and bills as they are issued and approved. Records waiting in dry run are sent too.'
        : 'Nothing more is sent to QuickBooks. Records keep being checked, and what is already in QuickBooks stays there.',
      acceptLabel: toLive ? 'Go Live' : 'Switch to dry run',
      rejectLabel: 'Cancel',
      acceptButtonStyleClass: toLive ? 'p-button-success' : 'p-button-warn',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => this.setMode(target)
    });
  }

  setMode(target: SyncMode): void {
    if (!this.canManage || this.isSwitchingMode) return;
    this.isSwitchingMode = true;
    this.service.setMode(target).subscribe({
      next: (settings) => {
        this.isSwitchingMode = false;
        this.settings = settings;
        this.settingsChange.emit(settings);
        this.messages.add(settings.mode === 'Live'
          ? { severity: 'success', summary: 'Live', detail: 'Records are now sent to QuickBooks.' }
          : { severity: 'info', summary: 'Dry run', detail: 'Nothing is sent to QuickBooks until you switch back to Live.' });
        this.loadSummary();
      },
      error: (err) => {
        this.isSwitchingMode = false;
        this.messages.add({ severity: 'error', summary: 'Mode not changed', detail: qboErrorMessage(err, 'The mode could not be changed.') });
        this.loadPreflight();
      }
    });
  }

  // ── Summary ─────────────────────────────────────────────────────────────────

  loadSummary(): void {
    this.isLoadingSummary = true;
    this.summaryFailed = false;
    this.service.getSyncSummary().subscribe({
      next: (s) => {
        this.isLoadingSummary = false;
        this.summary = s;
        this.cards = buildCards(s);
        this.summaryChange.emit(s);
      },
      error: () => { this.isLoadingSummary = false; this.summaryFailed = true; }
    });
  }

  /** One card per kind, in a fixed order — built when the summary arrives, not on every render. */
  cards: KindCard[] = buildCards(null);

  /** "Sync all": every SCM record of this kind is handed to the gateway again. */
  syncAll(kind: SyncKind): void {
    if (!this.canManage || this.syncingAll[kind]) return;
    this.syncingAll = { ...this.syncingAll, [kind]: true };
    this.service.backfill(kind).subscribe({
      next: (res) => {
        this.syncingAll = { ...this.syncingAll, [kind]: false };
        this.messages.add({
          severity: 'success', summary: 'Sent to the gateway',
          detail: `${res.sent} ${KIND_PLURAL[kind]} handed over${this.isLive ? ' to be sent to QuickBooks.' : ' and checked in dry run.'}`
        });
        this.loadSummary();
        this.loadItems(this.page);
      },
      error: (err) => {
        this.syncingAll = { ...this.syncingAll, [kind]: false };
        this.messages.add({ severity: 'error', summary: 'Not sent', detail: qboErrorMessage(err, `The ${KIND_PLURAL[kind]} could not be sent.`) });
      }
    });
  }

  /** A count on a card filters the table to it. */
  filterBy(kind: SyncKind, state: SyncState | null): void {
    this.kindFilter = kind;
    this.stateFilter = state;
    this.loadItems(1);
  }

  // ── Records ─────────────────────────────────────────────────────────────────

  loadItems(page = 1): void {
    this.isLoadingItems = true;
    this.itemsFailed = false;
    this.service.getSyncItems({
      kind: this.kindFilter, state: this.stateFilter, search: this.search, page, pageSize: this.pageSize
    }).subscribe({
      next: (res) => {
        this.isLoadingItems = false;
        this.items = res?.data ?? [];
        this.total = res?.totalRecords ?? 0;
        this.page = page;
      },
      error: () => { this.isLoadingItems = false; this.itemsFailed = true; this.items = []; this.total = 0; }
    });
  }

  onLazyLoad(event: TableLazyLoadEvent): void {
    this.pageSize = event.rows ?? this.pageSize;
    this.loadItems(Math.floor((event.first ?? 0) / this.pageSize) + 1);
  }

  onFilterChange(): void { this.loadItems(1); }

  onSearchChange(): void {
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => this.loadItems(1), 400);
  }

  clearFilters(): void {
    this.kindFilter = null;
    this.stateFilter = null;
    this.search = '';
    this.loadItems(1);
  }

  link(item: SyncItemModel): string[] | null { return sourceRoute(item.kind, item.externalId, item.sourceSystem); }

  labelOf(kind: string): string { return KIND_LABEL[kind as SyncKind] ?? kind; }
  iconOf(kind: string): string { return KIND_ICON[kind as SyncKind] ?? 'pi pi-circle'; }

  canRetry(item: SyncItemModel): boolean { return this.canSync && RETRYABLE.includes(item.state); }
  canResolve(item: SyncItemModel): boolean { return this.canManage && RESOLVABLE.includes(item.state); }
  /** Push now asks SCM for the record again, so only for records SCM sent. */
  canPush(item: SyncItemModel): boolean { return this.canSync && (!item.sourceSystem || item.sourceSystem.toUpperCase() === 'SCM'); }

  private replace(updated: SyncItemModel): void {
    this.items = this.items.map(i => (i.id === updated.id ? { ...i, ...updated } : i));
  }

  retry(item: SyncItemModel): void {
    if (!this.canRetry(item) || this.busy[item.id]) return;
    this.busy = { ...this.busy, [item.id]: true };
    this.service.retrySyncItem(item.id).subscribe({
      next: (updated) => {
        this.busy = { ...this.busy, [item.id]: false };
        if (updated) this.replace(updated);
        this.messages.add({ severity: 'success', summary: 'Queued', detail: `${item.displayLabel || 'The record'} is queued to be tried again.` });
        this.loadSummary();
      },
      error: (err) => {
        this.busy = { ...this.busy, [item.id]: false };
        this.messages.add({ severity: 'error', summary: 'Not retried', detail: qboErrorMessage(err, 'The record could not be retried.') });
      }
    });
  }

  pushNow(item: SyncItemModel): void {
    if (!this.canPush(item) || this.busy[item.id]) return;
    this.busy = { ...this.busy, [item.id]: true };
    this.service.pushNow(item.kind, [item.externalId]).subscribe({
      next: () => {
        this.busy = { ...this.busy, [item.id]: false };
        this.messages.add({ severity: 'success', summary: 'Pushed', detail: `${item.displayLabel || 'The record'} was sent from SCM again.` });
        this.loadItems(this.page);
        this.loadSummary();
      },
      error: (err) => {
        this.busy = { ...this.busy, [item.id]: false };
        this.messages.add({ severity: 'error', summary: 'Not pushed', detail: qboErrorMessage(err, 'The record could not be pushed.') });
      }
    });
  }

  // ── Resolve ─────────────────────────────────────────────────────────────────

  openResolve(item: SyncItemModel): void {
    if (!this.canResolve(item)) return;
    this.resolveItem = item;
    this.resolveAction = item.remoteId ? 'MarkResolved' : 'Requeue';
    this.resolveRemoteId = item.remoteId ?? '';
    this.resolveVisible = true;
  }

  get canSubmitResolve(): boolean {
    if (!this.resolveItem || this.isResolving) return false;
    return this.resolveAction !== 'LinkRemote' || !!this.resolveRemoteId.trim();
  }

  resolve(): void {
    const item = this.resolveItem;
    if (!item || !this.canSubmitResolve || !this.canManage) return;
    this.isResolving = true;

    this.service.resolveSyncItem(item.id, {
      action: this.resolveAction,
      remoteId: this.resolveAction === 'LinkRemote' ? this.resolveRemoteId.trim() : null
    }).subscribe({
      next: (updated) => {
        this.isResolving = false;
        this.resolveVisible = false;
        if (updated) this.replace(updated);
        this.messages.add({ severity: 'success', summary: 'Resolved', detail: `${item.displayLabel || 'The record'} is resolved.` });
        this.loadSummary();
      },
      error: (err) => {
        this.isResolving = false;
        this.messages.add({ severity: 'error', summary: 'Not resolved', detail: qboErrorMessage(err, 'The record could not be resolved.') });
      }
    });
  }

  // ── Attempt log ─────────────────────────────────────────────────────────────

  openLog(item: SyncItemModel): void {
    this.logItem = item;
    this.logs = [];
    this.logVisible = true;
    this.isLoadingLog = true;
    this.logFailed = false;

    this.service.getSyncLog(item.id).subscribe({
      next: (logs) => {
        if (this.logItem?.id !== item.id) return;
        this.isLoadingLog = false;
        this.logs = [...(logs ?? [])].sort((a, b) => (b.createdAt ?? '').localeCompare(a.createdAt ?? ''));
      },
      error: () => {
        if (this.logItem?.id !== item.id) return;
        this.isLoadingLog = false;
        this.logFailed = true;
      }
    });
  }

  outcomeSeverity(outcome: string): 'success' | 'danger' | 'warn' | 'info' | 'secondary' {
    const o = (outcome || '').toLowerCase();
    if (/(success|succeeded|ok|created|updated|synced|dryrun)/.test(o)) return 'success';
    if (/(fail|error|refused|invalid|blocked)/.test(o)) return 'danger';
    if (/(unknown|timeout|retry)/.test(o)) return 'warn';
    return 'secondary';
  }
}
