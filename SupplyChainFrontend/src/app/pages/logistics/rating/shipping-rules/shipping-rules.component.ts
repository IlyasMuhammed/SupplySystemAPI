import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';
import { ToastModule } from 'primeng/toast';
import { DialogModule } from 'primeng/dialog';
import { InputTextModule } from 'primeng/inputtext';
import { InputNumberModule } from 'primeng/inputnumber';
import { TextareaModule } from 'primeng/textarea';
import { SelectModule } from 'primeng/select';
import { MessageService } from 'primeng/api';

import {
  LogisticsService,
  ShippingRuleModel,
  CarrierListItemModel,
  CarrierServiceModel
} from '../../../../services/logistics.service';

/**
 * The standing decisions that route goods to a carrier automatically.
 *
 * **Order is the whole mechanism.** The first matching rule wins and the rest are never looked at,
 * so the screen shows them in priority order, numbered, with each rule's own sentence underneath —
 * a rule list that has to be decoded column by column is a rule list nobody audits.
 */
@Component({
  selector: 'app-shipping-rules',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, TagModule, TooltipModule, ToastModule, DialogModule,
    InputTextModule, InputNumberModule, TextareaModule, SelectModule
  ],
  templateUrl: './shipping-rules.component.html',
  styleUrls: ['./shipping-rules.component.scss'],
  providers: [MessageService]
})
export class ShippingRulesComponent implements OnInit {
  rules: ShippingRuleModel[] = [];
  carriers: CarrierListItemModel[] = [];
  services: CarrierServiceModel[] = [];

  isLoading = true;
  isSubmitting = false;

  dialogVisible = false;
  editing: ShippingRuleModel | null = null;

  form = {
    name: '',
    description: '',
    priority: 10,
    minChargeableWeightKg: null as number | null,
    maxChargeableWeightKg: null as number | null,
    originCountryIso: '',
    originPostcodePrefix: '',
    destinationCountryIso: '',
    destinationPostcodePrefix: '',
    minDeclaredValue: null as number | null,
    maxDeclaredValue: null as number | null,
    hazardous: null as boolean | null,
    cod: null as boolean | null,
    carrierUuid: null as string | null,
    serviceCode: null as string | null,
    strategy: 'CHEAPEST'
  };

  readonly strategyOptions = [
    { label: 'Cheapest first', value: 'CHEAPEST' },
    { label: 'Fastest first',  value: 'FASTEST'  }
  ];

  /** Three states, not two: "for dangerous goods" and "does not care" are different statements. */
  readonly tristateOptions = [
    { label: 'Either',   value: null  },
    { label: 'Only yes', value: true  },
    { label: 'Only no',  value: false }
  ];

  constructor(
    private logisticsService: LogisticsService,
    private messageService: MessageService
  ) {}

  ngOnInit() {
    this.load();
    this.loadCarriers();
  }

  load() {
    this.isLoading = true;

    this.logisticsService.getShippingRules().subscribe({
      next: (res) => {
        this.isLoading = false;
        this.rules = res.result ?? [];
      },
      error: (err) => {
        this.isLoading = false;
        this.rules = [];
        this.fail(err, 'The shipping rules could not be loaded.');
      }
    });
  }

  private loadCarriers() {
    this.logisticsService.getActiveCarriers().subscribe({
      next: (res) => this.carriers = res.result ?? [],
      error: () => this.carriers = []
    });
  }

  /** A service code only means something against a carrier, so the list follows the carrier. */
  onCarrierChange() {
    this.form.serviceCode = null;
    this.services = [];

    if (!this.form.carrierUuid) return;

    this.logisticsService.getCarrierServices(this.form.carrierUuid).subscribe({
      next: (res) => this.services = (res.result ?? []).filter(s => s.isActive),
      error: () => this.services = []
    });
  }

  get carrierOptions() {
    return [
      { label: 'Any carrier — let the strategy choose', value: null },
      ...this.carriers.map(c => ({ label: c.name, value: c.uuid }))
    ];
  }

