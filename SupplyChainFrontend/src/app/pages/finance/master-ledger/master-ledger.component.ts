import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router, RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { TableModule, TableLazyLoadEvent } from 'primeng/table';
import { ButtonModule } from 'primeng/button';
import { CalendarModule } from 'primeng/calendar';
import { AutoCompleteModule } from 'primeng/autocomplete';
import { MultiSelectModule } from 'primeng/multiselect';
import { InputNumberModule } from 'primeng/inputnumber';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';
import {
  MasterLedgerService, MasterLedgerEntryModel, MasterLedgerFilter, MasterLedgerSummaryModel
} from '../../../services/master-ledger.service';
import { SupplierService, SupplierListItemModel } from '../../../services/supplier.service';

const TRANSACTION_TYPE_OPTIONS = [
  { label: 'Invoice Approved',     value: 'INVOICE_APPROVED' },
  { label: 'Payment',              value: 'PAYMENT' },
  { label: 'Credit Note',          value: 'CREDIT_NOTE' },
  { label: 'Debit Note',           value: 'DEBIT_NOTE' },
  { label: 'Advance Payment',      value: 'ADVANCE_PAYMENT' },
  { label: 'Advance Adjustment',   value: 'ADVANCE_ADJUSTMENT' },
  { label: 'Retention Hold',       value: 'RETENTION_HOLD' },
  { label: 'Retention Release',    value: 'RETENTION_RELEASE' },
  { label: 'Cheque Bounce Reversal', value: 'CHEQUE_BOUNCE_REVERSAL' },
  { label: 'Bad Debt Write-off',   value: 'BAD_DEBT_WRITEOFF' },
  { label: 'Opening Balance',      value: 'OPENING_BALANCE' },
];

// Reference types with a real detail page today — everything else renders as plain text.
const REFERENCE_ROUTES: Record<string, string> = {
  Invoice:         '/portal/pages/finance/invoices',
  SupplierPayment: '/portal/pages/finance/payments',
};

@Component({
  selector: 'app-master-ledger',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    TableModule, ButtonModule, CalendarModule, AutoCompleteModule,
    MultiSelectModule, InputNumberModule, TagModule, TooltipModule, ToastModule
  ],
  templateUrl: './master-ledger.component.html',
  styleUrls: ['./master-ledger.component.scss'],
  providers: [MessageService]
})
export class MasterLedgerComponent implements OnInit {
  entries: MasterLedgerEntryModel[] = [];
  summary: MasterLedgerSummaryModel | null = null;
  isLoading = true;
  isLoadingSummary = false;
  isExportingPdf = false;
  isExportingExcel = false;

  totalRecords = 0;
  page = 1;
  pageSize = 20;

  // ── Filters ──────────────────────────────────────────────────────────────
  dateFrom: Date | null = null;
  dateTo: Date | null = null;
  minAmount: number | null = null;
  selectedTransactionTypes: string[] = [];
  transactionTypeOptions = TRANSACTION_TYPE_OPTIONS;

  selectedSupplier: SupplierListItemModel | null = null;
  supplierSuggestions: SupplierListItemModel[] = [];

  constructor(
    private ledgerService: MasterLedgerService,
    private supplierService: SupplierService,
    private router: Router,
    private messageService: MessageService
  ) {}

  ngOnInit() {
    this.load();
    this.loadSummary();
  }

  private buildFilter(): MasterLedgerFilter {
    return {
      dateFrom: this.dateFrom ? this.toIsoDate(this.dateFrom) : undefined,
      dateTo:   this.dateTo   ? this.toIsoDate(this.dateTo)   : undefined,
      supplierId: this.selectedSupplier?.uuid,
      transactionTypes: this.selectedTransactionTypes.length ? this.selectedTransactionTypes : undefined,
      minAmount: this.minAmount ?? undefined,
      page: this.page,
      pageSize: this.pageSize
    };
  }

  private toIsoDate(d: Date): string {
    return new Date(Date.UTC(d.getFullYear(), d.getMonth(), d.getDate())).toISOString();
  }

