import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

// ── Shared response wrappers ──────────────────────────────────────────────────
export interface ApiResponse<T = null> {
  success: boolean;
  message: string;
  result: T;
}

export interface PaginatedResponse<T> {
  data: T[];
  totalRecords: number;
  page: number;
  pageSize: number;
  totalPages: number;
}

// ── Categories ────────────────────────────────────────────────────────────────
export interface SubCategoryModel {
  id: number;
  categoryId: number;
  name: string;
  code?: string;
  description?: string;
  isActive: boolean;
}

export interface CategoryModel {
  id: number;
  name: string;
  code: string;
  description?: string;
  isActive: boolean;
  createdDate: string;
  subCategories: SubCategoryModel[];
}

export interface CreateCategoryRequest { name: string; code: string; description?: string; }
export interface UpdateCategoryRequest { name: string; description?: string; isActive: boolean; }
export interface CreateSubCategoryRequest { name: string; code: string; description?: string; }
export interface UpdateSubCategoryRequest {
  name: string;
  categoryId: number;
  description?: string;
  isActive: boolean;
}

export interface CategoryDeleteConflictResult {
  referencedProductCount: number;
  referencedSubCategoryCount: number;
}

export interface SubCategoryDeleteConflictResult {
  referencedProductCount: number;
}

export interface SubCategoryListFilter {
  categoryId?: number;
  search?: string;
  isActive?: boolean;
  page?: number;
  pageSize?: number;
}

export interface SubCategoryListDto {
  subCategoryId: number;
  subCategoryCode: string;
  subCategoryName: string;
  parentCategoryId: number;
  parentCategoryName: string;
  isActive: boolean;
  productCount: number;
}

// ── Products ──────────────────────────────────────────────────────────────────
export interface ProductListItemModel {
  id: number;
  uuid: string;
  sku: string;
  name: string;
  shortName?: string;
  brand?: string;
  categoryId?: number;
  categoryName?: string;
  subCategoryId?: number;
  subCategoryName?: string;
  uomCode?: string;
  status: string;
  isBatchTracked: boolean;
  isSerialTracked: boolean;
  createdDate: string;
  variantCount: number;
  defaultVariantPurchasePrice?: number;
}

export interface ProductDetailModel extends ProductListItemModel {
  description?: string;
  weightKg?: number;
  dimensions?: string;
  shelfLifeDays?: number;
  reorderPoint?: number;
  reorderQty?: number;
  minStockLevel?: number;
  maxStockLevel?: number;
  leadTimeDays?: number;
  preferredSupplierId?: number;
  notes?: string;
  imageUrl?: string;
  updatedDate?: string;
  createdBy: number;
  variants: ProductVariantModel[];
}

// ── Product Variants (FSD Addendum 26 / PV-001) ────────────────────────────────
export interface ProductVariantModel {
  id: number;
  uuid: string;
  sku: string;
  variantName: string;
  barcode?: string;
  purchasePrice: number;
  sellingPrice?: number;
  lastPurchasePrice?: number;
  weightKg?: number;
  dimensions?: string;
  isDefault: boolean;
  isActive: boolean;
  reorderPoint?: number;
  sortOrder?: number;
  createdDate: string;
}

export interface CreateProductVariantRequest {
  sku?: string;
  variantName: string;
  barcode?: string;
  purchasePrice: number;
  sellingPrice?: number;
  weight?: number;
  dimensions?: string;
  isDefault: boolean;
  reorderPoint?: number;
  sortOrder?: number;
}

// PV-007 — variant CRUD on an existing product.
export interface CreateVariantResult {
  uuid: string;
  sku: string;
}

export interface VariantDeleteResult {
  softDeleted: boolean;
}

// PV-004 — GRN barcode scan resolves straight to the matching variant + its parent product.
export interface VariantLookupModel {
  uuid: string;
  sku: string;
  variantName: string;
  barcode?: string;
  purchasePrice: number;
  productId: number;
  productUuid: string;
  productName: string;
}

export interface ProductListFilter {
  categoryId?: number;
  status?: string;
  search?: string;
  activeOnly?: boolean;
  page?: number;
  pageSize?: number;
}

