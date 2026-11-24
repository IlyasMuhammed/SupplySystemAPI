import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import type { ApiResponse } from '../pages/service/auth.service';

// A51 P1 - industries master. Management: api/system/industries (platform super admin). Read for any signed-in user:
// api/industries?country=PK (the active industries offered in a country - the create-organization form uses it).

export interface Industry {
  id: number;
  code: string;
  name: string;
  description: string | null;
  isActive: boolean;
  sortOrder: number;
  /** Country codes with an active assignment. */
  countryCodes: string[];
}

/** countryCodes: omitted = leave the assignments alone; a list = exactly these countries. */
export interface SaveIndustryRequest {
  code?: string | null;
  name: string;
  description: string | null;
  isActive?: boolean;
  sortOrder?: number;
  countryCodes?: string[];
}

@Injectable({ providedIn: 'root' })
export class IndustriesService {
  private readonly base = `${environment.apiUrl}/system/industries`;
  private readonly readBase = `${environment.apiUrl}/industries`;

  constructor(private http: HttpClient) {}

  /** Any signed-in user: the active industries for a country. */
  byCountry(country: string): Observable<ApiResponse<Industry[]>> {
    return this.http.get<ApiResponse<Industry[]>>(this.readBase, { params: new HttpParams().set('country', country) });
  }

  list(): Observable<ApiResponse<Industry[]>> {
    return this.http.get<ApiResponse<Industry[]>>(this.base);
  }

  get(id: number): Observable<ApiResponse<Industry>> {
    return this.http.get<ApiResponse<Industry>>(`${this.base}/${id}`);
  }

  create(body: SaveIndustryRequest): Observable<ApiResponse<Industry>> {
    return this.http.post<ApiResponse<Industry>>(this.base, body);
  }

  update(id: number, body: SaveIndustryRequest): Observable<ApiResponse<Industry>> {
    return this.http.put<ApiResponse<Industry>>(`${this.base}/${id}`, body);
  }

  assignToCountry(id: number, countryCode: string): Observable<ApiResponse<Industry>> {
    return this.http.post<ApiResponse<Industry>>(`${this.base}/${id}/countries`, { countryCode });
  }

  removeFromCountry(id: number, countryCode: string): Observable<ApiResponse<Industry>> {
    return this.http.delete<ApiResponse<Industry>>(`${this.base}/${id}/countries/${encodeURIComponent(countryCode)}`);
  }
}
