import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterModule } from '@angular/router';
import { AutoCompleteCompleteEvent, AutoCompleteModule } from 'primeng/autocomplete';
import { ButtonModule } from 'primeng/button';
import { CheckboxModule } from 'primeng/checkbox';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { InputTextModule } from 'primeng/inputtext';
import { SelectModule } from 'primeng/select';
import { TableLazyLoadEvent, TableModule } from 'primeng/table';
import { ToastModule } from 'primeng/toast';
import { ConfirmationService, MessageService } from 'primeng/api';
import { catchError, of } from 'rxjs';

import { Account, AccountingService } from '../../../../services/accounting.service';
import { BusinessPartnerModel, BusinessPartnerService } from '../../../../services/business-partner.service';
import {
  GL_JOURNAL_PERMS, GL_JOURNAL_ROUTE, GlJournalService, OpenItem, ReconcileGroupListItem, ReconcileSuggestion
} from '../../../../services/gl-journal.service';
import { AuthService } from '../../../service/auth.service';
import { FLOW } from '../../../../shared/flow';
import { glError, money } from '../gl.shared';
import { parens, reconcileProblem, selectionTotals } from '../journal.shared';

/** Receivable / payable accounts first: that is what is reconciled most. */
const TYPE_RANK: Record<string, number> = { Receivable: 0, Payable: 1, Bank: 2, Cash: 3 };

/**
 * A47 C8 (tasks 315–321) — `/gl/reconciliation`: pick an account (and optionally a partner), tick open lines, watch the
 * selected debit / credit / residual, ask for matching lines, and reconcile — Full when they net to zero, Partial
 * otherwise (BR-RC-004/005). The account's reconciliation groups (C-8, paged, newest first, reversal auto-groups too)
 * are listed below and can be undone with GL_RECONCILE_EXECUTE; a journal line's link `?groupUuid=` highlights its group.
 */
