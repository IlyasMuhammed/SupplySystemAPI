import { CURRENCY_RATE_DECIMALS, CurrencyRateModel, OrgCurrencyModel, isOpenEnd } from '../../services/org-currency.service';
import { fromIsoDate, toIsoDate } from './finance-setup.shared';

// A35 — what the Currencies, Exchange Rates and Currency Configuration settings pages share: the rules the server enforces
// (docs/multi-currency/API-CONTRACT.md §1/§2 error texts), checked here first so the person sees the problem before
// saving. Server: SMS.Modules.Finance currency / currency-rate services — keep the two in step.

export const MAX_CURRENCY_NAME = 60;
export const MAX_CURRENCY_SYMBOL = 5;
export const MAX_RATE_NOTES = 200;
/** The server's maximum is 99,999,999.9999999999 (decimal(18,10)); as a JS number that is 1e8, so "too large" = ≥ 1e8. */
const RATE_LIMIT = 100_000_000;

export const SYMBOL_POSITIONS = [
  { label: 'Before the amount ($100)', value: 'before' },
  { label: 'After the amount (100 CHF)', value: 'after' }
];

const DOMAIN_ORDER = ['SALE', 'PURCHASE', 'SERVICE'];
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

export function domainLabel(domain: string): string {
  return (domain ?? '').toLowerCase();
}

/** "a, b and c". */
function joinWords(words: string[]): string {
  if (words.length <= 1) return words.join('');
  return `${words.slice(0, -1).join(', ')} and ${words[words.length - 1]}`;
}

/** Why a currency cannot be deactivated (the 🔒 tooltip), or null when nothing holds it. */
export function currencyLockText(c: OrgCurrencyModel): string | null {
  const parts: string[] = [];
  const domains = [...(c.baseFor ?? [])].map(d => String(d).toUpperCase())
    .sort((a, b) => DOMAIN_ORDER.indexOf(a) - DOMAIN_ORDER.indexOf(b));
  if (domains.length) parts.push(`Used as ${joinWords(domains.map(domainLabel))} base currency`);
  if (c.isRateCurrency) parts.push('the rate currency (rate 1.0)');
  return parts.length ? parts.join('; ') : null;
}

/** True when a typed number has at most `places` decimals (float noise forgiven, as finance-setup.shared does). */
export function withinDecimals(value: number, places: number): boolean {
  const rounded = Number(value.toFixed(places));
  return Math.abs(value - rounded) <= Math.abs(value) * Number.EPSILON * 8;
}

/** '2026-10-07' → '7 Oct 2026'. */
export function dayText(iso: string | Date | null | undefined): string {
  const d = iso instanceof Date ? iso : fromIsoDate(iso ?? '');
  if (!d) return '';
  return `${d.getDate()} ${MONTHS[d.getMonth()]} ${d.getFullYear()}`;
}

/** 278.05 → '278.05' (up to 10 decimals, no exponent). */
export function formatCurrencyRate(rate: number | null | undefined, decimals = CURRENCY_RATE_DECIMALS): string {
  if (rate === null || rate === undefined || isNaN(rate)) return '';
  return rate.toFixed(decimals).replace(/\.?0+$/, '');
}

// ── Currencies (§1) ─────────────────────────────────────────────────────────

export interface OrgCurrencyDraft {
  code: string;
  name: string;
  symbol: string;
  decimalPlaces: number | null;
  rounding: number | null;
  symbolPosition: string;
  displayOrder: number | null;
  isActive: boolean;
}

/**
 * The first thing the server would refuse about this draft; null when it would accept it.
 * `needsNameAndSymbol`: a new code the global catalog does not have (the server takes them from the catalog otherwise).
 */
export function orgCurrencyProblem(
  draft: OrgCurrencyDraft, others: OrgCurrencyModel[], editingId: string | null, needsNameAndSymbol: boolean
): string | null {
  const code = (draft.code ?? '').trim().toUpperCase();
  if (code.length !== 3) return 'Currency code must be exactly 3 characters.';
  if (!/^[A-Z]{3}$/.test(code)) return 'Currency code must be letters A–Z.';
  if (!editingId && others.some(o => (o.code ?? '').toUpperCase() === code)) return `Currency ${code} already exists.`;

  const name = (draft.name ?? '').trim();
  const symbol = (draft.symbol ?? '').trim();
  if ((needsNameAndSymbol && !name) || name.length > MAX_CURRENCY_NAME) return `Name is required (max ${MAX_CURRENCY_NAME}).`;
  if ((needsNameAndSymbol && !symbol) || [...symbol].length > MAX_CURRENCY_SYMBOL) return `Symbol is required (max ${MAX_CURRENCY_SYMBOL}).`;

  const dp = draft.decimalPlaces;
  if (dp === null || dp === undefined || isNaN(dp) || dp < 0 || dp > 3 || Math.trunc(dp) !== dp) {
    return 'Decimal places must be between 0 and 3.';
  }
  if (draft.rounding === null || draft.rounding === undefined || isNaN(draft.rounding) || draft.rounding <= 0) {
    return 'Rounding must be greater than 0.';
  }
  if (draft.symbolPosition !== 'before' && draft.symbolPosition !== 'after') return "Symbol position must be 'before' or 'after'.";

  if (editingId && !draft.isActive) {
    const current = others.find(o => o.currencyId === editingId);
    const domains = [...(current?.baseFor ?? [])].map(d => String(d).toUpperCase())
      .sort((a, b) => DOMAIN_ORDER.indexOf(a) - DOMAIN_ORDER.indexOf(b));
    if (domains.length) return `Cannot deactivate — used as ${domainLabel(domains[0])} base currency.`;
    if (current?.isRateCurrency) return "Cannot deactivate — it is the organization's rate currency.";
  }
  return null;
}

