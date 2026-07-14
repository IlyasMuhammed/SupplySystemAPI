import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';

// ── Shared response wrapper ───────────────────────────────────────────────────

export interface ApiResponse<T = null> {
  success: boolean;
  message: string;
  result: T;
}

export interface PaginatedResponse<T> {
  data: T[];
  totalRecords: number;
  page: number;
  pageSize: number;
  totalPages: number;
}

// ── Junction types ────────────────────────────────────────────────────────────

export interface SupplierTypeMappingInput {
  lookupValueId: string;
  isPrimary: boolean;
  notes?: string;
}

export interface SupplierTypeMappingModel {
  lookupValueId: string;
  isPrimary: boolean;
  notes?: string;
  assignedAt: string;
}

// ── List / detail models ──────────────────────────────────────────────────────

export interface SupplierListItemModel {
  id: number;
  uuid: string;
  supplierName: string;
  supplierCode?: string;
  status?: string;
  country?: string;
  email?: string;
  phone?: string;
  isActive: boolean;
  supplierTypeIds: string[];
  industryIds: string[];
}

export interface SupplierDetailModel {
  id: number;
  uuid: string;
  supplierName: string;
  supplierCode?: string;
  registrationNo?: string;
  taxId?: string;
  country?: string;
  provinceState?: string;
  city?: string;
  addressLine1?: string;
  addressLine2?: string;
  postalCode?: string;
  phone?: string;
  fax?: string;
  email?: string;
  website?: string;
  primaryContactName?: string;
  primaryContactTitle?: string;
  primaryContactPhone?: string;
  primaryContactEmail?: string;
  preferredPaymentTerms?: string;
  preferredCurrency?: string;
  creditLimit?: number;
  leadTimeDays?: number;
  rating?: number;
  status?: string;
  onboardingDate?: string;
  lastReviewDate?: string;
  notes?: string;
  isPreferredSupplier: boolean;
  isActive: boolean;
  createdDate: string;
  supplierTypes: SupplierTypeMappingModel[];
  industries: SupplierTypeMappingModel[];
  contacts: ContactModel[];
}

export interface ContactModel {
  id: number;
  contactName: string;
  title?: string;
  phone?: string;
  email?: string;
  isPrimary: boolean;
  isActive: boolean;
}

export interface BankDetailModel {
  bankName?: string;
  bankAccountNo?: string;
  bankIban?: string;
  bankSwift?: string;
  createdAt: string;
  updatedAt?: string;
}

export interface DocumentModel {
  id: number;
  fileName: string;
  fileUrl: string;
  documentType?: string;
  uploadedAt: string;
  isActive: boolean;
}

// ── Filters ───────────────────────────────────────────────────────────────────

export interface SupplierListFilter {
  status?: string;
  supplierType?: string;
  industryCategory?: string;
  country?: string;
  search?: string;
  page?: number;
  pageSize?: number;
}

// ── Request bodies ────────────────────────────────────────────────────────────

export interface CreateSupplierRequest {
  supplierName: string;
  supplierCode: string;
  registrationNo?: string;
  taxId?: string;
  country?: string;
  provinceState?: string;
  city?: string;
  addressLine1?: string;
  addressLine2?: string;
  postalCode?: string;
  phone?: string;
  fax?: string;
  email?: string;
  website?: string;
  primaryContactName?: string;
  primaryContactTitle?: string;
  primaryContactPhone?: string;
  primaryContactEmail?: string;
  preferredPaymentTerms?: string;
  preferredCurrency?: string;
  creditLimit?: number;
  leadTimeDays?: number;
  notes?: string;
  isPreferredSupplier: boolean;
  supplierTypeIds: SupplierTypeMappingInput[];
  industryIds: SupplierTypeMappingInput[];
}

