import { Component, EventEmitter, Input, OnChanges, Output, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormControl, FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { CalendarModule } from 'primeng/calendar';
import { DialogModule } from 'primeng/dialog';
import { DropdownModule } from 'primeng/dropdown';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { ToggleSwitchModule } from 'primeng/toggleswitch';

import { InventoryService } from '../../services/inventory.service';
import { MoneyService } from '../../services/money.service';
import { SupplierService } from '../../services/supplier.service';
import {
  PRICING_DATE_BASIS_OPTIONS, PricelistErrors, PricelistFormValue, PricelistItem, PricelistSaveResult, PricelistUpsert,
  SupplierPricelistService, validatePricelist
} from '../../services/supplier-pricelist.service';
import { DocCurrencyPicker } from '../doc-currency/doc-currency-picker';
import { fromDateOnly, toDateOnly } from '../date-only';

export interface PricelistVariantOption { uuid: string; variantName: string; sku?: string; }

/**
 * A45 §4 C4 — add / edit one supplier price. Context decides the pickers: opened from a product the product is fixed and the
 * vendor is picked; from a supplier the vendor is fixed and the product is picked; from the standalone page both are picked.
 * Validation (price > 0, valid-to >= valid-from, ...) shows on blur and again on save. The overlap warning (C6) is passed to
 * the host in `saved`; the host shows it as a toast — it never blocks the save.
 */
@Component({
  selector: 'app-pricelist-dialog',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, CalendarModule, DialogModule, DropdownModule, InputNumberModule, InputTextModule,
    TextareaModule, ToggleSwitchModule],
  template: `
<p-dialog [visible]="visible" (visibleChange)="close($event)" [modal]="true" [draggable]="false" [style]="{ width: '46rem' }"
          [breakpoints]="{ '900px': '96vw' }" [header]="item ? 'Edit price' : 'Add price'" data-testid="pricelist-dialog">
  <ng-container *ngIf="visible">
    <div class="sf-pill er pd-error" *ngIf="error" role="alert" data-testid="pricelist-error">{{ error }}</div>

    <div class="sf-fields">
      <div class="sf-fld" *ngIf="!fixedProductUuid">
        <label for="pd-product" class="sf-req">Product</label>
        <p-dropdown inputId="pd-product" [options]="productOptions" [(ngModel)]="f.productUuid" optionLabel="label" optionValue="value"
                    [filter]="true" filterBy="label" appendTo="body" styleClass="w-full" placeholder="Select product"
                    (onChange)="touch('productUuid')" data-testid="pd-product"></p-dropdown>
        <small class="pd-err" *ngIf="show('productUuid')">{{ errors.productUuid }}</small>
      </div>
      <div class="sf-fld" *ngIf="fixedProductUuid">
        <label>Product</label>
        <span class="v" data-testid="pd-product-fixed">{{ fixedProductName || '—' }}</span>
      </div>

      <div class="sf-fld" *ngIf="variants.length">
        <label for="pd-variant">Variant</label>
        <p-dropdown inputId="pd-variant" [options]="variantOptions" [(ngModel)]="f.variantUuid" optionLabel="label" optionValue="value"
                    [showClear]="true" placeholder="All variants" appendTo="body" styleClass="w-full" data-testid="pd-variant"></p-dropdown>
      </div>

      <div class="sf-fld" *ngIf="!fixedSupplierUuid">
        <label for="pd-supplier" class="sf-req">Vendor</label>
        <p-dropdown inputId="pd-supplier" [options]="supplierOptions" [(ngModel)]="f.supplierUuid" optionLabel="label" optionValue="value"
                    [filter]="true" filterBy="label" appendTo="body" styleClass="w-full" placeholder="Select vendor"
                    (onChange)="touch('supplierUuid'); onSupplierPicked($event.value)" data-testid="pd-supplier"></p-dropdown>
        <small class="pd-err" *ngIf="show('supplierUuid')">{{ errors.supplierUuid }}</small>
      </div>
      <div class="sf-fld" *ngIf="fixedSupplierUuid">
        <label>Vendor</label>
        <span class="v" data-testid="pd-supplier-fixed">{{ fixedSupplierName || '—' }}</span>
      </div>

      <div class="sf-fld">
        <label for="pd-code">Vendor product code</label>
        <input pInputText id="pd-code" [(ngModel)]="f.vendorProductCode" maxlength="100" data-testid="pd-code" />
      </div>
      <div class="sf-fld">
        <label for="pd-vname">Vendor product name</label>
        <input pInputText id="pd-vname" [(ngModel)]="f.vendorProductName" maxlength="200" data-testid="pd-vname" />
      </div>

      <div class="sf-fld">
        <label for="pd-minqty" class="sf-req">Min quantity</label>
        <p-inputnumber inputId="pd-minqty" [(ngModel)]="f.minQuantity" mode="decimal" [minFractionDigits]="0" [maxFractionDigits]="4" [min]="0"
                       styleClass="w-full" (onBlur)="touch('minQuantity')" data-testid="pd-minqty"></p-inputnumber>
        <small class="pd-err" *ngIf="show('minQuantity')">{{ errors.minQuantity }}</small>
      </div>
      <div class="sf-fld">
        <label for="pd-price" class="sf-req">Unit price</label>
        <p-inputnumber inputId="pd-price" [(ngModel)]="f.unitPrice" mode="decimal" [minFractionDigits]="2" [maxFractionDigits]="4" [min]="0"
                       styleClass="w-full" (onBlur)="touch('unitPrice')" data-testid="pd-price"></p-inputnumber>
        <small class="pd-err" *ngIf="show('unitPrice')" data-testid="pd-price-error">{{ errors.unitPrice }}</small>
      </div>
      <div class="sf-fld">
        <label for="pd-currency" class="sf-req">Currency</label>
        <p-dropdown inputId="pd-currency" [options]="currencyOptions" [(ngModel)]="f.currencyId" optionLabel="label" optionValue="value"
                    appendTo="body" styleClass="w-full" placeholder="Select currency" (onChange)="touch('currencyId'); currencyPickedByHand = true" data-testid="pd-currency"></p-dropdown>
        <small class="pd-err" *ngIf="show('currencyId')">{{ errors.currencyId }}</small>
      </div>
      <div class="sf-fld">
        <label for="pd-basis">Pricing date basis</label>
        <p-dropdown inputId="pd-basis" [options]="basisOptions" [(ngModel)]="f.pricingDateBasis" optionLabel="label" optionValue="value"
                    appendTo="body" styleClass="w-full" data-testid="pd-basis"></p-dropdown>
      </div>

      <div class="sf-fld">
        <label for="pd-from" class="sf-req">Valid from</label>
        <p-calendar inputId="pd-from" [(ngModel)]="f.validFrom" dateFormat="dd M yy" [showIcon]="true" [showButtonBar]="true"
                    todayButtonStyleClass="" clearButtonStyleClass="" appendTo="body" styleClass="w-full"
                    (onSelect)="touch('validFrom'); touch('validTo')" data-testid="pd-from"></p-calendar>
        <small class="pd-err" *ngIf="show('validFrom')">{{ errors.validFrom }}</small>
      </div>
      <div class="sf-fld">
        <label for="pd-to" class="sf-req">Valid to</label>
        <p-calendar inputId="pd-to" [(ngModel)]="f.validTo" dateFormat="dd M yy" [showIcon]="true" [showButtonBar]="true"
                    todayButtonStyleClass="" clearButtonStyleClass="" appendTo="body" styleClass="w-full"
                    (onSelect)="touch('validTo')" data-testid="pd-to"></p-calendar>
        <small class="pd-err" *ngIf="show('validTo')" data-testid="pd-to-error">{{ errors.validTo }}</small>
      </div>

      <div class="sf-fld">
        <label for="pd-lead">Lead time (days)</label>
        <p-inputnumber inputId="pd-lead" [(ngModel)]="f.leadTimeDays" [useGrouping]="false" [min]="0" [max]="9999" styleClass="w-full"
                       (onBlur)="touch('leadTimeDays')" data-testid="pd-lead"></p-inputnumber>
        <small class="pd-err" *ngIf="show('leadTimeDays')">{{ errors.leadTimeDays }}</small>
      </div>
      <div class="sf-fld">
        <label for="pd-priority">Priority</label>
        <p-inputnumber inputId="pd-priority" [(ngModel)]="f.priority" [useGrouping]="false" [min]="0" [max]="9999" styleClass="w-full"
                       (onBlur)="touch('priority')" data-testid="pd-priority"></p-inputnumber>
        <small class="sf-mu sf-sm">Lower number wins.</small>
        <small class="pd-err" *ngIf="show('priority')">{{ errors.priority }}</small>
      </div>

      <div class="sf-fld span-2">
        <label for="pd-notes">Notes</label>
        <textarea pTextarea id="pd-notes" [(ngModel)]="f.notes" rows="2" maxlength="1000" data-testid="pd-notes"></textarea>
      </div>

      <div class="sf-fld pd-active">
        <label for="pd-active">Active</label>
        <p-toggleswitch inputId="pd-active" [(ngModel)]="f.isActive" data-testid="pd-active"></p-toggleswitch>
      </div>
    </div>
  </ng-container>
  <ng-template pTemplate="footer">
    <p-button label="Cancel" [text]="true" severity="secondary" (onClick)="close(false)"></p-button>
    <p-button [label]="item ? 'Save' : 'Add price'" icon="pi pi-check" [loading]="saving" (onClick)="save()" data-testid="pricelist-save"></p-button>
  </ng-template>
</p-dialog>`,
  styles: [`
    .pd-error { display: block; margin-bottom: .75rem; white-space: pre-line; }
    .pd-err { color: var(--sms-danger); font-size: .78rem; }
    .pd-active { flex-direction: row !important; align-items: center; gap: .6rem; }
    :host ::ng-deep .w-full { width: 100%; }
    :host ::ng-deep .p-inputnumber { width: 100%; }
  `]
})
export class PricelistDialogComponent implements OnChanges {
  @Input() visible = false;
  @Output() visibleChange = new EventEmitter<boolean>();
  /** null = a new price. */
  @Input() item: PricelistItem | null = null;
  /** Set when the host is a product: the product is fixed. */
  @Input() fixedProductUuid: string | null = null;
  @Input() fixedProductName: string | null = null;
  /** Set when the host is a supplier: the vendor is fixed. */
  @Input() fixedSupplierUuid: string | null = null;
  @Input() fixedSupplierName: string | null = null;
  /** The fixed product's variants; when given, a variant (default: every variant) can be chosen. */
  @Input() variants: PricelistVariantOption[] = [];
  @Output() saved = new EventEmitter<PricelistSaveResult>();
  /** A stale row (409) while editing: the host should reload. */
  @Output() conflict = new EventEmitter<void>();

