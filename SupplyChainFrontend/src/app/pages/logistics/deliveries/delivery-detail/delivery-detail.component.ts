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
import { TextareaModule } from 'primeng/textarea';
import { DropdownModule } from 'primeng/dropdown';
import { InputTextModule } from 'primeng/inputtext';
import { MessageService } from 'primeng/api';

import { TimelinePanelComponent } from '../../../../shared/timeline-panel/timeline-panel.component';
import { AttachmentListComponent } from '../../../../shared/attachment-list/attachment-list.component';
import {
  LogisticsService,
  CarrierListItemModel,
  CreateConsignmentRequest,
  DeliveryDetailModel,
  DeliveryAvailabilityModel,
  DeliveryNextAction,
  RecordPickupRequest,
  RouteStepProgressModel,
  PICKUP_ID_TYPES
} from '../../../../services/logistics.service';
import { SalesInvoiceService } from '../../../../services/sales-invoice.service';
import { AuthService } from '../../../service/auth.service';
import { DELIVERY_STATUS_SEVERITY } from '../delivery-list/delivery-list.component';
import { SHIPMENT_STATUS_SEVERITY } from '../../consignments/consignment-detail/consignment-detail.component';
import { FLOW, FlowSection, FlowStage, flowStagesFrom } from '../../../../shared/flow';

type Severity = 'success' | 'info' | 'warn' | 'danger' | 'secondary' | 'contrast';

/** The status a delivery must be able to move to for the action to make sense. */
type ReasonAction = 'hold' | 'cancel' | 'short-close';

/**
 * Where a self-pickup delivery can be collected from. Mirrors the server: packed goods are staged
 * and issued by the collection itself; issued goods are simply handed over.
 */
const COLLECTABLE_STATUSES = ['PACKED', 'STAGED', 'PENDING_APPROVAL', 'GOODS_ISSUED'];

/** Goods that have reached the customer can be invoiced. Same rule as the server's invoicing service. */
const INVOICEABLE_STATUSES = ['DELIVERED', 'CLOSED'];

/** A gate pass exists once the goods are at the dock. Same rule as the server's document service. */
const GATE_PASS_STATUSES = ['STAGED', 'PENDING_APPROVAL', 'GOODS_ISSUED', 'IN_TRANSIT', 'DELIVERED', 'PARTIALLY_DELIVERED'];

/**
 * Where a carrier can be booked for a delivery: once it is boxed, so the consignment has weights and
 * dimensions to rate and label, up to the moment it is handed over. After that the consignment
 * already exists, and the delivery follows it.
 */
const CONSIGNABLE_STATUSES = ['PACKED', 'STAGED', 'PENDING_APPROVAL', 'GOODS_ISSUED'];

/** A33 — a forward step a routed delivery can take next, as this page offers it. */
export interface RouteActionButton {
  action: DeliveryNextAction;
  label: string;
  icon: string;
  /** The operation's own permission, the same code the server checks (contract §6). */
  permission: string;
}

/**
 * A33 — the forward actions of contract §6 in flow order, so the first one offered is the primary button. HOLD,
 * RESUME, CANCEL and SHORT_CLOSE aren't here: they stay route-independent (BR-C5-05), on allowedNextStatuses as before.
 * STAGE and GOODS_ISSUE need no input and go through `advance`; APPROVE has its own endpoint (an approval must never
 * turn into a goods issue); the rest need input and open the screens and dialogs that already collect it.
 */
export const ROUTE_ACTION_BUTTONS: readonly RouteActionButton[] = [
  { action: 'RELEASE',            label: 'Release',               icon: 'pi pi-lock',         permission: 'DELIVERY_EDIT' },
  { action: 'GENERATE_PICK_LIST', label: 'Start Picking',         icon: 'pi pi-list-check',   permission: 'PICKING' },
  { action: 'CONFIRM_PICK',       label: 'Confirm Pick',          icon: 'pi pi-check-square', permission: 'PICKING' },
  { action: 'PACK',               label: 'Start Packing',         icon: 'pi pi-box',          permission: 'DISPATCH' },
  { action: 'STAGE',              label: 'Stage for Dispatch',    icon: 'pi pi-inbox',        permission: 'DISPATCH' },
  { action: 'APPROVE',            label: 'Approve Dispatch',      icon: 'pi pi-verified',     permission: 'DELIVERY_APPROVE' },
  { action: 'GOODS_ISSUE',        label: 'Post Goods Issue',      icon: 'pi pi-sign-out',     permission: 'DISPATCH' },
  { action: 'CREATE_CONSIGNMENT', label: 'Dispatch for Shipment', icon: 'pi pi-send',         permission: 'DELIVERY_EDIT' },
  { action: 'RECORD_COLLECTION',  label: 'Record Collection',     icon: 'pi pi-user-plus',    permission: 'DISPATCH' }
];

