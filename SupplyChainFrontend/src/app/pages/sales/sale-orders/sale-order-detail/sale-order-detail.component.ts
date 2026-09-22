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
import { CheckboxModule } from 'primeng/checkbox';
import { InputNumberModule } from 'primeng/inputnumber';
import { TextareaModule } from 'primeng/textarea';
import { TabViewModule, TabViewChangeEvent } from 'primeng/tabview';
import { MessageService } from 'primeng/api';
import { forkJoin, of } from 'rxjs';
import { catchError } from 'rxjs/operators';

import { TimelinePanelComponent } from '../../../../shared/timeline-panel/timeline-panel.component';
import {
  SaleOrderService, SaleOrderModel, SaleOrderLineModel, SaleOrderLineAvailabilityModel
} from '../../../../services/sale-order.service';
import { BusinessPartnerService } from '../../../../services/business-partner.service';
import {
  SalesInvoiceService, SalesInvoiceListItemModel, SalesInvoiceDetailModel, SalesInvoicePaymentModel
} from '../../../../services/sales-invoice.service';
import { AddressService } from '../../../../services/address.service';
import { AddressModel, DeliveryListItemModel, SourceLineSelection } from '../../../../services/logistics.service';
import { AuthService } from '../../../service/auth.service';
import { DELIVERY_STATUS_SEVERITY } from '../../../logistics/deliveries/delivery-list/delivery-list.component';
import { SALE_ORDER_STATUS_SEVERITY, formatCode } from '../sale-order-list/sale-order-list.component';
import { INVOICE_STATUS_SEVERITY } from '../../../finance/receivables/receivables.shared';

type Severity = 'success' | 'info' | 'warn' | 'danger' | 'secondary' | 'contrast';

/** One row of the create-delivery dialog: a line the order can still send, and how much of it. */
export interface DeliveryLineChoice {
  line: SaleOrderLineModel;
  outstanding: number;
  include: boolean;
  qty: number;
}

/** One line of the confirm dialog: what confirming would find for it, named. */
export interface AvailabilityRow extends SaleOrderLineAvailabilityModel {
  description: string;
}

/** A payment applied to one of the order's invoices, with the invoice it went to. */
export interface OrderPaymentRow extends SalesInvoicePaymentModel {
  invoiceNumber: string;
  currencyCode: string;
}

/** What the order's live invoices come to in one currency. */
export interface InvoiceTotal {
  currencyCode: string;
  invoiced: number;
  paid: number;
  balance: number;
}

/** Statuses in which a sale order still has goods to send. Mirrors the server's rule. */
const DELIVERABLE_ORDER_STATUSES = ['CONFIRMED', 'PARTIALLY_FULFILLED'];

/** Statuses an order can no longer be cancelled from. Mirrors the server's rule. */
const UNCANCELLABLE_ORDER_STATUSES = ['CANCELLED', 'CLOSED', 'INVOICED'];

/** Invoices that stand: issued and not since cancelled or credited. A draft has billed nothing. */
const LIVE_INVOICE_STATUSES = ['ISSUED', 'PARTIALLY_PAID', 'PAID', 'OVERDUE'];

const TAB_INVOICES = 2;
const TAB_PAYMENTS = 3;

@Component({
  selector: 'app-sale-order-detail',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    TableModule, ButtonModule, TagModule, TooltipModule, ToastModule,
    DialogModule, DropdownModule, CheckboxModule, InputNumberModule, TextareaModule, TabViewModule,
    TimelinePanelComponent
  ],
  templateUrl: './sale-order-detail.component.html',
  styleUrls: ['./sale-order-detail.component.scss'],
  providers: [MessageService]
})
export class SaleOrderDetailComponent implements OnInit {
  uuid = '';
  order: SaleOrderModel | null = null;
  partnerName: string | null = null;
  shipTo: AddressModel | null = null;
  isLoading = true;
  notFound = false;

