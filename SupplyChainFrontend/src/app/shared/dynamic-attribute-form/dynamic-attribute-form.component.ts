import { Component, EventEmitter, Input, OnChanges, Output, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormBuilder, FormGroup, ReactiveFormsModule, ValidatorFn, Validators } from '@angular/forms';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { InputNumberModule } from 'primeng/inputnumber';
import { CalendarModule } from 'primeng/calendar';
import { DropdownModule } from 'primeng/dropdown';
import { MultiSelectModule } from 'primeng/multiselect';
import { ToggleSwitchModule } from 'primeng/toggleswitch';
import {
  InventoryService,
  CategoryAttributeModel,
  VariantAttributeValueInput
} from '../../services/inventory.service';

// FSD Addendum 26 §5 — renders whichever attributes are configured for a category, with the
// control type each attribute defines (TEXTBOX/NUMBERBOX/DATEPICKER/TOGGLE/DROPDOWN/
// MULTI_SELECT/TEXTAREA). No category-specific code exists anywhere in here — the same six
// branches in the template handle every industry (laptop, garment, medicine, ...).
//
// Two ways to feed it attributes: pass [categoryId] to fetch GET /api/categories/{id}/attributes
// itself (product/variant forms), or pass [attributes] directly to skip the fetch (the Configure
// Attributes admin screen's live preview, where the list is the unsaved in-memory panel state).
@Component({
  selector: 'app-dynamic-attribute-form',
  standalone: true,
  imports: [
    CommonModule, ReactiveFormsModule,
    InputTextModule, TextareaModule, InputNumberModule, CalendarModule,
    DropdownModule, MultiSelectModule, ToggleSwitchModule
  ],
  template: `
<div class="daf-wrap" *ngIf="!isLoading">
  <div class="daf-empty" *ngIf="attrs.length === 0">
    <i class="pi pi-info-circle"></i> No attributes configured for this category.
  </div>

  <form [formGroup]="form" *ngIf="attrs.length > 0" class="daf-grid">
    <div class="daf-field" *ngFor="let a of attrs">
      <label class="daf-label">
        {{ a.displayName }}
        <span class="daf-required" *ngIf="a.isRequired">*</span>
      </label>

      <ng-container [ngSwitch]="a.controlType">
        <input *ngSwitchCase="'TEXTBOX'" pInputText [formControlName]="a.attributeUuid"
               class="w-full" [placeholder]="a.displayName" />

        <textarea *ngSwitchCase="'TEXTAREA'" pTextarea [formControlName]="a.attributeUuid"
                  rows="2" class="w-full" [placeholder]="a.displayName"></textarea>

        <p-inputNumber *ngSwitchCase="'NUMBERBOX'" [formControlName]="a.attributeUuid"
                        [mode]="a.dataType === 'DECIMAL' ? 'decimal' : 'decimal'"
                        [minFractionDigits]="a.dataType === 'DECIMAL' ? 1 : 0"
                        [maxFractionDigits]="a.dataType === 'DECIMAL' ? 4 : 0"
                        styleClass="w-full" [placeholder]="a.displayName"></p-inputNumber>

        <p-calendar *ngSwitchCase="'DATEPICKER'" [formControlName]="a.attributeUuid"
                    dateFormat="dd/mm/yy" [showIcon]="true" styleClass="w-full" appendTo="body"></p-calendar>

        <p-toggleswitch *ngSwitchCase="'TOGGLE'" [formControlName]="a.attributeUuid"></p-toggleswitch>

        <p-dropdown *ngSwitchCase="'DROPDOWN'" [formControlName]="a.attributeUuid"
                    [options]="optionItems(a)" optionLabel="label" optionValue="value"
                    [placeholder]="'Select ' + a.displayName + '…'"
                    [filter]="true" filterBy="label" [showClear]="!a.isRequired"
                    styleClass="w-full" appendTo="body"></p-dropdown>

        <p-multiSelect *ngSwitchCase="'MULTI_SELECT'" [formControlName]="a.attributeUuid"
                        [options]="optionItems(a)" optionLabel="label" optionValue="value"
                        [placeholder]="'Select ' + a.displayName + '…'"
                        styleClass="w-full" appendTo="body"></p-multiSelect>
      </ng-container>

      <small class="daf-error" *ngIf="isInvalid(a)">
        <ng-container [ngSwitch]="true">
          <span *ngSwitchCase="form.get(a.attributeUuid)?.hasError('required')">{{ a.displayName }} is required.</span>
          <span *ngSwitchCase="form.get(a.attributeUuid)?.hasError('pattern')">{{ a.displayName }} format is invalid.</span>
          <span *ngSwitchCase="form.get(a.attributeUuid)?.hasError('notInteger')">{{ a.displayName }} must be a whole number.</span>
          <span *ngSwitchDefault>{{ a.displayName }} is invalid.</span>
        </ng-container>
      </small>
    </div>
  </form>
</div>
<div class="daf-loading" *ngIf="isLoading"><i class="pi pi-spin pi-spinner"></i> Loading attributes…</div>
  `,
  styles: [`
    :host { display: block; }
    .daf-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 0 1.25rem; }
    .daf-field { display: flex; flex-direction: column; gap: .35rem; margin-bottom: 1.1rem; }
    .daf-label { font-size: .88rem; font-weight: 600; color: #495057; }
    .daf-required { color: #e74c3c; margin-left: .15rem; }
    .daf-error { color: #e74c3c; font-size: .78rem; }
    .daf-empty { color: #94a3b8; font-size: .85rem; padding: 1rem 0; display: flex; align-items: center; gap: .5rem; }
    .daf-loading { color: #94a3b8; font-size: .85rem; padding: 1rem 0; display: flex; align-items: center; gap: .5rem; }
    .w-full { width: 100%; }
  `]
})
export class DynamicAttributeFormComponent implements OnChanges {
  @Input() categoryId: number | null = null;
  @Input() attributes: CategoryAttributeModel[] | null = null;
  @Input() variantUuid: string | null = null;
  @Output() valuesChange = new EventEmitter<VariantAttributeValueInput[]>();
  @Output() validityChange = new EventEmitter<boolean>();

