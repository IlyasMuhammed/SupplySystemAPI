// A35 §11.5 / D-10..D-12 / D-21 — how a document's currency, its locked exchange rate and its base amounts are shown.
// Pure helpers shared by the sale order, purchase order, quotation, invoice and payment screens. Amounts themselves are
// written with FE-SET's MoneyService / `money` pipe; these only decide which amount and which currency.
// Contract: docs/multi-currency/API-CONTRACT.md §6–§7 (base fields are null until the rate is locked).

import { formatDate } from '@angular/common';

/** The currency fields a document carries (any may be missing on an older server or before the lock). */
export interface DocCurrencyInfo {
  currencyId?: string | null;
  currencyCode?: string | null;
  /** Units of the document's domain base per 1 unit of its currency; null = not locked yet (D-11). */
  exchangeRate?: number | null;
  baseCurrencyId?: string | null;
  baseCurrencyCode?: string | null;
  /** ISO UTC timestamp of the lock. */
  rateLockedAt?: string | null;
}

/** Which currency single amounts are shown in: the document's own, or the domain base ("Show in X / Show in base"). */
export type AmountView = 'DOC' | 'BASE';

/** The server's D-5 refusal: "No exchange rate for {CODE} on {yyyy-MM-dd}. Add one under Settings → Exchange Rates." */
export interface MissingRate {
  code: string;
  date: string;
  message: string;
}

const MISSING_RATE = /No exchange rate for ([A-Za-z]{3}) on (\d{4}-\d{2}-\d{2})[^\n]*/;

/** A rate with four decimals (register P3-13), e.g. "76.3000". */
export function formatRate(rate: number | null | undefined): string {
  if (rate === null || rate === undefined || typeof rate !== 'number' || isNaN(rate)) return '';
  return rate.toFixed(4);
}

export function isRateLocked(doc: DocCurrencyInfo | null | undefined): boolean {
  return doc?.exchangeRate !== null && doc?.exchangeRate !== undefined;
}

/** The document's currency is known to differ from its domain base (by id, else by code). */
export function isForeignCurrency(doc: DocCurrencyInfo | null | undefined): boolean {
  if (!doc) return false;
  const id = (doc.currencyId ?? '').toLowerCase();
  const baseId = (doc.baseCurrencyId ?? '').toLowerCase();
  if (id && baseId) return id !== baseId;
  const code = (doc.currencyCode ?? '').toUpperCase();
  const baseCode = (doc.baseCurrencyCode ?? '').toUpperCase();
  if (code && baseCode) return code !== baseCode;
  return false;
}

/** Base amounts are worth a column of their own: the rate is locked and the base is another currency. */
export function hasBaseAmounts(doc: DocCurrencyInfo | null | undefined): boolean {
  return isRateLocked(doc) && isForeignCurrency(doc);
}

/** "locked 7 Oct 2026" (the lock is a timestamp, shown in local time). */
export function lockedOnText(rateLockedAt: string | null | undefined): string {
  if (!rateLockedAt) return 'locked';
  return `locked ${formatDate(rateLockedAt, 'd MMM yyyy', 'en-US')}`;
}

/**
 * The exchange-rate line: "76.3000 (locked 7 Oct 2026)" once locked; before that, when it will be
 * (lockPoint: 'confirmation', 'approval', 'sending', 'issue', 'posting').
 */
export function rateText(doc: DocCurrencyInfo | null | undefined, lockPoint: string): string {
  if (isRateLocked(doc)) return `${formatRate(doc!.exchangeRate)} (${lockedOnText(doc!.rateLockedAt)})`;
  return `Locked at ${lockPoint}`;
}

/** The amount for the view; the document amount while no base amount exists. */
export function amountIn(view: AmountView, amount: number | null | undefined, amountBase: number | null | undefined): number | null {
  if (view === 'BASE' && amountBase !== null && amountBase !== undefined) return amountBase;
  return amount ?? null;
}

/** The currency (id, else code) the view's amounts are in — the document's own until base amounts exist. */
export function currencyIn(view: AmountView, doc: DocCurrencyInfo | null | undefined): string | null {
  if (view === 'BASE' && hasBaseAmounts(doc)) return doc!.baseCurrencyId || doc!.baseCurrencyCode || null;
  return doc?.currencyId || doc?.currencyCode || null;
}

/** The base currency as id, else code. */
export function baseCurrencyOf(doc: DocCurrencyInfo | null | undefined): string | null {
  return doc?.baseCurrencyId || doc?.baseCurrencyCode || null;
}

/**
 * The amount toggle of one document screen: which currency comes first, and the amount/currency for each of the two
 * columns. `new DualAmounts(() => this.po)`; templates read `dual.showBase`, `dual.primary(a, aBase)`, `dual.primaryCurrency`.
 */
export class DualAmounts {
  view: AmountView = 'DOC';

  constructor(private readonly doc: () => DocCurrencyInfo | null | undefined) {}

  get showBase(): boolean { return hasBaseAmounts(this.doc()); }
  private get effective(): AmountView { return this.showBase ? this.view : 'DOC'; }

  get primaryCurrency(): string | null { return currencyIn(this.effective, this.doc()); }
  get secondaryCurrency(): string | null { return currencyIn(this.effective === 'BASE' ? 'DOC' : 'BASE', this.doc()); }

  /** Codes for headers ("Total (AED)"); falls back to whatever the document carries. */
  get primaryCode(): string { return this.codeOf(this.effective); }
  get secondaryCode(): string { return this.codeOf(this.effective === 'BASE' ? 'DOC' : 'BASE'); }

  primary(amount: number | null | undefined, amountBase: number | null | undefined): number | null {
    return amountIn(this.effective, amount, amountBase);
  }

  secondary(amount: number | null | undefined, amountBase: number | null | undefined): number | null {
    return this.effective === 'BASE' ? (amount ?? null) : (amountBase ?? null);
  }

  reset() { this.view = 'DOC'; }

  private codeOf(view: AmountView): string {
    const d = this.doc();
    return (view === 'BASE' ? d?.baseCurrencyCode : d?.currencyCode) ?? '';
  }
}

/**
 * A mapper from a document model to DocCurrencyInfo that returns the same object while the source object is the same
 * (bindings must not see a new object on every change-detection pass).
 */
export function cachedDocCurrency<T extends object>(map: (src: T) => DocCurrencyInfo): (src: T | null | undefined) => DocCurrencyInfo | null {
  let lastSrc: T | null = null;
  let last: DocCurrencyInfo | null = null;
  return (src) => {
    if (!src) return null;
    if (src !== lastSrc) { lastSrc = src; last = map(src); }
    return last;
  };
}

/** Σ of the defined differences, or null when none is defined (an older server sends none). */
export function sumDifferences(values: (number | null | undefined)[]): number | null {
  const known = values.filter((v): v is number => v !== null && v !== undefined);
  if (!known.length) return null;
  return Math.round(known.reduce((s, v) => s + v, 0) * 10000) / 10000;
}

/** The missing-rate refusal in a server message (it may be one line of several), else null. */
export function missingRateOf(message: string | null | undefined): MissingRate | null {
  if (!message) return null;
  const m = MISSING_RATE.exec(message);
  return m ? { code: m[1].toUpperCase(), date: m[2], message: m[0].trim() } : null;
}
