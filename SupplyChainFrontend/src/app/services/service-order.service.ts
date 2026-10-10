import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ApiResponse, PaginatedResponse } from './inventory.service';
import { environment } from '../../environments/environment';

// A36 — service orders (docs/service-orders/API-CONTRACT.md §3). Feature MODULE_SERVICES.

export type ServiceOrderStatus =
  'DRAFT' | 'PLANNED' | 'MATERIAL_PENDING' | 'WAITING' | 'READY' | 'IN_PROGRESS' | 'COMPLETED' | 'CLOSED' | 'CANCELLED';

export type ServiceMaterialReadiness = 'NOT_CHECKED' | 'PARTIAL' | 'READY' | 'SHORTAGE' | 'NOT_APPLICABLE';

export type ServiceOrderAction = 'EDIT' | 'PLAN' | 'START' | 'ADD_MATERIAL' | 'COMPLETE' | 'CLOSE' | 'CANCEL';

export type ServiceInvoicingPolicy = 'FIXED_PRICE' | 'COST_PLUS' | 'TIME_AND_MATERIAL';
export type ServiceBillingModel = 'INCLUSIVE' | 'PASS_THROUGH';
export type ServiceMaterialSourceType = 'STOCK' | 'SUBCONTRACT' | 'INTERNAL_LABOR';

export const SERVICE_ORDER_STATUSES: { value: ServiceOrderStatus; label: string }[] = [
  { value: 'DRAFT',            label: 'Draft' },
  { value: 'PLANNED',          label: 'Planned' },
  { value: 'MATERIAL_PENDING', label: 'Material pending' },
  { value: 'WAITING',          label: 'Waiting' },
  { value: 'READY',            label: 'Ready' },
  { value: 'IN_PROGRESS',      label: 'In progress' },
  { value: 'COMPLETED',        label: 'Completed' },
  { value: 'CLOSED',           label: 'Closed' },
  { value: 'CANCELLED',        label: 'Cancelled' }
];

/** sf-pill tone per status: draft grey, planned blue, material pending amber, waiting orange, ready green,
 *  in progress teal, completed ok, closed grey, cancelled red. The kit has no orange token, so WAITING shares
 *  the warn tone with MATERIAL_PENDING (its icon tells them apart). */
const STATUS_TONE: Record<string, string> = {
  DRAFT: '', PLANNED: 'in', MATERIAL_PENDING: 'wn', WAITING: 'wn', READY: 'ok',
  IN_PROGRESS: 'te', COMPLETED: 'ok', CLOSED: '', CANCELLED: 'er'
};

export function serviceStatusTone(status: string | null | undefined): string {
  return STATUS_TONE[status ?? ''] ?? '';
}

export function serviceStatusLabel(status: string | null | undefined): string {
  return SERVICE_ORDER_STATUSES.find(s => s.value === status)?.label ?? (status ?? '');
}

/** Readiness icon + tone for lists and the header. */
export function readinessIcon(readiness: string | null | undefined): { icon: string; tone: string; color: string; label: string } {
  switch (readiness) {
    case 'READY':          return { icon: 'pi-check-circle',       tone: 'ok', color: 'var(--sms-ok)',         label: 'Materials ready' };
    case 'PARTIAL':        return { icon: 'pi-exclamation-circle', tone: 'wn', color: 'var(--sms-warn)',       label: 'Materials partially available' };
    case 'SHORTAGE':       return { icon: 'pi-times-circle',       tone: 'er', color: 'var(--sms-danger)',     label: 'Materials short' };
    case 'NOT_APPLICABLE': return { icon: 'pi-minus-circle',       tone: '',   color: 'var(--sms-text-faint)', label: 'No materials needed' };
    default:               return { icon: 'pi-question-circle',    tone: '',   color: 'var(--sms-text-faint)', label: 'Not checked yet' };
  }
}

export const SERVICE_PRIORITY_OPTIONS = [
  { value: 0, label: 'Low' },
  { value: 1, label: 'Normal' },
  { value: 2, label: 'High' },
  { value: 3, label: 'Urgent' }
];

export function servicePriorityLabel(priority: number | null | undefined): string {
  return SERVICE_PRIORITY_OPTIONS.find(p => p.value === priority)?.label ?? String(priority ?? '');
}

// ── Requests ─────────────────────────────────────────────────────────────────

export interface CreateServiceOrderRequest {
  serviceProductUuid: string;
  serviceVariantUuid?: string;
  customerUuid: string;
  quantity: number;
  warehouseUuid: string;
  assignedUserId?: number;
  assignedRoleId?: number;
  /** yyyy-MM-dd */
  scheduledDate?: string;
  /** HH:mm */
  scheduledTime?: string;
  estimatedHours?: number;
  priority?: number;
  notes?: string;
}

