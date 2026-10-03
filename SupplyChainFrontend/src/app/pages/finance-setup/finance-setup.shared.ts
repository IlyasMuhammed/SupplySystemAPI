import { HttpErrorResponse } from '@angular/common/http';

import { ExchangeRateModel, TaxCodeModel, TaxCodeUsage } from '../../services/finance-setup.service';

// What the Tax Codes and Exchange Rates settings pages share: the permission, the rules the server
// enforces (checked here first so the person sees the problem before saving), and date/number text.
// Server: SMS.Modules.Finance TaxCodeService / ExchangeRateService — keep the two in step.

/**
 * Writes need this; reads need only a sign-in. The routes and menu entries open for this or INVOICE_VIEW,
 * and without this the pages are read-only.
 */
export const FINANCE_SETUP_MANAGE = 'FINANCE_SETUP_MANAGE';

export const MAX_TAX_CODE_LENGTH = 20;
export const MAX_TAX_NAME_LENGTH = 100;
export const MAX_TAX_DESCRIPTION_LENGTH = 300;
export const MAX_RATE_NOTES_LENGTH = 300;
export const RATE_DECIMALS = 8;
export const MAX_EXCHANGE_RATE = 9_999_999_999.99999999;

export const USAGE_OPTIONS: { label: string; value: TaxCodeUsage }[] = [
  { label: 'Sales', value: 'SALES' },
  { label: 'Purchases', value: 'PURCHASE' },
  { label: 'Sales & purchases', value: 'BOTH' }
];

export function usageLabel(usage: TaxCodeUsage | string): string {
  return USAGE_OPTIONS.find(o => o.value === usage)?.label ?? usage;
}

/** True when a code of this usage can be picked on a SALES / PURCHASE document. */
export function usageAllows(usage: TaxCodeUsage | string, side: 'SALES' | 'PURCHASE'): boolean {
  return usage === 'BOTH' || usage === side;
}

/** Trimmed, upper-cased — what the server stores. */
export function normalizeTaxCode(code: string | null | undefined): string {
  return (code ?? '').trim().toUpperCase();
}

/** 17 → "17", 7.5 → "7.5", 0.25 → "0.25". */
export function formatPercent(rate: number | null | undefined): string {
  if (rate === null || rate === undefined || isNaN(rate)) return '';
  return String(Number(rate.toFixed(2)));
}

/** 278.5 → "278.5", 0.00359066 → "0.00359066" — up to the eight decimals a rate can have, never exponent notation. */
export function formatRate(rate: number | null | undefined): string {
  if (rate === null || rate === undefined || isNaN(rate)) return '';
  return rate.toFixed(RATE_DECIMALS).replace(/\.?0+$/, '');
}

/**
 * True when a typed number has at most `places` decimals, as the server counts them (decimal.Round(x, places) == x).
 * Float noise from arithmetic (0.57 + 1 = 1.5699999999999998, what the rate box's arrow keys produce) is forgiven —
 * the pages send the rounded value (roundRatePercent / roundExchangeRate) — but a real extra decimal is not. Comparing
 * against the rounded value scales with the number, so a big rate like 9000.3 is not mistaken for one with twelve
 * decimals (reading the digits at a fixed twelve places did that for anything above about 4,000).
 */
function withinDecimals(value: number, places: number): boolean {
  const rounded = Number(value.toFixed(places));
  return Math.abs(value - rounded) <= Math.abs(value) * Number.EPSILON * 8;
}

/** A tax % as the server keeps it: two decimals, without float noise like 1.5699999999999998. */
export function roundRatePercent(rate: number): number {
  return Number(rate.toFixed(2));
}

/** An exchange rate as the server keeps it: eight decimals, without float noise. */
export function roundExchangeRate(rate: number): number {
  return Number(rate.toFixed(RATE_DECIMALS));
}

/** A local calendar date as yyyy-MM-dd — never through UTC, which can move it to the day before. */
export function toIsoDate(date: Date): string {
  const y = date.getFullYear();
  const m = String(date.getMonth() + 1).padStart(2, '0');
  const d = String(date.getDate()).padStart(2, '0');
  return `${y}-${m}-${d}`;
}

/** yyyy-MM-dd as a local date at midnight; null when it is not one. */
export function fromIsoDate(text: string | null | undefined): Date | null {
  const match = /^(\d{4})-(\d{2})-(\d{2})/.exec(text ?? '');
  if (!match) return null;
  const date = new Date(Number(match[1]), Number(match[2]) - 1, Number(match[3]));
  return isNaN(date.getTime()) ? null : date;
}

// ── Tax codes ────────────────────────────────────────────────────────────────

export interface TaxCodeDraft {
  code: string;
  name: string;
  description: string;
  ratePercent: number | null;
  usage: TaxCodeUsage | null;
  isDefault: boolean;
  isActive: boolean;
}

