import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router, RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { TableLazyLoadEvent, TableModule } from 'primeng/table';

import { AP_SETTINGS_ROUTE, ArApService, MATCHING_ROUTE, MatchSummary } from '../../../../services/ar-ap.service';
import { FLOW } from '../../../../shared/flow';
import { arApError, matchLabel, matchState, matchTone, money, signedMoney } from '../ar-ap.shared';

/** Contract §6 `result` filter (LineMatchResult); none = every pending invoice. */
export const MATCH_FILTERS: { label: string; value: string | null }[] = [
  { label: 'All pending', value: null }, { label: 'Not matched yet', value: 'NotMatched' }, { label: 'Mismatch', value: 'Mismatch' },
  { label: 'Partial match', value: 'PartialMatch' }, { label: 'Overridden', value: 'Overridden' }, { label: 'Matched', value: 'Matched' }
];

/**
 * A48 task 576 — `/finance/ap/matching`: supplier invoices waiting for their PO / GRN match or its approval (contract
 * §6 `matching/pending`), filtered by the per-line result, paged on the server. A row opens the line comparison.
 */
@Component({
  selector: 'app-matching-list',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule, ButtonModule, InputTextModule, TableModule, ...FLOW],
  styleUrls: ['../../gl/gl.scss', '../ar-ap.scss'],
  template: `
<sf-page [crumbs]="['Finance', 'Payables', 'Invoice matching']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <a class="p-button p-button-text" [routerLink]="settingsRoute"><i class="pi pi-sliders-h"></i>&nbsp;Tolerance rules</a>
    <span class="sf-end"><p-button label="Refresh" icon="pi pi-refresh" [text]="true" (onClick)="load(1)" data-testid="refresh"></p-button></span>
  </div>
  <div class="sf-body">
    <div class="sf-lr-head"><div><h1>Invoice matching <span class="sf-count">{{ total }}</span></h1>
      <div class="sf-sub">Supplier invoices compared line by line with their purchase order and goods receipt before they can be paid</div></div>
      <div class="sf-filters">
        <button type="button" class="sf-chip" *ngFor="let f of filters" [class.on]="result === f.value" (click)="setResult(f.value)"
                [attr.data-testid]="'chip-' + (f.value || 'all')">{{ f.label }}</button>
      </div></div>
    <div class="sf-filters"><input pInputText class="sf-search" [(ngModel)]="search" (keyup.enter)="load(1)" placeholder="Invoice, vendor invoice no. or PO…" data-testid="search" /></div>
    <div class="gl-note er" *ngIf="loadError" data-testid="load-error"><i class="pi pi-exclamation-triangle"></i><div>{{ loadError }}</div></div>
    <section class="sf-card p0">
      <div class="gl-scroll">
        <p-table [value]="rows" [lazy]="true" (onLazyLoad)="onLazy($event)" [loading]="loading" [paginator]="true" [rows]="pageSize" [totalRecords]="total"
                 [rowsPerPageOptions]="[25, 50, 100]" dataKey="invoiceUuid" styleClass="p-datatable-sm" [tableStyle]="{ 'min-width': '52rem' }" data-testid="matches">
          <ng-template pTemplate="header"><tr><th>Invoice</th><th>Vendor</th><th>PO</th><th>Type</th><th class="gl-num">Total</th><th class="gl-num">Variance</th><th>Status</th></tr></ng-template>
          <ng-template pTemplate="body" let-m>
            <tr class="aa-click" (click)="open(m)" [attr.data-testid]="'match-' + m.invoiceUuid">
              <td><a class="sf-mono" [routerLink]="[route, m.invoiceUuid]" (click)="$event.stopPropagation()">{{ m.invoiceNumber }}</a>
                <div class="sf-mu sf-sm">{{ m.supplierInvoiceNo || '' }}<ng-container *ngIf="m.invoiceDate"> · {{ m.invoiceDate | date:'dd MMM yyyy' }}</ng-container></div></td>
              <td>{{ m.vendorName }}</td>
              <td class="sf-mono">{{ m.poNumber || '—' }}</td>
              <td class="sf-sm">{{ m.matchType ? label(m.matchType) : '' }}</td>
              <td class="gl-num">{{ amount(m.totalAmount) }} <span class="sf-mu sf-sm">{{ m.currencyCode }}</span></td>
              <td class="gl-num" [class.aa-warn]="m.withinTolerance === false">{{ m.varianceAmount ? signed(m.varianceAmount) : '' }}
                <span class="sf-mu sf-sm" *ngIf="m.variancePercent">({{ m.variancePercent }}%)</span></td>
              <td><span class="sf-pill" [ngClass]="tone(state(m))">{{ label(state(m)) }}</span></td>
            </tr>
          </ng-template>
          <ng-template pTemplate="emptymessage"><tr><td colspan="7" class="sf-empty" data-testid="empty">No invoice waiting here.</td></tr></ng-template>
        </p-table>
      </div>
    </section>
  </div>
</sf-page>`
})
export class MatchingListComponent {
  private readonly api = inject(ArApService);
  private readonly router = inject(Router);

  readonly route = MATCHING_ROUTE;
  readonly settingsRoute = AP_SETTINGS_ROUTE;
  readonly filters = MATCH_FILTERS;

  rows: MatchSummary[] = [];
  total = 0;
  pageSize = 25;
  result: string | null = null;
  search = '';
  loading = false;
  loadError = '';
  /** The table has done its first lazy load (which reads page 1). */
  private ready = false;

  onLazy(e: TableLazyLoadEvent): void {
    this.ready = true;
    this.pageSize = e.rows ?? this.pageSize;
    this.load(Math.floor((e.first ?? 0) / this.pageSize) + 1);
  }

  setResult(r: string | null): void { this.result = r; if (this.ready) this.load(1); }

  private seq = 0;

  load(page: number): void {
    const seq = ++this.seq;
    this.loading = true;
    this.loadError = '';
    this.api.getPendingMatches({ result: this.result, search: this.search, page, pageSize: this.pageSize }).subscribe({
      next: r => { if (seq !== this.seq) return; this.loading = false; this.rows = r.data; this.total = r.totalRecords; },
      error: err => { if (seq !== this.seq) return; this.loading = false; this.rows = []; this.total = 0; this.loadError = arApError(err, 'The matches could not be loaded.'); }
    });
  }

  open(m: MatchSummary): void { this.router.navigate([MATCHING_ROUTE, m.invoiceUuid]); }

  state(m: MatchSummary): string { return matchState(m); }
  tone(s: string | null | undefined): string { return matchTone(s); }
  label(s: string | null | undefined): string { return matchLabel(s); }
  signed(n: number | null | undefined): string { return signedMoney(n); }
  amount(n: number | null | undefined): string { return money(n); }
}
