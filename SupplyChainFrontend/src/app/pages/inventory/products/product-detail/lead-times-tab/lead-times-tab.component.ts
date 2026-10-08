import { Component, Input, OnChanges, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { InputNumberModule } from 'primeng/inputnumber';
import { SelectModule } from 'primeng/select';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';

import { ProductVariantModel } from '../../../../../services/inventory.service';
import { routeCategoryLabel, routeCategorySeverity } from '../../../../../services/fulfillment-routes.service';
import {
  LeadTimeService, MAX_VARIANT_LEAD_DAYS, ManufacturingLeadTimeNodeModel, UpdateVariantLeadTimesRequest, VARIANT_LEAD_TIME_FIELDS,
  VariantLeadTimeComponentModel, VariantLeadTimesModel, leadTimeSourceLabel, leadTimeSourceSeverity
} from '../../../../../services/lead-time.service';

type Draft = Record<keyof UpdateVariantLeadTimesRequest, number | null>;

/**
 * A34-PB-07 (D-26) — the "Lead Times" tab on product detail (spec §11.1, contract §4.5/§4.6). Pick a variant, then see
 * and edit its eight lead-time components:
 * - Components the route doesn't use are hidden (BR-C3-04/05: the server's `visible`, judged on the variant's route or
 *   the org's SHIP default).
 * - Each row shows the days and where they come from (Variant override / Org default / Supplier / Product / BOM /
 *   System default). Clearing an override shows what it falls back to (`defaultDays` / `defaultSource`).
 * - The total counts `includedInTotal` components (SUPPLIER is shown but not counted on a MANUFACTURE route). It is the
 *   server's total while nothing is changed, and follows the edits live otherwise.
 * - Save sends all eight (PUT replaces them all; null = use the default). "Reset to org defaults" clears every
 *   override in the form; Save then sends them. Editable with STOCK_MANAGE (`canEdit`), read-only otherwise.
 * - "Recalculate from BOM" (MANUFACTURE route, manufactured product, a calculate permission) shows the BOM-aware
 *   manufacturing total from calculate-manufacturing; it never writes (D-12).
 */
@Component({
  selector: 'app-lead-times-tab',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, InputNumberModule, SelectModule, TagModule, TooltipModule],
  templateUrl: './lead-times-tab.component.html',
  styleUrls: ['./lead-times-tab.component.scss']
})
export class LeadTimesTabComponent implements OnChanges {
  @Input() variants: ProductVariantModel[] = [];
  /** STOCK_MANAGE. */
  @Input() canEdit = false;
  /** Any of the calculate codes (contract §2). */
  @Input() canCalculate = false;
  /** The product's supply method is MANUFACTURE (calculate-manufacturing refuses anything else). */
  @Input() productManufactured = false;

  readonly maxDays = MAX_VARIANT_LEAD_DAYS;
  readonly sourceLabel = leadTimeSourceLabel;
  readonly sourceSeverity = leadTimeSourceSeverity;
  readonly categoryLabel = routeCategoryLabel;
  readonly categorySeverity = routeCategorySeverity;

  variantUuid: string | null = null;
  model: VariantLeadTimesModel | null = null;
  draft: Draft = emptyDraft();
  loading = false;
  loadFailed = false;
  saving = false;
  saveError = '';
  submitted = false;

  bomQuantity = 1;
  bomResult: ManufacturingLeadTimeNodeModel | null = null;
  calculating = false;
  bomError = '';

