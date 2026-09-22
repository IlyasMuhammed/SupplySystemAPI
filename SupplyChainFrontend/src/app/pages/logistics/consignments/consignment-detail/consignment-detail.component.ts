import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';
import { ToastModule } from 'primeng/toast';
import { DialogModule } from 'primeng/dialog';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { SelectModule } from 'primeng/select';
import { TableModule } from 'primeng/table';
import { InputNumberModule } from 'primeng/inputnumber';
import { DatePickerModule } from 'primeng/datepicker';
import { CheckboxModule } from 'primeng/checkbox';
import { MessageService } from 'primeng/api';

import {
  LogisticsService,
  ConsignmentDetailModel,
  ConsignmentBookingStatusModel,
  ConsignmentTrackingEventModel,
  CarrierAccountModel,
  ConsignmentRateModel,
  ConsignmentWeightModel,
  RateShopResultModel,
  RateShopOptionModel,
  RecordProofRequest,
  ShippingRuleDecisionModel
} from '../../../../services/logistics.service';
import { AuthService } from '../../../service/auth.service';

type Severity = 'success' | 'info' | 'warn' | 'danger' | 'secondary' | 'contrast';

/**
 * Where a consignment can be when a person records that the goods were handed over.
 *
 * BOOKED is the one that matters: a carrier booked by hand is silent — nothing scans it — so it is still
 * BOOKED when somebody who watched the handover types it in. Mirrors the server, which refuses anything
 * from which there is no legal route to DELIVERED (a draft, or a consignment already cancelled, lost or
 * returned); the server stays the authority.
 */
const HANDOVER_STATUSES = [
  'BOOKED', 'LABEL_READY', 'PICKUP_REQUESTED', 'PICKED_UP', 'IN_TRANSIT',
  'OUT_FOR_DELIVERY', 'DELIVERY_ATTEMPTED', 'EXCEPTION'
];

export const SHIPMENT_STATUS_SEVERITY: Record<string, Severity> = {
  DRAFT: 'secondary', RATED: 'info', BOOKING: 'warn', BOOKED: 'success',
  BOOKING_FAILED: 'danger', LABEL_READY: 'success', PICKUP_REQUESTED: 'info',
  PICKED_UP: 'info', IN_TRANSIT: 'info', OUT_FOR_DELIVERY: 'info',
  DELIVERY_ATTEMPTED: 'warn', DELIVERED: 'success', EXCEPTION: 'danger',
  RETURNED_TO_ORIGIN: 'warn', LOST: 'danger', CANCELLED: 'secondary'
};

/**
 * One consignment: who carries it, on what airway bill, and where it has got to.
 *
 * The screen is shaped by one fact — **booking is asynchronous**. `POST /book` answers 202 and the
 * carrier call happens in the background, so the button cannot report an outcome. It starts the
 * work, and the screen then polls until the booking settles, which is also what makes a refreshed
 * page pick up a booking somebody else started.
 */
@Component({
  selector: 'app-consignment-detail',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, TagModule, TooltipModule, ToastModule, DialogModule,
    InputTextModule, TextareaModule, SelectModule,
    TableModule, InputNumberModule, DatePickerModule, CheckboxModule
  ],
  templateUrl: './consignment-detail.component.html',
  styleUrls: ['./consignment-detail.component.scss'],
  providers: [MessageService]
})
export class ConsignmentDetailComponent implements OnInit, OnDestroy {
  /** How often to ask while a carrier call is in flight. */
  private static readonly PollMs = 2500;

  /**
   * Stop asking after this long. A booking still in flight after two minutes is not going to
   * resolve while somebody watches, and a page left open overnight should not keep polling.
   */
  private static readonly PollTimeoutMs = 120_000;

  uuid = '';
  consignment: ConsignmentDetailModel | null = null;
  booking: ConsignmentBookingStatusModel | null = null;
  tracking: ConsignmentTrackingEventModel[] = [];
  accounts: CarrierAccountModel[] = [];

