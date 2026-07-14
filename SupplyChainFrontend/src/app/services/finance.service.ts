import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';

const BASE = 'https://localhost:51800/api/finance';

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

// ── Invoice models ────────────────────────────────────────────────────────────

export interface InvoiceLineRequest {
  grnLineUuid?: string;
  poLineUuid: string;
  itemDescription: string;
  unitOfMeasure?: string;
  qtyInvoiced: number;
  unitPrice: number;
}

export interface InvoiceLineModel {
  uuid: string;
  grnLineUuid?: string;
  poLineUuid: string;
  lineNo: number;
  itemDescription: string;
  unitOfMeasure?: string;
  qtyInvoiced: number;
  unitPrice: number;
  lineTotal: number;
}

export interface CreateInvoiceRequest {
  supplierInvoiceNo?: string;
  supplierId: string;
  poUuid: string;
  grnUuid?: string;
  invoiceDate: string;
  receivedDate: string;
  dueDate: string;
  currency: string;
  subtotal: number;        // ignored when lines are provided (computed server-side)
  taxAmount: number;
  paymentMethod?: string;
  notes?: string;
  attachmentUrl?: string;
  lines?: InvoiceLineRequest[];
}

export interface PatchInvoiceRequest {
  supplierInvoiceNo?: string;
  dueDate?: string;
  paymentMethod?: string;
  taxAmount?: number;
  matchStatus?: string;
  paymentStatus?: string;
  notes?: string;
  attachmentUrl?: string;
}

export interface InvoiceListItemModel {
  uuid: string;
  invoiceNumber: string;
  supplierInvoiceNo?: string;
  supplierName: string;
  poNumber: string;
  grnNumber?: string;
  invoiceDate: string;
  dueDate: string;
  totalAmount: number;
  currency: string;
  matchStatus: string;
  paymentStatus: string;
}

export interface PaymentSummaryModel {
  uuid: string;
  paymentNumber: string;
  invoiceNumber: string;
  supplierName: string;
  paymentDate: string;
  amountPaid: number;
  paymentMethod: string;
  status: string;
}

export interface InvoiceDetailModel {
  uuid: string;
  invoiceNumber: string;
  supplierInvoiceNo?: string;
  supplierId: string;
  supplierName: string;
  poUuid: string;
  poNumber: string;
  grnUuid?: string;
  grnNumber?: string;
  invoiceDate: string;
  receivedDate: string;
  dueDate: string;
  currency: string;
  subtotal: number;
  taxAmount: number;
  totalAmount: number;
  matchedPoValue: number;
  matchedGrnValue: number;
  varianceAmount: number;
  matchStatus: string;
  paymentStatus: string;
  paymentMethod?: string;
  approvedBy?: number;
  approvedAt?: string;
  notes?: string;
  attachmentUrl?: string;
  createdDate: string;
  lines: InvoiceLineModel[];
  payments: PaymentSummaryModel[];
}

export interface InvoiceFilter {
  matchStatus?: string;
  paymentStatus?: string;
  supplierId?: string;
  search?: string;
  dateFrom?: string;
  dateTo?: string;
  page?: number;
  pageSize?: number;
}

// ── Payment models ────────────────────────────────────────────────────────────

export interface CreatePaymentRequest {
  invoiceUuid: string;
  paymentDate: string;
  amountPaid: number;
  paymentMethod: string;
  bankReference?: string;
  chequeNumber?: string;
  accountDebited?: string;
  notes?: string;
}

export interface PatchPaymentRequest {
  status?: string;
  bankReference?: string;
  chequeNumber?: string;
  notes?: string;
}

export interface PaymentListItemModel {
  uuid: string;
  paymentNumber: string;
  invoiceNumber: string;
  supplierName: string;
  paymentDate: string;
  amountPaid: number;
  paymentMethod: string;
  status: string;
}

export interface PaymentDetailModel {
  uuid: string;
  paymentNumber: string;
  invoiceUuid: string;
  invoiceNumber: string;
  supplierId: string;
  supplierName: string;
  paymentDate: string;
  amountPaid: number;
  paymentMethod: string;
  bankReference?: string;
  chequeNumber?: string;
  accountDebited?: string;
  status: string;
  notes?: string;
  processedAt: string;
  createdDate: string;
}

export interface PaymentFilter {
  status?: string;
  supplierId?: string;
  search?: string;
  page?: number;
  pageSize?: number;
}

// ── Debit Note models ─────────────────────────────────────────────────────────

export interface CreateDebitNoteRequest {
  sroId: string;
  debitReason: string;
  debitReasonDetail?: string;
  debitAmount: number;
  notes?: string;
}