/**
 * A33 REV-04 / REV-04b — the server's PackageRepository.IsStillAmendableWhenStagedAsync, read from the detail model: a
 * delivery its route staged automatically (no STAGE step, D-3) keeps its cartons amendable (weigh, resize, void) until
 * the dispatch is approved, a live (not cancelled) consignment carries it, or the goods are issued. No route: false,
 * today's rule (packages change only while PICKED / PACKED). The server checks it again on every change.
 */
export function packagesAmendableWhileStaged(
  d: Pick<DeliveryDetailModel, 'status' | 'routeSteps' | 'approvedAt' | 'consignments'> | null | undefined
): boolean {
  if (!d || d.status !== 'STAGED' || !d.routeSteps?.length) return false;
  if (d.routeSteps.some(s => s.stepCode === 'STAGE') || d.approvedAt) return false;
  return !(d.consignments ?? []).some(c => c.status !== 'CANCELLED');
}

/** Where the tracker stops moving; it stays where it was and the status says why (contract §8). */
const STOPPED_STATUSES = ['CANCELLED', 'SHORT_CLOSED', 'ON_HOLD'];

/** The statuses at which every route has finished: spec COMPLETED ≙ DELIVERED, then CLOSED (D-2). */
const COMPLETE_STATUSES = ['DELIVERED', 'CLOSED'];

const ADVANCE_SUCCESS: Partial<Record<DeliveryNextAction, string>> = {
  RELEASE:     'Delivery released and stock reserved.',
  STAGE:       'Delivery staged at the dock.',
  APPROVE:     'Dispatch approved.',
  GOODS_ISSUE: 'Goods issued.'
};

@Component({
  selector: 'app-delivery-detail',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    TableModule, ButtonModule, TagModule, TooltipModule, ToastModule,
    DialogModule, TextareaModule, DropdownModule, InputTextModule,
    TimelinePanelComponent, AttachmentListComponent,
    ...FLOW
  ],
  templateUrl: './delivery-detail.component.html',
  styleUrls: ['./delivery-detail.component.scss'],
  providers: [MessageService]
})
export class DeliveryDetailComponent implements OnInit {
  uuid = '';
  delivery: DeliveryDetailModel | null = null;
  isLoading = true;
  /** True only when the server said the delivery does not exist, as opposed to a failed request. */
  notFound = false;

  timelineVisible = false;

  reasonDialogVisible = false;
  reasonAction: ReasonAction | null = null;
  reasonText = '';
  isSubmitting = false;

  // ── Release ─────────────────────────────────────────────────────────────────
  // Releasing hard-reserves stock, so the dialog shows what the reservation would actually find
  // before anything is committed — the same figures the server will measure the release against.

  releaseDialogVisible = false;
  availability: DeliveryAvailabilityModel | null = null;
  isCheckingAvailability = false;

  // ── Self-pickup collection (A29 §8.2) ───────────────────────────────────────

  pickupDialogVisible = false;
  pickup: RecordPickupRequest = this.emptyPickup();
  idTypes = PICKUP_ID_TYPES;

  // ── Handing the delivery to a carrier ───────────────────────────────────────

  consignmentDialogVisible = false;
  carriers: CarrierListItemModel[] = [];
  consignment = this.emptyConsignment();

  // ── A33 — goods issue from the route's primary button ───────────────────────

  issueDialogVisible = false;

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private logisticsService: LogisticsService,
    private invoiceService: SalesInvoiceService,
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

