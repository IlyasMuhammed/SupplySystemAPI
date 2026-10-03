import { Component, DestroyRef, Inject, LOCALE_ID, OnInit, inject } from '@angular/core';
import { CommonModule, formatNumber } from '@angular/common';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, ParamMap, Router, RouterModule } from '@angular/router';
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
import { InputTextModule } from 'primeng/inputtext';
import { CalendarModule } from 'primeng/calendar';
import { TextareaModule } from 'primeng/textarea';
import { TabViewModule, TabViewChangeEvent } from 'primeng/tabview';
import { MessageService } from 'primeng/api';
import { Observable, forkJoin, of } from 'rxjs';
import { catchError } from 'rxjs/operators';

import { TimelinePanelComponent } from '../../../../shared/timeline-panel/timeline-panel.component';
import { AttachmentListComponent } from '../../../../shared/attachment-list/attachment-list.component';
import { fromDateOnly, toDateOnly } from '../../../../shared/date-only';
import {
  SaleOrderService, SaleOrderModel, SaleOrderLineModel, SaleOrderLineAvailabilityModel, SaleOrderSourceType,
  DeliveryIndicator, CustomerPoDuplicateModel, SaleOrderLineReservationModel, ConfirmBlockerCode, ConfirmBlockerModel,
  SaleOrderDeliveryPreviewModel, SkippedSaleOrderLineModel, SaleOrderCancelResultModel,
  SaleOrderConfirmResultModel, CreatedSaleOrderDeliveryModel
} from '../../../../services/sale-order.service';
import { FulfillmentRoutesService, FulfillmentRouteModel } from '../../../../services/fulfillment-routes.service';
import {
  LineRouteDisplay, ROUTE_LEGEND, confirmBlockedSummary, inheritPlaceholder, isRouteBlocker, lineRouteDisplay,
  splitServerMessage
} from '../fulfillment-route-display';
import { DeliveryPreviewPanelComponent } from '../delivery-preview-panel/delivery-preview-panel.component';
import { SALES_ATTACHMENT_CODES } from '../../../../services/sales-preorder.service';
import { AttachmentService, AttachmentModel } from '../../../../services/attachment.service';
import { BusinessPartnerService } from '../../../../services/business-partner.service';
import {
  SalesInvoiceService, SalesInvoiceListItemModel, SalesInvoiceDetailModel, SalesInvoicePaymentModel
} from '../../../../services/sales-invoice.service';
import { AddressService } from '../../../../services/address.service';
import {
  AddressModel, DeliveryListItemModel, DeliveryLineModel, LogisticsService, SourceLineSelection
} from '../../../../services/logistics.service';
import { ProductionOrderService, ProductionOrderListItem, productionStatusSeverity } from '../../../../services/production-order.service';
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

// ── A32 C3 / C4 ───────────────────────────────────────────────────────────────

/** BR-C4-08: without the code the button stays on screen, disabled, and says why. */
export const RESERVE_PERMISSION_TOOLTIP = 'You do not have permission to reserve inventory. Contact your administrator.';
export const RELEASE_PERMISSION_TOOLTIP = 'You do not have permission to release reserved inventory. Contact your administrator.';

/** How the order was made (§5.4). An order from before A32 reads MANUAL. */
export const SALE_ORDER_SOURCE_LABELS: Record<SaleOrderSourceType, string> = {
  MANUAL:         'Manual',
  FROM_QUOTATION: 'From quotation',
  PORTAL:         'Customer portal',
  INTER_TENANT:   'Inter-tenant'
};

const SOURCE_SEVERITY: Record<SaleOrderSourceType, Severity> = {
  MANUAL: 'secondary', FROM_QUOTATION: 'info', PORTAL: 'contrast', INTER_TENANT: 'contrast'
};

/** §6.3 — what each delivery indicator colour means, for its tooltip. */
export const DELIVERY_INDICATOR_MEANING: Record<DeliveryIndicator, string> = {
  GREEN:  'Delivered in full.',
  BLUE:   'Reserved in full — the stock is held, not yet shipped.',
  YELLOW: 'Partly delivered or partly reserved.',
  RED:    'Nothing reserved or delivered yet — action needed.',
  GREY:   'Cancelled line.'
};

/** Mirrors the server (contract §7.1): only a confirmed, still-open order reserves by hand; a draft reserves by confirming. */
const RESERVABLE_ORDER_STATUSES = ['CONFIRMED', 'PARTIALLY_FULFILLED'];
const RESERVABLE_LINE_STATUSES = ['OPEN', 'RESERVED', 'PARTIALLY_FULFILLED'];

/** The customer PO can be changed on any order but these (PUT …/customer-po). */
const PO_LOCKED_ORDER_STATUSES = ['CANCELLED', 'CLOSED'];

/** BR-C3-04 — free text, at most 50 characters. */
const CUSTOMER_PO_MAX_LENGTH = 50;

