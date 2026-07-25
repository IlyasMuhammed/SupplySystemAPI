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
import { MessageService } from 'primeng/api';
import { FinanceService, InvoiceDetailModel, SupplierPaymentListItemModel } from '../../../../services/finance.service';
import { PdfService } from '../../../../services/pdf.service';
import { TimelinePanelComponent } from '../../../../shared/timeline-panel/timeline-panel.component';

@Component({
  selector: 'app-invoice-detail',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, TagModule, ToastModule,
    CardModule, DialogModule, TableModule,
    TooltipModule, DividerModule,
    CalendarModule, DropdownModule, InputTextModule, TextareaModule, InputNumberModule, TimelinePanelComponent
  ],
  templateUrl: './invoice-detail.component.html',
  styleUrls: ['./invoice-detail.component.scss'],
  providers: [MessageService]
})
export class InvoiceDetailComponent implements OnInit {
  invoice: InvoiceDetailModel | null = null;
  isLoading = true;
  showTimeline = false;

  newPayments: SupplierPaymentListItemModel[] = [];
  isLoadingNewPayments = false;

  showApproveDialog = false;
  showRejectDialog  = false;
  approveNotes      = '';
  rejectReason      = '';
  isSaving = false;

  showUploadDialog  = false;
  selectedFile: File | null = null;
  isUploading = false;

  showEditDialog = false;
  editForm = { supplierInvoiceNo: '', dueDate: null as Date | null, paymentMethod: '', taxAmount: null as number | null, notes: '' };

  paymentMethodOptions = [
    { label: 'Bank Transfer', value: 'Bank Transfer' },
    { label: 'Cheque',        value: 'Cheque' },
    { label: 'Cash',          value: 'Cash' },
    { label: 'Credit Card',   value: 'Credit Card' },
    { label: 'Online',        value: 'Online' }
  ];

  constructor(
    private financeService: FinanceService,
    private messageService: MessageService,
    private pdfService: PdfService,
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
        this.newPayments = res.success ? res.result.data : [];
      },
      error: () => { this.isLoadingNewPayments = false; this.newPayments = []; }
    });
  }

  approve() {
    if (!this.invoice) return;
    this.isSaving = true;
    this.financeService.approveInvoice(this.invoice.uuid, this.approveNotes || undefined).subscribe({
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
    if (!this.invoice || !this.rejectReason.trim()) {
      this.messageService.add({ severity: 'warn', summary: 'Validation', detail: 'Rejection reason is required.' }); return;
    }
    this.isSaving = true;
    this.financeService.rejectInvoice(this.invoice.uuid, this.rejectReason).subscribe({
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

  openEditDialog() {
    if (!this.invoice) return;
    this.editForm = {
      supplierInvoiceNo: this.invoice.supplierInvoiceNo || '',
      dueDate:           this.invoice.dueDate ? new Date(this.invoice.dueDate) : null,
      paymentMethod:     this.invoice.paymentMethod || '',
      taxAmount:         this.invoice.taxAmount ?? null,
      notes:             this.invoice.notes || ''
    };
    this.showEditDialog = true;
  }

  saveEdit() {
    if (!this.invoice) return;
    this.isSaving = true;
    this.financeService.patchInvoice(this.invoice.uuid, {
      supplierInvoiceNo: this.editForm.supplierInvoiceNo || undefined,
      dueDate:           this.editForm.dueDate ? this.editForm.dueDate.toISOString() : undefined,
      paymentMethod:     this.editForm.paymentMethod || undefined,
      taxAmount:         this.editForm.taxAmount ?? undefined,
      notes:             this.editForm.notes || undefined
    }).subscribe({
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

  getMatchSeverity(s: string): 'success' | 'danger' | 'warn' | 'secondary' {
    switch (s) {
      case 'Matched': case 'Approved': return 'success';
      case 'Rejected': return 'danger';
      case 'Variance': return 'warn';
      default: return 'secondary';
    }
  }

  getPaymentSeverity(s: string): 'success' | 'danger' | 'warn' | 'info' | 'secondary' {
    switch (s) {
      case 'Paid': return 'success';
      case 'Overdue': return 'danger';
      case 'Partial': return 'warn';
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

  canApprove(): boolean { return this.invoice?.matchStatus === 'Matched' || this.invoice?.matchStatus === 'Variance'; }
  canReject(): boolean  { return this.invoice?.matchStatus !== 'Approved' && this.invoice?.matchStatus !== 'Rejected'; }
  canPay(): boolean     { return this.invoice?.matchStatus === 'Approved' && this.invoice?.paymentStatus !== 'Paid'; }
  canEdit(): boolean    { return this.invoice?.matchStatus !== 'Approved' && this.invoice?.matchStatus !== 'Rejected'; }

  downloadPdf(): void {
    if (this.invoice) this.pdfService.downloadInvoice(this.invoice);
  }
}