  rate: ConsignmentRateModel | null = null;
  weight: ConsignmentWeightModel | null = null;
  shop: RateShopResultModel | null = null;
  ruleDecision: ShippingRuleDecisionModel | null = null;

  isLoading = true;
  notFound = false;
  isSubmitting = false;
  isRefreshingTracking = false;
  isRating = false;
  isShopping = false;

  private pollTimer: ReturnType<typeof setTimeout> | null = null;
  private pollingSince = 0;

  // ── Dialogs ─────────────────────────────────────────────────────────────────

  bookDialogVisible = false;
  bookForm = { carrierAccountUuid: null as string | null, serviceCode: '' };

  manualDialogVisible = false;
  manualForm = { awb: '', carrierReference: '' };

  resolveDialogVisible = false;
  resolveForm = { carrierBooked: true, awb: '', note: '' };

  shopDialogVisible = false;
  shopForm = { strategy: 'CHEAPEST', requiredBy: null as Date | null, rateCardOnly: false };

  manualRateDialogVisible = false;
  manualRateForm = { amount: null as number | null, currency: 'PKR', note: '' };

  handoverDialogVisible = false;
  handoverForm = this.emptyHandover();

  /** A handover cannot have happened in the future; the server refuses it, the picker does not offer it. */
  today = new Date();

  constructor(
    private route: ActivatedRoute,
    private logisticsService: LogisticsService,
    private messageService: MessageService,
    private authService: AuthService
  ) {}

  ngOnInit() {
    this.uuid = this.route.snapshot.paramMap.get('uuid') ?? '';
    this.load();
  }

  ngOnDestroy() {
    // A polling timer that outlives the screen keeps hitting the carrier endpoint forever.
    this.stopPolling();
  }

  load() {
    if (!this.uuid) { this.isLoading = false; this.notFound = true; return; }

    this.isLoading = true;

    this.logisticsService.getConsignmentById(this.uuid).subscribe({
      next: (res) => {
        this.isLoading = false;
        this.consignment = res.result ?? null;
        this.notFound = !this.consignment;

        if (this.consignment) {
          this.loadBooking();
          this.loadTracking();
          this.loadAccounts();
          this.loadRate();
          this.loadWeight();
        }
      },
      error: (err) => {
        this.isLoading = false;
        this.consignment = null;
        this.notFound = err?.status === 404;
        if (!this.notFound) this.fail(err, 'Failed to load the consignment.');
      }
    });
  }

  private loadBooking(afterPoll = false) {
    this.logisticsService.getConsignmentBooking(this.uuid).subscribe({
      next: (res) => {
        this.booking = res.result ?? null;
        // Picks up a booking started elsewhere, or one still running from before a refresh.
        if (this.isBookingInFlight) this.schedulePoll();
        else if (afterPoll) this.onBookingSettled();
      },
      error: () => { /* The detail still renders; the booking panel simply stays empty. */ }
    });
  }

  private loadTracking() {
    this.logisticsService.getConsignmentTracking(this.uuid).subscribe({
      next: (res) => this.tracking = res.result ?? [],
      error: () => this.tracking = []
    });
  }

  /** Only to name the account a booking would go out on; failure is not worth a toast. */
  private loadAccounts() {
    const carrierUuid = this.consignment?.carrierUuid;
    if (!carrierUuid) return;

    this.logisticsService.getCarrierAccounts(carrierUuid).subscribe({
      next: (res) => this.accounts = (res.result ?? []).filter(a => a.isActive),
      error: () => this.accounts = []
    });
  }

  /** The stored quote. Absent is normal, not an error — most consignments are not rated yet. */
  private loadRate() {
    this.logisticsService.getConsignmentRate(this.uuid).subscribe({
      next: (res) => this.rate = res.result ?? null,
      error: () => this.rate = null
    });
  }

  /**
   * What each package is charged on. Read-only, so it is safe to load with the page — and it is
   * what makes a freight cost checkable rather than merely displayed.
   */
  private loadWeight() {
    this.logisticsService.getChargeableWeight(this.uuid).subscribe({
      next: (res) => this.weight = res.result ?? null,
      error: () => this.weight = null
    });
  }