  readonly basisOptions = PRICING_DATE_BASIS_OPTIONS;
  productOptions: { label: string; value: string }[] = [];
  supplierOptions: { label: string; value: string }[] = [];

  f: PricelistFormValue = this.blank();
  errors: PricelistErrors = {};
  saving = false;
  error: string | null = null;
  /** A46 C7 - once the user picks a currency themselves, a vendor change no longer overwrites it. */
  currencyPickedByHand = false;
  private submitted = false;
  private touched = new Set<string>();

  /** Org currencies, default = the purchase base (A35 D-9). */
  readonly currencyControl = new FormControl<string | null>(null);
  readonly currency: DocCurrencyPicker;

  constructor(
    private api: SupplierPricelistService,
    private inventory: InventoryService,
    private suppliers: SupplierService,
    money: MoneyService
  ) {
    this.currency = new DocCurrencyPicker(money, this.currencyControl, 'PURCHASE');
    this.currencyControl.valueChanges.subscribe(v => { if (v && !this.f.currencyId) this.f.currencyId = v; });
  }

  get currencyOptions() { return this.currency.options; }
  get variantOptions() { return this.variants.map(v => ({ label: v.sku ? `${v.variantName} (${v.sku})` : v.variantName, value: v.uuid })); }

  ngOnChanges(changes: SimpleChanges): void {
    if (this.visible && (changes['visible'] || changes['item'])) this.reset();
  }

