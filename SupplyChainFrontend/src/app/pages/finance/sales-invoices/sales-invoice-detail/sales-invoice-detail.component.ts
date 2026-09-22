import { Component, OnInit, ViewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TableModule } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';
import { ToastModule } from 'primeng/toast';
import { DialogModule } from 'primeng/dialog';
import { CalendarModule } from 'primeng/calendar';
import { TextareaModule } from 'primeng/textarea';
import { MessageService } from 'primeng/api';

import { AttachmentListComponent } from '../../../../shared/attachment-list/attachment-list.component';
import {
  SalesInvoiceService, SalesInvoiceDetailModel, SalesInvoicePaymentModel, PAYABLE_INVOICE_STATUSES
} from '../../../../services/sales-invoice.service';
import { AuthService } from '../../../service/auth.service';
import { formatCode } from '../../../../shared/format-code';
import { fromDateOnly, toDateOnly } from '../../../../shared/date-only';
import { INVOICE_STATUS_SEVERITY, PAYMENT_STATUS_SEVERITY, Severity } from '../../receivables/receivables.shared';
import { SalesInvoicePdfDialogComponent } from '../sales-invoice-pdf-dialog/sales-invoice-pdf-dialog.component';

@Component({
  selector: 'app-sales-invoice-detail',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    TableModule, ButtonModule, TagModule, TooltipModule, ToastModule, DialogModule, CalendarModule, TextareaModule,
    AttachmentListComponent, SalesInvoicePdfDialogComponent
  ],
  templateUrl: './sales-invoice-detail.component.html',
  styleUrls: ['./sales-invoice-detail.component.scss'],
  providers: [MessageService]
})
export class SalesInvoiceDetailComponent implements OnInit {
  @ViewChild(AttachmentListComponent) attachments?: AttachmentListComponent;

  uuid = '';
  invoice: SalesInvoiceDetailModel | null = null;
  isLoading = true;
  notFound = false;

  pdfVisible = false;

  editVisible = false;
  editDueDate: Date | null = null;
  editNotes = '';
  isSaving = false;

  issueVisible = false;
  isIssuing = false;

  deleteVisible = false;
  isDeleting = false;

  isFiling = false;