@Component({
  selector: 'app-gl-reconciliation',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule, AutoCompleteModule, ButtonModule, CheckboxModule, ConfirmDialogModule, InputTextModule,
    SelectModule, TableModule, ToastModule, ...FLOW],
  providers: [MessageService, ConfirmationService],
  styleUrls: ['../gl.scss'],
  template: `
<p-toast></p-toast>
<p-confirmDialog key="gl-rec" [style]="{ width: '32rem', maxWidth: '96vw' }"></p-confirmDialog>
<sf-page [crumbs]="['Finance', 'General Ledger', 'Reconciliation']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button *ngIf="canExecute" label="Reconcile" icon="pi pi-link" [disabled]="!!problem || busy" (onClick)="confirmReconcile()" data-testid="reconcile"></p-button>
    <p-button label="Suggest matches" icon="pi pi-sparkles" [text]="true" [disabled]="!selected.length || !accountUuid || suggesting"
              [loading]="suggesting" (onClick)="suggest()" data-testid="suggest"></p-button>
    <p-button label="Clear selection" [text]="true" severity="secondary" [disabled]="!selected.length" (onClick)="clearSelection()" data-testid="clear-selection"></p-button>
    <span class="sf-end"><p-button icon="pi pi-refresh" label="Refresh" [text]="true" [disabled]="!accountUuid" (onClick)="refresh()" data-testid="refresh"></p-button></span>
  </div>
  <div class="sf-body">
    <div class="sf-lr-head"><div><h1>Reconciliation</h1>
      <div class="sf-sub">Match open debits and credits on an account — invoices against payments, a bill against its payment</div></div></div>

    <section class="sf-card">
      <div class="sf-fields">
        <div class="sf-fld">
          <label for="rc-acc" class="sf-req">Account</label>
          <p-select inputId="rc-acc" [options]="accountOptions" [(ngModel)]="accountUuid" (ngModelChange)="onAccountChange()" optionLabel="label"
                    optionValue="value" [filter]="true" filterBy="label" appendTo="body" placeholder="Choose the account…" data-testid="account"></p-select>
        </div>
        <div class="sf-fld">
          <label for="rc-ptype">Partner</label>
          <div class="rc-partner">
            <p-select inputId="rc-ptype" [options]="partnerTypeOptions" [(ngModel)]="partnerType" (ngModelChange)="clearPartner()" optionLabel="label"
                      optionValue="value" appendTo="body" styleClass="rc-ptype" data-testid="partner-type"></p-select>
            <p-autoComplete [(ngModel)]="partner" [suggestions]="partnerSuggestions" optionLabel="companyName" (completeMethod)="searchPartners($event)"
                            (onSelect)="refresh()" (onClear)="clearPartner()" [minLength]="1" [disabled]="!partnerType" appendTo="body"
                            placeholder="Every partner" data-testid="partner"></p-autoComplete>
          </div>
        </div>
        <div class="sf-fld">
          <label for="rc-from">From</label>
          <input pInputText id="rc-from" type="date" [(ngModel)]="from" (change)="load()" data-testid="from" />
        </div>
        <div class="sf-fld">
          <label for="rc-to">To</label>
          <input pInputText id="rc-to" type="date" [(ngModel)]="to" (change)="load()" data-testid="to" />
        </div>
      </div>
    </section>

    <div class="gl-note er" *ngIf="loadError" data-testid="load-error"><i class="pi pi-exclamation-triangle"></i><div>{{ loadError }}</div></div>

    <!-- Task 318 — the running totals of the selection -->
    <div class="sf-grid sf-tiles" *ngIf="accountUuid" data-testid="totals">
      <div class="sf-tile"><span class="l">Selected</span><span class="v" data-testid="sel-count">{{ totals.count }}</span><span class="s">lines</span></div>
      <div class="sf-tile"><span class="l">Debit</span><span class="v" data-testid="sel-debit">{{ amount(totals.debit) }}</span></div>
      <div class="sf-tile"><span class="l">Credit</span><span class="v" data-testid="sel-credit">{{ amount(totals.credit) }}</span></div>
      <div class="sf-tile"><span class="l">Residual</span>
        <span class="v" [class.rc-ok]="totals.count >= 2 && totals.full" data-testid="sel-residual">{{ signed(totals.residual) }}</span>
        <span class="s" data-testid="sel-kind">{{ totals.count < 2 ? 'select two or more' : totals.full ? 'full reconciliation' : 'partial reconciliation' }}</span></div>
    </div>
    <div class="gl-note wn" *ngIf="selected.length && problem && selected.length >= 2" data-testid="problem"><i class="pi pi-info-circle"></i><div>{{ problem }}</div></div>

    <!-- Task 319 — suggestions -->
    <section class="sf-card" *ngIf="suggestions !== null" data-testid="suggestions">
      <div class="sf-ch"><h2>Suggested matches</h2>
        <span class="sf-end"><p-button icon="pi pi-times" [text]="true" size="small" severity="secondary" (onClick)="suggestions = null" ariaLabel="Close"></p-button></span></div>
      <div class="sf-empty" *ngIf="!suggestions.length" data-testid="no-suggestions">No opposite-side lines come close. Pick the lines by hand.</div>
      <ul class="rc-sugg" *ngIf="suggestions.length">
        <li *ngFor="let s of suggestions; let i = index" [attr.data-testid]="'suggestion-' + i">
          <span class="sf-pill" [ngClass]="s.exact ? 'ok' : 'wn'">{{ s.exact ? 'Exact' : 'Close' }}</span>
          <span class="grow">{{ describe(s) }}</span>
          <span class="gl-num">{{ signed(s.total) }} · leaves {{ signed(s.residualAfter) }}</span>
          <p-button label="Add" icon="pi pi-plus" size="small" [text]="true" (onClick)="applySuggestion(s)" [attr.data-testid]="'apply-' + i"></p-button>
        </li>
      </ul>
    </section>

    <section class="sf-card p0" *ngIf="accountUuid">
      <div class="sf-ch sf-pad"><h2>Open items <span class="sf-count">{{ items.length }}</span></h2>
        <span class="sf-end sf-mu sf-sm">Base currency · Σ open {{ signed(openTotal) }}</span></div>
      <div class="sf-empty" *ngIf="loading"><i class="pi pi-spin pi-spinner"></i> Loading…</div>
      <div class="gl-scroll" *ngIf="!loading">
        <table class="rc-table" data-testid="items">
          <thead><tr>
            <th style="width:2.5rem"><p-checkbox [binary]="true" [ngModel]="allSelected" (ngModelChange)="toggleAll($event)" ariaLabel="Select all" data-testid="select-all"></p-checkbox></th>
            <th>Date</th><th>Entry</th><th>Partner</th><th>Description</th><th class="gl-num">Debit</th><th class="gl-num">Credit</th><th class="gl-num">Open</th><th>Partial in</th>
          </tr></thead>
          <tbody>
            <tr *ngFor="let i of items" [class.rc-sel]="isSelected(i)" [attr.data-testid]="'item-' + i.lineUuid">
              <td><p-checkbox [binary]="true" [ngModel]="isSelected(i)" (ngModelChange)="toggle(i, $event)" [attr.data-testid]="'check-' + i.lineUuid"></p-checkbox></td>
              <td class="rc-nowrap">{{ i.entryDate | date:'dd MMM yyyy' }}</td>
              <td><a class="sf-mono" [routerLink]="[jeRoute, i.entry.uuid]">{{ i.entry.entryNumber }}</a><span class="sf-mu sf-sm"> · {{ i.lineNumber }}</span></td>
              <td>{{ i.partnerName || '' }}</td>
              <td class="rc-desc">{{ i.description || i.reference || '' }}</td>
              <td class="gl-num">{{ i.debit ? amount(i.debit) : '' }}</td>
              <td class="gl-num">{{ i.credit ? amount(i.credit) : '' }}</td>
              <td class="gl-num">{{ signed(i.amountResidual) }}</td>
              <td><span *ngIf="i.reconcileGroupUuid" class="sf-mono sf-sm" [attr.data-testid]="'partial-' + i.lineUuid">{{ i.reconcileGroupNumber || 'group' }}</span></td>
            </tr>
            <tr *ngIf="!items.length"><td colspan="9" class="sf-empty" data-testid="empty">Nothing open on this account{{ partner ? ' for this partner' : '' }}.</td></tr>
          </tbody>
        </table>
      </div>
    </section>
    <div class="sf-card sf-empty" *ngIf="!accountUuid" data-testid="choose-account">Choose an account to see its open items.</div>

    <!-- Task 321 / C-8 — the account's reconciliation groups (newest first), each can be undone -->
    <section class="sf-card p0" *ngIf="accountUuid" data-testid="groups">
      <div class="sf-ch sf-pad"><h2>Reconciliation groups <span class="sf-count">{{ groupTotal }}</span></h2>
        <span class="sf-end sf-mu sf-sm">Includes the groups made by reversals</span></div>
      <div class="gl-note er rc-inset" *ngIf="groupsError" data-testid="groups-error"><i class="pi pi-exclamation-triangle"></i><div>{{ groupsError }}</div></div>
      <div class="gl-scroll">
        <p-table [value]="groups" [lazy]="true" (onLazyLoad)="onGroupsLazyLoad($event)" [loading]="groupsLoading" [paginator]="true" [rows]="groupPageSize"
                 [totalRecords]="groupTotal" [first]="(groupPage - 1) * groupPageSize" [rowsPerPageOptions]="[25, 50, 100]" dataKey="uuid"
                 styleClass="p-datatable-sm" [tableStyle]="{ 'min-width': '44rem' }" data-testid="groups-table">
          <ng-template pTemplate="header">
            <tr><th>Group</th><th>Type</th><th class="gl-num">Matched</th><th class="gl-num">Lines</th><th>When</th><th style="width:9rem"></th></tr>
          </ng-template>
          <ng-template pTemplate="body" let-g>
            <tr [attr.data-testid]="'group-' + g.groupNumber" [class.rc-sel]="g.uuid === focusGroupUuid">
              <td class="sf-mono">{{ g.groupNumber }}</td>
              <td><span class="sf-pill" [ngClass]="g.reconcileType === 'Full' ? 'ok' : 'wn'">{{ g.reconcileType }}</span></td>
              <td class="gl-num">{{ amount(g.amount) }} <span class="sf-mu sf-sm">{{ g.currencyCode || '' }}</span></td>
              <td class="gl-num">{{ g.lineCount }}</td>
              <td class="sf-sm">{{ g.reconciledAt | date:'dd MMM yyyy, HH:mm' }}<ng-container *ngIf="g.createdByName"> · {{ g.createdByName }}</ng-container></td>
              <td><p-button *ngIf="canExecute" label="Unreconcile" icon="pi pi-replay" [text]="true" size="small" severity="danger" [disabled]="busy"
                            (onClick)="confirmUnreconcile(g)" [attr.data-testid]="'unreconcile-' + g.groupNumber"></p-button></td>
            </tr>
          </ng-template>
          <ng-template pTemplate="emptymessage">
            <tr><td colspan="6" class="sf-empty" data-testid="groups-empty">No reconciliation on this account{{ partnerUuid ? ' for this partner' : '' }} yet.</td></tr>
          </ng-template>
        </p-table>
      </div>
    </section>
  </div>
</sf-page>`,
  styles: [`
    .rc-partner { display: flex; gap: .35rem; }
    :host ::ng-deep .rc-ptype { width: 8rem; }
    .rc-table { width: 100%; min-width: 56rem; border-collapse: collapse; font-size: .875rem; }
    .rc-table th, .rc-table td { padding: .4rem .6rem; border-top: 1px solid var(--sms-border-soft); text-align: left; }
    .rc-table th.gl-num, .rc-table td.gl-num { text-align: right; }
    tr.rc-sel td { background: var(--sms-primary-soft); }
    .rc-nowrap { white-space: nowrap; }
    .rc-desc { max-width: 20rem; }
    .rc-ok { color: var(--sms-ok); }
    .rc-sugg { list-style: none; margin: 0; padding: 0; }
    .rc-sugg li { display: flex; align-items: center; gap: .6rem; padding: .45rem 0; border-bottom: 1px solid var(--sms-border-soft); flex-wrap: wrap; }
    .rc-sugg li:last-child { border-bottom: 0; }
    .rc-sugg .grow { flex: 1; min-width: 12rem; }
    .rc-inset { margin: 0 1rem .75rem; }
  `]
})
export class GlReconciliationComponent implements OnInit {
  private readonly api = inject(GlJournalService);
  private readonly accounting = inject(AccountingService);
  private readonly partners = inject(BusinessPartnerService);
  private readonly auth = inject(AuthService);
  private readonly confirm = inject(ConfirmationService);
  private readonly messages = inject(MessageService);
  private readonly activated = inject(ActivatedRoute, { optional: true });

