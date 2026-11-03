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
  AR_AP_PERMS, AgingBasis, AgingQuery, AgingReport, ApSummary, ArApService, MATCHING_ROUTE, MATCH_READ, PAYMENT_RUNS_ROUTE, SCHEDULES_ROUTE,
  SCHEDULE_READ, VENDOR_STATEMENT_READ, VENDOR_STATEMENT_ROUTE
} from '../../../../services/ar-ap.service';
import { AuthService } from '../../../service/auth.service';
import { saveBlob } from '../../../../services/crm.service';
import { saveDownload } from '../../../../services/import.service';
import { FLOW } from '../../../../shared/flow';
import { AgingDetailState, AgingTableComponent } from '../aging-table/aging-table.component';
import { catchError, of } from 'rxjs';
import { BASIS_OPTIONS, DEFAULT_BUCKETS, agingCsv, arApError, arApErrorAsync, bucketsProblem, money, parseBuckets, today } from '../ar-ap.shared';

/**
 * A48 §8.3, tasks 556–559 — `/finance/ap`: Total AP, Overdue, DPO and due this week (with trends when sent), the aged
 * payables (as-of date, basis, buckets), the vendor drill-down to its statement and reconciliation, a row expansion with
 * the open supplier invoices, and PDF / Excel / CSV export. Base currency (D-13) from the supplier documents (D-1).
 */
@Component({
  selector: 'app-ap-dashboard',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule, ButtonModule, InputTextModule, SelectModule, ToastModule, AgingTableComponent, ...FLOW],
  providers: [MessageService],
  styleUrls: ['../../gl/gl.scss', '../ar-ap.scss'],
  template: `
<p-toast></p-toast>
<sf-page [crumbs]="['Finance', 'Payables', 'Dashboard']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <a *ngIf="canPropose" class="p-button" [routerLink]="[runsRoute, 'new']" data-testid="go-run"><i class="pi pi-plus"></i>&nbsp;Create payment run</a>
    <a *ngIf="canMatch" class="p-button p-button-text" [routerLink]="matchingRoute" data-testid="go-matching"><i class="pi pi-check-square"></i>&nbsp;Pending matches<ng-container
       *ngIf="pendingMatches">&nbsp;({{ pendingMatches }})</ng-container></a>
    <a *ngIf="canSchedules" class="p-button p-button-text" [routerLink]="schedulesRoute" [queryParams]="{ view: 'discounts' }" data-testid="go-discounts">
      <i class="pi pi-percentage"></i>&nbsp;Discount opportunities</a>
    <span class="sf-end">
      <ng-container *ngIf="canExport">
        <p-button label="PDF" icon="pi pi-file-pdf" [text]="true" [disabled]="!!exporting" [loading]="exporting === 'pdf'" (onClick)="export('pdf')" data-testid="export-pdf"></p-button>
        <p-button label="Excel" icon="pi pi-file-excel" [text]="true" [disabled]="!!exporting" [loading]="exporting === 'xlsx'" (onClick)="export('xlsx')" data-testid="export-xlsx"></p-button>
      </ng-container>
      <p-button label="CSV" icon="pi pi-download" [text]="true" [disabled]="!report?.lines?.length" (onClick)="exportCsv()" data-testid="export"></p-button>
      <p-button label="Refresh" icon="pi pi-refresh" [text]="true" (onClick)="load()" data-testid="refresh"></p-button>
    </span>
  </div>
  <div class="sf-body aa-inv">
    <div class="sf-lr-head"><div><h1>Accounts payable</h1>
      <div class="sf-sub">What is owed to vendors, when it falls due and what can be paid early<ng-container *ngIf="currency"> · {{ currency }}</ng-container></div></div></div>
    <div class="gl-note er" *ngIf="summaryError" data-testid="summary-error"><i class="pi pi-exclamation-triangle"></i><div>{{ summaryError }}</div></div>

    <div class="sf-grid sf-tiles" data-testid="kpis">
      <div class="sf-tile" data-testid="kpi-total"><span class="l">Total AP</span><span class="v">{{ summary ? amount(summary.totalOutstanding) : '…' }}</span>
        <span class="s" *ngIf="summary">{{ summary.totalVendorsWithBalance }} vendors<ng-container *ngIf="summary.unappliedCredits"> · {{ amount(summary.unappliedCredits) }} credits</ng-container></span></div>
      <div class="sf-tile" data-testid="kpi-overdue"><span class="l">Overdue</span><span class="v">{{ summary ? amount(summary.totalOverdue) : '…' }}</span>
        <span class="s" *ngIf="summary">{{ summary.totalVendorsOverdue }} vendors</span></div>
      <div class="sf-tile" data-testid="kpi-dpo"><span class="l">DPO</span><span class="v">{{ summary ? days(summary.dpo) : '…' }}</span>
        <span class="s" *ngIf="summary">{{ summary.periodDays }}-day period</span></div>
      <div class="sf-tile" data-testid="kpi-week"><span class="l">Due this week</span><span class="v">{{ summary ? amount(summary.totalDueThisWeek) : '…' }}</span>
        <span class="s" *ngIf="summary?.discountOpportunityAmount">{{ amount(summary?.discountOpportunityAmount) }} discount available</span></div>
    </div>

    <section class="sf-card p0">
      <div class="sf-ch sf-pad"><h2>Aged payables</h2></div>
      <div class="aa-toolbar sf-pad" style="padding-top:0">
        <div class="sf-fld"><label for="ap-asof">As of</label><input pInputText id="ap-asof" type="date" [(ngModel)]="asOfDate" (change)="load()" data-testid="as-of" /></div>
        <div class="sf-fld"><label for="ap-basis">Age by</label>
          <p-select inputId="ap-basis" [options]="basisOptions" [(ngModel)]="basis" (ngModelChange)="loadAging()" optionLabel="label" optionValue="value" appendTo="body" data-testid="basis"></p-select></div>
        <div class="sf-fld"><label for="ap-buckets">Buckets (days)</label>
          <input pInputText id="ap-buckets" [(ngModel)]="bucketText" (keyup.enter)="applyBuckets()" placeholder="30, 60, 90, 120" data-testid="buckets" /></div>
        <p-button label="Apply" [text]="true" (onClick)="applyBuckets()" data-testid="apply-buckets"></p-button>
      </div>
      <div class="gl-note wn" *ngIf="bucketError" style="margin: 0 1rem .75rem" data-testid="bucket-error"><i class="pi pi-info-circle"></i><div>{{ bucketError }}</div></div>
      <div class="gl-note er" *ngIf="agingError" style="margin: 0 1rem .75rem" data-testid="aging-error"><i class="pi pi-exclamation-triangle"></i><div>{{ agingError }}</div></div>
      <div class="sf-empty" *ngIf="agingLoading"><i class="pi pi-spin pi-spinner"></i> Loading…</div>
      <app-aging-table *ngIf="!agingLoading" [report]="report" partnerLabel="Vendor" [partnerLink]="canVendor ? vendorLink : null" [details]="details"
                       [baseCurrency]="currency" (expand)="loadDetail($event)" emptyText="Nothing is owed to any vendor on this date."></app-aging-table>
      <div class="sf-mu sf-sm sf-pad" *ngIf="truncated" data-testid="truncated">Showing the first {{ report?.lines?.length }} of {{ report?.totalRecords }} vendors; the export has them all.</div>
    </section>
  </div>
</sf-page>`
})
export class ApDashboardComponent implements OnInit {
  private readonly api = inject(ArApService);
  private readonly auth = inject(AuthService);
  private readonly messages = inject(MessageService);

