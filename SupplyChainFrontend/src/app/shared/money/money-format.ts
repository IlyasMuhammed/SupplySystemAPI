// A35 D-21 / BR-C1-05 / T-C1-08..10 — how an amount is written in an org currency: its symbol, its decimal places and
// where the symbol goes. Pure functions; MoneyService feeds them the org's currencies and the `money` pipe uses them.

export type SymbolPosition = 'before' | 'after';

/** What formatting needs to know about a currency (an org currency from api/currencies carries all of it). */
export interface MoneyCurrency {
  code: string;
  symbol: string | null;
  decimalPlaces: number;
  symbolPosition: SymbolPosition | string;
}

export interface MoneyFormatOptions {
  /** 'symbol' (default): ¥1,234 / BD 1.567 / 100.00 CHF. 'code': AED 6,000.00. 'none': the number only. */
  display?: 'symbol' | 'code' | 'none';
  /** Overrides the currency's decimal places (e.g. 4 for a unit price). */
  decimals?: number;
}

/** Rounds half away from zero at `decimals` places (the server's MidpointRounding.AwayFromZero), without float noise. */
export function roundMoney(amount: number, decimals: number): number {
  const places = clampDecimals(decimals);
  const sign = amount < 0 ? -1 : 1;
  // Shifting through the exponent ("1.005e2") avoids 1.005 * 100 = 100.49999999999999.
  const abs = Math.abs(amount);
  const text = String(abs);
  const shifted = text.includes('e') ? Math.round(abs * Math.pow(10, places)) : Math.round(Number(`${text}e${places}`));
  const result = sign * Number(`${shifted}e-${places}`);
  return result === 0 ? 0 : result;
}

/**
 * The amount as text in this currency. A currency given only as a code (or unknown) is shown with two decimals and its
 * code. No amount (null, undefined, NaN) → ''.
 */
export function formatMoney(
  amount: number | null | undefined,
  currency: MoneyCurrency | string | null | undefined,
  options: MoneyFormatOptions = {}
): string {
  if (amount === null || amount === undefined || typeof amount !== 'number' || isNaN(amount)) return '';

  const cur: MoneyCurrency | null = typeof currency === 'string'
    ? { code: currency, symbol: null, decimalPlaces: 2, symbolPosition: 'before' }
    : currency ?? null;
  const decimals = clampDecimals(options.decimals ?? cur?.decimalPlaces ?? 2);

  const rounded = roundMoney(amount, decimals);
  const negative = rounded < 0;
  const number = new Intl.NumberFormat('en-US', { minimumFractionDigits: decimals, maximumFractionDigits: decimals })
    .format(Math.abs(rounded));
  const sign = negative ? '-' : '';

  const display = options.display ?? 'symbol';
  if (!cur || display === 'none') return sign + number;

  const code = (cur.code ?? '').trim().toUpperCase();
  const symbol = (cur.symbol ?? '').trim();
  const useCode = display === 'code' || !symbol;
  const mark = useCode ? code : symbol;
  if (!mark) return sign + number;

  const after = (cur.symbolPosition ?? 'before').toString().toLowerCase() === 'after';
  if (after) return `${sign}${number} ${mark}`;
  // A one-character sign ($, €, ¥, ₨) sits against the number; letters (BD, AED, د.إ) get a space.
  const tight = !useCode && [...mark].length === 1;
  return `${sign}${mark}${tight ? '' : ' '}${number}`;
}

function clampDecimals(decimals: number): number {
  if (typeof decimals !== 'number' || isNaN(decimals)) return 2;
  return Math.min(Math.max(Math.trunc(decimals), 0), 10);
}