  readonly jeRoute = GL_JOURNAL_ROUTE;
  readonly partnerTypeOptions = [{ label: 'Any', value: null as string | null }, { label: 'Customer', value: 'Customer' }, { label: 'Vendor', value: 'Vendor' }];

  accountOptions: { label: string; value: string }[] = [];
  accountUuid: string | null = null;
  partnerType: string | null = null;
  partner: BusinessPartnerModel | string | null = null;
  partnerSuggestions: BusinessPartnerModel[] = [];
  from = '';
  to = '';

  items: OpenItem[] = [];
  loading = false;
  loadError = '';
  private selectedIds = new Set<string>();
  suggestions: ReconcileSuggestion[] | null = null;
  suggesting = false;
  busy = false;
  // C-8 — the account's reconciliation groups, paged on the server, newest first.
  groups: ReconcileGroupListItem[] = [];
  groupTotal = 0;
  groupPage = 1;
  groupPageSize = 25;
  groupsLoading = false;
  groupsError = '';
  /** The group a journal line's link opened (?groupUuid=), highlighted. */
  focusGroupUuid: string | null = null;
  /** The groups table has done its first lazy load (later filter changes reload it here). */
  private groupsReady = false;

  get canExecute(): boolean { return this.auth.hasPermission(GL_JOURNAL_PERMS.RECONCILE_EXECUTE); }
  get selected(): OpenItem[] { return this.items.filter(i => this.selectedIds.has(i.lineUuid)); }
  get totals() { return selectionTotals(this.selected); }
  get problem(): string | null { return reconcileProblem(this.selected); }
  get allSelected(): boolean { return this.items.length > 0 && this.items.every(i => this.selectedIds.has(i.lineUuid)); }
  get openTotal(): number { return this.items.reduce((s, i) => s + Number(i.amountResidual || 0), 0); }
  get partnerUuid(): string | null { return this.partner && typeof this.partner === 'object' ? this.partner.uuid ?? null : null; }

