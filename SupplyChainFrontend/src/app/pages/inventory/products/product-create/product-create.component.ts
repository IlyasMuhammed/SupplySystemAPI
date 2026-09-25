import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormBuilder, FormGroup, FormArray, Validators } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { CardModule } from 'primeng/card';
import { ToastModule } from 'primeng/toast';
import { DropdownModule } from 'primeng/dropdown';
import { InputNumberModule } from 'primeng/inputnumber';
import { DividerModule } from 'primeng/divider';
import { CheckboxModule } from 'primeng/checkbox';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';
import { catchError, of } from 'rxjs';
import {
  InventoryService,
  CategoryModel,
  CreateProductRequest,
  CreateProductVariantRequest,
  PatchProductRequest,
  ProductVariantModel,
  VariantAttributeValueInput
} from '../../../../services/inventory.service';
import { SupplierService } from '../../../../services/supplier.service';
import { AttachmentService } from '../../../../services/attachment.service';
import { DynamicAttributeFormComponent } from '../../../../shared/dynamic-attribute-form/dynamic-attribute-form.component';

// UOM options from FSD Section 6.5 — static, no API call needed
const UOM_OPTIONS = [
  { label: 'Each (EA)',       value: 'EA'   },
  { label: 'Kilogram (KG)',   value: 'KG'   },
  { label: 'Litre (LTR)',     value: 'LTR'  },
  { label: 'Box (BOX)',       value: 'BOX'  },
  { label: 'Set (SET)',       value: 'SET'  },
  { label: 'Meter (MTR)',     value: 'MTR'  },
  { label: 'Packet (PKT)',    value: 'PKT'  },
  { label: 'Pair (PAIR)',     value: 'PAIR' },
];

@Component({
  selector: 'app-product-create',
  standalone: true,
  imports: [
    CommonModule, ReactiveFormsModule,
    ButtonModule, InputTextModule, TextareaModule, CardModule, ToastModule,
    DropdownModule, InputNumberModule, DividerModule, CheckboxModule, TooltipModule,
    DynamicAttributeFormComponent
  ],
  templateUrl: './product-create.component.html',
  styleUrls: ['./product-create.component.scss'],
  providers: [MessageService]
})
export class ProductCreateComponent implements OnInit {
  isEditMode = false;
  productId: number | null = null;
  productForm!: FormGroup;
  isSubmitting = false;
  isLoadingLookups = true;
  isLoadingProduct = false;

  categories: CategoryModel[] = [];
  categoryOptions: { label: string; value: number }[] = [];
  subCategoryOptions: { label: string; value: number }[] = [];
  supplierOptions: { label: string; value: number }[] = [];

  // Static from FSD — no API required
  readonly uomOptions = UOM_OPTIONS;

  editProductSku = '';

  // PV-001 — pricing now lives on the default variant, not Product itself. On edit, there's no
  // endpoint yet to patch an existing variant's price (only creation seeds it), so the existing
  // default variant's price is shown read-only rather than silently going nowhere.
  editDefaultVariant: ProductVariantModel | null = null;

  // PV-002 — dynamic attribute values captured from the embedded form, keyed by attributeUuid.
  // Submitted via PUT /api/variants/{variantId}/attributes after the product (create) or in
  // addition to the patch (edit) succeeds.
  attributeValues: VariantAttributeValueInput[] = [];
  attributesValid = true;

  // Product image — uploaded through the generic attachments endpoint (same pattern as the PO
  // document template's logo upload). documentId only needs to exist long enough to correlate
  // this upload with its resulting fileUrl; the product itself only ever stores the URL string.
  private readonly imageUploadDocId = crypto.randomUUID();
  isUploadingImage = false;

  constructor(
    private fb: FormBuilder,
    private route: ActivatedRoute,
    private router: Router,
    private inventoryService: InventoryService,
    private supplierService: SupplierService,
    private attachmentService: AttachmentService,
    private messageService: MessageService
  ) {}

