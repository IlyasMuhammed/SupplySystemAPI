import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

export interface OrganizationSettingsModel {
  ackLinkExpiryDays: number;
}

export interface ApiResponse<T = null> {
  success: boolean;
  message: string;
  result: T;
}

@Injectable({
  providedIn: 'root'
})
export class OrganizationSettingsService {
  private readonly baseUrl = `${environment.apiUrl}/organization-settings`;

  constructor(private http: HttpClient) {}

  get(): Observable<ApiResponse<OrganizationSettingsModel>> {
    return this.http.get<ApiResponse<OrganizationSettingsModel>>(this.baseUrl);
  }

  update(data: OrganizationSettingsModel): Observable<ApiResponse> {
    return this.http.put<ApiResponse>(this.baseUrl, data);
  }
}
