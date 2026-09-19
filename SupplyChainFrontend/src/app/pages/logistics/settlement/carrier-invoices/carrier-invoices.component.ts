import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TagModule } from 'primeng/tag';
import { TableModule, TableLazyLoadEvent } from 'primeng/table';
import { TooltipModule } from 'primeng/tooltip';
import { ToastModule } from 'primeng/toast';
import { DialogModule } from 'primeng/dialog';
import { InputTextModule } from 'primeng/inputtext';
import { InputNumberModule } from 'primeng/inputnumber';
import { TextareaModule } from 'primeng/textarea';
import { SelectModule } from 'primeng/select';
import { DatePickerModule } from 'primeng/datepicker';
import { MessageService } from 'primeng/api';

import {
  LogisticsService,
  CarrierInvoiceModel,
  CarrierInvoiceLineRequest,
  CarrierListItemModel
} from '../../../../services/logistics.service';

type Severity = 'success' | 'info' | 'warn' | 'danger' | 'secondary' | 'contrast';

export const INVOICE_STATUS_SEVERITY: Record<string, Severity> = {
  RECEIVED: 'info', MATCHED: 'success', DISPUTED: 'warn', CANCELLED: 'secondary'
};

/** A line being keyed. Kept apart from the request shape so the form can hold partial input. */
interface LineDraft {
  description: string;
  awbNumber: string;
  consignmentReference: string;
  chargeCode: string;
  serviceCode: string;
  chargeableWeightKg: number | null;
  amount: number | null;
}

/**
 * Bills from carriers — the third leg of the three-way match.
 *
 * **The lines must add up to the header.** The server refuses a bill that does not, and so does
 * this form, because a bill whose lines disagree with its own total has been mis-keyed — and
 * matching it line by line against a header saying something else is how a discrepancy gets
 * absorbed without anybody seeing it.
 */
@Component({
  selector: 'app-carrier-invoices',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, TagModule, TableModule, TooltipModule, ToastModule, DialogModule,
    InputTextModule, InputNumberModule, TextareaModule, SelectModule, DatePickerModule
  ],
  templateUrl: './carrier-invoices.component.html',
  styleUrls: ['./carrier-invoices.component.scss'],
  providers: [MessageService]
})
export class CarrierInvoicesComponent implements OnInit {
  /** Angular templates cannot reach globals, and the running total needs an absolute value. */
  readonly Math = Math;

  invoices: CarrierInvoiceModel[] = [];
  carriers: CarrierListItemModel[] = [];

  total = 0;
  page = 1;
  pageSize = 20;

  isLoading = true;
  isSubmitting = false;

  filter = { carrierUuid: null as string | null, status: null as string | null, search: '' };

  readonly statusOptions = [
    { label: 'Every status', value: null },
    { label: 'Received',     value: 'RECEIVED' },
    { label: 'Matched',      value: 'MATCHED' },
    { label: 'Disputed',     value: 'DISPUTED' },
    { label: 'Withdrawn',    value: 'CANCELLED' }
  ];

  // ── Keying a bill ───────────────────────────────────────────────────────────

  dialogVisible = false;

  form = {
    carrierUuid: null as string | null,
    invoiceNumber: '',
    invoiceDate: new Date() as Date | null,
    dueDate: null as Date | null,
    currency: 'PKR',
    totalAmount: null as number | null,
    taxAmount: null as number | null,
    notes: ''
  };

  lines: LineDraft[] = [];

  cancelDialogVisible = false;
  cancelling: CarrierInvoiceModel | null = null;
  cancelReason = '';

  constructor(
    private logisticsService: LogisticsService,
    private messageService: MessageService
  ) {}

  ngOnInit() {
    this.load();
    this.loadCarriers();
  }

  load(event?: TableLazyLoadEvent) {
    if (event) {
      this.pageSize = event.rows ?? this.pageSize;
      this.page = Math.floor((event.first ?? 0) / this.pageSize) + 1;
    }

    this.isLoading = true;

    this.logisticsService.getCarrierInvoices({
      carrierUuid: this.filter.carrierUuid ?? undefined,
      status:      this.filter.status ?? undefined,
      search:      this.filter.search.trim() || undefined,
      page:        this.page,
      pageSize:    this.pageSize
    }).subscribe({
      next: (res) => {
        this.isLoading = false;
        this.invoices = res.result?.data ?? [];
        this.total    = res.result?.totalRecords ?? 0;
      },
      error: (err) => {
        this.isLoading = false;
        this.invoices = [];
        this.fail(err, 'The bills could not be loaded.');
      }
    });
  }

  search() {
    this.page = 1;
    this.load();
  }

  private loadCarriers() {
    this.logisticsService.getActiveCarriers().subscribe({
      next: (res) => this.carriers = res.result ?? [],
      error: () => this.carriers = []
    });
  }

  get carrierOptions() {
    return [
      { label: 'Every carrier', value: null },
      ...this.carriers.map(c => ({ label: c.name, value: c.uuid }))
    ];
  }

  get carrierPickList() {
    return this.carriers.map(c => ({ label: c.name, value: c.uuid }));
  }

  statusSeverity(status: string): Severity {
    return INVOICE_STATUS_SEVERITY[status] ?? 'secondary';
  }

