import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { map } from 'rxjs/operators';
import { environment } from '../../environments/environment';
import {
  PricelistFilter, PricelistItem, PricelistPage, PricelistSaveResult, PricelistUpsert
} from './supplier-pricelist.models';

export * from './supplier-pricelist.models';

/** The platform envelope. The server serialises `Result`; the contract text calls it `data`, so both are read. */
interface Envelope<T> { success?: boolean; message?: string; result?: T; data?: T; }

function unwrap<T>(r: Envelope<T> | null | undefined): T | undefined {
  return r?.result ?? r?.data;
}

/** A45 (C4/C6) — supplier pricelist CRUD under /api/purchase/supplier-pricelists (rows identified by uuid, D-2). */
@Injectable({ providedIn: 'root' })
export class SupplierPricelistService {
  private readonly base = `${environment.apiUrl}/purchase/supplier-pricelists`;

  constructor(private http: HttpClient) {}

  list(filter: PricelistFilter = {}): Observable<PricelistPage> {
    let params = new HttpParams();
    if (filter.productUuid)  params = params.set('productUuid', filter.productUuid);
    if (filter.variantUuid)  params = params.set('variantUuid', filter.variantUuid);
    if (filter.supplierUuid) params = params.set('supplierUuid', filter.supplierUuid);
    if (filter.activeOnly != null) params = params.set('activeOnly', String(filter.activeOnly));
    if (filter.search?.trim()) params = params.set('search', filter.search.trim());
    params = params.set('page', String(filter.page ?? 1)).set('pageSize', String(filter.pageSize ?? 20));
    return this.http.get<Envelope<unknown>>(this.base, { params }).pipe(map(r => {
      const body = unwrap(r) as any;
      if (Array.isArray(body)) return { items: body as PricelistItem[], total: body.length };
      const items: PricelistItem[] = body?.items ?? body?.data ?? [];
      return { items, total: body?.totalRecords ?? body?.total ?? body?.totalCount ?? items.length };
    }));
  }

  get(uuid: string): Observable<PricelistItem> {
    return this.http.get<Envelope<PricelistItem>>(`${this.base}/${uuid}`).pipe(map(r => unwrap(r) as PricelistItem));
  }

  create(body: PricelistUpsert): Observable<PricelistSaveResult> {
    return this.http.post<Envelope<PricelistSaveResult>>(this.base, body).pipe(map(r => unwrap(r) as PricelistSaveResult));
  }

  /** `body.rowVersion` is required — a stale row answers 409. */
  update(uuid: string, body: PricelistUpsert): Observable<PricelistSaveResult> {
    return this.http.put<Envelope<PricelistSaveResult>>(`${this.base}/${uuid}`, body).pipe(map(r => unwrap(r) as PricelistSaveResult));
  }

  /** Soft delete: the row stays, inactive (D-9). */
  deactivate(uuid: string): Observable<void> {
    return this.http.delete<Envelope<unknown>>(`${this.base}/${uuid}`).pipe(map(() => undefined));
  }
}