  constructor(
    private route: ActivatedRoute,
    private router: Router,
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

    this.invoiceService.getInvoice(this.uuid).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success && res.result) {
          this.invoice = res.result;
          this.notFound = false;
        } else {
          this.invoice = null;
          this.notFound = true;
        }
      },
      error: (err) => {
        this.isLoading = false;
        this.invoice = null;
        this.notFound = err?.status === 404;
        if (!this.notFound) {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load the invoice.' });
        }
      }
    });
  }

  // ── What this user may do, and what the invoice allows ──────────────────────

  private can(code: string): boolean { return this.authService.hasPermission(code); }

  get isDraft(): boolean { return this.invoice?.status === 'DRAFT'; }

  /** A draft is the only invoice that can still change: everything else is booked on the customer's ledger. */
  get canEdit(): boolean   { return this.isDraft && this.can('SALES_INVOICE_MANAGE'); }
  get canIssue(): boolean  { return this.isDraft && this.can('SALES_INVOICE_MANAGE'); }
  get canDelete(): boolean { return this.isDraft && this.can('SALES_INVOICE_MANAGE'); }

  /** Something is still owing on an invoice that has been issued. */
  get canRecordPayment(): boolean {
    return !!this.invoice
        && PAYABLE_INVOICE_STATUSES.includes(this.invoice.status)
        && this.invoice.balanceDue > 0
        && this.can('CUSTOMER_PAYMENT_RECORD');
  }

  /** Files the PDF as it stands now, e.g. after a payment. Not for a draft, which is not yet what a customer is sent. */
  get canFilePdf(): boolean { return !!this.invoice && !this.isDraft && this.can('SALES_INVOICE_MANAGE'); }

  get canViewPayments(): boolean { return this.can('CUSTOMER_PAYMENT_VIEW'); }
  get canViewLedger(): boolean   { return this.can('CUSTOMER_LEDGER_VIEW'); }
  get canViewDelivery(): boolean { return this.can('DELIVERY_VIEW'); }
  get canViewOrder(): boolean {
    return ['SALE_ORDER_VIEW', 'SALE_ORDER_CREATE', 'SALE_ORDER_EDIT', 'SALE_ORDER_CONFIRM'].some(c => this.can(c));
  }

  // ── Edit a draft ────────────────────────────────────────────────────────────

  get editMinDate(): Date | null {
    return this.invoice ? fromDateOnly(this.invoice.invoiceDate) : null;
  }

  openEditDialog() {
    if (!this.canEdit || !this.invoice) return;
    this.editDueDate = fromDateOnly(this.invoice.dueDate);
    this.editNotes = this.invoice.notes ?? '';
    this.editVisible = true;
  }

  get canSaveEdit(): boolean {
    return !!this.editDueDate && this.editNotes.length <= 500 && !this.isSaving;
  }

  saveEdit() {
    if (!this.canEdit || !this.canSaveEdit) return;
    this.isSaving = true;

    this.invoiceService.updateInvoice(this.uuid, {
      dueDate: toDateOnly(this.editDueDate!),
      notes: this.editNotes.trim() || undefined
    }).subscribe({
      next: () => {
        this.isSaving = false;
        this.editVisible = false;
        this.messageService.add({ severity: 'success', summary: 'Saved', detail: 'The draft has been updated.' });
        this.load();
      },
      error: (err) => {
        this.isSaving = false;
        this.messageService.add({
          severity: 'error', summary: 'Not saved', detail: err?.error?.message ?? 'The invoice could not be saved.'
        });
      }
    });
  }

  // ── Issue ───────────────────────────────────────────────────────────────────

  openIssueDialog() {
    if (this.canIssue) this.issueVisible = true;
  }

  /** Books the receivable on the customer's ledger, then files the PDF. The response says whether the filing worked. */
  issue() {
    if (!this.canIssue || this.isIssuing) return;
    this.isIssuing = true;

    this.invoiceService.issueInvoice(this.uuid).subscribe({
      next: (res) => {
        this.isIssuing = false;
        this.issueVisible = false;
        this.messageService.add({ severity: 'success', summary: 'Invoice issued', detail: res.message || 'The invoice has been issued.' });
        // Reloaded as an issued invoice, it shows its filed copies, which load themselves when they appear.
        this.load();
      },
      error: (err) => {
        this.isIssuing = false;
        this.messageService.add({
          severity: 'error', summary: 'Not issued', detail: err?.error?.message ?? 'The invoice could not be issued.'
        });
      }
    });
  }

  // ── Delete ──────────────────────────────────────────────────────────────────

  openDeleteDialog() {
    if (this.canDelete) this.deleteVisible = true;
  }

  /** Frees the delivery to be invoiced again. */
  deleteDraft() {
    if (!this.canDelete || this.isDeleting) return;
    this.isDeleting = true;

    this.invoiceService.deleteInvoice(this.uuid).subscribe({
      next: () => {
        this.isDeleting = false;
        this.deleteVisible = false;
        this.messageService.add({ severity: 'success', summary: 'Deleted', detail: 'The draft invoice has been deleted.' });
        this.router.navigate(['/portal/pages/finance/sales-invoices']);
      },
      error: (err) => {
        this.isDeleting = false;
        this.messageService.add({
          severity: 'error', summary: 'Not deleted', detail: err?.error?.message ?? 'The invoice could not be deleted.'
        });
      }
    });
  }

  // ── File the PDF ────────────────────────────────────────────────────────────

  filePdf() {
    if (!this.canFilePdf || this.isFiling) return;
    this.isFiling = true;

    this.invoiceService.attachPdf(this.uuid).subscribe({
      next: (res) => {
        this.isFiling = false;
        this.messageService.add({ severity: 'success', summary: 'Filed', detail: res.message || 'The invoice PDF has been filed.' });
        this.attachments?.load();
      },
      error: (err) => {
        this.isFiling = false;
        this.messageService.add({
          severity: 'error', summary: 'Not filed', detail: err?.error?.message ?? 'The invoice PDF could not be filed.'
        });
      }
    });
  }

  // ── Display ─────────────────────────────────────────────────────────────────

  getStatusSeverity(status: string): Severity {
    return INVOICE_STATUS_SEVERITY[status] ?? 'secondary';
  }

  getPaymentSeverity(payment: SalesInvoicePaymentModel): Severity {
    return PAYMENT_STATUS_SEVERITY[payment.paymentStatus] ?? 'secondary';
  }

  formatStatus(code?: string | null): string { return formatCode(code); }
}