  constructor(private service: LeadTimeService) {}

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['variants'] && (!this.variantUuid || !this.variants.some(v => v.uuid === this.variantUuid))) {
      const first = this.variants.find(v => v.isDefault) ?? this.variants[0];
      if (first) this.selectVariant(first.uuid);
    }
  }

  get variantOptions(): { label: string; value: string }[] {
    return this.variants.map(v => ({ label: `${v.variantName} (${v.sku})${v.isActive === false ? ' — inactive' : ''}`, value: v.uuid }));
  }

  selectVariant(uuid: string | null): void {
    if (!uuid) return;
    this.variantUuid = uuid;
    this.bomResult = null;
    this.bomError = '';
    this.load();
  }

  load(): void {
    if (!this.variantUuid) return;
    const asked = this.variantUuid;
    this.loading = true;
    this.loadFailed = false;
    this.service.getVariantLeadTimes(asked).subscribe({
      next: res => {
        if (asked !== this.variantUuid) return;
        this.loading = false;
        this.apply(res.result);
      },
      error: () => {
        if (asked !== this.variantUuid) return;
        this.loading = false;
        this.loadFailed = true;
        this.model = null;
      }
    });
  }

  private apply(model: VariantLeadTimesModel): void {
    this.model = model;
    this.draft = draftFrom(model);
    this.submitted = false;
    this.saveError = '';
  }

  // ── Rows ────────────────────────────────────────────────────────────────────────────────────────────────

  get rows(): VariantLeadTimeComponentModel[] { return this.model?.components.filter(c => c.visible) ?? []; }

  get hiddenNames(): string[] { return this.model?.components.filter(c => !c.visible).map(c => c.name) ?? []; }

  private field(row: VariantLeadTimeComponentModel): keyof Draft { return row.field as keyof Draft; }

  value(row: VariantLeadTimeComponentModel): number | null { return this.draft[this.field(row)] ?? null; }

  /** What the row counts for now: the form's override, else what a cleared override falls back to. */
  effectiveDays(row: VariantLeadTimeComponentModel): number | null {
    const typed = this.value(row);
    if (typed !== null) return typed;
    if (row.defaultDays !== undefined && row.defaultDays !== null) return row.defaultDays;
    return row.storedDays === null || row.storedDays === undefined ? row.resolvedDays : null;
  }

  effectiveSource(row: VariantLeadTimeComponentModel): string {
    if (this.value(row) !== null) return 'VARIANT';
    return row.defaultSource ?? (row.storedDays === null || row.storedDays === undefined ? row.source : '');
  }

  counted(row: VariantLeadTimeComponentModel): boolean { return row.includedInTotal ?? row.visible; }

  /** The server's total while unchanged; the live sum once edited (null when a fallback is unknown). */
  get total(): number | null {
    if (!this.model) return null;
    if (!this.dirty) return this.model.totalDays;
    let sum = 0;
    for (const row of this.model.components.filter(c => this.counted(c))) {
      const days = this.effectiveDays(row);
      if (days === null) return null;
      sum += days;
    }
    return sum;
  }

  get dirty(): boolean {
    if (!this.model) return false;
    const saved = draftFrom(this.model);
    return VARIANT_LEAD_TIME_FIELDS.some(f => (this.draft[f] ?? null) !== saved[f]);
  }

  get problem(): string | null {
    for (const row of this.model?.components ?? []) {
      const v = this.value(row);
      if (v !== null && (!Number.isInteger(v) || v < 0 || v > MAX_VARIANT_LEAD_DAYS)) {
        return `${row.name}: a whole number of days between 0 and ${MAX_VARIANT_LEAD_DAYS}.`;
      }
    }
    return null;
  }

  onDaysChange(field: string, value: number | null): void {
    if (!this.canEdit || !(field in this.draft)) return;
    this.draft = { ...this.draft, [field]: value === undefined ? null : value };
  }

  /** Clears every override in the form (spec "Reset to Org Defaults"); Save sends them. */
  resetToDefaults(): void {
    if (!this.canEdit) return;
    this.draft = emptyDraft();
  }

  discard(): void {
    if (this.model) this.apply(this.model);
  }

  save(): void {
    this.submitted = true;
    if (!this.canEdit || !this.model || !this.variantUuid || this.saving || this.problem) return;
    this.saving = true;
    this.saveError = '';
    const asked = this.variantUuid;
    this.service.updateVariantLeadTimes(asked, { ...this.draft }).subscribe({
      next: res => {
        this.saving = false;
        if (asked === this.variantUuid) this.apply(res.result);
      },
      error: err => {
        this.saving = false;
        this.saveError = err?.error?.message || 'The lead times could not be saved.';
      }
    });
  }

  // ── Recalculate from BOM (view only) ────────────────────────────────────────────────────────────────────

  get canRecalculate(): boolean {
    return this.canCalculate && this.productManufactured && this.model?.routeCategory === 'MANUFACTURE';
  }

  recalculate(): void {
    if (!this.canRecalculate || !this.variantUuid || this.calculating) return;
    const quantity = this.bomQuantity && this.bomQuantity > 0 ? this.bomQuantity : 1;
    const asked = this.variantUuid;
    this.calculating = true;
    this.bomError = '';
    this.service.calculateManufacturing({ variantUuid: asked, quantity }).subscribe({
      next: res => {
        this.calculating = false;
        if (asked === this.variantUuid) this.bomResult = res.result;
      },
      error: err => {
        this.calculating = false;
        this.bomError = err?.error?.message || 'The BOM lead time could not be calculated.';
      }
    });
  }

  /** The longest component wait at the top level (what the level days are added to). */
  get longestWait(): number {
    return Math.max(0, ...(this.bomResult?.inputs ?? []).map(i => i.waitDays));
  }
}

function emptyDraft(): Draft {
  return Object.fromEntries(VARIANT_LEAD_TIME_FIELDS.map(f => [f, null])) as Draft;
}

function draftFrom(model: VariantLeadTimesModel): Draft {
  const draft = emptyDraft();
  for (const c of model.components) {
    if (c.field in draft) draft[c.field as keyof Draft] = c.storedDays ?? null;
  }
  return draft;
}
