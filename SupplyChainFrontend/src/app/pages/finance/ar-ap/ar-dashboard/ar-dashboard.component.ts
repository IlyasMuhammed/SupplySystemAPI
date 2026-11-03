import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { SelectModule } from 'primeng/select';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';

import {
  AR_AP_PERMS, AgingBasis, AgingQuery, AgingReport, ArApService, ArSummary, CREDIT_READ, CREDIT_ROUTE, DUNNING_RUNS_ROUTE, DUNNING_RUN_READ,
  STATEMENT_READ, STATEMENT_ROUTE
} from '../../../../services/ar-ap.service';
import { AuthService } from '../../../service/auth.service';
import { FLOW } from '../../../../shared/flow';
import { saveBlob } from '../../../../services/crm.service';
import { saveDownload } from '../../../../services/import.service';
import { AgingDetailState, AgingTableComponent } from '../aging-table/aging-table.component';
import { catchError, of } from 'rxjs';
import { BASIS_OPTIONS, DEFAULT_BUCKETS, agingCsv, arApError, arApErrorAsync, bucketsProblem, money, parseBuckets, today } from '../ar-ap.shared';

/**
 * A48 §8.1, tasks 539–544 / 555 — `/finance/ar`: Total AR, Overdue, DSO (with collection rate) and customers on hold
 * (the summary carries no trend — R-FE-1), the aged receivables (as-of date, due- or document-date basis, custom buckets), a customer
 * drill-down to credit control and a row expansion with the open documents; PDF / Excel export (REPORT_EXPORT) and a
 * CSV of the table. Base currency (D-13) from the Finance documents (D-1).
 */
