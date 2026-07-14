import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

export interface LookupType {
  id: string;
  name: string;
  description?: string;
}

export interface ApiResponse<T = null> {
  success: boolean;
  message: string;
  result: T;
}

@Injectable({
  providedIn: 'root'
})
export class LookupTypesService {
  private readonly baseUrl = 'https://localhost:51800/api/lookups';

  constructor(private http: HttpClient) {}

  getAllLookupTypes(): Observable<ApiResponse<LookupType[]>> {
    return this.http.get<ApiResponse<LookupType[]>>(`${this.baseUrl}/lookup-types`);
  }

  createLookupType(data: { name: string; description?: string }): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${this.baseUrl}/lookup-types`, data);
  }

  deleteLookupType(id: string): Observable<ApiResponse> {
    return this.http.delete<ApiResponse>(`${this.baseUrl}/lookup-types/${id}`);
  }
}
