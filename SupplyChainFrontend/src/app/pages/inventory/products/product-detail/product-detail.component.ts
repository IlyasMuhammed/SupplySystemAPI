import { Component, Injector, OnInit, computed, inject } from '@angular/core';
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
import { SupplierService } from '../../../../services/supplier.service';
import {
  PRODUCT_TYPE_OPTIONS, SUPPLY_METHOD_OPTIONS, SupplyMethodCode, classificationDefaults, isSupplyMethodAllowed,
  productTypeLabel, productTypeOption, supplyMethodLabel
} from '../../../../shared/product-classification';
import { BomManagerComponent } from './bom-manager/bom-manager.component';
import { QboSyncBadgeComponent } from '../../../../shared/components/qbo-sync-badge/qbo-sync-badge.component';
import { QboSyncStatusStore } from '../../../../shared/components/qbo-sync-badge/qbo-sync-status.store';
import { AuthService } from '../../../service/auth.service';
import { TenantService } from '../../../service/tenant.service';
import { VariantRouteFieldComponent } from './variant-route-field/variant-route-field.component';
import { LeadTimesTabComponent } from './lead-times-tab/lead-times-tab.component';
import { FLOW } from '../../../../shared/flow';
import {
  ServiceSettingsFieldsComponent, clearServiceSettingsUnlessService, serviceBillingModelLabel, serviceCategoryLabel,
  serviceInvoicingPolicyLabel, serviceSettingsControls, serviceSettingsPayload
} from '../service-settings-fields/service-settings-fields.component';
import { ProductRoutesPanelComponent } from './product-routes-panel/product-routes-panel.component';
import { BomService } from '../../../../services/bom.service';

/** A37 — reading GET /api/products/{uuid}/routes accepts any of these. */
const PRODUCT_ROUTES_VIEW_CODES = ['FULFILLMENT_ROUTE_VIEW', 'FULFILLMENT_ROUTE_MANAGE', 'FULFILLMENT_ROUTE_ASSIGN'];

/** A34 — any of these may call api/lead-time/calculate-manufacturing (API-CONTRACT §2). */
const LEAD_TIME_CALCULATE_CODES = [
  'SALE_ORDER_VIEW', 'SALE_ORDER_CREATE', 'SALE_ORDER_EDIT', 'SALE_INQUIRY_VIEW', 'SALE_INQUIRY_EDIT',
  'SALE_QUOTATION_VIEW', 'SALE_QUOTATION_EDIT', 'INVENTORY_VIEW', 'STOCK_MANAGE'
];