  ngOnInit(): void {
    const qp = this.activated?.snapshot?.queryParamMap;
    const acc = qp?.get('accountUuid');
    const group = qp?.get('groupUuid');
    this.accounting.getPostableAccounts().pipe(catchError(() => of([] as Account[]))).subscribe(list => {
      this.accountOptions = [...list]
        .sort((a, b) => (TYPE_RANK[a.accountType ?? ''] ?? 9) - (TYPE_RANK[b.accountType ?? ''] ?? 9) || a.accountCode.localeCompare(b.accountCode))
        .map(a => ({ label: `${a.accountCode} — ${a.accountName}${a.accountType ? ' · ' + a.accountType : ''}`, value: a.uuid }));
    });
    this.focusGroupUuid = group ?? null;
    if (acc) { this.accountUuid = acc; this.load(); }
    else if (group) {
      // A group link without its account: the group names it.
      this.api.getReconcileGroup(group).subscribe({
        next: g => { if (!this.accountUuid && g.account?.uuid) { this.accountUuid = g.account.uuid; this.load(); } },
        error: err => this.toast('error', glError(err, 'The reconciliation could not be loaded.'))
      });
    }
  }

  onAccountChange(): void {
    this.selectedIds.clear();
    this.suggestions = null;
    this.focusGroupUuid = null;
    this.refresh();
  }