  readonly basisOptions = BASIS_OPTIONS;
  readonly runsRoute = PAYMENT_RUNS_ROUTE;
  readonly matchingRoute = MATCHING_ROUTE;
  readonly schedulesRoute = SCHEDULES_ROUTE;
  readonly vendorLink = (uuid: string) => [VENDOR_STATEMENT_ROUTE, uuid];

  summary: ApSummary | null = null;
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

  get canPropose(): boolean { return this.auth.hasPermission(AR_AP_PERMS.PAYMENT_RUN_PROPOSE); }
  get canMatch(): boolean { return this.auth.hasAnyPermission(...MATCH_READ); }
  get canSchedules(): boolean { return this.auth.hasAnyPermission(...SCHEDULE_READ); }
  get canVendor(): boolean { return this.auth.hasAnyPermission(...VENDOR_STATEMENT_READ); }
  get canExport(): boolean { return this.auth.hasPermission(AR_AP_PERMS.AP_VIEW) && this.auth.hasPermission('REPORT_EXPORT'); }
  get currency(): string | null { return this.report?.baseCurrencyCode ?? this.summary?.baseCurrencyCode ?? null; }
  get truncated(): boolean { return !!this.report?.totalRecords && this.report.totalRecords > this.report.lines.length; }

  /** Invoices waiting in matching — the pending list's count (§6; the summary does not carry it). */
  pendingMatches: number | null = null;

  ngOnInit(): void { this.load(); }

  load(): void {
    this.loadSummary();
    this.loadAging();
    if (this.canMatch) this.api.getPendingMatches({ pageSize: 1 }).pipe(catchError(() => of(null))).subscribe(r => this.pendingMatches = r ? r.totalRecords : null);
  }

  loadSummary(): void {
    this.summaryError = '';
    this.api.getApSummary(this.asOfDate).subscribe({
      next: s => this.summary = s,
      error: err => { this.summary = null; this.summaryError = arApError(err, 'The payables summary could not be loaded.'); }
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
    this.api.getApAging(this.query()).subscribe({
      next: r => { if (seq !== this.agingSeq) return; this.agingLoading = false; this.report = r; },
      error: err => {
        if (seq !== this.agingSeq) return;
        this.agingLoading = false; this.report = null; this.agingError = arApError(err, 'The aged payables could not be loaded.');
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

  loadDetail(partnerUuid: string): void {
    this.details = { ...this.details, [partnerUuid]: 'loading' };
    this.api.getApAging(this.query({ partnerUuid, includeDetails: true })).subscribe({
      next: r => this.details = { ...this.details, [partnerUuid]: r.lines.find(l => l.partner.uuid === partnerUuid)?.details ?? r.lines[0]?.details ?? [] },
      error: () => this.details = { ...this.details, [partnerUuid]: 'error' }
    });
  }

  export(format: 'pdf' | 'xlsx'): void {
    this.exporting = format;
    this.api.exportApAging(this.query(), format).subscribe({
      next: res => { this.exporting = null; saveDownload(res, `aged-payables-${this.asOfDate}.${format}`); },
      error: async err => { this.exporting = null; this.messages.add({ severity: 'error', summary: 'Error', detail: await arApErrorAsync(err, 'The export could not be made.'), life: 8000 }); }
    });
  }

  exportCsv(): void {
    if (!this.report) return;
    saveBlob(new Blob(['﻿' + agingCsv(this.report, 'Vendor')], { type: 'text/csv;charset=utf-8' }), `aged-payables-${this.asOfDate}.csv`);
  }

  amount(n: number | null | undefined): string { return money(n); }
  days(n: number | null | undefined): string { return n === null || n === undefined ? '—' : `${Math.round(n)} days`; }
}