@Component({
  selector: 'app-ar-dashboard',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule, ButtonModule, InputTextModule, SelectModule, ToastModule, AgingTableComponent, ...FLOW],
  providers: [MessageService],
  styleUrls: ['../../gl/gl.scss', '../ar-ap.scss'],
  template: `
<p-toast></p-toast>
<sf-page [crumbs]="['Finance', 'Receivables', 'Dashboard']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <a *ngIf="canStatements" class="p-button" [routerLink]="statementRoute" data-testid="go-statements"><i class="pi pi-file"></i>&nbsp;Generate statements</a>
    <a *ngIf="canDunning" class="p-button p-button-text" [routerLink]="dunningRoute" data-testid="go-dunning"><i class="pi pi-bell"></i>&nbsp;Run dunning</a>
    <a *ngIf="canCredit" class="p-button p-button-text" [routerLink]="creditRoute" data-testid="go-credit"><i class="pi pi-shield"></i>&nbsp;Credit review</a>
    <span class="sf-end">
      <ng-container *ngIf="canExport">
        <p-button label="PDF" icon="pi pi-file-pdf" [text]="true" [disabled]="!!exporting" [loading]="exporting === 'pdf'" (onClick)="export('pdf')" data-testid="export-pdf"></p-button>
        <p-button label="Excel" icon="pi pi-file-excel" [text]="true" [disabled]="!!exporting" [loading]="exporting === 'xlsx'" (onClick)="export('xlsx')" data-testid="export-xlsx"></p-button>
      </ng-container>
      <p-button label="CSV" icon="pi pi-download" [text]="true" [disabled]="!report?.lines?.length" (onClick)="exportCsv()" data-testid="export"></p-button>
      <p-button label="Refresh" icon="pi pi-refresh" [text]="true" (onClick)="load()" data-testid="refresh"></p-button>
    </span>
  </div>
  <div class="sf-body">
    <div class="sf-lr-head"><div><h1>Accounts receivable</h1>
      <div class="sf-sub">What customers owe, how late it is, and who is on hold<ng-container *ngIf="currency"> · {{ currency }}</ng-container></div></div></div>

    <div class="gl-note er" *ngIf="summaryError" data-testid="summary-error"><i class="pi pi-exclamation-triangle"></i><div>{{ summaryError }}</div></div>

    <!-- Tasks 540 / 555 — KPI tiles (sf-tiles stack on a phone) -->
    <div class="sf-grid sf-tiles" data-testid="kpis">
      <div class="sf-tile" data-testid="kpi-total"><span class="l">Total AR</span><span class="v">{{ summary ? amount(summary.totalOutstanding) : '…' }}</span>
        <span class="s" *ngIf="summary">{{ summary.totalCustomersWithBalance }} customers<ng-container *ngIf="summary.unappliedCredits"> · {{ amount(summary.unappliedCredits) }} unapplied</ng-container></span></div>
      <div class="sf-tile" data-testid="kpi-overdue"><span class="l">Overdue</span><span class="v">{{ summary ? amount(summary.totalOverdue) : '…' }}</span>
        <span class="s" *ngIf="summary">{{ summary.totalCustomersOverdue }} customers · {{ days(summary.daysBeyondTerms) }} past terms</span></div>
      <div class="sf-tile" data-testid="kpi-dso"><span class="l">DSO</span><span class="v">{{ summary ? days(summary.dso) : '…' }}</span>
        <span class="s" *ngIf="summary">collection rate {{ summary.collectionRate | number:'1.0-1' }}% · {{ summary.periodDays }}-day period</span></div>
      <div class="sf-tile" data-testid="kpi-hold"><span class="l">On hold</span><span class="v">{{ onHold === null ? '—' : onHold }}</span>
        <span class="s">customers</span></div>
    </div>

    <!-- Tasks 541 / 542 — aged receivables -->
    <section class="sf-card p0">
      <div class="sf-ch sf-pad"><h2>Aged receivables</h2></div>
      <div class="aa-toolbar sf-pad" style="padding-top:0">
        <div class="sf-fld"><label for="ar-asof">As of</label>
          <input pInputText id="ar-asof" type="date" [(ngModel)]="asOfDate" (change)="load()" data-testid="as-of" /></div>
        <div class="sf-fld"><label for="ar-basis">Age by</label>
          <p-select inputId="ar-basis" [options]="basisOptions" [(ngModel)]="basis" (ngModelChange)="loadAging()" optionLabel="label" optionValue="value"
                    appendTo="body" data-testid="basis"></p-select></div>
        <div class="sf-fld"><label for="ar-buckets">Buckets (days)</label>
          <input pInputText id="ar-buckets" [(ngModel)]="bucketText" (keyup.enter)="applyBuckets()" placeholder="30, 60, 90, 120" data-testid="buckets" /></div>
        <p-button label="Apply" [text]="true" (onClick)="applyBuckets()" data-testid="apply-buckets"></p-button>
      </div>
      <div class="gl-note wn" *ngIf="bucketError" style="margin: 0 1rem .75rem" data-testid="bucket-error"><i class="pi pi-info-circle"></i><div>{{ bucketError }}</div></div>
      <div class="gl-note er" *ngIf="agingError" style="margin: 0 1rem .75rem" data-testid="aging-error"><i class="pi pi-exclamation-triangle"></i><div>{{ agingError }}</div></div>
      <div class="sf-empty" *ngIf="agingLoading"><i class="pi pi-spin pi-spinner"></i> Loading…</div>
      <app-aging-table *ngIf="!agingLoading" [report]="report" partnerLabel="Customer" [partnerLink]="canCredit ? creditLink : null"
                       [details]="details" [baseCurrency]="currency" (expand)="loadDetail($event)"
                       emptyText="No customer owes anything on this date."></app-aging-table>
      <div class="sf-mu sf-sm sf-pad" *ngIf="truncated" data-testid="truncated">Showing the first {{ report?.lines?.length }} of {{ report?.totalRecords }} customers; the export has them all.</div>
    </section>
  </div>
</sf-page>`
})
export class ArDashboardComponent implements OnInit {
  private readonly api = inject(ArApService);
  private readonly auth = inject(AuthService);
  private readonly messages = inject(MessageService);

  readonly basisOptions = BASIS_OPTIONS;
  readonly statementRoute = STATEMENT_ROUTE;
  readonly dunningRoute = DUNNING_RUNS_ROUTE;
  readonly creditRoute = CREDIT_ROUTE;
  readonly creditLink = (uuid: string) => [CREDIT_ROUTE, uuid];

  summary: ArSummary | null = null;
  summaryError = '';
  report: AgingReport | null = null;
  agingLoading = false;
  agingError = '';
  details: Record<string, AgingDetailState> = {};
  exporting: 'pdf' | 'xlsx' | null = null;