  /** Open items and the groups list, for the current account / partner. */
  refresh(): void {
    this.load();
    this.reloadGroups();
  }

  /** REV-18 — only the latest open-items request's answer is shown. */
  private itemsSeq = 0;

  load(): void {
    const seq = ++this.itemsSeq;
    if (!this.accountUuid) { this.items = []; return; }
    this.loading = true;
    this.loadError = '';
    this.api.getOpenItems({
      accountUuid: this.accountUuid, partnerUuid: this.partnerUuid, partnerType: this.partnerUuid ? this.partnerType : null,
      fromDate: this.from || null, toDate: this.to || null
    }).subscribe({
      next: list => {
        if (seq !== this.itemsSeq) return;
        this.loading = false;
        this.items = list;
        // Keep only selections still open.
        this.selectedIds = new Set([...this.selectedIds].filter(id => list.some(i => i.lineUuid === id)));
      },
      error: err => {
        if (seq !== this.itemsSeq) return;
        this.loading = false; this.items = []; this.loadError = glError(err, 'The open items could not be loaded.');
      }
    });
  }

  // ── Partner filter ──
  searchPartners(e: AutoCompleteCompleteEvent): void {
    const filter = this.partnerType === 'Vendor' ? { isVendor: true } : { isCustomer: true };
    this.partners.getPartners({ ...filter, active: true, search: e.query, pageSize: 20 }).pipe(catchError(() => of(null)))
      .subscribe(r => this.partnerSuggestions = r?.result?.data ?? []);
  }

  clearPartner(): void {
    const had = !!this.partnerUuid;
    this.partner = null;
    if (had) this.refresh();
  }

  // ── Selection ──
  isSelected(i: OpenItem): boolean { return this.selectedIds.has(i.lineUuid); }

  toggle(i: OpenItem, on: boolean): void {
    const next = new Set(this.selectedIds);
    if (on) next.add(i.lineUuid); else next.delete(i.lineUuid);
    this.selectedIds = next;
  }

  toggleAll(on: boolean): void { this.selectedIds = on ? new Set(this.items.map(i => i.lineUuid)) : new Set(); }

  clearSelection(): void { this.selectedIds = new Set(); this.suggestions = null; }

  // ── Suggest (task 319) ──
  suggest(): void {
    if (!this.accountUuid || !this.selected.length) return;
    this.suggesting = true;
    this.api.suggestMatches(this.accountUuid, this.selected.map(i => i.lineUuid)).subscribe({
      next: list => { this.suggesting = false; this.suggestions = list; },
      error: err => { this.suggesting = false; this.toast('error', glError(err, 'No suggestions could be made.')); }
    });
  }

  describe(s: ReconcileSuggestion): string {
    const names = s.lineUuids.map(id => {
      const i = this.items.find(x => x.lineUuid === id);
      return i ? `${i.entry.entryNumber}${i.partnerName ? ' (' + i.partnerName + ')' : ''}` : 'a line not shown';
    });
    return names.join(', ');
  }