  // ── Where the booking stands ────────────────────────────────────────────────

  get isBookingInFlight(): boolean {
    return this.booking?.status === 'BOOKING'
        || this.booking?.commandStatus === 'IN_FLIGHT';
  }

  /** The carrier's answer is unknown and nothing will retry — a person has to check. */
  get needsResolution(): boolean {
    return this.booking?.needsResolution === true;
  }

  get isManualCarrier(): boolean {
    return (this.consignment?.integrationMode || 'MANUAL') === 'MANUAL';
  }

  get isBooked(): boolean {
    return !!this.booking?.masterAwb;
  }

  get canBook(): boolean {
    if (this.isSubmitting || this.isBookingInFlight || this.needsResolution) return false;
    if (this.isManualCarrier) return false;
    return ['DRAFT', 'RATED', 'BOOKING_FAILED'].includes(this.consignment?.status ?? '');
  }

  get canBookManually(): boolean {
    if (this.isSubmitting || !this.isManualCarrier) return false;
    return ['DRAFT', 'RATED', 'BOOKING_FAILED'].includes(this.consignment?.status ?? '');
  }

  /**
   * A label exists once there is an airway bill. `hasStoredLabel` only says whether it is cached —
   * it is fetched from the carrier on first print, so offering it on the AWB is correct.
   */
  get canPrintLabel(): boolean {
    return this.isBooked;
  }

  get canRefreshTracking(): boolean {
    return this.isBooked && !this.isRefreshingTracking;
  }

  get accountOptions() {
    return [
      { label: 'The carrier’s default account', value: null },
      ...this.accounts.map(a => ({
        label: a.isSandbox ? `${a.accountName} (sandbox)` : a.accountName,
        value: a.uuid
      }))
    ];
  }

  // ── Booking ─────────────────────────────────────────────────────────────────

  openBookDialog() {
    this.bookForm = { carrierAccountUuid: null, serviceCode: '' };
    this.bookDialogVisible = true;
  }

  confirmBook() {
    if (this.isSubmitting) return;
    this.isSubmitting = true;

    this.logisticsService.bookConsignment(this.uuid, {
      carrierAccountUuid: this.bookForm.carrierAccountUuid ?? undefined,
      serviceCode:        this.bookForm.serviceCode.trim() || undefined
    }).subscribe({
      next: (res) => {
        this.isSubmitting = false;
        this.bookDialogVisible = false;
        this.booking = res.result ?? this.booking;

        // 202: the carrier has not answered yet. Saying "booked" here would be a lie the next
        // poll contradicts.
        this.messageService.add({
          severity: 'info', summary: 'Booking requested',
          detail: 'The carrier is being contacted. This updates itself.'
        });

        this.schedulePoll();
      },
      error: (err) => {
        this.isSubmitting = false;
        this.fail(err, 'The booking could not be requested.');
      }
    });
  }

  private schedulePoll() {
    if (this.pollTimer) return;
    if (!this.pollingSince) this.pollingSince = Date.now();

    if (Date.now() - this.pollingSince > ConsignmentDetailComponent.PollTimeoutMs) {
      this.stopPolling();
      this.messageService.add({
        severity: 'warn', summary: 'Still booking',
        detail: 'The carrier has not answered yet. Reopen the consignment to check again.'
      });
      return;
    }

    this.pollTimer = setTimeout(() => {
      this.pollTimer = null;
      this.loadBooking(true);
    }, ConsignmentDetailComponent.PollMs);
  }

  private stopPolling() {
    if (this.pollTimer) clearTimeout(this.pollTimer);
    this.pollTimer = null;
    this.pollingSince = 0;
  }