  private blank(): PricelistFormValue {
    return {
      productUuid: null, variantUuid: null, supplierUuid: null, vendorProductCode: null, vendorProductName: null,
      minQuantity: 1, unitPrice: null, currencyId: null, pricingDateBasis: 0, validFrom: null, validTo: null,
      leadTimeDays: null, priority: 0, notes: null, isActive: true
    };
  }

  private reset(): void {
    this.error = null;
    this.submitted = false;
    this.touched.clear();
    const i = this.item;
    this.f = i ? {
      productUuid: i.productUuid, variantUuid: i.variantUuid ?? null, supplierUuid: i.supplierUuid,
      vendorProductCode: i.vendorProductCode ?? null, vendorProductName: i.vendorProductName ?? null,
      minQuantity: i.minQuantity, unitPrice: i.unitPrice, currencyId: i.currencyId, pricingDateBasis: i.pricingDateBasis,
      validFrom: i.validFrom ? fromDateOnly(i.validFrom) : null, validTo: i.validTo ? fromDateOnly(i.validTo) : null,
      leadTimeDays: i.leadTimeDays ?? null, priority: i.priority, notes: i.notes ?? null, isActive: i.isActive
    } : {
      ...this.blank(), productUuid: this.fixedProductUuid, supplierUuid: this.fixedSupplierUuid
    };
    this.currencyPickedByHand = false;
    this.currencyControl.setValue(this.f.currencyId);
    this.currencyControl.markAsPristine();
    this.currency.load(!i);   // a new price starts in the purchase base (valueChanges below copies it into the form)
    this.revalidate();
    if (!i && this.f.supplierUuid) this.applySupplierCurrency(this.f.supplierUuid);   // A46 C7: vendor fixed by the host
    if (!this.fixedProductUuid && !this.productOptions.length) {
      this.inventory.getProducts({ activeOnly: true, pageSize: 500 }).subscribe({
        next: r => this.productOptions = (r?.result?.data ?? []).map(p => ({ label: p.sku ? `${p.name} (${p.sku})` : p.name, value: p.uuid })),
        error: () => { /* the picker stays empty; the save is refused for a missing product */ }
      });
    }
    if (!this.fixedSupplierUuid && !this.supplierOptions.length) {
      this.suppliers.getSuppliers({ status: 'ACTIVE', pageSize: 500 }).subscribe({
        next: r => this.supplierOptions = (r?.result?.data ?? []).map(s => ({ label: s.supplierName, value: s.uuid })),
        error: () => { /* as above */ }
      });
    }
  }

