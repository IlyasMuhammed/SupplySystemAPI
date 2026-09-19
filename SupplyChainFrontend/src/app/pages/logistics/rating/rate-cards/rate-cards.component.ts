import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TagModule } from 'primeng/tag';
import { TableModule } from 'primeng/table';
import { TooltipModule } from 'primeng/tooltip';
import { ToastModule } from 'primeng/toast';
import { DialogModule } from 'primeng/dialog';
import { InputTextModule } from 'primeng/inputtext';
import { InputNumberModule } from 'primeng/inputnumber';
import { SelectModule } from 'primeng/select';
import { DatePickerModule } from 'primeng/datepicker';
import { MessageService } from 'primeng/api';

import {
  LogisticsService,
  RateCardModel,
  RateCardLaneRequest,
  CarrierServiceModel,
  CarrierListItemModel
} from '../../../../services/logistics.service';

type Severity = 'success' | 'info' | 'warn' | 'danger' | 'secondary' | 'contrast';

/** A lane being edited. Kept apart from the request shape so the form can hold partial input. */
interface LaneDraft {
  name: string;
  originCountryIso: string;
  originPostcodePrefix: string;
  destinationCountryIso: string;
  destinationPostcodePrefix: string;
  breaks: { fromWeightKg: number | null; basis: string; amount: number | null }[];
}

/**
 * Negotiated tariffs for one carrier — what it charges when it will not quote, and what its own
 * quote gets checked against when it will (decision G9).
 *
 * **A card is edited as a whole.** The server replaces every lane when `lanes` is sent, because a
 * card patched break by break would spend a moment with a hole in it — and a card with a hole
 * prices most consignments correctly and one silently wrong. The dialog mirrors that: it loads the
 * whole tariff, and saves the whole tariff.
 */
@Component({
  // Named for the carrier, because suppliers have rate cards too and they are a different thing:
  // that one is what a supplier charges for goods, this is what a carrier charges to move them.
  selector: 'app-carrier-rate-cards',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, TagModule, TableModule, TooltipModule, ToastModule, DialogModule,
    InputTextModule, InputNumberModule, SelectModule, DatePickerModule
  ],
  templateUrl: './rate-cards.component.html',
  styleUrls: ['./rate-cards.component.scss'],
  providers: [MessageService]
})
export class CarrierRateCardsComponent implements OnInit {
  carrierUuid = '';
  carrier: CarrierListItemModel | null = null;
  cards: RateCardModel[] = [];
  services: CarrierServiceModel[] = [];

  isLoading = true;
  isSubmitting = false;

  // ── The editor ──────────────────────────────────────────────────────────────

  dialogVisible = false;
  editing: RateCardModel | null = null;

  form = {
    name: '',
    serviceCode: null as string | null,
    currency: 'PKR',
    effectiveFrom: new Date() as Date | null,
    effectiveTo: null as Date | null,
    minimumCharge: null as number | null,
    fuelSurchargePercent: null as number | null,
    codFeePercent: null as number | null,
    codFeeMinimum: null as number | null
  };

  lanes: LaneDraft[] = [];

  readonly basisOptions = [
    { label: 'Per kilogram', value: 'PER_KG' },
    { label: 'Flat charge',  value: 'FLAT'   }
  ];

  // ── The dry run ─────────────────────────────────────────────────────────────

  quoteDialogVisible = false;
  quoteForm = {
    chargeableWeightKg: 10 as number | null,
    serviceCode: null as string | null,
    originCountryIso: '',
    originPostcode: '',
    destinationCountryIso: '',
    destinationPostcode: ''
  };
  quoteResult: { status: string; explanation?: string; total?: number; currency?: string;
                 cardName?: string; laneName?: string } | null = null;

  constructor(
    private route: ActivatedRoute,
    private logisticsService: LogisticsService,
    private messageService: MessageService
  ) {}

  ngOnInit() {
    this.carrierUuid = this.route.snapshot.paramMap.get('uuid') ?? '';
    this.load();
    this.loadServices();
  }