  applySuggestion(s: ReconcileSuggestion): void {
    const next = new Set(this.selectedIds);
    for (const id of s.lineUuids) if (this.items.some(i => i.lineUuid === id)) next.add(id);
    this.selectedIds = next;
    this.suggestions = null;
  }

  // ── Reconcile (task 320) ──
  confirmReconcile(): void {
    if (this.problem || !this.canExecute) return;
    const t = this.totals;
    this.confirm.confirm({
      key: 'gl-rec', header: 'Reconcile lines', icon: 'pi pi-link', acceptLabel: 'Reconcile', rejectLabel: 'Cancel',
      message: t.full
        ? `Reconcile ${t.count} lines (debit ${money(t.debit)} against credit ${money(t.credit)})? They net to zero: a full reconciliation.`
        : `Reconcile ${t.count} lines? They differ by ${parens(t.residual)}: a partial reconciliation — the larger side stays open for the rest.`,
      accept: () => this.reconcile()
    });
  }

  reconcile(): void {
    const ids = this.selected.map(i => i.lineUuid);
    this.busy = true;
    this.api.reconcile(ids).subscribe({
      next: g => {
        this.busy = false;
        this.toast('success', `${g?.groupNumber ?? 'The lines'} — ${g?.reconcileType === 'Partial' ? 'partially' : 'fully'} reconciled.`);
        this.selectedIds = new Set();
        this.suggestions = null;
        this.focusGroupUuid = g?.uuid ?? null;
        this.refresh();
      },
      error: err => { this.busy = false; this.toast('error', glError(err, 'The lines could not be reconciled.')); }
    });
  }

  // ── Groups (task 321) ──
  loadGroups(page = 1): void {
    if (!this.accountUuid) { this.groups = []; this.groupTotal = 0; return; }
    const account = this.accountUuid;
    this.groupsLoading = true;
    this.groupsError = '';
    this.api.getReconcileGroups({ accountUuid: account, partnerUuid: this.partnerUuid, page, pageSize: this.groupPageSize }).subscribe({
      next: r => {
        if (this.accountUuid !== account) return;
        this.groupsLoading = false;
        this.groups = r.data;
        this.groupTotal = r.totalRecords;
        this.groupPage = page;
      },
      error: err => { this.groupsLoading = false; this.groups = []; this.groupTotal = 0; this.groupsError = glError(err, 'The reconciliation groups could not be loaded.'); }
    });
  }

  onGroupsLazyLoad(e: TableLazyLoadEvent): void {
    this.groupsReady = true;
    this.groupPageSize = e.rows ?? this.groupPageSize;
    this.loadGroups(Math.floor((e.first ?? 0) / this.groupPageSize) + 1);
  }

  /** Page 1 again after a filter change or an action (the table's first lazy load does the initial read). */
  private reloadGroups(): void { if (this.groupsReady) this.loadGroups(1); }

  confirmUnreconcile(g: ReconcileGroupListItem): void {
    if (!this.canExecute) return;
    this.confirm.confirm({
      key: 'gl-rec', header: 'Unreconcile', icon: 'pi pi-replay', acceptLabel: 'Unreconcile', rejectLabel: 'Cancel',
      acceptButtonStyleClass: 'p-button-danger',
      message: `Undo ${g.groupNumber}? Its ${g.lineCount} lines become open again with their full amounts.`,
      accept: () => this.unreconcile(g)
    });
  }

  unreconcile(g: ReconcileGroupListItem): void {
    this.busy = true;
    this.api.unreconcile(g.uuid).subscribe({
      next: r => {
        this.busy = false;
        if (this.focusGroupUuid === g.uuid) this.focusGroupUuid = null;
        this.toast('success', `${r?.groupNumber ?? g.groupNumber} is undone; ${r?.linesRestored ?? g.lineCount} lines are open again.`);
        this.refresh();
      },
      error: err => { this.busy = false; this.toast('error', glError(err, 'The reconciliation could not be undone.')); }
    });
  }

  amount(n: number): string { return money(n); }
  signed(n: number): string { return parens(n); }

  private toast(severity: 'success' | 'error', detail: string): void {
    this.messages.add({ severity, summary: severity === 'success' ? 'Done' : 'Error', detail, life: severity === 'error' ? 8000 : 3000 });
  }
}
