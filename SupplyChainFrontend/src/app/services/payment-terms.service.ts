import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

export interface PaymentTermModel {
  id: string;
  name: string;
  days: number | null;
}

export interface CreatePaymentTermRequest {
  name: string;
  days?: number;
}

export interface ApiResponse<T = null> {
  success: boolean;
  message: string;
  result: T;
}

@Injectable({ providedIn: 'root' })
export class PaymentTermsService {
  private readonly baseUrl = 'https://localhost:51800/api/lookups';

  constructor(private http: HttpClient) {}

  getAll(): Observable<ApiResponse<PaymentTermModel[]>> {
    return this.http.get<ApiResponse<PaymentTermModel[]>>(`${this.baseUrl}/payment-terms`);
  }

  create(data: CreatePaymentTermRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${this.baseUrl}/payment-terms`, data);
  }

  update(id: string, data: CreatePaymentTermRequest): Observable<ApiResponse> {
    return this.http.put<ApiResponse>(`${this.baseUrl}/payment-terms/${id}`, data);
  }

  delete(id: string): Observable<ApiResponse> {
    return this.http.delete<ApiResponse>(`${this.baseUrl}/payment-terms/${id}`);
  }
}
