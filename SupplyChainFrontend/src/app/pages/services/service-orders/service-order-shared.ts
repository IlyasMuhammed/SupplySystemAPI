import { Observable, of } from 'rxjs';
import { catchError, map } from 'rxjs/operators';
import { BusinessPartnerService } from '../../../services/business-partner.service';

/** A36 — helpers shared by the service order pages. */

export const SERVICE_ORDERS_ROUTE = '/portal/pages/services/service-orders';

/** Local calendar date as yyyy-MM-dd (no UTC shift). */
export function ymd(d: Date | null | undefined): string | undefined {
  if (!d) return undefined;
  const p = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
}

/** Local time as HH:mm. */
export function hhmm(d: Date | null | undefined): string | undefined {
  if (!d) return undefined;
  const p = (n: number) => String(n).padStart(2, '0');
  return `${p(d.getHours())}:${p(d.getMinutes())}`;
}

/** "yyyy-MM-dd…" → a local Date at midnight (null when absent). */
export function fromYmd(s: string | null | undefined): Date | null {
  if (!s) return null;
  const [y, m, d] = s.substring(0, 10).split('-').map(Number);
  return y && m && d ? new Date(y, m - 1, d) : null;
}

/** "HH:mm[:ss]" → a Date today at that time (null when absent). */
export function fromHhmm(s: string | null | undefined): Date | null {
  if (!s) return null;
  const [h, m] = s.split(':').map(Number);
  if (isNaN(h) || isNaN(m)) return null;
  const d = new Date();
  d.setHours(h, m, 0, 0);
  return d;
}

export interface PartnerOption { label: string; value: string; }

/** Active customers for a searchable dropdown (first 200 by name; the dropdown filters client-side). */
export function customerOptions$(partners: BusinessPartnerService): Observable<PartnerOption[]> {
  return partners.getPartners({ isCustomer: true, active: true, pageSize: 200 }).pipe(
    map(res => (res?.result?.data ?? [])
      .filter(p => !!p.uuid)
      .map(p => ({ label: p.partnerCode ? `${p.companyName} (${p.partnerCode})` : p.companyName, value: p.uuid! }))),
    catchError(() => of([]))
  );
}

export function serverMessage(err: any, fallback: string): string {
  return err?.error?.message || fallback;
}