export interface CreateProductRequest {
  sku?: string;
  name: string;
  shortName?: string;
  description?: string;
  categoryId?: number;
  subCategoryId?: number;
  brand?: string;
  uomCode?: string;
  weightKg?: number;
  dimensions?: string;
  shelfLifeDays?: number;
  isBatchTracked?: boolean;
  isSerialTracked?: boolean;
  reorderPoint?: number;
  reorderQty?: number;
  minStockLevel?: number;
  maxStockLevel?: number;
  leadTimeDays?: number;
  preferredSupplierId?: number;
  notes?: string;
  imageUrl?: string;

  // Variant seeding (PV-001) — supply Variants for a multi-SKU product (exactly one
  // isDefault=true), or omit it and supply purchasePrice for a single auto-created default
  // variant (simple products, e.g. Cement).
  variants?: CreateProductVariantRequest[];
  purchasePrice?: number;
  sellingPrice?: number;
  barcode?: string;
}

export interface PatchProductRequest {
  name?: string;
  shortName?: string;
  description?: string;
  categoryId?: number;
  subCategoryId?: number;
  brand?: string;
  uomCode?: string;
  weightKg?: number;
  dimensions?: string;
  shelfLifeDays?: number;
  isBatchTracked?: boolean;
  isSerialTracked?: boolean;
  reorderPoint?: number;
  reorderQty?: number;
  minStockLevel?: number;
  maxStockLevel?: number;
  leadTimeDays?: number;
  preferredSupplierId?: number;
  notes?: string;
  imageUrl?: string;
  status?: string;
}

// ── Dynamic Attributes (FSD Addendum 26 §4) ─────────────────────────────────────
export interface AttributeDefinitionModel {
  uuid: string;
  attributeName: string;
  displayName: string;
  dataType: 'TEXT' | 'NUMBER' | 'DECIMAL' | 'DATE' | 'BOOLEAN' | 'DROPDOWN' | 'MULTI_SELECT';
  controlType: 'TEXTBOX' | 'NUMBERBOX' | 'DATEPICKER' | 'TOGGLE' | 'DROPDOWN' | 'MULTI_SELECT' | 'TEXTAREA';
  dropdownOptions?: string[];
  defaultValue?: string;
  validationRegex?: string;
  isRequired: boolean;
  isSearchable: boolean;
  isFilterable: boolean;
  sortOrder: number;
  isActive: boolean;
}

export interface CreateAttributeDefinitionRequest {
  attributeName: string;
  displayName: string;
  dataType: string;
  controlType: string;
  dropdownOptions?: string[];
  defaultValue?: string;
  validationRegex?: string;
  isRequired?: boolean;
  isSearchable?: boolean;
  isFilterable?: boolean;
  sortOrder?: number;
}

export interface UpdateAttributeDefinitionRequest {
  displayName?: string;
  controlType?: string;
  dropdownOptions?: string[];
  defaultValue?: string;
  validationRegex?: string;
  isRequired?: boolean;
  isSearchable?: boolean;
  isFilterable?: boolean;
  sortOrder?: number;
  isActive?: boolean;
}

export interface AttributeDeleteConflictResult {
  referencedCategoryCount: number;
  referencedVariantValueCount: number;
}

export interface CategoryAttributeModel {
  categoryId: number;
  attributeUuid: string;
  attributeName: string;
  displayName: string;
  dataType: AttributeDefinitionModel['dataType'];
  controlType: AttributeDefinitionModel['controlType'];
  dropdownOptions?: string[];
  validationRegex?: string;
  isRequired: boolean;
  isSearchable: boolean;
  displayOrder: number;
}

export interface CreateCategoryAttributeRequest {
  attributeUuid: string;
  isRequired?: boolean;
  displayOrder: number;
}

export interface CategoryAttributeOrderItem {
  attributeUuid: string;
  isRequired: boolean;
  displayOrder: number;
}

export interface SetCategoryAttributesRequest {
  attributes: CategoryAttributeOrderItem[];
}

