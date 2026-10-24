import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, of } from 'rxjs';
import { catchError, map } from 'rxjs/operators';
import { environment } from '../../environments/environment';

// A45 (ADDENDUM-45-ANALYSIS.md Part B) + A46 (ADDENDUM-46-ANALYSIS.md E-9) — PO line auto-pricing from the supplier pricelist.

/** Mirrors the backend PriceSource enum (BR-05/06/17; 4 = BlanketOrder, A46 E-7 — read only, never sent by the UI). */
export enum PriceSource { Manual = 0, Pricelist = 1, Override = 2, Rfq = 3, BlanketOrder = 4 }

export const PRICE_SOURCE_LABELS: Record<number, string> = {
  [PriceSource.Manual]: 'Manual',
  [PriceSource.Pricelist]: 'Pricelist',
  [PriceSource.Override]: 'Override',
  [PriceSource.Rfq]: 'RFQ',
  [PriceSource.BlanketOrder]: 'Blanket'
};

export interface ResolvePriceRequest {
  variantUuid: string;
  productUuid?: string;
  supplierUuid: string;
  quantity: number;
  /** yyyy-MM-dd — the PO's order date (A46 C1), not today. */
  orderDate: string;
  /** yyyy-MM-dd */
  expectedDeliveryDate?: string;
  /** A45 name for the PO currency; the server accepts both and `poCurrencyId` wins (A46 E-9). */
  currencyId?: string;
  /** A46 C10 — the PO currency the price must be expressed in. */
  poCurrencyId?: string;
}

export interface ResolvePriceResult {
  found: boolean;
  unitPrice?: number | null;
  currencyId?: string | null;
  currencyCode?: string | null;
  supplierPricelistUuid?: string | null;
  dateBasisUsed?: 'OrderDate' | 'DeliveryDate' | null;
  effectiveDateUsed?: string | null;
  leadTimeDays?: number | null;
  warningMessage?: string | null;
  // A46 C10 — set when the pricelist row was in another currency than the PO's.
  originalPrice?: number | null;
  originalCurrencyId?: string | null;
  originalCurrencyCode?: string | null;
  exchangeRateUsed?: number | null;
  exchangeRateDate?: string | null;
  currencyConverted?: boolean | null;
  /** True when a price exists but no exchange rate could convert it: found, unitPrice null. */
  currencyMismatchWarning?: boolean | null;
}

/** What the UI keeps about a converted price (tooltip + info icon). */
export interface PriceConversionInfo {
  code?: string | null;
  price?: number | null;
  rate?: number | null;
  date?: string | null;
}

/** A46 C10 tooltip: "Original: {code} {price} @ {rate} ({date})". Missing pieces are left out. */
export function conversionTooltip(c: PriceConversionInfo | null | undefined): string {
  if (!c) return '';
  const num = (n: number | null | undefined, max: number) =>
    n == null ? '' : Number(n).toLocaleString('en-US', { minimumFractionDigits: 0, maximumFractionDigits: max, useGrouping: false });
  const head = [c.code, num(c.price, 4)].filter(x => !!x).join(' ');
  const rate = c.rate != null ? ` @ ${num(c.rate, 8)}` : '';
  const date = c.date ? ` (${c.date.slice(0, 10)})` : '';
  return `Original: ${head}${rate}${date}`;
}

/** A46 C10 banner for a price that could not be converted. */
export function currencyMismatchText(from: string | null | undefined, to: string | null | undefined, date: string): string {
  return `No exchange rate available for ${from || '?'} → ${to || '?'} on ${date}. Enter price manually.`;
}

/** A46 C3 — prices show at 2dp in grids; the full (up to 4dp) value is the tooltip / input. */
export function price2(n: number | null | undefined): string {
  return n == null ? '' : Number(n).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}
export function price4(n: number | null | undefined): string {
  return n == null ? '' : Number(n).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 4 });
}
/** Prices are compared at their stored precision (4dp, A46 E-6). */
export function samePrice(a: number | null | undefined, b: number | null | undefined): boolean {
  return Math.round(Number(a ?? 0) * 10000) === Math.round(Number(b ?? 0) * 10000);
}

/** A46 C5 / BR-30 — PR → PO conversion batch summary. */
export interface PricingSummary {
  totalLines: number;
  pricedCount: number;
  unpricedCount: number;
  warnings?: string[];
}

@Injectable({ providedIn: 'root' })
export class PurchasePricingService {
  private readonly baseUrl = `${environment.apiUrl}/purchase`;

  constructor(private http: HttpClient) {}

  /** POST /api/purchase/resolve-price. Emits null when the call fails — resolution must never block a PO. */
  resolvePrice(req: ResolvePriceRequest): Observable<ResolvePriceResult | null> {
    return this.http.post<{ success?: boolean; result?: ResolvePriceResult; data?: ResolvePriceResult }>(
      `${this.baseUrl}/resolve-price`, req
    ).pipe(
      map(res => (res?.result ?? res?.data ?? null) as ResolvePriceResult | null),
      catchError(() => of(null))
    );
  }
}
