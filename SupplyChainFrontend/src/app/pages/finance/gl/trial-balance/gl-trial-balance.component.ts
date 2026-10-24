import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { ButtonModule } from 'primeng/button';
import { CheckboxModule } from 'primeng/checkbox';
import { DrawerModule } from 'primeng/drawer';
import { InputTextModule } from 'primeng/inputtext';
import { SelectButtonModule } from 'primeng/selectbutton';
import { SelectModule } from 'primeng/select';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';
import { catchError, of } from 'rxjs';

import { AccountingService, FiscalYear } from '../../../../services/accounting.service';
import { GlAccountRef, GlJournalService, TrialBalanceLine, TrialBalanceQuery, TrialBalanceReport } from '../../../../services/gl-journal.service';
import { saveBlob } from '../../../../services/crm.service';
import { saveDownload } from '../../../../services/import.service';
import { FLOW } from '../../../../shared/flow';
import { toIsoDate } from '../../../finance-setup/finance-setup.shared';
import { glError } from '../gl.shared';
import { closingBalance, openingBalance, parens, tbTotals, trialBalanceCsv } from '../journal.shared';
import { GlAccountLedgerComponent } from './gl-account-ledger.component';

type Mode = 'year' | 'range';

/**
 * A47 C9 (tasks 347–355) — `/gl/trial-balance`: by fiscal year (whole year or one period) or a date range, with or
 * without opening balances and closing entries; per leaf account the opening, the period's debits and credits and the
 * balance (negatives in parentheses), a total row with the balanced indicator, drill-down to the account ledger and a
 * CSV export (the server's; built here only when the export route is missing).
 */
