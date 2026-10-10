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
  FinanceService, CreateInvoiceRequest, InvoiceLineRequest, purchaseTaxFor, INVOICE_NOTES_MAX, taxCodeOptionLabel
} from '../../../../services/finance.service';
import { FinanceSetupService, TaxCodeModel } from '../../../../services/finance-setup.service';
import { CurrenciesService } from '../../../../services/currencies.service';
import { DemandService, PoDetailModel } from '../../../../services/demand.service';
import { SupplierService } from '../../../../services/supplier.service';
import { WarehouseService, GrnListItemModel, GrnDetailModel } from '../../../../services/warehouse.service';
import { TenantService } from '../../../service/tenant.service';
import { AttachmentListComponent } from '../../../../shared/attachment-list/attachment-list.component';
import { toDateOnly } from '../../../../shared/date-only';
import { FLOW } from '../../../../shared/flow';

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
    DividerModule, TooltipModule, AttachmentListComponent,
    ...FLOW
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
    currency: 'PKR', subtotal: 0, taxAmount: 0, taxCodeUuid: null,
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
  poOptions:         { label: string; value: string; status?: string; supplierId?: string }[] = [];

  /** The invoice's notes column holds this many characters; the server refuses more. */
  readonly notesMax = INVOICE_NOTES_MAX;
  allGrns:           GrnListItemModel[] = [];
  grnMatchingOptions: { label: string; value: string }[] = [];  // for 3-way match field (includes "No GRN")
  grnFetchOptions:    { label: string; value: string }[] = [];  // for fetch dropdown (only real GRNs)
  selectedPoStatus = '';

  /** From Lookups (every currency with a code), not a fixed list. PKR until they load. */
  currencyOptions: { label: string; value: string }[] = [{ label: 'PKR', value: 'PKR' }];

  /** Active codes usable on a purchase (PURCHASE or BOTH), the default first. */
  taxCodes: TaxCodeModel[] = [];
  taxCodeOptions: { label: string; value: string | null }[] = [{ label: 'No tax code — enter the amount', value: null }];

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
  get subtotal(): number {
    return this.invoiceLines.length > 0 ? this.linesSubtotal : (this.form.subtotal || 0);
  }
  /** The chosen purchase tax code, if any. */
  get selectedTaxCode(): TaxCodeModel | null {
    return this.taxCodes.find(c => c.uuid === this.form.taxCodeUuid) ?? null;
  }
  /** With a code the tax is worked out (and the field is read-only); without one it is whatever was entered. */
  get taxAmount(): number {
    const code = this.selectedTaxCode;
    return code ? purchaseTaxFor(this.subtotal, code.ratePercent) : (this.form.taxAmount || 0);
  }
  get totalAmount(): number {
    return this.subtotal + this.taxAmount;
  }

  constructor(
    private financeService: FinanceService,
    private financeSetupService: FinanceSetupService,
    private currenciesService: CurrenciesService,
    private tenantService: TenantService,
    private demandService: DemandService,
    private supplierService: SupplierService,
    private warehouseService: WarehouseService,
    private messageService: MessageService,
    private router: Router
  ) {}

  ngOnInit() {
    this.loadCurrencies();
    this.loadTaxCodes();
    this.supplierService.getSuppliers({ status: 'ACTIVE', pageSize: 200 }).subscribe(res => {
      if (res.success && res.result)
        this.supplierOptions = res.result.data.map(s => ({ label: s.supplierName, value: s.uuid }));
    });
    this.demandService.getPos({ page: 1, pageSize: 200 }).subscribe(res => {
      if (res.success && res.result)
        this.poOptions = res.result.data.map((p: any) => ({
          label: `${p.poNumber} — ${p.supplierName} [${p.status}]`,
          value: p.uuid,
          status: p.status,
          supplierId: p.supplierId
        }));
    });
    this.warehouseService.getGrns({ page: 1, pageSize: 500 }).subscribe(res => {
      if (res.success && res.result) {
        this.allGrns = res.result.data.filter((g: GrnListItemModel) => g.status === 'APPROVED');
        this.buildGrnOptions();
      }
    });
  }

  // ── Currency and tax code ─────────────────────────────────────────────────

  /** Every Lookups currency that has a code; the organization's base currency pre-selected when it is one of them. */
  private loadCurrencies() {
    this.currenciesService.getAll().subscribe({
      next: (res) => {
        const currencies = (res.result ?? []).filter(c => !!c.code?.trim());
        if (currencies.length === 0) return;

        this.currencyOptions = currencies.map(c => ({ label: `${c.code!.trim()} — ${c.name}`, value: c.code!.trim().toUpperCase() }));

        const baseId = this.tenantService.tenant()?.baseCurrency;
        const base = currencies.find(c => c.id === baseId)?.code?.trim().toUpperCase();
        const current = this.form.currency?.toUpperCase();
        if (base) this.form.currency = base;
        else if (!this.currencyOptions.some(o => o.value === current)) this.form.currency = this.currencyOptions[0].value;
      },
      error: () => this.messageService.add({ severity: 'warn', summary: 'Currencies', detail: 'The currency list could not be loaded; PKR is offered.' })
    });
  }

  /** Purchase tax codes (PURCHASE or BOTH, active); the default one pre-selected. */
  private loadTaxCodes() {
    this.financeSetupService.getTaxCodes('PURCHASE').subscribe({
      next: (res) => {
        this.taxCodes = res.result ?? [];
        this.taxCodeOptions = [
          { label: 'No tax code — enter the amount', value: null },
          ...this.taxCodes.map(c => ({ label: taxCodeOptionLabel(c.code, c.ratePercent, c.name), value: c.uuid }))
        ];
        const preferred = this.taxCodes.find(c => c.isDefault);
        if (preferred && !this.form.taxCodeUuid) this.form.taxCodeUuid = preferred.uuid;
      },
      error: () => this.messageService.add({ severity: 'warn', summary: 'Tax codes', detail: 'Tax codes could not be loaded; enter the tax as an amount.' })
    });
  }

  // ── PO selection ──────────────────────────────────────────────────────────

  /**
   * The supplier follows the purchase order: the server names the invoice after the PO's supplier but books it
   * to the supplier sent, so an invoice on another supplier's PO would bill one and name the other.
   */
  onPoChange() {
    const sel = this.poOptions.find(p => p.value === this.form.poUuid);
    this.selectedPoStatus = sel?.status ?? '';
    if (sel?.supplierId) this.form.supplierId = sel.supplierId;
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
    // The purchase order is optional (G10: a payable nobody ordered, e.g. a freight bill). When there is one it
    // must be this supplier's — the server books the invoice to the supplier sent but names the PO's.
    const po = this.form.poUuid ? this.poOptions.find(p => p.value === this.form.poUuid) : undefined;
    if (po?.supplierId && po.supplierId !== this.form.supplierId) {
      this.messageService.add({ severity: 'warn', summary: 'Validation', detail: 'The purchase order is for another supplier. Pick that supplier, or another purchase order.' }); return;
    }
    if (!this.invoiceDateVal || !this.receivedDateVal || !this.dueDateVal) {
      this.messageService.add({ severity: 'warn', summary: 'Validation', detail: 'All dates are required.' }); return;
    }
    if (this.invoiceLines.length === 0 && (this.form.subtotal || 0) <= 0) {
      this.messageService.add({ severity: 'warn', summary: 'Validation', detail: 'Enter a subtotal or add invoice lines.' }); return;
    }
    if ((this.form.notes ?? '').length > INVOICE_NOTES_MAX) {
      this.messageService.add({ severity: 'warn', summary: 'Validation', detail: `Notes can be at most ${INVOICE_NOTES_MAX} characters.` }); return;
    }

    // Built afresh, so a failed save leaves the form as it was typed (the hand-entered tax included).
    const req: CreateInvoiceRequest = {
      ...this.form,
      // The day picked, not its UTC instant: the server reads the invoice date for the exchange rate (S-5).
      invoiceDate:  toDateOnly(this.invoiceDateVal),
      receivedDate: toDateOnly(this.receivedDateVal),
      dueDate:      toDateOnly(this.dueDateVal),
      // With a code the server works the tax out itself (what is sent is ignored); this is the same figure.
      taxAmount:    this.taxAmount,
      taxCodeUuid:  this.form.taxCodeUuid || null
    };
    // No PO / no GRN is sent as nothing: '' is not a Guid, and the server would refuse the whole request.
    if (!req.poUuid) delete req.poUuid;
    if (!req.grnUuid) delete req.grnUuid;

    if (this.invoiceLines.length > 0) {
      req.lines = this.invoiceLines.map(l => ({
        grnLineUuid:     l.grnLineUuid || undefined,
        poLineUuid:      l.poLineUuid,
        itemDescription: l.itemDescription,
        unitOfMeasure:   l.unitOfMeasure,
        qtyInvoiced:     l.qtyInvoiced,
        unitPrice:       l.unitPrice
      } as InvoiceLineRequest));
    } else {
      delete req.lines;
    }

    this.isSaving = true;
    this.financeService.createInvoice(req).subscribe({
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