  load() {
    if (!this.carrierUuid) { this.isLoading = false; return; }

    this.isLoading = true;

    this.logisticsService.getRateCards(this.carrierUuid).subscribe({
      next: (res) => {
        this.isLoading = false;
        this.cards = res.result ?? [];
        this.carrier = this.carrier ?? ({ name: this.cards[0]?.carrierName } as CarrierListItemModel);
      },
      error: (err) => {
        this.isLoading = false;
        this.cards = [];
        this.fail(err, 'The rate cards could not be loaded.');
      }
    });
  }

  /** So a card can name a service the carrier actually sells, rather than a typed string. */
  private loadServices() {
    this.logisticsService.getCarrierServices(this.carrierUuid).subscribe({
      next: (res) => this.services = (res.result ?? []).filter(s => s.isActive),
      error: () => this.services = []
    });
  }

  get serviceOptions() {
    return [
      { label: 'Every service this carrier sells', value: null },
      ...this.services.map(s => ({ label: `${s.serviceName} (${s.serviceCode})`, value: s.serviceCode }))
    ];
  }

  // ── Reading a card ──────────────────────────────────────────────────────────

  /** In effect, configured for later, expired, or switched off — four states, not two. */
  statusOf(card: RateCardModel): { label: string; severity: Severity } {
    if (!card.isActive)  return { label: 'Switched off', severity: 'secondary' };
    if (card.isInEffect) return { label: 'In effect',    severity: 'success'   };

    return new Date(card.effectiveFrom) > new Date()
      ? { label: 'Starts later', severity: 'info' }
      : { label: 'Expired',      severity: 'warn' };
  }

  laneLabel(lane: { name?: string; originCountryIso?: string; originPostcodePrefix?: string;
                    destinationCountryIso?: string; destinationPostcodePrefix?: string }): string {
    if (lane.name) return lane.name;

    const from = this.place(lane.originCountryIso, lane.originPostcodePrefix);
    const to   = this.place(lane.destinationCountryIso, lane.destinationPostcodePrefix);

    if (!from && !to) return 'Anywhere';
    return `${from || 'anywhere'} → ${to || 'anywhere'}`;
  }

  private place(country?: string, prefix?: string): string {
    return [country, prefix].filter(Boolean).join(' ');
  }

  basisLabel(basis: string): string {
    return basis === 'FLAT' ? 'flat' : '/kg';
  }

  // ── Editing ─────────────────────────────────────────────────────────────────

  openCreate() {
    this.editing = null;
    this.form = {
      name: '', serviceCode: null, currency: 'PKR',
      effectiveFrom: new Date(), effectiveTo: null,
      minimumCharge: null, fuelSurchargePercent: null, codFeePercent: null, codFeeMinimum: null
    };
    // A card with no lanes prices nothing, so the editor starts with the catch-all somebody
    // almost always wants.
    this.lanes = [this.newLane('Anywhere')];
    this.dialogVisible = true;
  }

  openEdit(card: RateCardModel) {
    this.editing = card;
    this.form = {
      name: card.name,
      serviceCode: card.serviceCode ?? null,
      currency: card.currency,
      effectiveFrom: new Date(card.effectiveFrom),
      effectiveTo: card.effectiveTo ? new Date(card.effectiveTo) : null,
      minimumCharge: card.minimumCharge ?? null,
      fuelSurchargePercent: card.fuelSurchargePercent ?? null,
      codFeePercent: card.codFeePercent ?? null,
      codFeeMinimum: card.codFeeMinimum ?? null
    };

    this.lanes = card.lanes.map(l => ({
      name: l.name ?? '',
      originCountryIso: l.originCountryIso ?? '',
      originPostcodePrefix: l.originPostcodePrefix ?? '',
      destinationCountryIso: l.destinationCountryIso ?? '',
      destinationPostcodePrefix: l.destinationPostcodePrefix ?? '',
      breaks: l.breaks.map(b => ({
        fromWeightKg: b.fromWeightKg, basis: b.basis, amount: b.amount
      }))
    }));

    this.dialogVisible = true;
  }