@Component({
  selector: 'app-gl-trial-balance',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, CheckboxModule, DrawerModule, InputTextModule, SelectButtonModule, SelectModule, ToastModule,
    GlAccountLedgerComponent, ...FLOW],
  providers: [MessageService],
  styleUrls: ['../gl.scss'],
  template: `
<p-toast></p-toast>
<sf-page [crumbs]="['Finance', 'General Ledger', 'Trial Balance']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button label="Generate" icon="pi pi-play" [loading]="loading" [disabled]="!!queryProblem" (onClick)="generate()" data-testid="generate"></p-button>
    <p-button label="Export CSV" icon="pi pi-download" [text]="true" [disabled]="!report || exporting" [loading]="exporting" (onClick)="exportCsv()" data-testid="export"></p-button>
    <p-button label="Print" icon="pi pi-print" [text]="true" [disabled]="!report" (onClick)="print()" data-testid="print"></p-button>
  </div>
  <div class="sf-body">
    <div class="sf-lr-head"><div><h1>Trial balance</h1>
      <div class="sf-sub">Debits and credits per account, computed from posted journal lines in the base currency</div></div></div>

    <section class="sf-card tb-noprint">
      <p-selectButton [options]="modeOptions" [(ngModel)]="mode" optionLabel="label" optionValue="value" [allowEmpty]="false" data-testid="mode"></p-selectButton>
      <div class="sf-fields" style="margin-top:.75rem">
        <ng-container *ngIf="mode === 'year'">
          <div class="sf-fld">
            <label for="tb-year" class="sf-req">Fiscal year</label>
            <p-select inputId="tb-year" [options]="yearOptions" [(ngModel)]="yearUuid" (ngModelChange)="onYearChange()" optionLabel="label"
                      optionValue="value" appendTo="body" placeholder="Choose…" data-testid="year"></p-select>
          </div>
          <div class="sf-fld">
            <label for="tb-period">Period</label>
            <p-select inputId="tb-period" [options]="periodOptions" [(ngModel)]="periodUuid" optionLabel="label" optionValue="value"
                      appendTo="body" data-testid="period"></p-select>
          </div>
        </ng-container>
        <ng-container *ngIf="mode === 'range'">
          <div class="sf-fld">
            <label for="tb-from" class="sf-req">From</label>
            <input pInputText id="tb-from" type="date" [(ngModel)]="from" data-testid="from" />
          </div>
          <div class="sf-fld">
            <label for="tb-to" class="sf-req">To</label>
            <input pInputText id="tb-to" type="date" [(ngModel)]="to" data-testid="to" />
          </div>
        </ng-container>
        <div class="sf-fld" style="grid-column: 1 / -1">
          <div class="tb-checks">
            <label class="gl-check"><p-checkbox [(ngModel)]="includeOpening" [binary]="true" data-testid="include-opening"></p-checkbox> Include opening balances</label>
            <label class="gl-check"><p-checkbox [(ngModel)]="includeClosing" [binary]="true" data-testid="include-closing"></p-checkbox> Include closing entries</label>
            <label class="gl-check"><p-checkbox [(ngModel)]="includeZero" [binary]="true" data-testid="include-zero"></p-checkbox> Show accounts without activity</label>
          </div>
        </div>
      </div>
      <div class="sf-sm" style="color: var(--sms-danger)" *ngIf="tried && queryProblem" data-testid="query-problem">{{ queryProblem }}</div>
    </section>

    <div class="gl-note er" *ngIf="loadError" data-testid="load-error"><i class="pi pi-exclamation-triangle"></i><div>{{ loadError }}</div></div>
    <div class="sf-card sf-empty" *ngIf="loading && !report"><i class="pi pi-spin pi-spinner"></i> Generating…</div>

    <section class="sf-card p0" *ngIf="report" data-testid="report">
      <div class="sf-ch sf-pad"><h2>{{ report.fromDate | date:'dd MMM yyyy' }} – {{ report.toDate | date:'dd MMM yyyy' }}
          <span class="sf-mu sf-sm" *ngIf="report.fiscalYear"> · {{ report.fiscalYear.fiscalYearName }}</span></h2>
        <span class="sf-end sf-mu sf-sm">{{ report.currencyCode || '' }}<ng-container *ngIf="report.generatedAt"> · generated {{ report.generatedAt | date:'dd MMM yyyy, HH:mm' }}</ng-container></span></div>
      <div class="gl-scroll">
        <table class="tb-table" data-testid="tb">
          <thead><tr><th>Code</th><th>Account</th><th class="gl-num" *ngIf="showOpening">Opening</th><th class="gl-num">Debit</th><th class="gl-num">Credit</th><th class="gl-num">Balance</th></tr></thead>
          <tbody>
            <tr *ngFor="let l of report.lines" [attr.data-testid]="'tb-' + l.account.accountCode" [class.gl-inactive]="l.isActive === false">
              <td class="sf-mono"><a href="" (click)="$event.preventDefault(); drill(l)" [attr.data-testid]="'drill-' + l.account.accountCode">{{ l.account.accountCode }}</a></td>
              <td><a href="" class="tb-acc" (click)="$event.preventDefault(); drill(l)">{{ l.account.accountName }}</a> <span class="sf-mu sf-sm">{{ l.rootType }}</span></td>
              <td class="gl-num" *ngIf="showOpening">{{ signed(opening(l)) }}</td>
              <td class="gl-num">{{ l.periodDebit ? plain(l.periodDebit) : '' }}</td>
              <td class="gl-num">{{ l.periodCredit ? plain(l.periodCredit) : '' }}</td>
              <td class="gl-num" [attr.data-testid]="'bal-' + l.account.accountCode">{{ signed(closing(l)) }}</td>
            </tr>
            <tr *ngIf="!report.lines.length"><td colspan="6" class="sf-empty" data-testid="tb-empty">No account has posted activity in this range.</td></tr>
          </tbody>
          <tfoot>
            <tr class="sf-tot" data-testid="tb-total">
              <td></td><td>Total</td>
              <td class="gl-num" *ngIf="showOpening">{{ signed(totals.opening) }}</td>
              <td class="gl-num" data-testid="tb-total-debit">{{ plain(totals.debit) }}</td>
              <td class="gl-num" data-testid="tb-total-credit">{{ plain(totals.credit) }}</td>
              <td class="gl-num" data-testid="tb-total-balance">{{ signed(totals.balance) }}</td>
            </tr>
          </tfoot>
        </table>
      </div>
      <div class="gl-foot">
        <span [class.tb-ok]="totals.balanced" [class.tb-bad]="!totals.balanced" data-testid="balanced">
          <i class="pi" [ngClass]="totals.balanced ? 'pi-check-circle' : 'pi-exclamation-triangle'"></i>
          {{ totals.balanced ? 'Trial balance is balanced.' : 'Trial balance is not balanced — closing debits ' + plain(totals.closingDebit) + ', credits ' + plain(totals.closingCredit) + '.' }}</span>
        <span class="sf-end">Click an account for its ledger.</span>
      </div>
    </section>
  </div>
</sf-page>

<p-drawer [(visible)]="ledgerVisible" position="right" styleClass="tb-drawer" data-testid="ledger-drawer">
  <ng-template pTemplate="header">
    <div class="tb-dhead" *ngIf="ledgerAccount"><strong><span class="sf-mono">{{ ledgerAccount.accountCode }}</span> {{ ledgerAccount.accountName }}</strong>
      <span class="sf-mu sf-sm">{{ report?.fromDate | date:'dd MMM yyyy' }} – {{ report?.toDate | date:'dd MMM yyyy' }}</span></div>
  </ng-template>
  <app-gl-account-ledger *ngIf="ledgerVisible && ledgerAccount" [account]="ledgerAccount" [fromDate]="report?.fromDate ?? null"
                         [toDate]="report?.toDate ?? null" [includeClosingEntries]="!!report?.includeClosingEntries"></app-gl-account-ledger>
</p-drawer>`,
  styles: [`
    .tb-checks { display: flex; flex-wrap: wrap; gap: .5rem 1.5rem; }
    .tb-table { width: 100%; min-width: 44rem; border-collapse: collapse; font-size: .875rem; }
    .tb-table th, .tb-table td { padding: .4rem .75rem; border-top: 1px solid var(--sms-border-soft); text-align: left; }
    .tb-table th.gl-num, .tb-table td.gl-num { text-align: right; }
    .tb-table tfoot td { font-weight: 600; }
    .tb-acc { color: var(--sms-text); text-decoration: none; }
    .tb-acc:hover { text-decoration: underline; }
    .tb-ok { color: var(--sms-ok); } .tb-bad { color: var(--sms-danger); font-weight: 600; }
    :host ::ng-deep .tb-drawer { width: 48rem; max-width: 100vw; }
    .tb-dhead { display: flex; flex-direction: column; gap: .125rem; }
    @media print { .tb-noprint { display: none; } }
  `]
})
export class GlTrialBalanceComponent implements OnInit {
  private readonly api = inject(GlJournalService);
  private readonly accounting = inject(AccountingService);
  private readonly messages = inject(MessageService);

