import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ApiResponse } from './demand.service';

const BASE = 'https://localhost:52800/api/po-document-template';

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