export interface VariantAttributeValueModel {
  attributeUuid: string;
  attributeName: string;
  displayName: string;
  dataType: AttributeDefinitionModel['dataType'];
  value: string;
}

export interface VariantAttributeValueInput {
  attributeUuid: string;
  value: string;
}

export interface SetVariantAttributeValuesRequest {
  values: VariantAttributeValueInput[];
}

// ── Warehouses ────────────────────────────────────────────────────────────────
export interface WarehouseModel {
  id: number;
  uuid: string;
  code: string;
  name: string;
  address?: string;
  city?: string;
  country?: string;
  contactName?: string;
  contactPhone?: string;
  googleMapsUrl?: string;
  latitude?: number;
  longitude?: number;
  isActive: boolean;
  createdDate: string;
}

export interface CreateWarehouseRequest {
  code: string;
  name: string;
  address?: string;
  city?: string;
  country?: string;
  contactName?: string;
  contactPhone?: string;
  googleMapsUrl?: string;
  latitude?: number;
  longitude?: number;
}

// ── Warehouse structure ───────────────────────────────────────────────────────
export interface StructureConflictResult { childCount: number; childType: string; }

export interface BinNodeModel {
  id: number; zoneId: number; rackId?: number; shelfId?: number;
  code: string; description?: string; isActive: boolean;
}
export interface ShelfNodeModel {
  id: number; rackId: number; shelfCode: string; shelfLevel?: string; isActive: boolean;
  bins: BinNodeModel[];
}
export interface RackNodeModel {
  id: number; zoneId: number; rackCode: string; rackName?: string; isActive: boolean;
  shelves: ShelfNodeModel[];
  directBins: BinNodeModel[];
}
export interface ZoneNodeModel {
  id: number; name: string; code?: string; description?: string; isActive: boolean;
  racks: RackNodeModel[];
  directBins: BinNodeModel[];
}
export interface WarehouseStructureModel {
  warehouseId: number;
  zones: ZoneNodeModel[];
}

export interface CreateZoneRequest  { name: string; code?: string; description?: string; }
export interface UpdateZoneRequest  { name?: string; code?: string; description?: string; }
export interface CreateRackRequest  { rackCode: string; rackName?: string; }
export interface UpdateRackRequest  { rackCode?: string; rackName?: string; }
export interface CreateShelfRequest { shelfCode: string; shelfLevel?: string; }
export interface UpdateShelfRequest { shelfCode?: string; shelfLevel?: string; }
export interface CreateBinRequest   { code: string; description?: string; }
export interface UpdateBinRequest   { code?: string; description?: string; }

// ── Stock ─────────────────────────────────────────────────────────────────────
export interface StockLevelModel {
  inventoryItemId: number;
  variantId: number;
  variantUuid: string;
  variantSku: string;
  variantName: string;
  productId: number;
  productUuid: string;
  productName: string;
  categoryName?: string;
  uomCode?: string;
  warehouseId: number;
  warehouseName: string;
  binId?: number;
  binCode?: string;
  qtyOnHand: number;
  qtyReserved: number;
  qtyAvailable: number;
  qtyOnOrder: number;
  reorderPoint?: number;
  isBelowReorder: boolean;
  lastUpdated: string;
}

export interface StockLevelFilter {
  categoryId?: number;
  belowReorderOnly?: boolean;
  includeZeroStock?: boolean;
  page?: number;
  pageSize?: number;
}

export interface ProductStockModel {
  warehouseId: number;
  warehouseUuid: string;
  warehouseCode: string;
  warehouseName: string;
  binId?: number;
  binCode?: string;
  qtyOnHand: number;
  qtyReserved: number;
  qtyAvailable: number;
  qtyOnOrder: number;
  lastUpdated: string;
}

export interface ReorderAlertModel {
  variantId: number;
  variantUuid: string;
  variantSku: string;
  variantName: string;
  productId: number;
  productUuid: string;
  productName: string;
  categoryName?: string;
  warehouseId: number;
  warehouseUuid: string;
  warehouseName: string;
  qtyOnHand: number;
  qtyAvailable: number;
  reorderPoint: number;
  reorderQty?: number;
}

