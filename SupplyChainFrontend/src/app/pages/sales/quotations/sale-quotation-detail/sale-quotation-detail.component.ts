import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TableModule } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';
import { ToastModule } from 'primeng/toast';
import { DialogModule } from 'primeng/dialog';
import { DropdownModule } from 'primeng/dropdown';
import { CalendarModule } from 'primeng/calendar';
import { InputTextModule } from 'primeng/inputtext';
import { SelectButtonModule } from 'primeng/selectbutton';
import { TabViewModule } from 'primeng/tabview';
import { MessageService } from 'primeng/api';

import {
  SalesPreorderService, SaleQuotation, SaleQuotationLine, SaleQuotationAction, ConvertQuotationToOrderRequest,
  SALES_ATTACHMENT_CODES
} from '../../../../services/sales-preorder.service';
import { SaleOrderService } from '../../../../services/sale-order.service';
import { AddressService } from '../../../../services/address.service';
import { AddressModel } from '../../../../services/logistics.service';
import { AuthService } from '../../../service/auth.service';
import { AttachmentListComponent } from '../../../../shared/attachment-list/attachment-list.component';
import { formatCode } from '../../../../shared/format-code';
import { fromDateOnly, toDateOnly } from '../../../../shared/date-only';
import { taxCodeLabel } from '../../../../shared/tax-code-label';
import {
  SALE_QUOTATION_STATUS_SEVERITY, Severity, QuotationLineRow, quotationLineRows,
  QUOTATIONS_ROUTE, INQUIRIES_ROUTE, SALE_ORDERS_ROUTE
} from '../sale-quotation.shared';
import { SaleQuotationLineEditorComponent } from '../sale-quotation-line-editor/sale-quotation-line-editor.component';
import { SaleQuotationResponsePanelComponent } from '../sale-quotation-response-panel/sale-quotation-response-panel.component';
import { DocCurrencyPanelComponent } from '../../../../shared/doc-currency/doc-currency-panel.component';
import { DocCurrencyInfo, cachedDocCurrency, hasBaseAmounts, missingRateOf, sumDifferences } from '../../../../shared/doc-currency/doc-currency';
import { MoneyPipe } from '../../../../shared/money/money.pipe';

const TAB_LINES = 1;

/** What the convert dialog asks for: the customer's PO, and where the order goes. */
export interface ConvertForm {
  customerPoReference: string;
  customerPoDate: Date | null;
  deliveryMode: string;
  shippingAddressId: string | null;
}

/**
 * A32-PC-11 / PC-14 — one sale quotation: header, Details / Lines / Terms / Attachments, the line editor
 * (DRAFT), the customer response panel (SENT), send, convert to a sale order (ACCEPTED) and copy.
 * Every action is shown only when the server's allowedActions (state) and the user's permission both allow it.
 */
@Component({
  selector: 'app-sale-quotation-detail',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    TableModule, ButtonModule, TagModule, TooltipModule, ToastModule, DialogModule, DropdownModule, CalendarModule,
    InputTextModule, SelectButtonModule, TabViewModule,
    AttachmentListComponent, SaleQuotationLineEditorComponent, SaleQuotationResponsePanelComponent,
    DocCurrencyPanelComponent, MoneyPipe
  ],
  templateUrl: './sale-quotation-detail.component.html',
  styleUrls: ['./sale-quotation-detail.component.scss'],
  providers: [MessageService]
})
export class SaleQuotationDetailComponent implements OnInit {
  readonly tabs = ['Details', 'Lines', 'Terms', 'Attachments'];
  readonly attachmentCode = SALES_ATTACHMENT_CODES.quotation;
  readonly quotationsRoute = QUOTATIONS_ROUTE;
  readonly inquiriesRoute = INQUIRIES_ROUTE;
  readonly saleOrdersRoute = SALE_ORDERS_ROUTE;

  uuid = '';
  quotation: SaleQuotation | null = null;

