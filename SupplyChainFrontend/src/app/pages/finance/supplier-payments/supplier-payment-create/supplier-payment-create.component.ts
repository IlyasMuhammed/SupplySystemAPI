import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule, Router, ActivatedRoute } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { DropdownModule } from 'primeng/dropdown';
import { InputNumberModule } from 'primeng/inputnumber';
import { CalendarModule } from 'primeng/calendar';
import { ToastModule } from 'primeng/toast';
import { CardModule } from 'primeng/card';
import { TagModule } from 'primeng/tag';
import { TextareaModule } from 'primeng/textarea';
import { TableModule } from 'primeng/table';
import { AutoCompleteModule } from 'primeng/autocomplete';
import { MessageService } from 'primeng/api';
import { FinanceService, CreateSupplierPaymentRequest, OutstandingInvoiceModel } from '../../../../services/finance.service';
import { SupplierService, SupplierListItemModel } from '../../../../services/supplier.service';
import { AttachmentListComponent } from '../../../../shared/attachment-list/attachment-list.component';

export interface PaymentLineInput {
  invoiceUuid: string;
  invoiceNumber: string;
  outstandingAmount: number;
  allocatedAmount: number;
  notes?: string;
}

@Component({
  selector: 'app-supplier-payment-create',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, InputTextModule, DropdownModule,
    InputNumberModule, CalendarModule, ToastModule,
    CardModule, TagModule, TextareaModule, TableModule, AutoCompleteModule, AttachmentListComponent
  ],
  templateUrl: './supplier-payment-create.component.html',
  styleUrls: ['./supplier-payment-create.component.scss'],
  providers: [MessageService]
})
export class SupplierPaymentCreateComponent implements OnInit {
  // Supplier selection
  supplierLocked = false;
  selectedSupplierId = '';
  selectedSupplierName = '';
  supplierAuto: SupplierListItemModel | null = null;
  supplierSuggestions: SupplierListItemModel[] = [];
  supplierTouched = false;

  // Outstanding invoices for the chosen supplier
  outstandingInvoices: OutstandingInvoiceModel[] = [];
  isLoadingInvoices = false;

  // Allocation lines
  lineInputs: PaymentLineInput[] = [];
  selectedInvoiceToAdd = '';

  // Header fields
  paymentDateVal: Date | null = new Date();
  paymentMethod = 'BANK_TRANSFER';
  bankAccount = '';
  chequeNo = '';
  chequeDateVal: Date | null = null;
  notes = '';
  paymentType: 'STANDARD' | 'ADVANCE_PAYMENT' = 'STANDARD';
  manualTotalAmount: number | null = null;

  isSaving = false;

  // Client-generated id so payment-evidence attachments uploaded before save can be linked
  // to this payment via the same DocumentId — becomes the payment's own UUID on save.
  readonly paymentUuid = crypto.randomUUID();

  paymentMethodOptions = [
    { label: 'Bank Transfer', value: 'BANK_TRANSFER' },
    { label: 'Online Wire',   value: 'ONLINE_WIRE' },
    { label: 'Cheque',        value: 'CHEQUE' },
    { label: 'Cash',          value: 'CASH' }
  ];

  paymentTypeOptions = [
    { label: 'Standard Payment', value: 'STANDARD' },
    { label: 'Advance Payment',  value: 'ADVANCE_PAYMENT' }
  ];

  get showBankField():   boolean { return this.paymentMethod === 'BANK_TRANSFER' || this.paymentMethod === 'ONLINE_WIRE'; }
  get showChequeFields(): boolean { return this.paymentMethod === 'CHEQUE'; }

  get addableInvoices(): OutstandingInvoiceModel[] {
    const added = new Set(this.lineInputs.map(l => l.invoiceUuid));
    return this.outstandingInvoices.filter(i => !added.has(i.invoiceUuid));
  }

  get totalAmount(): number {
    if (this.lineInputs.length > 0) {
      const sum = this.lineInputs.reduce((s, l) => s + (l.allocatedAmount || 0), 0);
      return Math.round(sum * 100) / 100;
    }
    return this.manualTotalAmount ?? 0;
  }

  constructor(
    private financeService: FinanceService,
    private supplierService: SupplierService,
    private messageService: MessageService,
    private router: Router,
    private route: ActivatedRoute
  ) {}

  ngOnInit() {
    const supplierId  = this.route.snapshot.queryParamMap.get('supplierId');
    const invoiceUuid = this.route.snapshot.queryParamMap.get('invoiceUuid');

    if (supplierId) {
      this.supplierLocked = true;
      this.selectedSupplierId = supplierId;
      this.supplierService.getSupplierById(supplierId).subscribe({
        next: (res) => { this.selectedSupplierName = res.success ? res.result.supplierName : ''; },
        error: () => { this.selectedSupplierName = ''; }
      });
      this.loadOutstandingInvoices(supplierId, invoiceUuid ?? undefined);
    }
  }