// PV-005 — product-level rollup across every one of its variants' stock. Computed on the fly,
// never stored.
export interface VariantStockSummaryItem {
  variantUuid: string;
  sku: string;
  variantName: string;
  onHand: number;
  reserved: number;
  available: number;
}

export interface ProductStockSummaryModel {
  productId: number;
  productUuid: string;
  totalOnHand: number;
  totalReserved: number;
  totalAvailable: number;
  variants: VariantStockSummaryItem[];
}

// ── Adjustments ───────────────────────────────────────────────────────────────
export interface StockAdjustmentModel {
  id: number;
  uuid: string;
  adjNumber?: string;
  inventoryItemId: number;
  variantId: number;
  variantSku: string;
  variantName: string;
  productName: string;
  warehouseId: number;
  warehouseName: string;
  adjType?: string;
  reason?: string;
  referenceDoc?: string;
  qtyBefore: number;
  qtyAdjusted: number;
  qtyAfter: number;
  unitCost?: number;
  notes?: string;
  status: string;
  rejectionReason?: string;
  createdDate: string;
  createdBy: number;
  reviewedBy?: number;
  reviewedDate?: string;
}

export interface AdjustmentListFilter {
  variantId?: number;
  warehouseId?: number;
  status?: string;
  adjType?: string;
  page?: number;
  pageSize?: number;
}

export interface CreateAdjustmentRequest {
  variantId: number;
  warehouseId: number;
  adjType?: string;
  reason?: string;
  referenceDoc?: string;
  qtyAdjusted: number;
  unitCost?: number;
  notes?: string;
}

export interface RejectAdjustmentRequest { reason: string; }

export interface StockAdjustmentResult {
  id: number;
  uuid: string;
  adjNumber?: string;
  status: string;
  stockUpdated: boolean;
}

// ── Service ───────────────────────────────────────────────────────────────────
@Injectable({ providedIn: 'root' })
export class InventoryService {
  private readonly base = environment.apiUrl;

  constructor(private http: HttpClient) {}

  // Categories
  getCategories(): Observable<ApiResponse<CategoryModel[]>> {
    return this.http.get<ApiResponse<CategoryModel[]>>(`${this.base}/product-categories`);
  }
  createCategory(data: CreateCategoryRequest): Observable<ApiResponse<number>> {
    return this.http.post<ApiResponse<number>>(`${this.base}/product-categories`, data);
  }
  updateCategory(id: number, data: UpdateCategoryRequest): Observable<ApiResponse> {
    return this.http.put<ApiResponse>(`${this.base}/product-categories/${id}`, data);
  }
  deleteCategory(id: number): Observable<ApiResponse<CategoryDeleteConflictResult>> {
    return this.http.delete<ApiResponse<CategoryDeleteConflictResult>>(`${this.base}/product-categories/${id}`);
  }
  deactivateCategory(id: number): Observable<ApiResponse> {
    return this.http.patch<ApiResponse>(`${this.base}/product-categories/${id}/deactivate`, {});
  }
  getSubCategories(filter: SubCategoryListFilter = {}): Observable<ApiResponse<PaginatedResponse<SubCategoryListDto>>> {
    let params = new HttpParams();
    if (filter.categoryId != null) params = params.set('categoryId', String(filter.categoryId));
    if (filter.search)             params = params.set('search',     filter.search);
    if (filter.isActive != null)   params = params.set('isActive',   String(filter.isActive));
    params = params.set('page',     String(filter.page     ?? 1));
    params = params.set('pageSize', String(filter.pageSize ?? 50));
    return this.http.get<ApiResponse<PaginatedResponse<SubCategoryListDto>>>(`${this.base}/product-categories/sub-categories`, { params });
  }
  createSubCategory(categoryId: number, data: CreateSubCategoryRequest): Observable<ApiResponse<number>> {
    return this.http.post<ApiResponse<number>>(`${this.base}/product-categories/${categoryId}/sub-categories`, data);
  }
  updateSubCategory(subId: number, data: UpdateSubCategoryRequest): Observable<ApiResponse> {
    return this.http.put<ApiResponse>(`${this.base}/product-categories/sub-categories/${subId}`, data);
  }
  deleteSubCategory(subId: number): Observable<ApiResponse<SubCategoryDeleteConflictResult>> {
    return this.http.delete<ApiResponse<SubCategoryDeleteConflictResult>>(`${this.base}/product-categories/sub-categories/${subId}`);
  }
  deactivateSubCategory(subId: number): Observable<ApiResponse> {
    return this.http.patch<ApiResponse>(`${this.base}/product-categories/sub-categories/${subId}/deactivate`, {});
  }

