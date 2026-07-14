import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

export interface CurrencyModel {
  id: string;
  name: string;
  code: string | null;
  symbol: string | null;
}

export interface CreateCurrencyRequest {
  name: string;
  code?: string;
  symbol?: string;
}

export interface ApiResponse<T = null> {
  success: boolean;
  message: string;
  result: T;
}

@Injectable({ providedIn: 'root' })
export class CurrenciesService {
  private readonly baseUrl = 'https://localhost:51800/api/lookups';

  constructor(private http: HttpClient) {}

  getAll(): Observable<ApiResponse<CurrencyModel[]>> {
    return this.http.get<ApiResponse<CurrencyModel[]>>(`${this.baseUrl}/currencies`);
  }

  create(data: CreateCurrencyRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${this.baseUrl}/currencies`, data);
  }

  update(id: string, data: CreateCurrencyRequest): Observable<ApiResponse> {
    return this.http.put<ApiResponse>(`${this.baseUrl}/currencies/${id}`, data);
  }

  delete(id: string): Observable<ApiResponse> {
    return this.http.delete<ApiResponse>(`${this.baseUrl}/currencies/${id}`);
  }
}