  get serviceOptions() {
    return [
      { label: 'Any service the carrier sells', value: null },
      ...this.services.map(s => ({ label: `${s.serviceName} (${s.serviceCode})`, value: s.serviceCode }))
    ];
  }

  // ── Editing ─────────────────────────────────────────────────────────────────

  /** The next free priority, so two rules do not collide by accident. */
  private nextPriority(): number {
    return this.rules.length ? Math.max(...this.rules.map(r => r.priority)) + 10 : 10;
  }

  openCreate() {
    this.editing = null;
    this.services = [];
    this.form = {
      name: '', description: '', priority: this.nextPriority(),
      minChargeableWeightKg: null, maxChargeableWeightKg: null,
      originCountryIso: '', originPostcodePrefix: '',
      destinationCountryIso: '', destinationPostcodePrefix: '',
      minDeclaredValue: null, maxDeclaredValue: null,
      hazardous: null, cod: null,
      carrierUuid: null, serviceCode: null, strategy: 'CHEAPEST'
    };
    this.dialogVisible = true;
  }

  openEdit(rule: ShippingRuleModel) {
    this.editing = rule;
    this.form = {
      name: rule.name,
      description: rule.description ?? '',
      priority: rule.priority,
      minChargeableWeightKg: rule.minChargeableWeightKg ?? null,
      maxChargeableWeightKg: rule.maxChargeableWeightKg ?? null,
      originCountryIso: rule.originCountryIso ?? '',
      originPostcodePrefix: rule.originPostcodePrefix ?? '',
      destinationCountryIso: rule.destinationCountryIso ?? '',
      destinationPostcodePrefix: rule.destinationPostcodePrefix ?? '',
      minDeclaredValue: rule.minDeclaredValue ?? null,
      maxDeclaredValue: rule.maxDeclaredValue ?? null,
      hazardous: rule.appliesToHazardous ?? null,
      cod: rule.appliesToCod ?? null,
      carrierUuid: rule.carrierUuid ?? null,
      serviceCode: rule.serviceCode ?? null,
      strategy: rule.strategy
    };

    this.services = [];
    if (rule.carrierUuid) {
      this.logisticsService.getCarrierServices(rule.carrierUuid).subscribe({
        next: (res) => this.services = (res.result ?? []).filter(s => s.isActive),
        error: () => this.services = []
      });
    }

    this.dialogVisible = true;
  }

  /** The rules the server enforces, said here so the dialog names the field rather than the round trip. */
  get validationError(): string | null {
    if (!this.form.name.trim()) return 'A rule needs a name — somebody has to be able to say which rule fired.';

    const clash = this.rules.find(
      r => r.priority === this.form.priority && r.uuid !== this.editing?.uuid);

    if (clash)
      return `'${clash.name}' is already priority ${this.form.priority}. Two rules at one priority `
           + 'would make the one that fires depend on row order.';

    const { minChargeableWeightKg: minW, maxChargeableWeightKg: maxW } = this.form;
    if (minW != null && maxW != null && minW > maxW)
      return `This matches weights from ${minW} kg to ${maxW} kg, which is nothing at all.`;

    const { minDeclaredValue: minV, maxDeclaredValue: maxV } = this.form;
    if (minV != null && maxV != null && minV > maxV)
      return 'This matches a value range that is nothing at all.';

    if (!this.form.carrierUuid && this.form.serviceCode)
      return 'A service belongs to a carrier — the same code means different things at two of them.';

    return null;
  }

  get canSave(): boolean {
    return !this.isSubmitting && this.validationError === null;
  }