  load() {
    this.isLoading = true;
    this.ledgerService.getLedger(this.buildFilter()).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success) {
          this.entries = res.result.data;
          this.totalRecords = res.result.totalRecords;
        }
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load master ledger.' });
      }
    });
  }

  loadSummary() {
    this.isLoadingSummary = true;
    this.ledgerService.getSummary(this.buildFilter()).subscribe({
      next: (res) => {
        this.isLoadingSummary = false;
        this.summary = res.success ? res.result : null;
      },
      error: () => { this.isLoadingSummary = false; this.summary = null; }
    });
  }

  onPageChange(event: TableLazyLoadEvent) {
    const rows = event.rows || this.pageSize;
    this.pageSize = rows;
    this.page = Math.floor((event.first || 0) / rows) + 1;
    this.load();
  }

  applyFilters() {
    this.page = 1;
    this.load();
    this.loadSummary();
  }

  resetFilters() {
    this.dateFrom = null;
    this.dateTo = null;
    this.minAmount = null;
    this.selectedTransactionTypes = [];
    this.selectedSupplier = null;
    this.page = 1;
    this.load();
    this.loadSummary();
  }

  searchSuppliers(event: any) {
    const q = (event.query as string ?? '').trim();
    this.supplierService.getSuppliers({ search: q || undefined, pageSize: 20 }).subscribe({
      next: (res) => { this.supplierSuggestions = res.success ? res.result.data : []; },
      error: () => { this.supplierSuggestions = []; }
    });
  }

  // ── Per-supplier ledger toggle ────────────────────────────────────────────

  get canViewPerSupplierLedger(): boolean {
    return !!this.selectedSupplier;
  }

  viewPerSupplierLedger() {
    if (!this.selectedSupplier) return;
    this.router.navigate(['/portal/pages/suppliers', this.selectedSupplier.uuid]);
  }

  // ── Drill-down ─────────────────────────────────────────────────────────────

  canDrillDown(e: MasterLedgerEntryModel): boolean {
    return !!REFERENCE_ROUTES[e.referenceType];
  }

  drillDown(e: MasterLedgerEntryModel) {
    const base = REFERENCE_ROUTES[e.referenceType];
    if (base) this.router.navigate([base, e.referenceId]);
  }

  // ── Export ───────────────────────────────────────────────────────────────

  exportPdf() {
    this.isExportingPdf = true;
    this.ledgerService.exportPdf(this.buildFilter()).subscribe({
      next: (blob) => { this.isExportingPdf = false; this.downloadBlob(blob, 'master-payables-ledger.pdf'); },
      error: () => {
        this.isExportingPdf = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'PDF export failed.' });
      }
    });
  }

  exportExcel() {
    this.isExportingExcel = true;
    this.ledgerService.exportExcel(this.buildFilter()).subscribe({
      next: (blob) => { this.isExportingExcel = false; this.downloadBlob(blob, 'master-payables-ledger.xlsx'); },
      error: () => {
        this.isExportingExcel = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Excel export failed.' });
      }
    });
  }

  private downloadBlob(blob: Blob, fileName: string) {
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    a.click();
    URL.revokeObjectURL(url);
  }

  getTypeSeverity(t: string): 'success' | 'danger' | 'warn' | 'secondary' | 'info' {
    switch (t) {
      case 'INVOICE_APPROVED':
      case 'ADVANCE_ADJUSTMENT':
      case 'RETENTION_RELEASE':
      case 'CHEQUE_BOUNCE_REVERSAL':
      case 'OPENING_BALANCE':
        return 'danger'; // debit — increases what's owed
      case 'PAYMENT':
      case 'CREDIT_NOTE':
      case 'DEBIT_NOTE':
      case 'ADVANCE_PAYMENT':
      case 'RETENTION_HOLD':
      case 'BAD_DEBT_WRITEOFF':
        return 'success'; // credit — reduces what's owed
      default:
        return 'secondary';
    }
  }

  getTypeLabel(t: string): string {
    return this.transactionTypeOptions.find(o => o.value === t)?.label ?? t;
  }
}
