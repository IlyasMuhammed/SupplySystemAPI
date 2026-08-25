import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule, Router } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { DropdownModule } from 'primeng/dropdown';
import { InputNumberModule } from 'primeng/inputnumber';
import { CalendarModule } from 'primeng/calendar';
import { ToastModule } from 'primeng/toast';
import { CardModule } from 'primeng/card';
import { TextareaModule } from 'primeng/textarea';
import { TagModule } from 'primeng/tag';
import { DividerModule } from 'primeng/divider';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';
import {
  FinanceService, CreateInvoiceRequest, InvoiceLineRequest
} from '../../../../services/finance.service';
import { DemandService, PoDetailModel } from '../../../../services/demand.service';
import { SupplierService } from '../../../../services/supplier.service';
import { WarehouseService, GrnListItemModel, GrnDetailModel } from '../../../../services/warehouse.service';
import { AttachmentListComponent } from '../../../../shared/attachment-list/attachment-list.component';

export interface InvoiceLineInput {
  poLineUuid:      string;
  grnLineUuid?:    string;
  itemDescription: string;
  unitOfMeasure?:  string;
  qtyInvoiced:     number;
  unitPrice:       number;
  lineTotal:       number;
  // display-only helpers
  qtyAccepted?:    number;
  qtyOrdered?:     number;
}

@Component({
  selector: 'app-invoice-create',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, InputTextModule, DropdownModule,
    InputNumberModule, CalendarModule, ToastModule,
    CardModule, TextareaModule, TagModule,
    DividerModule, TooltipModule, AttachmentListComponent
  ],
  templateUrl: './invoice-create.component.html',
  styleUrls: ['./invoice-create.component.scss'],
  providers: [MessageService]
})
export class InvoiceCreateComponent implements OnInit {

  // Generated once per page load so attachments uploaded before save can be linked to the
  // eventual Invoice (which is created with this same UUID on submit).
  readonly invoiceUuid = crypto.randomUUID();

  // ── Header form ────────────────────────────────────────────────────────────
  form: CreateInvoiceRequest = {
    supplierId: '', poUuid: '',
    invoiceDate: '', receivedDate: '', dueDate: '',
    currency: 'PKR', subtotal: 0, taxAmount: 0,
    invoiceUuid: this.invoiceUuid
  };
  invoiceDateVal:  Date | null = null;
  receivedDateVal: Date | null = null;
  dueDateVal:      Date | null = null;
  isSaving = false;

  // ── Invoice line state ─────────────────────────────────────────────────────
  invoiceLines: InvoiceLineInput[] = [];
  loadingLines  = false;
  selectedGrnForFetch: string | null = null;

  // ── Dropdown option lists ──────────────────────────────────────────────────
  supplierOptions:   { label: string; value: string }[] = [];
  poOptions:         { label: string; value: string; status?: string }[] = [];
  allGrns:           GrnListItemModel[] = [];
  grnMatchingOptions: { label: string; value: string }[] = [];  // for 3-way match field (includes "No GRN")
  grnFetchOptions:    { label: string; value: string }[] = [];  // for fetch dropdown (only real GRNs)
  selectedPoStatus = '';

  currencyOptions = [
    { label: 'PKR', value: 'PKR' },
    { label: 'USD', value: 'USD' },
    { label: 'EUR', value: 'EUR' },
    { label: 'GBP', value: 'GBP' }
  ];
  paymentMethodOptions = [
    { label: 'Bank Transfer', value: 'Bank Transfer' },
    { label: 'Cheque',        value: 'Cheque' },
    { label: 'Cash',          value: 'Cash' },
    { label: 'Online',        value: 'Online' }
  ];

  // ── Computed totals ────────────────────────────────────────────────────────
  get linesSubtotal(): number {
    return this.invoiceLines.reduce((s, l) => s + (l.lineTotal || 0), 0);
  }
  get totalAmount(): number {
    const sub = this.invoiceLines.length > 0 ? this.linesSubtotal : (this.form.subtotal || 0);
    return sub + (this.form.taxAmount || 0);
  }

