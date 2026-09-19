import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { ReactiveFormsModule, FormBuilder, FormGroup, Validators, FormsModule } from '@angular/forms';
import { forkJoin } from 'rxjs';
import { ButtonModule } from 'primeng/button';
import { CardModule } from 'primeng/card';
import { TabViewModule } from 'primeng/tabview';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { DialogModule } from 'primeng/dialog';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { InputNumberModule } from 'primeng/inputnumber';
import { DividerModule } from 'primeng/divider';
import { TooltipModule } from 'primeng/tooltip';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { DropdownModule } from 'primeng/dropdown';
import { TableModule } from 'primeng/table';
import { CheckboxModule } from 'primeng/checkbox';
import { CalendarModule } from 'primeng/calendar';
import { MessageService, ConfirmationService } from 'primeng/api';
import {
  InventoryService,
  ProductDetailModel,
  ProductVariantModel,
  ProductStockModel,
  CategoryModel,
  SubCategoryModel,
  WarehouseModel,
  StockAdjustmentResult,
  PatchProductRequest,
  CreateProductVariantRequest,
  VariantAttributeValueInput
} from '../../../../services/inventory.service';
import { DynamicAttributeFormComponent } from '../../../../shared/dynamic-attribute-form/dynamic-attribute-form.component';
import { AttachmentService } from '../../../../services/attachment.service';
import {
  PricingRuleService,
  PricingRuleModel,
  PriceType,
  SalePriceResolution
} from '../../../../services/pricing-rule.service';
import { CurrenciesService, CurrencyModel } from '../../../../services/currencies.service';
import { BusinessPartnerService, BusinessPartnerModel } from '../../../../services/business-partner.service';

