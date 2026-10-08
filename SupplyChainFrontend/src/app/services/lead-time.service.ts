import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { ApiResponse } from './logistics.service';

// A34 C3/C4 — lead times (Inventory, MODULE_INVENTORY). Contract: docs/route-classification/API-CONTRACT.md §4.4–§4.6.
// Reads (defaults, variant lead times) need any of LEAD_TIME_DEFAULTS_MANAGE, INVENTORY_VIEW, STOCK_MANAGE,
// SALE_ORDER_VIEW. PUT defaults needs LEAD_TIME_DEFAULTS_MANAGE; PUT variant lead times needs STOCK_MANAGE.
// calculate / calculate-manufacturing need any of SALE_ORDER_VIEW/CREATE/EDIT, SALE_INQUIRY_VIEW/EDIT,
// SALE_QUOTATION_VIEW/EDIT, INVENTORY_VIEW, STOCK_MANAGE.

export type LeadTimeComponentCode =
  'SUPPLIER' | 'MANUFACTURING' | 'MFG_BUFFER' | 'QC' | 'TRANSFER' | 'PICK_PACK' | 'SHIPPING' | 'SALES_BUFFER';

/** In display order (contract §1). */
export const LEAD_TIME_COMPONENT_CODES: readonly LeadTimeComponentCode[] =
  ['SUPPLIER', 'MANUFACTURING', 'MFG_BUFFER', 'QC', 'TRANSFER', 'PICK_PACK', 'SHIPPING', 'SALES_BUFFER'];

export type LeadTimeSource =
  'VARIANT' | 'ORG_DEFAULT' | 'SUPPLIER_RATE' | 'SUPPLIER_RECORD' | 'PRODUCT' | 'BOM' | 'IN_STOCK' | 'SYSTEM_DEFAULT';

const SOURCE_LABELS: Record<LeadTimeSource, string> = {
  VARIANT: 'Variant override',
  ORG_DEFAULT: 'Org default',
  SUPPLIER_RATE: 'Supplier',
  SUPPLIER_RECORD: 'Supplier',
  PRODUCT: 'Product',
  BOM: 'BOM',
  IN_STOCK: 'In stock',
  SYSTEM_DEFAULT: 'System default'
};

/** The source badge's text (contract §4.5); an unknown code is shown as it is. */
export function leadTimeSourceLabel(source: string | null | undefined): string {
  return SOURCE_LABELS[source as LeadTimeSource] ?? source ?? '';
}

/** Badge colour: the variant's own override stands out; "in stock" is good news; everything inherited is grey. */
export function leadTimeSourceSeverity(source: string | null | undefined): 'info' | 'success' | 'warn' | 'secondary' {
  switch (source) {
    case 'VARIANT':  return 'info';
    case 'IN_STOCK': return 'success';
    case 'BOM':      return 'warn';
    default:         return 'secondary';
  }
}

// ── §4.4 Organization defaults ──────────────────────────────────────────────────────────────────────────────

export interface LeadTimeDefaultsValues {
  pickPackDays: number;
  shippingLeadTimeDays: number;
  salesBufferDays: number;
  manufacturingBufferDays: number;
  qualityInspectionDays: number;
  internalTransferDays: number;
}

export interface LeadTimeDefaultsModel extends LeadTimeDefaultsValues {
  /** False = the organization has no row yet; the values are the system defaults (D-10). */
  isSaved: boolean;
  modifiedBy?: number | null;
  /** UTC. */
  modifiedDate?: string | null;
}

/** All six required, 0–365 (BR-C3-01). */
export type UpdateLeadTimeDefaultsRequest = LeadTimeDefaultsValues;

/** What an organization with no saved row gets (D-10). */
export const SYSTEM_LEAD_TIME_DEFAULTS: Readonly<LeadTimeDefaultsValues> = {
  pickPackDays: 1, shippingLeadTimeDays: 3, salesBufferDays: 1,
  manufacturingBufferDays: 0, qualityInspectionDays: 0, internalTransferDays: 0
};

export const MAX_DEFAULT_LEAD_DAYS = 365;
export const MAX_VARIANT_LEAD_DAYS = 3650;

