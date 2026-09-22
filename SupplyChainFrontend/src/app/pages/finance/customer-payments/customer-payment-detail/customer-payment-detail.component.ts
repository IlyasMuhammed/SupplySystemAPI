import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { TableModule } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';

import {
  CustomerPaymentService, CustomerPaymentDetailModel, CustomerPaymentAllocatedModel, ManualPaymentAllocation
} from '../../../../services/customer-payment.service';
import { SalesInvoiceService, SalesInvoiceListItemModel } from '../../../../services/sales-invoice.service';
import { AuthService } from '../../../service/auth.service';
import { formatCode } from '../../../../shared/format-code';
import { INVOICE_STATUS_SEVERITY, PAYMENT_STATUS_SEVERITY, Severity } from '../../receivables/receivables.shared';
import { PaymentAllocationEditorComponent } from '../payment-allocation-editor/payment-allocation-editor.component';
import { AllocationAmounts, allocationProblem, toAllocations } from '../payment-allocation-editor/payment-allocation';

@Component({
  selector: 'app-customer-payment-detail',
  standalone: true,
  imports: [
    CommonModule, RouterModule,
    TableModule, ButtonModule, TagModule, TooltipModule, ToastModule,
    PaymentAllocationEditorComponent
  ],
  templateUrl: './customer-payment-detail.component.html',
  styleUrls: ['./customer-payment-detail.component.scss'],
  providers: [MessageService]
})
export class CustomerPaymentDetailComponent implements OnInit {
  uuid = '';
  payment: CustomerPaymentDetailModel | null = null;
  isLoading = true;
  notFound = false;

  // What is left of the payment, and the invoices it could still pay.
  openInvoices: SalesInvoiceListItemModel[] = [];
  invoicesFailed = false;
  isLoadingInvoices = false;
  amounts: AllocationAmounts = {};
  isApplying = false;

  constructor(
    private route: ActivatedRoute,
    private paymentService: CustomerPaymentService,
    private invoiceService: SalesInvoiceService,
    public authService: AuthService,
    private messageService: MessageService
  ) {}

  ngOnInit() {
    this.uuid = this.route.snapshot.paramMap.get('uuid') ?? '';
    this.load();
  }

  load() {
    if (!this.uuid) { this.isLoading = false; this.notFound = true; return; }

    this.isLoading = true;

    this.paymentService.getPayment(this.uuid).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success && res.result) {
          this.payment = res.result;
          this.notFound = false;
          this.amounts = {};
          if (this.canApply) this.loadOpenInvoices(res.result);
          else this.openInvoices = [];
        } else {
          this.payment = null;
          this.notFound = true;
        }
      },
      error: (err) => {
        this.isLoading = false;
        this.payment = null;
        this.notFound = err?.status === 404;
        if (!this.notFound) {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load the payment.' });
        }
      }
    });
  }

  // ── What this user may do, and what the payment allows ──────────────────────

  private can(code: string): boolean { return this.authService.hasPermission(code); }

  /** Money received and not yet applied to an invoice: the only payment there is anything to apply. */
  get canApply(): boolean {
    return this.payment?.status === 'RECEIVED'
        && this.payment.unallocatedAmount > 0
        && this.can('CUSTOMER_PAYMENT_RECORD');
  }

  /** Choosing invoices means being able to see them; oldest-first needs no list. */
  get canChooseInvoices(): boolean { return this.can('SALES_INVOICE_VIEW'); }

  get canViewInvoices(): boolean { return this.can('SALES_INVOICE_VIEW'); }
  get canViewLedger(): boolean   { return this.can('CUSTOMER_LEDGER_VIEW'); }

  /** A bounced or reversed payment keeps its allocation rows as history: none of it is applied any more. */
  get isHistoryOnly(): boolean {
    return !!this.payment && this.payment.status !== 'RECEIVED';
  }

  // ── Apply what is left ──────────────────────────────────────────────────────

  private loadOpenInvoices(payment: CustomerPaymentDetailModel) {
    this.openInvoices = [];
    this.invoicesFailed = false;
    if (!this.canChooseInvoices) return;

    this.isLoadingInvoices = true;
    this.invoiceService.getOpenInvoices(payment.partnerId, payment.currencyCode).subscribe({
      next: (open) => {
        this.isLoadingInvoices = false;
        this.openInvoices = open.invoices;
      },
      error: () => {
        this.isLoadingInvoices = false;
        this.invoicesFailed = true;
      }
    });
  }

  get allocations(): ManualPaymentAllocation[] {
    return toAllocations(this.openInvoices, this.amounts);
  }

  get problem(): string | null {
    return allocationProblem(this.openInvoices, this.amounts, this.payment?.unallocatedAmount ?? 0);
  }

  get canApplyChosen(): boolean {
    return this.canApply && this.allocations.length > 0 && !this.problem && !this.isApplying;
  }

  /** Oldest invoice first, decided by the server. */
  applyOldestFirst() {
    if (!this.canApply || this.isApplying) return;
    this.apply(undefined);
  }

  applyChosen() {
    if (!this.canApplyChosen) return;
    this.apply(this.allocations);
  }

  private apply(allocations: ManualPaymentAllocation[] | undefined) {
    this.isApplying = true;

    this.paymentService.allocatePayment(this.uuid, allocations).subscribe({
      next: (res) => {
        this.isApplying = false;
        this.messageService.add({ severity: 'success', summary: 'Payment applied', detail: this.describe(res.result) });
        this.load();
      },
      error: (err) => {
        this.isApplying = false;
        this.messageService.add({
          severity: 'error', summary: 'Not applied',
          // The server says why: nothing left, no unpaid invoice in this currency, a balance that has moved.
          detail: err?.error?.message ?? 'The payment could not be applied.'
        });
      }
    });
  }

  private describe(done: CustomerPaymentAllocatedModel | null | undefined): string {
    if (!done) return 'The payment has been applied.';
    const count = done.allocations.length;
    const applied = done.allocations.reduce((sum, a) => sum + a.amount, 0);
    const invoices = `${count} invoice${count === 1 ? '' : 's'}`;
    return `${applied.toFixed(2)} applied to ${invoices}; ${done.unallocatedAmount.toFixed(2)} still on account.`;
  }

  // ── Display ─────────────────────────────────────────────────────────────────

  getStatusSeverity(status: string): Severity {
    return PAYMENT_STATUS_SEVERITY[status] ?? 'secondary';
  }

  getInvoiceSeverity(status: string): Severity {
    return INVOICE_STATUS_SEVERITY[status] ?? 'secondary';
  }

  formatStatus(code?: string | null): string { return formatCode(code); }
}