  activeTab = 0;

  deliveries: DeliveryListItemModel[] = [];
  isLoadingDeliveries = false;

  // Invoices and payments come from the same two calls, made when either tab is first opened.
  invoices: SalesInvoiceListItemModel[] = [];
  payments: OrderPaymentRow[] = [];
  invoiceTotals: InvoiceTotal[] = [];
  isLoadingInvoices = false;
  invoicesFailed = false;
  private invoicesRequested = false;

  // ── Create delivery ─────────────────────────────────────────────────────────

  createDialogVisible = false;
  isSubmitting = false;
  deliveryMode = 'SHIP';
  deliveryNotes = '';
  choices: DeliveryLineChoice[] = [];

  modeOptions = [
    { label: 'Ship to the customer',   value: 'SHIP' },
    { label: 'Customer collects',      value: 'SELF_PICKUP' }
  ];

  // ── Confirm and cancel ──────────────────────────────────────────────────────

  confirmDialogVisible = false;
  availability: AvailabilityRow[] = [];
  isLoadingAvailability = false;
  availabilityFailed = false;
  isConfirming = false;

  cancelDialogVisible = false;
  cancelReason = '';
  isCancelling = false;

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private saleOrderService: SaleOrderService,
    private partnerService: BusinessPartnerService,
    private invoiceService: SalesInvoiceService,
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

