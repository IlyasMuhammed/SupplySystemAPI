import { Component, Input, OnChanges, SimpleChanges, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { CheckboxModule } from 'primeng/checkbox';
import { DropdownModule } from 'primeng/dropdown';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { MessageService } from 'primeng/api';

import {
  DEFAULT_PRODUCT_POS_SETTINGS, PosAdminService, ProductPosSettings, isHexColor, posError
} from '../../../services/pos-admin.service';
import { InventoryService } from '../../../services/inventory.service';

/** What the tab shows read-only from the product's default variant. */
export interface PosTabVariant { sku?: string | null; barcode?: string | null; sellingPrice?: number | null; isAvailableForPos?: boolean; }

/**
 * A38 §17.7.5 — the product's "POS" tab (API-CONTRACT §6): POS name, POS category, tile colour, image, available on POS,
 * favourite, weighable, sort order. Barcode and selling price stay on the variant and are shown read-only here.
 * A sub-component (no sf-page); the product page renders it only with MODULE_POS, a saleable product and POS_PRODUCT_SETTINGS.
 */
@Component({
  selector: 'app-product-pos-tab',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, CheckboxModule, DropdownModule, InputNumberModule, InputTextModule],
  template: `
<div class="sf-card" data-testid="pos-tab">
  <div class="sf-ch"><h2>Point of sale</h2><span class="sf-mu sf-sm" *ngIf="settings.modifiedAt">changed {{ settings.modifiedAt | date:'dd MMM yyyy, HH:mm' }}</span></div>
  <div *ngIf="loading" class="sf-empty"><i class="pi pi-spin pi-spinner"></i> Loading…</div>
  <div *ngIf="loadError" class="sf-empty" data-testid="pos-load-error">{{ loadError }}</div>

  <ng-container *ngIf="!loading && !loadError">
    <div class="sf-pill er pt-err" *ngIf="error" role="alert" data-testid="pos-error">{{ error }}</div>
    <div class="sf-fields">
      <div class="sf-fld">
        <label for="posName">POS name</label>
        <input pInputText id="posName" [(ngModel)]="settings.posName" maxlength="40" [disabled]="!canEdit" placeholder="Short name on the till button"
               data-testid="pos-name" />
      </div>
      <div class="sf-fld">
        <label for="posCat">POS category</label>
        <p-dropdown inputId="posCat" [options]="categoryOptions" [(ngModel)]="settings.posCategoryId" optionLabel="label" optionValue="value"
                    [showClear]="true" placeholder="The product's own category" [disabled]="!canEdit" appendTo="body" styleClass="w-full"
                    data-testid="pos-category"></p-dropdown>
      </div>
      <div class="sf-fld">
        <label for="posColor">Tile colour</label>
        <div class="pt-color">
          <input type="color" [ngModel]="settings.color || '#ffffff'" (ngModelChange)="settings.color = $event" [disabled]="!canEdit"
                 aria-label="Pick a colour" data-testid="pos-color-picker" />
          <input pInputText id="posColor" [(ngModel)]="settings.color" maxlength="7" placeholder="#RRGGBB" [disabled]="!canEdit"
                 data-testid="pos-color" />
          <p-button *ngIf="settings.color && canEdit" icon="pi pi-times" [text]="true" severity="secondary" size="small" ariaLabel="No colour"
                    (onClick)="settings.color = null"></p-button>
        </div>
        <small class="pt-bad" *ngIf="colorInvalid" data-testid="pos-color-error">Use #RRGGBB, e.g. #1E88E5.</small>
      </div>
      <div class="sf-fld">
        <label for="posImg">Image URL</label>
        <input pInputText id="posImg" [(ngModel)]="settings.imageUrl" maxlength="500" placeholder="Leave empty to use the product image"
               [disabled]="!canEdit" data-testid="pos-image" />
      </div>
      <div class="sf-fld">
        <label for="posSort">Sort order</label>
        <p-inputnumber inputId="posSort" [(ngModel)]="settings.sortOrder" [min]="0" [max]="9999" [useGrouping]="false" [disabled]="!canEdit"
                       styleClass="w-full" data-testid="pos-sort"></p-inputnumber>
      </div>
      <div class="sf-fld pt-checks">
        <span><p-checkbox inputId="posActive" [(ngModel)]="settings.isPosActive" [binary]="true" [disabled]="!canEdit" data-testid="pos-active"></p-checkbox>
          <label for="posActive">Available on POS</label></span>
        <span><p-checkbox inputId="posFav" [(ngModel)]="settings.isFavorite" [binary]="true" [disabled]="!canEdit" data-testid="pos-favorite"></p-checkbox>
          <label for="posFav">Favourite (quick button)</label></span>
        <span><p-checkbox inputId="posWeigh" [(ngModel)]="settings.isWeighable" [binary]="true" [disabled]="!canEdit" data-testid="pos-weighable"></p-checkbox>
          <label for="posWeigh">Sold by weight</label></span>
      </div>
    </div>

    <div class="sf-ch pt-sub"><h3>From the default variant</h3></div>
    <div class="sf-kv" data-testid="pos-variant">
      <span>SKU</span><span class="sf-mono">{{ variant?.sku || '—' }}</span>
      <span>Barcode</span><span class="sf-mono" data-testid="pos-barcode">{{ variant?.barcode || 'none' }}</span>
      <span>Selling price</span><span data-testid="pos-price">{{ variant?.sellingPrice != null ? (variant!.sellingPrice | number:'1.2-2') : '—' }}</span>
      <span>Variant on POS</span><span>{{ variant?.isAvailableForPos === false ? 'No — the variant is not available for POS' : 'Yes' }}</span>
    </div>
    <small class="sf-mu" data-testid="pos-variant-hint">Barcode, selling price and POS availability of each variant are edited on the Variants tab;
      price rules on the Pricing tab.</small>

    <div class="sf-fbar pt-bar" *ngIf="canEdit">
      <span class="sf-mu sf-sm">Tills pick changes up on their next catalog sync.</span>
      <span class="sf-end">
        <p-button label="Save POS settings" icon="pi pi-check" [loading]="saving" [disabled]="colorInvalid" (onClick)="save()" data-testid="pos-save"></p-button>
      </span>
    </div>
  </ng-container>
</div>
  `,
  styles: [`
    .pt-err { display: block; margin-bottom: .75rem; white-space: pre-line; }
    .pt-color { display: flex; gap: .5rem; align-items: center; }
    .pt-color input[type=color] { width: 2.5rem; height: 2.25rem; padding: 0; border: 1px solid var(--sms-border); border-radius: var(--sms-radius-sm); background: var(--sms-input-bg); }
    .pt-checks { display: flex; flex-direction: column; gap: .5rem; }
    .pt-checks span { display: flex; align-items: center; gap: .5rem; }
    .pt-bad { color: var(--sms-danger); }
    .pt-sub { margin-top: 1rem; }
    .pt-bar { margin-top: 1rem; }
    :host ::ng-deep .w-full { width: 100%; }
  `]
})
export class ProductPosTabComponent implements OnChanges {
  private readonly pos = inject(PosAdminService);
  private readonly inventory = inject(InventoryService);
  /** The host page's toast, when it has one. */
  private readonly messages = inject(MessageService, { optional: true });

