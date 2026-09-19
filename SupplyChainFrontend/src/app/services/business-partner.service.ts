import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { ApiResponse, PaginatedResponse } from './supplier.service';

// Addendum 29 — the new partner-type-aware surface (P1-04/P1-05 on the backend). Deliberately
// separate from SupplierService: that one is the full legacy vendor onboarding workflow (status
// state machine, contacts, bank details, documents) and keeps working exactly as it does today.
// This is the narrower create/edit-the-flags/list-by-type surface /api/partners exposes.

export interface BusinessPartnerModel {
  // Never read from the request body by the backend on create (a fresh one is always generated)
  // or update (the route parameter is what's used) — optional so a create payload can omit it
  // entirely rather than send an empty string, which fails ASP.NET's Guid model binding outright.
  uuid?: string;
  partnerCode: string;
  companyName: string;
  partnerType: string;
  isVendor: boolean;
  isCustomer: boolean;
  isCarrier: boolean;
  isServiceProvider: boolean;
  vehicleTypes?: string | null;
  serviceCategories?: string | null;
  isActive: boolean;
}

export interface BusinessPartnerFilter {
  type?: string;
  isVendor?: boolean;
  isCustomer?: boolean;
  isCarrier?: boolean;
  isServiceProvider?: boolean;
  active?: boolean;
  search?: string;
  page?: number;
  pageSize?: number;
}

@Injectable({ providedIn: 'root' })
export class BusinessPartnerService {
  private readonly baseUrl = `${environment.apiUrl}/partners`;

  constructor(private http: HttpClient) {}

  getPartners(filter: BusinessPartnerFilter = {}): Observable<ApiResponse<PaginatedResponse<BusinessPartnerModel>>> {
    let params = new HttpParams();
    if (filter.type)              params = params.set('type', filter.type);
    if (filter.isVendor !== undefined)          params = params.set('isVendor', String(filter.isVendor));
    if (filter.isCustomer !== undefined)        params = params.set('isCustomer', String(filter.isCustomer));
    if (filter.isCarrier !== undefined)         params = params.set('isCarrier', String(filter.isCarrier));
    if (filter.isServiceProvider !== undefined) params = params.set('isServiceProvider', String(filter.isServiceProvider));
    if (filter.active !== undefined)            params = params.set('active', String(filter.active));
    if (filter.search)            params = params.set('search', filter.search);
    params = params.set('page',     String(filter.page     ?? 1));
    params = params.set('pageSize', String(filter.pageSize ?? 20));
    return this.http.get<ApiResponse<PaginatedResponse<BusinessPartnerModel>>>(this.baseUrl, { params });
  }

  getPartnerById(uuid: string): Observable<ApiResponse<BusinessPartnerModel>> {
    return this.http.get<ApiResponse<BusinessPartnerModel>>(`${this.baseUrl}/${uuid}`);
  }

  createPartner(model: BusinessPartnerModel): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(this.baseUrl, model);
  }

  updatePartner(uuid: string, model: BusinessPartnerModel): Observable<ApiResponse> {
    return this.http.put<ApiResponse>(`${this.baseUrl}/${uuid}`, model);
  }

  deletePartner(uuid: string): Observable<ApiResponse> {
    return this.http.delete<ApiResponse>(`${this.baseUrl}/${uuid}`);
  }
}