    this.saleOrderService.getSaleOrderById(this.uuid).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success && res.result) {
          this.order = res.result;
          this.notFound = false;
          this.loadPartnerName(res.result.partnerId);
          this.loadShipTo(res.result);
          this.loadDeliveries();
          // Whatever the order did just now may have changed what has been billed.
          this.invoicesRequested = false;
          if (this.activeTab === TAB_INVOICES || this.activeTab === TAB_PAYMENTS) this.ensureInvoicesLoaded();
        } else {
          this.order = null;
          this.notFound = true;
        }
      },
      error: (err) => {
        this.isLoading = false;
        this.order = null;
        this.notFound = err?.status === 404;
        if (!this.notFound) {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load the sale order.' });
        }
      }
    });
  }

  /** A name is nicer than an id, but the order must render without one. */
  private loadPartnerName(partnerId: string) {
    this.partnerName = null;
    if (!partnerId) return;
    this.partnerService.getPartnerById(partnerId).subscribe({
      next: (res) => { this.partnerName = res.result?.companyName ?? null; },
      error: () => { this.partnerName = null; }
    });
  }

  /** Where it ships to, in words. Best effort: the order renders without it. */
  private loadShipTo(order: SaleOrderModel) {
    this.shipTo = null;
    if (order.deliveryMode !== 'SHIP' || !order.shippingAddressId) return;
    this.addressService.getAddress(order.shippingAddressId).subscribe({
      next: (res) => { this.shipTo = res.result ?? null; },
      error: () => { this.shipTo = null; }
    });
  }

  loadDeliveries() {
    if (!this.canViewDeliveries) { this.deliveries = []; return; }

    this.isLoadingDeliveries = true;
    this.saleOrderService.getDeliveries(this.uuid).subscribe({
      next: (res) => {
        this.isLoadingDeliveries = false;
        this.deliveries = res.result ?? [];
      },
      error: () => {
        this.isLoadingDeliveries = false;
        this.deliveries = [];
        this.messageService.add({ severity: 'error', summary: 'Error', detail: "Failed to load the order's deliveries." });
      }
    });
  }

  // ── Tabs ────────────────────────────────────────────────────────────────────

  onTabChange(event: TabViewChangeEvent) {
    this.activeTab = event.index;
    if (event.index === TAB_INVOICES || event.index === TAB_PAYMENTS) this.ensureInvoicesLoaded();
  }

  /** Loads the order's invoices, then each one's payments, once. */
  ensureInvoicesLoaded() {
    if (!this.canViewInvoices || this.invoicesRequested) return;
    this.invoicesRequested = true;
    this.isLoadingInvoices = true;
    this.invoicesFailed = false;

    this.invoiceService.getInvoices({ saleOrderUuid: this.uuid, pageSize: 100 }).subscribe({
      next: (res) => {
        this.invoices = res.result?.data ?? [];
        this.invoiceTotals = this.totalsOf(this.invoices);

        // A draft has nothing paid against it, so it is not worth a call.
        const billed = this.invoices.filter(i => i.status !== 'DRAFT');
        if (billed.length === 0) { this.payments = []; this.isLoadingInvoices = false; return; }

        forkJoin(billed.map(i => this.invoiceService.getInvoice(i.uuid).pipe(catchError(() => of(null))))).subscribe(details => {
          this.isLoadingInvoices = false;
          const found = details.map(d => d?.result).filter((d): d is SalesInvoiceDetailModel => !!d);
          this.invoicesFailed = found.length < billed.length;
          this.payments = this.paymentsOf(found);
        });
      },
      error: () => {
        this.isLoadingInvoices = false;
        this.invoicesRequested = false;
        this.invoicesFailed = true;
        this.invoices = [];
        this.payments = [];
        this.invoiceTotals = [];
        this.messageService.add({ severity: 'error', summary: 'Error', detail: "Failed to load the order's invoices." });
      }
    });
  }

  /** What the invoices that stand come to, a currency at a time: there is no rate to add them with. */
  totalsOf(invoices: SalesInvoiceListItemModel[]): InvoiceTotal[] {
    const byCurrency = new Map<string, InvoiceTotal>();
    for (const i of invoices.filter(i => LIVE_INVOICE_STATUSES.includes(i.status))) {
      const total = byCurrency.get(i.currencyCode) ?? { currencyCode: i.currencyCode, invoiced: 0, paid: 0, balance: 0 };
      total.invoiced += i.grandTotal;
      total.paid     += i.amountPaid;
      total.balance  += i.balanceDue;
      byCurrency.set(i.currencyCode, total);
    }
    return [...byCurrency.values()].sort((a, b) => a.currencyCode.localeCompare(b.currencyCode));
  }

  /** Every payment applied to any of the invoices, oldest first. */
  paymentsOf(details: SalesInvoiceDetailModel[]): OrderPaymentRow[] {
    return details
      .flatMap(d => (d.payments ?? []).map(p => ({ ...p, invoiceNumber: d.invoiceNumber, currencyCode: d.currencyCode })))
      .sort((a, b) => a.paymentDate.localeCompare(b.paymentDate) || a.allocatedAt.localeCompare(b.allocatedAt));
  }

  downloadInvoicePdf(invoice: SalesInvoiceListItemModel) {
    this.invoiceService.downloadPdf(invoice.uuid).subscribe({
      next: (blob) => {
        const url = URL.createObjectURL(blob);
        window.open(url, '_blank');
        setTimeout(() => URL.revokeObjectURL(url), 60_000);
      },
      error: () => this.messageService.add({ severity: 'error', summary: 'Error', detail: 'The invoice PDF could not be downloaded.' })
    });
  }

  // ── What this user may do, and what the order allows ────────────────────────

  get canViewDeliveries(): boolean { return this.authService.hasPermission('DELIVERY_VIEW'); }
  get canViewInvoices(): boolean   { return this.authService.hasPermission('SALES_INVOICE_VIEW'); }
  get canViewPayments(): boolean   { return this.authService.hasPermission('CUSTOMER_PAYMENT_VIEW'); }

  get canEdit(): boolean {
    return this.order?.status === 'DRAFT' && this.authService.hasPermission('SALE_ORDER_EDIT');
  }

  get canConfirm(): boolean {
    return this.order?.status === 'DRAFT' && this.authService.hasPermission('SALE_ORDER_CONFIRM');
  }

  get canCancel(): boolean {
    return !!this.order && !UNCANCELLABLE_ORDER_STATUSES.includes(this.order.status)
        && this.authService.hasPermission('SALE_ORDER_CANCEL');
  }

  // ── Fulfilment figures ──────────────────────────────────────────────────────

  outstanding(line: SaleOrderLineModel): number {
    return Math.max(0, line.quantity - line.fulfilledQty);
  }

  /**
   * Lines a delivery can carry: not cancelled, not shipped by the vendor directly, and with
   * something still to send. The server applies the same rule; this only decides what to offer.
   */
  get deliverableLines(): SaleOrderLineModel[] {
    return (this.order?.lines ?? []).filter(l =>
      l.status !== 'CANCELLED' && l.fulfillmentMode !== 'DROP_SHIP' && this.outstanding(l) > 0);
  }

  get canCreateDelivery(): boolean {
    return !!this.order
        && DELIVERABLE_ORDER_STATUSES.includes(this.order.status)
        && this.deliverableLines.length > 0
        && this.authService.hasPermission('DELIVERY_CREATE');
  }

  get orderedQty(): number   { return (this.order?.lines ?? []).filter(l => l.status !== 'CANCELLED').reduce((s, l) => s + l.quantity, 0); }
  get fulfilledQty(): number { return (this.order?.lines ?? []).filter(l => l.status !== 'CANCELLED').reduce((s, l) => s + Math.min(l.fulfilledQty, l.quantity), 0); }
  get fulfilledPercent(): number {
    return this.orderedQty > 0 ? Math.round((this.fulfilledQty / this.orderedQty) * 100) : 0;
  }

  describe(line: SaleOrderLineModel): string {
    return line.itemDescription ?? `Variant ${line.variantUuid}`;
  }

  // ── Confirm ─────────────────────────────────────────────────────────────────

  /** Shows what confirming would find, then asks. Nothing is reserved until the order is confirmed. */
  openConfirmDialog() {
    if (!this.canConfirm) return;
    this.confirmDialogVisible = true;
    this.availability = [];
    this.availabilityFailed = false;
    this.isLoadingAvailability = true;

    this.saleOrderService.getAvailability(this.uuid).subscribe({
      next: (res) => {
        this.isLoadingAvailability = false;
        const named = new Map((this.order?.lines ?? []).map(l => [l.variantUuid, this.describe(l)]));
        this.availability = (res.result ?? []).map(a => ({ ...a, description: named.get(a.variantUuid) ?? `Variant ${a.variantUuid}` }));
      },
      error: () => {
        // The preview is a courtesy: a failure to fetch it must not stop the order being confirmed.
        this.isLoadingAvailability = false;
        this.availabilityFailed = true;
      }
    });
  }

  /** Lines the stock on hand does not cover, which confirming will raise purchase orders for. */
  get shortfalls(): AvailabilityRow[] {
    return this.availability.filter(a => a.deficitQty > 0);
  }

  confirmOrder() {
    if (!this.canConfirm || this.isConfirming) return;
    this.isConfirming = true;

    this.saleOrderService.confirmSaleOrder(this.uuid).subscribe({
      next: () => {
        this.isConfirming = false;
        this.confirmDialogVisible = false;
        this.messageService.add({ severity: 'success', summary: 'Order confirmed', detail: 'Stock has been reserved.' });
        this.load();
      },
      error: (err) => {
        this.isConfirming = false;
        this.messageService.add({
          severity: 'error', summary: 'Not confirmed',
          detail: err?.error?.message ?? 'The order could not be confirmed.'
        });
      }
    });
  }

  // ── Cancel ──────────────────────────────────────────────────────────────────

  openCancelDialog() {
    if (!this.canCancel) return;
    this.cancelReason = '';
    this.cancelDialogVisible = true;
  }

  cancelOrder() {
    if (!this.canCancel || this.isCancelling) return;
    this.isCancelling = true;

    this.saleOrderService.cancelSaleOrder(this.uuid, this.cancelReason.trim() || undefined).subscribe({
      next: () => {
        this.isCancelling = false;
        this.cancelDialogVisible = false;
        this.messageService.add({ severity: 'success', summary: 'Order cancelled', detail: 'Its reservations have been released.' });
        this.load();
      },
      error: (err) => {
        this.isCancelling = false;
        this.messageService.add({
          severity: 'error', summary: 'Not cancelled',
          detail: err?.error?.message ?? 'The order could not be cancelled.'
        });
      }
    });
  }

  // ── Create delivery ─────────────────────────────────────────────────────────

  openCreateDialog() {
    if (!this.order) return;
    this.deliveryMode  = this.order.deliveryMode || 'SHIP';
    this.deliveryNotes = '';
    // Everything outstanding, in full, by default — the common case is "send what is left".
    this.choices = this.deliverableLines.map(line => ({
      line, outstanding: this.outstanding(line), include: true, qty: this.outstanding(line)
    }));
    this.createDialogVisible = true;
  }

  get selectedChoices(): DeliveryLineChoice[] {
    return this.choices.filter(c => c.include);
  }

  get canSubmitDelivery(): boolean {
    return this.selectedChoices.length > 0
        && this.selectedChoices.every(c => c.qty > 0 && c.qty <= c.outstanding)
        && !this.isSubmitting;
  }

  /** What the request will carry — exported so the spec can pin it without a DOM walk. */
  buildRequestLines(): SourceLineSelection[] {
    return this.selectedChoices.map(c => ({ sourceLineUuid: c.line.uuid, qty: c.qty }));
  }

  submitDelivery() {
    if (!this.canSubmitDelivery) return;
    this.isSubmitting = true;

    this.saleOrderService.createDelivery(this.uuid, {
      deliveryMode: this.deliveryMode,
      notes: this.deliveryNotes.trim() || undefined,
      lines: this.buildRequestLines()
    }).subscribe({
      next: (res) => {
        this.isSubmitting = false;
        this.createDialogVisible = false;
        this.messageService.add({ severity: 'success', summary: 'Delivery created', detail: 'Opening the new delivery.' });
        if (res.result) this.router.navigate(['/portal/pages/logistics/deliveries', res.result]);
        else this.loadDeliveries();
      },
      error: (err) => {
        this.isSubmitting = false;
        this.messageService.add({
          severity: 'error', summary: 'Not allowed',
          // The server explains refusals — which line, how much is left — so show that.
          detail: err?.error?.message ?? 'The delivery could not be created.'
        });
      }
    });
  }

  // ── Display ─────────────────────────────────────────────────────────────────

  getStatusSeverity(status: string): Severity {
    return SALE_ORDER_STATUS_SEVERITY[status] ?? 'secondary';
  }

  getDeliverySeverity(status: string): Severity {
    return DELIVERY_STATUS_SEVERITY[status] ?? 'secondary';
  }

  getInvoiceSeverity(status: string): Severity {
    return INVOICE_STATUS_SEVERITY[status] ?? 'secondary';
  }

  /** A payment that came in and stood is green; one that bounced or was reversed is not. */
  getPaymentSeverity(status: string): Severity {
    const s = (status || '').toUpperCase();
    if (s.includes('BOUNC') || s.includes('REVERS') || s.includes('CANCEL')) return 'danger';
    if (s.includes('PARTIAL') || s.includes('PENDING') || s.includes('DRAFT')) return 'warn';
    return 'success';
  }

  formatStatus(code?: string | null): string { return formatCode(code); }

  /** "Plot 12, Korangi, Karachi 74900, Pakistan" */
  formatAddress(a: AddressModel): string {
    const cityLine = [a.cityName, a.postalCode].filter(Boolean).join(' ');
    return [a.line1, a.line2, cityLine, a.countryName].filter(Boolean).join(', ');
  }
}
