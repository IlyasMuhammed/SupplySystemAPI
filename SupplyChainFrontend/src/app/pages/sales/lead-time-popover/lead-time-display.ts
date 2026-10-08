import { fromDateOnly } from '../../../shared/date-only';
import type { DeliveryDateSource } from '../../../services/sale-order.service';

// A34 PC-09 — a sales line's delivery date and where it comes from (spec §6.7, API-CONTRACT.md §5.1, D-15):
// effective = manual ?? calculated. Pure functions, shared by the popover and the pages that list lines.

export interface DeliveryDateFields {
  /** The user's own date: inquiry estimatedDeliveryDate, quotation promisedDeliveryDate, SO manualDeliveryDate. */
  manualDate?: string | null;
  calculatedDate?: string | null;
  calculatedDays?: number | null;
  /** UTC timestamp. */
  calculatedAt?: string | null;
}

export interface DeliveryDateState {
  source: DeliveryDateSource;
  /** yyyy-MM-dd, or null with neither date. */
  effective: string | null;
  /** "✎ Manual" · "⏱ Calculated" · "— Not calculated" */
  label: string;
  tooltip: string;
}

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

function day(d: Date): string {
  return `${MONTHS[d.getMonth()]} ${d.getDate()}, ${d.getFullYear()}`;
}

/** "Oct 16, 2026" for a date-only value ("2026-10-16", "2026-10-16T00:00:00", "…Z"): the day it names, no UTC shift. */
export function longDate(iso?: string | null): string {
  return iso ? day(fromDateOnly(iso)) : '—';
}

/** "Oct 3, 2026" for a UTC timestamp, in the reader's own time zone. A value without a zone is read as UTC. */
export function timestampDay(iso?: string | null): string {
  if (!iso) return '—';
  const zoned = /([zZ]|[+-]\d{2}:?\d{2})$/.test(iso) ? iso : `${iso}Z`;
  const d = new Date(zoned);
  return isNaN(d.getTime()) ? '—' : day(d);
}

function dateOnly(iso?: string | null): string | null {
  return iso ? iso.slice(0, 10) : null;
}

export function deliveryDateState(f: DeliveryDateFields): DeliveryDateState {
  const manual = dateOnly(f.manualDate);
  const calculated = dateOnly(f.calculatedDate);

  if (manual) {
    return {
      source: 'MANUAL', effective: manual, label: '✎ Manual',
      tooltip: calculated ? `Manual date — calculated was ${longDate(calculated)}` : 'Manual date — not calculated'
    };
  }
  if (calculated) {
    const days = f.calculatedDays;
    const lead = days != null ? `Lead time: ${days} day${days === 1 ? '' : 's'}` : 'Calculated delivery date';
    return {
      source: 'CALCULATED', effective: calculated, label: '⏱ Calculated',
      tooltip: f.calculatedAt ? `${lead} — calculated on ${timestampDay(f.calculatedAt)}` : lead
    };
  }
  return { source: 'NONE', effective: null, label: '— Not calculated', tooltip: 'Click ⏱ to calculate lead time' };
}
