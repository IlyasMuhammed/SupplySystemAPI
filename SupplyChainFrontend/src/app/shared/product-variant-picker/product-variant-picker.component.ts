import { Component, EventEmitter, Input, OnChanges, Output, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { DropdownModule } from 'primeng/dropdown';
import {
  InventoryService,
  ProductListItemModel,
  ProductVariantModel
} from '../../services/inventory.service';
import { AttachmentService } from '../../services/attachment.service';

export interface VariantPickerSelection {
  productUuid: string | null;
  productName: string | null;
  variantId: number | null;
  variantUuid: string | null;
  variantSku: string | null;
  variantName: string | null;
  purchasePrice: number | null;
  uomCode: string | null;
}

// PV-004 §7.1/7.2 — two-level picker: Step 1 searchable product dropdown (product name + SKU),
// Step 2 variant dropdown (variant name + SKU + purchase price). For products with a single
// variant (the common case — every simple product still gets exactly one is_default=true variant
// per PV-001), the variant dropdown auto-selects and hides itself so the user only ever sees one
// control for single-SKU items like Cement.
@Component({
  selector: 'app-product-variant-picker',
  standalone: true,
  imports: [CommonModule, FormsModule, DropdownModule],
  template: `
<div class="pvp-wrap">
  <div class="pvp-row">
    <img *ngIf="selectedProduct?.imageUrl" [src]="resolveImageUrl(selectedProduct!.imageUrl!)"
         alt="" class="pvp-thumb" />
    <div class="pvp-fields">
      <p-dropdown
        [options]="productOptions"
        [(ngModel)]="selectedProductUuid"
        (onChange)="onProductChange()"
        optionLabel="label" optionValue="value"
        [filter]="true" filterBy="label"
        placeholder="Select product…"
        [showClear]="!required"
        styleClass="w-full"
        appendTo="body">
      </p-dropdown>

      <p-dropdown
        *ngIf="showVariantDropdown"
        [options]="variantOptions"
        [(ngModel)]="selectedVariantUuid"
        (onChange)="onVariantChange()"
        optionLabel="label" optionValue="value"
        placeholder="Select variant…"
        styleClass="w-full pvp-variant"
        appendTo="body">
      </p-dropdown>

      <div class="pvp-single-variant" *ngIf="!showVariantDropdown && singleVariant as v">
        <i class="pi pi-tag"></i> {{ v.variantName }} <span class="pvp-sku">({{ v.sku }})</span>
      </div>
    </div>
  </div>
</div>
  `,
  styles: [`
    .pvp-wrap { display: flex; flex-direction: column; gap: .4rem; }
    .pvp-row { display: flex; align-items: flex-start; gap: .6rem; }
    .pvp-fields { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: .4rem; }
    .pvp-thumb {
      width: 38px; height: 38px; object-fit: cover; border-radius: 8px;
      border: 1px solid #e5e7eb; flex-shrink: 0; margin-top: .1rem;
    }
    .pvp-variant { margin-top: 0; }
    .pvp-single-variant {
      font-size: .8rem; color: #64748b; display: flex; align-items: center; gap: .35rem;
    }
    .pvp-sku { color: #94a3b8; }
    .w-full { width: 100%; }
  `]
})
export class ProductVariantPickerComponent implements OnChanges {
  @Input() products: ProductListItemModel[] = [];
  @Input() productUuid: string | null = null;
  @Input() variantUuid: string | null = null;
  @Input() required = false;
  @Output() selectionChange = new EventEmitter<VariantPickerSelection>();

  selectedProductUuid: string | null = null;
  selectedVariantUuid: string | null = null;

  productOptions: { label: string; value: string }[] = [];
  variantOptions: { label: string; value: string }[] = [];
  variants: ProductVariantModel[] = [];
  showVariantDropdown = false;
  singleVariant: ProductVariantModel | null = null;

  private productsById = new Map<string, ProductListItemModel>();

  get selectedProduct(): ProductListItemModel | null {
    return this.selectedProductUuid ? this.productsById.get(this.selectedProductUuid) ?? null : null;
  }

  constructor(
    private inventoryService: InventoryService,
    private attachmentService: AttachmentService
  ) {}

  resolveImageUrl(url: string): string {
    return this.attachmentService.resolveUrl(url);
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['products']) {
      this.productsById.clear();
      this.productOptions = this.products.map(p => {
        this.productsById.set(p.uuid, p);
        return { label: `${p.name} (${p.sku})`, value: p.uuid };
      });
    }

    if (changes['productUuid'] && this.productUuid !== this.selectedProductUuid) {
      this.selectedProductUuid = this.productUuid;
      if (this.selectedProductUuid) {
        this.loadVariants(this.selectedProductUuid, this.variantUuid ?? null, /*emit*/ false);
      } else {
        this.resetVariants();
      }
    } else if (changes['variantUuid'] && !changes['productUuid'] && this.variantUuid !== this.selectedVariantUuid) {
      this.selectedVariantUuid = this.variantUuid;
    }
  }

  onProductChange(): void {
    if (!this.selectedProductUuid) {
      this.resetVariants();
      this.emitSelection();
      return;
    }
    this.loadVariants(this.selectedProductUuid, null, /*emit*/ true);
  }

  onVariantChange(): void {
    this.singleVariant = null;
    this.emitSelection();
  }

  private resetVariants(): void {
    this.variants = [];
    this.variantOptions = [];
    this.showVariantDropdown = false;
    this.singleVariant = null;
    this.selectedVariantUuid = null;
  }

  private loadVariants(productUuid: string, preselectVariantUuid: string | null, emit: boolean): void {
    const product = this.productsById.get(productUuid);
    if (!product) { this.resetVariants(); return; }

    this.inventoryService.getProductById(product.id).subscribe({
      next: res => {
        this.variants = res?.result?.variants ?? [];
        const activeVariants = this.variants.filter(v => v.isActive);

        if (activeVariants.length <= 1) {
          const only = activeVariants[0] ?? this.variants.find(v => v.isDefault) ?? this.variants[0] ?? null;
          this.showVariantDropdown = false;
          this.singleVariant = only;
          this.selectedVariantUuid = only?.uuid ?? null;
        } else {
          this.showVariantDropdown = true;
          this.singleVariant = null;
          this.variantOptions = activeVariants.map(v => ({
            label: `${v.variantName} — ${v.sku} — ${v.purchasePrice.toLocaleString()}`,
            value: v.uuid
          }));
          // Falls back to the product's default variant (not null) when nothing was explicitly
          // preselected — otherwise a multi-variant product pick emitted with variantUuid: null,
          // and every consumer (PO/PR/Quotation line items) gates populating item description/
          // UoM/price on variantUuid being set, so the whole row silently stayed blank until the
          // user separately opened this dropdown too. The dropdown still shows and can be
          // changed — this only fixes what's selected by default.
          this.selectedVariantUuid = preselectVariantUuid && activeVariants.some(v => v.uuid === preselectVariantUuid)
            ? preselectVariantUuid
            : (activeVariants.find(v => v.isDefault)?.uuid ?? activeVariants[0].uuid);
        }

        if (emit) this.emitSelection();
      },
      error: () => { this.resetVariants(); if (emit) this.emitSelection(); }
    });
  }

  private emitSelection(): void {
    const product = this.selectedProductUuid ? this.productsById.get(this.selectedProductUuid) ?? null : null;
    const variant = this.variants.find(v => v.uuid === this.selectedVariantUuid) ?? this.singleVariant;

    this.selectionChange.emit({
      productUuid:   this.selectedProductUuid,
      productName:   product?.name ?? null,
      variantId:     variant?.id ?? null,
      variantUuid:   this.selectedVariantUuid,
      variantSku:    variant?.sku ?? null,
      variantName:   variant?.variantName ?? null,
      purchasePrice: variant?.purchasePrice ?? null,
      uomCode:       product?.uomCode ?? null
    });
  }
}