/** DRAFT/PLANNED: every field; later only notes is taken (SVC-12). */
export interface UpdateServiceOrderRequest {
  customerUuid: string;
  quantity: number;
  warehouseUuid: string;
  assignedUserId?: number | null;
  assignedRoleId?: number | null;
  scheduledDate?: string | null;
  scheduledTime?: string | null;
  estimatedHours?: number | null;
  priority: number;
  notes?: string | null;
  /** base64 */
  rowVersion: string;
}

export interface ConsumedMaterialRequest { smrUuid: string; consumedQuantity: number; }

export interface CompleteServiceOrderRequest {
  consumedMaterials: ConsumedMaterialRequest[];
  actualHours?: number;
  completionNotes?: string;
  customerSignature: boolean;
}

export interface AddAdhocMaterialRequest { variantUuid: string; quantity: number; notes?: string; }

export interface CancelServiceOrderRequest { reason: string; }

export interface ServiceOrderListFilter {
  /** One or more statuses (sent comma-separated). */
  status?: string[];
  customerUuid?: string;
  assignedUserId?: number;
  /** yyyy-MM-dd */
  fromDate?: string;
  toDate?: string;
  priority?: number;
  search?: string;
  page?: number;
  pageSize?: number;
}

// ── Results ──────────────────────────────────────────────────────────────────

export interface ServiceOrderListItem {
  uuid: string;
  serviceNumber: string;
  serviceProductName: string;
  serviceVariantName?: string | null;
  customerUuid: string;
  customerName: string;
  scheduledDate?: string | null;
  scheduledTime?: string | null;
  assignedUserId?: number | null;
  assignedUserName?: string | null;
  status: ServiceOrderStatus;
  priority: number;
  materialReadiness: ServiceMaterialReadiness;
  quantity: number;
}

export interface ServiceMaterial {
  uuid: string;
  productUuid: string;
  variantUuid: string;
  productName: string;
  variantName?: string | null;
  sku?: string | null;
  sourceType: ServiceMaterialSourceType;
  requiredQuantity: number;
  netQuantity: number;
  scrapAllowance: number;
  reservedQuantity: number;
  issuedQuantity: number;
  consumedQuantity: number;
  returnedQuantity: number;
  shortageQuantity: number;
  /** Free stock in the warehouse now. */
  availableQuantity: number;
  uom: string;
  isCritical: boolean;
  isAdhoc: boolean;
  /** PENDING PARTIALLY_RESERVED FULLY_RESERVED ISSUED CONSUMED RETURNED CANCELLED */
  status: string;
  requiredDate: string;
  addedByName?: string | null;
  notes?: string | null;
  supplyRequirementNumber?: string | null;
  supplyRequirementStatus?: string | null;
  canRemove: boolean;
}

export interface ServiceLedgerEntry {
  id: number;
  entryType: 'DEBIT';
  productUuid: string;
  variantUuid: string;
  productName: string;
  /** Negative = returned. */
  quantity: number;
  uom: string;
  warehouseName: string;
  sourceDocumentType: 'SERVICE_ISSUE' | 'SERVICE_RETURN';
  sourceDocumentNumber: string;
  movementType: 'SERVICE_ISSUE' | 'SERVICE_RETURN';
  transactionDate: string;
  notes?: string | null;
}

export interface ServiceLedgerNet { variantUuid: string; productName: string; uom: string; netQuantity: number; }

export interface ServiceLedger { entries: ServiceLedgerEntry[]; netByProduct: ServiceLedgerNet[]; }

export interface ServiceOrderDetail extends ServiceOrderListItem {
  serviceProductUuid: string;
  serviceVariantUuid: string;
  warehouseUuid: string;
  warehouseName: string;
  assignedRoleId?: number | null;
  assignedRoleName?: string | null;
  bomId?: number | null;
  bomNumber?: string | null;
  bomVersion?: number | null;
  estimatedHours?: number | null;
  actualHours?: number | null;
  actualStartDate?: string | null;
  actualEndDate?: string | null;
  sourceType: 'MANUAL' | 'SALES_ORDER';
  sourceUuid?: string | null;
  sourceLineUuid?: string | null;
  /** The SO number. */
  sourceReference?: string | null;
  invoicingPolicy?: ServiceInvoicingPolicy | null;
  billingModel?: ServiceBillingModel | null;
  completionNotes?: string | null;
  customerSignature: boolean;
  notes?: string | null;
  traceId: string;
  rowVersion: string;
  createdAt: string;
  updatedAt: string;
  materials: ServiceMaterial[];
  ledger: ServiceLedgerEntry[];
  allowedActions: ServiceOrderAction[];
}

export interface ServiceDashboard {
  today: ServiceOrderListItem[];
  waitingForMaterials: ServiceOrderListItem[];
  mine: ServiceOrderListItem[];
  completionRate: { completedThisWeek: number; scheduledThisWeek: number; percent: number };
}

