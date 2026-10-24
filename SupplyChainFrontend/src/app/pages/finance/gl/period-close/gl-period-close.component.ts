import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { DialogModule } from 'primeng/dialog';
import { SelectModule } from 'primeng/select';
import { ToastModule } from 'primeng/toast';
import { ConfirmationService, MessageService } from 'primeng/api';
import { Observable, catchError, of } from 'rxjs';

import { Account, AccountingService, FiscalYear } from '../../../../services/accounting.service';
import {
  CloseResult, GL_JOURNAL_PERMS, GL_JOURNAL_ROUTE, GlJournalService, PeriodAction, PeriodCloseOverview, PeriodCloseStatus, YearEndCloseResult,
  YearEndPreview
} from '../../../../services/gl-journal.service';
import { AuthService } from '../../../service/auth.service';
import { FLOW } from '../../../../shared/flow';
import { toIsoDate } from '../../../finance-setup/finance-setup.shared';
import { glError, money } from '../gl.shared';
import {
  PERIOD_ACTION_LABEL, actionBlockedByDrafts, parens, periodCloseActions, periodStatusLabel, periodStatusTone, yearEndBlockers
} from '../journal.shared';

const RETAINED_EARNINGS = 'DEFAULT_RETAINED_EARNINGS';

/**
 * A47 C10 (tasks 393–401) — `/gl/period-close`: a fiscal year's periods with their posted and draft entry counts and
 * status, the D-11 actions (Lock, Adjustments only, Reopen, Final close — as the server allows them to this user), a
 * warning while drafts block a lock, and the year-end close: retained earnings account (DEFAULT_RETAINED_EARNINGS by
 * default), preview of the closing entry, execute with a confirmation, and the summary afterwards.
 */
