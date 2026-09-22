import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TableModule, TableLazyLoadEvent } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';
import { ToastModule } from 'primeng/toast';
import { CalendarModule } from 'primeng/calendar';
import { AutoCompleteModule, AutoCompleteCompleteEvent } from 'primeng/autocomplete';
import { MessageService } from 'primeng/api';
import { Subscription } from 'rxjs';

import { CustomerLedgerService, CustomerLedgerEntryModel } from '../../../services/customer-ledger.service';
import { BusinessPartnerService, BusinessPartnerModel } from '../../../services/business-partner.service';
import { AuthService } from '../../service/auth.service';
import { formatCode } from '../../../shared/format-code';
import { toDateOnly } from '../../../shared/date-only';
import { LEDGER_ENTRY_SEVERITY, Severity } from '../receivables/receivables.shared';

@Component({
  selector: 'app-customer-ledger',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    TableModule, ButtonModule, TagModule, TooltipModule, ToastModule, CalendarModule, AutoCompleteModule
  ],
  templateUrl: './customer-ledger.component.html',
  styleUrls: ['./customer-ledger.component.scss'],
  providers: [MessageService]
})
export class CustomerLedgerComponent implements OnInit, OnDestroy {
  /** The customer whose ledger is showing: the one in the address. */
  partnerId: string | null = null;

  /** What the customer box holds: the partner once picked, or the text being typed. */
  customer: BusinessPartnerModel | string | null = null;
  customerSuggestions: BusinessPartnerModel[] = [];

  entries: CustomerLedgerEntryModel[] = [];
  totalRecords = 0;
  currentPage  = 1;
  pageSize     = 20;
  isLoading    = false;

  dateFrom: Date | null = null;
  dateTo: Date | null = null;

  /** What the customer owed after the newest entry in the range, from the first page. Null with no entries to read it from. */
  balance: number | null = null;
  balanceAsOf: string | null = null;
  balanceCurrency: string | null = null;

  private params?: Subscription;

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private ledgerService: CustomerLedgerService,
    private partnerService: BusinessPartnerService,
    public authService: AuthService,
    private messageService: MessageService
  ) {}

  ngOnInit() {
    // The route is reused when one customer's ledger gives way to another's, so follow the address, not a snapshot of it.
    this.params = this.route.paramMap.subscribe(params => this.showCustomer(params.get('partnerId')));
  }

  ngOnDestroy() {
    this.params?.unsubscribe();
  }

  get canRecordPayment(): boolean { return this.authService.hasPermission('CUSTOMER_PAYMENT_RECORD'); }

  // ── Which customer ──────────────────────────────────────────────────────────

  private showCustomer(partnerId: string | null) {
    this.partnerId = partnerId;
    this.entries = [];
    this.totalRecords = 0;
    this.balance = null;
    this.balanceAsOf = null;
    this.balanceCurrency = null;
    this.currentPage = 1;

    if (!partnerId) { this.customer = null; return; }

    // Only fetched when the box does not already hold this customer, which it does after the user picked them.
    if (!(this.customer && typeof this.customer === 'object' && this.customer.uuid === partnerId)) {
      this.partnerService.getPartnerById(partnerId).subscribe({
        next: (res) => { if (this.partnerId === partnerId && res.result) this.customer = res.result; },
        error: () => { /* the ledger renders without the name */ }
      });
    }

    this.load();
  }

  searchCustomers(event: AutoCompleteCompleteEvent) {
    this.partnerService.getPartners({ isCustomer: true, active: true, search: event.query, pageSize: 20 }).subscribe({
      next: (res) => { this.customerSuggestions = res.result?.data ?? []; },
      error: () => { this.customerSuggestions = []; }
    });
  }

  onCustomerSelected(partner: BusinessPartnerModel) {
    if (partner?.uuid) this.router.navigate(['/portal/pages/finance/customer-ledger', partner.uuid]);
  }

  // ── The ledger ──────────────────────────────────────────────────────────────

  load() {
    if (!this.partnerId) return;
    const partnerId = this.partnerId;
    this.isLoading = true;

    this.ledgerService.getLedger(partnerId, {
      dateFrom: this.dateFrom ? toDateOnly(this.dateFrom) : undefined,
      dateTo: this.dateTo ? toDateOnly(this.dateTo) : undefined,
      page: this.currentPage,
      pageSize: this.pageSize
    }).subscribe({
      next: (res) => {
        if (this.partnerId !== partnerId) return;
        this.isLoading = false;
        this.entries      = res.success && res.result ? res.result.data ?? [] : [];
        this.totalRecords = res.success && res.result ? res.result.totalRecords ?? 0 : 0;

        // Newest first, so the top of the first page is where the balance stands.
        if (this.currentPage === 1) {
          const latest = this.entries[0];
          this.balance = latest ? latest.runningBalance : null;
          this.balanceAsOf = latest ? latest.entryDate : null;
          this.balanceCurrency = latest ? latest.currencyCode : null;
        }
      },
      error: (err) => {
        if (this.partnerId !== partnerId) return;
        this.isLoading = false;
        this.entries = [];
        this.totalRecords = 0;
        this.balance = null;
        this.messageService.add({
          severity: 'error', summary: 'Error', detail: err?.error?.message ?? "The customer's ledger could not be loaded."
        });
      }
    });
  }

  onFilterChange() { this.currentPage = 1; this.load(); }

  onPageChange(event: TableLazyLoadEvent) {
    this.currentPage = Math.floor((event.first ?? 0) / (event.rows ?? this.pageSize)) + 1;
    this.pageSize    = event.rows ?? this.pageSize;
    this.load();
  }

  resetFilters() {
    this.dateFrom = null;
    this.dateTo = null;
    this.onFilterChange();
  }

  // ── Display ─────────────────────────────────────────────────────────────────

  /** The ledger keeps one running balance per customer and adds entries in different currencies together, unconverted. */
  get mixedCurrencies(): boolean {
    return new Set(this.entries.map(e => e.currencyCode)).size > 1;
  }

  /** The balance without its sign: the label says whether it is owed or in credit. */
  get absBalance(): number { return Math.abs(this.balance ?? 0); }

  /** Positive is what the customer owes; negative is what they have paid ahead. */
  get balanceLabel(): string {
    if (this.balance === null) return '';
    if (this.balance > 0) return 'owed by the customer';
    if (this.balance < 0) return 'in credit';
    return 'settled';
  }

  /** The document behind an entry, when there is a page for it and this user may open it. */
  referenceLink(entry: CustomerLedgerEntryModel): string[] | null {
    if (!entry.referenceId) return null;
    if (entry.referenceType === 'SalesInvoice' && this.authService.hasPermission('SALES_INVOICE_VIEW')) {
      return ['/portal/pages/finance/sales-invoices', entry.referenceId];
    }
    if (entry.referenceType === 'CustomerPayment' && this.authService.hasPermission('CUSTOMER_PAYMENT_VIEW')) {
      return ['/portal/pages/finance/customer-payments', entry.referenceId];
    }
    return null;
  }

  getEntrySeverity(entryType: string): Severity {
    return LEDGER_ENTRY_SEVERITY[entryType] ?? 'secondary';
  }

  formatStatus(code?: string | null): string { return formatCode(code); }
}
