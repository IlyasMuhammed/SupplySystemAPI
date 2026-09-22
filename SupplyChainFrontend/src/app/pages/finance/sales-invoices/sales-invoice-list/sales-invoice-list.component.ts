import { Component, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router, RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TableModule, TableLazyLoadEvent } from 'primeng/table';
import { InputTextModule } from 'primeng/inputtext';
import { InputIconModule } from 'primeng/inputicon';
import { IconFieldModule } from 'primeng/iconfield';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';
import { ToastModule } from 'primeng/toast';
import { DropdownModule } from 'primeng/dropdown';
import { CalendarModule } from 'primeng/calendar';
import { DialogModule } from 'primeng/dialog';
import { AutoCompleteModule, AutoCompleteCompleteEvent } from 'primeng/autocomplete';
import { MessageService } from 'primeng/api';

import { SalesInvoiceService, SalesInvoiceListItemModel, SalesInvoiceFilter } from '../../../../services/sales-invoice.service';
import { LogisticsService } from '../../../../services/logistics.service';
import { AuthService } from '../../../service/auth.service';
import { formatCode } from '../../../../shared/format-code';
import { toDateOnly } from '../../../../shared/date-only';
import { INVOICE_STATUS_OPTIONS, INVOICE_STATUS_SEVERITY, Severity } from '../../receivables/receivables.shared';
import { SalesInvoicePdfDialogComponent } from '../sales-invoice-pdf-dialog/sales-invoice-pdf-dialog.component';

/** A delivery that has reached the customer, as the "new invoice" box offers it. */
export interface DeliveryChoice {
  uuid: string;
  label: string;
}

@Component({
  selector: 'app-sales-invoice-list',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    TableModule, ButtonModule, InputTextModule, InputIconModule, IconFieldModule,
    TagModule, TooltipModule, ToastModule, DropdownModule, CalendarModule, DialogModule, AutoCompleteModule,
    SalesInvoicePdfDialogComponent
  ],
  templateUrl: './sales-invoice-list.component.html',
  styleUrls: ['./sales-invoice-list.component.scss'],
  providers: [MessageService]
})
export class SalesInvoiceListComponent implements OnDestroy {
  invoices: SalesInvoiceListItemModel[] = [];
  totalRecords = 0;
  currentPage  = 1;
  pageSize     = 20;
  isLoading    = true;

  searchText     = '';
  selectedStatus = '';
  dateFrom: Date | null = null;
  dateTo: Date | null = null;

  statusOptions = INVOICE_STATUS_OPTIONS;

  // The invoice whose PDF is open.
  pdfVisible = false;
  pdfInvoice: SalesInvoiceListItemModel | null = null;

  // The "new invoice" dialog.
  newDialogVisible = false;
  deliverySearch: DeliveryChoice | string | null = null;
  deliverySuggestions: DeliveryChoice[] = [];
  isCreating = false;

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  constructor(
    private invoiceService: SalesInvoiceService,
    private logisticsService: LogisticsService,
    private router: Router,
    public authService: AuthService,
    private messageService: MessageService
  ) {}

  /** Raising an invoice starts from a delivery, so it needs the right to find one as well as to invoice. */
  get canCreate(): boolean {
    return this.authService.hasPermission('SALES_INVOICE_MANAGE') && this.authService.hasPermission('DELIVERY_VIEW');
  }

  ngOnDestroy() {
    if (this.searchTimer) clearTimeout(this.searchTimer);
  }

  load() {
    this.isLoading = true;

    const filter: SalesInvoiceFilter = {
      page: this.currentPage,
      pageSize: this.pageSize,
      search: this.searchText || undefined,
      status: this.selectedStatus || undefined,
      dateFrom: this.dateFrom ? toDateOnly(this.dateFrom) : undefined,
      dateTo: this.dateTo ? toDateOnly(this.dateTo) : undefined
    };

    this.invoiceService.getInvoices(filter).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success && res.result) {
          this.invoices     = res.result.data ?? [];
          this.totalRecords = res.result.totalRecords ?? 0;
        } else {
          this.invoices = [];
          this.totalRecords = 0;
        }
      },
      error: (err) => {
        this.isLoading = false;
        this.invoices = [];
        this.totalRecords = 0;
        this.messageService.add({
          severity: 'error', summary: 'Error', detail: err?.error?.message ?? 'Failed to load sales invoices.'
        });
      }
    });
  }

  onSearchChange() {
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => { this.currentPage = 1; this.load(); }, 400);
  }

  onFilterChange() { this.currentPage = 1; this.load(); }

  onPageChange(event: TableLazyLoadEvent) {
    this.currentPage = Math.floor((event.first ?? 0) / (event.rows ?? this.pageSize)) + 1;
    this.pageSize    = event.rows ?? this.pageSize;
    this.load();
  }

  resetFilters() {
    this.searchText = '';
    this.selectedStatus = '';
    this.dateFrom = null;
    this.dateTo = null;
    this.currentPage = 1;
    this.load();
  }

  openPdf(invoice: SalesInvoiceListItemModel) {
    this.pdfInvoice = invoice;
    this.pdfVisible = true;
  }

  // ── New invoice from a delivery ─────────────────────────────────────────────

  openNewDialog() {
    if (!this.canCreate) return;
    this.deliverySearch = null;
    this.deliverySuggestions = [];
    this.newDialogVisible = true;
  }

  /** Sale-order deliveries that have reached the customer: the only ones an invoice can be raised for. */
  searchDeliveries(event: AutoCompleteCompleteEvent) {
    this.logisticsService.getDeliveries({
      status: 'DELIVERED', direction: 'OUTBOUND', sourceType: 'SALE_ORDER', search: event.query, pageSize: 20
    }).subscribe({
      next: (res) => {
        this.deliverySuggestions = (res.result?.data ?? []).map(d => ({
          uuid: d.uuid,
          label: d.sourceNumber ? `${d.deliveryNumber} · ${d.sourceNumber}` : d.deliveryNumber
        }));
      },
      error: () => { this.deliverySuggestions = []; }
    });
  }

  /** The delivery picked from the list; text typed into the box is not a choice. */
  get chosenDelivery(): DeliveryChoice | null {
    const v = this.deliverySearch;
    return v && typeof v === 'object' && !!v.uuid ? v : null;
  }

  createInvoice() {
    const delivery = this.chosenDelivery;
    if (!delivery || this.isCreating || !this.canCreate) return;
    this.isCreating = true;

    this.invoiceService.createFromDelivery(delivery.uuid).subscribe({
      next: (res) => {
        this.isCreating = false;
        this.newDialogVisible = false;
        const created = res.result;
        this.messageService.add({
          // Asking again for a delivery that has an invoice hands back that invoice: worth saying, not an error.
          severity: created?.alreadyExisted ? 'info' : 'success',
          summary: created?.alreadyExisted ? 'Already invoiced' : 'Invoice created',
          detail: res.message || (created ? `Draft invoice ${created.invoiceNumber} raised.` : 'Draft invoice raised.')
        });
        if (created?.invoiceUuid) this.router.navigate(['/portal/pages/finance/sales-invoices', created.invoiceUuid]);
        else this.load();
      },
      error: (err) => {
        this.isCreating = false;
        this.messageService.add({
          severity: 'error', summary: 'Not created',
          detail: err?.error?.message ?? 'The invoice could not be created.'
        });
      }
    });
  }

  // ── Display ─────────────────────────────────────────────────────────────────

  getStatusSeverity(status: string): Severity {
    return INVOICE_STATUS_SEVERITY[status] ?? 'secondary';
  }

  formatStatus(code?: string | null): string { return formatCode(code); }
}
