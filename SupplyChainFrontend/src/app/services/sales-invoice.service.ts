import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable, forkJoin } from 'rxjs';
import { map } from 'rxjs/operators';
import { environment } from '../../environments/environment';
import { ApiResponse, PaginatedResponse } from './logistics.service';

// A29 §9 — sales invoices (/api/sales-invoices, Finance): the read side the sale order's Invoices and
// Payments tabs use, and the receivables screens' raise, issue, edit and delete.

export interface SalesInvoiceListItemModel {
  uuid: string;
  invoiceNumber: string;
  saleOrderUuid: string;
  saleOrderNumber: string;
  deliveryUuid?: string | null;
  deliveryNumber?: string | null;
  partnerId: string;
  partnerName: string;
  invoiceDate: string;
  dueDate: string;
  grandTotal: number;
  amountPaid: number;
  balanceDue: number;
  /** DRAFT | ISSUED | PARTIALLY_PAID | PAID | OVERDUE | CANCELLED | CREDIT_NOTE */
  status: string;
  currencyCode: string;
}

export interface SalesInvoiceLineModel {
  lineNo: number;
  soLineUuid: string;
  variantUuid: string;
  description: string;
  quantity: number;
  unitPrice: number;
  discountPercent: number;
  taxPercent: number;
  lineTotal: number;
}

/** One customer payment applied to an invoice. */
export interface SalesInvoicePaymentModel {
  allocationUuid: string;
  paymentUuid: string;
  paymentNumber: string;
  paymentDate: string;
  paymentMethod: string;
  paymentStatus: string;
  allocatedAmount: number;
  allocatedAt: string;
  allocatedBy: number;
}

export interface SalesInvoiceDetailModel extends SalesInvoiceListItemModel {
  traceId: string;
  subtotal: number;
  discountAmount: number;
  taxAmount: number;
  notes?: string | null;
  createdDate: string;
  lines: SalesInvoiceLineModel[];
  payments: SalesInvoicePaymentModel[];
}

export interface SalesInvoiceFilter {
  partnerId?: string;
  saleOrderUuid?: string;
  status?: string;
  dateFrom?: string;
  dateTo?: string;
  search?: string;
  page?: number;
  pageSize?: number;
}

/** POST /api/sales-invoices. `alreadyExisted` means the delivery had an invoice and this is it, not a second one. */
export interface SalesInvoiceCreatedModel {
  invoiceUuid: string;
  invoiceNumber: string;
  grandTotal: number;
  currencyCode: string;
  alreadyExisted: boolean;
}

/** POST /api/sales-invoices/{id}/issue. `partnerBalance` is what the customer owes once the invoice is booked. */
export interface SalesInvoiceIssuedModel {
  invoiceUuid: string;
  invoiceNumber: string;
  status: string;
  grandTotal: number;
  partnerBalance: number;
}

/** PUT /api/sales-invoices/{id}, a DRAFT only. Nothing else about an invoice can be changed. */
export interface UpdateSalesInvoiceRequest {
  /** yyyy-MM-dd */
  dueDate: string;
  notes?: string;
}

/** The statuses an invoice can be paid from: it has been issued and still has something owing. */
export const PAYABLE_INVOICE_STATUSES = ['ISSUED', 'PARTIALLY_PAID', 'OVERDUE'];

/** The invoices a payment can be applied to, oldest first, and whether there were more than one page of them. */
export interface OpenInvoices {
  invoices: SalesInvoiceListItemModel[];
  /** True when a status had more invoices than the 100 one page holds, so this is not all of them. */
  truncated: boolean;
}

@Injectable({ providedIn: 'root' })
export class SalesInvoiceService {
  private readonly baseUrl = `${environment.apiUrl}/sales-invoices`;

  constructor(private http: HttpClient) {}

  /** Raises a DRAFT invoice for a delivered delivery, or returns the one it already has. */
  createFromDelivery(deliveryUuid: string): Observable<ApiResponse<SalesInvoiceCreatedModel>> {
    return this.http.post<ApiResponse<SalesInvoiceCreatedModel>>(this.baseUrl, { deliveryUuid });
  }

  /** DRAFT to ISSUED: books the receivable on the customer's ledger. */
  issueInvoice(uuid: string): Observable<ApiResponse<SalesInvoiceIssuedModel>> {
    return this.http.post<ApiResponse<SalesInvoiceIssuedModel>>(`${this.baseUrl}/${uuid}/issue`, {});
  }

  updateInvoice(uuid: string, req: UpdateSalesInvoiceRequest): Observable<ApiResponse> {
    return this.http.put<ApiResponse>(`${this.baseUrl}/${uuid}`, req);
  }

  deleteInvoice(uuid: string): Observable<ApiResponse> {
    return this.http.delete<ApiResponse>(`${this.baseUrl}/${uuid}`);
  }

  /** Files the invoice's PDF as it stands now as an attachment on the invoice. */
  attachPdf(uuid: string): Observable<ApiResponse<{ alreadyStored: boolean }>> {
    return this.http.post<ApiResponse<{ alreadyStored: boolean }>>(`${this.baseUrl}/${uuid}/attach-pdf`, {});
  }

  /**
   * A customer's invoices that can still be paid, oldest first. The list endpoint filters by one status
   * at a time, so this asks for each payable status and joins them. `currencyCode` keeps only that
   * currency: a payment is only ever applied to invoices in its own.
   */
  getOpenInvoices(partnerId: string, currencyCode?: string): Observable<OpenInvoices> {
    const pageSize = 100;
    return forkJoin(PAYABLE_INVOICE_STATUSES.map(status => this.getInvoices({ partnerId, status, pageSize }))).pipe(
      map(pages => {
        const invoices = pages
          .flatMap(p => p.result?.data ?? [])
          .filter(i => i.balanceDue > 0 && (!currencyCode || i.currencyCode === currencyCode))
          .sort((a, b) => a.invoiceDate.localeCompare(b.invoiceDate) || a.invoiceNumber.localeCompare(b.invoiceNumber));
        const truncated = pages.some(p => (p.result?.totalRecords ?? 0) > (p.result?.data?.length ?? 0));
        return { invoices, truncated };
      })
    );
  }

  getInvoices(filter: SalesInvoiceFilter = {}): Observable<ApiResponse<PaginatedResponse<SalesInvoiceListItemModel>>> {
    let params = new HttpParams();
    if (filter.partnerId)     params = params.set('partnerId',     filter.partnerId);
    if (filter.saleOrderUuid) params = params.set('saleOrderUuid', filter.saleOrderUuid);
    if (filter.status)        params = params.set('status',        filter.status);
    if (filter.dateFrom)      params = params.set('dateFrom',      filter.dateFrom);
    if (filter.dateTo)        params = params.set('dateTo',        filter.dateTo);
    if (filter.search)        params = params.set('search',        filter.search);
    params = params.set('page',     String(filter.page     ?? 1));
    params = params.set('pageSize', String(filter.pageSize ?? 20));
    return this.http.get<ApiResponse<PaginatedResponse<SalesInvoiceListItemModel>>>(this.baseUrl, { params });
  }

  /** The invoice with its lines and the payments applied to it. 404 when it does not exist. */
  getInvoice(uuid: string): Observable<ApiResponse<SalesInvoiceDetailModel>> {
    return this.http.get<ApiResponse<SalesInvoiceDetailModel>>(`${this.baseUrl}/${uuid}`);
  }

  /** The invoice as a PDF. */
  downloadPdf(uuid: string): Observable<Blob> {
    return this.http.get(`${this.baseUrl}/${uuid}/pdf`, { responseType: 'blob' });
  }
}