  form: FormGroup;
  attrs: CategoryAttributeModel[] = [];
  isLoading = false;

  private optionCache = new Map<string, { label: string; value: string }[]>();

  constructor(private fb: FormBuilder, private inventoryService: InventoryService) {
    this.form = this.fb.group({});
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['categoryId'] || changes['attributes']) {
      this.loadAttributes();
    } else if (changes['variantUuid'] && !changes['variantUuid'].isFirstChange()) {
      this.loadVariantValues();
    }
  }

  private loadAttributes(): void {
    if (this.attributes) {
      this.attrs = this.attributes;
      this.buildForm();
      return;
    }

    if (this.categoryId == null) {
      this.attrs = [];
      this.buildForm();
      return;
    }

    this.isLoading = true;
    this.inventoryService.getCategoryAttributes(this.categoryId).subscribe({
      next: (res) => {
        this.isLoading = false;
        this.attrs = res.result ?? [];
        this.buildForm();
      },
      error: () => {
        this.isLoading = false;
        this.attrs = [];
        this.buildForm();
      }
    });
  }

  private buildForm(): void {
    const group: Record<string, any> = {};
    for (const a of this.attrs) {
      const validators: ValidatorFn[] = [];
      if (a.isRequired) validators.push(Validators.required);
      if (a.validationRegex) validators.push(Validators.pattern(a.validationRegex));
      if (a.dataType === 'NUMBER') validators.push(integerValidator);

      const defaultValue = a.dataType === 'BOOLEAN' ? false : a.dataType === 'MULTI_SELECT' ? [] : null;
      group[a.attributeUuid] = [defaultValue, validators];
    }

    this.form = this.fb.group(group);
    this.form.valueChanges.subscribe(() => this.emitState());
    this.emitState();

    if (this.variantUuid) this.loadVariantValues();
  }

  private loadVariantValues(): void {
    if (!this.variantUuid || this.attrs.length === 0) return;

    this.inventoryService.getVariantAttributeValues(this.variantUuid).subscribe(res => {
      const values = res.result ?? [];
      const patch: Record<string, any> = {};
      for (const v of values) {
        const attr = this.attrs.find(a => a.attributeUuid === v.attributeUuid);
        if (!attr) continue;
        patch[v.attributeUuid] =
          attr.dataType === 'MULTI_SELECT' ? this.tryParseArray(v.value) :
          attr.dataType === 'BOOLEAN'      ? v.value === 'true' :
          attr.dataType === 'DATE'         ? new Date(v.value) :
          v.value;
      }
      this.form.patchValue(patch, { emitEvent: true });
    });
  }

  private emitState(): void {
    const raw = this.form.value;
    const out: VariantAttributeValueInput[] = [];
    for (const a of this.attrs) {
      const v = raw[a.attributeUuid];
      if (v == null || v === '' || (Array.isArray(v) && v.length === 0)) continue;
      const value =
        a.dataType === 'MULTI_SELECT' ? JSON.stringify(v) :
        a.dataType === 'DATE' && v instanceof Date ? v.toISOString().slice(0, 10) :
        String(v);
      out.push({ attributeUuid: a.attributeUuid, value });
    }
    this.valuesChange.emit(out);
    this.validityChange.emit(this.form.valid);
  }

  private tryParseArray(value: string): string[] {
    try { return JSON.parse(value); } catch { return []; }
  }

  optionItems(a: CategoryAttributeModel): { label: string; value: string }[] {
    if (!a.dropdownOptions) return [];
    const cached = this.optionCache.get(a.attributeUuid);
    if (cached) return cached;
    const items = a.dropdownOptions.map(o => ({ label: o, value: o }));
    this.optionCache.set(a.attributeUuid, items);
    return items;
  }

  isInvalid(a: CategoryAttributeModel): boolean {
    const c = this.form.get(a.attributeUuid);
    return !!c && c.invalid && (c.dirty || c.touched);
  }

  markAllTouched(): void {
    this.form.markAllAsTouched();
  }
}

// The server validates NUMBER via int.TryParse (SMS.Modules.Inventory.Repositories
// .InventoryRepository.ValidateValue) — mirrored client-side so a decimal typed into a NUMBER
// field is caught before submit, not just on the round trip.
function integerValidator(control: { value: any }): { notInteger: true } | null {
  if (control.value == null || control.value === '') return null;
  return Number.isInteger(Number(control.value)) ? null : { notInteger: true };
}
