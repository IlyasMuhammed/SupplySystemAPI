import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { ApiResponse, PaginatedResponse } from './logistics.service';

// A29 §3 — the organization's sale order policy (/api/sale-order-config, Demand). One row per
// organization; the server creates it with defaults on first read.

export interface SaleOrderConfigModel {
  uuid: string;
  autoPoEnabled: boolean;
  /** DEFAULT_SUPPLIER | BEST_MATCH | MANUAL */
  supplierSelectionMode: string;
  /** REQUIRE_WORKFLOW | AUTO_SEND | DRAFT_ONLY */
  autoPoApprovalMode: string;
  dropShipEnabled: boolean;
  selfPickupEnabled: boolean;
  /** IN_STOCK | BACK_TO_BACK | DROP_SHIP */
  defaultFulfillmentMode: string;
  reservationTtlHours: number;
  partialFulfillmentAllowed: boolean;
  emailIntimationEnabled: boolean;
  intimationDepartmentId?: number | null;
  intimationCcEmails?: string | null;
  shipmentRequiredDefault: boolean;
  updatedBy?: number | null;
  updatedAt?: string | null;
}

/** A save replaces the whole policy, so every setting is sent. */
export type UpdateSaleOrderConfigRequest = Omit<SaleOrderConfigModel, 'uuid' | 'updatedBy' | 'updatedAt'>;

/** One setting that changed. `fieldChanged` is the server's property name, for example "AutoPoEnabled". */
export interface SaleOrderConfigAuditModel {
  id: number;
  fieldChanged: string;
  /** Values are stored as text: "True" and "False" for switches, the id for a department. */
  oldValue?: string | null;
  newValue?: string | null;
  changedBy: number;
  changedByName?: string | null;
  changedAt: string;
}

export interface DepartmentOptionModel {
  departmentId: number;
  name: string;
  code?: string | null;
  /** False when nobody heads the department, so nobody can be notified through it. */
  hasHead: boolean;
}

@Injectable({ providedIn: 'root' })
export class SaleOrderConfigService {
  private readonly baseUrl = `${environment.apiUrl}/sale-order-config`;

  constructor(private http: HttpClient) {}

  getConfig(): Observable<ApiResponse<SaleOrderConfigModel>> {
    return this.http.get<ApiResponse<SaleOrderConfigModel>>(this.baseUrl);
  }

  updateConfig(body: UpdateSaleOrderConfigRequest): Observable<ApiResponse<SaleOrderConfigModel>> {
    return this.http.put<ApiResponse<SaleOrderConfigModel>>(this.baseUrl, body);
  }

  getDepartments(): Observable<ApiResponse<DepartmentOptionModel[]>> {
    return this.http.get<ApiResponse<DepartmentOptionModel[]>>(`${this.baseUrl}/departments`);
  }

  getAudit(page = 1, pageSize = 20): Observable<ApiResponse<PaginatedResponse<SaleOrderConfigAuditModel>>> {
    const params = new HttpParams().set('page', page).set('pageSize', pageSize);
    return this.http.get<ApiResponse<PaginatedResponse<SaleOrderConfigAuditModel>>>(`${this.baseUrl}/audit`, { params });
  }
}