export interface UpdateDebitNoteStatusRequest {
  newStatus: string;  // ACKNOWLEDGED | DISPUTED | SETTLED | WRITTEN_OFF
  disputeNotes?: string;
  notes?: string;
}

export interface DebitNoteListFilter {
  supplierId?: string;
  status?: string;
  dateFrom?: string;
  dateTo?: string;
  page?: number;
  pageSize?: number;
}

export interface DebitNoteListItemModel {
  uuid: string;
  debitNoteNumber: string;
  sroNumber: string;
  supplierId: string;
  supplierName: string;
  debitReason: string;
  debitAmount: number;
  status: string;
  issuedAt?: string;
  createdDate: string;
}

export interface DebitNoteDetailModel {
  uuid: string;
  debitNoteNumber: string;
  sroUuid: string;
  sroNumber: string;
  supplierId: string;
  supplierName: string;
  supplierContactEmail?: string;
  debitReason: string;
  debitReasonDetail?: string;
  debitAmount: number;
  status: string;
  issuedAt?: string;
  acknowledgedAt?: string;
  disputedAt?: string;
  settledAt?: string;
  disputeNotes?: string;
  notes?: string;
  createdDate: string;
}

// ── Credit Note models ────────────────────────────────────────────────────────

export interface CreateCreditNoteRequest {
  sroId: string;
  supplierCreditNoteNo: string;
  creditDate: string;    // ISO date
  creditAmount: number;
  invoiceUuid?: string;
  notes?: string;
}

export interface CreditNoteListFilter {
  supplierId?: string;
  applicationStatus?: string;
  dateFrom?: string;
  dateTo?: string;
  page?: number;
  pageSize?: number;
}

export interface CreditNoteListItemModel {
  uuid: string;
  creditNoteNumber: string;
  supplierCreditNoteNo: string;
  sroNumber: string;
  supplierId: string;
  supplierName: string;
  invoiceNumber?: string;
  creditDate: string;
  creditAmount: number;
  applicationStatus: string;
  appliedToInvoiceNumber?: string;
  createdDate: string;
}

export interface CreditNoteDetailModel {
  uuid: string;
  creditNoteNumber: string;
  supplierCreditNoteNo: string;
  sroUuid: string;
  sroNumber: string;
  supplierId: string;
  supplierName: string;
  invoiceUuid?: string;
  invoiceNumber?: string;
  creditDate: string;
  creditAmount: number;
  applicationStatus: string;
  appliedToInvoiceUuid?: string;
  appliedToInvoiceNumber?: string;
  carriedForwardAmount?: number;
  appliedAt?: string;
  notes?: string;
  createdDate: string;
}

// ── Service ───────────────────────────────────────────────────────────────────

@Injectable({ providedIn: 'root' })
export class FinanceService {
  constructor(private http: HttpClient) {}

  // ── Invoices ──────────────────────────────────────────────────────────────

