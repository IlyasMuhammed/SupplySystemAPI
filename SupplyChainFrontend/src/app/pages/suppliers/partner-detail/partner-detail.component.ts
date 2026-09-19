import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { CardModule } from 'primeng/card';
import { TagModule } from 'primeng/tag';
import { TabViewModule } from 'primeng/tabview';
import { TableModule } from 'primeng/table';
import { CalendarModule } from 'primeng/calendar';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';
import { BusinessPartnerService, BusinessPartnerModel } from '../../../services/business-partner.service';
import {
  FinanceService,
  SupplierLedgerEntryModel,
  SupplierBalanceSummary
} from '../../../services/finance.service';

// Addendum 29 §1.6 — "Partner detail page with ledger tab." The ledger tab reuses
// FinanceService.getSupplierLedger/getSupplierBalance verbatim: a BusinessPartner's UUID is the
// same UUID the ledger has always been keyed on (P1-01 renamed the table, not the identity), so
// no backend change was needed to make this work for a partner opened through the new surface.
@Component({
  selector: 'app-partner-detail',
  standalone: true,
  imports: [
    CommonModule, FormsModule, RouterModule,
    ButtonModule, CardModule, TagModule, TabViewModule, TableModule, CalendarModule, ToastModule
  ],
  templateUrl: './partner-detail.component.html',
  styleUrls: ['./partner-detail.component.scss'],
  providers: [MessageService]
})
export class PartnerDetailComponent implements OnInit {
  uuid!: string;
  partner: Partial<BusinessPartnerModel> = {};
  isLoading = true;

  // ── Ledger tab ────────────────────────────────────────────────────────────
  ledgerEntries: SupplierLedgerEntryModel[] = [];
  ledgerBalance: SupplierBalanceSummary | null = null;
  isLoadingLedger = false;
  ledgerDateFrom: Date | null = null;
  ledgerDateTo: Date | null = null;
  ledgerPage = 1;
  ledgerPageSize = 20;
  ledgerTotalRecords = 0;

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private partnerService: BusinessPartnerService,
    private financeService: FinanceService,
    private messageService: MessageService
  ) {}

  ngOnInit() {
    this.uuid = this.route.snapshot.paramMap.get('uuid') ?? '';
    if (!this.uuid) { this.router.navigate(['/portal/pages/suppliers/partner-list']); return; }
    this.loadPartner();
  }

  loadPartner() {
    this.isLoading = true;
    this.partnerService.getPartnerById(this.uuid).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success && res.result) {
          this.partner = res.result;
          this.loadLedger();
          this.loadLedgerBalance();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Business partner not found.' });
        }
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load business partner.' });
      }
    });
  }

  goBack() {
    this.router.navigate(['/portal/pages/suppliers/partner-list']);
  }

  capabilityTags(): { label: string; severity: 'success' | 'info' | 'warn' | 'help' }[] {
    const tags: { label: string; severity: 'success' | 'info' | 'warn' | 'help' }[] = [];
    if (this.partner.isVendor)          tags.push({ label: 'Vendor',           severity: 'success' });
    if (this.partner.isCustomer)        tags.push({ label: 'Customer',         severity: 'info' });
    if (this.partner.isCarrier)         tags.push({ label: 'Carrier',          severity: 'warn' });
    if (this.partner.isServiceProvider) tags.push({ label: 'Service Provider', severity: 'help' });
    return tags;
  }

  // ── Ledger tab ────────────────────────────────────────────────────────────

  loadLedger(page: number = 1) {
    this.isLoadingLedger = true;
    this.ledgerPage = page;
    this.financeService.getSupplierLedger(this.uuid, {
      dateFrom: this.ledgerDateFrom ? this.toIsoDate(this.ledgerDateFrom) : undefined,
      dateTo:   this.ledgerDateTo   ? this.toIsoDate(this.ledgerDateTo)   : undefined,
      page:     this.ledgerPage,
      pageSize: this.ledgerPageSize
    }).subscribe({
      next: (res) => {
        this.isLoadingLedger = false;
        if (res.success && res.result) {
          this.ledgerEntries = res.result.data;
          this.ledgerTotalRecords = res.result.totalRecords;
        }
      },
      error: () => { this.isLoadingLedger = false; }
    });
  }

  loadLedgerBalance() {
    this.financeService.getSupplierBalance(this.uuid).subscribe({
      next: (res) => { this.ledgerBalance = res.success ? res.result : null; },
      error: () => { this.ledgerBalance = null; }
    });
  }

  onLedgerPageChange(event: { first: number; rows: number }) {
    this.ledgerPageSize = event.rows;
    this.loadLedger(Math.floor(event.first / event.rows) + 1);
  }

  onLedgerFilterChange() {
    this.loadLedger(1);
  }

  private toIsoDate(d: Date): string {
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
  }

  getLedgerTypeSeverity(type: string): 'success' | 'danger' | 'warn' | 'info' | 'secondary' {
    switch (type) {
      case 'INVOICE_APPROVED':    return 'danger';
      case 'CREDIT_NOTE_APPROVED':
      case 'PAYMENT_POSTED':      return 'success';
      case 'DEBIT_NOTE_APPROVED': return 'danger';
      case 'PAYMENT_BOUNCED':     return 'warn';
      default:                    return 'secondary';
    }
  }

  getLedgerTypeLabel(type: string): string {
    return (type || '')
      .toLowerCase()
      .split('_')
      .map(w => w.charAt(0).toUpperCase() + w.slice(1))
      .join(' ');
  }
}