@Component({
  selector: 'app-product-detail',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule, ReactiveFormsModule,
    ButtonModule, CardModule, TabViewModule, TagModule, ToastModule,
    DialogModule, InputTextModule, TextareaModule, InputNumberModule,
    DividerModule, TooltipModule, ConfirmDialogModule, DropdownModule, TableModule,
    CheckboxModule, CalendarModule, DynamicAttributeFormComponent, BomManagerComponent, QboSyncBadgeComponent,
    VariantRouteFieldComponent, LeadTimesTabComponent, ServiceSettingsFieldsComponent, ProductRoutesPanelComponent,
    ...FLOW
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

  /** The variants table's QuickBooks column: only for organizations with the integration and users who may see it. */
  private readonly qboStore = inject(QboSyncStatusStore);
  readonly qboAvailable = computed(() => this.qboStore.isAvailable());

  // ── Edit dialog ───────────────────────────────────────────────────────────
  showEditDialog = false;
  editForm!: FormGroup;
  isSaving = false;
  categories: CategoryModel[] = [];
  categoryOptions: { label: string; value: number | null }[] = [];
  subCategoryOptions: { label: string; value: number | null }[] = [];
  isLoadingLookups = false;
  supplierOptions: { label: string; value: number }[] = [];

  // A30 §6 — manufacturing classification.
  readonly productTypeOptions = PRODUCT_TYPE_OPTIONS;
  readonly supplyMethodOptions = SUPPLY_METHOD_OPTIONS;
  // warehouseOptions (declared with the adjustment dialog below) is shared with the edit dialog.
  readonly productTypeLabel = productTypeLabel;
  readonly supplyMethodLabel = supplyMethodLabel;

  get productTypeHint(): string {
    return productTypeOption(this.editForm?.get('productType')?.value)?.description ?? '';
  }

  get editIsManufactured(): boolean {
    return this.editForm?.get('supplyMethod')?.value === 'MANUFACTURE';
  }

  // ── A36 — service products ────────────────────────────────────────────────
  readonly serviceInvoicingPolicyLabel = serviceInvoicingPolicyLabel;
  readonly serviceBillingModelLabel = serviceBillingModelLabel;

  get isService(): boolean { return this.product?.productType === 'SERVICE'; }
  get editIsService(): boolean { return this.editForm?.get('productType')?.value === 'SERVICE'; }
  /** D-4 — a BOM is eligible for a SERVICE product once it has a service BOM. */
  get isServiceBomProduct(): boolean { return this.isService && !!this.product?.hasServiceBom; }
  /**
   * A37 — with FEATURE_BOM_MANAGEMENT the tab follows the product (manufactured, or a service with a service BOM);
   * without it the BOMs are read only (BOM-SHR-03), so the tab shows only when the product has any.
   */
  get showBomTab(): boolean {
    if (!this.bomManagementEnabled) return this.bomExists;
    return !!this.product?.isManufacturable || this.isServiceBomProduct;
  }

  // ── A37 PRD-CAP — module-dependent sections (API-CONTRACT §2) ──────────────
  readonly serviceCategoryLabel = serviceCategoryLabel;
  /** The server sends `productionSettings` only while MODULE_MANUFACTURING is enabled (PRD-CAP-03). */
  get hasProductionSettings(): boolean { return !!this.product && this.product.productionSettings != null; }
  /** The server sends `serviceSettings` only while MODULE_SERVICES is enabled (PRD-CAP-03). */
  get hasServiceSettings(): boolean { return !!this.product && this.product.serviceSettings != null; }
  /** Derived on the server (= SERVICE type); the type itself when an older server leaves it out. */
  get isServiceable(): boolean { return this.product?.isServiceable ?? this.isService; }
  /** PRD-CAP-02 — the section needs both the module (key present) and the flag. */
  get showProductionSection(): boolean { return this.hasProductionSettings && !!this.product?.isManufacturable; }
  get showServiceSection(): boolean { return this.hasServiceSettings && this.isServiceable; }
  get productionWarehouseName(): string | null {
    return this.product?.productionSettings?.defaultProductionWarehouseName ?? this.product?.defaultProductionWarehouseName ?? null;
  }
  get requiresSiteVisit(): boolean {
    return !!(this.product?.requiresSiteVisit ?? this.product?.serviceSettings?.requiresSiteVisit);
  }

  /** A37 D-11 — create/edit/workflow on BOMs need FEATURE_BOM_MANAGEMENT; reading them does not. */
  get bomManagementEnabled(): boolean { return this.tenantService.hasFeature('FEATURE_BOM_MANAGEMENT'); }
  /** Checked only while BOM management is off: does the product have a BOM to show read-only? */
  bomExists = false;
  private readonly injector = inject(Injector);

  /** Set when GET /api/products/{uuid}/routes answers 403: the Routes tab is then hidden. */
  productRoutesForbidden = false;
  get canViewProductRoutes(): boolean {
    return !this.productRoutesForbidden && this.routesEnabled && PRODUCT_ROUTES_VIEW_CODES.some(code => this.authService.hasPermission(code));
  }
  /** D-3 — the product page's warning instead of refusing the save. */
  get missingActiveServiceBom(): boolean { return this.isServiceBomProduct && !this.product?.hasActiveServiceBom; }

  supplyMethodDisabled(method: SupplyMethodCode): boolean {
    return !isSupplyMethodAllowed(this.editForm?.get('productType')?.value, method);
  }

  onProductTypeChange(): void {
    const defaults = classificationDefaults(this.editForm.get('productType')?.value);
    this.editForm.patchValue({
      supplyMethod: defaults.supplyMethod,
      isSaleable: defaults.isSaleable,
      isPurchasable: defaults.isPurchasable,
      isStockable: defaults.isStockable
    });
    clearServiceSettingsUnlessService(this.editForm);
  }

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

  // A33-PB-04 — the variant's fulfillment route. Saved after the variant through its own gated call
  // (PUT api/variants/{uuid}/fulfillment-route, FULFILLMENT_ROUTE_ASSIGN); PATCH api/variants ignores it.
  // Hidden for organizations without Logistics (D-11: no routes there).
  variantRouteUuid: string | null = null;
  private readonly authService = inject(AuthService);
  private readonly tenantService = inject(TenantService);
  get routesEnabled(): boolean { return this.tenantService.hasFeature('MODULE_LOGISTICS'); }
  get canAssignRoute(): boolean { return this.authService.hasPermission('FULFILLMENT_ROUTE_ASSIGN'); }

  // A34 — make-to-order routes are offered only for MANUFACTURE products (D-3) in MODULE_MANUFACTURING orgs (D-9).
  get productManufactured(): boolean { return this.product?.supplyMethod === 'MANUFACTURE'; }
  get manufacturingEnabled(): boolean { return this.tenantService.hasFeature('MODULE_MANUFACTURING'); }

  // A34-PB-07 (D-26) — the Lead Times tab: editable with STOCK_MANAGE (the PUT's code); "Recalculate from BOM" with a
  // calculate code (contract §2).
  get canEditLeadTimes(): boolean { return this.authService.hasPermission('STOCK_MANAGE'); }
  get canCalculateLeadTimes(): boolean { return LEAD_TIME_CALCULATE_CODES.some(c => this.authService.hasPermission(c)); }

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
    private businessPartnerService: BusinessPartnerService,
    private supplierService: SupplierService
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
      maxStockLevel:  [null, [Validators.min(0)]],
      preferredSupplierId: [null],
      productType:    ['STOCK_ITEM'],
      supplyMethod:   ['PURCHASE'],
      isSaleable:     [true],
      isPurchasable:  [true],
      isStockable:    [true],
      defaultProductionWarehouseId: [null],
      ...serviceSettingsControls()
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
      isDefault:     [false],
      isAvailableForRetail:     [false],
      isAvailableForPos:        [false],
      isAvailableForMirMiv:     [false],
      isAvailableForProduction: [false],
      isAvailableForServices:   [false],
      saleOrderMinQty: [null, [Validators.min(0)]],
      saleOrderMaxQty: [null, [Validators.min(0)]]
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
          this.checkBomExists();
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

  /** A37 BOM-SHR-03 — only when BOM management is off; the BOM service is fetched lazily for that case alone. */
  private checkBomExists(): void {
    if (this.bomManagementEnabled || !this.product?.uuid) { this.bomExists = false; return; }
    this.injector.get(BomService).getBoms({ productUuid: this.product.uuid, pageSize: 1 }).subscribe({
      next: (res) => { this.bomExists = (res.result?.totalRecords ?? res.result?.data?.length ?? 0) > 0; },
      error: () => { this.bomExists = false; }
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
      maxStockLevel:  this.product.maxStockLevel   ?? null,
      preferredSupplierId: this.product.preferredSupplierId ?? null,
      productType:    this.product.productType     ?? 'STOCK_ITEM',
      supplyMethod:   this.product.supplyMethod    ?? 'PURCHASE',
      isSaleable:     this.product.isSaleable      ?? true,
      isPurchasable:  this.product.isPurchasable   ?? true,
      isStockable:    this.product.isStockable     ?? true,
      defaultProductionWarehouseId: this.product.defaultProductionWarehouseId ?? null,
      serviceInvoicingPolicy: this.product.serviceInvoicingPolicy ?? null,
      serviceBillingModel:    this.product.serviceBillingModel    ?? null,
      estimatedDurationHours: this.product.estimatedDurationHours ?? null,
      hasServiceBom:          !!this.product.hasServiceBom,
      isSubcontractable:      !!this.product.isSubcontractable,
      serviceCategory:        this.product.serviceCategory ?? this.product.serviceSettings?.serviceCategory ?? null,
      requiresSiteVisit:      !!(this.product.requiresSiteVisit ?? this.product.serviceSettings?.requiresSiteVisit)
    });
    this.loadLookups().then(() => {
      this.loadSuppliers();
      this.loadWarehouses();
      this.showEditDialog = true;
    });
  }

  private loadWarehouses() {
    if (this.warehouseOptions.length > 0) return;
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

  private loadSuppliers() {
    if (this.supplierOptions.length > 0) return;
    this.supplierService.getSuppliers({ status: 'ACTIVE', pageSize: 500 }).subscribe({
      next: (res) => {
        if (res.success) {
          this.supplierOptions = res.result.data.map(s => ({ label: s.supplierName, value: s.id }));
        }
      },
      error: () => {
        this.messageService.add({ severity: 'warn', summary: 'Warning', detail: 'Failed to load suppliers.' });
      }
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
      maxStockLevel: raw.maxStockLevel ?? undefined,
      preferredSupplierId: raw.preferredSupplierId ?? undefined,
      productType:   raw.productType,
      supplyMethod:  raw.supplyMethod,
      isSaleable:    !!raw.isSaleable,
      isPurchasable: !!raw.isPurchasable,
      isStockable:   !!raw.isStockable,
      defaultProductionWarehouseId: raw.defaultProductionWarehouseId ?? undefined,
      ...serviceSettingsPayload(raw)
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
      barcode: '', weight: null, dimensions: '', reorderPoint: null, sortOrder: null, isDefault: false,
      isAvailableForRetail: false, isAvailableForPos: false, isAvailableForMirMiv: false,
      isAvailableForProduction: false, isAvailableForServices: false,
      saleOrderMinQty: null, saleOrderMaxQty: null
    });
    this.variantRouteUuid = null;
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
      isDefault: variant.isDefault,
      isAvailableForRetail: variant.isAvailableForRetail,
      isAvailableForPos: variant.isAvailableForPos,
      isAvailableForMirMiv: variant.isAvailableForMirMiv,
      isAvailableForProduction: variant.isAvailableForProduction,
      isAvailableForServices: variant.isAvailableForServices,
      saleOrderMinQty: variant.saleOrderMinQty ?? null,
      saleOrderMaxQty: variant.saleOrderMaxQty ?? null
    });
    this.variantRouteUuid = variant.fulfillmentRouteUuid ?? null;
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
    // A31-BR-C1-05 — mirrors the server's own check, so the dialog can refuse this before a round trip.
    if (raw.saleOrderMinQty > 0 && raw.saleOrderMaxQty > 0 && raw.saleOrderMinQty > raw.saleOrderMaxQty) {
      this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Sale order maximum quantity must be greater than or equal to the minimum.' });
      return;
    }
    const payload: CreateProductVariantRequest = {
      sku:           raw.sku || undefined,
      variantName:   raw.variantName,
      barcode:       raw.barcode || undefined,
      purchasePrice: raw.purchasePrice,
      sellingPrice:  raw.sellingPrice ?? undefined,
      weight:        raw.weight ?? undefined,
      dimensions:    raw.dimensions || undefined,
      isDefault:     !!raw.isDefault,
      isAvailableForRetail:     !!raw.isAvailableForRetail,
      isAvailableForPos:        !!raw.isAvailableForPos,
      isAvailableForMirMiv:     !!raw.isAvailableForMirMiv,
      isAvailableForProduction: !!raw.isAvailableForProduction,
      isAvailableForServices:   !!raw.isAvailableForServices,
      reorderPoint:  raw.reorderPoint ?? undefined,
      sortOrder:     raw.sortOrder ?? undefined,
      saleOrderMinQty: raw.saleOrderMinQty ?? undefined,
      saleOrderMaxQty: raw.saleOrderMaxQty ?? undefined
    };

    this.isSavingVariant = true;
    if (this.editingVariant) {
      const variantUuid = this.editingVariant.uuid;
      const savedRoute = this.editingVariant.fulfillmentRouteUuid ?? null;
      this.inventoryService.updateVariant(variantUuid, payload).subscribe({
        next: (res) => {
          if (res.success) {
            this.saveVariantAttributesThen(variantUuid, () =>
              this.saveVariantRouteThen(variantUuid, savedRoute, () => this.finishVariantSave('Variant updated.')));
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
            const variantUuid = res.result.uuid;
            this.saveVariantAttributesThen(variantUuid, () =>
              this.saveVariantRouteThen(variantUuid, null, () => this.finishVariantSave('Variant added.')));
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

  // A33-PB-04 — the route is a secondary write too, through its own gated call and only when it changed. A refusal
  // (inactive or unknown route, 400) is a warning: the variant itself is saved.
  private saveVariantRouteThen(variantUuid: string, savedRoute: string | null, then: () => void): void {
    const route = this.variantRouteUuid ?? null;
    if (!this.routesEnabled || !this.canAssignRoute || route === savedRoute) { then(); return; }
    this.inventoryService.setVariantFulfillmentRoute(variantUuid, route).subscribe({
      next: () => then(),
      error: (err) => {
        this.messageService.add({
          severity: 'warn', summary: 'Partial Save', life: 8000,
          detail: `Variant saved, but the fulfillment route was not set: ${err?.error?.message || 'the request failed.'}`
        });
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