@Component({
  selector: 'app-gl-period-close',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule, ButtonModule, ConfirmDialogModule, DialogModule, SelectModule, ToastModule, ...FLOW],
  providers: [MessageService, ConfirmationService],
  styleUrls: ['../gl.scss'],
  template: `
<p-toast></p-toast>
<p-confirmDialog key="gl-pc" [style]="{ width: '32rem', maxWidth: '96vw' }"></p-confirmDialog>
<sf-page [crumbs]="['Finance', 'General Ledger', 'Period Close']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-select [options]="yearOptions" [(ngModel)]="yearUuid" (ngModelChange)="load()" optionLabel="label" optionValue="value" appendTo="body"
              placeholder="Fiscal year" data-testid="year"></p-select>
    <span class="sf-end"><p-button icon="pi pi-refresh" label="Refresh" [text]="true" [disabled]="!yearUuid" (onClick)="load()" data-testid="refresh"></p-button></span>
  </div>
  <div class="sf-body">
    <div class="sf-lr-head"><div><h1>Period close<ng-container *ngIf="overview?.fiscalYear"> — {{ overview!.fiscalYear!.fiscalYearName }}</ng-container>
        <span class="sf-pill er" *ngIf="yearClosed" data-testid="year-closed">Closed</span></h1>
      <div class="sf-sub">Lock periods once they are reported, close them finally, then close the year into retained earnings</div></div></div>

    <div class="gl-note er" *ngIf="loadError" data-testid="load-error"><i class="pi pi-exclamation-triangle"></i><div>{{ loadError }}</div></div>
    <div class="sf-card sf-empty" *ngIf="!years.length && !yearsLoading" data-testid="no-years">No fiscal year to close yet.</div>
    <div class="sf-card sf-empty" *ngIf="loading && !overview"><i class="pi pi-spin pi-spinner"></i> Loading…</div>

    <ng-container *ngIf="overview">
      <!-- Task 397 -->
      <div class="gl-note wn" *ngFor="let p of draftPeriods" [attr.data-testid]="'drafts-' + p.periodNumber">
        <i class="pi pi-exclamation-triangle"></i>
        <div>{{ p.periodName }} has {{ p.draftEntryCount }} unposted draft{{ p.draftEntryCount === 1 ? '' : 's' }}. Post or delete
          {{ p.draftEntryCount === 1 ? 'it' : 'them' }} before locking the period.
          <a [routerLink]="jeRoute" [queryParams]="{ status: 'Draft', periodUuid: p.uuid }">Show the drafts</a></div>
      </div>

      <!-- Tasks 395–396 -->
      <section class="sf-card p0">
        <div class="gl-scroll">
          <table class="pc-table" data-testid="periods">
            <thead><tr><th style="width:3rem">#</th><th>Period</th><th>Dates</th><th class="gl-num">Entries</th><th class="gl-num">Drafts</th><th>Status</th><th></th></tr></thead>
            <tbody>
              <tr *ngFor="let p of overview.periods" [attr.data-testid]="'period-' + p.periodNumber">
                <td class="sf-mu">{{ p.periodNumber }}</td>
                <td>{{ p.periodName }} <span class="sf-pill vi" *ngIf="p.periodNumber === 13">adjustments</span></td>
                <td class="sf-sm pc-nowrap">{{ p.startDate | date:'d MMM' }} – {{ p.endDate | date:'d MMM y' }}</td>
                <td class="gl-num">{{ p.postedEntryCount }}</td>
                <td class="gl-num" [class.pc-warn]="p.draftEntryCount > 0" [attr.data-testid]="'drafts-count-' + p.periodNumber">{{ p.draftEntryCount }}</td>
                <td><span class="sf-pill" [ngClass]="tone(p.status)" [attr.data-testid]="'status-' + p.periodNumber">{{ label(p.status) }}</span></td>
                <td class="text-right">
                  <span class="gl-actions">
                    <p-button *ngFor="let a of actionsFor(p)" [label]="actionLabel(a)" [text]="true" size="small"
                              [severity]="a === 'FinalClose' ? 'danger' : undefined" [disabled]="busy === p.uuid || blocked(a, p)"
                              (onClick)="confirmAction(p, a)" [attr.data-testid]="a + '-' + p.periodNumber"></p-button>
                    <span class="sf-mu" *ngIf="!actionsFor(p).length">—</span>
                  </span>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
        <div class="gl-foot"><span>Lock stops postings until reopened · Adjustments only accepts opening/closing adjustment entries · Final close cannot be undone.</span></div>
      </section>

      <!-- Tasks 398–401 -->
      <section class="sf-card" data-testid="year-end">
        <div class="sf-ch"><h2>Year-end close</h2></div>
        <ng-container *ngIf="yearClosed; else openYear">
          <div class="gl-note ok" data-testid="year-done"><i class="pi pi-check-circle"></i>
            <div>{{ overview.fiscalYear?.fiscalYearName }} is closed<ng-container *ngIf="overview.fiscalYear?.closedAt"> ({{ overview.fiscalYear!.closedAt | date:'d MMM y' }})</ng-container>.
              <ng-container *ngIf="overview.closingEntry"> Closing entry <a class="sf-mono" [routerLink]="[jeRoute, overview.closingEntry.uuid]">{{ overview.closingEntry.entryNumber }}</a>.</ng-container></div></div>
        </ng-container>
        <ng-template #openYear>
          <p class="sf-mu sf-sm" style="margin-top:0">All periods 1–12 must be Locked or Closed and no draft may remain. Revenue and expense balances are
            closed into retained earnings with one closing entry; then the year and all its periods are closed for good.</p>
          <ul class="pc-blockers" *ngIf="blockers.length" data-testid="blockers"><li *ngFor="let b of blockers">{{ b }}</li></ul>
          <div class="sf-fields">
            <div class="sf-fld">
              <label for="pc-re" class="sf-req">Retained earnings account</label>
              <p-select inputId="pc-re" [options]="equityOptions" [(ngModel)]="retainedUuid" optionLabel="label" optionValue="value" [filter]="true"
                        filterBy="label" appendTo="body" placeholder="Choose the account…" data-testid="retained"></p-select>
              <span class="gl-sub" *ngIf="retainedIsDefault">The organization's default (DEFAULT_RETAINED_EARNINGS).</span>
            </div>
          </div>
          <div class="pc-ye-actions">
            <p-button label="Preview closing entry" icon="pi pi-eye" [outlined]="true" [loading]="previewing" (onClick)="preview()" data-testid="preview"></p-button>
            <p-button *ngIf="canAdmin" label="Execute year-end close" icon="pi pi-lock" severity="danger" [disabled]="!canExecuteYear || executing"
                      [loading]="executing" (onClick)="confirmExecute()" data-testid="execute"></p-button>
          </div>
        </ng-template>
      </section>
    </ng-container>
  </div>
</sf-page>

<!-- Task 399 -->
<p-dialog [(visible)]="previewVisible" [modal]="true" [draggable]="false" [style]="{ width: '46rem' }" [breakpoints]="{ '720px': '96vw' }"
          header="Closing entry preview">
  <ng-container *ngIf="previewData as pv">
    <div class="gl-totals" data-testid="preview-totals">
      <div><span class="l">Revenue</span><span class="v">{{ amount(pv.totalRevenue) }}</span></div>
      <div><span class="l">Expenses</span><span class="v">{{ amount(pv.totalExpenses) }}</span></div>
      <div><span class="l">{{ pv.netIncome >= 0 ? 'Net income' : 'Net loss' }}</span><span class="v" [class.er]="pv.netIncome < 0" data-testid="preview-net">{{ signed(pv.netIncome) }}</span></div>
      <div><span class="l">Dated</span><span class="v">{{ pv.entryDate | date:'d MMM y' }}<ng-container *ngIf="pv.periodNumber"> · period {{ pv.periodNumber }}</ng-container></span></div>
    </div>
    <div class="sf-mu sf-sm" style="margin:.5rem 0" *ngIf="pv.retainedEarningsAccount">Into {{ pv.retainedEarningsAccount.accountCode }} — {{ pv.retainedEarningsAccount.accountName }}</div>
    <ul class="pc-blockers" *ngIf="pv.blockers?.length" data-testid="preview-blockers"><li *ngFor="let b of pv.blockers">{{ b }}</li></ul>
    <div class="gl-scroll">
      <table class="pc-table" data-testid="preview-lines">
        <thead><tr><th>Account</th><th>Type</th><th class="gl-num">Balance</th><th class="gl-num">Debit</th><th class="gl-num">Credit</th></tr></thead>
        <tbody>
          <tr *ngFor="let l of pv.lines">
            <td><span class="sf-mono">{{ l.account.accountCode }}</span> {{ l.account.accountName }}</td>
            <td>{{ l.rootType }}</td>
            <td class="gl-num">{{ signed(l.balance) }}</td>
            <td class="gl-num">{{ l.closingDebit ? amount(l.closingDebit) : '' }}</td>
            <td class="gl-num">{{ l.closingCredit ? amount(l.closingCredit) : '' }}</td>
          </tr>
          <tr *ngIf="!pv.lines.length"><td colspan="5" class="sf-empty">Every revenue and expense balance is zero: no closing entry is needed.</td></tr>
        </tbody>
        <tfoot *ngIf="pv.lines.length"><tr class="sf-tot"><td colspan="3">Total</td>
          <td class="gl-num">{{ amount(previewDebit) }}</td><td class="gl-num">{{ amount(previewCredit) }}</td></tr></tfoot>
      </table>
    </div>
  </ng-container>
  <ng-template pTemplate="footer">
    <p-button label="Close" [text]="true" (onClick)="previewVisible = false"></p-button>
    <p-button *ngIf="canAdmin && !yearClosed" label="Execute year-end close" icon="pi pi-lock" severity="danger"
              [disabled]="!canExecuteYear || executing" (onClick)="previewVisible = false; confirmExecute()" data-testid="preview-execute"></p-button>
  </ng-template>
</p-dialog>

<!-- Task 401 -->
<p-dialog [(visible)]="resultVisible" [modal]="true" [draggable]="false" [style]="{ width: '30rem' }" [breakpoints]="{ '640px': '96vw' }"
          header="Year closed">
  <ng-container *ngIf="result as r">
    <div class="gl-note ok" style="margin-bottom:.75rem" data-testid="result"><i class="pi pi-check-circle"></i>
      <div>{{ r.alreadyClosed ? 'The year was already closed.' : (r.fiscalYear?.fiscalYearName || 'The fiscal year') + ' is closed.' }}</div></div>
    <div class="sf-kv">
      <span>Revenue closed</span><span>{{ amount(r.totalRevenueClosing) }}</span>
      <span>Expenses closed</span><span>{{ amount(r.totalExpenseClosing) }}</span>
      <span>{{ r.netIncomeTransferred >= 0 ? 'Net income' : 'Net loss' }} to retained earnings</span><span data-testid="result-net">{{ signed(r.netIncomeTransferred) }}</span>
      <span>Closing entry</span><span data-testid="result-entry">
        <a *ngIf="r.closingEntry; else noEntry" class="sf-mono" [routerLink]="[jeRoute, r.closingEntry.uuid]" (click)="resultVisible = false">{{ r.closingEntry.entryNumber }}</a>
        <ng-template #noEntry>None (all balances were zero)</ng-template>
        <ng-container *ngFor="let x of moreEntries(r)">, <a class="sf-mono" [routerLink]="[jeRoute, x.uuid]" (click)="resultVisible = false">{{ x.entryNumber }}</a></ng-container></span>
    </div>
  </ng-container>
  <ng-template pTemplate="footer"><p-button label="Done" (onClick)="resultVisible = false" data-testid="result-done"></p-button></ng-template>
</p-dialog>`,
  styles: [`
    .pc-table { width: 100%; min-width: 44rem; border-collapse: collapse; font-size: .875rem; }
    .pc-table th, .pc-table td { padding: .4rem .75rem; border-top: 1px solid var(--sms-border-soft); text-align: left; }
    .pc-table th.gl-num, .pc-table td.gl-num { text-align: right; }
    .pc-table tfoot td { font-weight: 600; }
    .pc-nowrap { white-space: nowrap; }
    .pc-warn { color: var(--sms-warn); font-weight: 600; }
    .pc-blockers { margin: .25rem 0 .75rem; padding-left: 1.1rem; color: var(--sms-warn); font-size: .85rem; }
    .pc-ye-actions { display: flex; flex-wrap: wrap; gap: .5rem; margin-top: .75rem; }
  `]
})
export class GlPeriodCloseComponent implements OnInit {
  private readonly api = inject(GlJournalService);
  private readonly accounting = inject(AccountingService);
  private readonly auth = inject(AuthService);
  private readonly confirm = inject(ConfirmationService);
  private readonly messages = inject(MessageService);