export interface PatchSupplierRequest {
  supplierName?: string;
  registrationNo?: string;
  taxId?: string;
  country?: string;
  provinceState?: string;
  city?: string;
  addressLine1?: string;
  addressLine2?: string;
  postalCode?: string;
  phone?: string;
  fax?: string;
  email?: string;
  website?: string;
  primaryContactName?: string;
  primaryContactTitle?: string;
  primaryContactPhone?: string;
  primaryContactEmail?: string;
  preferredPaymentTerms?: string;
  preferredCurrency?: string;
  creditLimit?: number;
  leadTimeDays?: number;
  notes?: string;
  isPreferredSupplier?: boolean;
  isActive?: boolean;
  supplierTypeIds?: SupplierTypeMappingInput[];
  industryIds?: SupplierTypeMappingInput[];
}

export interface AddContactRequest {
  contactName: string;
  title?: string;
  phone?: string;
  email?: string;
  isPrimary: boolean;
}

export interface PatchContactRequest {
  phone?: string;
  email?: string;
  title?: string;
}

// ── RFQ-001: Eligible contacts ────────────────────────────────────────────────

export interface EligibleContactModel {
  id: number;
  contactName: string;
  title?: string;
  phone?: string;
  email?: string;
  isPrimary: boolean;
  isMobileValid: boolean;
  normalisedMobile?: string;
  sortOrder: number;
}

export interface EligibleContactsResponse {
  hasUsableContact: boolean;
  contacts: EligibleContactModel[];
}

export interface RejectSupplierRequest {
  reason: string;
}

export interface BlacklistSupplierRequest {
  reason: string;
}

export interface SuspendSupplierRequest {
  reason: string;
  reviewDate?: string;
}

export interface UpsertBankDetailRequest {
  bankName?: string;
  bankAccountNo?: string;
  bankIban?: string;
  bankSwift?: string;
}

export interface AttachDocumentRequest {
  fileName: string;
  fileUrl: string;
  documentType?: string;
}

// ── Legacy models (kept for backward compat) ──────────────────────────────────

export interface SupplierModel {
  uuid: string;
  supplierName: string;
  supplierCode?: string;
  isActive: boolean;
}

export interface SupplierTypeModel {
  id: string;
  name: string;
  description: string;
}

export interface SupplierCategoryModel {
  id: string;
  name: string;
}

// ── Service ───────────────────────────────────────────────────────────────────

@Injectable({ providedIn: 'root' })
export class SupplierService {
  private readonly baseUrl = 'https://localhost:51800/api/suppliers';

  constructor(private http: HttpClient) {}

  // ── Supplier CRUD ─────────────────────────────────────────────────────────

  getSuppliers(filter: SupplierListFilter = {}): Observable<ApiResponse<PaginatedResponse<SupplierListItemModel>>> {
    let params = new HttpParams();
    if (filter.status)          params = params.set('status', filter.status);
    if (filter.supplierType)    params = params.set('supplierType', filter.supplierType);
    if (filter.industryCategory) params = params.set('industryCategory', filter.industryCategory);
    if (filter.country)         params = params.set('country', filter.country);
    if (filter.search)          params = params.set('search', filter.search);
    params = params.set('page',     String(filter.page     ?? 1));
    params = params.set('pageSize', String(filter.pageSize ?? 20));
    return this.http.get<ApiResponse<PaginatedResponse<SupplierListItemModel>>>(this.baseUrl, { params });
  }

  getSupplierById(uuid: string): Observable<ApiResponse<SupplierDetailModel>> {
    return this.http.get<ApiResponse<SupplierDetailModel>>(`${this.baseUrl}/${uuid}`);
  }

  createSupplier(data: CreateSupplierRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(this.baseUrl, data);
  }

  patchSupplier(uuid: string, data: PatchSupplierRequest): Observable<ApiResponse> {
    return this.http.patch<ApiResponse>(`${this.baseUrl}/${uuid}`, data);
  }

  addContact(uuid: string, data: AddContactRequest): Observable<ApiResponse<number>> {
    return this.http.post<ApiResponse<number>>(`${this.baseUrl}/${uuid}/contacts`, data);
  }