  // ── Supplier picker (only used when not locked via query params) ──────────

  searchSuppliers(event: any) {
    const q = (event.query as string ?? '').trim();
    this.supplierService.getSuppliers({ search: q || undefined, pageSize: 20 }).subscribe({
      next: (res) => { this.supplierSuggestions = res.success ? res.result.data : []; },
      error: () => { this.supplierSuggestions = []; }
    });
  }

  onSupplierSelect(event: any) {
    const s: SupplierListItemModel = event.value ?? event;
    if (s && typeof s === 'object') {
      this.selectedSupplierId = s.uuid;
      this.selectedSupplierName = s.supplierName;
      this.lineInputs = [];
      this.loadOutstandingInvoices(s.uuid);
    }
  }

  onSupplierClear() {
    this.selectedSupplierId = '';
    this.selectedSupplierName = '';
    this.outstandingInvoices = [];
    this.lineInputs = [];
  }

  // ── Outstanding invoices ────────────────────────────────────────────────

  loadOutstandingInvoices(supplierId: string, preselectInvoiceUuid?: string) {
    this.isLoadingInvoices = true;
    this.financeService.getOutstandingInvoices(supplierId).subscribe({
      next: (res) => {
        this.isLoadingInvoices = false;
        this.outstandingInvoices = res.success ? res.result : [];
        if (preselectInvoiceUuid) {
          const match = this.outstandingInvoices.find(i => i.invoiceUuid === preselectInvoiceUuid);
          if (match) this.addLine(match.invoiceUuid);
        }
      },
      error: () => { this.isLoadingInvoices = false; this.outstandingInvoices = []; }
    });
  }

  // ── Lines ────────────────────────────────────────────────────────────────

  addLine(invoiceUuid: string) {
    const inv = this.outstandingInvoices.find(i => i.invoiceUuid === invoiceUuid);
    if (!inv) return;
    this.lineInputs.push({
      invoiceUuid: inv.invoiceUuid,
      invoiceNumber: inv.invoiceNumber,
      outstandingAmount: inv.outstandingAmount,
      allocatedAmount: inv.outstandingAmount
    });
    this.selectedInvoiceToAdd = '';
  }

  removeLine(index: number) {
    this.lineInputs.splice(index, 1);
  }

  // ── Submit ───────────────────────────────────────────────────────────────

  save() {
    if (!this.selectedSupplierId) {
      this.messageService.add({ severity: 'warn', summary: 'Validation', detail: 'Supplier is required.' }); return;
    }
    if (!this.paymentDateVal) {
      this.messageService.add({ severity: 'warn', summary: 'Validation', detail: 'Payment date is required.' }); return;
    }
    if (this.showBankField && !this.bankAccount.trim()) {
      this.messageService.add({ severity: 'warn', summary: 'Validation', detail: 'Bank account is required for this method.' }); return;
    }
    if (this.showChequeFields && (!this.chequeNo.trim() || !this.chequeDateVal)) {
      this.messageService.add({ severity: 'warn', summary: 'Validation', detail: 'Cheque number and cheque date are required.' }); return;
    }
    if (this.totalAmount <= 0) {
      this.messageService.add({ severity: 'warn', summary: 'Validation', detail: 'Total amount must be greater than zero.' }); return;
    }
    for (const l of this.lineInputs) {
      if (l.allocatedAmount > l.outstandingAmount) {
        this.messageService.add({ severity: 'warn', summary: 'Validation', detail: `Allocation for ${l.invoiceNumber} exceeds its outstanding amount.` });
        return;
      }
    }

    const req: CreateSupplierPaymentRequest = {
      supplierId: this.selectedSupplierId,
      supplierName: this.selectedSupplierName,
      paymentDate: this.paymentDateVal.toISOString(),
      paymentMethod: this.paymentMethod,
      totalAmount: this.totalAmount,
      bankAccount: this.showBankField ? this.bankAccount.trim() : undefined,
      chequeNo: this.showChequeFields ? this.chequeNo.trim() : undefined,
      chequeDate: this.showChequeFields && this.chequeDateVal ? this.chequeDateVal.toISOString() : undefined,
      notes: this.notes.trim() || undefined,
      paymentType: this.paymentType,
      paymentUuid: this.paymentUuid,
      lines: this.lineInputs.map(l => ({
        invoiceUuid: l.invoiceUuid,
        allocatedAmount: l.allocatedAmount,
        notes: l.notes
      }))
    };

    this.isSaving = true;
    this.financeService.createSupplierPayment(req).subscribe({
      next: (res) => {
        this.isSaving = false;
        this.router.navigate(['/portal/pages/finance/payments', res.result]);
      },
      error: (err) => {
        this.isSaving = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message || 'Failed to record payment.' });
      }
    });
  }
}