  readonly jeRoute = GL_JOURNAL_ROUTE;
  years: FiscalYear[] = [];
  yearsLoading = true;
  yearOptions: { label: string; value: string }[] = [];
  yearUuid: string | null = null;

  overview: PeriodCloseOverview | null = null;
  loading = false;
  loadError = '';
  busy: string | null = null;

  equityOptions: { label: string; value: string }[] = [];
  retainedUuid: string | null = null;
  private defaultRetainedUuid: string | null = null;

  previewVisible = false;
  previewData: YearEndPreview | null = null;
  previewing = false;
  executing = false;
  resultVisible = false;
  result: YearEndCloseResult | null = null;

  get canAdmin(): boolean { return this.auth.hasPermission(GL_JOURNAL_PERMS.PERIOD_CLOSE_ADMIN); }
  get canExecutePeriods(): boolean {
    return this.auth.hasPermission(GL_JOURNAL_PERMS.PERIOD_CLOSE_EXECUTE) || this.canAdmin;
  }
  get yearClosed(): boolean { return !!this.overview?.fiscalYear?.isClosed; }
  get draftPeriods(): PeriodCloseStatus[] { return (this.overview?.periods ?? []).filter(p => p.draftEntryCount > 0 && p.status !== 'Closed'); }
  /** The server's year-close blockers; computed from the table only when it sent none. */
  get blockers(): string[] {
    const o = this.overview;
    if (!o) return [];
    if (Array.isArray(o.yearCloseBlockers)) return o.yearCloseBlockers;
    return yearEndBlockers(o.periods);
  }
  get canExecuteYear(): boolean {
    const o = this.overview;
    return !!o && !this.yearClosed && !!this.retainedUuid && (o.canCloseYear ?? this.blockers.length === 0);
  }
  get retainedIsDefault(): boolean { return !!this.retainedUuid && this.retainedUuid === this.defaultRetainedUuid; }
  get previewDebit(): number { return (this.previewData?.lines ?? []).reduce((s, l) => s + Number(l.closingDebit || 0), 0); }
  get previewCredit(): number { return (this.previewData?.lines ?? []).reduce((s, l) => s + Number(l.closingCredit || 0), 0); }