  /** The booking has settled one way or another; say which, and refresh what it changed. */
  private onBookingSettled() {
    this.stopPolling();

    if (this.booking?.masterAwb) {
      this.messageService.add({
        severity: 'success', summary: 'Booked',
        detail: `Airway bill ${this.booking.masterAwb}.`
      });
    } else if (this.needsResolution) {
      this.messageService.add({
        severity: 'warn', summary: 'Outcome unknown',
        detail: this.booking?.failureReason
             ?? 'The carrier did not answer. Check with them before booking again.',
        life: 8000
      });
    } else if (this.booking?.failureReason) {
      this.messageService.add({
        severity: 'error', summary: 'Not booked', detail: this.booking.failureReason, life: 8000
      });
    }

    // The consignment's own status and airway bill have changed underneath.
    this.logisticsService.getConsignmentById(this.uuid).subscribe({
      next: (res) => this.consignment = res.result ?? this.consignment
    });
  }

  // ── Manual booking ──────────────────────────────────────────────────────────

  openManualDialog() {
    this.manualForm = { awb: '', carrierReference: '' };
    this.manualDialogVisible = true;
  }

  get canSaveManual(): boolean {
    return !this.isSubmitting && !!this.manualForm.awb.trim();
  }

  confirmManual() {
    if (!this.canSaveManual) return;
    this.isSubmitting = true;

    this.logisticsService.bookConsignmentManually(this.uuid, {
      awb:              this.manualForm.awb.trim(),
      carrierReference: this.manualForm.carrierReference.trim() || undefined
    }).subscribe({
      next: () => {
        this.isSubmitting = false;
        this.manualDialogVisible = false;
        this.ok('Consignment booked.');
        this.load();
      },
      error: (err) => {
        this.isSubmitting = false;
        this.fail(err, 'The consignment could not be booked.');
      }
    });
  }

  // ── Recording the handover ──────────────────────────────────────────────────
  //
  // The only way a manually booked consignment ever reaches DELIVERED, and with it the delivery, the
  // sale order and the invoice behind it. An API carrier's scan usually gets there first.

  /** Booked or on its way, and somebody who may record a proof is looking at it. */
  get canRecordHandover(): boolean {
    return !this.isSubmitting
        && HANDOVER_STATUSES.includes(this.consignment?.status ?? '')
        && this.authService.hasPermission('POD_CAPTURE');
  }

  private emptyHandover() {
    return { receivedBy: '', relationship: '', deliveredAt: null as Date | null, notes: '' };
  }

  openHandoverDialog() {
    this.handoverForm = this.emptyHandover();
    // Fresh each time: a page left open all afternoon would otherwise stop offering the last hours.
    this.today = new Date();
    this.handoverDialogVisible = true;
  }

  /** A proof naming nobody is a proof of nothing; the server refuses it, so the button waits. */
  get canSaveHandover(): boolean {
    return !this.isSubmitting && !!this.handoverForm.receivedBy.trim();
  }

  confirmHandover() {
    if (!this.canSaveHandover) return;
    this.isSubmitting = true;

    const body: RecordProofRequest = {
      receivedBy:   this.handoverForm.receivedBy.trim(),
      relationship: this.handoverForm.relationship.trim() || undefined,
      // Left out when not given: the server then stamps the moment it was recorded.
      deliveredAt:  this.handoverForm.deliveredAt?.toISOString(),
      notes:        this.handoverForm.notes.trim() || undefined
    };

    this.logisticsService.recordProof(this.uuid, body).subscribe({
      next: () => {
        this.isSubmitting = false;
        this.handoverDialogVisible = false;
        this.ok('Handover recorded. The consignment is delivered, and its delivery follows.');
        this.load();
      },
      error: (err) => {
        this.isSubmitting = false;
        this.fail(err, 'The handover could not be recorded.');
      }
    });
  }

  // ── Resolving an unknown outcome ────────────────────────────────────────────

  openResolveDialog() {
    this.resolveForm = { carrierBooked: true, awb: '', note: '' };
    this.resolveDialogVisible = true;
  }

  get canResolve(): boolean {
    if (this.isSubmitting || !this.resolveForm.note.trim()) return false;
    // An airway bill is what "the carrier has it" means; without one there is nothing to track.
    return !this.resolveForm.carrierBooked || !!this.resolveForm.awb.trim();
  }