@Component({
  selector: 'app-product-detail',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule, ReactiveFormsModule,
    ButtonModule, CardModule, TabViewModule, TagModule, ToastModule,
    DialogModule, InputTextModule, TextareaModule, InputNumberModule,
    DividerModule, TooltipModule, ConfirmDialogModule, DropdownModule, TableModule,
    CheckboxModule, CalendarModule, DynamicAttributeFormComponent
  ],
  templateUrl: './product-detail.component.html',
  styleUrls: ['./product-detail.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class ProductDetailComponent implements OnInit {
  productId = 0;
  product: ProductDetailModel | null = null;
  stockLevels: ProductStockModel[] = [];
  isLoading = true;

  get defaultVariant() {
    return this.product?.variants?.find(v => v.isDefault) ?? this.product?.variants?.[0] ?? null;
  }

  // ── Edit dialog ───────────────────────────────────────────────────────────
  showEditDialog = false;
  editForm!: FormGroup;
  isSaving = false;
  categories: CategoryModel[] = [];
  categoryOptions: { label: string; value: number | null }[] = [];
  subCategoryOptions: { label: string; value: number | null }[] = [];
  isLoadingLookups = false;

  uomOptions: { label: string; value: string }[] = [
    { label: 'Piece (PCS)',      value: 'PCS' },
    { label: 'Each (EA)',        value: 'EA' },
    { label: 'Kilogram (KG)',    value: 'KG' },
    { label: 'Gram (G)',         value: 'G' },
    { label: 'Litre (L)',        value: 'L' },
    { label: 'Millilitre (ML)',  value: 'ML' },
    { label: 'Metre (M)',        value: 'M' },
    { label: 'Box (BOX)',        value: 'BOX' },
    { label: 'Carton (CTN)',     value: 'CTN' },
    { label: 'Pair (PR)',        value: 'PR' },
    { label: 'Set (SET)',        value: 'SET' },
    { label: 'Dozen (DZ)',       value: 'DZ' }
  ];

  // ── Stock adjustment dialog ───────────────────────────────────────────────
  showAdjustmentDialog = false;
  adjustmentForm!: FormGroup;
  warehouseOptions: { label: string; value: number }[] = [];
  isSavingAdjustment = false;
  adjustmentResult: StockAdjustmentResult | null = null;

  // ── Variant dialog (PV-007) ───────────────────────────────────────────────
  showVariantDialog = false;
  variantForm!: FormGroup;
  isSavingVariant = false;
  editingVariant: ProductVariantModel | null = null;
  private variantAttributeValues: VariantAttributeValueInput[] = [];
  private variantAttributesValid = true;

  // ── Pricing tab (A29-P2-06) ───────────────────────────────────────────────
  pricingVariantUuid: string | null = null;
  pricingRules: PricingRuleModel[] = [];
  isLoadingPricing = false;
  currencies: CurrencyModel[] = [];
  partners: BusinessPartnerModel[] = [];
  priceTypeOptions: { label: string; value: PriceType }[] = [
    { label: 'Selling',     value: 'SELLING' },
    { label: 'Cost',        value: 'COST' },
    { label: 'Promotional', value: 'PROMOTIONAL' },
    { label: 'Contract',    value: 'CONTRACT' }
  ];

  showPriceRuleDialog = false;
  priceRuleForm!: FormGroup;
  isSavingPriceRule = false;
  editingPriceRule: PricingRuleModel | null = null;

  previewForm!: FormGroup;
  isCalculatingPreview = false;
  previewResult: SalePriceResolution | null = null;

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private fb: FormBuilder,
    private inventoryService: InventoryService,
    private messageService: MessageService,
    private confirmationService: ConfirmationService,
    private attachmentService: AttachmentService,
    private pricingRuleService: PricingRuleService,
    private currenciesService: CurrenciesService,
    private businessPartnerService: BusinessPartnerService
  ) {}

  resolveImageUrl(url: string): string {
    return this.attachmentService.resolveUrl(url);
  }

  // ── Product image — manage directly from the detail page ───────────────────
  isUploadingImage = false;

  onImageSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    input.value = '';
    if (!file || !this.product) return;

    this.isUploadingImage = true;
    this.attachmentService.upload(file, 'PRODUCT_IMAGE', this.product.uuid).subscribe({
      next: (res) => {
        if (!res.success) {
          this.isUploadingImage = false;
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Image upload failed.' });
          return;
        }
        this.attachmentService.getAttachments('PRODUCT_IMAGE', this.product!.uuid).subscribe({
          next: (listRes) => {
            const latest = (listRes.result ?? []).sort((a, b) =>
              new Date(b.uploadedDate).getTime() - new Date(a.uploadedDate).getTime())[0];
            if (latest) {
              this.saveImageUrl(latest.fileUrl);
            } else {
              this.isUploadingImage = false;
            }
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
    this.saveImageUrl('');
  }

  private saveImageUrl(imageUrl: string): void {
    if (!this.product) return;
    this.inventoryService.patchProduct(this.productId, { imageUrl }).subscribe({
      next: (res) => {
        this.isUploadingImage = false;
        if (res.success) {
          this.product!.imageUrl = imageUrl || undefined;
          this.messageService.add({ severity: 'success', summary: 'Saved', detail: imageUrl ? 'Product picture updated.' : 'Product picture removed.' });
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Failed to save picture.' });
        }
      },
      error: (err) => {
        this.isUploadingImage = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to save picture.' });
      }
    });
  }

  ngOnInit() {
    this.productId = +this.route.snapshot.paramMap.get('id')!;
    this.initForms();
    this.loadProduct();
  }

  // ── Forms ─────────────────────────────────────────────────────────────────

  private initForms() {
    this.editForm = this.fb.group({
      name:           ['', [Validators.required, Validators.minLength(2), Validators.maxLength(200)]],
      description:    [''],
      brand:          [''],
      model:          [''],
      categoryId:     [null],
      subCategoryId:  [null],
      uomCode:        [null],
      reorderPoint:   [null, [Validators.min(0)]],
      reorderQty:     [null, [Validators.min(0)]],
      minStockLevel:  [null, [Validators.min(0)]],
      maxStockLevel:  [null, [Validators.min(0)]]
    });

    this.adjustmentForm = this.fb.group({
      variantId:        [null, [Validators.required]],
      warehouseId:      [null, [Validators.required]],
      qtyAdjusted:      [null, [Validators.required]],
      unitCost:         [null, [Validators.min(0)]],
      adjustmentReason: ['']
    });

    this.variantForm = this.fb.group({
      sku:           [''],
      variantName:   ['', [Validators.required, Validators.maxLength(200)]],
      purchasePrice: [null, [Validators.required, Validators.min(0)]],
      sellingPrice:  [null, [Validators.min(0)]],
      barcode:       [''],
      weight:        [null, [Validators.min(0)]],
      dimensions:    [''],
      reorderPoint:  [null, [Validators.min(0)]],
      sortOrder:     [null],
      isDefault:     [false]
    });

    this.priceRuleForm = this.fb.group({
      partnerUuid:   [null],
      priceType:     ['SELLING', [Validators.required]],
      minQty:        [null, [Validators.min(0)]],
      maxQty:        [null, [Validators.min(0)]],
      unitPrice:     [null, [Validators.required, Validators.min(0)]],
      currencyId:    [null],
      effectiveFrom: [new Date(), [Validators.required]],
      effectiveTo:   [null],
      isActive:      [true]
    });

    this.previewForm = this.fb.group({
      partnerUuid: [null],
      qty:         [1, [Validators.required, Validators.min(0.0001)]],
      date:        [new Date()]
    });
  }

  // Adjustment dialog picks a variant directly — dropdown only shown when the product has
  // more than one active variant (PV-005: stock is tracked per variant, not per product).
  get activeVariants() {
    return this.product?.variants?.filter(v => v.isActive) ?? [];
  }

  // ── Load data ─────────────────────────────────────────────────────────────

  loadProduct() {
    this.isLoading = true;
    this.inventoryService.getProductById(this.productId).subscribe({
      next: (res) => {
        if (res.success) {
          this.product = res.result;
          if (!this.pricingVariantUuid) {
            this.pricingVariantUuid = this.defaultVariant?.uuid ?? this.product.variants?.[0]?.uuid ?? null;
          }
          this.loadStock();
        } else {
          this.isLoading = false;
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Failed to load product.' });
        }
      },
      error: (err) => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to load product.' });
      }
    });
  }

  private loadStock() {
    this.inventoryService.getProductStock(this.productId).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success) {
          this.stockLevels = res.result ?? [];
        }
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'warn', summary: 'Warning', detail: 'Failed to load stock levels.' });
      }
    });
  }

  private loadLookups(): Promise<void> {
    if (this.categories.length > 0) return Promise.resolve();
    this.isLoadingLookups = true;
    return new Promise((resolve) => {
      this.inventoryService.getCategories().subscribe({
        next: (res) => {
          this.isLoadingLookups = false;
          if (res.success) {
            this.categories = res.result ?? [];
            this.categoryOptions = [
              { label: 'None', value: null },
              ...this.categories.map(c => ({ label: c.name, value: c.id }))
            ];
            this.buildSubCategoryOptions(this.editForm.value.categoryId);
          }
          resolve();
        },
        error: () => {
          this.isLoadingLookups = false;
          this.messageService.add({ severity: 'warn', summary: 'Warning', detail: 'Failed to load categories.' });
          resolve();
        }
      });
    });
  }

  private buildSubCategoryOptions(categoryId: number | null) {
    if (!categoryId) {
      this.subCategoryOptions = [{ label: 'None', value: null }];
      return;
    }
    const cat = this.categories.find(c => c.id === categoryId);
    const subs: SubCategoryModel[] = (cat?.subCategories ?? []).filter(s => s.isActive);
    this.subCategoryOptions = [
      { label: 'None', value: null },
      ...subs.map(s => ({ label: s.name, value: s.id }))
    ];
  }

  // ── Edit dialog ───────────────────────────────────────────────────────────

  openEditDialog() {
    if (!this.product) return;
    this.editForm.patchValue({
      name:           this.product.name,
      description:    this.product.description    ?? '',
      brand:          this.product.brand          ?? '',
      categoryId:     this.product.categoryId     ?? null,
      subCategoryId:  this.product.subCategoryId  ?? null,
      uomCode:        this.product.uomCode        ?? null,
      reorderPoint:   this.product.reorderPoint    ?? null,
      reorderQty:     this.product.reorderQty      ?? null,
      minStockLevel:  this.product.minStockLevel   ?? null,
      maxStockLevel:  this.product.maxStockLevel   ?? null
    });
    this.loadLookups().then(() => {
      this.showEditDialog = true;
    });
  }

  onCategoryChange() {
    const catId = this.editForm.value.categoryId;
    this.editForm.patchValue({ subCategoryId: null });
    this.buildSubCategoryOptions(catId);
  }

  saveEdit() {
    if (this.editForm.invalid) { this.editForm.markAllAsTouched(); return; }
    const raw = this.editForm.value;
    const payload: PatchProductRequest = {
      name:          raw.name          || undefined,
      description:   raw.description   || undefined,
      brand:         raw.brand         || undefined,
      categoryId:    raw.categoryId    ?? undefined,
      subCategoryId: raw.subCategoryId ?? undefined,
      uomCode:       raw.uomCode       ?? undefined,
      reorderPoint:  raw.reorderPoint  ?? undefined,
      reorderQty:    raw.reorderQty    ?? undefined,
      minStockLevel: raw.minStockLevel ?? undefined,
      maxStockLevel: raw.maxStockLevel ?? undefined
    };
    this.isSaving = true;
    this.inventoryService.patchProduct(this.productId, payload).subscribe({
      next: (res) => {
        this.isSaving = false;
        this.showEditDialog = false;
        if (res.success) {
          this.messageService.add({ severity: 'success', summary: 'Saved', detail: 'Product updated successfully.' });
          this.loadProduct();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message });
        }
      },
      error: (err) => {
        this.isSaving = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to save changes.' });
      }
    });
  }

  // ── Adjustment dialog ─────────────────────────────────────────────────────

  openAdjustmentDialog() {
    this.adjustmentResult = null;
    this.adjustmentForm.reset({
      variantId: this.defaultVariant?.id ?? null,
      warehouseId: null,
      qtyAdjusted: null,
      unitCost: null,
      adjustmentReason: ''
    });
    if (this.warehouseOptions.length === 0) {
      this.inventoryService.getWarehouses().subscribe({
        next: (res) => {
          if (res.success) {
            this.warehouseOptions = (res.result ?? [])
              .filter((w: WarehouseModel) => w.isActive)
              .map((w: WarehouseModel) => ({ label: `${w.code} – ${w.name}`, value: w.id }));
          }
        },
        error: () => {
          this.messageService.add({ severity: 'warn', summary: 'Warning', detail: 'Failed to load warehouses.' });
        }
      });
    }
    this.showAdjustmentDialog = true;
  }

  saveAdjustment() {
    if (this.adjustmentForm.invalid) { this.adjustmentForm.markAllAsTouched(); return; }
    const raw = this.adjustmentForm.value;
    this.isSavingAdjustment = true;
    this.inventoryService.createAdjustment({
      variantId:   raw.variantId,
      warehouseId: raw.warehouseId,
      qtyAdjusted: raw.qtyAdjusted,
      unitCost:    raw.unitCost     ?? undefined,
      notes:       raw.adjustmentReason || undefined
    }).subscribe({
      next: (res) => {
        this.isSavingAdjustment = false;
        if (res.success) {
          this.adjustmentResult = res.result;
          const isAutoApproved = res.result?.stockUpdated;
          this.messageService.add({
            severity: isAutoApproved ? 'success' : 'info',
            summary: isAutoApproved ? 'Adjustment Applied' : 'Pending Approval',
            detail: isAutoApproved
              ? 'Stock adjustment was auto-approved and applied.'
              : 'Adjustment submitted and is pending Inventory Manager approval.'
          });
          this.showAdjustmentDialog = false;
          this.loadStock();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message });
        }
      },
      error: (err) => {
        this.isSavingAdjustment = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to create adjustment.' });
      }
    });
  }

  // ── Variant dialog (PV-007) ───────────────────────────────────────────────

  openAddVariantDialog(): void {
    this.editingVariant = null;
    this.variantAttributeValues = [];
    this.variantAttributesValid = true;
    this.variantForm.reset({
      sku: '', variantName: '', purchasePrice: null, sellingPrice: null,
      barcode: '', weight: null, dimensions: '', reorderPoint: null, sortOrder: null, isDefault: false
    });
    this.showVariantDialog = true;
  }

  openEditVariantDialog(variant: ProductVariantModel): void {
    this.editingVariant = variant;
    this.variantAttributeValues = [];
    this.variantAttributesValid = true;
    this.variantForm.reset({
      sku: variant.sku,
      variantName: variant.variantName,
      purchasePrice: variant.purchasePrice,
      sellingPrice: variant.sellingPrice ?? null,
      barcode: variant.barcode ?? '',
      weight: variant.weightKg ?? null,
      dimensions: variant.dimensions ?? '',
      reorderPoint: variant.reorderPoint ?? null,
      sortOrder: variant.sortOrder ?? null,
      isDefault: variant.isDefault
    });
    this.showVariantDialog = true;
  }

  onVariantAttributeValuesChange(values: VariantAttributeValueInput[]): void {
    this.variantAttributeValues = values;
  }

  onVariantAttributeValidityChange(valid: boolean): void {
    this.variantAttributesValid = valid;
  }

  saveVariant(): void {
    if (this.variantForm.invalid) { this.variantForm.markAllAsTouched(); return; }
    if (!this.variantAttributesValid) {
      this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Please fix the invalid attribute fields below.' });
      return;
    }
    const raw = this.variantForm.value;
    const payload: CreateProductVariantRequest = {
      sku:           raw.sku || undefined,
      variantName:   raw.variantName,
      barcode:       raw.barcode || undefined,
      purchasePrice: raw.purchasePrice,
      sellingPrice:  raw.sellingPrice ?? undefined,
      weight:        raw.weight ?? undefined,
      dimensions:    raw.dimensions || undefined,
      isDefault:     !!raw.isDefault,
      reorderPoint:  raw.reorderPoint ?? undefined,
      sortOrder:     raw.sortOrder ?? undefined
    };

    this.isSavingVariant = true;
    if (this.editingVariant) {
      const variantUuid = this.editingVariant.uuid;
      this.inventoryService.updateVariant(variantUuid, payload).subscribe({
        next: (res) => {
          if (res.success) {
            this.saveVariantAttributesThen(variantUuid, () => this.finishVariantSave('Variant updated.'));
          } else {
            this.isSavingVariant = false;
            this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message });
          }
        },
        error: (err) => {
          this.isSavingVariant = false;
          this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to update variant.' });
        }
      });
    } else {
      this.inventoryService.addVariant(this.productId, payload).subscribe({
        next: (res) => {
          if (res.success && res.result) {
            this.saveVariantAttributesThen(res.result.uuid, () => this.finishVariantSave('Variant added.'));
          } else {
            this.isSavingVariant = false;
            this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message });
          }
        },
        error: (err) => {
          this.isSavingVariant = false;
          this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to add variant.' });
        }
      });
    }
  }

  // Attribute values are a secondary write — a failure here must never be reported as the
  // variant save itself having failed (matches product-create's saveAttributeValuesThen).
  private saveVariantAttributesThen(variantUuid: string, then: () => void): void {
    if (this.variantAttributeValues.length === 0) { then(); return; }
    this.inventoryService.setVariantAttributeValues(variantUuid, { values: this.variantAttributeValues }).subscribe({
      next: () => then(),
      error: () => {
        this.messageService.add({ severity: 'warn', summary: 'Partial Save', detail: 'Variant saved, but attribute values failed to save.' });
        then();
      }
    });
  }

  private finishVariantSave(detail: string): void {
    this.isSavingVariant = false;
    this.showVariantDialog = false;
    this.messageService.add({ severity: 'success', summary: 'Saved', detail });
    this.loadProduct();
  }

  confirmDeleteVariant(variant: ProductVariantModel): void {
    this.confirmationService.confirm({
      message: `Delete variant "${variant.variantName}" (${variant.sku})? If it has ever been transacted, it will be deactivated instead of removed.`,
      header: 'Confirm Delete',
      icon: 'pi pi-exclamation-triangle',
      acceptButtonStyleClass: 'p-button-danger',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => {
        this.inventoryService.deleteVariant(variant.uuid).subscribe({
          next: (res) => {
            if (res.success) {
              const softDeleted = res.result?.softDeleted ?? false;
              this.messageService.add({
                severity: softDeleted ? 'warn' : 'success',
                summary: 'Done',
                detail: softDeleted
                  ? 'Variant has been transacted — deactivated instead of deleted.'
                  : 'Variant deleted.'
              });
              this.loadProduct();
            } else {
              this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message });
            }
          },
          error: (err) => {
            this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to delete variant.' });
          }
        });
      }
    });
  }

  // ── Pricing tab (A29-P2-06 §2.2) ─────────────────────────────────────────
  // Rules are per-variant, so this tab needs its own variant picker; it defaults to the
  // product's default variant once the product loads (see loadProduct). Lookups (currencies,
  // partners) are loaded once, the first time the tab is actually opened, not on every page load.
  private pricingLookupsLoaded = false;

  onTabChange(event: { index: number }): void {
    const pricingTabIndex = 4;
    if (event.index === pricingTabIndex && !this.pricingLookupsLoaded) {
      this.pricingLookupsLoaded = true;
      this.currenciesService.getAll().subscribe({
        next: (res) => { if (res.success) this.currencies = res.result ?? []; },
        error: () => this.messageService.add({ severity: 'warn', summary: 'Warning', detail: 'Failed to load currencies.' })
      });
      this.businessPartnerService.getPartners({ pageSize: 200 }).subscribe({
        next: (res) => { if (res.success) this.partners = res.result?.data ?? []; },
        error: () => this.messageService.add({ severity: 'warn', summary: 'Warning', detail: 'Failed to load partners.' })
      });
      if (this.pricingVariantUuid) this.loadPricingRules();
    }
  }

  onPricingVariantChange(): void {
    this.loadPricingRules();
  }

  loadPricingRules(): void {
    if (!this.pricingVariantUuid) { this.pricingRules = []; return; }
    this.isLoadingPricing = true;
    this.pricingRuleService.getRules({ variantUuid: this.pricingVariantUuid, pageSize: 100 }).subscribe({
      next: (res) => {
        this.isLoadingPricing = false;
        if (res.success) this.pricingRules = res.result?.data ?? [];
      },
      error: (err) => {
        this.isLoadingPricing = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to load pricing rules.' });
      }
    });
  }

  partnerName(uuid?: string | null): string {
    if (!uuid) return 'All Partners';
    return this.partners.find(p => p.uuid === uuid)?.companyName ?? uuid;
  }

  currencyCode(id?: string | null): string {
    if (!id) return '';
    const c = this.currencies.find(x => x.id === id);
    return c?.code ?? c?.name ?? '';
  }

  priceTypeSeverity(type: string): 'success' | 'info' | 'warn' | 'danger' {
    switch (type) {
      case 'CONTRACT':    return 'danger';
      case 'PROMOTIONAL': return 'warn';
      case 'SELLING':     return 'success';
      default:            return 'info'; // COST
    }
  }

  openAddPriceRuleDialog(): void {
    this.editingPriceRule = null;
    this.priceRuleForm.reset({
      partnerUuid: null, priceType: 'SELLING', minQty: null, maxQty: null,
      unitPrice: null, currencyId: this.currencies[0]?.id ?? null,
      effectiveFrom: new Date(), effectiveTo: null, isActive: true
    });
    this.showPriceRuleDialog = true;
  }

  openEditPriceRuleDialog(rule: PricingRuleModel): void {
    this.editingPriceRule = rule;
    this.priceRuleForm.reset({
      partnerUuid: rule.partnerUuid ?? null,
      priceType: rule.priceType,
      minQty: rule.minQty ?? null,
      maxQty: rule.maxQty ?? null,
      unitPrice: rule.unitPrice,
      currencyId: rule.currencyId,
      effectiveFrom: new Date(rule.effectiveFrom),
      effectiveTo: rule.effectiveTo ? new Date(rule.effectiveTo) : null,
      isActive: rule.isActive
    });
    this.showPriceRuleDialog = true;
  }

  savePriceRule(): void {
    if (this.priceRuleForm.invalid) { this.priceRuleForm.markAllAsTouched(); return; }
    if (!this.pricingVariantUuid) return;
    const raw = this.priceRuleForm.value;

    this.isSavingPriceRule = true;
    const onSuccess = (message: string) => {
      this.isSavingPriceRule = false;
      this.showPriceRuleDialog = false;
      this.messageService.add({ severity: 'success', summary: 'Saved', detail: message });
      this.loadPricingRules();
    };
    const onError = (err: { error?: { message?: string } }) => {
      this.isSavingPriceRule = false;
      this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message || 'Save failed.' });
    };

    if (this.editingPriceRule) {
      this.pricingRuleService.updateRule(this.editingPriceRule.uuid, {
        priceType:     raw.priceType,
        minQty:        raw.minQty ?? null,
        maxQty:        raw.maxQty ?? null,
        unitPrice:     raw.unitPrice,
        currencyId:    raw.currencyId,
        effectiveFrom: (raw.effectiveFrom as Date).toISOString(),
        effectiveTo:   raw.effectiveTo ? (raw.effectiveTo as Date).toISOString() : null,
        isActive:      raw.isActive
      }).subscribe({ next: (res) => res.success ? onSuccess('Pricing rule updated.') : onError({ error: { message: res.message } }), error: onError });
    } else {
      this.pricingRuleService.createRule({
        variantUuid:   this.pricingVariantUuid,
        partnerUuid:   raw.partnerUuid ?? null,
        priceType:     raw.priceType,
        minQty:        raw.minQty ?? null,
        maxQty:        raw.maxQty ?? null,
        unitPrice:     raw.unitPrice,
        currencyId:    raw.currencyId ?? null,
        effectiveFrom: (raw.effectiveFrom as Date).toISOString(),
        effectiveTo:   raw.effectiveTo ? (raw.effectiveTo as Date).toISOString() : null
      }).subscribe({ next: (res) => res.success ? onSuccess('Pricing rule created.') : onError({ error: { message: res.message } }), error: onError });
    }
  }

  confirmDeletePriceRule(rule: PricingRuleModel): void {
    this.confirmationService.confirm({
      message: `Delete this ${rule.priceType} price rule? It will be deactivated, not permanently removed.`,
      header: 'Confirm Delete',
      icon: 'pi pi-exclamation-triangle',
      acceptButtonStyleClass: 'p-button-danger',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => {
        this.pricingRuleService.deleteRule(rule.uuid).subscribe({
          next: (res) => {
            if (res.success) {
              this.messageService.add({ severity: 'success', summary: 'Deleted', detail: 'Pricing rule deactivated.' });
              this.loadPricingRules();
            } else {
              this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message });
            }
          },
          error: (err) => this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to delete pricing rule.' })
        });
      }
    });
  }

  // ── Price preview calculator ─────────────────────────────────────────────

  calculatePreview(): void {
    if (this.previewForm.invalid || !this.pricingVariantUuid) { this.previewForm.markAllAsTouched(); return; }
    const raw = this.previewForm.value;
    this.isCalculatingPreview = true;
    this.previewResult = null;
    this.pricingRuleService.resolvePrice(
      this.pricingVariantUuid, raw.partnerUuid ?? null, raw.qty, (raw.date as Date)?.toISOString()
    ).subscribe({
      next: (res) => {
        this.isCalculatingPreview = false;
        if (res.success) {
          this.previewResult = res.result;
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message });
        }
      },
      error: (err) => {
        this.isCalculatingPreview = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to calculate price.' });
      }
    });
  }

  tierLabel(tier?: string | null): string {
    switch (tier) {
      case 'CONTRACT':         return 'Contract';
      case 'PROMOTIONAL':      return 'Promotional';
      case 'PARTNER_SELLING':  return 'Partner-Specific Selling';
      case 'DEFAULT_SELLING':  return 'Default Selling';
      case 'VARIANT_DEFAULT':  return "Variant's List Price";
      default:                 return '—';
    }
  }

  get prf() { return this.priceRuleForm.controls; }
  get pvf() { return this.previewForm.controls; }

  // ── Deactivate ────────────────────────────────────────────────────────────

  confirmDeactivate() {
    this.confirmationService.confirm({
      message: `Deactivate product "${this.product?.name}"? This will set its status to INACTIVE.`,
      header: 'Confirm Deactivation',
      icon: 'pi pi-exclamation-triangle',
      acceptButtonStyleClass: 'p-button-danger',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => {
        this.inventoryService.deleteProduct(this.productId).subscribe({
          next: (res) => {
            if (res.success) {
              this.messageService.add({ severity: 'warn', summary: 'Deactivated', detail: 'Product has been deactivated.' });
              setTimeout(() => this.goBack(), 1200);
            } else {
              this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message });
            }
          },
          error: (err) => {
            this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to deactivate.' });
          }
        });
      }
    });
  }

  // ── Helpers ───────────────────────────────────────────────────────────────

  getStatusSeverity(status: string): 'success' | 'danger' | 'warn' | 'secondary' {
    switch (status?.toUpperCase()) {
      case 'ACTIVE':   return 'success';
      case 'INACTIVE': return 'secondary';
      case 'PENDING':  return 'warn';
      default:         return 'danger';
    }
  }

  getAvailabilityClass(stock: ProductStockModel): string {
    if (stock.qtyAvailable <= 0) return 'qty-zero';
    if (this.product?.reorderPoint != null && stock.qtyAvailable <= this.product.reorderPoint) return 'qty-low';
    return 'qty-ok';
  }

  goBack() {
    this.router.navigate(['/portal/pages/inventory/products']);
  }

  get totalOnHand(): number {
    return this.stockLevels.reduce((sum, s) => sum + s.qtyOnHand, 0);
  }

  get totalAvailable(): number {
    return this.stockLevels.reduce((sum, s) => sum + s.qtyAvailable, 0);
  }

  get ef() { return this.editForm.controls; }
  get af() { return this.adjustmentForm.controls; }
  get vf() { return this.variantForm.controls; }
}