/** The first thing the server would refuse about this draft, in words; null when it would accept it. */
export function taxCodeProblem(draft: TaxCodeDraft, others: TaxCodeModel[], editingUuid: string | null): string | null {
  const typed = (draft.code ?? '').trim();
  const code = normalizeTaxCode(typed);
  if (!code) return 'Give the tax code a code, e.g. GST17.';
  if (typed.length > MAX_TAX_CODE_LENGTH) return `A code can be at most ${MAX_TAX_CODE_LENGTH} characters.`;
  // Checked on the text as typed (any case): JavaScript upper-cases 'ß' to 'SS' and 'ﬁ' to 'FI', which the
  // server's ToUpperInvariant does not, so the code would silently become a different text than was typed.
  if (!/^[A-Za-z0-9_-]+$/.test(typed)) return "A code may contain only the letters A–Z, the digits 0–9, '_' and '-'.";

  const clash = others.find(o => o.uuid !== editingUuid && normalizeTaxCode(o.code) === code);
  if (clash) {
    return clash.isActive
      ? `There is already a tax code ${clash.code}.`
      : `There is already a tax code ${clash.code}, deactivated — reactivate it instead.`;
  }

  const name = (draft.name ?? '').trim();
  if (!name) return 'Give the tax code a name, e.g. "GST 17%".';
  if (name.length > MAX_TAX_NAME_LENGTH) return `The name can be at most ${MAX_TAX_NAME_LENGTH} characters.`;
  if ((draft.description ?? '').trim().length > MAX_TAX_DESCRIPTION_LENGTH) {
    return `The description can be at most ${MAX_TAX_DESCRIPTION_LENGTH} characters.`;
  }

  const rate = draft.ratePercent;
  if (rate === null || rate === undefined || isNaN(rate)) return 'Enter the rate, from 0 to 100%.';
  if (rate < 0 || rate > 100) return 'The rate must be between 0 and 100%.';
  if (!withinDecimals(rate, 2)) return 'The rate can have at most two decimals, like 17.25.';

  if (!draft.usage) return 'Choose where the code is used.';
  return null;
}

/** The codes whose default this draft would take, as the server decides it (a BOTH default competes with both sides). */
export function defaultsTakenOver(draft: TaxCodeDraft, others: TaxCodeModel[], editingUuid: string | null): TaxCodeModel[] {
  if (!draft.isDefault || !draft.isActive || !draft.usage) return [];
  const usage = draft.usage;
  return others.filter(o =>
    o.uuid !== editingUuid && o.isDefault && (o.usage === 'BOTH' || usage === 'BOTH' || o.usage === usage));
}

// ── Exchange rates ───────────────────────────────────────────────────────────

export interface ExchangeRateDraft {
  fromCurrencyCode: string | null;
  toCurrencyCode: string | null;
  rate: number | null;
  effectiveDate: Date | null;
  notes: string;
}

/** The first thing the server would refuse about this draft, in words; null when it would accept it. */
export function exchangeRateProblem(draft: ExchangeRateDraft, others: ExchangeRateModel[], editingUuid: string | null): string | null {
  const from = (draft.fromCurrencyCode ?? '').trim().toUpperCase();
  const to = (draft.toCurrencyCode ?? '').trim().toUpperCase();
  if (!from) return 'Choose the currency to convert from.';
  if (!to) return 'Choose the currency to convert to.';
  if (from === to) return 'A rate converts between two different currencies.';

  const rate = draft.rate;
  if (rate === null || rate === undefined || isNaN(rate)) return 'Enter the rate.';
  if (rate <= 0) return 'The rate must be greater than 0.';
  if (rate > MAX_EXCHANGE_RATE) return 'The rate is too large.';
  if (!withinDecimals(rate, RATE_DECIMALS)) return `The rate can have at most ${RATE_DECIMALS} decimals.`;

  if (!draft.effectiveDate) return 'Choose the date the rate applies from.';
  if ((draft.notes ?? '').trim().length > MAX_RATE_NOTES_LENGTH) return `Notes can be at most ${MAX_RATE_NOTES_LENGTH} characters.`;

  const day = toIsoDate(draft.effectiveDate);
  const clash = others.find(o => o.uuid !== editingUuid && o.fromCurrencyCode === from && o.toCurrencyCode === to && o.effectiveDate === day);
  if (clash) return `There is already a ${from} → ${to} rate for this date (${formatRate(clash.rate)}). Change that one instead.`;
  return null;
}

// ── Errors ───────────────────────────────────────────────────────────────────

/** The server's own words when it sent some (every 400/404/409 here does), else a plain fallback. */
export function setupErrorMessage(err: unknown, fallback: string): string {
  const httpError = err as Partial<HttpErrorResponse> | null | undefined;
  const body = httpError?.error as { message?: unknown } | string | null | undefined;

  if (body && typeof body === 'object' && typeof body.message === 'string' && body.message.trim()) return body.message;
  if (typeof body === 'string' && body.trim() && body.length < 300) return body;

  switch (httpError?.status) {
    case 0:   return 'The server could not be reached. Check your connection and try again.';
    case 401: return 'Your session has ended. Sign in again.';
    case 403: return 'You do not have permission to do this.';
    case 404: return 'It was not found. It may have been removed.';
    default:  return fallback;
  }
}
