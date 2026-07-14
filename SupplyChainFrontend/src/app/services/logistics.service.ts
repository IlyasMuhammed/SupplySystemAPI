import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';

const BASE = 'https://localhost:51800/api/logistics';

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

// ── Carrier models ────────────────────────────────────────────────────────────

export interface CreateCarrierRequest {
  name: string;
  code: string;
  serviceType?: string;
  trackingUrlTemplate?: string;
  contactName?: string;
  contactPhone?: string;
  contactEmail?: string;
  ratePerKg?: number;
}

export interface PatchCarrierRequest {
  name?: string;
  serviceType?: string;
  trackingUrlTemplate?: string;
  contactName?: string;
  contactPhone?: string;
  contactEmail?: string;
  ratePerKg?: number;
  status?: string;
  isActive?: boolean;
}

export interface CarrierListItemModel {
  uuid: string;
  name: string;
  code: string;
  serviceType?: string;
  status: string;
  ratePerKg?: number;
  isActive: boolean;
}

export interface CarrierDetailModel {
  uuid: string;
  name: string;
  code: string;
  serviceType?: string;
  trackingUrlTemplate?: string;
  contactName?: string;
  contactPhone?: string;
  contactEmail?: string;
  ratePerKg?: number;
  status: string;
  isActive: boolean;
  createdDate: string;
}

export interface CarrierFilter {
  search?: string;
  status?: string;
  page?: number;
  pageSize?: number;
}

// ── Shipment models ───────────────────────────────────────────────────────────

export interface CreateShipmentRequest {
  poUuid: string;
  carrierUuid?: string;
  shipmentType: string;
  dispatchDate: string;
  estimatedArrival: string;
  trackingNumber?: string;
  originWarehouseUuid?: string;
  destinationAddress: string;
  weightKg?: number;
  volumeCbm?: number;
  freightCost?: number;
  notes?: string;
}

export interface PatchShipmentRequest {
  shipmentType?: string;
  dispatchDate?: string;
  estimatedArrival?: string;
  actualArrival?: string;
  trackingNumber?: string;
  trackingUrl?: string;
  destinationAddress?: string;
  weightKg?: number;
  volumeCbm?: number;
  freightCost?: number;
  status?: string;
  proofOfDeliveryUrl?: string;
  notes?: string;
  carrierUuid?: string;
}

export interface ShipmentListItemModel {
  uuid: string;
  shipmentNumber: string;
  poNumber: string;
  carrierName?: string;
  shipmentType: string;
  dispatchDate: string;
  estimatedArrival: string;
  actualArrival?: string;
  status: string;
  trackingNumber?: string;
}

export interface ShipmentDetailModel {
  uuid: string;
  shipmentNumber: string;
  poUuid: string;
  poNumber: string;
  carrierUuid?: string;
  carrierName?: string;
  shipmentType: string;
  dispatchDate: string;
  estimatedArrival: string;
  actualArrival?: string;
  trackingNumber?: string;
  trackingUrl?: string;
  originWarehouseUuid?: string;
  destinationAddress: string;
  weightKg?: number;
  volumeCbm?: number;
  freightCost?: number;
  status: string;
  proofOfDeliveryUrl?: string;
  notes?: string;
  createdDate: string;
}

export interface ShipmentFilter {
  status?: string;
  search?: string;
  carrierUuid?: string;
  page?: number;
  pageSize?: number;
}

// ── Service ───────────────────────────────────────────────────────────────────

@Injectable({ providedIn: 'root' })
export class LogisticsService {
  constructor(private http: HttpClient) {}

  // ── Carriers ──────────────────────────────────────────────────────────────

  createCarrier(req: CreateCarrierRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${BASE}/carriers`, req);
  }

  getCarriers(filter: CarrierFilter = {}): Observable<ApiResponse<PaginatedResponse<CarrierListItemModel>>> {
    let params = new HttpParams();
    if (filter.search)   params = params.set('search',   filter.search);
    if (filter.status)   params = params.set('status',   filter.status);
    params = params.set('page',     String(filter.page     ?? 1));
    params = params.set('pageSize', String(filter.pageSize ?? 20));
    return this.http.get<ApiResponse<PaginatedResponse<CarrierListItemModel>>>(`${BASE}/carriers`, { params });
  }

  getActiveCarriers(): Observable<ApiResponse<CarrierListItemModel[]>> {
    return this.http.get<ApiResponse<CarrierListItemModel[]>>(`${BASE}/carriers/active`);
  }

  getCarrierById(uuid: string): Observable<ApiResponse<CarrierDetailModel>> {
    return this.http.get<ApiResponse<CarrierDetailModel>>(`${BASE}/carriers/${uuid}`);
  }

  patchCarrier(uuid: string, req: PatchCarrierRequest): Observable<ApiResponse> {
    return this.http.patch<ApiResponse>(`${BASE}/carriers/${uuid}`, req);
  }

  deleteCarrier(uuid: string): Observable<ApiResponse> {
    return this.http.delete<ApiResponse>(`${BASE}/carriers/${uuid}`);
  }

  // ── Shipments ─────────────────────────────────────────────────────────────

  createShipment(req: CreateShipmentRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${BASE}/shipments`, req);
  }

  getShipments(filter: ShipmentFilter = {}): Observable<ApiResponse<PaginatedResponse<ShipmentListItemModel>>> {
    let params = new HttpParams();
    if (filter.status)      params = params.set('status',      filter.status);
    if (filter.search)      params = params.set('search',      filter.search);
    if (filter.carrierUuid) params = params.set('carrierUuid', filter.carrierUuid);
    params = params.set('page',     String(filter.page     ?? 1));
    params = params.set('pageSize', String(filter.pageSize ?? 20));
    return this.http.get<ApiResponse<PaginatedResponse<ShipmentListItemModel>>>(`${BASE}/shipments`, { params });
  }

  getShipmentById(uuid: string): Observable<ApiResponse<ShipmentDetailModel>> {
    return this.http.get<ApiResponse<ShipmentDetailModel>>(`${BASE}/shipments/${uuid}`);
  }

  patchShipment(uuid: string, req: PatchShipmentRequest): Observable<ApiResponse> {
    return this.http.patch<ApiResponse>(`${BASE}/shipments/${uuid}`, req);
  }

  uploadPod(uuid: string, file: File): Observable<ApiResponse<string>> {
    const form = new FormData();
    form.append('file', file, file.name);
    return this.http.post<ApiResponse<string>>(`${BASE}/shipments/${uuid}/pod/upload`, form);
  }

  resolveFileUrl(url: string): string {
    if (!url) return '';
    return url.startsWith('/') ? `https://localhost:51800${url}` : url;
  }
}
