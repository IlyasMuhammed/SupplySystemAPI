import { Component, OnDestroy, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { DrawerModule } from 'primeng/drawer';
import { InputTextModule } from 'primeng/inputtext';
import { SelectModule } from 'primeng/select';
import { TableLazyLoadEvent, TableModule } from 'primeng/table';
import { ToastModule } from 'primeng/toast';
import { ToggleSwitchModule } from 'primeng/toggleswitch';
import { TooltipModule } from 'primeng/tooltip';
import { ConfirmationService, MessageService } from 'primeng/api';

import {
  FinancialPostingService, JournalView, LineTotals, POSTED_DOC_TYPES, PostingDetail, PostingLogRow, PostingSummary, PostingTargetStatus,
  PostingTile, TARGET_LABEL, TARGET_STATUSES, TenantFinanceConfig, docTypeLabel, entryTypeLabel, isActionable, journalViews, lineTotals,
  sourceDocRoute, summaryTiles, targetStatusLabel, targetTone
} from '../../../../services/financial-posting.service';
import { FEATURE_GENERAL_LEDGER, GL_PERMS, GL_SETUP_ROUTE } from '../../../../services/accounting.service';
import { AuthService } from '../../../service/auth.service';
import { TenantService } from '../../../service/tenant.service';
import { FLOW } from '../../../../shared/flow';
import { glError, money } from '../gl.shared';

const TILES: { key: PostingTile; label: string; status: string; tone: string }[] = [
  { key: 'Posted', label: 'Posted', status: 'Posted', tone: 'ok' },
  { key: 'Queued', label: 'Queued', status: 'Queued', tone: 'in' },
  { key: 'Held', label: 'Held', status: 'Held', tone: 'wn' },
  { key: 'Failed', label: 'Failed', status: 'Failed', tone: 'er' },
  { key: 'DeadLetter', label: 'Dead letter', status: 'DeadLetter', tone: 'er' }
];

/**
 * A44 D-8 / D-12 / D-30 — `/gl/posting`: whether documents post to the internal general ledger (and as posted or draft
 * journals), and the posting log — every document the gateway was given, with its status per target (general ledger,
 * QuickBooks), the error, and retry / skip. Opens with FEATURE_GENERAL_LEDGER or MODULE_INTEGRATION (D-80).
 */
@Component({
  selector: 'app-gl-posting',
  standalone: true,
  imports: [
    CommonModule, FormsModule, RouterModule, ButtonModule, ConfirmDialogModule, DrawerModule, InputTextModule, SelectModule, TableModule,
    ToastModule, ToggleSwitchModule, TooltipModule, ...FLOW
  ],
  providers: [MessageService, ConfirmationService],
  styleUrls: ['../gl.scss'],
  template: `
<p-toast></p-toast>
<p-confirmDialog key="gl-posting" [style]="{ width: '30rem', maxWidth: '96vw' }"></p-confirmDialog>
<sf-page [crumbs]="['Finance', 'General Ledger', 'Posting']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button icon="pi pi-refresh" label="Refresh" [text]="true" (onClick)="refresh()" data-testid="refresh"></p-button>
    <span class="sf-end" *ngIf="qboOn"><a class="sf-mu sf-sm" routerLink="/portal/pages/integrations/quickbooks" data-testid="qbo-link">
      <i class="pi pi-sync"></i> QuickBooks integration</a></span>
  </div>

  <div class="sf-body">
    <div class="sf-lr-head"><div><h1>Financial posting</h1>
      <div class="sf-sub">Where invoices, payments, bills and credits are posted — the general ledger and QuickBooks — and how each one went</div></div></div>

    <!-- ── Tiles ── -->
    <div class="gl-note er" *ngIf="summaryError" data-testid="summary-error"><i class="pi pi-exclamation-triangle"></i><div>{{ summaryError }}</div></div>
    <div class="sf-grid sf-tiles" data-testid="tiles">
      <button type="button" class="sf-tile post-tile" *ngFor="let t of tileDefs" [class.on]="statusFilter === t.status"
              (click)="filterStatus(t.status)" [attr.data-testid]="'tile-' + t.key">
        <span class="l">{{ t.label }}</span>
        <span class="v" [ngClass]="tiles[t.key] ? t.tone : ''">{{ tiles[t.key] | number }}</span>
      </button>
    </div>

    <!-- ── Internal GL (D-8) ── -->
    <section class="sf-card" data-testid="config-card" *ngIf="canReadConfig">
      <div class="sf-ch"><h2>Internal general ledger</h2></div>
      <div class="sf-empty" *ngIf="configLoading && !config"><i class="pi pi-spin pi-spinner"></i> Loading…</div>
      <div class="gl-note er" *ngIf="configError" data-testid="config-error"><i class="pi pi-exclamation-triangle"></i><div>{{ configError }}</div></div>

      <div class="gl-note wn" *ngIf="!glOn" data-testid="gl-off">
        <i class="pi pi-info-circle"></i><div>The general ledger is not part of this organization's plan, so documents are only sent to QuickBooks.</div>
      </div>

      <ng-container *ngIf="config && glOn">
        <div class="gl-note wn" *ngIf="glNotReady && draft.internalAccountingEnabled" data-testid="gl-not-ready">
          <i class="pi pi-exclamation-triangle"></i>
          <div>General ledger setup is not complete, so nothing is posted yet (postings are skipped as "GL not ready").
            <a [routerLink]="glSetupRoute">Finish the setup</a>.</div>
        </div>

        <div class="post-switches">
          <label class="post-switch">
            <p-toggleswitch [(ngModel)]="draft.internalAccountingEnabled" [disabled]="!canManage || saving" data-testid="enabled"></p-toggleswitch>
            <span><strong>Post to the internal general ledger</strong>
              <span class="sf-mu sf-sm">Each document becomes a journal entry. Off: only QuickBooks (when connected) receives them.</span></span>
          </label>
          <label class="post-switch" [class.dim]="!draft.internalAccountingEnabled">
            <p-toggleswitch [(ngModel)]="draft.autoPostToGL" [disabled]="!canManage || saving || !draft.internalAccountingEnabled" data-testid="auto-post"></p-toggleswitch>
            <span><strong>Post journals automatically</strong>
              <span class="sf-mu sf-sm">Off: journals are created as drafts for an accountant to review and post.</span></span>
          </label>
        </div>
        <div class="sf-mu sf-sm" *ngIf="config.defaultCurrencyCode">Journals are in {{ config.defaultCurrencyCode }} (the organization's base currency).</div>

        <div class="post-actions" *ngIf="canManage">
          <span class="sf-mu sf-sm" *ngIf="configDirty">Unsaved changes</span>
          <span class="sf-end"></span>
          <p-button label="Discard" [text]="true" severity="secondary" [disabled]="!configDirty || saving" (onClick)="discard()" data-testid="discard"></p-button>
          <p-button label="Save" icon="pi pi-save" [disabled]="!configDirty || saving" [loading]="saving" (onClick)="saveConfig()" data-testid="save-config"></p-button>
        </div>
      </ng-container>
    </section>

    <!-- ── Posting log (D-12) ── -->
    <section class="sf-card p0" data-testid="log-card">
      <div class="sf-ch sf-pad"><h2>Posting log <span class="sf-count">{{ total }}</span></h2></div>
      <div class="sf-filters post-filters">
        <p-select [options]="docTypeOptions" [(ngModel)]="docTypeFilter" (ngModelChange)="reload()" optionLabel="label" optionValue="value"
                  appendTo="body" data-testid="doc-type-filter"></p-select>
        <p-select [options]="statusOptions" [(ngModel)]="statusFilter" (ngModelChange)="reload()" optionLabel="label" optionValue="value"
                  appendTo="body" data-testid="status-filter"></p-select>
        <input pInputText type="date" [(ngModel)]="from" (change)="reload()" aria-label="From" data-testid="from" />
        <input pInputText type="date" [(ngModel)]="to" (change)="reload()" aria-label="To" data-testid="to" />
        <input pInputText [(ngModel)]="search" (ngModelChange)="onSearch()" placeholder="Document number…" data-testid="search" />
        <p-button icon="pi pi-filter-slash" [text]="true" severity="secondary" pTooltip="Clear filters" (onClick)="clearFilters()" data-testid="clear"></p-button>
      </div>

      <div class="gl-note er post-inset" *ngIf="logError" data-testid="log-error"><i class="pi pi-exclamation-triangle"></i>
        <div>{{ logError }} <a href="" (click)="$event.preventDefault(); loadLog(page)">Try again</a></div></div>

      <div class="gl-scroll">
        <p-table [value]="rows" [lazy]="true" (onLazyLoad)="onLazyLoad($event)" [loading]="logLoading" [paginator]="true" [rows]="pageSize"
                 [totalRecords]="total" [first]="(page - 1) * pageSize" [rowsPerPageOptions]="[25, 50, 100]" dataKey="uuid"
                 styleClass="p-datatable-sm" [tableStyle]="{ 'min-width': '64rem' }" data-testid="log">
          <ng-template pTemplate="header">
            <tr><th>Document</th><th>Type</th><th>Date</th><th class="gl-num">Amount</th>
              <th *ngFor="let t of targets">{{ targetLabel(t) }}</th><th>Error</th><th style="width:8rem"></th></tr>
          </ng-template>
          <ng-template pTemplate="body" let-r>
            <tr [attr.data-testid]="'row-' + r.uuid">
              <td>
                <a *ngIf="docRoute(r) as route; else plainDoc" [routerLink]="route" class="sf-mono">{{ r.sourceDocumentNumber || '—' }}</a>
                <ng-template #plainDoc><span class="sf-mono">{{ r.sourceDocumentNumber || '—' }}</span></ng-template>
                <div class="sf-mu sf-sm" *ngIf="r.partnerName">{{ r.partnerName }}</div>
              </td>
              <td>{{ typeLabel(r.documentType) }}
                <span class="sf-mu sf-sm" *ngIf="r.kind && r.kind !== 'Post'"> · {{ r.kind }}</span>
                <span class="sf-mu sf-sm" *ngIf="r.version && r.version > 1"> · v{{ r.version }}</span></td>
              <td class="sf-nowrap">{{ r.transactionDate ? (r.transactionDate | date:'dd MMM yyyy') : '—' }}</td>
              <td class="gl-num">{{ r.totalAmount != null ? amount(r.totalAmount) : '—' }} <span class="sf-mu sf-sm">{{ r.currencyCode || '' }}</span></td>
              <td *ngFor="let t of targets">
                <ng-container *ngIf="target(r, t) as s; else noTarget">
                  <span class="sf-pill" [ngClass]="tone(s.status)" [pTooltip]="s.lastError || ''" [tooltipDisabled]="!s.lastError"
                        [attr.data-testid]="'chip-' + r.uuid + '-' + t">{{ statusLabel(s.status) }}</span>
                  <div class="sf-mu sf-sm sf-mono" *ngIf="refOf(s) as ref">{{ ref }}</div>
                  <div class="sf-mu sf-sm" *ngIf="s.attempts && s.attempts > 1">{{ s.attempts }} attempts</div>
                  <div class="sf-mu sf-sm" *ngIf="s.nextAttemptAt && s.status === 'Failed'">next try {{ s.nextAttemptAt | date:'d MMM, HH:mm' }}</div>
                </ng-container>
                <ng-template #noTarget><span class="sf-mu">—</span></ng-template>
              </td>
              <td class="post-error">{{ errorOf(r) || '—' }}</td>
              <td>
                <p-button icon="pi pi-book" [text]="true" size="small" severity="secondary" pTooltip="Journals and lines"
                          (onClick)="openDetail(r)" [attr.data-testid]="'open-' + r.uuid"></p-button>
                <span class="gl-actions" *ngIf="canAct && actionable(r)">
                  <p-button icon="pi pi-replay" [text]="true" size="small" pTooltip="Retry" [disabled]="!!busy[r.uuid]" (onClick)="retry(r)"
                            [attr.data-testid]="'retry-' + r.uuid"></p-button>
                  <p-button icon="pi pi-ban" [text]="true" size="small" severity="secondary" pTooltip="Skip (do not retry)" [disabled]="!!busy[r.uuid]"
                            (onClick)="requestSkip(r)" [attr.data-testid]="'skip-' + r.uuid"></p-button>
                </span>
              </td>
            </tr>
          </ng-template>
          <ng-template pTemplate="emptymessage">
            <tr><td [attr.colspan]="6 + targets.length" class="sf-empty" data-testid="log-empty">
              {{ hasFilters ? 'No posting matches the filters.' : 'Nothing has been posted yet. Issued invoices, payments and approved bills appear here.' }}</td></tr>
          </ng-template>
        </p-table>
      </div>
    </section>
  </div>
</sf-page>

<!-- ── D-36: one posting's journals and lines ── -->
<p-drawer [(visible)]="detailVisible" position="right" styleClass="post-drawer" data-testid="detail-drawer">
  <ng-template pTemplate="header">
    <div class="post-dhead">
      <strong>{{ detailRow?.sourceDocumentNumber || 'Posting' }}</strong>
      <span class="sf-mu sf-sm" *ngIf="detailRow">{{ typeLabel(detailRow.documentType) }}
        <ng-container *ngIf="detailRow.transactionDate"> · {{ detailRow.transactionDate | date:'dd MMM yyyy' }}</ng-container></span>
    </div>
  </ng-template>

  <div class="sf-empty" *ngIf="detailLoading"><i class="pi pi-spin pi-spinner"></i> Loading…</div>
  <div class="gl-note er" *ngIf="detailError" data-testid="detail-error"><i class="pi pi-exclamation-triangle"></i><div>{{ detailError }}</div></div>

  <ng-container *ngIf="detail && !detailLoading">
    <div class="sf-mu sf-sm post-desc" *ngIf="detail.description">{{ detail.description }}</div>

    <div class="gl-note" *ngIf="!journals.length" data-testid="no-journal">
      <i class="pi pi-info-circle"></i><div>{{ noJournalText }}</div>
    </div>

    <section class="sf-card p0 post-journal" *ngFor="let j of journals" [attr.data-testid]="'journal-' + j.entryNumber">
      <div class="sf-ch sf-pad post-jhead">
        <h2 class="sf-mono">{{ j.entryNumber }}</h2>
        <span class="sf-pill" [ngClass]="j.status === 'Posted' ? 'ok' : 'wn'" [attr.data-testid]="'jstatus-' + j.entryNumber">{{ j.status }}</span>
        <span class="sf-mu sf-sm">{{ entryType(j.entryType) }} · {{ j.entryDate | date:'dd MMM yyyy' }}</span>
      </div>
      <div class="post-jmeta sf-sm">
        <span *ngIf="j.reversesEntryNumber" [attr.data-testid]="'reverses-' + j.entryNumber">Reverses <span class="sf-mono">{{ j.reversesEntryNumber }}</span></span>
        <span *ngIf="j.reversedBy || j.isReversed" [attr.data-testid]="'reversed-' + j.entryNumber">
          Reversed<ng-container *ngIf="j.reversedBy"> by <span class="sf-mono">{{ j.reversedBy }}</span></ng-container></span>
        <span class="sf-end">Dr {{ amount(j.totalDebit) }} · Cr {{ amount(j.totalCredit) }} <span class="sf-mu">{{ j.currencyCode || '' }}</span></span>
      </div>
      <div class="gl-scroll" *ngIf="j.lines.length">
        <table class="post-lines" [attr.data-testid]="'jlines-' + j.entryNumber">
          <thead><tr><th>Account</th><th>Memo</th><th class="gl-num">Debit</th><th class="gl-num">Credit</th></tr></thead>
          <tbody>
            <tr *ngFor="let l of j.lines">
              <td><span class="sf-mono">{{ l.accountCode || '—' }}</span> {{ l.accountName || '' }}</td>
              <td>{{ l.memo || l.description || '' }}</td>
              <td class="gl-num">{{ l.debit ? amount(l.debit) : '' }}</td>
              <td class="gl-num">{{ l.credit ? amount(l.credit) : '' }}</td>
            </tr>
          </tbody>
          <tfoot>
            <tr class="sf-tot" *ngIf="totals(j.lines) as t">
              <td colspan="2">Total <span class="post-bal" [class.bad]="!t.balanced">{{ t.balanced ? 'balanced' : 'not balanced' }}</span></td>
              <td class="gl-num">{{ amount(t.debit) }}</td><td class="gl-num">{{ amount(t.credit) }}</td>
            </tr>
          </tfoot>
        </table>
      </div>
    </section>

    <!-- The server sends no journal lines yet: the posting's own lines, as the document posted them. -->
    <section class="sf-card p0 post-journal" *ngIf="journals.length && !anyJournalLines && detail.lines.length" data-testid="posted-lines">
      <div class="sf-ch sf-pad"><h2>Posted lines</h2>
        <span class="sf-mu sf-sm">As the document sent them, in {{ detail.currencyCode || 'document currency' }}; accounts by role</span></div>
      <div class="gl-scroll">
        <table class="post-lines">
          <thead><tr><th>Account</th><th>Memo</th><th class="gl-num">Debit</th><th class="gl-num">Credit</th></tr></thead>
          <tbody>
            <tr *ngFor="let l of detail.lines">
              <td class="sf-mono">{{ l.accountCode || roleLabel(l.accountRole) }}</td>
              <td>{{ l.label || '' }}</td>
              <td class="gl-num">{{ l.debit ? amount(l.debit) : '' }}</td>
              <td class="gl-num">{{ l.credit ? amount(l.credit) : '' }}</td>
            </tr>
          </tbody>
          <tfoot>
            <tr class="sf-tot" *ngIf="totals(detail.lines) as t">
              <td colspan="2">Total <span class="post-bal" [class.bad]="!t.balanced">{{ t.balanced ? 'balanced' : 'not balanced' }}</span></td>
              <td class="gl-num">{{ amount(t.debit) }}</td><td class="gl-num">{{ amount(t.credit) }}</td>
            </tr>
          </tfoot>
        </table>
      </div>
    </section>
  </ng-container>
</p-drawer>`,
  styles: [`
    .post-tile { font: inherit; text-align: left; cursor: pointer; border: 1px solid var(--sms-border); }
    .post-tile.on { border-color: var(--sms-primary); box-shadow: 0 0 0 1px var(--sms-primary-soft-border); }
    .post-tile .v.ok { color: var(--sms-ok); } .post-tile .v.in { color: var(--sms-primary-text); }
    .post-tile .v.wn { color: var(--sms-warn); } .post-tile .v.er { color: var(--sms-danger); }
    .post-switches { display: flex; flex-direction: column; gap: .75rem; margin: .5rem 0; }
    .post-switch { display: flex; gap: .75rem; align-items: flex-start; cursor: pointer; }
    .post-switch > span { display: flex; flex-direction: column; gap: .125rem; }
    .post-switch.dim { opacity: .6; }
    .post-actions { display: flex; align-items: center; gap: .5rem; flex-wrap: wrap; margin-top: .75rem; padding-top: .75rem;
      border-top: 1px solid var(--sms-border-soft); }
    .post-filters { padding: 0 1rem .75rem; flex-wrap: wrap; }
    .post-filters input[type=date] { width: 10rem; }
    .post-inset { margin: 0 1rem .75rem; }
    .post-error { max-width: 22rem; font-size: .8125rem; color: var(--sms-danger); }
    .sf-nowrap { white-space: nowrap; }
    :host ::ng-deep .post-drawer { width: 44rem; max-width: 100vw; }
    .post-dhead { display: flex; flex-direction: column; gap: .125rem; }
    .post-desc { margin-bottom: .75rem; }
    .post-journal { margin-top: .75rem; }
    .post-jhead { display: flex; align-items: center; gap: .5rem; flex-wrap: wrap; }
    .post-jhead h2 { margin: 0; }
    .post-jmeta { display: flex; flex-wrap: wrap; gap: .25rem 1rem; padding: 0 1rem .6rem; color: var(--sms-text-muted); }
    .post-lines { width: 100%; min-width: 28rem; border-collapse: collapse; font-size: .85rem; }
    .post-lines th, .post-lines td { padding: .4rem .75rem; border-top: 1px solid var(--sms-border-soft); text-align: left; }
    .post-lines th.gl-num, .post-lines td.gl-num { text-align: right; }
    .post-lines tfoot td { font-weight: 600; }
    .post-bal { font-weight: 400; font-size: .75rem; color: var(--sms-ok); margin-left: .375rem; }
    .post-bal.bad { color: var(--sms-danger); }
  `]
})
export class GlPostingComponent implements OnInit, OnDestroy {
  private readonly api = inject(FinancialPostingService);
  private readonly auth = inject(AuthService);
  private readonly tenant = inject(TenantService, { optional: true });
  private readonly messages = inject(MessageService);
  private readonly confirmation = inject(ConfirmationService);

  readonly glSetupRoute = GL_SETUP_ROUTE;
  readonly tileDefs = TILES;
  readonly targets = ['internal_gl', 'quickbooks'];

  readonly docTypeOptions = [{ label: 'Every type', value: null as string | null },
    ...POSTED_DOC_TYPES.map(t => ({ label: docTypeLabel(t), value: t as string | null }))];
  readonly statusOptions = [{ label: 'Every status', value: null as string | null },
    ...TARGET_STATUSES.map(s => ({ label: targetStatusLabel(s), value: s as string | null }))];

  // ── Summary ──
  summary: PostingSummary | null = null;
  summaryError = '';
  tiles: Record<PostingTile, number> = summaryTiles(null);

  // ── Config ──
  config: TenantFinanceConfig | null = null;
  draft = { internalAccountingEnabled: false, autoPostToGL: true };
  configLoading = false;
  configError = '';
  saving = false;

  // ── Log ──
  rows: PostingLogRow[] = [];
  total = 0;
  page = 1;
  pageSize = 25;
  logLoading = false;
  logError = '';
  docTypeFilter: string | null = null;
  statusFilter: string | null = null;
  from = '';
  to = '';
  search = '';
  busy: Record<string, boolean> = {};
  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  /** Config PUT (D-41): GL_SETUP_MANAGE. */
  get canManage(): boolean { return this.auth.hasPermission(GL_PERMS.SETUP_MANAGE); }

  /** Retry / skip (D-41): GL_SETUP_MANAGE or INTEGRATION_MANAGE. */
  get canAct(): boolean { return this.canManage || this.auth.hasPermission('INTEGRATION_MANAGE'); }

  /** Config GET needs a GL permission; a QuickBooks-only user (INTEGRATION_VIEW) sees the log alone. */
  get canReadConfig(): boolean {
    return [GL_PERMS.VIEW, GL_PERMS.ACCOUNT_MANAGE, GL_PERMS.SETUP_MANAGE].some(p => this.auth.hasPermission(p));
  }

  private feature(code: string): boolean {
    try { return !!this.tenant?.isSuperAdmin?.() || !!this.tenant?.hasFeature(code); } catch { return false; }
  }

  /** The general ledger is licensed (the server's answer wins when it gave one). */
  get glOn(): boolean {
    const c = this.config;
    if (c) return c.glFeatureEnabled ?? c.notReadyReason !== 'FEATURE_OFF';
    return this.feature(FEATURE_GENERAL_LEDGER);
  }
  get qboOn(): boolean { return this.feature('MODULE_INTEGRATION'); }

  /** Feature on but A42 setup not Complete: the GL target skips postings ("GL not ready"). */
  get glNotReady(): boolean {
    const c = this.config;
    if (!c) return false;
    if (c.notReadyReason) return c.notReadyReason === 'SETUP_INCOMPLETE';
    if (c.glReady === false) return true;
    return !!c.setupStatus && c.setupStatus !== 'Complete';
  }

  get configDirty(): boolean {
    return !!this.config && (this.draft.internalAccountingEnabled !== !!this.config.internalAccountingEnabled
      || this.draft.autoPostToGL !== (this.config.autoPostToGL ?? true));
  }

  get hasFilters(): boolean { return !!(this.docTypeFilter || this.statusFilter || this.from || this.to || this.search.trim()); }

  ngOnInit(): void {
    this.loadConfig();
    this.loadSummary();
    // The table's first lazy load fetches page 1.
  }

  ngOnDestroy(): void { if (this.searchTimer) clearTimeout(this.searchTimer); }

  refresh(): void {
    this.loadConfig();
    this.loadSummary();
    this.loadLog(this.page);
  }

  // ── Config ──────────────────────────────────────────────────────────────────

  loadConfig(): void {
    if (!this.canReadConfig) return;
    this.configLoading = true;
    this.configError = '';
    this.api.getConfig().subscribe({
      next: c => { this.configLoading = false; this.applyConfig(c); },
      error: err => { this.configLoading = false; this.configError = glError(err, 'The posting settings could not be loaded.'); }
    });
  }

  private applyConfig(c: TenantFinanceConfig | null): void {
    this.config = c;
    this.draft = { internalAccountingEnabled: !!c?.internalAccountingEnabled, autoPostToGL: c?.autoPostToGL ?? true };
  }

  discard(): void { this.applyConfig(this.config); }

  saveConfig(): void {
    if (!this.canManage || this.saving || !this.configDirty) return;
    this.saving = true;
    this.api.updateConfig({ internalAccountingEnabled: this.draft.internalAccountingEnabled, autoPostToGL: this.draft.autoPostToGL }).subscribe({
      next: c => {
        this.saving = false;
        this.applyConfig(c ?? { ...this.config!, ...this.draft });
        this.messages.add({ severity: 'success', summary: 'Saved', detail: this.draft.internalAccountingEnabled
          ? 'Documents are posted to the general ledger from now on.' : 'Documents are no longer posted to the general ledger.', life: 4000 });
      },
      error: err => {
        this.saving = false;
        this.messages.add({ severity: 'error', summary: 'Not saved', detail: glError(err, 'The posting settings could not be saved.'), life: 8000 });
      }
    });
  }

  // ── Summary ─────────────────────────────────────────────────────────────────

  loadSummary(): void {
    this.summaryError = '';
    this.api.getSummary().subscribe({
      next: s => { this.summary = s; this.tiles = summaryTiles(s); },
      error: err => { this.summaryError = glError(err, 'The posting counts could not be loaded.'); }
    });
  }

  filterStatus(status: string): void {
    this.statusFilter = this.statusFilter === status ? null : status;
    this.reload();
  }

  // ── Log ─────────────────────────────────────────────────────────────────────

  loadLog(page = 1): void {
    this.logLoading = true;
    this.logError = '';
    this.api.getLog({
      status: this.statusFilter, docType: this.docTypeFilter, from: this.from || null, to: this.to || null, search: this.search,
      page, pageSize: this.pageSize
    }).subscribe({
      next: r => { this.logLoading = false; this.rows = r.data; this.total = r.totalRecords; this.page = page; },
      error: err => { this.logLoading = false; this.rows = []; this.total = 0; this.logError = glError(err, 'The posting log could not be loaded.'); }
    });
  }

  onLazyLoad(e: TableLazyLoadEvent): void {
    this.pageSize = e.rows ?? this.pageSize;
    this.loadLog(Math.floor((e.first ?? 0) / this.pageSize) + 1);
  }

  reload(): void { this.loadLog(1); }

  onSearch(): void {
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => this.reload(), 400);
  }

  clearFilters(): void {
    this.docTypeFilter = null;
    this.statusFilter = null;
    this.from = '';
    this.to = '';
    this.search = '';
    this.reload();
  }

  target(r: PostingLogRow, code: string): PostingTargetStatus | null {
    return r.targets.find(t => (t.targetCode ?? '').toLowerCase() === code) ?? null;
  }

  actionable(r: PostingLogRow): boolean { return r.targets.some(t => isActionable(t.status)); }

  /** The targets' errors (a skipped target's reason too), each named when there are several. */
  errorOf(r: PostingLogRow): string {
    const withError = r.targets.filter(t => !!t.lastError);
    if (withError.length === 1) return withError[0].lastError!;
    return withError.map(t => `${this.targetLabel(t.targetCode)}: ${t.lastError}`).join(' · ');
  }

  /** What identifies the posting at the target: the GL journal, the QuickBooks id (its internal id is only the payload uuid). */
  refOf(s: PostingTargetStatus): string | null {
    return (s.targetCode ?? '').toLowerCase() === 'quickbooks' ? (s.externalId ? `#${s.externalId}` : null) : (s.internalId || null);
  }

  targetLabel(code: string): string { return TARGET_LABEL[code] ?? code; }
  typeLabel(t: string): string { return docTypeLabel(t); }
  tone(s: string): string { return targetTone(s); }
  statusLabel(s: string): string { return targetStatusLabel(s); }
  amount(n: number): string { return money(n); }
  docRoute(r: PostingLogRow): string[] | null { return sourceDocRoute(r); }

  // ── D-36 — detail drawer ────────────────────────────────────────────────────

  detailVisible = false;
  detailRow: PostingLogRow | null = null;
  detail: PostingDetail | null = null;
  journals: JournalView[] = [];
  detailLoading = false;
  detailError = '';

  openDetail(r: PostingLogRow): void {
    this.detailRow = r;
    this.detail = null;
    this.journals = [];
    this.detailError = '';
    this.detailLoading = true;
    this.detailVisible = true;
    this.api.getEntry(r.uuid).subscribe({
      next: d => {
        if (this.detailRow?.uuid !== r.uuid) return;
        this.detailLoading = false;
        this.detail = d;
        this.journals = journalViews(d.journals);
      },
      error: err => {
        if (this.detailRow?.uuid !== r.uuid) return;
        this.detailLoading = false;
        this.detailError = glError(err, 'The posting could not be loaded.');
      }
    });
  }

  get anyJournalLines(): boolean { return this.journals.some(j => j.lines.length > 0); }

  /** Why there is no journal: no internal GL at all, or the GL posting has not happened yet. */
  get noJournalText(): string {
    const gl = this.detail?.targets.find(t => (t.targetCode ?? '').toLowerCase() === 'internal_gl');
    const skippedAsOff = ['GL_NOT_READY', 'GL_DISABLED', 'TARGET_INACTIVE'].some(c => (gl?.lastError ?? '').startsWith(c));
    const glOff = !gl || skippedAsOff || this.config?.glActive === false || (!this.config && !this.glOn);
    if (glOff) return 'No general ledger journal — internal accounting is off or not set up.';
    return `No general ledger journal yet — the posting to the general ledger is ${targetStatusLabel(gl!.status).toLowerCase()}.`;
  }

  totals(lines: { debit: number; credit: number }[]): LineTotals { return lineTotals(lines); }
  entryType(t: string): string { return entryTypeLabel(t); }
  roleLabel(role: string | null | undefined): string { return entryTypeLabel((role ?? '').replace(/^DEFAULT_/, '')) || '—'; }

  private replace(updated: PostingLogRow): void {
    this.rows = this.rows.map(r => r.uuid === updated.uuid ? { ...r, ...updated } : r);
  }

  retry(r: PostingLogRow): void {
    if (!this.canAct || this.busy[r.uuid]) return;
    this.busy = { ...this.busy, [r.uuid]: true };
    this.api.retry(r.uuid).subscribe({
      next: u => {
        this.busy = { ...this.busy, [r.uuid]: false };
        if (u?.uuid) this.replace(u);
        this.messages.add({ severity: 'success', summary: 'Queued', detail: `${r.sourceDocumentNumber || 'The document'} is queued to be posted again.`, life: 3000 });
        this.loadSummary();
      },
      error: err => {
        this.busy = { ...this.busy, [r.uuid]: false };
        this.messages.add({ severity: 'error', summary: 'Not retried', detail: glError(err, 'The posting could not be retried.'), life: 8000 });
      }
    });
  }

  requestSkip(r: PostingLogRow): void {
    if (!this.canAct || this.busy[r.uuid]) return;
    this.confirmation.confirm({
      key: 'gl-posting',
      header: 'Skip this posting?',
      icon: 'pi pi-ban',
      message: `${r.sourceDocumentNumber || 'The document'} is not posted to the failed target and is not tried again. Post it by hand if it still needs to be in the books.`,
      acceptLabel: 'Skip',
      rejectLabel: 'Cancel',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => this.skip(r)
    });
  }

  skip(r: PostingLogRow): void {
    if (!this.canAct || this.busy[r.uuid]) return;
    this.busy = { ...this.busy, [r.uuid]: true };
    this.api.skip(r.uuid).subscribe({
      next: u => {
        this.busy = { ...this.busy, [r.uuid]: false };
        if (u?.uuid) this.replace(u);
        this.messages.add({ severity: 'success', summary: 'Skipped', detail: `${r.sourceDocumentNumber || 'The document'} will not be tried again.`, life: 3000 });
        this.loadSummary();
      },
      error: err => {
        this.busy = { ...this.busy, [r.uuid]: false };
        this.messages.add({ severity: 'error', summary: 'Not skipped', detail: glError(err, 'The posting could not be skipped.'), life: 8000 });
      }
    });
  }
}
