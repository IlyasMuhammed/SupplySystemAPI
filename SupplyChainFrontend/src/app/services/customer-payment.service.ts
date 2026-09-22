import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { ApiResponse, PaginatedResponse } from './logistics.service';

// A29 §9.3 and §9.4 — money received from customers (/api/customer-payments, Finance): recording it,
// and applying what is left of it to invoices.

/** How a customer paid. The server accepts exactly these. */
export const CUSTOMER_PAYMENT_METHODS = [
  { value: 'CASH',          label: 'Cash',          icon: 'pi pi-money-bill' },
  { value: 'CHEQUE',        label: 'Cheque',        icon: 'pi pi-book' },
  { value: 'BANK_TRANSFER', label: 'Bank transfer', icon: 'pi pi-building-columns' },
  { value: 'CARD',          label: 'Card',          icon: 'pi pi-credit-card' },
  { value: 'ONLINE',        label: 'Online',        icon: 'pi pi-globe' }
] as const;

/** One line of a manual allocation: pay this much of this invoice. */
export interface ManualPaymentAllocation {
  invoiceUuid: string;
  amount: number;
}

/**
 * POST /api/customer-payments. Leave `allocations` out and the money goes to the customer's oldest unpaid
 * invoices in the same currency; give a list, even an empty one, and it is applied exactly as written,
 * whatever it does not name staying on the customer's account.
 */
export interface RecordCustomerPaymentRequest {
  partnerId: string;
  amount: number;
  method: string;
  /** The currency the money arrived in, as the Lookups catalog spells it. */
  currencyCode: string;
  /** yyyy-MM-dd. Today when left out. */
  paymentDate?: string;
  /** Required for CHEQUE. */
  chequeNumber?: string;
  bankReference?: string;
  notes?: string;
  allocations?: ManualPaymentAllocation[];
}

/** One invoice a payment was just applied to, with where the invoice stands now. */
export interface AppliedPaymentAllocationModel {
  invoiceUuid: string;
  invoiceNumber: string;
  amount: number;
  balanceDue: number;
  invoiceStatus: string;
}

export interface CustomerPaymentRecordedModel {
  paymentUuid: string;
  paymentNumber: string;
  amount: number;
  allocatedAmount: number;
  /** Received but not applied to any invoice: held on the customer's account. */
  unallocatedAmount: number;
  currencyCode: string;
  allocations: AppliedPaymentAllocationModel[];
  /** What the customer owes after this payment. Negative means they are in credit. */
  partnerBalance: number;
}

export interface CustomerPaymentAllocatedModel {
  paymentUuid: string;
  paymentNumber: string;
  amount: number;
  /** Applied to invoices in all: what was applied before, plus this call. */
  allocatedAmount: number;
  unallocatedAmount: number;
  /** Only what this call applied. */
  allocations: AppliedPaymentAllocationModel[];
}

export interface CustomerPaymentFilter {
  partnerId?: string;
  /** RECEIVED | BOUNCED | REVERSED */
  status?: string;
  method?: string;
  dateFrom?: string;
  dateTo?: string;
  /** True keeps only payments with money still on account, the ones worth applying. */
  unallocated?: boolean;
  search?: string;
  page?: number;
  pageSize?: number;
}

export interface CustomerPaymentListItemModel {
  uuid: string;
  paymentNumber: string;
  partnerId: string;
  partnerName: string;
  paymentDate: string;
  amount: number;
  allocatedAmount: number;
  unallocatedAmount: number;
  paymentMethod: string;
  chequeNumber?: string | null;
  bankReference?: string | null;
  currencyCode: string;
  /** RECEIVED | BOUNCED | REVERSED */
  status: string;
}

export interface CustomerPaymentAllocationModel {
  allocationUuid: string;
  invoiceUuid: string;
  invoiceNumber: string;
  allocatedAmount: number;
  allocatedAt: string;
  allocatedBy: number;
  invoiceBalanceDue: number;
  invoiceStatus: string;
}

export interface CustomerPaymentDetailModel extends CustomerPaymentListItemModel {
  notes?: string | null;
  createdBy: number;
  createdDate: string;
  modifiedBy?: number | null;
  modifiedDate?: string | null;
  allocations: CustomerPaymentAllocationModel[];
}

@Injectable({ providedIn: 'root' })
export class CustomerPaymentService {
  private readonly baseUrl = `${environment.apiUrl}/customer-payments`;

  constructor(private http: HttpClient) {}

  recordPayment(req: RecordCustomerPaymentRequest): Observable<ApiResponse<CustomerPaymentRecordedModel>> {
    return this.http.post<ApiResponse<CustomerPaymentRecordedModel>>(this.baseUrl, req);
  }

  getPayments(filter: CustomerPaymentFilter = {}): Observable<ApiResponse<PaginatedResponse<CustomerPaymentListItemModel>>> {
    let params = new HttpParams();
    if (filter.partnerId)              params = params.set('partnerId',   filter.partnerId);
    if (filter.status)                 params = params.set('status',      filter.status);
    if (filter.method)                 params = params.set('method',      filter.method);
    if (filter.dateFrom)               params = params.set('dateFrom',    filter.dateFrom);
    if (filter.dateTo)                 params = params.set('dateTo',      filter.dateTo);
    if (filter.unallocated !== undefined) params = params.set('unallocated', String(filter.unallocated));
    if (filter.search)                 params = params.set('search',      filter.search);
    params = params.set('page',     String(filter.page     ?? 1));
    params = params.set('pageSize', String(filter.pageSize ?? 20));
    return this.http.get<ApiResponse<PaginatedResponse<CustomerPaymentListItemModel>>>(this.baseUrl, { params });
  }

  /** The payment with everything it was applied to. 404 when it does not exist. */
  getPayment(uuid: string): Observable<ApiResponse<CustomerPaymentDetailModel>> {
    return this.http.get<ApiResponse<CustomerPaymentDetailModel>>(`${this.baseUrl}/${uuid}`);
  }

  /** Applies what is left of a received payment: oldest invoice first with no allocations, exactly as written with some. */
  allocatePayment(uuid: string, allocations?: ManualPaymentAllocation[]): Observable<ApiResponse<CustomerPaymentAllocatedModel>> {
    return this.http.post<ApiResponse<CustomerPaymentAllocatedModel>>(
      `${this.baseUrl}/${uuid}/allocate`, allocations ? { allocations } : {});
  }
}
