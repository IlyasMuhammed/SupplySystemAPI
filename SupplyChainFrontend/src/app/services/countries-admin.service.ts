import { HttpClient } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import type { ApiResponse } from '../pages/service/auth.service';

// A51 P1 - Super admin country master (api/system/countries, platform super admin only).

export interface CountryAdmin {
  id: string;
  code: string;
  name: string;
  currencyCode: string | null;
  defaultTimezone: string | null;
  fiscalYearStartMonth: number;
  dateFormat: string;
  phoneCode: string | null;
  isActive: boolean;
  sortOrder: number;
}

/** Create and update share the shape; the code is fixed once the country exists. */
export interface SaveCountryRequest {
  code?: string | null;
  name: string;
  currencyCode: string | null;
  defaultTimezone: string | null;
  fiscalYearStartMonth: number;
  dateFormat: string;
  phoneCode: string | null;
  isActive?: boolean;
  sortOrder?: number;
}

@Injectable({ providedIn: 'root' })
export class CountriesAdminService {
  private readonly base = `${environment.apiUrl}/system/countries`;

  constructor(private http: HttpClient) {}

  list(): Observable<ApiResponse<CountryAdmin[]>> {
    return this.http.get<ApiResponse<CountryAdmin[]>>(this.base);
  }

  get(id: string): Observable<ApiResponse<CountryAdmin>> {
    return this.http.get<ApiResponse<CountryAdmin>>(`${this.base}/${id}`);
  }

  create(body: SaveCountryRequest): Observable<ApiResponse<CountryAdmin>> {
    return this.http.post<ApiResponse<CountryAdmin>>(this.base, body);
  }

  update(id: string, body: SaveCountryRequest): Observable<ApiResponse<CountryAdmin>> {
    return this.http.put<ApiResponse<CountryAdmin>>(`${this.base}/${id}`, body);
  }

  delete(id: string): Observable<ApiResponse<boolean>> {
    return this.http.delete<ApiResponse<boolean>>(`${this.base}/${id}`);
  }

  activate(id: string): Observable<ApiResponse<CountryAdmin>> {
    return this.http.post<ApiResponse<CountryAdmin>>(`${this.base}/${id}/activate`, {});
  }

  deactivate(id: string): Observable<ApiResponse<CountryAdmin>> {
    return this.http.post<ApiResponse<CountryAdmin>>(`${this.base}/${id}/deactivate`, {});
  }
}
