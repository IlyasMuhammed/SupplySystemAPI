import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { ApiResponse } from './logistics.service';

// A33 C1 — fulfillment routes (/api/fulfillment-routes, Logistics). Contract: docs/fulfillment-routes/API-CONTRACT.md §3.
// Reading needs any of FULFILLMENT_ROUTE_VIEW / _MANAGE / _ASSIGN, SALE_ORDER_VIEW, INVENTORY_VIEW, DELIVERY_VIEW;
// every change needs FULFILLMENT_ROUTE_MANAGE; assign-by-category needs FULFILLMENT_ROUTE_ASSIGN.

/** The step codes, in the only order a route may list them (L-2). PICK and GOODS_ISSUE are always required. */
export type FulfillmentStepCode = 'PICK' | 'PACK' | 'STAGE' | 'APPROVAL' | 'GOODS_ISSUE' | 'SHIP';

export const FULFILLMENT_STEP_CODES: readonly FulfillmentStepCode[] = ['PICK', 'PACK', 'STAGE', 'APPROVAL', 'GOODS_ISSUE', 'SHIP'];
export const REQUIRED_FULFILLMENT_STEPS: readonly FulfillmentStepCode[] = ['PICK', 'GOODS_ISSUE'];

export const FULFILLMENT_STEP_LABELS: Record<FulfillmentStepCode, string> = {
  PICK: 'Pick',
  PACK: 'Pack',
  STAGE: 'Stage',
  APPROVAL: 'Approval',
  GOODS_ISSUE: 'Goods Issue',
  SHIP: 'Ship'
};

// ── A34 C1 — route category (docs/route-classification/API-CONTRACT.md) ─────────────────────────────────────

/**
 * What a route is for (BR-C1-01). STOCK ships from stock (A33 behaviour); MANUFACTURE is make-to-order (D-1). BUY and
 * DROPSHIP are reserved: the server refuses them (D-7), the editor shows them disabled.
 */
export type FulfillmentRouteCategory = 'STOCK' | 'MANUFACTURE' | 'BUY' | 'DROPSHIP';

export const ROUTE_CATEGORIES: readonly FulfillmentRouteCategory[] = ['STOCK', 'MANUFACTURE', 'BUY', 'DROPSHIP'];
/** The categories a route can be created with or changed to today. */
export const ACTIVE_ROUTE_CATEGORIES: readonly FulfillmentRouteCategory[] = ['STOCK', 'MANUFACTURE'];

const ROUTE_CATEGORY_LABELS: Record<FulfillmentRouteCategory, string> = {
  STOCK: 'Stock', MANUFACTURE: 'Manufacture', BUY: 'Buy', DROPSHIP: 'Drop ship'
};

export const RESERVED_CATEGORY_TOOLTIP = 'Reserved for future release';

/** Spec §3.6 — shown in the editor while MANUFACTURE is chosen. */
export const MANUFACTURE_ROUTE_NOTE = 'Products with this route will trigger a Production Order at Sale Order confirmation. ' +
  'Delivery is created after production completes.';

export interface RouteCategoryOption {
  label: string;
  value: FulfillmentRouteCategory;
  disabled: boolean;
  tooltip?: string;
}

/** A route saved before A34 (or a model without the field) is STOCK, the column's default. */
export function routeCategoryOf(route: { routeCategory?: string | null } | null | undefined): FulfillmentRouteCategory {
  return (route?.routeCategory as FulfillmentRouteCategory | null | undefined) ?? 'STOCK';
}

export function routeCategoryLabel(category: string | null | undefined): string {
  return ROUTE_CATEGORY_LABELS[(category ?? 'STOCK') as FulfillmentRouteCategory] ?? category ?? '';
}

/** The badge colour: STOCK green, MANUFACTURE orange, anything else grey. */
export function routeCategorySeverity(category: string | null | undefined): 'success' | 'warn' | 'secondary' {
  switch (category ?? 'STOCK') {
    case 'STOCK':       return 'success';
    case 'MANUFACTURE': return 'warn';
    default:            return 'secondary';
  }
}

/**
 * The editor's category dropdown: STOCK and MANUFACTURE, then BUY and DROPSHIP disabled (D-7). Without
 * MODULE_MANUFACTURING, MANUFACTURE is left out (D-9) unless the route already has it.
 */