    this.logisticsService.getDeliveryById(this.uuid).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success && res.result) {
          this.delivery = res.result;
          this.notFound = false;
        } else {
          this.delivery = null;
          this.notFound = true;
        }
      },
      error: (err) => {
        this.isLoading = false;
        this.delivery = null;
        // A 404 is "this delivery does not exist"; anything else is a failure to ask. Showing
        // "not found" for a network error would send someone hunting for a deleted record.
        this.notFound = err?.status === 404;
        if (!this.notFound) {
          this.messageService.add({
            severity: 'error', summary: 'Error', detail: 'Failed to load the delivery.'
          });
        }
      }
    });
  }

  // ── What the delivery may do next ───────────────────────────────────────────
  //
  // Every one of these reads the server's own state machine, delivered in
  // allowedNextStatuses. Keeping a second copy of the rules here is how a UI ends up offering a
  // button the server refuses.

  private canMoveTo(status: string): boolean {
    return this.delivery?.allowedNextStatuses?.includes(status) ?? false;
  }

  get canHold(): boolean       { return this.canMoveTo('ON_HOLD'); }
  get canCancel(): boolean     { return this.canMoveTo('CANCELLED'); }
  get canShortClose(): boolean { return this.canMoveTo('SHORT_CLOSED'); }

  /** On a routed delivery the route says (nextActions); otherwise today's rule. */
  get canRelease(): boolean {
    return this.hasRoute ? this.routeAllows('RELEASE') : this.canMoveTo('RELEASED');
  }

  /** Picking starts from a released delivery; the server owns the rest of the rule. */
  get canGeneratePickList(): boolean {
    return this.hasRoute ? this.routeAllows('GENERATE_PICK_LIST') : this.canMoveTo('PICKING');
  }

  /**
   * The dock screen is worth offering once goods are off the shelf and until they have left.
   * Driven by the delivery's own status rather than by a permission check here — the route guard
   * and the server both enforce DISPATCH, and a third copy of that rule would be one too many.
   */
  get canOpenPackStation(): boolean {
    return ['PICKED', 'PACKED', 'STAGED', 'PENDING_APPROVAL']
      .includes(this.delivery?.status ?? '');
  }

  /** Resuming is only meaningful for a held delivery that recorded where it was held from. */
  get canResume(): boolean {
    return this.delivery?.status === 'ON_HOLD'
        && !!this.delivery?.statusBeforeHold
        && this.canMoveTo(this.delivery.statusBeforeHold);
  }

  get isSelfPickup(): boolean { return this.delivery?.deliveryMode === 'SELF_PICKUP'; }

  /** The counter's one button: the customer is here for a self-pickup that is ready to hand over. */
  get canRecordPickup(): boolean {
    // A routed delivery collects when its route says so: PICK_ONLY issues the goods first (D-2).
    if (this.hasRoute) return this.routeAllows('RECORD_COLLECTION');
    return this.isSelfPickup && COLLECTABLE_STATUSES.includes(this.delivery?.status ?? '');
  }

  /** Already collected — the pass and the collector's details are the record of it. */
  get isCollected(): boolean {
    return this.isSelfPickup && !!this.delivery?.pickedUpAt;
  }

  get canDownloadGatePass(): boolean {
    return GATE_PASS_STATUSES.includes(this.delivery?.status ?? '');
  }

  /**
   * An outbound delivery that a carrier will move and that has none yet. A collection never travels,
   * so it never gets one; and once a consignment exists this is the wrong button — the delivery
   * already follows it. The server's create is gated by DELIVERY_EDIT; asking here as well spares
   * everyone else a button that ends in a refusal.
   */
  get canCreateConsignment(): boolean {
    const d = this.delivery;
    // A routed delivery is consigned when its route reaches SHIP (the server refuses it on a route without SHIP).
    if (d && this.hasRoute) return this.routeAllows('CREATE_CONSIGNMENT');
    return !!d
        && d.direction === 'OUTBOUND'
        && !this.isSelfPickup
        && CONSIGNABLE_STATUSES.includes(d.status)
        && !d.consignments?.length
        && this.authService.hasPermission('DELIVERY_EDIT');
  }

  /** The delivery is moved by its consignment: say so, so a page that does not change is not a mystery. */
  get followsConsignment(): boolean {
    return !!this.delivery?.consignments?.length
        && ['GOODS_ISSUED', 'IN_TRANSIT'].includes(this.delivery.status);
  }

  /** A sale-order delivery that has reached the customer, for someone who may raise invoices. */
  get canCreateInvoice(): boolean {
    return !!this.delivery?.saleOrderUuid
        && INVOICEABLE_STATUSES.includes(this.delivery.status)
        && this.authService.hasPermission('SALES_INVOICE_MANAGE');
  }

  /** Raises a draft invoice for the delivery, or opens the one it already has. */
  createInvoice() {
    if (!this.canCreateInvoice || this.isSubmitting) return;
    this.isSubmitting = true;

    this.invoiceService.createFromDelivery(this.uuid).subscribe({
      next: (res) => {
        this.isSubmitting = false;
        const created = res.result;
        this.messageService.add({
          severity: created?.alreadyExisted ? 'info' : 'success',
          summary: created?.alreadyExisted ? 'Already invoiced' : 'Invoice created',
          detail: res.message || 'Draft invoice raised.'
        });
        if (created?.invoiceUuid) this.router.navigate(['/portal/pages/finance/sales-invoices', created.invoiceUuid]);
      },
      error: (err) => {
        this.isSubmitting = false;
        this.messageService.add({
          severity: 'error', summary: 'Not created',
          detail: err?.error?.message ?? 'The invoice could not be created.'
        });
      }
    });
  }

  // ── Actions ─────────────────────────────────────────────────────────────────

  openReasonDialog(action: ReasonAction) {
    this.reasonAction = action;
    this.reasonText = '';
    this.reasonDialogVisible = true;
  }

  get reasonDialogTitle(): string {
    switch (this.reasonAction) {
      case 'hold':        return 'Place this delivery on hold';
      case 'cancel':      return 'Cancel this delivery';
      case 'short-close': return 'Close this delivery short';
      default:            return '';
    }
  }

  get reasonDialogHint(): string {
    switch (this.reasonAction) {
      case 'hold':
        return 'It will resume exactly where it paused.';
      case 'cancel':
        return 'This cannot be undone. Stock reserved against the delivery is released.';
      case 'short-close':
        return 'Any quantity not delivered is recorded as short on each line.';
      default:
        return '';
    }
  }

  confirmReason() {
    if (!this.reasonAction || !this.reasonText.trim() || this.isSubmitting) return;

    const body = { reason: this.reasonText.trim() };
    const action = this.reasonAction;

    const request =
      action === 'hold'   ? this.logisticsService.holdDelivery(this.uuid, body)
    : action === 'cancel' ? this.logisticsService.cancelDelivery(this.uuid, body)
    :                       this.logisticsService.shortCloseDelivery(this.uuid, body);

    this.isSubmitting = true;

    request.subscribe({
      next: () => {
        this.isSubmitting = false;
        this.reasonDialogVisible = false;
        this.messageService.add({
          severity: 'success', summary: 'Done', detail: this.successMessage(action)
        });
        // Reload rather than patching local state: the server decides the resulting status and
        // which actions are legal next, and guessing here is how the two drift apart.
        this.load();
      },
      error: (err) => {
        this.isSubmitting = false;
        this.messageService.add({
          severity: 'error',
          summary: 'Not allowed',
          // The server explains refusals — "a delivery in GOODS_ISSUED cannot move to
          // CANCELLED" — and that is far more use than a generic failure.
          detail: err?.error?.message ?? 'The action could not be completed.'
        });
      }
    });
  }

  resume() {
    if (this.isSubmitting) return;
    this.isSubmitting = true;

    this.logisticsService.resumeDelivery(this.uuid).subscribe({
      next: () => {
        this.isSubmitting = false;
        this.messageService.add({ severity: 'success', summary: 'Done', detail: 'Delivery resumed.' });
        this.load();
      },
      error: (err) => {
        this.isSubmitting = false;
        this.messageService.add({
          severity: 'error', summary: 'Not allowed',
          detail: err?.error?.message ?? 'The delivery could not be resumed.'
        });
      }
    });
  }

  // ── Release ─────────────────────────────────────────────────────────────────

  openReleaseDialog() {
    this.availability = null;
    this.releaseDialogVisible = true;
    this.isCheckingAvailability = true;

    this.logisticsService.getDeliveryAvailability(this.uuid).subscribe({
      next: (res) => {
        this.isCheckingAvailability = false;
        this.availability = res.result ?? null;
      },
      error: () => {
        this.isCheckingAvailability = false;
        // The dialog still opens: the check is a preview, not a precondition, and the server
        // decides the release either way. Blocking on a failed preview would be worse.
        this.availability = null;
      }
    });
  }

  get shortLines() {
    return this.availability?.lines.filter(l => l.shortfall > 0) ?? [];
  }

  release(onShortage: 'BLOCK' | 'SPLIT') {
    if (this.isSubmitting) return;
    this.isSubmitting = true;

    this.logisticsService.releaseDelivery(this.uuid, { onShortage }).subscribe({
      next: () => {
        this.isSubmitting = false;
        this.releaseDialogVisible = false;
        this.messageService.add({
          severity: 'success',
          summary: 'Released',
          detail: onShortage === 'SPLIT'
            ? 'Released for what stock covers. The balance is a new draft delivery.'
            : 'Delivery released and stock reserved.'
        });
        this.load();
      },
      error: (err) => {
        this.isSubmitting = false;
        this.messageService.add({
          severity: 'error', summary: 'Not allowed',
          detail: err?.error?.message ?? 'The delivery could not be released.'
        });
      }
    });
  }

  generatePickList() {
    if (this.isSubmitting) return;
    this.isSubmitting = true;

    this.logisticsService.generatePickList(this.uuid).subscribe({
      next: (res) => {
        this.isSubmitting = false;
        this.messageService.add({
          severity: 'success', summary: 'Done', detail: 'Pick list generated.'
        });
        if (res.result) this.router.navigate(['/portal/pages/logistics/picking', res.result]);
        else this.load();
      },
      error: (err) => {
        this.isSubmitting = false;
        this.messageService.add({
          severity: 'error', summary: 'Not allowed',
          detail: err?.error?.message ?? 'The pick list could not be generated.'
        });
      }
    });
  }

  private successMessage(action: ReasonAction): string {
    switch (action) {
      case 'hold':        return 'Delivery placed on hold.';
      case 'cancel':      return 'Delivery cancelled.';
      case 'short-close': return 'Delivery closed short.';
    }
  }

  // ── Self-pickup collection ──────────────────────────────────────────────────

  private emptyPickup(): RecordPickupRequest {
    return { pickupPersonName: '', pickupPersonIdType: 'CNIC', pickupPersonIdNumber: '', pickupAuthorization: '' };
  }

  openPickupDialog() {
    this.pickup = this.emptyPickup();
    this.pickupDialogVisible = true;
  }

  /** Name and ID are what the gate pass has to say; authorization is only for a stand-in collector. */
  get canSubmitPickup(): boolean {
    return !!this.pickup.pickupPersonName.trim()
        && !!this.pickup.pickupPersonIdType
        && !!this.pickup.pickupPersonIdNumber.trim()
        && !this.isSubmitting;
  }

  submitPickup() {
    if (!this.canSubmitPickup) return;
    this.isSubmitting = true;

    const body: RecordPickupRequest = {
      pickupPersonName:     this.pickup.pickupPersonName.trim(),
      pickupPersonIdType:   this.pickup.pickupPersonIdType,
      pickupPersonIdNumber: this.pickup.pickupPersonIdNumber.trim(),
      pickupAuthorization:  this.pickup.pickupAuthorization?.trim() || undefined
    };

    this.logisticsService.recordPickup(this.uuid, body).subscribe({
      next: (res) => {
        this.isSubmitting = false;
        this.pickupDialogVisible = false;
        const result = res.result;
        this.messageService.add({
          severity: 'success',
          summary: 'Collected',
          detail: result?.saleOrderStatus
            ? `Handed over. The sale order is now ${this.formatStatus(result.saleOrderStatus).toLowerCase()}.`
            : 'Handed over and marked as delivered.'
        });
        this.load();
      },
      error: (err) => {
        this.isSubmitting = false;
        this.messageService.add({
          severity: 'error', summary: 'Not allowed',
          detail: err?.error?.message ?? 'The collection could not be recorded.'
        });
      }
    });
  }

  // ── Handing the delivery to a carrier ───────────────────────────────────────

  private emptyConsignment() {
    return { carrierUuid: null as string | null, notes: '' };
  }

  openConsignmentDialog() {
    this.consignment = this.emptyConsignment();
    this.consignmentDialogVisible = true;

    // Only the first time: the list of active carriers does not change while a page is open, and
    // the dialog is usable without it — a carrier can be chosen later, when the parcel is rated.
    if (this.carriers.length) return;

    this.logisticsService.getActiveCarriers().subscribe({
      next: (res) => this.carriers = res.result ?? [],
      error: () => this.carriers = []
    });
  }

  submitConsignment() {
    if (!this.canCreateConsignment || this.isSubmitting) return;
    this.isSubmitting = true;

    const body: CreateConsignmentRequest = {
      deliveryUuids: [this.uuid],
      carrierUuid: this.consignment.carrierUuid || undefined,
      notes: this.consignment.notes.trim() || undefined
    };

    this.logisticsService.createConsignment(body).subscribe({
      next: (res) => {
        this.isSubmitting = false;
        this.consignmentDialogVisible = false;
        this.messageService.add({
          severity: 'success', summary: 'Consignment created',
          detail: 'Book the carrier and follow the parcel from its page.'
        });

        // Straight to the consignment: booking, the label and tracking all live there.
        if (res.result) this.router.navigate(['/portal/pages/logistics/consignments', res.result]);
        else this.load();
      },
      error: (err) => {
        this.isSubmitting = false;
        this.messageService.add({
          severity: 'error', summary: 'Not created',
          detail: err?.error?.message ?? 'The consignment could not be created.'
        });
      }
    });
  }

  openGatePass() {
    this.logisticsService.downloadGatePass(this.uuid).subscribe({
      next: (blob) => {
        const url = URL.createObjectURL(blob);
        window.open(url, '_blank');
        // Give the new tab time to take the URL before it is revoked.
        setTimeout(() => URL.revokeObjectURL(url), 60_000);
      },
      error: (err) => {
        this.messageService.add({
          severity: 'error', summary: 'Not available',
          detail: err?.error?.message ?? 'The gate pass could not be generated.'
        });
      }
    });
  }

  // ── A33 — the delivery's fulfillment route (API-CONTRACT.md §6–§8) ──────────
  //
  // The server maps the route onto the real statuses (D-2/D-3) and sends the tracker (routeSteps) and what can be done
  // next (nextActions). This page draws them and gates each button on the operation's own permission; it keeps no copy
  // of the mapping. A delivery with no route (routeSteps empty) keeps today's page exactly.

  /** True for a delivery raised on a route. Empty routeSteps is the contract's "no route": today's path, unchanged. */
  get hasRoute(): boolean {
    return !!this.delivery?.routeSteps?.length;
  }

  /** The route's steps, then Complete: every route ends at DELIVERED (D-2), even if the server sent the steps alone. */
  get trackerSteps(): RouteStepProgressModel[] {
    const steps = this.delivery?.routeSteps ?? [];
    if (!steps.length || steps[steps.length - 1].stepCode === 'COMPLETE') return steps;

    const status = this.delivery!.status;
    const state: RouteStepProgressModel['state'] =
        COMPLETE_STATUSES.includes(status) ? 'DONE'
      : steps.every(s => s.state === 'DONE') ? 'CURRENT'
      : 'PENDING';
    return [...steps, { stepCode: 'COMPLETE', label: 'Complete', state }];
  }

  trackStep(_: number, step: RouteStepProgressModel): string {
    return step.stepCode;
  }

  stepMarker(step: RouteStepProgressModel): string {
    return step.state === 'DONE' ? '✓' : step.state === 'CURRENT' ? '●' : '○';
  }

  /** Under the current step: the real status, so "Ship ●" says whether the parcel is in transit or not yet booked. */
  stepCaption(step: RouteStepProgressModel): string {
    if (step.state === 'DONE') return 'done';
    if (step.state !== 'CURRENT' || !this.delivery) return '';
    return this.formatStatus(this.delivery.status);
  }

  /** CANCELLED / SHORT_CLOSED / ON_HOLD: the tracker stays where it was, and this says why (contract §8). */
  get trackerStoppedNote(): string | null {
    const d = this.delivery;
    if (!d || !this.hasRoute || !STOPPED_STATUSES.includes(d.status)) return null;
    return `${this.formatStatus(d.status)} — the tracker shows where it stopped.`;
  }

  /** The route offers this action now and the user holds its permission. */
  private routeAllows(action: DeliveryNextAction): boolean {
    const button = ROUTE_ACTION_BUTTONS.find(b => b.action === action);
    return !!button
        && !!this.delivery?.nextActions?.includes(action)
        && this.authService.hasPermission(button.permission);
  }

  /** The forward actions this user can take now, in flow order: the first is the primary button. */
  get routeActions(): RouteActionButton[] {
    if (!this.hasRoute) return [];
    return ROUTE_ACTION_BUTTONS.filter(b => this.routeAllows(b.action));
  }

  /** The next step when it's someone else's to take (no permission): named, so the page doesn't just go quiet. */
  get blockedRouteAction(): RouteActionButton | null {
    if (!this.hasRoute || this.routeActions.length) return null;
    const next = this.delivery?.nextActions ?? [];
    return ROUTE_ACTION_BUTTONS.find(b => next.includes(b.action)) ?? null;
  }

  get routeHasPacking(): boolean {
    return this.trackerSteps.some(s => s.stepCode === 'PACK');
  }

  /**
   * The dock screen. Without a route, today's rule. On a route without PACK the boxing is automatic (D-3) and staging
   * and goods issue are on this page, so there's usually nothing there to do; and when packing is the next step, "Start
   * Packing" already goes there. Except (REV-04b): while an auto-staged delivery's cartons are still amendable, the pack
   * station is where the auto LOOSE unit gets weighed — a courier refuses a package with no weight.
   */
  get showPackStationLink(): boolean {
    if (!this.canOpenPackStation) return false;
    if (!this.hasRoute) return true;
    if (packagesAmendableWhileStaged(this.delivery)) return true;
    return this.routeHasPacking && !this.routeActions.some(a => a.action === 'PACK');
  }

  /** The route's buttons. Steps that need input open the screen or dialog that collects it; never `advance`. */
  performRouteAction(action: DeliveryNextAction) {
    if (this.isSubmitting || !this.routeAllows(action)) return;

    switch (action) {
      case 'RELEASE':            this.openReleaseDialog(); break;
      case 'GENERATE_PICK_LIST': this.generatePickList(); break;
      case 'CONFIRM_PICK':       this.openPickList(); break;
      case 'PACK':               this.router.navigate(['/portal/pages/logistics/deliveries', this.uuid, 'pack']); break;
      case 'STAGE':              this.advance('STAGE'); break;
      case 'APPROVE':            this.approveDispatch(); break;
      case 'GOODS_ISSUE':        this.issueDialogVisible = true; break;
      case 'CREATE_CONSIGNMENT': this.openConsignmentDialog(); break;
      case 'RECORD_COLLECTION':  this.openPickupDialog(); break;
    }
  }

  /** The point of no return, asked for first exactly as the pack station does. */
  confirmGoodsIssue() {
    if (!this.issueDialogVisible || !this.routeAllows('GOODS_ISSUE')) return;
    this.advance('GOODS_ISSUE');
  }

  /**
   * One step that needs no input. `expectedStatus` is the status on screen: if someone else moved the delivery on, the
   * server answers 409 instead of doing a step nobody saw, and the reload shows what it is now.
   */
  private advance(action: DeliveryNextAction) {
    if (this.isSubmitting || !this.delivery) return;
    this.isSubmitting = true;

    this.logisticsService.advanceDelivery(this.uuid, { expectedStatus: this.delivery.status }).subscribe({
      next: (res) => {
        this.isSubmitting = false;
        this.issueDialogVisible = false;
        const done = res.result?.action ?? action;
        this.messageService.add({
          severity: 'success', summary: 'Done',
          detail: ADVANCE_SUCCESS[done] ?? res.message ?? 'Done.'
        });
        this.load();
      },
      error: (err) => {
        this.isSubmitting = false;
        this.issueDialogVisible = false;
        if (err?.status === 409) {
          // Moved on since the page loaded, or the step needs input after all: say what the server said, then show
          // the delivery as it is now.
          this.messageService.add({
            severity: 'warn', summary: 'Not done',
            detail: err?.error?.message ?? 'The delivery has changed since this page loaded.'
          });
          this.load();
          return;
        }
        this.messageService.add({
          severity: 'error', summary: 'Not allowed',
          detail: err?.error?.message ?? 'The step could not be completed.'
        });
      }
    });
  }

  /** D-7, DELIVERY_APPROVE. Its own endpoint rather than advance: a click on "Approve" must never issue the goods. */
  private approveDispatch() {
    if (this.isSubmitting) return;
    this.isSubmitting = true;

    this.logisticsService.approveDelivery(this.uuid).subscribe({
      next: (res) => {
        this.isSubmitting = false;
        // A 200 no-op when someone approved it first (contract §6): worth saying, not worth alarming anyone over.
        const already = !!res.result?.alreadyApproved;
        this.messageService.add({
          severity: already ? 'info' : 'success',
          summary: already ? 'Already approved' : 'Approved',
          detail: res.message || (already ? 'The dispatch had already been approved.' : 'Dispatch approved.')
        });
        this.load();
      },
      error: (err) => {
        this.isSubmitting = false;
        this.messageService.add({
          severity: 'error', summary: 'Not approved',
          detail: err?.error?.message ?? 'The dispatch could not be approved.'
        });
      }
    });
  }

  /** The pick is confirmed on the pick walk, against the list the warehouse is working from. */
  private openPickList() {
    this.logisticsService.getPickListForDelivery(this.uuid).subscribe({
      next: (res) => {
        if (res.result?.uuid) this.router.navigate(['/portal/pages/logistics/picking', res.result.uuid]);
        else this.messageService.add({ severity: 'warn', summary: 'No pick list', detail: 'This delivery has no pick list yet.' });
      },
      error: (err) => {
        this.messageService.add({
          severity: 'error', summary: 'Not available',
          detail: err?.error?.message ?? 'The pick list could not be opened.'
        });
      }
    });
  }

  // ── Display ─────────────────────────────────────────────────────────────────

  /** SMS Flow status strip for a delivery with no route (a routed one shows its own step tracker). */
  get stages(): FlowStage[] {
    const d = this.delivery;
    if (!d) return [];
    if (d.status === 'CANCELLED' || d.status === 'SHORT_CLOSED') {
      return flowStagesFrom(['Open', this.formatStatus(d.status)], 1, { failed: true });
    }
    const labels = ['Draft', 'Released', 'Picking', 'Packing', 'Goods issue', 'Delivery'];
    const at: Record<string, number> = {
      DRAFT: 0, RELEASED: 1, PICKING: 2, PICKED: 3, PACKED: 4, STAGED: 4, PENDING_APPROVAL: 4,
      GOODS_ISSUED: 5, IN_TRANSIT: 5, PARTIALLY_DELIVERED: 5, DELIVERED: 6, CLOSED: 6
    };
    const held = d.status === 'ON_HOLD';
    const current = at[held ? (d.statusBeforeHold ?? '') : d.status] ?? 0;
    return flowStagesFrom(labels, current, { failed: held });
  }

  /** Cached, so the anchors are not re-observed on every change-detection pass. */
  get sections(): FlowSection[] {
    const d = this.delivery;
    const shipment = !!d && !!(d.consignments?.length || this.canCreateConsignment);
    const lines = d?.lines?.length ?? null;
    if (shipment !== this.sectionsShipment || lines !== this.sectionsLines || !this.sectionsCache.length) {
      this.sectionsShipment = shipment;
      this.sectionsLines = lines;
      this.sectionsCache = [
        { id: 'sec-details', label: 'Details' },
        ...(shipment ? [{ id: 'sec-shipment', label: 'Shipment' }] : []),
        { id: 'sec-lines', label: 'Lines', count: lines }
      ];
    }
    return this.sectionsCache;
  }
  private sectionsCache: FlowSection[] = [];
  private sectionsShipment = false;
  private sectionsLines: number | null = null;

  getStatusSeverity(status: string): Severity {
    return DELIVERY_STATUS_SEVERITY[status] ?? 'secondary';
  }

  getShipmentSeverity(status: string): Severity {
    return SHIPMENT_STATUS_SEVERITY[status] ?? 'secondary';
  }

  formatStatus(status: string): string {
    if (!status) return '';
    return status
      .split('_')
      .map(word => word.charAt(0) + word.slice(1).toLowerCase())
      .join(' ');
  }

  /** A line is short when less was delivered than ordered and the delivery has been closed out. */
  isShort(line: { qtyShort: number }): boolean {
    return line.qtyShort > 0;
  }
}