  ngOnInit(): void {
    const idParam = this.route.snapshot.paramMap.get('id');
    if (idParam && idParam !== 'new') {
      this.isEditMode = true;
      this.productId = +idParam;
    }

    this.initForm();
    this.loadCategories();
    this.loadSuppliers();

    if (this.isEditMode && this.productId != null) {
      this.loadProduct(this.productId);
    }
  }

  private initForm(): void {
    this.productForm = this.fb.group({
      // Basic Information
      name:                ['', [Validators.required, Validators.minLength(2), Validators.maxLength(200)]],
      shortName:           [''],
      sku:                 [''],
      description:         [''],

      // Classification
      categoryId:          [null],
      subCategoryId:       [null],
      brand:               [''],
      uomCode:             [null],

      // Pricing (default variant) — required only when creating; edit shows it read-only.
      purchasePrice:       [null, this.isEditMode ? [] : [Validators.required, Validators.min(0)]],
      sellingPrice:        [null, Validators.min(0)],
      barcode:             [''],

      // Physical Attributes
      weightKg:            [null],
      dimensions:          [''],
      shelfLifeDays:       [null, Validators.min(0)],

      // Tracking
      isBatchTracked:      [false],
      isSerialTracked:     [false],

      // Stock Parameters
      reorderPoint:        [null, Validators.min(0)],
      reorderQty:          [null, Validators.min(0)],
      minStockLevel:       [null],
      maxStockLevel:       [null],
      leadTimeDays:        [null, Validators.min(0)],

      // Supplier & Notes
      preferredSupplierId: [null],
      notes:               [''],
      imageUrl:            [''],

      // Additional variants (PV-001) — the Purchase Price/Selling Price/Barcode fields above
      // become the default variant automatically; anything added here rides alongside it in the
      // same create request, so a multi-SKU product (sizes, colors) never needs a second trip to
      // the product-detail "Add Variant" screen just to get its first extra SKU in.
      variants:            this.fb.array([])
    });
  }

  get variants(): FormArray { return this.productForm.get('variants') as FormArray; }

  private newVariant(): FormGroup {
    return this.fb.group({
      variantName:   ['', [Validators.required, Validators.maxLength(200)]],
      sku:           [''],
      purchasePrice: [null, [Validators.required, Validators.min(0), Validators.max(100000000)]],
      sellingPrice:  [null, [Validators.min(0), Validators.max(100000000)]],
      barcode:       [''],
      weight:        [null, Validators.min(0)],
      reorderPoint:  [null, Validators.min(0)],
      isAvailableForRetail:     [false],
      isAvailableForPos:        [false],
      isAvailableForMirMiv:     [false],
      isAvailableForProduction: [false],
      isAvailableForServices:   [false]
    });
  }

  addVariant(): void {
    this.variants.push(this.newVariant());
  }

  removeVariant(i: number): void {
    this.variants.removeAt(i);
  }

  private loadSuppliers(): void {
    this.supplierService.getSuppliers({ status: 'ACTIVE', pageSize: 500 })
      .pipe(catchError(() => of(null)))
      .subscribe(res => {
        if (res?.result?.data) {
          this.supplierOptions = res.result.data.map(s => ({ label: s.supplierName, value: s.id }));
        }
      });
  }

  private loadCategories(): void {
    this.isLoadingLookups = true;
    this.inventoryService.getCategories()
      .pipe(catchError(() => of(null)))
      .subscribe(res => {
        this.isLoadingLookups = false;
        if (res?.success && res.result) {
          this.categories = res.result.filter((c: CategoryModel) => c.isActive);
          this.categoryOptions = this.categories.map(c => ({ label: c.name, value: c.id }));
        }
      });
  }

  onCategoryChange(): void {
    const selectedId: number | null = this.productForm.value.categoryId;
    this.productForm.patchValue({ subCategoryId: null });
    if (selectedId == null) {
      this.subCategoryOptions = [];
      return;
    }
    const cat = this.categories.find(c => c.id === selectedId);
    this.subCategoryOptions = cat
      ? cat.subCategories.filter(s => s.isActive).map(s => ({ label: s.name, value: s.id }))
      : [];
  }