// ── Exchange rates (§2) ─────────────────────────────────────────────────────

export interface RateDraft {
  currencyId: string | null;
  rate: number | null;
  effectiveFrom: Date | null;
  /** True: current (open-ended, to 9999-12-31). False: fixed end `effectiveTo`. */
  openEnded: boolean;
  effectiveTo: Date | null;
  notes: string;
}

/**
 * The first thing the server would refuse about this rate; null when it would accept it. `rates` = the currency's rows
 * (others are ignored). A new open-ended rate closes the current one when it starts after it (BR-C2-03); anything else
 * must not overlap another row (BR-C2-02).
 */
export function rateProblem(
  draft: RateDraft, rates: CurrencyRateModel[], editingId: string | null, rateCurrencyId: string | null
): string | null {
  if (!draft.currencyId) return 'Choose the currency.';
  if (rateCurrencyId && draft.currencyId === rateCurrencyId) return 'Base currency rate cannot be modified.';

  const rate = draft.rate;
  if (rate === null || rate === undefined || isNaN(rate)) return 'Enter the rate.';
  if (rate <= 0) return 'Rate must be positive.';
  if (rate >= RATE_LIMIT) return 'The rate is too large.';
  if (!withinDecimals(rate, CURRENCY_RATE_DECIMALS)) return `A rate can have at most ${CURRENCY_RATE_DECIMALS} decimals.`;

  if (!draft.effectiveFrom) return 'Choose the date the rate applies from.';
  if (!draft.openEnded && !draft.effectiveTo) return 'Choose the last date of the rate, or make it current.';
  const from = toIsoDate(draft.effectiveFrom);
  const to = draft.openEnded ? '9999-12-31' : toIsoDate(draft.effectiveTo!);
  if (to < from) return 'Effective to must be on or after effective from.';
  if ((draft.notes ?? '').trim().length > MAX_RATE_NOTES) return `Notes can be at most ${MAX_RATE_NOTES} characters.`;

  const others = rates.filter(r => r.currencyId === draft.currencyId && r.id !== editingId);
  const overlapping = others.filter(r => {
    const rFrom = r.effectiveFrom.slice(0, 10);
    const rTo = isOpenEnd(r.effectiveTo) ? '9999-12-31' : r.effectiveTo.slice(0, 10);
    // A new current rate closes the current row when it starts after it — that row does not count.
    if (!editingId && draft.openEnded && isOpenEnd(r.effectiveTo) && rFrom < from) return false;
    return rFrom <= to && rTo >= from;
  });
  if (!overlapping.length) return null;
  const clash = overlapping.find(r => r.effectiveFrom.slice(0, 10) <= from && (isOpenEnd(r.effectiveTo) || r.effectiveTo.slice(0, 10) >= from))
    ?? overlapping[0];
  return `Rate already exists for this date (${clash.currencyCode} ${formatCurrencyRate(clash.rate)} from ${clash.effectiveFrom.slice(0, 10)}). `
    + 'Edit that rate instead.';
}

/** §11.4 banner: what a new current rate starting `from` does to the current one; null when it closes nothing. */
export function previousRateNotice(current: CurrencyRateModel | null | undefined, from: Date | null): string | null {
  if (!current || !from || !isOpenEnd(current.effectiveTo)) return null;
  const start = fromIsoDate(current.effectiveFrom);
  if (!start || start >= from) return null;
  const closeTo = new Date(from.getFullYear(), from.getMonth(), from.getDate() - 1);
  return `The previous active rate (${formatCurrencyRate(current.rate)} from ${dayText(start)}) will be closed to ${dayText(closeTo)}.`;
}

/** '0.003596 USD/PKR' — 1 / rate, units of the currency per 1 unit of the rate currency. */
export function inverseText(rate: number | null | undefined, code: string, rateCode: string): string {
  if (!rate || rate <= 0 || isNaN(rate)) return '';
  return `${formatCurrencyRate(1 / rate, 6)} ${code}/${rateCode}`;
}