  confirmResolve() {
    if (!this.canResolve) return;
    this.isSubmitting = true;

    this.logisticsService.resolveConsignmentBooking(this.uuid, {
      carrierBooked: this.resolveForm.carrierBooked,
      awb:           this.resolveForm.carrierBooked ? this.resolveForm.awb.trim() : undefined,
      note:          this.resolveForm.note.trim()
    }).subscribe({
      next: (res) => {
        this.isSubmitting = false;
        this.resolveDialogVisible = false;
        this.booking = res.result ?? this.booking;
        this.ok('Booking resolved.');
        this.load();
      },
      error: (err) => {
        this.isSubmitting = false;
        this.fail(err, 'The booking could not be resolved.');
      }
    });
  }

  // ── Label ───────────────────────────────────────────────────────────────────

  printLabel() {
    this.logisticsService.downloadConsignmentLabel(this.uuid).subscribe({
      next: (blob) => {
        // Opened rather than downloaded: the server serves it inline precisely so a browser can
        // go straight to print, which is what a dispatch desk actually does with it.
        const url = URL.createObjectURL(blob);
        const opened = window.open(url, '_blank');

        if (!opened) {
          this.messageService.add({
            severity: 'warn', summary: 'Pop-up blocked',
            detail: 'Allow pop-ups for this site to open the label.'
          });
        }

        // Revoked late: revoking immediately can race the new tab's load.
        setTimeout(() => URL.revokeObjectURL(url), 60_000);
      },
      error: (err) => this.fail(err, 'The label could not be fetched.')
    });
  }

  // ── Tracking ────────────────────────────────────────────────────────────────

  refreshTracking() {
    if (!this.canRefreshTracking) return;
    this.isRefreshingTracking = true;

    this.logisticsService.refreshConsignmentTracking(this.uuid).subscribe({
      next: (res) => {
        this.isRefreshingTracking = false;
        const result = res.result;

        if (result && !result.polled) {
          // Honest rather than pretending: the carrier was asked moments ago and was not asked
          // again, which is a rate limit doing its job, not a failure.
          this.messageService.add({
            severity: 'info', summary: 'Just asked',
            detail: 'The carrier was contacted moments ago. Showing what it said then.'
          });
        } else if (result?.error) {
          this.messageService.add({
            severity: 'warn', summary: 'Carrier did not answer', detail: result.error
          });
        } else {
          this.ok(result?.newEvents
            ? `${result.newEvents} new tracking event(s).`
            : 'Nothing new from the carrier.');
        }

        this.loadTracking();
        this.load();
      },
      error: (err) => {
        this.isRefreshingTracking = false;
        this.fail(err, 'Tracking could not be refreshed.');
      }
    });
  }

  // ── Rating ──────────────────────────────────────────────────────────────────

  /**
   * Pricing is only meaningful before the carrier has the goods. After booking, what it costs is
   * what was booked — the server refuses too, and this keeps the button from offering it.
   */
  get canRate(): boolean {
    if (this.isRating || this.isSubmitting) return false;
    return ['DRAFT', 'RATED', 'BOOKING_FAILED'].includes(this.consignment?.status ?? '');
  }

  get isRated(): boolean {
    return this.rate?.isRated === true;
  }

  /** The carrier's own quote, a negotiated tariff, or somebody's word — they are not equal. */
  sourceLabelFor(source?: string): string {
    switch (source) {
      case 'CARRIER':   return 'Quoted by the carrier';
      case 'RATE_CARD': return 'Priced from a rate card';
      case 'MANUAL':    return 'Entered by hand';
      default:          return 'Not priced';
    }
  }

  sourceSeverityFor(source?: string): Severity {
    switch (source) {
      case 'CARRIER':   return 'success';
      case 'RATE_CARD': return 'info';
      case 'MANUAL':    return 'warn';
      default:          return 'secondary';
    }
  }