/** A reserve call that found less free stock than asked, waiting for the user to accept the part there is. */
export interface PendingPartialReservation {
  line: SaleOrderLineModel;
  result: SaleOrderLineReservationModel;
}

@Component({
  selector: 'app-sale-order-detail',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    TableModule, ButtonModule, TagModule, TooltipModule, ToastModule,
    DialogModule, DropdownModule, CheckboxModule, InputNumberModule, InputTextModule, CalendarModule, TextareaModule,
    TabViewModule, TimelinePanelComponent, AttachmentListComponent, DeliveryPreviewPanelComponent
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

  // A31 C7/PD-06 — manufactured lines raise their own production order(s); shown here so the link
  // works both ways (the production order already shows "From sale order ...").
  productionOrders: ProductionOrderListItem[] = [];
  isLoadingProductionOrders = false;
  readonly productionSeverity = productionStatusSeverity;

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

  // ── A32 PD-06: customer PO ──────────────────────────────────────────────────

  readonly customerPoInterface = SALES_ATTACHMENT_CODES.customerPo;
  readonly customerPoMaxLength = CUSTOMER_PO_MAX_LENGTH;

  /** Other orders of this organization carrying the same customer PO reference (BR-C3-05: a warning, never a block). */
  poDuplicates: CustomerPoDuplicateModel[] = [];
  /** The order's CUSTOMER_PO files, to name the linked one and to choose it in the dialog. */
  customerPoFiles: AttachmentModel[] = [];

  poDialogVisible = false;
  poReference = '';
  poDate: Date | null = null;
  poAttachmentUuid: string | null = null;
  dialogPoDuplicates: CustomerPoDuplicateModel[] = [];
  isSavingPo = false;

  // ── A32 PE-08/09: reservations ──────────────────────────────────────────────

  /** The line a reserve or release is in flight for (or waiting on the partial dialog); its buttons are held. */
  busyLineUuid: string | null = null;
  isReservingAll = false;

  partialDialogVisible = false;
  partial: PendingPartialReservation | null = null;

  // ── A33: routes, preview, confirm gate, fulfillment ─────────────────────────

  activeRoutes: FulfillmentRouteModel[] = [];
  private routesRequested = false;
  /** The line whose route change is being saved; every route dropdown waits for it. */
  savingRouteLineUuid: string | null = null;

  preview: SaleOrderDeliveryPreviewModel | null = null;
  isLoadingPreview = false;
  previewFailed = false;
  private previewSeq = 0;

  /** The confirm 400, one blocker per line (the server joins them with "\n"). */
  confirmErrors: string[] = [];
  /** Confirmed, but the deliveries were not (all) created: the server's reason, shown with the recovery button. */
  deliveryCreationMessage: string | null = null;
  isCreatingDeliveries = false;
  /** Lines the last confirm or "Create deliveries" left off a delivery, with why. */
  createDeliveriesSkipped: SkippedSaleOrderLineModel[] = [];

  /** D-15 — what cancelling the order did to its deliveries. */
  cancelResult: SaleOrderCancelResultModel | null = null;

  private readonly expandedDeliveries = new Set<string>();
  private readonly deliveryLines = new Map<string, DeliveryLineModel[]>();
  private readonly loadingDeliveryLines = new Set<string>();
  private readonly failedDeliveryLines = new Set<string>();

  private readonly destroyRef = inject(DestroyRef);

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private saleOrderService: SaleOrderService,
    private routesService: FulfillmentRoutesService,
    private logisticsService: LogisticsService,
    private partnerService: BusinessPartnerService,
    private invoiceService: SalesInvoiceService,
    private addressService: AddressService,
    private productionOrderService: ProductionOrderService,
    private attachmentService: AttachmentService,
    public authService: AuthService,
    private messageService: MessageService,
    @Inject(LOCALE_ID) private locale: string
  ) {}

  ngOnInit() {
    // A link on this page can lead to another order on the same route (the duplicate customer PO warning), and
    // Angular keeps this component for it — so follow the route's uuid, not just the one it opened with.
    const params$ = (this.route as Partial<ActivatedRoute>).paramMap as Observable<ParamMap> | undefined;
    if (params$) {
      params$.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(p => this.open(p.get('uuid') ?? ''));
    } else {
      this.open(this.route.snapshot.paramMap.get('uuid') ?? '');
    }
  }

  private open(uuid: string) {
    if (this.order && uuid === this.uuid) return;
    if (this.order) {
      // Another order: start over, rather than show this one's figures under its number.
      this.order = null;
      this.activeTab = 0;
      this.invoices = [];
      this.payments = [];
      this.invoiceTotals = [];
      this.customerPoFiles = [];
      this.poDuplicates = [];
      this.preview = null;
      this.cancelResult = null;
      this.deliveryCreationMessage = null;
      this.createDeliveriesSkipped = [];
      this.expandedDeliveries.clear();
      this.deliveryLines.clear();
      this.failedDeliveryLines.clear();
    }
    this.uuid = uuid;
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
          this.checkPoDuplicates(res.result);
          if (res.result.customerPoAttachmentUuid) this.loadCustomerPoFiles();
          this.loadPartnerName(res.result.partnerId);
          this.loadShipTo(res.result);
          this.loadDeliveries();
          this.loadProductionOrders();
          this.ensureRoutesLoaded();
          this.loadPreview();
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
    // A33 D-4 — a collected order with a line routed to Ship has an address too.
    if (!order.shippingAddressId) return;
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

  loadProductionOrders() {
    if (!this.canViewProduction) { this.productionOrders = []; return; }

    this.isLoadingProductionOrders = true;
    this.productionOrderService.getList({ sourceUuid: this.uuid, pageSize: 100 }).subscribe({
      next: (res) => {
        this.isLoadingProductionOrders = false;
        this.productionOrders = res.result?.data ?? [];
      },
      error: () => {
        this.isLoadingProductionOrders = false;
        this.productionOrders = [];
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
  get canViewProduction(): boolean { return this.authService.hasPermission('PROD_VIEW'); }
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
   * Lines a delivery can carry: not cancelled, not shipped by the vendor directly, something still
   * to send, and — critically — actually available to send. A line's own status stays OPEN until
   * stock is actually held for it (SaleOrderFulfillmentListener moves it to RESERVED once production
   * or a GRN covers it); offering an OPEN line here would let someone draft a delivery for goods that
   * have not been manufactured or received yet, which the warehouse could never actually pick.
   */
  get deliverableLines(): SaleOrderLineModel[] {
    return (this.order?.lines ?? []).filter(l =>
      l.status !== 'CANCELLED' && l.status !== 'OPEN' && l.fulfillmentMode !== 'DROP_SHIP' && this.outstanding(l) > 0);
  }

  /** Whether anything is outstanding but not yet available — shown so "no delivery button" doesn't read as a bug. */
  get hasUnavailableLines(): boolean {
    return (this.order?.lines ?? []).some(l =>
      l.status === 'OPEN' && l.fulfillmentMode !== 'DROP_SHIP' && this.outstanding(l) > 0);
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
    if (!this.canConfirm || this.isConfirmBlocked) return;
    this.confirmErrors = [];
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
    if (!this.canConfirm || this.isConfirming || this.isConfirmBlocked) return;
    this.isConfirming = true;
    this.confirmErrors = [];

    this.saleOrderService.confirmSaleOrder(this.uuid).subscribe({
      next: (res) => {
        this.isConfirming = false;
        this.confirmDialogVisible = false;
        this.afterConfirm(res?.result ?? null);
        this.load();
      },
      error: (err) => {
        this.isConfirming = false;
        // A33 PC-09 — the gate's 400 is one blocker per line: listed in the dialog, which stays open.
        const message: string | undefined = err?.error?.message;
        this.confirmErrors = splitServerMessage(message);
        this.messageService.add({
          severity: 'error', summary: 'Not confirmed',
          detail: this.confirmErrors.length > 1
            ? `${this.confirmErrors.length} things stop the order being confirmed — they are listed in the dialog.`
            : message ?? 'The order could not be confirmed.'
        });
      }
    });
  }

  /** A33 D-1 — what confirming created; when the deliveries failed, the order is still confirmed: say so and offer the retry. */
  private afterConfirm(result: SaleOrderConfirmResultModel | null) {
    const created = result?.deliveries ?? [];
    this.createDeliveriesSkipped = result?.skippedLines ?? [];
    this.deliveryCreationMessage = result?.deliveryCreationFailed
      ? result.deliveryMessage || 'The order is confirmed, but its delivery orders could not be created.'
      : null;

    if (this.deliveryCreationMessage) {
      this.messageService.add({ severity: 'warn', summary: 'Order confirmed', detail: `Stock has been reserved. ${this.deliveryCreationMessage}` });
    } else {
      this.messageService.add({
        severity: 'success', summary: 'Order confirmed',
        detail: created.length ? `Stock has been reserved. ${this.createdText(created)}` : 'Stock has been reserved.'
      });
    }
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
      next: (res) => {
        this.isCancelling = false;
        this.cancelDialogVisible = false;
        // A33 D-15 — deliveries not yet goods-issued are cancelled with the order; issued ones stay.
        const result = res?.result ?? null;
        const cancelled = result?.cancelledDeliveries ?? [];
        const issued = result?.issuedDeliveries ?? [];
        this.cancelResult = cancelled.length || issued.length ? { cancelledDeliveries: cancelled, issuedDeliveries: issued } : null;

        const parts = ['Its reservations have been released.'];
        if (cancelled.length) parts.push(`${cancelled.length} deliver${cancelled.length === 1 ? 'y' : 'ies'} cancelled.`);
        if (issued.length) parts.push(`${issued.length} already goods-issued kept — see the list on the order.`);
        this.messageService.add({ severity: issued.length ? 'warn' : 'success', summary: 'Order cancelled', detail: parts.join(' ') });
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

  // ── A32 PD-06: source ───────────────────────────────────────────────────────

  get sourceType(): SaleOrderSourceType { return this.order?.sourceType ?? 'MANUAL'; }

  sourceLabel(type: SaleOrderSourceType): string { return SALE_ORDER_SOURCE_LABELS[type] ?? formatCode(type); }

  sourceSeverity(type: SaleOrderSourceType): Severity { return SOURCE_SEVERITY[type] ?? 'secondary'; }

  /** A number links to its page only for someone that page would let in (its route guard). */
  get canViewQuotations(): boolean { return this.authService.hasPermission('SALE_QUOTATION_VIEW'); }
  get canViewInquiries(): boolean  { return this.authService.hasPermission('SALE_INQUIRY_VIEW'); }

  // ── A32 PD-06: customer PO ──────────────────────────────────────────────────

  /** "2026-10-05T00:00:00Z" is the 5th, wherever the reader is. */
  dateOnly(iso: string): Date { return fromDateOnly(iso); }

  get canEditCustomerPo(): boolean {
    return !!this.order && !PO_LOCKED_ORDER_STATUSES.includes(this.order.status)
        && this.authService.hasPermission('SALE_ORDER_EDIT');
  }

  /** The server's duplicate check (GET customer-po-check) answers to any of these. */
  private get canCheckCustomerPo(): boolean {
    return ['SALE_ORDER_VIEW', 'SALE_ORDER_CREATE', 'SALE_ORDER_EDIT'].some(code => this.authService.hasPermission(code));
  }

  /** The file linked as the customer's PO, once the order's CUSTOMER_PO files are known. */
  get linkedPoFile(): AttachmentModel | null {
    const linked = this.order?.customerPoAttachmentUuid;
    return linked ? this.customerPoFiles.find(f => f.uuid === linked) ?? null : null;
  }

  /** An uploaded file is a plain link; a document the API serves behind the caller's token is not (open it from the panel). */
  poFileHref(file: AttachmentModel): string | null {
    return this.attachmentService.isApiUrl(file.fileUrl) ? null : this.attachmentService.resolveUrl(file.fileUrl);
  }

  get poFileOptions(): { label: string; value: string }[] {
    return this.customerPoFiles.map(f => ({ label: f.fileName, value: f.uuid }));
  }

  /** Best effort: without the warning the order is still right, so a failed check says nothing. */
  private checkPoDuplicates(order: SaleOrderModel) {
    this.poDuplicates = [];
    const reference = order.customerPoReference?.trim();
    if (!reference || !this.canCheckCustomerPo) return;
    this.saleOrderService.checkCustomerPo(reference, order.uuid).subscribe({
      next: (res) => { if (this.order?.uuid === order.uuid) this.poDuplicates = res.result ?? []; },
      error: () => { this.poDuplicates = []; }
    });
  }

  /** @param pickTheOnlyOne In the dialog: with no file linked yet and exactly one uploaded, that one is the PO. */
  loadCustomerPoFiles(pickTheOnlyOne = false) {
    if (!this.uuid) return;
    this.attachmentService.getAttachments(this.customerPoInterface, this.uuid).subscribe({
      next: (res) => {
        this.customerPoFiles = res.success ? res.result ?? [] : [];
        if (pickTheOnlyOne && this.poDialogVisible && !this.poAttachmentUuid && this.customerPoFiles.length === 1) {
          this.poAttachmentUuid = this.customerPoFiles[0].uuid;
        }
      },
      error: () => { this.customerPoFiles = []; }
    });
  }

  openCustomerPoDialog() {
    if (!this.canEditCustomerPo || !this.order) return;
    this.poReference = this.order.customerPoReference ?? '';
    this.poDate = this.order.customerPoDate ? fromDateOnly(this.order.customerPoDate) : null;
    this.poAttachmentUuid = this.order.customerPoAttachmentUuid ?? null;
    this.dialogPoDuplicates = [];
    this.poDialogVisible = true;
    this.loadCustomerPoFiles(true);
  }

  /** On leaving the reference box: is it on another order already? A warning only. */
  checkDialogPoReference() {
    const reference = this.poReference.trim();
    if (!reference || !this.canCheckCustomerPo) { this.dialogPoDuplicates = []; return; }
    this.saleOrderService.checkCustomerPo(reference, this.uuid).subscribe({
      next: (res) => { this.dialogPoDuplicates = res.result ?? []; },
      error: () => { this.dialogPoDuplicates = []; }
    });
  }

  /** PUT …/customer-po replaces all three: an emptied field clears it. */
  saveCustomerPo() {
    if (!this.canEditCustomerPo || this.isSavingPo) return;
    const reference = this.poReference.trim();
    if (reference.length > CUSTOMER_PO_MAX_LENGTH) {
      this.messageService.add({
        severity: 'warn', summary: 'Check the reference',
        detail: `The customer PO reference can be at most ${CUSTOMER_PO_MAX_LENGTH} characters.`
      });
      return;
    }
    this.isSavingPo = true;

    this.saleOrderService.updateCustomerPo(this.uuid, {
      customerPoReference: reference || null,
      customerPoDate: this.poDate ? toDateOnly(this.poDate) : null,
      customerPoAttachmentUuid: this.poAttachmentUuid || null
    }).subscribe({
      next: () => {
        this.isSavingPo = false;
        this.poDialogVisible = false;
        this.messageService.add({ severity: 'success', summary: 'Customer PO saved', detail: 'The order now carries it.' });
        this.load();
      },
      error: (err) => {
        this.isSavingPo = false;
        this.messageService.add({
          severity: 'error', summary: 'Not saved',
          detail: err?.error?.message ?? 'The customer PO could not be saved.'
        });
      }
    });
  }

  // ── A32 PE-07: delivery indicator ───────────────────────────────────────────

  private qty(n: number | null | undefined): string {
    return formatNumber(n ?? 0, this.locale, '1.0-4');
  }

  /** What the dot means, and the figures behind it. */
  indicatorTooltip(line: SaleOrderLineModel): string {
    const indicator = line.deliveryIndicator;
    if (!indicator) return '';
    const meaning = DELIVERY_INDICATOR_MEANING[indicator] ?? indicator;
    if (indicator === 'GREY') return meaning;
    return `${meaning} Reserved ${this.qty(line.reservedQty)} · delivered ${this.qty(line.fulfilledQty)} of ${this.qty(line.quantity)}.`;
  }

  // ── A32 PE-08: reserve / release ────────────────────────────────────────────

  /** The two codes are independent (§6.5): either button can be usable without the other. */
  get mayReserve(): boolean { return this.authService.hasPermission('SALE_ORDER_RESERVE'); }
  get mayRelease(): boolean { return this.authService.hasPermission('SALE_ORDER_RELEASE_RESERVATION'); }

  /** Whether the order is one stock can be reserved for by hand at all. */
  get isReservableOrder(): boolean {
    return !!this.order && RESERVABLE_ORDER_STATUSES.includes(this.order.status);
  }

  /** Reserve is on the line while the server would accept it (contract §7.1); the permission only disables it. */
  canReserveLine(line: SaleOrderLineModel): boolean {
    return this.isReservableOrder
        && RESERVABLE_LINE_STATUSES.includes(line.status)
        && line.fulfillmentMode !== 'DROP_SHIP'
        && (line.reservableQty ?? 0) > 0;
  }

  /** Release is on the line while the order holds something for it (§6.4). */
  canReleaseLine(line: SaleOrderLineModel): boolean {
    return (line.reservedQty ?? 0) > 0;
  }

  get hasReservableLines(): boolean {
    return (this.order?.lines ?? []).some(l => this.canReserveLine(l));
  }

  reserveTooltip(line: SaleOrderLineModel): string {
    return this.mayReserve ? `Hold up to ${this.qty(line.reservableQty)} from free stock.` : RESERVE_PERMISSION_TOOLTIP;
  }

  releaseTooltip(line: SaleOrderLineModel): string {
    return this.mayRelease ? `Give the ${this.qty(line.reservedQty)} held back to free stock.` : RELEASE_PERMISSION_TOOLTIP;
  }

  get reserveAllTooltip(): string {
    if (!this.mayReserve) return RESERVE_PERMISSION_TOOLTIP;
    if (!this.hasReservableLines) return 'Every open line is already reserved, or has nothing left to reserve.';
    return 'Hold what free stock there is for every open line.';
  }

  /** Someone without one of the codes gets a note under the table saying who to ask (§10.6). */
  get lacksReservationPermission(): boolean { return !this.mayReserve || !this.mayRelease; }

  get canPressReserveAll(): boolean {
    return this.mayReserve && this.hasReservableLines && !this.isReservingAll && !this.busyLineUuid;
  }

  reserveLine(line: SaleOrderLineModel) {
    if (!this.mayReserve || !this.canReserveLine(line) || this.busyLineUuid || this.isReservingAll) return;
    this.busyLineUuid = line.uuid;

    this.saleOrderService.reserveLine(this.uuid, line.uuid, {}).subscribe({
      next: (res) => {
        const result = res.result;
        if (result?.outcome === 'NEEDS_CONFIRMATION') {
          // Nothing was held. Ask; the line stays busy until the answer.
          this.partial = { line, result };
          this.partialDialogVisible = true;
          return;
        }
        this.busyLineUuid = null;
        if (result) this.afterLineChange(line, result);
      },
      error: (err) => this.reservationFailed(err, 'Not reserved', 'The stock could not be reserved.')
    });
  }

  releaseLine(line: SaleOrderLineModel) {
    if (!this.mayRelease || !this.canReleaseLine(line) || this.busyLineUuid || this.isReservingAll) return;
    this.busyLineUuid = line.uuid;

    this.saleOrderService.releaseLine(this.uuid, line.uuid, {}).subscribe({
      next: (res) => {
        this.busyLineUuid = null;
        if (res.result) this.afterLineChange(line, res.result);
      },
      error: (err) => this.reservationFailed(err, 'Not released', 'The reservation could not be released.')
    });
  }

  /** PE-09 — the user accepts the part that is free: hold exactly that, from the warehouse it was counted in. */
  confirmPartial() {
    const pending = this.partial;
    if (!pending) return;
    this.partial = null;
    this.partialDialogVisible = false;
    const { line, result } = pending;

    this.saleOrderService.reserveLine(this.uuid, line.uuid, {
      quantity: result.availableQty,
      allowPartial: true,
      ...(result.warehouseUuid ? { warehouseUuid: result.warehouseUuid } : {})
    }).subscribe({
      next: (res) => {
        this.busyLineUuid = null;
        if (res.result) this.afterLineChange(line, res.result);
      },
      error: (err) => this.reservationFailed(err, 'Not reserved', 'The stock could not be reserved.')
    });
  }

  /** PE-09 — "Available: 30, Required: 100. Reserve 30 units?" (§6.4). */
  partialMessage(p: PendingPartialReservation): string {
    const available = this.qty(p.result.availableQty);
    return `Available: ${available}, Required: ${this.qty(p.result.requestedQty)}. Reserve ${available} units?`;
  }

  /** PE-09 — Cancel (or closing the dialog) holds nothing. */
  cancelPartial() {
    this.partialDialogVisible = false;
    if (!this.partial) return;
    this.partial = null;
    this.busyLineUuid = null;
  }

  reserveAllLines() {
    if (!this.canPressReserveAll) return;
    this.isReservingAll = true;

    this.saleOrderService.reserveAll(this.uuid, true).subscribe({
      next: (res) => {
        this.isReservingAll = false;
        const summary = res.result;
        if (!summary) return;
        summary.lines.forEach(r => this.applyReservation(r));

        const held = summary.reservedLineCount + summary.partialLineCount;
        const n = summary.reservedLineCount;
        this.messageService.add({
          severity: held > 0 ? 'success' : 'warn',
          summary: held > 0 ? 'Stock reserved' : 'Nothing reserved',
          detail: `${n} line${n === 1 ? '' : 's'} reserved in full, ${summary.partialLineCount} in part, ` +
                  `${summary.unchangedLineCount} unchanged.`
        });
        if (held > 0) this.refreshOrder();
      },
      error: (err) => {
        this.isReservingAll = false;
        this.messageService.add({
          severity: 'error', summary: 'Not reserved', detail: err?.error?.message ?? 'The stock could not be reserved.'
        });
      }
    });
  }

  /** Shows what the call did on the line at once, says so, and re-reads the order when stock moved. */
  private afterLineChange(line: SaleOrderLineModel, result: SaleOrderLineReservationModel) {
    this.applyReservation(result);
    const what = this.describe(line);
    const where = result.warehouseName ? ` in ${result.warehouseName}` : '';

    switch (result.outcome) {
      case 'RESERVED':
        this.messageService.add({ severity: 'success', summary: 'Reserved', detail: `${this.qty(result.changedQty)} held for ${what}${where}.` });
        break;
      case 'PARTIAL':
        this.messageService.add({
          severity: 'info', summary: 'Partly reserved',
          detail: `${this.qty(result.changedQty)} of ${this.qty(result.requestedQty)} held for ${what}${where}.`
        });
        break;
      case 'RELEASED':
        this.messageService.add({ severity: 'success', summary: 'Released', detail: `${this.qty(result.changedQty)} of ${what} given back to free stock.` });
        break;
      default:
        this.messageService.add({
          severity: 'warn', summary: 'Nothing reserved',
          detail: result.message ?? 'No stock is free for this item right now.'
        });
    }

    if (result.changedQty > 0) this.refreshOrder();
  }

  /** The line as the server left it: the dot and the figures change before the order is re-read. */
  private applyReservation(result: SaleOrderLineReservationModel) {
    const target = this.order?.lines.find(l => l.uuid === result.lineUuid);
    if (!target) return;
    target.reservedQty = result.reservedQty;
    target.reservableQty = result.reservableQty;
    if (result.deliveryIndicator) target.deliveryIndicator = result.deliveryIndicator;
    if (result.lineStatus) target.status = result.lineStatus;
  }

  /** Re-reads the order alone; on failure the line already shows what the call did. */
  private refreshOrder() {
    this.saleOrderService.getSaleOrderById(this.uuid).subscribe({
      next: (res) => { if (res.success && res.result) this.order = res.result; },
      error: () => { /* keep what is shown */ }
    });
  }

  private reservationFailed(err: any, summary: string, fallback: string) {
    this.busyLineUuid = null;
    this.messageService.add({ severity: 'error', summary, detail: err?.error?.message ?? fallback });
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

  // ── A33 PC-07: the Route column ─────────────────────────────────────────────

  readonly routeLegend = ROUTE_LEGEND;

  /** The order's organization has Logistics (D-11). An order read from an older server has no flag: no routes. */
  get routesOn(): boolean { return this.order?.routesEnabled === true; }

  /** DRAFT only (the route is snapshotted at confirm, D-16), and changing it is editing the order. */
  get canChangeRoute(): boolean {
    return this.routesOn && this.order?.status === 'DRAFT' && this.authService.hasPermission('SALE_ORDER_EDIT');
  }

  routeDisplay(line: SaleOrderLineModel): LineRouteDisplay {
    return lineRouteDisplay(line);
  }

  private ensureRoutesLoaded() {
    if (this.routesRequested || !this.canChangeRoute) return;
    this.routesRequested = true;
    this.routesService.getRoutes().subscribe({
      next: (res) => { this.activeRoutes = res.result ?? []; },
      error: () => { this.activeRoutes = []; }
    });
  }

  /** The active routes, and the line's own override when it is no longer among them (inactive, deleted). */
  routeOptionsFor(line: SaleOrderLineModel): { label: string; value: string }[] {
    const options = this.activeRoutes.map(r => ({ label: r.name, value: r.uuid }));
    const own = line.fulfillmentRouteUuid;
    if (own && !options.some(o => o.value === own)) {
      const name = line.effectiveRouteName || line.effectiveRouteCode || 'Route';
      const why = line.routeBlocker === 'ROUTE_INACTIVE' ? ' (inactive)' : line.routeBlocker === 'ROUTE_UNKNOWN' ? ' (no longer available)' : '';
      options.push({ label: name + why, value: own });
    }
    return options;
  }

  inheritedRouteLabel(line: SaleOrderLineModel): string {
    return inheritPlaceholder(line, !!line.fulfillmentRouteUuid);
  }

  /**
   * Through the route-only line endpoint (PUT …/lines/{line}/fulfillment-route): nothing else on the order changes —
   * never the full-draft PUT, which rebuilds and re-prices every line (and would drop what the form does not show).
   * Then the order and the preview are read again.
   */
  changeLineRoute(line: SaleOrderLineModel, routeUuid: string | null) {
    if (!this.canChangeRoute || !this.order || this.savingRouteLineUuid) return;
    if ((line.fulfillmentRouteUuid ?? null) === (routeUuid ?? null)) return;
    this.savingRouteLineUuid = line.uuid;

    this.saleOrderService.setLineFulfillmentRoute(this.uuid, line.uuid, routeUuid ?? null).subscribe({
      next: () => {
        this.savingRouteLineUuid = null;
        this.messageService.add({ severity: 'success', summary: 'Route changed', detail: `${this.describe(line)} now ${routeUuid ? 'has its own route' : 'inherits its route'}.` });
        this.reloadDraft();
      },
      error: (err) => {
        this.savingRouteLineUuid = null;
        // Fresh line objects re-draw the dropdowns with what the order still has.
        if (this.order) this.order = { ...this.order, lines: this.order.lines.map(l => ({ ...l })) };
        this.messageService.add({ severity: 'error', summary: 'Route not changed', detail: err?.error?.message ?? 'The route could not be changed.' });
      }
    });
  }

  /** After a route change: the order (its lines' sources and blockers) and the preview, nothing else. */
  private reloadDraft() {
    this.saleOrderService.getSaleOrderById(this.uuid).subscribe({
      next: (res) => {
        if (res.success && res.result) this.order = res.result;
        this.loadPreview();
      },
      error: () => this.loadPreview()
    });
  }

  // ── A33 PC-08: the preview of a saved draft ─────────────────────────────────

  private loadPreview() {
    const seq = ++this.previewSeq;
    if (!this.routesOn || this.order?.status !== 'DRAFT') {
      this.preview = null;
      this.isLoadingPreview = false;
      return;
    }
    this.isLoadingPreview = true;
    this.previewFailed = false;
    this.saleOrderService.getDeliveryPreview(this.uuid).subscribe({
      next: (res) => {
        if (seq !== this.previewSeq) return;
        this.isLoadingPreview = false;
        this.preview = res.result ?? null;
      },
      error: () => {
        if (seq !== this.previewSeq) return;
        this.isLoadingPreview = false;
        this.previewFailed = true;
      }
    });
  }

  // ── A33 PC-09: the Confirm gate ─────────────────────────────────────────────

  get confirmBlockers(): ConfirmBlockerModel[] {
    return this.order?.status === 'DRAFT' ? this.order.confirmBlockers ?? [] : [];
  }

  /** Confirm stays on screen, disabled, while anything blocks it (spec §5.5: not hidden). */
  get isConfirmBlocked(): boolean { return this.confirmBlockers.length > 0; }

  get confirmBlockedTooltip(): string { return confirmBlockedSummary(this.confirmBlockers); }

  /** The banner's lines: a line's route problem names the line; anything else is in the server's words. */
  get confirmBlockerRows(): string[] {
    const lines = this.order?.lines ?? [];
    return this.confirmBlockers.map(b => {
      if (!isRouteBlocker(b.code) || b.lineNumber == null) return b.message;
      const line = lines.find(l => l.uuid === b.lineUuid) ?? lines[b.lineNumber - 1];
      const what = line ? ` · ${this.describe(line)}` : '';
      return `Line ${b.lineNumber}${what}: ${BLOCKER_REASONS[b.code] ?? b.message}`;
    });
  }

  // ── A33 PD-06: the Deliveries tab as the order's fulfillment ────────────────

  private get openLines(): SaleOrderLineModel[] {
    return (this.order?.lines ?? []).filter(l => l.status !== 'CANCELLED');
  }

  /** "N of M lines delivered" (spec §10.4): a line counts once all of it has gone. */
  get linesToDeliver(): number { return this.openLines.length; }
  get linesDelivered(): number { return this.openLines.filter(l => l.fulfilledQty >= l.quantity).length; }
  get linesDeliveredPercent(): number {
    return this.linesToDeliver ? Math.round((this.linesDelivered / this.linesToDeliver) * 100) : 0;
  }

  /** "Create deliveries" (D-12 recovery): one per route for whatever is not on a delivery yet. Same gate as the server. */
  get canCreateRouteDeliveries(): boolean {
    return this.routesOn && !!this.order && DELIVERABLE_ORDER_STATUSES.includes(this.order.status)
        && this.authService.hasPermission('DELIVERY_CREATE');
  }

  createRouteDeliveries() {
    if (!this.canCreateRouteDeliveries || this.isCreatingDeliveries) return;
    this.isCreatingDeliveries = true;

    this.saleOrderService.createDeliveries(this.uuid).subscribe({
      next: (res) => {
        this.isCreatingDeliveries = false;
        const created = res.result?.created ?? [];
        this.createDeliveriesSkipped = res.result?.skipped ?? [];
        this.deliveryCreationMessage = null;
        this.messageService.add(created.length
          ? { severity: 'success', summary: 'Deliveries created', detail: this.createdText(created) }
          : { severity: 'info', summary: 'Nothing to create', detail: 'Every line that can go is already on a delivery.' });
        this.loadDeliveries();
      },
      error: (err) => {
        this.isCreatingDeliveries = false;
        this.messageService.add({ severity: 'error', summary: 'Not created', detail: err?.error?.message ?? 'The deliveries could not be created.' });
      }
    });
  }

  /** "2 delivery orders created: DLV-2026-00001, DLV-2026-00002." */
  private createdText(created: CreatedSaleOrderDeliveryModel[]): string {
    const n = created.length;
    return `${n} delivery order${n === 1 ? '' : 's'} created: ${created.map(d => d.deliveryNumber).join(', ')}.`;
  }

  /** A skipped line, named. */
  skippedLineName(s: SkippedSaleOrderLineModel): string {
    const line = this.order?.lines.find(l => l.uuid === s.soLineUuid);
    return line ? this.describe(line) : 'A line';
  }

  isDeliveryExpanded(d: DeliveryListItemModel): boolean { return this.expandedDeliveries.has(d.uuid); }

  /** Opens a delivery's lines under its row; they are read the first time only. */
  toggleDelivery(d: DeliveryListItemModel) {
    if (this.expandedDeliveries.has(d.uuid)) { this.expandedDeliveries.delete(d.uuid); return; }
    this.expandedDeliveries.add(d.uuid);
    if (this.deliveryLines.has(d.uuid) || this.loadingDeliveryLines.has(d.uuid)) return;

    this.loadingDeliveryLines.add(d.uuid);
    this.logisticsService.getDeliveryById(d.uuid).subscribe({
      next: (res) => {
        this.loadingDeliveryLines.delete(d.uuid);
        this.deliveryLines.set(d.uuid, res.result?.lines ?? []);
      },
      error: () => {
        this.loadingDeliveryLines.delete(d.uuid);
        this.failedDeliveryLines.add(d.uuid);
      }
    });
  }

  linesOf(d: DeliveryListItemModel): DeliveryLineModel[] | null { return this.deliveryLines.get(d.uuid) ?? null; }
  isLoadingLinesOf(d: DeliveryListItemModel): boolean { return this.loadingDeliveryLines.has(d.uuid); }
  linesFailedFor(d: DeliveryListItemModel): boolean { return this.failedDeliveryLines.has(d.uuid) && !this.deliveryLines.has(d.uuid); }

  /** FE-DLV's filtered delivery list for this order. */
  get deliveriesListQuery(): Record<string, string> {
    return { saleOrderUuid: this.uuid, saleOrderNumber: this.order?.soNumber ?? '' };
  }

  dismissCancelResult() { this.cancelResult = null; }
}

/** The banner's words for a line whose route stops confirmation. */
const BLOCKER_REASONS: Partial<Record<ConfirmBlockerCode, string>> = {
  ROUTE_MISSING: 'no fulfillment route — assign one on the line, or set a default route on the product variant.',
  ROUTE_INACTIVE: 'its route is inactive — choose another.',
  ROUTE_UNKNOWN: 'its route no longer exists — choose another.'
};