  formatStatus(status?: string): string {
    if (!status) return '';
    if (status === 'CANCELLED') return 'Withdrawn';
    return status.charAt(0) + status.slice(1).toLowerCase();
  }

  // ── The editor ──────────────────────────────────────────────────────────────

  openCreate() {
    this.form = {
      carrierUuid: null, invoiceNumber: '',
      invoiceDate: new Date(), dueDate: null,
      currency: 'PKR', totalAmount: null, taxAmount: null, notes: ''
    };
    this.lines = [this.newLine()];
    this.dialogVisible = true;
  }

  private newLine(): LineDraft {
    return {
      description: '', awbNumber: '', consignmentReference: '',
      chargeCode: '', serviceCode: '', chargeableWeightKg: null, amount: null
    };
  }

  addLine()             { this.lines.push(this.newLine()); }
  removeLine(i: number) { this.lines.splice(i, 1); }

  /** The sum of what has been keyed, shown live against the header. */
  get lineTotal(): number {
    return this.lines.reduce((sum, l) => sum + (l.amount ?? 0), 0);
  }

  get difference(): number {
    return this.lineTotal - (this.form.totalAmount ?? 0);
  }

  /**
   * The server's rules, checked here too so the dialog names the field rather than surfacing a
   * refusal after a round trip.
   */
  get validationError(): string | null {
    if (!this.form.carrierUuid)            return 'Say which carrier sent this bill.';
    if (!this.form.invoiceNumber.trim())   return "A bill needs the carrier's own invoice number.";
    if (!this.form.invoiceDate)            return 'A bill needs the date the carrier issued it.';
    if (this.form.currency.trim().length !== 3) return 'A currency is three ISO letters, like PKR.';

    if (this.form.dueDate && this.form.invoiceDate && this.form.dueDate < this.form.invoiceDate)
      return 'A bill cannot fall due before it was issued.';

    if (!this.form.totalAmount) return 'A bill for nothing is not a bill.';
    if (!this.lines.length)     return 'A bill with no lines cannot be matched against anything.';

    for (const [i, line] of this.lines.entries()) {
      if (!line.description.trim()) return `Line ${i + 1} needs a description.`;
      if (!line.amount)             return `Line ${i + 1} charges nothing.`;
    }

    // A cent of rounding is ordinary; anything larger has been mis-keyed.
    if (Math.abs(this.difference) > 0.01)
      return `The lines come to ${this.lineTotal.toFixed(2)} and the bill says `
           + `${(this.form.totalAmount ?? 0).toFixed(2)}. They have to agree.`;

    return null;
  }

  get canSave(): boolean {
    return !this.isSubmitting && this.validationError === null;
  }

  save() {
    if (!this.canSave) return;
    this.isSubmitting = true;

    const lines: CarrierInvoiceLineRequest[] = this.lines.map(l => ({
      description:          l.description.trim(),
      awbNumber:            l.awbNumber.trim() || undefined,
      consignmentReference: l.consignmentReference.trim() || undefined,
      chargeCode:           l.chargeCode.trim() || undefined,
      serviceCode:          l.serviceCode.trim() || undefined,
      chargeableWeightKg:   l.chargeableWeightKg ?? undefined,
      amount:               l.amount!
    }));

    this.logisticsService.createCarrierInvoice({
      carrierUuid:   this.form.carrierUuid!,
      invoiceNumber: this.form.invoiceNumber.trim(),
      invoiceDate:   this.form.invoiceDate!.toISOString(),
      dueDate:       this.form.dueDate?.toISOString(),
      currency:      this.form.currency.trim().toUpperCase(),
      totalAmount:   this.form.totalAmount!,
      taxAmount:     this.form.taxAmount ?? undefined,
      notes:         this.form.notes.trim() || undefined,
      lines
    }).subscribe({
      next: () => {
        this.isSubmitting = false;
        this.dialogVisible = false;
        this.ok('Bill recorded.');
        this.load();
      },
      error: (err) => {
        this.isSubmitting = false;
        this.fail(err, 'The bill could not be recorded.');
      }
    });
  }

  // ── Withdrawing ─────────────────────────────────────────────────────────────

  openCancel(invoice: CarrierInvoiceModel) {
    this.cancelling = invoice;
    this.cancelReason = '';
    this.cancelDialogVisible = true;
  }

  get canCancel(): boolean {
    return !this.isSubmitting && !!this.cancelReason.trim();
  }

  confirmCancel() {
    if (!this.canCancel || !this.cancelling) return;
    this.isSubmitting = true;

    this.logisticsService.cancelCarrierInvoice(this.cancelling.uuid, this.cancelReason.trim()).subscribe({
      next: () => {
        this.isSubmitting = false;
        this.cancelDialogVisible = false;
        this.ok('Bill withdrawn.');
        this.load();
      },
      error: (err) => {
        this.isSubmitting = false;
        this.fail(err, 'The bill could not be withdrawn.');
      }
    });
  }

  private ok(detail: string) {
    this.messageService.add({ severity: 'success', summary: 'Done', detail });
  }

  private fail(err: any, fallback: string) {
    this.messageService.add({
      severity: 'error', summary: 'Not allowed',
      detail: err?.error?.message ?? fallback, life: 8000
    });
  }
}