  /** Which term decided a package's chargeable weight, as something a human reads. */
  basisLabel(basis: string): string {
    switch (basis) {
      case 'ACTUAL':     return 'Weighed';
      case 'VOLUMETRIC': return 'By volume';
      case 'MINIMUM':    return 'Service minimum';
      default:           return 'Cannot be rated';
    }
  }

  basisSeverity(basis: string): Severity {
    switch (basis) {
      case 'ACTUAL':     return 'secondary';
      case 'VOLUMETRIC': return 'info';
      case 'MINIMUM':    return 'warn';
      default:           return 'danger';
    }
  }

  rateNow(rateCardOnly = false) {
    if (!this.canRate) return;
    this.isRating = true;

    this.logisticsService.rateConsignment(this.uuid, { rateCardOnly }).subscribe({
      next: (res) => {
        this.isRating = false;
        this.rate = res.result ?? this.rate;

        // Both attempts come back whichever succeeded. Saying only "rated" would hide that the
        // carrier refused and a card answered instead, which is the fact worth chasing.
        const carrier = this.rate?.attempts.find(a => a.source === 'CARRIER');

        if (this.rate?.source === 'RATE_CARD' && carrier && !carrier.succeeded) {
          this.messageService.add({
            severity: 'info', summary: 'Priced from the rate card',
            detail: carrier.message ?? 'The carrier did not quote.', life: 8000
          });
        } else {
          this.ok(`Rated at ${this.rate?.freightCurrency} ${this.rate?.freightCost}.`);
        }

        this.afterRating();
      },
      error: (err) => {
        this.isRating = false;
        this.fail(err, 'The consignment could not be priced.');
      }
    });
  }

  /** The status, the carrier and the stored package weights have all moved underneath. */
  private afterRating() {
    this.shop = null;
    this.ruleDecision = null;
    this.load();
  }

  clearRate() {
    if (this.isSubmitting) return;
    this.isSubmitting = true;

    this.logisticsService.clearConsignmentRate(this.uuid).subscribe({
      next: () => {
        this.isSubmitting = false;
        this.rate = null;
        this.ok('Quote discarded.');
        this.afterRating();
      },
      error: (err) => {
        this.isSubmitting = false;
        this.fail(err, 'The quote could not be discarded.');
      }
    });
  }

  // ── A price obtained outside the system ─────────────────────────────────────

  openManualRateDialog() {
    this.manualRateForm = {
      amount: null,
      currency: this.rate?.freightCurrency ?? this.consignment?.codCurrency ?? 'PKR',
      note: ''
    };
    this.manualRateDialogVisible = true;
  }

  get canSaveManualRate(): boolean {
    if (this.isSubmitting) return false;
    // Provenance is required by the server too — a figure nobody can trace cannot be defended
    // when the invoice disagrees with it.
    return (this.manualRateForm.amount ?? 0) > 0
        && this.manualRateForm.currency.trim().length === 3
        && !!this.manualRateForm.note.trim();
  }

  confirmManualRate() {
    if (!this.canSaveManualRate) return;
    this.isSubmitting = true;

    this.logisticsService.setManualRate(this.uuid, {
      amount:   this.manualRateForm.amount!,
      currency: this.manualRateForm.currency.trim().toUpperCase(),
      note:     this.manualRateForm.note.trim()
    }).subscribe({
      next: (res) => {
        this.isSubmitting = false;
        this.manualRateDialogVisible = false;
        this.rate = res.result ?? this.rate;
        this.ok('Freight cost recorded.');
        this.afterRating();
      },
      error: (err) => {
        this.isSubmitting = false;
        this.fail(err, 'The freight cost could not be recorded.');
      }
    });
  }

  // ── Rate shopping ───────────────────────────────────────────────────────────

  openShopDialog() {
    this.shopForm = { strategy: 'CHEAPEST', requiredBy: null, rateCardOnly: false };
    this.shopDialogVisible = true;
  }

  readonly strategyOptions = [
    { label: 'Cheapest first', value: 'CHEAPEST' },
    { label: 'Fastest first',  value: 'FASTEST'  }
  ];