  private newLane(name = ''): LaneDraft {
    return {
      name,
      originCountryIso: '', originPostcodePrefix: '',
      destinationCountryIso: '', destinationPostcodePrefix: '',
      // Weight breaks must start at zero, or anything lighter falls through the card. The editor
      // seeds that rather than letting somebody discover it from a server error.
      breaks: [{ fromWeightKg: 0, basis: 'PER_KG', amount: null }]
    };
  }

  addLane()             { this.lanes.push(this.newLane()); }
  removeLane(i: number) { this.lanes.splice(i, 1); }

  addBreak(lane: LaneDraft) {
    lane.breaks.push({ fromWeightKg: null, basis: 'PER_KG', amount: null });
  }

  removeBreak(lane: LaneDraft, i: number) { lane.breaks.splice(i, 1); }

  /**
   * The rules the server enforces, checked here too so the dialog can say which field is wrong
   * rather than surfacing a refusal after a round trip.
   */
  get validationError(): string | null {
    if (!this.form.name.trim())                 return 'A rate card needs a name.';
    if (this.form.currency.trim().length !== 3) return 'A currency is three ISO letters, like PKR.';
    if (!this.form.effectiveFrom)               return 'A rate card needs a date it takes effect from.';

    if (this.form.effectiveTo && this.form.effectiveFrom
        && this.form.effectiveTo < this.form.effectiveFrom)
      return 'A rate card cannot stop applying before it starts.';

    if (!this.lanes.length) return 'A card with no lanes prices nothing. Add at least one.';

    for (const lane of this.lanes) {
      const label = lane.name.trim() || 'this lane';

      if (!lane.breaks.length) return `${label} has no weight breaks, so it prices nothing.`;

      const weights = lane.breaks.map(b => b.fromWeightKg);

      if (!weights.includes(0))
        return `${label} must have a break starting at 0 kg, or anything lighter falls through the card.`;

      if (new Set(weights).size !== weights.length)
        return `${label} has two breaks starting at the same weight. One weight, one rate.`;

      if (lane.breaks.some(b => !b.amount || b.amount <= 0))
        return `Every weight break on ${label} needs a positive rate.`;
    }

    return null;
  }

  get canSave(): boolean {
    return !this.isSubmitting && this.validationError === null;
  }

  private laneRequests(): RateCardLaneRequest[] {
    return this.lanes.map(l => ({
      name: l.name.trim() || undefined,
      originCountryIso: l.originCountryIso.trim() || undefined,
      originPostcodePrefix: l.originPostcodePrefix.trim() || undefined,
      destinationCountryIso: l.destinationCountryIso.trim() || undefined,
      destinationPostcodePrefix: l.destinationPostcodePrefix.trim() || undefined,
      breaks: l.breaks.map(b => ({
        fromWeightKg: b.fromWeightKg ?? 0, basis: b.basis, amount: b.amount ?? 0
      }))
    }));
  }

  save() {
    if (!this.canSave) return;
    this.isSubmitting = true;

    const terms = {
      minimumCharge:        this.form.minimumCharge ?? undefined,
      fuelSurchargePercent: this.form.fuelSurchargePercent ?? undefined,
      codFeePercent:        this.form.codFeePercent ?? undefined,
      codFeeMinimum:        this.form.codFeeMinimum ?? undefined
    };

    const done = (message: string) => {
      this.isSubmitting = false;
      this.dialogVisible = false;
      this.ok(message);
      this.load();
    };

    const failed = (err: any) => {
      this.isSubmitting = false;
      this.fail(err, 'The rate card could not be saved.');
    };

    if (this.editing) {
      // Sending lanes replaces the tariff wholesale, which is how the server models it too.
      this.logisticsService.patchRateCard(this.editing.uuid, {
        name:          this.form.name.trim(),
        effectiveFrom: this.form.effectiveFrom!.toISOString(),
        effectiveTo:   this.form.effectiveTo?.toISOString(),
        ...terms,
        lanes: this.laneRequests(),
        // Cleared explicitly: a null on a patch means "leave alone", so emptying a field on the
        // form would otherwise be silently ignored.
        clearTerms: this.termsToClear()
      }).subscribe({ next: () => done('Rate card updated.'), error: failed });
    } else {
      this.logisticsService.createRateCard({
        carrierUuid:   this.carrierUuid,
        serviceCode:   this.form.serviceCode ?? undefined,
        name:          this.form.name.trim(),
        currency:      this.form.currency.trim().toUpperCase(),
        effectiveFrom: this.form.effectiveFrom!.toISOString(),
        effectiveTo:   this.form.effectiveTo?.toISOString(),
        ...terms,
        lanes: this.laneRequests()
      }).subscribe({ next: () => done('Rate card created.'), error: failed });
    }
  }