  asOfDate = today();
  basis: AgingBasis = 'DueDate';
  bucketText = DEFAULT_BUCKETS.join(', ');
  buckets: number[] = [...DEFAULT_BUCKETS];
  bucketError = '';

  get canStatements(): boolean { return this.auth.hasAnyPermission(...STATEMENT_READ); }
  get canDunning(): boolean { return this.auth.hasAnyPermission(...DUNNING_RUN_READ); }
  get canCredit(): boolean { return this.auth.hasAnyPermission(...CREDIT_READ); }
  /** §5 — export needs AR_VIEW + REPORT_EXPORT. */
  get canExport(): boolean { return this.auth.hasPermission(AR_AP_PERMS.AR_VIEW) && this.auth.hasPermission('REPORT_EXPORT'); }
  get currency(): string | null { return this.report?.baseCurrencyCode ?? this.summary?.baseCurrencyCode ?? null; }
  get truncated(): boolean { return !!this.report?.totalRecords && this.report.totalRecords > this.report.lines.length; }

  /** Customers on credit hold — the exposures list's count (§2; the summary does not carry it). */
  onHold: number | null = null;

  ngOnInit(): void { this.load(); }

  load(): void {
    this.loadSummary();
    this.loadAging();
    this.api.getExposures({ onHold: true, pageSize: 1 }).pipe(catchError(() => of(null))).subscribe(r => this.onHold = r ? r.totalRecords : null);
  }

  loadSummary(): void {
    this.summaryError = '';
    this.api.getArSummary(this.asOfDate).subscribe({
      next: s => this.summary = s,
      error: err => { this.summary = null; this.summaryError = arApError(err, 'The receivables summary could not be loaded.'); }
    });
  }

  private query(extra: Partial<AgingQuery> = {}): AgingQuery {
    return { asOfDate: this.asOfDate, basis: this.basis, buckets: this.buckets, sortBy: 'TotalOutstanding', sortDirection: 'Desc', ...extra };
  }

  private agingSeq = 0;

  loadAging(): void {
    const seq = ++this.agingSeq;
    this.agingLoading = true;
    this.agingError = '';
    this.details = {};
    this.api.getArAging(this.query()).subscribe({
      next: r => { if (seq !== this.agingSeq) return; this.agingLoading = false; this.report = r; },
      error: err => {
        if (seq !== this.agingSeq) return;
        this.agingLoading = false; this.report = null; this.agingError = arApError(err, 'The aged receivables could not be loaded.');
      }
    });
  }

  applyBuckets(): void {
    const b = parseBuckets(this.bucketText);
    this.bucketError = bucketsProblem(b) ?? '';
    if (this.bucketError) return;
    this.buckets = b!;
    this.loadAging();
  }

  /** Task 544 — the customer's open documents for the expanded row. */
  loadDetail(partnerUuid: string): void {
    this.details = { ...this.details, [partnerUuid]: 'loading' };
    this.api.getArAging(this.query({ partnerUuid, includeDetails: true })).subscribe({
      next: r => this.details = { ...this.details, [partnerUuid]: r.lines.find(l => l.partner.uuid === partnerUuid)?.details ?? r.lines[0]?.details ?? [] },
      error: () => this.details = { ...this.details, [partnerUuid]: 'error' }
    });
  }

  export(format: 'pdf' | 'xlsx'): void {
    this.exporting = format;
    this.api.exportArAging(this.query(), format).subscribe({
      next: res => { this.exporting = null; saveDownload(res, `aged-receivables-${this.asOfDate}.${format}`); },
      error: async err => { this.exporting = null; this.messages.add({ severity: 'error', summary: 'Error', detail: await arApErrorAsync(err, 'The export could not be made.'), life: 8000 }); }
    });
  }

  exportCsv(): void {
    if (!this.report) return;
    saveBlob(new Blob(['﻿' + agingCsv(this.report, 'Customer')], { type: 'text/csv;charset=utf-8' }), `aged-receivables-${this.asOfDate}.csv`);
  }

  amount(n: number | null | undefined): string { return money(n); }
  days(n: number | null | undefined): string { return n === null || n === undefined ? '—' : `${Math.round(n)} days`; }
}
