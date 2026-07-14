import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

export interface CountryModel {
  id: string;
  name: string;
  code: string;
  isActive: boolean;
}

export interface ApiResponse<T = null> {
  success: boolean;
  message: string;
  result: T;
}

@Injectable({
  providedIn: 'root'
})
export class CountriesService {
  private readonly baseUrl = 'https://localhost:51800/api/lookups';

  constructor(private http: HttpClient) {}

  getAllCountries(): Observable<ApiResponse<CountryModel[]>> {
    return this.http.get<ApiResponse<CountryModel[]>>(`${this.baseUrl}/countries`);
  }

  createCountry(data: { name: string; code: string }): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${this.baseUrl}/countries`, data);
  }

  updateCountry(id: string, data: { name: string; code: string }): Observable<ApiResponse> {
    return this.http.put<ApiResponse>(`${this.baseUrl}/countries/${id}`, data);
  }

  deleteCountry(id: string): Observable<ApiResponse> {
    return this.http.delete<ApiResponse>(`${this.baseUrl}/countries/${id}`);
  }
}
