import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ApiResponse } from './demand.service';
import { environment } from '../../environments/environment';

const BASE = `${environment.apiUrl}/po-document-template`;

export interface PoDocumentTemplateModel {
  id: string;
  companyName?: string;
  companyAddress?: string;
  companyLogoUrl?: string;
  companyTaxId?: string;
  companyPhone?: string;
  companyEmail?: string;
  bodyHtml?: string;
  showSignatureBlock: boolean;
  signatureDisclaimer?: string;
  preparedByLabel?: string;
  approvedByLabel?: string;
  authorizedSignatoryLabel?: string;
  footerText?: string;
  /** The company's bank details, printed on sales invoices so customers know where to pay. */
  bankDetails?: string;
  modifiedDate?: string;
}

export interface UpsertPoDocumentTemplateRequest {
  companyName?: string;
  companyAddress?: string;
  companyLogoUrl?: string;
  companyTaxId?: string;
  companyPhone?: string;
  companyEmail?: string;
  bodyHtml?: string;
  showSignatureBlock: boolean;
  signatureDisclaimer?: string;
  preparedByLabel?: string;
  approvedByLabel?: string;
  authorizedSignatoryLabel?: string;
  footerText?: string;
  /** Up to 1,000 characters, one item per line. Blank clears it. */
  bankDetails?: string;
}

export interface PoDocumentTokenModel {
  token: string;
  label: string;
  group: string;
}

@Injectable({ providedIn: 'root' })
export class PoDocumentTemplateService {
  constructor(private http: HttpClient) {}

  get(): Observable<ApiResponse<PoDocumentTemplateModel | null>> {
    return this.http.get<ApiResponse<PoDocumentTemplateModel | null>>(BASE);
  }

  upsert(req: UpsertPoDocumentTemplateRequest): Observable<ApiResponse<string>> {
    return this.http.put<ApiResponse<string>>(BASE, req);
  }

  getTokens(): Observable<ApiResponse<PoDocumentTokenModel[]>> {
    return this.http.get<ApiResponse<PoDocumentTokenModel[]>>(`${BASE}/tokens`);
  }
}