/** The settings page's six fields, in the order of spec §11.5. */
export const LEAD_TIME_DEFAULT_FIELDS: readonly { field: keyof LeadTimeDefaultsValues; label: string; hint: string }[] = [
  { field: 'pickPackDays',            label: 'Pick & Pack Time',        hint: 'Warehouse time to pick and pack an order.' },
  { field: 'shippingLeadTimeDays',    label: 'Shipping Lead Time',      hint: 'Transit time to the customer. Counted only on routes with a SHIP step.' },
  { field: 'salesBufferDays',         label: 'Sales Safety Buffer',     hint: 'Extra days added to every promised date for the unexpected.' },
  { field: 'manufacturingBufferDays', label: 'Manufacturing Buffer',    hint: 'Safety days for production delays. Counted only on make-to-order routes.' },
  { field: 'qualityInspectionDays',   label: 'Quality Inspection Time', hint: 'Time for incoming or post-production inspection.' },
  { field: 'internalTransferDays',    label: 'Internal Transfer Time',  hint: 'Time to move goods between your own locations.' }
];

// ── §4.5 Variant lead times ─────────────────────────────────────────────────────────────────────────────────

export interface VariantLeadTimeComponentModel {
  code: LeadTimeComponentCode | string;
  name: string;
  /** The request property: supplierLeadTimeDays, manufacturingLeadTimeDays, … */
  field: keyof UpdateVariantLeadTimesRequest | string;
  /** The variant's override; null = not set. */
  storedDays?: number | null;
  /** What the calculator would use (stock-unaware). */
  resolvedDays: number;
  source: LeadTimeSource | string;
  /** v1.2: the value and source a cleared override falls back to (never VARIANT). */
  defaultDays?: number | null;
  defaultSource?: LeadTimeSource | string | null;
  /** e.g. "ACME Ltd (preferred)". */
  detail?: string | null;
  /** §5.6 / BR-C3-04/05: MANUFACTURING and MFG_BUFFER only on MANUFACTURE routes; SHIPPING only with SHIP. */
  visible: boolean;
  /** Counted in totalDays: visible, except SUPPLIER on a MANUFACTURE route (shown, not counted). Absent = visible. */
  includedInTotal?: boolean | null;
}

export interface VariantLeadTimesModel {
  variantUuid: string;
  productId: number;
  sku?: string | null;
  variantName?: string | null;
  routeUuid?: string | null;
  routeCode?: string | null;
  routeName?: string | null;
  /** The variant's route, else the org's SHIP default, else STOCK. */
  routeCategory: 'STOCK' | 'MANUFACTURE';
  /** The variant has no route of its own: the visibility is judged on the org's SHIP default. */
  routeFromOrgDefault: boolean;
  requiresShipping: boolean;
  /** Always all eight, in LEAD_TIME_COMPONENT_CODES order. */
  components: VariantLeadTimeComponentModel[];
  /** Sum of resolvedDays over the visible components (no stock, no BOM recursion). */
  totalDays: number;
}

/** Replaces all eight; null = use the default ("Reset to org defaults" = all null). Each 0–3650. */
export interface UpdateVariantLeadTimesRequest {
  supplierLeadTimeDays?: number | null;
  manufacturingLeadTimeDays?: number | null;
  manufacturingBufferDays?: number | null;
  qualityInspectionDays?: number | null;
  internalTransferDays?: number | null;
  pickPackDays?: number | null;
  shippingLeadTimeDays?: number | null;
  salesBufferDays?: number | null;
}

export const VARIANT_LEAD_TIME_FIELDS: readonly (keyof UpdateVariantLeadTimesRequest)[] = [
  'supplierLeadTimeDays', 'manufacturingLeadTimeDays', 'manufacturingBufferDays', 'qualityInspectionDays',
  'internalTransferDays', 'pickPackDays', 'shippingLeadTimeDays', 'salesBufferDays'
];

// ── §4.6 Calculator ─────────────────────────────────────────────────────────────────────────────────────────

export interface LeadTimeCalculateRequest {
  variantUuid: string;
  quantity: number;
  /** Null = the variant's route, else the org's SHIP default. */
  routeUuid?: string | null;
  /** Date-only "yyyy-MM-dd". */
  requestedDate?: string | null;
}

export interface LeadTimeComponentResult {
  code: string;
  name: string;
  days: number;
  source: string;
  detail?: string | null;
}

