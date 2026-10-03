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
  steps: FulfillmentRouteStepRequest[];
}

/** The code never changes. Omit `steps` (or send null) to leave them as they are; a system route's steps are locked. */
export interface UpdateFulfillmentRouteRequest {
  name: string;
  description?: string | null;
  displayOrder: number;
  steps?: FulfillmentRouteStepRequest[] | null;
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

  /** Active routes by display order (all of them with includeInactive, for the settings screen). */
  getRoutes(includeInactive = false): Observable<ApiResponse<FulfillmentRouteModel[]>> {
    let params = new HttpParams();
    if (includeInactive) params = params.set('includeInactive', 'true');
    return this.http.get<ApiResponse<FulfillmentRouteModel[]>>(this.baseUrl, { params });
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