  // Products
  getProducts(filter: ProductListFilter = {}): Observable<ApiResponse<PaginatedResponse<ProductListItemModel>>> {
    let params = new HttpParams();
    if (filter.categoryId)        params = params.set('categoryId',  String(filter.categoryId));
    if (filter.status)            params = params.set('status',       filter.status);
    if (filter.search)            params = params.set('search',       filter.search);
    if (filter.activeOnly != null) params = params.set('activeOnly', String(filter.activeOnly));
    params = params.set('page',     String(filter.page     ?? 1));
    params = params.set('pageSize', String(filter.pageSize ?? 20));
    return this.http.get<ApiResponse<PaginatedResponse<ProductListItemModel>>>(`${this.base}/products`, { params });
  }
  getProductById(id: number): Observable<ApiResponse<ProductDetailModel>> {
    return this.http.get<ApiResponse<ProductDetailModel>>(`${this.base}/products/${id}`);
  }
  // PV-004 — GRN barcode scan. Unmatched barcodes 404; callers should handle via the error channel.
  getVariantByBarcode(barcode: string): Observable<ApiResponse<VariantLookupModel>> {
    const params = new HttpParams().set('barcode', barcode);
    return this.http.get<ApiResponse<VariantLookupModel>>(`${this.base}/variants/lookup`, { params });
  }
  createProduct(data: CreateProductRequest): Observable<ApiResponse<{ id: number; sku: string }>> {
    return this.http.post<ApiResponse<{ id: number; sku: string }>>(`${this.base}/products`, data);
  }
  patchProduct(id: number, data: PatchProductRequest): Observable<ApiResponse> {
    return this.http.patch<ApiResponse>(`${this.base}/products/${id}`, data);
  }
  deleteProduct(id: number): Observable<ApiResponse> {
    return this.http.delete<ApiResponse>(`${this.base}/products/${id}`);
  }
  getProductStock(id: number): Observable<ApiResponse<ProductStockModel[]>> {
    return this.http.get<ApiResponse<ProductStockModel[]>>(`${this.base}/products/${id}/stock`);
  }
  // PV-005 — aggregated on-hand/reserved/available across every variant of the product.
  getProductStockSummary(id: number): Observable<ApiResponse<ProductStockSummaryModel>> {
    return this.http.get<ApiResponse<ProductStockSummaryModel>>(`${this.base}/products/${id}/stock-summary`);
  }

  // PV-007 — variant CRUD on an existing product.
  addVariant(productId: number, data: CreateProductVariantRequest): Observable<ApiResponse<CreateVariantResult>> {
    return this.http.post<ApiResponse<CreateVariantResult>>(`${this.base}/products/${productId}/variants`, data);
  }
  updateVariant(variantUuid: string, data: CreateProductVariantRequest): Observable<ApiResponse> {
    return this.http.patch<ApiResponse>(`${this.base}/variants/${variantUuid}`, data);
  }
  // Soft-deletes (is_active=false) if the variant has ever been transacted; hard-deletes
  // otherwise — result.softDeleted tells the caller which happened.
  deleteVariant(variantUuid: string): Observable<ApiResponse<VariantDeleteResult>> {
    return this.http.delete<ApiResponse<VariantDeleteResult>>(`${this.base}/variants/${variantUuid}`);
  }