  constructor(
    private financeService: FinanceService,
    private demandService: DemandService,
    private supplierService: SupplierService,
    private warehouseService: WarehouseService,
    private messageService: MessageService,
    private router: Router
  ) {}

  ngOnInit() {
    this.supplierService.getSuppliers({ status: 'ACTIVE', pageSize: 200 }).subscribe(res => {
      if (res.success && res.result)
        this.supplierOptions = res.result.data.map(s => ({ label: s.supplierName, value: s.uuid }));
    });
    this.demandService.getPos({ page: 1, pageSize: 200 }).subscribe(res => {
      if (res.success && res.result)
        this.poOptions = res.result.data.map((p: any) => ({
          label: `${p.poNumber} — ${p.supplierName} [${p.status}]`,
          value: p.uuid,
          status: p.status
        }));
    });
    this.warehouseService.getGrns({ page: 1, pageSize: 500 }).subscribe(res => {
      if (res.success && res.result) {
        this.allGrns = res.result.data.filter((g: GrnListItemModel) => g.status === 'APPROVED');
        this.buildGrnOptions();
      }
    });
  }

  // ── PO selection ──────────────────────────────────────────────────────────

  onPoChange() {
    const sel = this.poOptions.find(p => p.value === this.form.poUuid);
    this.selectedPoStatus = sel?.status ?? '';
    this.form.grnUuid     = undefined;
    this.invoiceLines     = [];
    this.buildGrnOptions();
  }

  private buildGrnOptions() {
    const filtered = this.form.poUuid
      ? this.allGrns.filter(g => g.poUuid === this.form.poUuid)
      : this.allGrns;
    const mapped = filtered.map(g => ({
      label: `${g.grnNumber}${g.isPartialReceipt ? ' [Partial]' : ''} — ${new Date(g.receivedAt).toLocaleDateString()}`,
      value: g.uuid
    }));
    this.grnMatchingOptions = [
      { label: '— No GRN (amount-only matching) —', value: '' },
      ...mapped
    ];
    this.grnFetchOptions = mapped;
    // Auto-select the first available GRN for the fetch dropdown
    this.selectedGrnForFetch = mapped.length > 0 ? mapped[0].value : null;
  }

  // ── Fetch lines from GRN ──────────────────────────────────────────────────

  fetchFromGrn() {
    const grnUuid = this.selectedGrnForFetch;
    if (!grnUuid) {
      this.messageService.add({ severity: 'warn', summary: 'Select a GRN', detail: 'Choose an approved GRN to fetch items from.' });
      return;
    }
    this.loadingLines = true;
    this.warehouseService.getGrnById(grnUuid).subscribe({
      next: (res) => {
        this.loadingLines = false;
        const grn: GrnDetailModel = res.result!;
        this.invoiceLines = grn.lines.map(l => ({
          poLineUuid:      l.poLineUuid,
          grnLineUuid:     l.uuid,
          itemDescription: l.itemDescription,
          unitOfMeasure:   l.unitOfMeasure,
          qtyInvoiced:     l.qtyAccepted,
          unitPrice:       l.unitCost ?? 0,
          lineTotal:       l.qtyAccepted * (l.unitCost ?? 0),
          qtyAccepted:     l.qtyAccepted,
          qtyOrdered:      l.qtyOrdered
        }));
        if (!this.form.grnUuid) this.form.grnUuid = grnUuid;
        this.messageService.add({ severity: 'success', summary: 'Loaded', detail: `${this.invoiceLines.length} line(s) loaded from GRN.` });
      },
      error: () => {
        this.loadingLines = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Could not load GRN details.' });
      }
    });
  }

  // ── Fetch lines from PO ───────────────────────────────────────────────────

