import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { AddressModel, AddressRequest, ApiResponse } from './logistics.service';

// A29 §7.7 — a customer's shipping addresses (/api/addresses, Logistics). A sale order points at one by uuid.
// An address is a snapshot: it can be saved and read, never edited.

@Injectable({ providedIn: 'root' })
export class AddressService {
  private readonly baseUrl = `${environment.apiUrl}/addresses`;

  constructor(private http: HttpClient) {}

  /** The customer's addresses, newest first, each place once. */
  getAddresses(customerUuid: string): Observable<ApiResponse<AddressModel[]>> {
    const params = new HttpParams().set('consigneeUuid', customerUuid);
    return this.http.get<ApiResponse<AddressModel[]>>(this.baseUrl, { params });
  }

  getAddress(uuid: string): Observable<ApiResponse<AddressModel>> {
    return this.http.get<ApiResponse<AddressModel>>(`${this.baseUrl}/${uuid}`);
  }

  /** Saves an address for the customer named in `consigneeUuid`. A structurally meaningless one is refused. */
  createAddress(req: AddressRequest): Observable<ApiResponse<AddressModel>> {
    return this.http.post<ApiResponse<AddressModel>>(this.baseUrl, req);
  }
}