  /** A46 C7 - a vendor picked in the dialog: a new price takes that vendor's default purchase currency. */
  onSupplierPicked(supplierUuid: string | null): void {
    if (this.item || this.currencyPickedByHand) return;
    if (supplierUuid) this.applySupplierCurrency(supplierUuid); else this.setCurrencyDefault(null);
  }

  /**
   * A46 C6/C7 - the vendor's default purchase currency (A35 defaultPurchaseCurrencyId; older servers: preferredCurrency),
   * else the organization's purchase base. A default only - the dropdown stays editable. A failed lookup keeps what is there.
   */
  private applySupplierCurrency(supplierUuid: string): void {
    this.suppliers.getSupplierById(supplierUuid).subscribe({
      next: r => {
        if (this.item || this.currencyPickedByHand || this.f.supplierUuid !== supplierUuid) return;   // stale answer
        const s = r?.result as ({ defaultPurchaseCurrencyId?: string | null; preferredCurrency?: string | null } | null | undefined);
        this.setCurrencyDefault(s?.defaultPurchaseCurrencyId ?? s?.preferredCurrency ?? null);
      },
      error: () => { /* keep the current currency */ }
    });
  }

  private setCurrencyDefault(vendorDefault: string | null): void {
    const options = this.currencyOptions;
    const usable = vendorDefault && (!options.length || options.some(o => o.value === vendorDefault)) ? vendorDefault : null;
    const wanted = usable ?? this.currency.domainBase;
    if (!wanted) return;
    this.f.currencyId = wanted;
    this.currencyControl.setValue(wanted, { emitEvent: false });
  }

  private revalidate(): void { this.errors = validatePricelist(this.f); }

  touch(field: string): void { this.touched.add(field); this.revalidate(); }

  /** A field's error: after it was left, or after a save attempt. */
  show(field: keyof PricelistFormValue): boolean {
    this.revalidate();
    return !!this.errors[field] && (this.submitted || this.touched.has(field));
  }

  close(visible: boolean): void {
    if (!visible) { this.visible = false; this.visibleChange.emit(false); }
  }

  /** The request body for the current form (dates as yyyy-MM-dd). Assumes the form is valid. */
  toRequest(): PricelistUpsert {
    const f = this.f;
    const clean = (s: string | null) => (s?.trim() ? s.trim() : null);
    return {
      productUuid: f.productUuid!, variantUuid: f.variantUuid || null, supplierUuid: f.supplierUuid!,
      vendorProductCode: clean(f.vendorProductCode), vendorProductName: clean(f.vendorProductName),
      minQuantity: f.minQuantity!, unitPrice: f.unitPrice!, currencyId: f.currencyId!, pricingDateBasis: f.pricingDateBasis,
      validFrom: toDateOnly(f.validFrom!), validTo: toDateOnly(f.validTo!),
      leadTimeDays: f.leadTimeDays ?? null, priority: f.priority ?? 0, notes: clean(f.notes), isActive: f.isActive,
      ...(this.item ? { rowVersion: this.item.rowVersion } : {})
    };
  }

  save(): void {
    this.error = null;
    this.submitted = true;
    this.revalidate();
    if (Object.keys(this.errors).length) { this.error = 'Fix the highlighted fields before saving.'; return; }
    const req = this.toRequest();
    this.saving = true;
    (this.item ? this.api.update(this.item.uuid, req) : this.api.create(req)).subscribe({
      next: r => { this.saving = false; this.saved.emit(r); this.close(false); },
      error: err => {
        this.saving = false;
        if (err?.status === 409 && this.item) {
          this.error = err?.error?.message || 'This price was changed by someone else. Close the dialog to reload the latest values, then try again.';
          this.conflict.emit();
        } else {
          this.error = err?.error?.message || 'The price could not be saved.';
        }
      }
    });
  }
}