  ngOnInit(): void {
    const today = toIsoDate(new Date()); // REV-18 — the local date, not UTC
    this.accounting.getFiscalYears().pipe(catchError(() => of(null))).subscribe(r => {
      this.yearsLoading = false;
      this.years = [...(r?.result ?? [])].sort((a, b) => b.startDate.localeCompare(a.startDate));
      this.yearOptions = this.years.map(y => ({ label: y.fiscalYearName + (y.isClosed ? ' (closed)' : ''), value: y.uuid }));
      // The oldest open year is the one to close next; else the current one.
      const open = [...this.years].reverse().find(y => !y.isClosed);
      const pick = open ?? this.years.find(y => y.startDate <= today && today <= y.endDate) ?? this.years[0];
      if (pick) { this.yearUuid = pick.uuid; this.load(); }
    });
    this.accounting.getPostableAccounts().pipe(catchError(() => of([] as Account[]))).subscribe(list => {
      this.equityOptions = list.filter(a => a.rootType === 'Equity').map(a => ({ label: `${a.accountCode} — ${a.accountName}`, value: a.uuid }));
    });
    this.accounting.getDefaults().pipe(catchError(() => of(null))).subscribe(r => {
      const item = r?.result?.categories?.flatMap(c => c.items).find(i => i.defaultKey === RETAINED_EARNINGS);
      if (item?.account?.uuid) this.useDefault(item.account.uuid, `${item.account.accountCode} — ${item.account.accountName}`);
    });
  }