export function routeCategoryOptions(manufacturingEnabled: boolean, current?: string | null): RouteCategoryOption[] {
  return ROUTE_CATEGORIES
    .filter(c => c !== 'MANUFACTURE' || manufacturingEnabled || current === 'MANUFACTURE')
    .map(c => ACTIVE_ROUTE_CATEGORIES.includes(c)
      ? { label: ROUTE_CATEGORY_LABELS[c], value: c, disabled: false }
      : { label: ROUTE_CATEGORY_LABELS[c], value: c, disabled: true, tooltip: RESERVED_CATEGORY_TOOLTIP });
}

/**
 * D-9 — MANUFACTURE routes are hidden from an organization without MODULE_MANUFACTURING. A37 RTE-01: so is any route
 * the server marks unavailable (pickers list those separately, disabled — see unavailableRouteOptions).
 */
export function routesVisibleToOrg<T extends { routeCategory?: string | null; isAvailable?: boolean | null }>(
  routes: readonly T[], manufacturingEnabled: boolean): T[] {
  return routes.filter(r => isRouteAvailable(r) && (manufacturingEnabled || routeCategoryOf(r) !== 'MANUFACTURE'));
}

// ── A37 D-12 — module-aware routes (docs/module-registry/API-CONTRACT.md §4) ───────────────────────────────

/** Absent on older servers = available. */
export function isRouteAvailable(route: { isAvailable?: boolean | null } | null | undefined): boolean {
  return route?.isAvailable !== false;
}

/** The server's reason, or a generic one. */
export function routeUnavailableReason(route: { unavailableReason?: string | null } | null | undefined): string {
  return route?.unavailableReason || 'Not available';
}

/** A picker option for a route that cannot be chosen: shown, disabled, with the reason in the label. */
export interface UnavailableRouteOption {
  label: string;
  value: string;
  disabled: true;
  reason: string;
}

/** A37 — the routes the server marks unavailable, as disabled picker options ("Name — reason"), minus `except`. */
export function unavailableRouteOptions(
  routes: readonly FulfillmentRouteModel[], except: readonly string[] = [], withCode = false): UnavailableRouteOption[] {
  return routes
    .filter(r => !isRouteAvailable(r) && !except.includes(r.uuid))
    .map(r => {
      const reason = routeUnavailableReason(r);
      const name = withCode ? `${r.name} (${r.code})` : r.name;
      return { label: `${name} — ${reason}`, value: r.uuid, disabled: true as const, reason };
    });
}

/** GET /api/products/{productUuid}/routes — one row per variant (FULFILLMENT_ROUTE_VIEW). */
export interface ProductVariantRouteModel {
  variantUuid: string;
  variantName: string;
  sku: string;
  /** The variant's own route; null = none (the organization default applies). */
  routeUuid?: string | null;
  routeName?: string | null;
  category?: FulfillmentRouteCategory | null;
  /** The configured route is usable (true with no route of its own). */
  isAvailable: boolean;
  effectiveRouteUuid?: string | null;
  effectiveRouteName?: string | null;
  /** e.g. the configured route is unavailable and the organization default STOCK route applies (RTE-02/03). */
  warning?: string | null;
}

/**
 * D-3 / D-9 — the routes a variant may be given: MANUFACTURE routes only when the product's supply method is
 * MANUFACTURE and the organization has MODULE_MANUFACTURING (the server refuses the rest with 400).
 */
export function routesForVariant<T extends { routeCategory?: string | null }>(
  routes: readonly T[], productManufactured: boolean, manufacturingEnabled: boolean): T[] {
  return routes.filter(r => routeCategoryOf(r) !== 'MANUFACTURE' || (productManufactured && manufacturingEnabled));
}

/** Where a sale order line's route came from (BR-C3-03). */
export type FulfillmentRouteSource = 'LINE_OVERRIDE' | 'VARIANT' | 'ORG_DEFAULT' | 'NONE';

export const ROUTE_SOURCE_LABELS: Record<FulfillmentRouteSource, string> = {
  LINE_OVERRIDE: 'Overridden on this line',
  VARIANT: 'Variant default',
  ORG_DEFAULT: 'Organization default',
  NONE: 'No route'
};

export interface FulfillmentRouteStepModel {
  stepCode: FulfillmentStepCode;
  /** "Pick", "Goods Issue", … */
  label: string;
  stepOrder: number;
  isMandatory: boolean;
  description?: string | null;
}

