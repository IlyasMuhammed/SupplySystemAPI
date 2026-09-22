import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { ApiResponse, PaginatedResponse } from './logistics.service';

// A29 §10 — what a customer owes, entry by entry (/api/partners/{id}/ledger, Finance).

export interface CustomerLedgerEntryModel {
  uuid: string;
  partnerId: string;
  /** Position in this customer's ledger: the order the entries were posted in. */
  sequenceNo: number;
  /** The business date, which for a back-dated receipt is earlier than entries posted before it. */
  entryDate: string;
  /** INVOICE | PAYMENT | CREDIT_NOTE | DEBIT_NOTE | ADVANCE | REFUND | OPENING_BAL */
  entryType: string;
  /** e.g. SalesInvoice, CustomerPayment */
  referenceType: string;
  referenceId: string;
  referenceNumber: string;
  /** Increases what the customer owes. */
  debitAmount: number;
  /** Decreases what the customer owes. */
  creditAmount: number;
  /** What the customer owed after this entry. Negative means they are in credit. */
  runningBalance: number;
  currencyCode: string;
  narration?: string | null;
  createdBy: number;
  createdDate: string;
}

export interface CustomerLedgerFilter {
  /** yyyy-MM-dd, inclusive. */
  dateFrom?: string;
  dateTo?: string;
  page?: number;
  pageSize?: number;
}

@Injectable({ providedIn: 'root' })
export class CustomerLedgerService {
  constructor(private http: HttpClient) {}

  /** A page of the customer's ledger, newest entry first. */
  getLedger(partnerUuid: string, filter: CustomerLedgerFilter = {}): Observable<ApiResponse<PaginatedResponse<CustomerLedgerEntryModel>>> {
    let params = new HttpParams();
    if (filter.dateFrom) params = params.set('dateFrom', filter.dateFrom);
    if (filter.dateTo)   params = params.set('dateTo',   filter.dateTo);
    params = params.set('page',     String(filter.page     ?? 1));
    params = params.set('pageSize', String(filter.pageSize ?? 20));
    return this.http.get<ApiResponse<PaginatedResponse<CustomerLedgerEntryModel>>>(
      `${environment.apiUrl}/partners/${partnerUuid}/ledger`, { params });
  }
}