  // ── A35: currency, rate locked at SENT (sale base), line amounts in the base ──
  readonly moneyCode = { display: 'code' } as const;
  private readonly mapCurrency = cachedDocCurrency((q: SaleQuotation) => ({
    currencyId: q.currencyId, currencyCode: q.currencyCode, exchangeRate: q.exchangeRate,
    baseCurrencyId: q.baseCurrencyId, baseCurrencyCode: q.baseCurrencyCode, rateLockedAt: q.rateLockedAt
  }));
  get currencyInfo(): DocCurrencyInfo | null { return this.mapCurrency(this.quotation); }
  /** Locked, and the base is another currency: line totals in the base get a column. */
  get showBase(): boolean { return hasBaseAmounts(this.currencyInfo); }
  get baseCurrencyRef(): string | null { return this.quotation?.baseCurrencyId || this.quotation?.baseCurrencyCode || null; }
  /** The quotation has no header base total (contract §6): it is the sum of the line base totals (D-13). */
  get baseGrandTotal(): number | null {
    return this.showBase ? sumDifferences((this.quotation?.lines ?? []).map(l => l.lineTotalBase)) : null;
  }
  rows: QuotationLineRow[] = [];
  isLoading = true;
  notFound = false;
  activeTab = TAB_LINES;

  // ── Lines (DRAFT) ───────────────────────────────────────────────────────────
  lineDialogVisible = false;
  editingLine: SaleQuotationLine | null = null;
  lineToDelete: SaleQuotationLine | null = null;
  isDeleting = false;

  // ── Send ────────────────────────────────────────────────────────────────────
  sendDialogVisible = false;
  isSending = false;

  // ── Convert ─────────────────────────────────────────────────────────────────
  convertDialogVisible = false;
  isConverting = false;
  convertError: string | null = null;
  convert: ConvertForm = this.blankConvert();
  addressOptions: { label: string; value: string }[] = [];
  isLoadingAddresses = false;
  modeOptions = [
    { label: 'Ship to the customer', value: 'SHIP' },
    { label: 'Customer collects',    value: 'SELF_PICKUP' }
  ];

  isCopying = false;

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private salesPreorderService: SalesPreorderService,
    private saleOrderService: SaleOrderService,
    private addressService: AddressService,
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