export interface FulfillmentRouteModel {
  uuid: string;
  code: string;
  name: string;
  description?: string | null;
  /**
   * The organization's default for this route's class (L-1): for SHIP orders when requiresShipping, otherwise for
   * SELF_PICKUP orders. At most one of each.
   */
  isDefault: boolean;
  isActive: boolean;
  /** A seeded route: code and steps locked, can't be deleted (name, description and order stay editable). */
  isSystem: boolean;
  requiresPacking: boolean;
  requiresShipping: boolean;
  /** A34 C1. Absent / null reads as STOCK (see routeCategoryOf). */
  routeCategory?: FulfillmentRouteCategory | null;
  /** A37 RTE-01 — false when the route's module is off (MANUFACTURE without Manufacturing). Absent = available. */
  isAvailable?: boolean;
  /** A37 — why not, e.g. "Manufacturing is switched off". */
  unavailableReason?: string | null;
  displayOrder: number;
  /** In step order. */
  steps: FulfillmentRouteStepModel[];
  /** "Pick → Pack → Goods Issue → Ship". */
  stepsText: string;
  /** DRAFT … DELIVERED — the editor's preview line. */
  statusPath: string[];
  createdDate: string;
  modifiedDate?: string | null;
}

export interface FulfillmentRouteStepRequest {
  stepCode: FulfillmentStepCode;
  /** 1, 2, 3 … with no gaps. */
  stepOrder: number;
  /** Forced true for PICK and GOODS_ISSUE. */
  isMandatory: boolean;
  description?: string | null;
}

export interface CreateFulfillmentRouteRequest {
  /** ≤ 30, letters/digits/underscores; upper-cased by the server; never changes afterwards. */
  code: string;
  name: string;
  description?: string | null;
  /** Last + 10 when omitted. */
  displayOrder?: number | null;
  /** A34: STOCK when omitted; BUY / DROPSHIP are refused (D-7). */
  routeCategory?: FulfillmentRouteCategory | null;
  steps: FulfillmentRouteStepRequest[];
}

/** The code never changes. Omit `steps` (or send null) to leave them as they are; a system route's steps are locked. */
export interface UpdateFulfillmentRouteRequest {
  name: string;
  description?: string | null;
  displayOrder: number;
  steps?: FulfillmentRouteStepRequest[] | null;
  /** A34: null / omitted = unchanged. A change on a system, default or in-use route is a 409 (D-8). */
  routeCategory?: FulfillmentRouteCategory | null;
}

/** POST /api/fulfillment-routes/{uuid}/assign-by-category (Inventory) — only variants with no route are set (BR-C2-02). */
export interface AssignRouteByCategoryRequest {
  categoryId: number;
  /** Only this sub-category of the category; all of them when omitted. */
  subCategoryId?: number | null;
}

export interface AssignRouteByCategoryResult {
  updated: number;
  /** Variants that already had a route — left as they were. */
  skipped: number;
  total: number;
}

/** "Pick → Goods Issue → Ship" for any list of step codes. */
export function routeStepsText(steps: readonly string[]): string {
  return steps.map(s => FULFILLMENT_STEP_LABELS[s as FulfillmentStepCode] ?? s).join(' → ');
}

/**
 * The server's step rules (L-2, BR-C1-03/04/05), for the editor to explain a problem before saving. Returns the
 * first problem in words, or null when the steps are valid. The server checks the same rules again.
 */
export function validateRouteSteps(steps: readonly FulfillmentRouteStepRequest[]): string | null {
  if (steps.length === 0) return 'A route needs at least one step.';
  const sorted = [...steps].sort((a, b) => a.stepOrder - b.stepOrder);
  const codes = sorted.map(s => s.stepCode);
  if (codes.some(c => !FULFILLMENT_STEP_CODES.includes(c))) return 'Unknown step.';
  if (new Set(codes).size !== codes.length) return 'A step appears more than once.';
  if (sorted.some((s, i) => s.stepOrder !== i + 1)) return 'Step order must run 1, 2, 3 … with no gaps.';
  if (!codes.includes('PICK')) return 'A route must include the PICK step.';
  if (!codes.includes('GOODS_ISSUE')) return 'A route must include the GOODS_ISSUE step.';
  if (codes[0] !== 'PICK') return 'PICK must be the first step.';
  if (codes.includes('SHIP') && codes.indexOf('SHIP') < codes.indexOf('GOODS_ISSUE')) return 'GOODS_ISSUE must come before SHIP.';
  const ranks = codes.map(c => FULFILLMENT_STEP_CODES.indexOf(c));
  if (ranks.some((r, i) => i > 0 && r < ranks[i - 1])) {
    return `Steps must follow the order ${FULFILLMENT_STEP_CODES.join(', ')}.`;
  }
  return null;
}