@Injectable({ providedIn: 'root' })
export class ServiceOrderService {
  private readonly base = `${environment.apiUrl}/service-orders`;

  constructor(private http: HttpClient) {}

  /** #1 — returns the new order's uuid. */
  create(req: CreateServiceOrderRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(this.base, req);
  }

  /** #2 — sorted scheduled date asc, priority desc. */
  getList(filter: ServiceOrderListFilter = {}): Observable<ApiResponse<PaginatedResponse<ServiceOrderListItem>>> {
    let params = new HttpParams();
    if (filter.status?.length)  params = params.set('status', filter.status.join(','));
    if (filter.customerUuid)    params = params.set('customerUuid', filter.customerUuid);
    if (filter.assignedUserId != null) params = params.set('assignedUserId', String(filter.assignedUserId));
    if (filter.fromDate)        params = params.set('fromDate', filter.fromDate);
    if (filter.toDate)          params = params.set('toDate', filter.toDate);
    if (filter.priority != null) params = params.set('priority', String(filter.priority));
    if (filter.search)          params = params.set('search', filter.search);
    params = params.set('page', String(filter.page ?? 1));
    params = params.set('pageSize', String(filter.pageSize ?? 25));
    return this.http.get<ApiResponse<PaginatedResponse<ServiceOrderListItem>>>(this.base, { params });
  }

  /** #3 */
  getById(uuid: string): Observable<ApiResponse<ServiceOrderDetail>> {
    return this.http.get<ApiResponse<ServiceOrderDetail>>(`${this.base}/${uuid}`);
  }

  /** #4 */
  update(uuid: string, req: UpdateServiceOrderRequest): Observable<ApiResponse<ServiceOrderDetail>> {
    return this.http.put<ApiResponse<ServiceOrderDetail>>(`${this.base}/${uuid}`, req);
  }

  /** #5 */
  plan(uuid: string): Observable<ApiResponse<ServiceOrderDetail>> {
    return this.http.post<ApiResponse<ServiceOrderDetail>>(`${this.base}/${uuid}/plan`, {});
  }

  /** #6 */
  start(uuid: string): Observable<ApiResponse<ServiceOrderDetail>> {
    return this.http.post<ApiResponse<ServiceOrderDetail>>(`${this.base}/${uuid}/start`, {});
  }

  /** #7 */
  complete(uuid: string, req: CompleteServiceOrderRequest): Observable<ApiResponse<ServiceOrderDetail>> {
    return this.http.post<ApiResponse<ServiceOrderDetail>>(`${this.base}/${uuid}/complete`, req);
  }

  /** #8 */
  cancel(uuid: string, req: CancelServiceOrderRequest): Observable<ApiResponse<ServiceOrderDetail>> {
    return this.http.post<ApiResponse<ServiceOrderDetail>>(`${this.base}/${uuid}/cancel`, req);
  }

  /** #8a */
  close(uuid: string): Observable<ApiResponse<ServiceOrderDetail>> {
    return this.http.post<ApiResponse<ServiceOrderDetail>>(`${this.base}/${uuid}/close`, {});
  }

  /** #9 */
  getMaterials(uuid: string): Observable<ApiResponse<ServiceMaterial[]>> {
    return this.http.get<ApiResponse<ServiceMaterial[]>>(`${this.base}/${uuid}/materials`);
  }

  /** #10 — ad-hoc material (IN_PROGRESS / WAITING). */
  addMaterial(uuid: string, req: AddAdhocMaterialRequest): Observable<ApiResponse<ServiceOrderDetail>> {
    return this.http.post<ApiResponse<ServiceOrderDetail>>(`${this.base}/${uuid}/materials`, req);
  }

  /** #11 */
  removeMaterial(uuid: string, smrUuid: string): Observable<ApiResponse<ServiceOrderDetail>> {
    return this.http.delete<ApiResponse<ServiceOrderDetail>>(`${this.base}/${uuid}/materials/${smrUuid}`);
  }

  /** #11a — "Reserve all available". */
  allocate(uuid: string): Observable<ApiResponse<ServiceOrderDetail>> {
    return this.http.post<ApiResponse<ServiceOrderDetail>>(`${this.base}/${uuid}/materials/allocate`, {});
  }

  /** #12 */
  getLedger(uuid: string): Observable<ApiResponse<ServiceLedger>> {
    return this.http.get<ApiResponse<ServiceLedger>>(`${this.base}/${uuid}/ledger`);
  }

  /** #13 */
  getDashboard(): Observable<ApiResponse<ServiceDashboard>> {
    return this.http.get<ApiResponse<ServiceDashboard>>(`${this.base}/dashboard`);
  }
}