  private termsToClear(): string[] | undefined {
    const clear: string[] = [];

    if (!this.form.effectiveTo)          clear.push('EFFECTIVE_TO');
    if (this.form.minimumCharge == null) clear.push('MINIMUM_CHARGE');
    if (this.form.fuelSurchargePercent == null) clear.push('FUEL_SURCHARGE');
    if (this.form.codFeePercent == null && this.form.codFeeMinimum == null) clear.push('COD_FEE');

    return clear.length ? clear : undefined;
  }

  toggleActive(card: RateCardModel) {
    this.isSubmitting = true;

    this.logisticsService.patchRateCard(card.uuid, { isActive: !card.isActive }).subscribe({
      next: () => {
        this.isSubmitting = false;
        this.ok(card.isActive ? 'Rate card switched off.' : 'Rate card switched on.');
        this.load();
      },
      error: (err) => {
        this.isSubmitting = false;
        this.fail(err, 'The rate card could not be changed.');
      }
    });
  }

  remove(card: RateCardModel) {
    this.isSubmitting = true;

    this.logisticsService.deleteRateCard(card.uuid).subscribe({
      next: () => {
        this.isSubmitting = false;
        this.ok('Rate card removed.');
        this.load();
      },
      error: (err) => {
        this.isSubmitting = false;
        this.fail(err, 'The rate card could not be removed.');
      }
    });
  }

  // ── Checking it does what somebody meant ────────────────────────────────────

  openQuoteDialog() {
    this.quoteResult = null;
    this.quoteDialogVisible = true;
  }

  runQuote() {
    if (this.isSubmitting) return;
    this.isSubmitting = true;

    this.logisticsService.quoteRateCard({
      carrierUuid:           this.carrierUuid,
      serviceCode:           this.quoteForm.serviceCode ?? undefined,
      chargeableWeightKg:    this.quoteForm.chargeableWeightKg ?? 0,
      originCountryIso:      this.quoteForm.originCountryIso.trim() || undefined,
      originPostcode:        this.quoteForm.originPostcode.trim() || undefined,
      destinationCountryIso: this.quoteForm.destinationCountryIso.trim() || undefined,
      destinationPostcode:   this.quoteForm.destinationPostcode.trim() || undefined
    }).subscribe({
      next: (res) => {
        this.isSubmitting = false;
        const q = res.result;

        // Every way this can come to nothing is reported, because "no price" and "priced at
        // nothing" look identical on a screen and mean opposite things.
        this.quoteResult = {
          status:      q?.status ?? 'NoCard',
          explanation: q?.explanation,
          total:       q?.option?.totalAmount,
          currency:    q?.option?.currency,
          cardName:    q?.cardName,
          laneName:    q?.laneName
        };
      },
      error: (err) => {
        this.isSubmitting = false;
        this.fail(err, 'The card could not be tried.');
      }
    });
  }

  private ok(detail: string) {
    this.messageService.add({ severity: 'success', summary: 'Done', detail });
  }

  private fail(err: any, fallback: string) {
    this.messageService.add({
      severity: 'error', summary: 'Not allowed',
      detail: err?.error?.message ?? fallback, life: 8000
    });
  }
}