  readonly modeOptions = [{ label: 'Fiscal year', value: 'year' }, { label: 'Date range', value: 'range' }];
  mode: Mode = 'year';
  years: FiscalYear[] = [];
  yearOptions: { label: string; value: string }[] = [];
  periodOptions: { label: string; value: string | null }[] = [{ label: 'Whole year', value: null }];
  yearUuid: string | null = null;
  periodUuid: string | null = null;
  from = '';
  to = '';
  includeOpening = true;
  includeClosing = false;
  includeZero = false;

  report: TrialBalanceReport | null = null;
  /** The query the shown report was made with (the export repeats it). */
  private lastQuery: TrialBalanceQuery | null = null;
  loading = false;
  loadError = '';
  tried = false;
  exporting = false;

  ledgerVisible = false;
  ledgerAccount: GlAccountRef | null = null;

  get totals() { return tbTotals(this.report); }
  get showOpening(): boolean { return this.report?.includeOpeningBalances ?? this.lastQuery?.includeOpeningBalances ?? true; }

  /** The server's own texts (§5) for what it would refuse. */
  get queryProblem(): string | null {
    if (this.mode === 'year') return this.yearUuid ? null : 'Enter a from and a to date, or a fiscal year.';
    if (!this.from || !this.to) return 'Enter a from and a to date, or a fiscal year.';
    if (this.to < this.from) return 'The to date must not be before the from date.';
    return null;
  }