  createInvoice(req: CreateInvoiceRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${BASE}/invoices`, req);
  }

  getInvoices(filter: InvoiceFilter = {}): Observable<ApiResponse<PaginatedResponse<InvoiceListItemModel>>> {
    let params = new HttpParams();
    if (filter.matchStatus)   params = params.set('matchStatus',   filter.matchStatus);
    if (filter.paymentStatus) params = params.set('paymentStatus', filter.paymentStatus);
    if (filter.supplierId)    params = params.set('supplierId',    filter.supplierId);
    if (filter.search)        params = params.set('search',        filter.search);
    if (filter.dateFrom)      params = params.set('dateFrom',      filter.dateFrom);
    if (filter.dateTo)        params = params.set('dateTo',        filter.dateTo);
    params = params.set('page',     String(filter.page     ?? 1));
    params = params.set('pageSize', String(filter.pageSize ?? 20));
    return this.http.get<ApiResponse<PaginatedResponse<InvoiceListItemModel>>>(`${BASE}/invoices`, { params });
  }

  getInvoiceById(uuid: string): Observable<ApiResponse<InvoiceDetailModel>> {
    return this.http.get<ApiResponse<InvoiceDetailModel>>(`${BASE}/invoices/${uuid}`);
  }

  patchInvoice(uuid: string, req: PatchInvoiceRequest): Observable<ApiResponse> {
    return this.http.patch<ApiResponse>(`${BASE}/invoices/${uuid}`, req);
  }

  approveInvoice(uuid: string, notes?: string): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${BASE}/invoices/${uuid}/approve`, { notes });
  }

  rejectInvoice(uuid: string, reason: string): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${BASE}/invoices/${uuid}/reject`, { reason });
  }

  uploadAttachment(uuid: string, file: File): Observable<ApiResponse<string>> {
    const form = new FormData();
    form.append('file', file, file.name);
    return this.http.post<ApiResponse<string>>(`${BASE}/invoices/${uuid}/attachment/upload`, form);
  }

  // ── Payments ──────────────────────────────────────────────────────────────

  createPayment(req: CreatePaymentRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${BASE}/payments`, req);
  }

  getPayments(filter: PaymentFilter = {}): Observable<ApiResponse<PaginatedResponse<PaymentListItemModel>>> {
    let params = new HttpParams();
    if (filter.status)     params = params.set('status',     filter.status);
    if (filter.supplierId) params = params.set('supplierId', filter.supplierId);
    if (filter.search)     params = params.set('search',     filter.search);
    params = params.set('page',     String(filter.page     ?? 1));
    params = params.set('pageSize', String(filter.pageSize ?? 20));
    return this.http.get<ApiResponse<PaginatedResponse<PaymentListItemModel>>>(`${BASE}/payments`, { params });
  }

  getPaymentById(uuid: string): Observable<ApiResponse<PaymentDetailModel>> {
    return this.http.get<ApiResponse<PaymentDetailModel>>(`${BASE}/payments/${uuid}`);
  }

  patchPayment(uuid: string, req: PatchPaymentRequest): Observable<ApiResponse> {
    return this.http.patch<ApiResponse>(`${BASE}/payments/${uuid}`, req);
  }

  resolveFileUrl(url: string): string {
    if (!url) return '';
    return url.startsWith('/') ? `https://localhost:51800${url}` : url;
  }

  // ── Credit Notes ──────────────────────────────────────────────────────────

  createCreditNote(req: CreateCreditNoteRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`https://localhost:51800/api/credit-notes`, req);
  }

  getCreditNotes(filter: CreditNoteListFilter): Observable<ApiResponse<PaginatedResponse<CreditNoteListItemModel>>> {
    let params = new HttpParams()
      .set('page',     filter.page?.toString()     ?? '1')
      .set('pageSize', filter.pageSize?.toString() ?? '20');
    if (filter.supplierId)        params = params.set('supplierId',        filter.supplierId);
    if (filter.applicationStatus) params = params.set('applicationStatus', filter.applicationStatus);
    if (filter.dateFrom)          params = params.set('dateFrom',          filter.dateFrom);
    if (filter.dateTo)            params = params.set('dateTo',            filter.dateTo);
    return this.http.get<ApiResponse<PaginatedResponse<CreditNoteListItemModel>>>(
      `https://localhost:51800/api/credit-notes`, { params });
  }

  getCreditNoteById(uuid: string): Observable<ApiResponse<CreditNoteDetailModel>> {
    return this.http.get<ApiResponse<CreditNoteDetailModel>>(`https://localhost:51800/api/credit-notes/${uuid}`);
  }

  applyCreditNote(uuid: string, invoiceUuid: string): Observable<ApiResponse<null>> {
    return this.http.post<ApiResponse<null>>(`https://localhost:51800/api/credit-notes/${uuid}/apply`, { invoiceUuid });
  }

  // ── Debit Notes ───────────────────────────────────────────────────────────

  createDebitNote(req: CreateDebitNoteRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`https://localhost:51800/api/debit-notes`, req);
  }

  getDebitNotes(filter: DebitNoteListFilter): Observable<ApiResponse<PaginatedResponse<DebitNoteListItemModel>>> {
    let params = new HttpParams()
      .set('page',     filter.page?.toString()     ?? '1')
      .set('pageSize', filter.pageSize?.toString() ?? '20');
    if (filter.supplierId) params = params.set('supplierId', filter.supplierId);
    if (filter.status)     params = params.set('status',     filter.status);
    if (filter.dateFrom)   params = params.set('dateFrom',   filter.dateFrom);
    if (filter.dateTo)     params = params.set('dateTo',     filter.dateTo);
    return this.http.get<ApiResponse<PaginatedResponse<DebitNoteListItemModel>>>(
      `https://localhost:51800/api/debit-notes`, { params });
  }

  getDebitNoteById(uuid: string): Observable<ApiResponse<DebitNoteDetailModel>> {
    return this.http.get<ApiResponse<DebitNoteDetailModel>>(`https://localhost:51800/api/debit-notes/${uuid}`);
  }

  updateDebitNoteStatus(uuid: string, req: UpdateDebitNoteStatusRequest): Observable<ApiResponse<null>> {
    return this.http.patch<ApiResponse<null>>(`https://localhost:51800/api/debit-notes/${uuid}/status`, req);
  }
}
