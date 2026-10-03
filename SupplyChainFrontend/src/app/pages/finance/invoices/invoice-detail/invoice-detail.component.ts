import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule, ActivatedRoute } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { CardModule } from 'primeng/card';
import { DialogModule } from 'primeng/dialog';
import { TableModule } from 'primeng/table';
import { TooltipModule } from 'primeng/tooltip';
import { DividerModule } from 'primeng/divider';
import { CalendarModule } from 'primeng/calendar';
import { DropdownModule } from 'primeng/dropdown';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { InputNumberModule } from 'primeng/inputnumber';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { MessageService, ConfirmationService } from 'primeng/api';
import {
  FinanceService, InvoiceDetailModel, SupplierPaymentListItemModel, PatchInvoiceRequest, NO_TAX_CODE, purchaseTaxFor,
  INVOICE_NOTES_MAX, taxCodeOptionLabel
} from '../../../../services/finance.service';
import { FinanceSetupService, TaxCodeModel } from '../../../../services/finance-setup.service';
import { AuthService } from '../../../service/auth.service';
import { TimelinePanelComponent } from '../../../../shared/timeline-panel/timeline-panel.component';
import { AttachmentListComponent } from '../../../../shared/attachment-list/attachment-list.component';
import { QboSyncBadgeComponent } from '../../../../shared/components/qbo-sync-badge/qbo-sync-badge.component';
import { fromDateOnly, toDateOnly } from '../../../../shared/date-only';

/** The reversal reason's column length on the server. */
export const REVERSAL_REASON_MAX = 500;

/** Payment statuses after which nothing more is owed. */
const SETTLED_PAYMENT_STATUSES = ['Paid', 'FULLY_PAID', 'OVERPAID'];

