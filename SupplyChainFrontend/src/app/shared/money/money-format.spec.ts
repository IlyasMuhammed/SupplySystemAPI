import { MoneyCurrency, formatMoney, roundMoney } from './money-format';

// A35 D-21 / T-C1-08..10 — an amount shown with the org currency's symbol, decimal places and symbol position.
const JPY: MoneyCurrency = { code: 'JPY', symbol: '¥', decimalPlaces: 0, symbolPosition: 'before' };
const BHD: MoneyCurrency = { code: 'BHD', symbol: 'BD', decimalPlaces: 3, symbolPosition: 'before' };
const CHF: MoneyCurrency = { code: 'CHF', symbol: 'CHF', decimalPlaces: 2, symbolPosition: 'after' };
const USD: MoneyCurrency = { code: 'USD', symbol: '$', decimalPlaces: 2, symbolPosition: 'before' };
const EUR_AFTER: MoneyCurrency = { code: 'EUR', symbol: '€', decimalPlaces: 2, symbolPosition: 'after' };

describe('formatMoney', () => {
  it('T-C1-08: JPY has no decimals — ¥1,234', () => {
    expect(formatMoney(1234, JPY)).toBe('¥1,234');
    expect(formatMoney(1234.5, JPY)).toBe('¥1,235');
  });

  it('T-C1-09: BHD has three decimals — BD 1.567', () => {
    expect(formatMoney(1.567, BHD)).toBe('BD 1.567');
    expect(formatMoney(1.5, BHD)).toBe('BD 1.500');
  });

  it('T-C1-10: a symbol placed after the amount — 100 CHF', () => {
    expect(formatMoney(100, CHF)).toBe('100.00 CHF');
    expect(formatMoney(100, EUR_AFTER)).toBe('100.00 €');
  });

  it('groups thousands and writes a one-character symbol against the number', () => {
    expect(formatMoney(1234567.891, USD)).toBe('$1,234,567.89');
  });

  it('puts the minus sign in front of the symbol', () => {
    expect(formatMoney(-1234.5, USD)).toBe('-$1,234.50');
    expect(formatMoney(-100, CHF)).toBe('-100.00 CHF');
  });

  it('rounds half away from zero, without float noise (1.005 → 1.01)', () => {
    expect(formatMoney(1.005, USD)).toBe('$1.01');
    expect(formatMoney(-1.005, USD)).toBe('-$1.01');
    expect(formatMoney(2.5, JPY)).toBe('¥3');
  });

  it('can show the ISO code instead of the symbol — AED 6,000.00', () => {
    const aed: MoneyCurrency = { code: 'AED', symbol: 'د.إ', decimalPlaces: 2, symbolPosition: 'before' };
    expect(formatMoney(6000, aed, { display: 'code' })).toBe('AED 6,000.00');
    expect(formatMoney(100, CHF, { display: 'code' })).toBe('100.00 CHF');
  });

  it('can show the number only', () => {
    expect(formatMoney(1.567, BHD, { display: 'none' })).toBe('1.567');
  });

  it('falls back to the code when the currency has no symbol', () => {
    expect(formatMoney(5, { code: 'XYZ', symbol: null, decimalPlaces: 2, symbolPosition: 'before' })).toBe('XYZ 5.00');
  });

  it('shows two decimals and the code when the currency is unknown', () => {
    expect(formatMoney(5, null)).toBe('5.00');
    expect(formatMoney(5, 'PKR')).toBe('PKR 5.00');
  });

  it('shows nothing for no amount', () => {
    expect(formatMoney(null, USD)).toBe('');
    expect(formatMoney(undefined, USD)).toBe('');
    expect(formatMoney(NaN, USD)).toBe('');
  });

  it('never writes -0', () => {
    expect(formatMoney(-0.001, USD)).toBe('$0.00');
  });
});

describe('roundMoney', () => {
  it('rounds to the currency decimals, half away from zero', () => {
    expect(roundMoney(1.005, 2)).toBe(1.01);
    expect(roundMoney(-2.5, 0)).toBe(-3);
    expect(roundMoney(1.0004, 3)).toBe(1);
    expect(roundMoney(1.0005, 3)).toBe(1.001);
  });
});