  fetchFromPo() {
    if (!this.form.poUuid) {
      this.messageService.add({ severity: 'warn', summary: 'Select a PO', detail: 'Choose a purchase order first.' });
      return;
    }
    this.loadingLines = true;
    this.demandService.getPoById(this.form.poUuid).subscribe({
      next: (res) => {
        this.loadingLines = false;
        const po: PoDetailModel = res.result!;
        this.invoiceLines = po.lines.map(l => {
          const pendingInv = (l.qtyPendingInvoice ?? 0) > 0 ? l.qtyPendingInvoice! : (l.qtyReceived ?? 0);
          const defaultQty = pendingInv > 0 ? pendingInv : l.quantity;
          return {
            poLineUuid:      l.uuid,
            grnLineUuid:     undefined,
            itemDescription: l.itemDescription,
            unitOfMeasure:   l.unitOfMeasure,
            qtyInvoiced:     defaultQty,
            unitPrice:       l.unitPrice,
            lineTotal:       defaultQty * l.unitPrice,
            qtyAccepted:     l.qtyReceived ?? l.quantity,
            qtyOrdered:      l.quantity
          };
        });
        this.messageService.add({ severity: 'success', summary: 'Loaded', detail: `${this.invoiceLines.length} line(s) loaded from PO.` });
      },
      error: () => {
        this.loadingLines = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Could not load PO details.' });
      }
    });
  }

  // ── Line editing ──────────────────────────────────────────────────────────

  onLineQtyChange(line: InvoiceLineInput) {
    line.lineTotal = (line.qtyInvoiced || 0) * (line.unitPrice || 0);
  }

  onLinePriceChange(line: InvoiceLineInput) {
    line.lineTotal = (line.qtyInvoiced || 0) * (line.unitPrice || 0);
  }

  removeLine(i: number) {
    this.invoiceLines.splice(i, 1);
  }

  clearLines() {
    this.invoiceLines = [];
  }

  // ── Save ──────────────────────────────────────────────────────────────────

  save() {
    if (!this.form.supplierId) {
      this.messageService.add({ severity: 'warn', summary: 'Validation', detail: 'Supplier is required.' }); return;
    }
    if (!this.form.poUuid) {
      this.messageService.add({ severity: 'warn', summary: 'Validation', detail: 'Purchase order is required.' }); return;
    }
    if (!this.invoiceDateVal || !this.receivedDateVal || !this.dueDateVal) {
      this.messageService.add({ severity: 'warn', summary: 'Validation', detail: 'All dates are required.' }); return;
    }
    if (this.invoiceLines.length === 0 && (this.form.subtotal || 0) <= 0) {
      this.messageService.add({ severity: 'warn', summary: 'Validation', detail: 'Enter a subtotal or add invoice lines.' }); return;
    }

    this.form.invoiceDate  = this.invoiceDateVal.toISOString();
    this.form.receivedDate = this.receivedDateVal.toISOString();
    this.form.dueDate      = this.dueDateVal.toISOString();
    if (!this.form.grnUuid) delete (this.form as any).grnUuid;

    if (this.invoiceLines.length > 0) {
      this.form.lines = this.invoiceLines.map(l => ({
        grnLineUuid:     l.grnLineUuid || undefined,
        poLineUuid:      l.poLineUuid,
        itemDescription: l.itemDescription,
        unitOfMeasure:   l.unitOfMeasure,
        qtyInvoiced:     l.qtyInvoiced,
        unitPrice:       l.unitPrice
      } as InvoiceLineRequest));
    } else {
      delete (this.form as any).lines;
    }

    this.isSaving = true;
    this.financeService.createInvoice(this.form).subscribe({
      next: (res) => {
        this.isSaving = false;
        this.router.navigate(['/portal/pages/finance/invoices', res.result]);
      },
      error: (err) => {
        this.isSaving = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message || 'Failed to create invoice.' });
      }
    });
  }
}
