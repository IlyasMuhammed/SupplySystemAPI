import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable, expand, map, reduce, EMPTY } from 'rxjs';
import { ApiResponse, PaginatedResponse } from './inventory.service';
import { environment } from '../../environments/environment';

// A37 D-13 — the Customer Master: a facade over business partners with IsCustomer (docs/module-registry/API-CONTRACT.md §5).

export type CustomerType = 'WALK_IN' | 'INDIVIDUAL' | 'COMPANY' | 'EMPLOYEE';

export const CUSTOMER_TYPE_OPTIONS: { label: string; value: CustomerType }[] = [
  { label: 'Company',    value: 'COMPANY' },
  { label: 'Individual', value: 'INDIVIDUAL' },
  { label: 'Employee',   value: 'EMPLOYEE' },
  { label: 'Walk-in',    value: 'WALK_IN' }
];

export function customerTypeLabel(type: string | null | undefined): string {
  return CUSTOMER_TYPE_OPTIONS.find(o => o.value === type)?.label ?? (type || '—');
}

export interface CustomerListItem {
  uuid: string;
  code: string;
  name: string;
  customerType: CustomerType;
  phone?: string | null;
  mobile?: string | null;
  email?: string | null;
  creditLimit: number;
  balance: number;
  currencyCode?: string | null;
  isActive: boolean;
  isSystem: boolean;
}

export interface CustomerDetail extends CustomerListItem {
  taxId?: string | null;
  addressLine1?: string | null;
  addressLine2?: string | null;
  city?: string | null;
  provinceState?: string | null;
  postalCode?: string | null;
  country?: string | null;
  paymentTermsDays: number;
  defaultSaleCurrencyId?: string | null;
  notes?: string | null;
  isVendor: boolean;
  createdAt: string;
  modifiedAt?: string | null;
  /** Sent back on PUT when the server has one (contract §5). */
  rowVersion?: string | null;
}

export interface CustomerUpsert {
  name: string;
  customerType: CustomerType;
  phone?: string | null;
  mobile?: string | null;
  email?: string | null;
  taxId?: string | null;
  /** Ignored by the server without FEATURE_CREDIT_MANAGEMENT; the form leaves it out then. */
  creditLimit?: number | null;
  paymentTermsDays?: number | null;
  defaultSaleCurrencyId?: string | null;
  addressLine1?: string | null;
  addressLine2?: string | null;
  city?: string | null;
  provinceState?: string | null;
  postalCode?: string | null;
  country?: string | null;
  notes?: string | null;
  rowVersion?: string | null;
}

export type CustomerSortField = 'code' | 'name' | 'customerType' | 'creditLimit' | 'balance' | 'status';

export interface CustomerFilter {
  search?: string;
  type?: CustomerType | '';
  status?: 'ACTIVE' | 'INACTIVE' | '';
  page?: number;
  pageSize?: number;
  sortField?: CustomerSortField | null;
  /** asc | desc */
  sortOrder?: 'asc' | 'desc' | null;
}

export interface CustomerBalance {
  balance: number;
  currencyCode?: string | null;
  overdue: number;
}

@Injectable({ providedIn: 'root' })
export class CustomerService {
  private readonly base = `${environment.apiUrl}/customers`;

  constructor(private http: HttpClient) {}

  getCustomers(filter: CustomerFilter = {}): Observable<ApiResponse<PaginatedResponse<CustomerListItem>>> {
    let params = new HttpParams()
      .set('page', String(filter.page ?? 1))
      .set('pageSize', String(filter.pageSize ?? 25));
    if (filter.search)    params = params.set('search', filter.search);
    if (filter.type)      params = params.set('type', filter.type);
    if (filter.status)    params = params.set('status', filter.status);
    if (filter.sortField) params = params.set('sortField', filter.sortField);
    if (filter.sortField && filter.sortOrder) params = params.set('sortOrder', filter.sortOrder);
    return this.http.get<ApiResponse<PaginatedResponse<CustomerListItem>>>(this.base, { params });
  }

  /** Every customer matching the filter (all pages), for the export. */
  getAllCustomers(filter: CustomerFilter, pageSize = 200): Observable<CustomerListItem[]> {
    const page = (n: number) => this.getCustomers({ ...filter, page: n, pageSize });
    let n = 1;
    return page(n).pipe(
      expand(res => {
        const r = res.result;
        const totalPages = r?.totalPages ?? Math.ceil((r?.totalRecords ?? 0) / pageSize);
        return r && n < totalPages && (r.data?.length ?? 0) > 0 ? page(++n) : EMPTY;
      }),
      map(res => res.result?.data ?? []),
      reduce((all, rows) => all.concat(rows), [] as CustomerListItem[])
    );
  }

  getCustomer(uuid: string): Observable<ApiResponse<CustomerDetail>> {
    return this.http.get<ApiResponse<CustomerDetail>>(`${this.base}/${uuid}`);
  }

  createCustomer(req: CustomerUpsert): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(this.base, req);
  }

  updateCustomer(uuid: string, req: CustomerUpsert): Observable<ApiResponse<unknown>> {
    return this.http.put<ApiResponse<unknown>>(`${this.base}/${uuid}`, req);
  }

  setStatus(uuid: string, isActive: boolean): Observable<ApiResponse<unknown>> {
    return this.http.patch<ApiResponse<unknown>>(`${this.base}/${uuid}/status`, { isActive });
  }

  search(q: string, limit = 10): Observable<ApiResponse<CustomerListItem[]>> {
    const params = new HttpParams().set('q', q).set('limit', String(limit));
    return this.http.get<ApiResponse<CustomerListItem[]>>(`${this.base}/search`, { params });
  }

  getBalance(uuid: string): Observable<ApiResponse<CustomerBalance>> {
    return this.http.get<ApiResponse<CustomerBalance>>(`${this.base}/${uuid}/balance`);
  }
}

/** CSV of customer rows (RFC 4180 quoting), first line = headers. */
export function customersCsv(rows: readonly CustomerListItem[]): string {
  const cell = (v: unknown) => {
    const s = v == null ? '' : String(v);
    return /[",\r\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
  };
  const header = ['Code', 'Name', 'Type', 'Phone', 'Mobile', 'Email', 'Credit limit', 'Balance', 'Currency', 'Status'];
  const lines = rows.map(r => [
    r.code, r.name, customerTypeLabel(r.customerType), r.phone, r.mobile, r.email,
    r.creditLimit, r.balance, r.currencyCode, r.isActive ? 'Active' : 'Inactive'
  ].map(cell).join(','));
  return [header.join(','), ...lines].join('\r\n');
}