  private useDefault(uuid: string, label: string): void {
    this.defaultRetainedUuid = uuid;
    if (!this.retainedUuid) this.retainedUuid = uuid;
    if (!this.equityOptions.some(o => o.value === uuid)) this.equityOptions = [...this.equityOptions, { label, value: uuid }];
  }

  load(): void {
    if (!this.yearUuid) return;
    const uuid = this.yearUuid;
    this.loading = true;
    this.loadError = '';
    this.api.getPeriodCloseStatus(uuid).subscribe({
      next: o => {
        if (this.yearUuid !== uuid) return;
        this.loading = false;
        this.overview = o;
        const d = o.defaultRetainedEarningsAccount;
        if (d?.uuid) this.useDefault(d.uuid, `${d.accountCode} — ${d.accountName}`);
      },
      error: err => { this.loading = false; this.overview = null; this.loadError = glError(err, 'The periods could not be loaded.'); }
    });
  }

  tone(s: string): string { return periodStatusTone(s); }
  label(s: string): string { return periodStatusLabel(s); }
  amount(n: number): string { return money(n); }
  signed(n: number): string { return parens(n); }
  actionLabel(a: PeriodAction): string { return PERIOD_ACTION_LABEL[a]; }
  /** C-7 / D-RPT-7 — the closing entries after the first one. */
  moreEntries(r: YearEndCloseResult) { return (r.closingEntries ?? []).filter(x => x.uuid !== r.closingEntry?.uuid); }