  patchContact(supplierUuid: string, contactId: number, data: PatchContactRequest): Observable<ApiResponse> {
    return this.http.patch<ApiResponse>(`${this.baseUrl}/${supplierUuid}/contacts/${contactId}`, data);
  }

  getEligibleContacts(uuid: string): Observable<ApiResponse<EligibleContactsResponse>> {
    return this.http.get<ApiResponse<EligibleContactsResponse>>(`${this.baseUrl}/${uuid}/contacts/eligible`);
  }

  // ── Status state machine ──────────────────────────────────────────────────

  approveSupplier(uuid: string): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${this.baseUrl}/${uuid}/approve`, {});
  }

  rejectSupplier(uuid: string, data: RejectSupplierRequest): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${this.baseUrl}/${uuid}/reject`, data);
  }

  blacklistSupplier(uuid: string, data: BlacklistSupplierRequest): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${this.baseUrl}/${uuid}/blacklist`, data);
  }

  suspendSupplier(uuid: string, data: SuspendSupplierRequest): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${this.baseUrl}/${uuid}/suspend`, data);
  }

  // ── Bank details ──────────────────────────────────────────────────────────

  upsertBankDetail(uuid: string, data: UpsertBankDetailRequest): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${this.baseUrl}/${uuid}/bank-details`, data);
  }

  getBankDetail(uuid: string): Observable<ApiResponse<BankDetailModel>> {
    return this.http.get<ApiResponse<BankDetailModel>>(`${this.baseUrl}/${uuid}/bank-details`);
  }

  // ── Documents ─────────────────────────────────────────────────────────────

  attachDocument(uuid: string, data: AttachDocumentRequest): Observable<ApiResponse<number>> {
    return this.http.post<ApiResponse<number>>(`${this.baseUrl}/${uuid}/documents`, data);
  }

  uploadDocument(uuid: string, file: File, documentType?: string): Observable<ApiResponse<number>> {
    const form = new FormData();
    form.append('file', file, file.name);
    if (documentType) form.append('documentType', documentType);
    return this.http.post<ApiResponse<number>>(`${this.baseUrl}/${uuid}/documents/upload`, form);
  }

  getDocuments(uuid: string): Observable<ApiResponse<DocumentModel[]>> {
    return this.http.get<ApiResponse<DocumentModel[]>>(`${this.baseUrl}/${uuid}/documents`);
  }

  softDeleteDocument(uuid: string, docId: number): Observable<ApiResponse> {
    return this.http.delete<ApiResponse>(`${this.baseUrl}/${uuid}/documents/${docId}`);
  }

  // ── Legacy dropdown methods ───────────────────────────────────────────────

  getSupplierTypes(): Observable<ApiResponse<SupplierTypeModel[]>> {
    return this.http.get<ApiResponse<SupplierTypeModel[]>>(`${this.baseUrl}/types`);
  }

  createSupplierType(data: { name: string; description: string }): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${this.baseUrl}/types`, data);
  }

  updateSupplierType(id: string, data: { name: string; description: string }): Observable<ApiResponse> {
    return this.http.put<ApiResponse>(`${this.baseUrl}/types/${id}`, data);
  }

  deleteSupplierType(id: string): Observable<ApiResponse> {
    return this.http.delete<ApiResponse>(`${this.baseUrl}/types/${id}`);
  }

  getCategories(): Observable<ApiResponse<SupplierCategoryModel[]>> {
    return this.http.get<ApiResponse<SupplierCategoryModel[]>>(`${this.baseUrl}/categories`);
  }

  createCategory(data: { name: string }): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${this.baseUrl}/categories`, data);
  }

  updateCategory(id: string, data: { name: string }): Observable<ApiResponse> {
    return this.http.put<ApiResponse>(`${this.baseUrl}/categories/${id}`, data);
  }

  deleteCategory(id: string): Observable<ApiResponse> {
    return this.http.delete<ApiResponse>(`${this.baseUrl}/categories/${id}`);
  }
}