  ngOnInit(): void {
    const today = toIsoDate(new Date());
    this.accounting.getFiscalYears().pipe(catchError(() => of(null))).subscribe(r => {
      this.years = [...(r?.result ?? [])].sort((a, b) => b.startDate.localeCompare(a.startDate));
      this.yearOptions = this.years.map(y => ({ label: y.fiscalYearName + (y.isClosed ? ' (closed)' : ''), value: y.uuid }));
      if (!this.years.length) {
        // No fiscal year readable (or none yet): a date range, this month.
        this.mode = 'range';
        this.from = today.slice(0, 8) + '01';
        this.to = today;
        return;
      }
      const current = this.years.find(y => y.startDate <= today && today <= y.endDate) ?? this.years[0];
      this.yearUuid = current.uuid;
      this.onYearChange();
    });
  }

  onYearChange(): void {
    const y = this.years.find(x => x.uuid === this.yearUuid);
    this.periodUuid = null;
    this.periodOptions = [{ label: 'Whole year', value: null },
      ...[...(y?.periods ?? [])].sort((a, b) => a.periodNumber - b.periodNumber).map(p => ({ label: `${p.periodNumber}. ${p.periodName}`, value: p.uuid as string | null }))];
  }

  /**
   * D-FE-9 — a whole year goes as `fiscalYearUuid` (contract §5); a single period as its dates (the contract lists no
   * period filter); a range as its dates.
   */
  buildQuery(): TrialBalanceQuery {
    const flags = { includeOpeningBalances: this.includeOpening, includeClosingEntries: this.includeClosing, includeZeroBalances: this.includeZero };
    if (this.mode === 'range') return { fromDate: this.from, toDate: this.to, ...flags };
    const y = this.years.find(x => x.uuid === this.yearUuid);
    const p = y?.periods?.find(x => x.uuid === this.periodUuid);
    if (p) return { fromDate: p.startDate, toDate: p.endDate, ...flags };
    return { fiscalYearUuid: this.yearUuid, ...flags };
  }

  generate(): void {
    this.tried = true;
    if (this.queryProblem || this.loading) return;
    const q = this.buildQuery();
    this.loading = true;
    this.loadError = '';
    this.api.getTrialBalance(q).subscribe({
      next: r => { this.loading = false; this.report = r; this.lastQuery = q; },
      error: err => { this.loading = false; this.report = null; this.loadError = glError(err, 'The trial balance could not be generated.'); }
    });
  }

  opening(l: TrialBalanceLine): number { return openingBalance(l); }
  closing(l: TrialBalanceLine): number { return closingBalance(l); }
  signed(n: number): string { return parens(n); }
  plain(n: number): string { return parens(n); }

  drill(l: TrialBalanceLine): void {
    this.ledgerAccount = l.account;
    this.ledgerVisible = true;
  }

  exportCsv(): void {
    if (!this.report || !this.lastQuery) return;
    const r = this.report;
    const name = `trial-balance-${r.fromDate}-${r.toDate}.csv`;
    this.exporting = true;
    this.api.exportTrialBalance(this.lastQuery).subscribe({
      next: res => { this.exporting = false; saveDownload(res, name); },
      error: err => {
        this.exporting = false;
        // The export route missing (404/405): the shown report as CSV, built here.
        if (err instanceof HttpErrorResponse && (err.status === 404 || err.status === 405)) {
          saveBlob(new Blob(['﻿' + trialBalanceCsv(r)], { type: 'text/csv;charset=utf-8' }), name);
          return;
        }
        this.messages.add({ severity: 'error', summary: 'Not exported', detail: glError(err, 'The trial balance could not be exported.'), life: 8000 });
      }
    });
  }

  print(): void { window.print(); }
}