  // ── Product image ────────────────────────────────────────────────────────
  onImageSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file) return;

    this.isUploadingImage = true;
    this.attachmentService.upload(file, 'PRODUCT_IMAGE', this.imageUploadDocId).subscribe({
      next: (res) => {
        if (!res.success) {
          this.isUploadingImage = false;
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Image upload failed.' });
          return;
        }
        this.attachmentService.getAttachments('PRODUCT_IMAGE', this.imageUploadDocId).subscribe({
          next: (listRes) => {
            this.isUploadingImage = false;
            const latest = (listRes.result ?? []).sort((a, b) =>
              new Date(b.uploadedDate).getTime() - new Date(a.uploadedDate).getTime())[0];
            if (latest) this.productForm.patchValue({ imageUrl: latest.fileUrl });
          },
          error: () => { this.isUploadingImage = false; }
        });
      },
      error: (err) => {
        this.isUploadingImage = false;
        const detail = err.error?.message || (err.status ? `Image upload failed (HTTP ${err.status}).` : 'Image upload failed.');
        this.messageService.add({ severity: 'error', summary: 'Error', detail, life: 8000 });
      }
    });
  }

  removeImage(): void {
    this.productForm.patchValue({ imageUrl: '' });
  }

  resolveImageUrl(url: string): string {
    return this.attachmentService.resolveUrl(url);
  }

  private loadProduct(id: number): void {
    this.isLoadingProduct = true;
    this.inventoryService.getProductById(id).subscribe({
      next: (res) => {
        this.isLoadingProduct = false;
        if (res.success && res.result) {
          const p = res.result;
          this.editProductSku = p.sku;

          this.productForm.patchValue({
            name:                p.name,
            shortName:           p.shortName           ?? '',
            sku:                 p.sku,
            description:         p.description         ?? '',
            categoryId:          p.categoryId          ?? null,
            subCategoryId:       p.subCategoryId       ?? null,
            brand:               p.brand               ?? '',
            uomCode:             p.uomCode             ?? null,
            weightKg:            p.weightKg            ?? null,
            dimensions:          p.dimensions          ?? '',
            shelfLifeDays:       p.shelfLifeDays       ?? null,
            isBatchTracked:      p.isBatchTracked      ?? false,
            isSerialTracked:     p.isSerialTracked     ?? false,
            reorderPoint:        p.reorderPoint        ?? null,
            reorderQty:          p.reorderQty          ?? null,
            minStockLevel:       p.minStockLevel       ?? null,
            maxStockLevel:       p.maxStockLevel       ?? null,
            leadTimeDays:        p.leadTimeDays        ?? null,
            preferredSupplierId: p.preferredSupplierId ?? null,
            notes:               p.notes               ?? '',
            imageUrl:            p.imageUrl            ?? ''
          });

          if (p.categoryId) {
            const cat = this.categories.find(c => c.id === p.categoryId);
            this.subCategoryOptions = cat
              ? cat.subCategories.filter(s => s.isActive).map(s => ({ label: s.name, value: s.id }))
              : [];
          }

          this.editDefaultVariant = p.variants?.find(v => v.isDefault) ?? p.variants?.[0] ?? null;
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Product not found' });
          this.goBack();
        }
      },
      error: () => {
        this.isLoadingProduct = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load product' });
        this.goBack();
      }
    });
  }

  onSubmit(): void {
    if (this.productForm.invalid) {
      this.productForm.markAllAsTouched();
      return;
    }

    if (!this.attributesValid) {
      this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Please fix the invalid attribute fields below.' });
      return;
    }

    const raw = this.productForm.value;

    if (this.isEditMode && this.productId != null) {
      const payload: PatchProductRequest = {
        name:                raw.name,
        shortName:           raw.shortName           || undefined,
        description:         raw.description         || undefined,
        categoryId:          raw.categoryId          ?? undefined,
        subCategoryId:       raw.subCategoryId       ?? undefined,
        brand:               raw.brand               || undefined,
        uomCode:             raw.uomCode             ?? undefined,
        weightKg:            raw.weightKg            ?? undefined,
        dimensions:          raw.dimensions          || undefined,
        shelfLifeDays:       raw.shelfLifeDays       ?? undefined,
        isBatchTracked:      raw.isBatchTracked,
        isSerialTracked:     raw.isSerialTracked,
        reorderPoint:        raw.reorderPoint        ?? undefined,
        reorderQty:          raw.reorderQty          ?? undefined,
        minStockLevel:       raw.minStockLevel       ?? undefined,
        maxStockLevel:       raw.maxStockLevel       ?? undefined,
        leadTimeDays:        raw.leadTimeDays        ?? undefined,
        preferredSupplierId: raw.preferredSupplierId ?? undefined,
        notes:               raw.notes               || undefined,
        imageUrl:            raw.imageUrl             || undefined
      };

      this.isSubmitting = true;
      this.inventoryService.patchProduct(this.productId, payload).subscribe({
        next: (res) => {
          if (res.success) {
            this.saveAttributeValuesThen(this.editDefaultVariant?.uuid ?? null, () => {
              this.isSubmitting = false;
              this.messageService.add({ severity: 'success', summary: 'Updated', detail: 'Product updated successfully' });
              setTimeout(() => this.goBack(), 1500);
            });
          } else {
            this.isSubmitting = false;
            this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Update failed' });
          }
        },
        error: (err) => {
          this.isSubmitting = false;
          this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to update product' });
        }
      });
    } else {
      // Additional variants added on this page ride alongside the default one — the backend
      // treats "variants supplied" and "scalar purchasePrice" as mutually exclusive (explicit
      // Variants[] wins entirely when present), so once there's at least one extra variant the
      // top-level Purchase Price/Selling Price/Barcode fields get folded into variant #1
      // (isDefault=true) instead of being sent as scalars.
      const extraVariants = this.variants.value as Array<{
        variantName: string; sku: string; purchasePrice: number; sellingPrice: number | null;
        barcode: string; weight: number | null; reorderPoint: number | null;
        isAvailableForRetail: boolean; isAvailableForPos: boolean; isAvailableForMirMiv: boolean;
        isAvailableForProduction: boolean; isAvailableForServices: boolean;
      }>;
      // The product-level form has no channel checkboxes of its own, so the variant synthesized
      // from it starts available nowhere — same as any other freshly created variant, editable
      // afterwards from the product's own Variants tab.
      const variantsPayload: CreateProductVariantRequest[] | undefined = extraVariants.length > 0
        ? [
            {
              variantName:   raw.name,
              sku:           raw.sku || undefined,
              purchasePrice: raw.purchasePrice,
              sellingPrice:  raw.sellingPrice ?? undefined,
              barcode:       raw.barcode || undefined,
              isDefault:     true,
              isAvailableForRetail: false, isAvailableForPos: false, isAvailableForMirMiv: false,
              isAvailableForProduction: false, isAvailableForServices: false
            },
            ...extraVariants.map(v => ({
              variantName:   v.variantName,
              sku:           v.sku || undefined,
              purchasePrice: v.purchasePrice,
              sellingPrice:  v.sellingPrice ?? undefined,
              barcode:       v.barcode || undefined,
              weight:        v.weight ?? undefined,
              reorderPoint:  v.reorderPoint ?? undefined,
              isDefault:     false,
              isAvailableForRetail:     !!v.isAvailableForRetail,
              isAvailableForPos:        !!v.isAvailableForPos,
              isAvailableForMirMiv:     !!v.isAvailableForMirMiv,
              isAvailableForProduction: !!v.isAvailableForProduction,
              isAvailableForServices:   !!v.isAvailableForServices
            }))
          ]
        : undefined;

      const payload: CreateProductRequest = {
        name:                raw.name,
        sku:                 raw.sku                 || undefined,
        shortName:           raw.shortName           || undefined,
        description:         raw.description         || undefined,
        categoryId:          raw.categoryId          ?? undefined,
        subCategoryId:       raw.subCategoryId       ?? undefined,
        brand:               raw.brand               || undefined,
        uomCode:             raw.uomCode             ?? undefined,
        purchasePrice:       variantsPayload ? undefined : (raw.purchasePrice ?? undefined),
        sellingPrice:        variantsPayload ? undefined : (raw.sellingPrice  ?? undefined),
        barcode:             variantsPayload ? undefined : (raw.barcode      || undefined),
        variants:            variantsPayload,
        weightKg:            raw.weightKg            ?? undefined,
        dimensions:          raw.dimensions          || undefined,
        shelfLifeDays:       raw.shelfLifeDays       ?? undefined,
        isBatchTracked:      raw.isBatchTracked      ?? false,
        isSerialTracked:     raw.isSerialTracked      ?? false,
        reorderPoint:        raw.reorderPoint        ?? undefined,
        reorderQty:          raw.reorderQty          ?? undefined,
        minStockLevel:       raw.minStockLevel       ?? undefined,
        maxStockLevel:       raw.maxStockLevel       ?? undefined,
        leadTimeDays:        raw.leadTimeDays        ?? undefined,
        preferredSupplierId: raw.preferredSupplierId ?? undefined,
        notes:               raw.notes               || undefined,
        imageUrl:            raw.imageUrl             || undefined
      };

      this.isSubmitting = true;
      this.inventoryService.createProduct(payload).subscribe({
        next: (res) => {
          if (res.success && this.attributeValues.length > 0 && res.result?.id != null) {
            // Need the auto-created default variant's UUID before attribute values can be saved
            // against it — the create response only returns {id, sku}, so fetch the detail once.
            this.inventoryService.getProductById(res.result.id).subscribe(detail => {
              const variantUuid = detail.result?.variants?.find(v => v.isDefault)?.uuid ?? null;
              this.saveAttributeValuesThen(variantUuid, () => this.finishCreate(res.result?.sku));
            });
            return;
          }
          if (res.success) {
            this.finishCreate(res.result?.sku);
          } else {
            this.isSubmitting = false;
            this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Creation failed' });
          }
        },
        error: (err) => {
          this.isSubmitting = false;
          const msg = err.error?.message || err.error?.title || 'Failed to create product. Please try again.';
          this.messageService.add({ severity: 'error', summary: 'Error', detail: msg });
        }
      });
    }
  }

  private finishCreate(sku?: string): void {
    this.isSubmitting = false;
    this.messageService.add({
      severity: 'success',
      summary: 'Created',
      detail: `Product created — SKU: ${sku ?? 'auto-generated'}`
    });
    setTimeout(() => this.goBack(), 1500);
  }

  // Saves the captured dynamic-attribute values against the given variant (no-op if there's
  // nothing to save or no variant to save it against), then always calls `then` — attribute
  // values are a secondary write and must never block the product save from completing.
  private saveAttributeValuesThen(variantUuid: string | null, then: () => void): void {
    if (!variantUuid || this.attributeValues.length === 0) {
      then();
      return;
    }
    this.inventoryService.setVariantAttributeValues(variantUuid, { values: this.attributeValues }).subscribe({
      next: () => then(),
      error: () => {
        this.messageService.add({ severity: 'warn', summary: 'Partial Save', detail: 'Product saved, but attribute values failed to save.' });
        then();
      }
    });
  }

  onAttributeValuesChange(values: VariantAttributeValueInput[]): void {
    this.attributeValues = values;
  }

  onAttributeValidityChange(valid: boolean): void {
    this.attributesValid = valid;
  }

  goBack(): void {
    this.router.navigate(['/portal/pages/inventory/products']);
  }

  get f() {
    return this.productForm.controls;
  }
}
