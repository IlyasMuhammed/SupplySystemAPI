import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

export interface LookupValue {
  value: any;
  id: string;
  typeId: string;
  displayName: string;
}

export interface LookupValueModel {
  id: string;
  displayName: string;
  notes?: string;
  isActive: boolean;
  sortOrder: number;
}

export interface CreateLookupValueByTypeRequest {
  displayName: string;
  notes?: string;
  sortOrder?: number;
}

export interface PatchLookupValueRequest {
  displayName?: string;
  notes?: string;
  isActive?: boolean;
  sortOrder?: number;
}

export interface ApiResponse<T = null> {
  success: boolean;
  message: string;
  result: T;
}

@Injectable({
  providedIn: 'root'
})
export class LookupValuesService {
  private readonly baseUrl = 'https://localhost:51800/api/lookups';

  constructor(private http: HttpClient) {}

  // ── Legacy endpoints (TypeId-based) ─────────────────────────────────────────

  createLookupValue(data: { typeId: string; displayName: string }): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${this.baseUrl}/lookup-values`, data);
  }

  deleteLookupValue(id: string): Observable<ApiResponse> {
    return this.http.delete<ApiResponse>(`${this.baseUrl}/lookup-values/${id}`);
  }

  getAllDropdowns(): Observable<ApiResponse<{ paymentTerms: { id: string; name: string }[]; deliveryTerms: { id: string; name: string }[]; currency: { id: string; name: string }[] }>> {
    return this.http.get<ApiResponse<{ paymentTerms: { id: string; name: string }[]; deliveryTerms: { id: string; name: string }[]; currency: { id: string; name: string }[] }>>(`${this.baseUrl}/all`);
  }

  // ── Slug-based endpoints ─────────────────────────────────────────────────────

  getByType(type: string): Observable<ApiResponse<LookupValueModel[]>> {
    return this.http.get<ApiResponse<LookupValueModel[]>>(`${this.baseUrl}/${type}`);
  }

  createByType(type: string, data: CreateLookupValueByTypeRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${this.baseUrl}/${type}`, data);
  }

  patchByType(type: string, id: string, data: PatchLookupValueRequest): Observable<ApiResponse> {
    return this.http.patch<ApiResponse>(`${this.baseUrl}/${type}/${id}`, data);
  }

  deleteByType(type: string, id: string): Observable<ApiResponse> {
    return this.http.delete<ApiResponse>(`${this.baseUrl}/${type}/${id}`);
  }
}