    this.salesPreorderService.getQuotation(this.uuid).subscribe({
      next: (res) => {
        this.isLoading = false;
        this.quotation = res.success && res.result ? res.result : null;
        this.notFound = !this.quotation;
        this.rows = this.quotation ? quotationLineRows(this.quotation.lines) : [];
      },
      error: (err) => {
        this.isLoading = false;
        this.quotation = null;
        this.rows = [];
        // Another organization's quotation is a 404, as is a bad link.
        this.notFound = err?.status === 404;
        if (!this.notFound) {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load the sale quotation.' });
        }
      }
    });
  }

  // ── What may be done ────────────────────────────────────────────────────────

  private allows(action: SaleQuotationAction): boolean {
    return (this.quotation?.allowedActions ?? []).includes(action);
  }

  private can(code: string): boolean { return this.authService.hasPermission(code); }

  /** Lines and header change only on a draft (BR-C2-05), for someone who may edit. */
  get canEdit(): boolean {
    const q = this.quotation;
    return !!q && q.status === 'DRAFT' && q.isEditable && this.can('SALE_QUOTATION_EDIT');
  }

  get canSend(): boolean {
    return this.quotation?.status === 'DRAFT' && this.allows('SEND') && this.can('SALE_QUOTATION_SEND');
  }

  get canRecordResponses(): boolean {
    return this.quotation?.status === 'SENT' && this.allows('RECORD_RESPONSE') && this.can('SALE_QUOTATION_EDIT');
  }

  get canConvert(): boolean {
    return this.quotation?.status === 'ACCEPTED' && this.allows('CONVERT') && this.can('SALE_ORDER_CREATE');
  }

  get canCopy(): boolean {
    return this.allows('COPY') && this.can('SALE_QUOTATION_CREATE');
  }

  // ── Display ─────────────────────────────────────────────────────────────────

  /** "2026-10-31T00:00:00" is the 31st, wherever the reader is. */
  day(value?: string | null): Date | null {
    return value ? fromDateOnly(value) : null;
  }

  formatStatus(code?: string | null): string { return formatCode(code); }

  getStatusSeverity(status: string): Severity {
    return SALE_QUOTATION_STATUS_SEVERITY[status as keyof typeof SALE_QUOTATION_STATUS_SEVERITY] ?? 'secondary';
  }

  typeBadge(line: SaleQuotationLine): string {
    return line.lineType === 'REJECTED' ? '✕ REJ' : line.lineType === 'ALTERNATIVE' ? '◇ ALT' : 'NORMAL';
  }

  taxText(line: SaleQuotationLine): string {
    return line.taxCode ? taxCodeLabel(line.taxCode, line.taxPercent) : `${Number(line.taxPercent.toFixed(2))}%`;
  }

  alternativeText(line: SaleQuotationLine): string {
    const number = line.alternativeForLineNumber ?? this.quotation?.lines.find(l => l.uuid === line.alternativeForLineUuid)?.lineNumber;
    const base = number != null ? `Alternative for line ${number}` : 'Alternative';
    return line.alternativeNotes ? `${base} (${line.alternativeNotes})` : base;
  }

  // ── Lines (A32-PC-12 host) ──────────────────────────────────────────────────

  openAddLine() {
    if (!this.canEdit) return;
    this.editingLine = null;
    this.lineDialogVisible = true;
  }

  openEditLine(line: SaleQuotationLine) {
    if (!this.canEdit) return;
    this.editingLine = line;
    this.lineDialogVisible = true;
  }

  onLineSaved() {
    const wasEdit = !!this.editingLine;
    this.lineDialogVisible = false;
    this.editingLine = null;
    this.messageService.add({ severity: 'success', summary: wasEdit ? 'Line saved' : 'Line added' });
    this.load();
  }

  /** The server refuses to delete a rejected line that alternatives point at; say so before trying. */
  deleteBlockedReason(line: SaleQuotationLine): string | null {
    const hasAlternatives = line.lineType === 'REJECTED'
      && (this.quotation?.lines ?? []).some(l => l.alternativeForLineUuid === line.uuid);
    return hasAlternatives ? 'Remove its alternatives first: they are offered in its place.' : null;
  }

  deleteLine(line: SaleQuotationLine) {
    if (!this.canEdit || this.deleteBlockedReason(line)) return;
    this.lineToDelete = line;
  }

  confirmDeleteLine() {
    const line = this.lineToDelete;
    if (!line || !this.canEdit || this.isDeleting) return;
    this.isDeleting = true;
    this.salesPreorderService.deleteQuotationLine(this.uuid, line.uuid).subscribe({
      next: () => {
        this.isDeleting = false;
        this.lineToDelete = null;
        this.messageService.add({ severity: 'success', summary: 'Line removed' });
        this.load();
      },
      error: (err) => {
        this.isDeleting = false;
        this.lineToDelete = null;
        this.messageService.add({ severity: 'error', summary: 'Not removed', detail: err?.error?.message ?? 'The line could not be removed.' });
      }
    });
  }

  // ── Send (A32-PC-14) ────────────────────────────────────────────────────────

  openSendDialog() {
    if (this.canSend) this.sendDialogVisible = true;
  }

  confirmSend() {
    if (!this.canSend || this.isSending) return;
    this.isSending = true;
    this.salesPreorderService.sendQuotation(this.uuid).subscribe({
      next: () => {
        this.isSending = false;
        this.sendDialogVisible = false;
        this.messageService.add({ severity: 'success', summary: 'Sent', detail: 'The quotation is now awaiting the customer\'s response.' });
        this.load();
      },
      error: (err) => {
        this.isSending = false;
        this.sendDialogVisible = false;
        // A35 D-5 — sending locks the rate; the quotation's currency has none on the sent date.
        const missing = missingRateOf(err?.error?.message);
        if (missing) {
          this.messageService.add({ severity: 'error', summary: 'No exchange rate', detail: missing.message, life: 10000 });
          return;
        }
        this.messageService.add({ severity: 'error', summary: 'Not sent', detail: err?.error?.message ?? 'The quotation could not be sent.' });
      }
    });
  }

  // ── Convert (A32-PC-14) ─────────────────────────────────────────────────────

  private blankConvert(): ConvertForm {
    return { customerPoReference: '', customerPoDate: null, deliveryMode: 'SHIP', shippingAddressId: null };
  }

  openConvertDialog() {
    const q = this.quotation;
    if (!q || !this.canConvert) return;
    this.convert = this.blankConvert();
    this.convertError = null;
    this.convertDialogVisible = true;

    // Where the order starts, as on a new sale order; without the answer it ships, and the server has the last word.
    this.saleOrderService.getDefaults().subscribe({
      next: (res) => {
        const d = res.result;
        if (!d) return;
        this.modeOptions = this.modeOptions.map(o => ({ ...o, disabled: o.value === 'SELF_PICKUP' && !d.selfPickupEnabled }));
        this.convert.deliveryMode = d.selfPickupEnabled ? d.deliveryMode : 'SHIP';
      },
      error: () => { /* keep SHIP */ }
    });

    this.isLoadingAddresses = true;
    this.addressService.getAddresses(q.partnerId).subscribe({
      next: (res) => {
        this.isLoadingAddresses = false;
        const addresses = res.result ?? [];
        this.addressOptions = addresses.map(a => ({ label: this.formatAddress(a), value: a.uuid }));
        if (addresses.length === 1 && !this.convert.shippingAddressId) this.convert.shippingAddressId = addresses[0].uuid;
      },
      error: () => { this.isLoadingAddresses = false; this.addressOptions = []; }
    });
  }

  private formatAddress(a: AddressModel): string {
    return [a.line1, a.line2, a.cityName, a.countryName].filter(p => !!p && `${p}`.trim()).join(', ');
  }

  /** A shipped order needs an address; a customer PO reference is at most 50 characters. */
  get canConfirmConvert(): boolean {
    const c = this.convert;
    return !this.isConverting
      && (c.deliveryMode !== 'SHIP' || !!c.shippingAddressId)
      && c.customerPoReference.trim().length <= 50;
  }

  confirmConvert() {
    if (!this.canConvert || !this.canConfirmConvert) return;
    const c = this.convert;
    const req: ConvertQuotationToOrderRequest = {
      deliveryMode: c.deliveryMode,
      shippingAddressId: c.deliveryMode === 'SHIP' ? c.shippingAddressId : null,
      customerPoReference: c.customerPoReference.trim() || null,
      customerPoDate: c.customerPoDate ? toDateOnly(c.customerPoDate) : null
    };

    this.isConverting = true;
    this.convertError = null;
    this.salesPreorderService.convertQuotationToOrder(this.uuid, req).subscribe({
      next: (res) => {
        this.isConverting = false;
        this.convertDialogVisible = false;
        // A duplicate customer PO is allowed but warned about (BR-C3-05): the server says so in the message.
        if (res.message) this.messageService.add({ severity: 'info', summary: 'Sale order created', detail: res.message });
        this.router.navigate([SALE_ORDERS_ROUTE, res.result]);
      },
      error: (err) => {
        this.isConverting = false;
        this.convertError = err?.error?.message ?? 'The quotation could not be converted.';
      }
    });
  }

  /** FE-SO's sale order form, preloaded with this quotation, for the order's other fields. */
  get fullConvertLink(): { path: string[]; query: Record<string, string> } {
    return { path: [SALE_ORDERS_ROUTE, 'new'], query: { quotation: this.uuid } };
  }

  // ── Copy ────────────────────────────────────────────────────────────────────

  /** A sent quotation cannot change: changes go on a new draft copied from it (§4.4). */
  copy() {
    if (!this.canCopy || this.isCopying) return;
    this.isCopying = true;
    this.salesPreorderService.copyQuotation(this.uuid).subscribe({
      next: (res) => {
        this.isCopying = false;
        const copyUuid = res.result as string;
        this.router.navigate([QUOTATIONS_ROUTE, copyUuid]).then(() => {
          // The same page shows the copy: the router reuses this component for another uuid.
          this.uuid = copyUuid;
          this.activeTab = TAB_LINES;
          this.load();
        });
      },
      error: (err) => {
        this.isCopying = false;
        this.messageService.add({ severity: 'error', summary: 'Not copied', detail: err?.error?.message ?? 'The quotation could not be copied.' });
      }
    });
  }
}