  /** The server's allowedActions (state + permissions); without them, D-11 by state and this user's codes. */
  actionsFor(p: PeriodCloseStatus): PeriodAction[] {
    if (this.yearClosed) return [];
    const list = periodCloseActions(p.status, p.allowedActions);
    if (Array.isArray(p.allowedActions)) return list;
    return list.filter(a => a === 'FinalClose' ? this.canAdmin : this.canExecutePeriods);
  }

  blocked(a: PeriodAction, p: PeriodCloseStatus): boolean { return actionBlockedByDrafts(a, p.draftEntryCount); }

  confirmAction(p: PeriodCloseStatus, a: PeriodAction): void {
    const text: Record<PeriodAction, string> = {
      Lock: `Lock ${p.periodName}? Nothing can be posted into it until it is reopened.`,
      AdjustmentOnly: `Set ${p.periodName} to adjustments only? Only opening and closing adjustment entries can be posted into it.`,
      Reopen: `Reopen ${p.periodName} for all postings?`,
      FinalClose: `Close ${p.periodName} finally? This cannot be undone — a closed period can never be reopened.`
    };
    this.confirm.confirm({
      key: 'gl-pc', header: PERIOD_ACTION_LABEL[a], icon: a === 'Reopen' ? 'pi pi-lock-open' : 'pi pi-lock', message: text[a],
      acceptLabel: PERIOD_ACTION_LABEL[a], rejectLabel: 'Cancel', acceptButtonStyleClass: a === 'FinalClose' ? 'p-button-danger' : undefined,
      accept: () => this.run(p, a)
    });
  }

  run(p: PeriodCloseStatus, a: PeriodAction): void {
    const call: Observable<CloseResult> = a === 'Lock' ? this.api.closePeriod(p.uuid, 'Locked')
      : a === 'AdjustmentOnly' ? this.api.closePeriod(p.uuid, 'AdjustmentOnly')
      : a === 'Reopen' ? this.api.reopenPeriod(p.uuid)
      : this.api.finalClosePeriod(p.uuid);
    this.busy = p.uuid;
    call.subscribe({
      next: r => {
        this.busy = null;
        this.messages.add({ severity: 'success', summary: 'Done', detail: `${p.periodName} is now ${periodStatusLabel(r?.newStatus ?? r?.period?.status).toLowerCase() || 'changed'}.`, life: 3000 });
        this.load();
      },
      error: err => { this.busy = null; this.messages.add({ severity: 'error', summary: 'Error', detail: glError(err, 'The period could not be changed.'), life: 8000 }); }
    });
  }

  preview(): void {
    if (!this.yearUuid) return;
    this.previewing = true;
    this.api.previewYearEnd(this.yearUuid, this.retainedUuid).subscribe({
      next: p => { this.previewing = false; this.previewData = p; this.previewVisible = true; },
      error: err => { this.previewing = false; this.messages.add({ severity: 'error', summary: 'Error', detail: glError(err, 'The closing entry could not be previewed.'), life: 8000 }); }
    });
  }

  confirmExecute(): void {
    if (!this.canExecuteYear || !this.yearUuid) return;
    const name = this.overview?.fiscalYear?.fiscalYearName ?? 'the fiscal year';
    const acc = this.equityOptions.find(o => o.value === this.retainedUuid)?.label ?? 'the retained earnings account';
    this.confirm.confirm({
      key: 'gl-pc', header: 'Execute year-end close', icon: 'pi pi-lock', acceptButtonStyleClass: 'p-button-danger',
      message: `Close ${name}? Revenue and expenses are closed into ${acc} with a posted closing entry, and the year and every period are closed for good. This cannot be undone.`,
      acceptLabel: 'Close the year', rejectLabel: 'Cancel',
      accept: () => this.execute()
    });
  }

  execute(): void {
    const uuid = this.yearUuid!;
    this.executing = true;
    this.api.executeYearEnd(uuid, this.retainedUuid).subscribe({
      next: r => { this.executing = false; this.result = r; this.resultVisible = true; this.load(); },
      error: err => { this.executing = false; this.messages.add({ severity: 'error', summary: 'Not closed', detail: glError(err, 'The year could not be closed.'), life: 10000 }); }
    });
  }
}