@Injectable({ providedIn: 'root' })
export class FulfillmentRoutesService {
  private readonly baseUrl = `${environment.apiUrl}/fulfillment-routes`;

  constructor(private http: HttpClient) {}

  /** Active routes by display order (all of them with includeInactive, for the settings screen); A34: of one category. */
  getRoutes(includeInactive = false, category?: FulfillmentRouteCategory | null): Observable<ApiResponse<FulfillmentRouteModel[]>> {
    let params = new HttpParams();
    if (includeInactive) params = params.set('includeInactive', 'true');
    if (category) params = params.set('category', category);
    return this.http.get<ApiResponse<FulfillmentRouteModel[]>>(this.baseUrl, { params });
  }

  /** A37 D-12 — each variant's configured and effective route, with availability. */
  getProductRoutes(productUuid: string): Observable<ApiResponse<ProductVariantRouteModel[]>> {
    return this.http.get<ApiResponse<ProductVariantRouteModel[]>>(`${environment.apiUrl}/products/${productUuid}/routes`);
  }

  getRoute(uuid: string): Observable<ApiResponse<FulfillmentRouteModel>> {
    return this.http.get<ApiResponse<FulfillmentRouteModel>>(`${this.baseUrl}/${uuid}`);
  }

  createRoute(req: CreateFulfillmentRouteRequest): Observable<ApiResponse<FulfillmentRouteModel>> {
    return this.http.post<ApiResponse<FulfillmentRouteModel>>(this.baseUrl, req);
  }

  updateRoute(uuid: string, req: UpdateFulfillmentRouteRequest): Observable<ApiResponse<FulfillmentRouteModel>> {
    return this.http.put<ApiResponse<FulfillmentRouteModel>>(`${this.baseUrl}/${uuid}`, req);
  }

  /** 409 while the route is a default or still used by active variants / open sale order lines (BR-C1-07). */
  deactivateRoute(uuid: string): Observable<ApiResponse<FulfillmentRouteModel>> {
    return this.http.patch<ApiResponse<FulfillmentRouteModel>>(`${this.baseUrl}/${uuid}/deactivate`, {});
  }

  activateRoute(uuid: string): Observable<ApiResponse<FulfillmentRouteModel>> {
    return this.http.patch<ApiResponse<FulfillmentRouteModel>>(`${this.baseUrl}/${uuid}/activate`, {});
  }

  /** Makes it the default of its class (SHIP / SELF_PICKUP orders); the previous default of that class is cleared. */
  setDefault(uuid: string): Observable<ApiResponse<FulfillmentRouteModel>> {
    return this.http.patch<ApiResponse<FulfillmentRouteModel>>(`${this.baseUrl}/${uuid}/set-default`, {});
  }

  /** Leaves the class with no default — lines with no other route then block confirmation. */
  clearDefault(uuid: string): Observable<ApiResponse<FulfillmentRouteModel>> {
    return this.http.patch<ApiResponse<FulfillmentRouteModel>>(`${this.baseUrl}/${uuid}/clear-default`, {});
  }

  /** A custom route nothing refers to; a system, default or used route is a 409 (deactivate it instead). */
  deleteRoute(uuid: string): Observable<ApiResponse> {
    return this.http.delete<ApiResponse>(`${this.baseUrl}/${uuid}`);
  }

  /** Bulk-assigns the route to every variant in the category that has none yet (FULFILLMENT_ROUTE_ASSIGN). */
  assignByCategory(uuid: string, req: AssignRouteByCategoryRequest): Observable<ApiResponse<AssignRouteByCategoryResult>> {
    return this.http.post<ApiResponse<AssignRouteByCategoryResult>>(`${this.baseUrl}/${uuid}/assign-by-category`, req);
  }
}
