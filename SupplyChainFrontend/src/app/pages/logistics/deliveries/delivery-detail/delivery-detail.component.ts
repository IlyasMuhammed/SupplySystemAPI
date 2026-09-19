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
  DeliveryDetailModel,
  DeliveryAvailabilityModel,
  RecordPickupRequest,
  PICKUP_ID_TYPES
} from '../../../../services/logistics.service';
import { DELIVERY_STATUS_SEVERITY } from '../delivery-list/delivery-list.component';

type Severity = 'success' | 'info' | 'warn' | 'danger' | 'secondary' | 'contrast';

/** The status a delivery must be able to move to for the action to make sense. */
type ReasonAction = 'hold' | 'cancel' | 'short-close';

/**
 * Where a self-pickup delivery can be collected from. Mirrors the server: packed goods are staged
 * and issued by the collection itself; issued goods are simply handed over.
 */
const COLLECTABLE_STATUSES = ['PACKED', 'STAGED', 'PENDING_APPROVAL', 'GOODS_ISSUED'];

/** A gate pass exists once the goods are at the dock. Same rule as the server's document service. */
const GATE_PASS_STATUSES = ['STAGED', 'PENDING_APPROVAL', 'GOODS_ISSUED', 'IN_TRANSIT', 'DELIVERED', 'PARTIALLY_DELIVERED'];

@Component({
  selector: 'app-delivery-detail',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    TableModule, ButtonModule, TagModule, TooltipModule, ToastModule,
    DialogModule, TextareaModule, DropdownModule, InputTextModule,
    TimelinePanelComponent, AttachmentListComponent
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

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private logisticsService: LogisticsService,
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
  get canRelease(): boolean    { return this.canMoveTo('RELEASED'); }

  /** Picking starts from a released delivery; the server owns the rest of the rule. */
  get canGeneratePickList(): boolean { return this.canMoveTo('PICKING'); }

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
    return this.isSelfPickup && COLLECTABLE_STATUSES.includes(this.delivery?.status ?? '');
  }

  /** Already collected — the pass and the collector's details are the record of it. */
  get isCollected(): boolean {
    return this.isSelfPickup && !!this.delivery?.pickedUpAt;
  }

  get canDownloadGatePass(): boolean {
    return GATE_PASS_STATUSES.includes(this.delivery?.status ?? '');
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

  // ── Display ─────────────────────────────────────────────────────────────────

  getStatusSeverity(status: string): Severity {
    return DELIVERY_STATUS_SEVERITY[status] ?? 'secondary';
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