  // Warehouses
  getWarehouses(): Observable<ApiResponse<WarehouseModel[]>> {
    return this.http.get<ApiResponse<WarehouseModel[]>>(`${this.base}/warehouses`);
  }
  createWarehouse(data: CreateWarehouseRequest): Observable<ApiResponse<number>> {
    return this.http.post<ApiResponse<number>>(`${this.base}/warehouses`, data);
  }
  updateWarehouse(id: number, data: Partial<CreateWarehouseRequest> & { isActive?: boolean }): Observable<ApiResponse> {
    return this.http.patch<ApiResponse>(`${this.base}/warehouses/${id}`, data);
  }
  deleteWarehouse(id: number): Observable<ApiResponse> {
    return this.http.delete<ApiResponse>(`${this.base}/warehouses/${id}`);
  }

  // Warehouse structure
  getWarehouseStructure(warehouseId: number): Observable<ApiResponse<WarehouseStructureModel>> {
    return this.http.get<ApiResponse<WarehouseStructureModel>>(`${this.base}/warehouses/${warehouseId}/structure`);
  }
  createZone(warehouseId: number, data: CreateZoneRequest): Observable<ApiResponse<number>> {
    return this.http.post<ApiResponse<number>>(`${this.base}/warehouses/${warehouseId}/zones`, data);
  }
  updateZone(id: number, data: UpdateZoneRequest): Observable<ApiResponse> {
    return this.http.put<ApiResponse>(`${this.base}/zones/${id}`, data);
  }
  deactivateZone(id: number): Observable<ApiResponse<StructureConflictResult>> {
    return this.http.patch<ApiResponse<StructureConflictResult>>(`${this.base}/zones/${id}/deactivate`, {});
  }
  createRack(zoneId: number, data: CreateRackRequest): Observable<ApiResponse<number>> {
    return this.http.post<ApiResponse<number>>(`${this.base}/zones/${zoneId}/racks`, data);
  }
  updateRack(id: number, data: UpdateRackRequest): Observable<ApiResponse> {
    return this.http.put<ApiResponse>(`${this.base}/racks/${id}`, data);
  }
  deactivateRack(id: number): Observable<ApiResponse<StructureConflictResult>> {
    return this.http.patch<ApiResponse<StructureConflictResult>>(`${this.base}/racks/${id}/deactivate`, {});
  }
  createShelf(rackId: number, data: CreateShelfRequest): Observable<ApiResponse<number>> {
    return this.http.post<ApiResponse<number>>(`${this.base}/racks/${rackId}/shelves`, data);
  }
  updateShelf(id: number, data: UpdateShelfRequest): Observable<ApiResponse> {
    return this.http.put<ApiResponse>(`${this.base}/shelves/${id}`, data);
  }
  deactivateShelf(id: number): Observable<ApiResponse<StructureConflictResult>> {
    return this.http.patch<ApiResponse<StructureConflictResult>>(`${this.base}/shelves/${id}/deactivate`, {});
  }
  createLegacyBin(zoneId: number, data: CreateBinRequest): Observable<ApiResponse<number>> {
    return this.http.post<ApiResponse<number>>(`${this.base}/warehouses/zones/${zoneId}/bins`, data);
  }
  createStructuredBin(shelfId: number, data: CreateBinRequest): Observable<ApiResponse<number>> {
    return this.http.post<ApiResponse<number>>(`${this.base}/shelves/${shelfId}/bins`, data);
  }
  updateBin(id: number, data: UpdateBinRequest): Observable<ApiResponse> {
    return this.http.put<ApiResponse>(`${this.base}/bins/${id}`, data);
  }
  deactivateBin(id: number): Observable<ApiResponse> {
    return this.http.patch<ApiResponse>(`${this.base}/bins/${id}/deactivate`, {});
  }

  getWarehouseStock(warehouseId: number, filter: StockLevelFilter = {}): Observable<ApiResponse<PaginatedResponse<StockLevelModel>>> {
    let params = new HttpParams();
    if (filter.categoryId)       params = params.set('categoryId',      String(filter.categoryId));
    if (filter.belowReorderOnly) params = params.set('belowReorderOnly', 'true');
    if (filter.includeZeroStock) params = params.set('includeZeroStock', 'true');
    params = params.set('page',     String(filter.page     ?? 1));
    params = params.set('pageSize', String(filter.pageSize ?? 20));
    return this.http.get<ApiResponse<PaginatedResponse<StockLevelModel>>>(`${this.base}/warehouses/${warehouseId}/stock`, { params });
  }

