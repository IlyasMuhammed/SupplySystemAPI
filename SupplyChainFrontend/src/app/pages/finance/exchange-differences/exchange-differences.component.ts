import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { TableModule, TableLazyLoadEvent } from 'primeng/table';
import { DropdownModule } from 'primeng/dropdown';
import { CalendarModule } from 'primeng/calendar';
import { DialogModule } from 'primeng/dialog';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';

import {
  EXCHANGE_REVALUATION_RUN, ExchangeDifferenceKind, ExchangeDifferenceModel, ExchangeDifferenceService, ExchangeDifferenceSide,
  RevaluationRunResultModel
} from '../../../services/exchange-difference.service';
import { AuthService } from '../../service/auth.service';
import { MoneyPipe } from '../../../shared/money/money.pipe';
import { formatRate } from '../../../shared/doc-currency/doc-currency';
import { fromDateOnly, toDateOnly } from '../../../shared/date-only';
import { FLOW } from '../../../shared/flow';

const PAGE_SIZE = 50;

/**
 * A35-E-06 / D-15 — the exchange-difference register: realized gains and losses written when a payment settles a
 * foreign-currency invoice at another rate, and unrealized ones from the revaluation of open documents. "Run revaluation"
 * (EXCHANGE_REVALUATION_RUN) revalues as of a date; re-running a date replaces that date's rows.
 * Contract: docs/multi-currency/API-CONTRACT.md §7.1–§7.2.
 */
@Component({
  selector: 'app-exchange-differences',
  standalone: true,
  imports: [
    CommonModule, FormsModule, RouterModule, ButtonModule, TableModule, DropdownModule, CalendarModule, DialogModule,
    TagModule, TooltipModule, MoneyPipe,
    ...FLOW
  ],
  templateUrl: './exchange-differences.component.html',
  styleUrls: ['./exchange-differences.component.scss']
})
export class ExchangeDifferencesComponent implements OnInit {
  private readonly service = inject(ExchangeDifferenceService);
  readonly authService = inject(AuthService);

  rows: ExchangeDifferenceModel[] = [];
  totalRecords = 0;
  page = 1;
  readonly pageSize = PAGE_SIZE;
  isLoading = false;
  loadError: string | null = null;

  filterKind: ExchangeDifferenceKind | null = null;
  filterSide: ExchangeDifferenceSide | null = null;
  filterFrom: Date | null = null;
  filterTo: Date | null = null;

  readonly kindOptions = [
    { label: 'Realized (payments)', value: 'REALIZED' },
    { label: 'Unrealized (revaluation)', value: 'UNREALIZED' }
  ];
  readonly sideOptions = [
    { label: 'Receivables', value: 'RECEIVABLE' },
    { label: 'Payables', value: 'PAYABLE' }
  ];
  readonly moneyCode = { display: 'code' } as const;
  readonly formatRate = formatRate;

  runVisible = false;
  runDate: Date | null = null;
  isRunning = false;
  runResult: RevaluationRunResultModel | null = null;
  runError: string | null = null;

  get canRun(): boolean {
    return this.authService.hasPermission(EXCHANGE_REVALUATION_RUN);
  }

  ngOnInit() {
    this.load();
  }

  load() {
    this.isLoading = true;
    this.loadError = null;
    this.service.getDifferences({
      kind: this.filterKind, side: this.filterSide,
      from: this.filterFrom ? toDateOnly(this.filterFrom) : null,
      to: this.filterTo ? toDateOnly(this.filterTo) : null,
      page: this.page, pageSize: this.pageSize
    }).subscribe({
      next: res => {
        this.isLoading = false;
        this.rows = res?.result?.data ?? [];
        this.totalRecords = res?.result?.totalRecords ?? 0;
      },
      error: err => {
        this.isLoading = false;
        this.rows = [];
        this.totalRecords = 0;
        this.loadError = err?.error?.message ?? 'The exchange differences could not be loaded.';
      }
    });
  }

  applyFilters() {
    this.page = 1;
    this.load();
  }

  clearFilters() {
    this.filterKind = null;
    this.filterSide = null;
    this.filterFrom = null;
    this.filterTo = null;
    this.applyFilters();
  }

  onPage(event: TableLazyLoadEvent) {
    const next = Math.floor((event.first ?? 0) / this.pageSize) + 1;
    if (next === this.page) return;
    this.page = next;
    this.load();
  }

  /** Where the source document opens. */
  documentLink(d: ExchangeDifferenceModel): string[] | null {
    if (!d.documentUuid) return null;
    if (d.documentType === 'SALES_INVOICE') return ['/portal/pages/finance/sales-invoices', d.documentUuid];
    if (d.documentType === 'SUPPLIER_INVOICE') return ['/portal/pages/finance/invoices', d.documentUuid];
    return null;
  }

  paymentLink(d: ExchangeDifferenceModel): string[] | null {
    if (!d.paymentUuid) return null;
    if (d.paymentType === 'CUSTOMER_PAYMENT') return ['/portal/pages/finance/customer-payments', d.paymentUuid];
    if (d.paymentType === 'SUPPLIER_PAYMENT') return ['/portal/pages/finance/payments', d.paymentUuid];
    return null;
  }

  /** Realized rows by posting time; unrealized by revaluation date (date-only, no UTC shift). */
  rowDate(d: ExchangeDifferenceModel): Date | string | null {
    if (d.revaluationDate) return fromDateOnly(d.revaluationDate);
    return d.postedAt ?? null;
  }

  // ── Run revaluation ──────────────────────────────────────────────────────

  openRun() {
    this.runVisible = true;
    this.runDate = this.runDate ?? new Date();
    this.runResult = null;
    this.runError = null;
  }

  run() {
    if (this.isRunning || !this.canRun) return;
    this.isRunning = true;
    this.runError = null;
    this.runResult = null;
    this.service.runRevaluation(this.runDate ? toDateOnly(this.runDate) : null).subscribe({
      next: res => {
        this.isRunning = false;
        this.runResult = res?.result ?? null;
        this.load();
      },
      error: err => {
        this.isRunning = false;
        this.runError = err?.error?.message ?? 'The revaluation could not be run.';
      }
    });
  }
}