@Component({
  selector: 'app-invoice-detail',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, TagModule, ToastModule,
    CardModule, DialogModule, TableModule,
    TooltipModule, DividerModule, ConfirmDialogModule,
    CalendarModule, DropdownModule, InputTextModule, TextareaModule, InputNumberModule, TimelinePanelComponent,
    AttachmentListComponent, QboSyncBadgeComponent
  ],
  templateUrl: './invoice-detail.component.html',
  styleUrls: ['./invoice-detail.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class InvoiceDetailComponent implements OnInit {
  invoice: InvoiceDetailModel | null = null;
  isLoading = true;
  showTimeline = false;

  newPayments: SupplierPaymentListItemModel[] = [];
  isLoadingNewPayments = false;
  /**
   * The supplier payments could not be read (GET needs PAYMENT_VIEW, which a custom role may lack): whether
   * anything is paid is unknown, so nothing that needs "unpaid" (reverse, tax, supplier number) is offered on a guess.
   */
  paymentsUnknown = false;

  showApproveDialog = false;
  showRejectDialog  = false;
  approveNotes      = '';
  rejectReason      = '';
  isSaving = false;

  showReverseDialog = false;
  reverseReason     = '';
  readonly reversalReasonMax = REVERSAL_REASON_MAX;
  /** Notes, approval notes and a rejection reason: the server refuses more (400). */
  readonly notesMax = INVOICE_NOTES_MAX;

  showUploadDialog  = false;
  selectedFile: File | null = null;
  isUploading = false;

  showEditDialog = false;
  editForm = {
    supplierInvoiceNo: '', dueDate: null as Date | null, paymentMethod: '',
    taxCodeUuid: null as string | null, taxAmount: null as number | null, notes: ''
  };

  /** Purchase tax codes for the edit dialog — loaded the first time it opens on an invoice whose tax can still change. */
  taxCodes: TaxCodeModel[] = [];
  taxCodeOptions: { label: string; value: string | null }[] = [{ label: 'No tax code — enter the amount', value: null }];
  private taxCodesLoaded = false;

  paymentMethodOptions = [
    { label: 'Bank Transfer', value: 'Bank Transfer' },
    { label: 'Cheque',        value: 'Cheque' },
    { label: 'Cash',          value: 'Cash' },
    { label: 'Credit Card',   value: 'Credit Card' },
    { label: 'Online',        value: 'Online' }
  ];

  isDownloadingPdf = false;

  constructor(
    private financeService: FinanceService,
    private financeSetupService: FinanceSetupService,
    private authService: AuthService,
    private messageService: MessageService,
    private confirmationService: ConfirmationService,
    private route: ActivatedRoute
  ) {}

  ngOnInit() {
    const uuid = this.route.snapshot.paramMap.get('uuid');
    if (uuid) this.load(uuid);
  }

  load(uuid: string) {
    this.isLoading = true;
    this.financeService.getInvoiceById(uuid).subscribe({
      next: (res) => {
        this.isLoading = false;
        this.invoice   = res.success ? res.result : null;
        if (this.invoice) this.loadNewPayments(this.invoice.uuid);
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load invoice.' });
      }
    });
  }

  loadNewPayments(invoiceUuid: string) {
    this.isLoadingNewPayments = true;
    this.financeService.getSupplierPayments({ invoiceUuid }).subscribe({
      next: (res) => {
        this.isLoadingNewPayments = false;
        this.paymentsUnknown = !res.success;
        this.newPayments = res.success ? (res.result?.data ?? []) : [];
      },
      error: () => { this.isLoadingNewPayments = false; this.newPayments = []; this.paymentsUnknown = true; }
    });
  }

  openApproveDialog() {
    this.approveNotes = '';
    this.showApproveDialog = true;
  }

  openRejectDialog() {
    this.rejectReason = '';
    this.showRejectDialog = true;
  }

  /** Approval notes replace the invoice's notes on the server, so blanks are not sent — they would wipe them. */
  approve() {
    if (!this.invoice) return;
    const notes = this.approveNotes.trim();
    if (notes.length > INVOICE_NOTES_MAX) {
      this.messageService.add({ severity: 'warn', summary: 'Validation', detail: `Notes can be at most ${INVOICE_NOTES_MAX} characters.` }); return;
    }
    this.isSaving = true;
    this.financeService.approveInvoice(this.invoice.uuid, notes || undefined).subscribe({
      next: () => {
        this.isSaving = false;
        this.showApproveDialog = false;
        this.messageService.add({ severity: 'success', summary: 'Approved', detail: 'Invoice approved.' });
        this.load(this.invoice!.uuid);
      },
      error: (err) => {
        this.isSaving = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message || 'Failed.' });
      }
    });
  }

  reject() {
    const reason = this.rejectReason.trim();
    if (!this.invoice || !reason) {
      this.messageService.add({ severity: 'warn', summary: 'Validation', detail: 'Rejection reason is required.' }); return;
    }
    if (reason.length > INVOICE_NOTES_MAX) {
      this.messageService.add({ severity: 'warn', summary: 'Validation', detail: `The reason can be at most ${INVOICE_NOTES_MAX} characters.` }); return;
    }
    this.isSaving = true;
    this.financeService.rejectInvoice(this.invoice.uuid, reason).subscribe({
      next: () => {
        this.isSaving = false;
        this.showRejectDialog = false;
        this.messageService.add({ severity: 'success', summary: 'Rejected', detail: 'Invoice rejected.' });
        this.load(this.invoice!.uuid);
      },
      error: (err) => {
        this.isSaving = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message || 'Failed.' });
      }
    });
  }

  // ── Reverse (S-7) ──────────────────────────────────────────────────────────

  openReverseDialog() {
    this.reverseReason = '';
    this.showReverseDialog = true;
  }

  /** Asks once more — a reversal posts to the supplier ledger and cannot be undone — then reverses. */
  confirmReverse() {
    const inv = this.invoice;
    const reason = this.reverseReason.trim();
    if (!inv) return;
    if (!reason) {
      this.messageService.add({ severity: 'warn', summary: 'Validation', detail: 'Say why the invoice is being reversed.' }); return;
    }
    if (reason.length > REVERSAL_REASON_MAX) {
      this.messageService.add({ severity: 'warn', summary: 'Validation', detail: `The reason can be at most ${REVERSAL_REASON_MAX} characters.` }); return;
    }

    this.confirmationService.confirm({
      header: 'Confirm Reversal',
      icon: 'pi pi-exclamation-triangle',
      message: `Reverse ${inv.invoiceNumber}? ${inv.supplierName}'s ledger is credited with the approved `
             + `${inv.totalAmount.toFixed(2)} ${inv.currency} and the invoice becomes Reversed. This cannot be undone.`,
      acceptLabel: 'Reverse',
      rejectLabel: 'Cancel',
      acceptButtonStyleClass: 'p-button-danger',
      accept: () => this.reverse(reason)
    });
  }

  reverse(reason: string) {
    if (!this.invoice) return;
    this.isSaving = true;
    this.financeService.reverseInvoice(this.invoice.uuid, reason).subscribe({
      next: (res) => {
        this.isSaving = false;
        this.showReverseDialog = false;
        this.messageService.add({ severity: 'success', summary: 'Reversed', detail: res.message || 'Invoice reversed.' });
        if (res.success && res.result) this.invoice = res.result;
        this.load(this.invoice!.uuid);
      },
      error: (err) => {
        this.isSaving = false;
        this.messageService.add({ severity: 'error', summary: 'Not reversed', detail: err?.error?.message || 'The invoice could not be reversed.' });
      }
    });
  }

  // ── Edit ───────────────────────────────────────────────────────────────────

  openEditDialog() {
    if (!this.invoice) return;
    this.editForm = {
      supplierInvoiceNo: this.invoice.supplierInvoiceNo || '',
      // The day the server holds (its yyyy-MM-dd), not that instant in the reader's zone: new Date() on a
      // "…Z" or bare date string is the day before west of UTC, and saving unchanged would then move it.
      dueDate:           this.invoice.dueDate ? fromDateOnly(this.invoice.dueDate) : null,
      paymentMethod:     this.invoice.paymentMethod || '',
      taxCodeUuid:       this.invoice.taxCodeUuid ?? null,
      taxAmount:         this.invoice.taxAmount ?? null,
      notes:             this.invoice.notes || ''
    };
    if (this.canEditTax()) this.loadTaxCodes();
    this.showEditDialog = true;
  }

  private loadTaxCodes() {
    if (this.taxCodesLoaded) return;
    this.financeSetupService.getTaxCodes('PURCHASE').subscribe({
      next: (res) => {
        this.taxCodesLoaded = true;
        this.taxCodes = res.result ?? [];
        this.taxCodeOptions = [
          { label: 'No tax code — enter the amount', value: null },
          ...this.taxCodes.map(c => ({ label: taxCodeOptionLabel(c.code, c.ratePercent, c.name), value: c.uuid }))
        ];
        // The invoice's own code may since have been deactivated; it still shows as what it is.
        this.addOwnTaxCodeOption(' (no longer active)');
      },
      error: () => {
        // Never blank: the invoice's own code still shows, though nothing else can be picked.
        this.addOwnTaxCodeOption('');
        this.messageService.add({ severity: 'warn', summary: 'Tax codes', detail: 'Tax codes could not be loaded.' });
      }
    });
  }

  private addOwnTaxCodeOption(suffix: string) {
    const inv = this.invoice;
    const own = inv?.taxCodeUuid;
    if (!inv || !own || this.taxCodeOptions.some(o => o.value === own)) return;
    this.taxCodeOptions = [...this.taxCodeOptions, { label: taxCodeOptionLabel(inv.taxCode ?? '', inv.taxPercent) + suffix, value: own }];
  }

  /** The code chosen in the edit dialog, when it is one of the purchase codes on offer. */
  get editTaxCode(): TaxCodeModel | null {
    return this.taxCodes.find(c => c.uuid === this.editForm.taxCodeUuid) ?? null;
  }

  /** What the tax would be after the edit: the code's figure (the invoice's own snapshot if unchanged), or the amount typed. */
  get editTaxPreview(): number {
    const inv = this.invoice;
    if (!inv) return 0;
    if (this.editForm.taxCodeUuid && this.editForm.taxCodeUuid === inv.taxCodeUuid) return inv.taxAmount;
    const code = this.editTaxCode;
    return code ? purchaseTaxFor(inv.subtotal, code.ratePercent) : (this.editForm.taxAmount ?? 0);
  }

  /**
   * Only what changed is sent — the server refuses some fields on an approved or paid invoice even when resent
   * unchanged. On a reversed invoice only the notes are ever sent (anything else is 409).
   */
  buildPatch(): PatchInvoiceRequest {
    const inv = this.invoice!;
    const patch: PatchInvoiceRequest = {};

    const notes = this.editForm.notes ?? '';
    if (notes !== (inv.notes ?? '')) patch.notes = notes;
    if (this.notesOnlyEdit) return patch;

    const supplierNo = this.editForm.supplierInvoiceNo.trim();
    if (this.canEditSupplierNo() && supplierNo !== (inv.supplierInvoiceNo ?? '')) patch.supplierInvoiceNo = supplierNo;

    const due = this.editForm.dueDate ? toDateOnly(this.editForm.dueDate) : null;
    if (due && due !== (inv.dueDate ?? '').slice(0, 10)) patch.dueDate = due;

    // The dropdown's clear (x) writes null: that is a change to "no method", sent as '' (the server clears it).
    const method = this.editForm.paymentMethod ?? '';
    if (method !== (inv.paymentMethod ?? '')) patch.paymentMethod = method;

    if (this.canEditTax()) {
      const code = this.editForm.taxCodeUuid ?? null;
      const ownCode = inv.taxCodeUuid ?? null;
      if (code !== ownCode) {
        patch.taxCodeUuid = code ?? NO_TAX_CODE;
        if (!code) patch.taxAmount = this.editForm.taxAmount ?? inv.taxAmount;
      } else if (!code && this.editForm.taxAmount !== null && this.editForm.taxAmount !== inv.taxAmount) {
        patch.taxAmount = this.editForm.taxAmount;
      }
    }
    return patch;
  }

  saveEdit() {
    if (!this.invoice) return;
    if ((this.editForm.notes ?? '').length > INVOICE_NOTES_MAX) {
      this.messageService.add({ severity: 'warn', summary: 'Validation', detail: `Notes can be at most ${INVOICE_NOTES_MAX} characters.` }); return;
    }
    const patch = this.buildPatch();
    if (Object.keys(patch).length === 0) { this.showEditDialog = false; return; }

    this.isSaving = true;
    this.financeService.patchInvoice(this.invoice.uuid, patch).subscribe({
      next: () => {
        this.isSaving = false;
        this.showEditDialog = false;
        this.messageService.add({ severity: 'success', summary: 'Updated', detail: 'Invoice updated successfully.' });
        this.load(this.invoice!.uuid);
      },
      error: (err) => {
        this.isSaving = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message || 'Update failed.' });
      }
    });
  }

  onFileSelected(event: Event) {
    const input = event.target as HTMLInputElement;
    this.selectedFile = input.files?.[0] ?? null;
  }

  uploadAttachment() {
    if (!this.invoice || !this.selectedFile) return;
    this.isUploading = true;
    this.financeService.uploadAttachment(this.invoice.uuid, this.selectedFile).subscribe({
      next: () => {
        this.isUploading = false;
        this.showUploadDialog = false;
        this.selectedFile = null;
        this.messageService.add({ severity: 'success', summary: 'Uploaded', detail: 'Attachment uploaded.' });
        this.load(this.invoice!.uuid);
      },
      error: (err) => {
        this.isUploading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message || 'Upload failed.' });
      }
    });
  }

  resolveUrl(url: string) { return this.financeService.resolveFileUrl(url); }

  getMatchSeverity(s: string): 'success' | 'danger' | 'warn' | 'secondary' | 'contrast' {
    switch (s) {
      case 'Matched': case 'Approved': return 'success';
      case 'Rejected': return 'danger';
      case 'Variance': return 'warn';
      case 'Reversed': return 'contrast';
      default: return 'secondary';
    }
  }

  getPaymentSeverity(s: string): 'success' | 'danger' | 'warn' | 'info' | 'secondary' {
    switch (s) {
      case 'Paid': case 'FULLY_PAID': return 'success';
      case 'Overdue': return 'danger';
      case 'Partial': case 'PARTIALLY_PAID': return 'warn';
      case 'Scheduled': return 'info';
      default: return 'secondary';
    }
  }

  get deductionRows(): { type: string; number: string; reason: string | null; amount: number; date: string }[] {
    if (!this.invoice) return [];
    const debits = (this.invoice.debitNotes ?? []).map(d => ({
      type: 'Debit Note', number: d.debitNoteNumber, reason: d.debitReason, amount: d.debitAmount, date: d.issuedAt || d.createdDate
    }));
    const credits = (this.invoice.creditNotes ?? []).map(c => ({
      type: 'Credit Note', number: c.creditNoteNumber, reason: null, amount: c.creditAmount, date: c.creditDate
    }));
    return [...debits, ...credits];
  }

  get totalDeducted(): number {
    return this.deductionRows.reduce((s, d) => s + (d.amount || 0), 0);
  }

  // ── What the server allows, mirrored ─────────────────────────────────────────

  private is(status: string): boolean { return this.invoice?.matchStatus === status; }

  /** Payments recorded against it: money posted, a supplier payment not cancelled or bounced, or a legacy payment not reversed. */
  get hasPayments(): boolean {
    const inv = this.invoice;
    if (!inv) return false;
    return (inv.paidAmount ?? 0) !== 0
        || this.newPayments.some(p => p.status !== 'CANCELLED' && p.status !== 'BOUNCED')
        || (inv.payments ?? []).some(p => p.status !== 'Reversed');
  }

  get taxLabel(): string {
    const inv = this.invoice;
    return inv?.taxCode ? `Tax (${inv.taxCode} · ${inv.taxPercent ?? 0}%)` : 'Tax';
  }

  /** The base-currency figure is worth showing only when the invoice is in another currency. */
  get showBaseTotal(): boolean {
    const inv = this.invoice;
    return !!inv && inv.baseTotalAmount != null && !!inv.baseCurrencyCode && inv.baseCurrencyCode !== inv.currency;
  }

  /** Approved in a foreign currency when no rate was on file: say so rather than show nothing. */
  get missingRate(): boolean {
    const inv = this.invoice;
    return !!inv && !!inv.approvedAt && !!inv.baseCurrencyCode && inv.baseCurrencyCode !== inv.currency && inv.exchangeRate == null;
  }

  /**
   * Everything that changes an invoice — create, edit, approve, reject, reverse, upload — needs INVOICE_PROCESS on
   * the server (403 otherwise); INVOICE_VIEW only reads it and its PDF.
   */
  get canProcess(): boolean { return this.authService.hasPermission('INVOICE_PROCESS'); }

  canApprove(): boolean { return this.canProcess && (this.is('Pending') || this.is('Matched') || this.is('Variance')); }
  /** Refused (409) once Approved (reverse instead), Rejected or Reversed. */
  canReject(): boolean  { return this.canProcess && !!this.invoice && !this.is('Approved') && !this.is('Rejected') && !this.is('Reversed'); }
  /** Payments are recorded on the payments page (PAYMENT_PROCESS), against an approved invoice still owed money. */
  canPay(): boolean {
    return this.is('Approved') && this.authService.hasPermission('PAYMENT_PROCESS')
        && !SETTLED_PAYMENT_STATUSES.includes(this.invoice?.paymentStatus ?? '');
  }
  /**
   * An approved invoice can still have its due date, method, notes and (while unpaid) supplier number edited; a
   * reversed one only its notes (see notesOnlyEdit). A rejected one is not offered for editing at all.
   */
  canEdit(): boolean    { return this.canProcess && !!this.invoice && !this.is('Rejected'); }
  /** Reversed: the server takes only notes and the attachment (409 for anything else). */
  get notesOnlyEdit(): boolean { return this.is('Reversed'); }
  /** Tax and tax code: not once approved (reverse instead), not once reversed, not once paid against. */
  canEditTax(): boolean { return this.canEdit() && !this.is('Approved') && !this.is('Reversed') && !this.hasPayments && !this.paymentsUnknown; }
  canEditSupplierNo(): boolean { return !this.is('Reversed') && !this.hasPayments && !this.paymentsUnknown; }
  /** An attachment can be added in any status, a reversed invoice included. */
  canUpload(): boolean  { return this.canProcess && !!this.invoice; }

  /** Shown to INVOICE_PROCESS holders on an approved invoice; disabled with the reason when the server would refuse. */
  canSeeReverse(): boolean { return this.is('Approved') && this.canProcess; }

  get reverseBlockedReason(): string | null {
    const inv = this.invoice;
    if (!inv) return null;
    if (this.hasPayments) return 'Payments have been recorded against this invoice. Only an unpaid invoice can be reversed.';
    if (this.paymentsUnknown) return 'The payments against this invoice could not be checked (that needs PAYMENT_VIEW), so it cannot be confirmed unpaid.';
    if ((inv.debitNotes?.length ?? 0) + (inv.creditNotes?.length ?? 0) > 0)
      return 'A credit or debit note has been deducted from this invoice, so it cannot be reversed.';
    return null;
  }

  downloadPdf(): void {
    if (!this.invoice) return;
    this.isDownloadingPdf = true;
    this.financeService.downloadInvoicePdf(this.invoice.uuid).subscribe({
      next: (blob) => {
        this.isDownloadingPdf = false;
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = `Invoice-${this.invoice?.invoiceNumber || this.invoice?.uuid}.pdf`;
        a.click();
        URL.revokeObjectURL(url);
      },
      error: () => {
        this.isDownloadingPdf = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to generate invoice PDF.' });
      }
    });
  }
}
