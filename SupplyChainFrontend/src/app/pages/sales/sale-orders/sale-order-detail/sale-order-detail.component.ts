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
import { TabViewModule } from 'primeng/tabview';
import { MessageService } from 'primeng/api';

import { TimelinePanelComponent } from '../../../../shared/timeline-panel/timeline-panel.component';
import { SaleOrderService, SaleOrderModel, SaleOrderLineModel } from '../../../../services/sale-order.service';
import { BusinessPartnerService } from '../../../../services/business-partner.service';
import { DeliveryListItemModel, SourceLineSelection } from '../../../../services/logistics.service';
import { DELIVERY_STATUS_SEVERITY } from '../../../logistics/deliveries/delivery-list/delivery-list.component';
import { SALE_ORDER_STATUS_SEVERITY, formatCode } from '../sale-order-list/sale-order-list.component';

type Severity = 'success' | 'info' | 'warn' | 'danger' | 'secondary' | 'contrast';

/** One row of the create-delivery dialog: a line the order can still send, and how much of it. */
export interface DeliveryLineChoice {
  line: SaleOrderLineModel;
  outstanding: number;
  include: boolean;
  qty: number;
}

/** Statuses in which a sale order still has goods to send. Mirrors the server's rule. */
const DELIVERABLE_ORDER_STATUSES = ['CONFIRMED', 'PARTIALLY_FULFILLED'];

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
  isLoading = true;
  notFound = false;

  deliveries: DeliveryListItemModel[] = [];
  isLoadingDeliveries = false;

  timelineVisible = false;

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

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private saleOrderService: SaleOrderService,
    private partnerService: BusinessPartnerService,
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
          this.loadDeliveries();
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

  loadDeliveries() {
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
        && this.deliverableLines.length > 0;
  }

  get orderedQty(): number   { return (this.order?.lines ?? []).filter(l => l.status !== 'CANCELLED').reduce((s, l) => s + l.quantity, 0); }
  get fulfilledQty(): number { return (this.order?.lines ?? []).filter(l => l.status !== 'CANCELLED').reduce((s, l) => s + Math.min(l.fulfilledQty, l.quantity), 0); }
  get fulfilledPercent(): number {
    return this.orderedQty > 0 ? Math.round((this.fulfilledQty / this.orderedQty) * 100) : 0;
  }

  describe(line: SaleOrderLineModel): string {
    return line.itemDescription ?? `Variant ${line.variantUuid}`;
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

  formatStatus(code?: string | null): string { return formatCode(code); }
}