  runShop() {
    if (this.isShopping) return;
    this.isShopping = true;

    this.logisticsService.shopRates(this.uuid, {
      strategy:     this.shopForm.strategy,
      requiredBy:   this.shopForm.requiredBy?.toISOString(),
      rateCardOnly: this.shopForm.rateCardOnly || undefined
    }).subscribe({
      next: (res) => {
        this.isShopping = false;
        this.shopDialogVisible = false;
        this.shop = res.result ?? null;

        if (!this.shop?.options.length) {
          this.messageService.add({
            severity: 'warn', summary: 'Nothing to compare',
            detail: this.shop?.warnings[0] ?? 'No carrier could price this consignment.',
            life: 8000
          });
        }
      },
      error: (err) => {
        this.isShopping = false;
        this.fail(err, 'Rates could not be compared.');
      }
    });
  }

  /** Shopping changes nothing, so an option has to be accepted deliberately. */
  acceptOption(option: RateShopOptionModel) {
    if (this.isSubmitting) return;
    this.isSubmitting = true;

    this.logisticsService.acceptRate(this.uuid, {
      carrierUuid:        option.carrierUuid,
      carrierAccountUuid: option.carrierAccountUuid,
      serviceCode:        option.serviceCode,
      source:             option.source,
      shipDate:           this.shop?.shipDate
    }).subscribe({
      next: (res) => {
        this.isSubmitting = false;
        this.rate = res.result ?? this.rate;
        this.ok(`${option.carrierName} ${option.serviceCode} accepted.`);
        this.afterRating();
      },
      error: (err) => {
        this.isSubmitting = false;
        // The price is re-quoted on accept, so a stale figure is refused rather than stored.
        this.fail(err, 'That option could not be accepted. Compare again and pick from what comes back.');
      }
    });
  }

  // ── Shipping rules ──────────────────────────────────────────────────────────

  evaluateRules() {
    if (this.isShopping) return;
    this.isShopping = true;

    this.logisticsService.evaluateShippingRules(this.uuid).subscribe({
      next: (res) => {
        this.isShopping = false;
        this.ruleDecision = res.result ?? null;

        if (!this.ruleDecision?.matchedRule) {
          this.messageService.add({
            severity: 'warn', summary: 'No rule matches',
            detail: this.ruleDecision?.warnings[0] ?? 'Choose a carrier by hand.', life: 8000
          });
        }
      },
      error: (err) => {
        this.isShopping = false;
        this.fail(err, 'The shipping rules could not be evaluated.');
      }
    });
  }

  get canApplyRule(): boolean {
    return this.canRate && !!this.ruleDecision?.recommended;
  }

  applyRule() {
    if (!this.canApplyRule) return;
    this.isSubmitting = true;

    this.logisticsService.applyShippingRule(this.uuid).subscribe({
      next: (res) => {
        this.isSubmitting = false;
        this.rate = res.result ?? this.rate;
        this.ok(`Applied “${this.ruleDecision?.matchedRule?.name}”.`);
        this.afterRating();
      },
      error: (err) => {
        this.isSubmitting = false;
        this.fail(err, 'The shipping rule could not be applied.');
      }
    });
  }

  // ── Display ─────────────────────────────────────────────────────────────────

  getStatusSeverity(status: string): Severity {
    return SHIPMENT_STATUS_SEVERITY[status] ?? 'secondary';
  }

  formatStatus(status?: string): string {
    if (!status) return '';
    return status.split('_')
      .map(word => word.charAt(0) + word.slice(1).toLowerCase())
      .join(' ');
  }

  /** Where an event came from, as something a human reads. */
  sourceLabel(source: string): string {
    return source === 'WEBHOOK' ? 'Carrier push' : 'Polled';
  }

  private ok(detail: string) {
    this.messageService.add({ severity: 'success', summary: 'Done', detail });
  }

  private fail(err: any, fallback: string) {
    this.messageService.add({
      severity: 'error',
      summary: 'Not allowed',
      detail: err?.error?.message ?? fallback
    });
  }
}