export interface LeadTimeResultModel {
  totalLeadTimeDays: number;
  /** Date-only: UTC today + total. */
  earliestDeliveryDate: string;
  /** Date-only; only with requestedDate. */
  latestStartDate?: string | null;
  meetsRequestedDate?: boolean | null;
  routeUuid?: string | null;
  routeCode?: string | null;
  routeCategory: 'STOCK' | 'MANUFACTURE';
  components: LeadTimeComponentResult[];
  /** UTC. */
  calculatedAt: string;
}

export interface ManufacturingLeadTimeCalculateRequest {
  variantUuid: string;
  quantity: number;
}

export interface ManufacturingLeadTimeInputModel {
  variantUuid: string;
  displayName: string;
  requiredQty: number;
  /** Free stock in the parent's production warehouse. */
  freeQty: number;
  shortfallQty: number;
  isManufactured: boolean;
  /** 0 when shortfallQty is 0. */
  waitDays: number;
  /** IN_STOCK | BOM (recursed) | SUPPLIER_* | PRODUCT | SYSTEM_DEFAULT. */
  source: string;
  detail?: string | null;
  /** Only when recursed. */
  node?: ManufacturingLeadTimeNodeModel | null;
}

/** POST api/lead-time/calculate-manufacturing — one BOM level; inputs recurse (D-12/D-13). */
export interface ManufacturingLeadTimeNodeModel {
  variantUuid: string;
  displayName: string;
  quantity: number;
  levelDays: number;
  levelDaysSource: 'VARIANT' | 'PRODUCT' | 'SYSTEM_DEFAULT' | string;
  /** Null: no active BOM (levelDays only, with a warning). */
  bomUuid?: string | null;
  bomNumber?: string | null;
  bomVersion?: number | null;
  /** levelDays + max(input waitDays). */
  totalDays: number;
  inputs: ManufacturingLeadTimeInputModel[];
  warnings: string[];
}

@Injectable({ providedIn: 'root' })
export class LeadTimeService {
  private readonly base = environment.apiUrl;

  constructor(private http: HttpClient) {}

  /** The caller's own organization; never 404 (a missing row comes back with isSaved: false). */
  getDefaults(): Observable<ApiResponse<LeadTimeDefaultsModel>> {
    return this.http.get<ApiResponse<LeadTimeDefaultsModel>>(`${this.base}/lead-time/defaults`);
  }

  /** Upsert. LEAD_TIME_DEFAULTS_MANAGE. */
  updateDefaults(req: UpdateLeadTimeDefaultsRequest): Observable<ApiResponse<LeadTimeDefaultsModel>> {
    return this.http.put<ApiResponse<LeadTimeDefaultsModel>>(`${this.base}/lead-time/defaults`, req);
  }

  getVariantLeadTimes(variantUuid: string): Observable<ApiResponse<VariantLeadTimesModel>> {
    return this.http.get<ApiResponse<VariantLeadTimesModel>>(`${this.base}/variants/${variantUuid}/lead-times`);
  }

  /** Replaces all eight overrides. STOCK_MANAGE. */
  updateVariantLeadTimes(variantUuid: string, req: UpdateVariantLeadTimesRequest): Observable<ApiResponse<VariantLeadTimesModel>> {
    return this.http.put<ApiResponse<VariantLeadTimesModel>>(`${this.base}/variants/${variantUuid}/lead-times`, req);
  }

  /** The full breakdown for one line (unsaved forms; saved lines use the document's own …/lead-time endpoint). */
  calculate(req: LeadTimeCalculateRequest): Observable<ApiResponse<LeadTimeResultModel>> {
    return this.http.post<ApiResponse<LeadTimeResultModel>>(`${this.base}/lead-time/calculate`, req);
  }

  /** The BOM-aware manufacturing tree. 400 when the product is not MANUFACTURE. Never writes anything. */
  calculateManufacturing(req: ManufacturingLeadTimeCalculateRequest): Observable<ApiResponse<ManufacturingLeadTimeNodeModel>> {
    return this.http.post<ApiResponse<ManufacturingLeadTimeNodeModel>>(`${this.base}/lead-time/calculate-manufacturing`, req);
  }
}