  // Reorder alerts
  getReorderAlerts(): Observable<ApiResponse<ReorderAlertModel[]>> {
    return this.http.get<ApiResponse<ReorderAlertModel[]>>(`${this.base}/inventory-items/reorder-alerts`);
  }

  // Stock adjustments
  getAdjustments(filter: AdjustmentListFilter = {}): Observable<ApiResponse<PaginatedResponse<StockAdjustmentModel>>> {
    let params = new HttpParams();
    if (filter.variantId)   params = params.set('variantId',   String(filter.variantId));
    if (filter.warehouseId) params = params.set('warehouseId', String(filter.warehouseId));
    if (filter.status)      params = params.set('status',      filter.status);
    if (filter.adjType)     params = params.set('adjType',     filter.adjType);
    params = params.set('page',     String(filter.page     ?? 1));
    params = params.set('pageSize', String(filter.pageSize ?? 20));
    return this.http.get<ApiResponse<PaginatedResponse<StockAdjustmentModel>>>(`${this.base}/stock-adjustments`, { params });
  }
  createAdjustment(data: CreateAdjustmentRequest): Observable<ApiResponse<StockAdjustmentResult>> {
    return this.http.post<ApiResponse<StockAdjustmentResult>>(`${this.base}/stock-adjustments`, data);
  }
  approveAdjustment(uuid: string): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${this.base}/stock-adjustments/${uuid}/approve`, {});
  }
  rejectAdjustment(uuid: string, data: RejectAdjustmentRequest): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${this.base}/stock-adjustments/${uuid}/reject`, data);
  }

  // Dynamic Attributes
  getAttributes(): Observable<ApiResponse<AttributeDefinitionModel[]>> {
    return this.http.get<ApiResponse<AttributeDefinitionModel[]>>(`${this.base}/attributes`);
  }
  createAttribute(data: CreateAttributeDefinitionRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${this.base}/attributes`, data);
  }
  updateAttribute(uuid: string, data: UpdateAttributeDefinitionRequest): Observable<ApiResponse> {
    return this.http.put<ApiResponse>(`${this.base}/attributes/${uuid}`, data);
  }
  deleteAttribute(uuid: string): Observable<ApiResponse<AttributeDeleteConflictResult>> {
    return this.http.delete<ApiResponse<AttributeDeleteConflictResult>>(`${this.base}/attributes/${uuid}`);
  }
  getCategoryAttributes(categoryId: number): Observable<ApiResponse<CategoryAttributeModel[]>> {
    return this.http.get<ApiResponse<CategoryAttributeModel[]>>(`${this.base}/categories/${categoryId}/attributes`);
  }
  linkCategoryAttribute(categoryId: number, data: CreateCategoryAttributeRequest): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${this.base}/categories/${categoryId}/attributes`, data);
  }
  unlinkCategoryAttribute(categoryId: number, attributeUuid: string): Observable<ApiResponse> {
    return this.http.delete<ApiResponse>(`${this.base}/categories/${categoryId}/attributes/${attributeUuid}`);
  }
  setCategoryAttributes(categoryId: number, data: SetCategoryAttributesRequest): Observable<ApiResponse<CategoryAttributeModel[]>> {
    return this.http.put<ApiResponse<CategoryAttributeModel[]>>(`${this.base}/categories/${categoryId}/attributes`, data);
  }
  getVariantAttributeValues(variantUuid: string): Observable<ApiResponse<VariantAttributeValueModel[]>> {
    return this.http.get<ApiResponse<VariantAttributeValueModel[]>>(`${this.base}/variants/${variantUuid}/attributes`);
  }
  setVariantAttributeValues(variantUuid: string, data: SetVariantAttributeValuesRequest): Observable<ApiResponse> {
    return this.http.put<ApiResponse>(`${this.base}/variants/${variantUuid}/attributes`, data);
  }
}