  save() {
    if (!this.canSave) return;
    this.isSubmitting = true;

    const body = {
      name:        this.form.name.trim(),
      description: this.form.description.trim() || undefined,
      priority:    this.form.priority,
      minChargeableWeightKg: this.form.minChargeableWeightKg ?? undefined,
      maxChargeableWeightKg: this.form.maxChargeableWeightKg ?? undefined,
      originCountryIso:          this.form.originCountryIso.trim() || undefined,
      originPostcodePrefix:      this.form.originPostcodePrefix.trim() || undefined,
      destinationCountryIso:     this.form.destinationCountryIso.trim() || undefined,
      destinationPostcodePrefix: this.form.destinationPostcodePrefix.trim() || undefined,
      minDeclaredValue: this.form.minDeclaredValue ?? undefined,
      maxDeclaredValue: this.form.maxDeclaredValue ?? undefined,
      appliesToHazardous: this.form.hazardous ?? undefined,
      appliesToCod:       this.form.cod ?? undefined,
      carrierUuid: this.form.carrierUuid ?? undefined,
      serviceCode: this.form.serviceCode ?? undefined,
      strategy:    this.form.strategy
    };

    const done = (message: string) => {
      this.isSubmitting = false;
      this.dialogVisible = false;
      this.ok(message);
      this.load();
    };

    const failed = (err: any) => {
      this.isSubmitting = false;
      this.fail(err, 'The rule could not be saved.');
    };

    if (this.editing) {
      this.logisticsService.patchShippingRule(this.editing.uuid, {
        ...body,
        // Emptied fields are cleared explicitly: a null on a patch means "leave alone", so
        // clearing a condition on the form would otherwise be silently ignored.
        clearConditions: this.conditionsToClear()
      }).subscribe({ next: () => done('Rule updated.'), error: failed });
    } else {
      this.logisticsService.createShippingRule(body)
        .subscribe({ next: () => done('Rule created.'), error: failed });
    }
  }

  private conditionsToClear(): string[] | undefined {
    const clear: string[] = [];

    if (this.form.minChargeableWeightKg == null) clear.push('MIN_WEIGHT');
    if (this.form.maxChargeableWeightKg == null) clear.push('MAX_WEIGHT');
    if (!this.form.originCountryIso.trim())          clear.push('ORIGIN_COUNTRY');
    if (!this.form.originPostcodePrefix.trim())      clear.push('ORIGIN_POSTCODE');
    if (!this.form.destinationCountryIso.trim())     clear.push('DESTINATION_COUNTRY');
    if (!this.form.destinationPostcodePrefix.trim()) clear.push('DESTINATION_POSTCODE');
    if (this.form.minDeclaredValue == null) clear.push('MIN_VALUE');
    if (this.form.maxDeclaredValue == null) clear.push('MAX_VALUE');
    if (this.form.hazardous == null) clear.push('HAZARDOUS');
    if (this.form.cod == null)       clear.push('COD');
    // Clearing the carrier clears the service with it, server-side — a service with no carrier
    // resolves against nothing.
    if (!this.form.carrierUuid)      clear.push('CARRIER');
    else if (!this.form.serviceCode) clear.push('SERVICE');

    return clear.length ? clear : undefined;
  }

  toggleActive(rule: ShippingRuleModel) {
    this.isSubmitting = true;

    this.logisticsService.patchShippingRule(rule.uuid, { isActive: !rule.isActive }).subscribe({
      next: () => {
        this.isSubmitting = false;
        this.ok(rule.isActive ? 'Rule switched off.' : 'Rule switched on.');
        this.load();
      },
      error: (err) => {
        this.isSubmitting = false;
        this.fail(err, 'The rule could not be changed.');
      }
    });
  }

  remove(rule: ShippingRuleModel) {
    this.isSubmitting = true;

    this.logisticsService.deleteShippingRule(rule.uuid).subscribe({
      next: () => {
        this.isSubmitting = false;
        this.ok('Rule removed.');
        this.load();
      },
      error: (err) => {
        this.isSubmitting = false;
        this.fail(err, 'The rule could not be removed.');
      }
    });
  }

  // ── Reading the list ────────────────────────────────────────────────────────

  /**
   * A list with no catch-all leaves some consignments unroutable, and the symptom only shows up
   * when somebody tries to apply a rule to one of them.
   */
  get missingCatchAll(): boolean {
    const active = this.rules.filter(r => r.isActive);
    return active.length > 0 && !active.some(r => r.summary.startsWith('Anything'));
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