  @Input() productUuid = '';
  @Input() variant: PosTabVariant | null = null;
  /** POS_PRODUCT_SETTINGS (the page only shows the tab with it; kept for a read-only use). */
  @Input() canEdit = true;

  settings: ProductPosSettings = { ...DEFAULT_PRODUCT_POS_SETTINGS };
  categoryOptions: { label: string; value: number }[] = [];
  loading = false;
  loadError: string | null = null;
  saving = false;
  error: string | null = null;

  get colorInvalid(): boolean { return !!this.settings.color && !isHexColor(this.settings.color); }

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['productUuid'] && this.productUuid) this.load();
  }

  load(): void {
    this.loading = true;
    this.loadError = null;
    this.pos.getProductSettings(this.productUuid).subscribe({
      next: res => { this.loading = false; this.settings = { ...DEFAULT_PRODUCT_POS_SETTINGS, ...(res.result ?? {}) }; },
      error: err => { this.loading = false; this.loadError = posError(err, 'The POS settings could not be loaded.'); }
    });
    if (!this.categoryOptions.length) {
      this.inventory.getCategories().subscribe({
        next: res => this.categoryOptions = (res.result ?? []).filter(c => c.isActive).map(c => ({ label: c.name, value: c.id })),
        error: () => this.categoryOptions = []
      });
    }
  }

  save(): void {
    if (this.colorInvalid || this.saving) return;
    this.saving = true;
    this.error = null;
    const trim = (v: string | null | undefined) => (v ?? '').trim() || null;
    const req: ProductPosSettings = {
      posName: trim(this.settings.posName),
      posCategoryId: this.settings.posCategoryId ?? null,
      color: this.settings.color ? this.settings.color.toUpperCase() : null,
      imageUrl: trim(this.settings.imageUrl),
      isWeighable: !!this.settings.isWeighable,
      isFavorite: !!this.settings.isFavorite,
      sortOrder: this.settings.sortOrder ?? 0,
      isPosActive: !!this.settings.isPosActive
    };
    this.pos.saveProductSettings(this.productUuid, req).subscribe({
      next: res => {
        this.saving = false;
        if (res && res.success === false) { this.error = res.message || 'Not saved.'; return; }
        this.settings = { ...DEFAULT_PRODUCT_POS_SETTINGS, ...(res.result ?? req) };
        this.messages?.add({ severity: 'success', summary: 'Saved', detail: 'POS settings saved.' });
      },
      error: err => { this.saving = false; this.error = posError(err, 'Not saved.'); }
    });
  }
}
