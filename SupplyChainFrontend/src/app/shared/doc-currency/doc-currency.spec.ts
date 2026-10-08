import {
  DocCurrencyInfo, amountIn, currencyIn, formatRate, hasBaseAmounts, isForeignCurrency, isRateLocked, lockedOnText,
  missingRateOf, rateText
} from './doc-currency';

describe('doc-currency (A35 D-21, §11.5)', () => {
  const aed: DocCurrencyInfo = {
    currencyId: 'cur-aed', currencyCode: 'AED', exchangeRate: 76.3, baseCurrencyId: 'cur-pkr', baseCurrencyCode: 'PKR',
    rateLockedAt: '2026-10-07T09:12:00Z'
  };

  it('writes a rate with four decimals', () => {
    expect(formatRate(76.3)).toBe('76.3000');
    expect(formatRate(0.0035964754)).toBe('0.0036');
    expect(formatRate(278.05)).toBe('278.0500');
    expect(formatRate(null)).toBe('');
  });

  it('calls a document locked once it carries a rate, and foreign when its currency is not its base', () => {
    expect(isRateLocked(aed)).toBeTrue();
    expect(isRateLocked({ ...aed, exchangeRate: null })).toBeFalse();
    expect(isForeignCurrency(aed)).toBeTrue();
    expect(isForeignCurrency({ ...aed, baseCurrencyId: 'CUR-AED' })).withContext('ids compare without case').toBeFalse();
    expect(isForeignCurrency({ currencyCode: 'PKR', baseCurrencyCode: 'pkr' })).toBeFalse();
    expect(isForeignCurrency({ currencyCode: 'AED' })).withContext('no base known yet').toBeFalse();
    expect(hasBaseAmounts(aed)).toBeTrue();
    expect(hasBaseAmounts({ ...aed, exchangeRate: null })).withContext('base amounts are null until the lock').toBeFalse();
    expect(hasBaseAmounts({ ...aed, baseCurrencyId: 'cur-aed', baseCurrencyCode: 'AED' })).withContext('same currency').toBeFalse();
  });

  it('says when the rate was locked, and what it will be locked at before then', () => {
    expect(lockedOnText('2026-10-07T09:12:00Z')).toBe('locked 7 Oct 2026');
    expect(lockedOnText(null)).toBe('locked');
    expect(rateText(aed, 'confirmation')).toBe('76.3000 (locked 7 Oct 2026)');
    expect(rateText({ ...aed, exchangeRate: null, rateLockedAt: null }, 'confirmation'))
      .toBe('Locked at confirmation');
  });

  it('picks the amount and currency for the view, falling back to the document currency while there is no base amount', () => {
    expect(amountIn('DOC', 6000, 457800)).toBe(6000);
    expect(amountIn('BASE', 6000, 457800)).toBe(457800);
    expect(amountIn('BASE', 6000, null)).toBe(6000);
    expect(currencyIn('BASE', aed)).toBe('cur-pkr');
    expect(currencyIn('DOC', aed)).toBe('cur-aed');
    expect(currencyIn('BASE', { ...aed, exchangeRate: null })).toBe('cur-aed');
    expect(currencyIn('DOC', { currencyCode: 'USD' })).toBe('USD');
  });

  it('recognises the server\'s missing-rate message (D-5)', () => {
    const msg = 'No exchange rate for AED on 2026-10-07. Add one under Settings → Exchange Rates.';
    expect(missingRateOf(msg)).toEqual({ code: 'AED', date: '2026-10-07', message: msg });
    expect(missingRateOf('Line 2: no route\n' + msg)?.code).toBe('AED');
    expect(missingRateOf('Shipping address required')).toBeNull();
    expect(missingRateOf(undefined)).toBeNull();
  });
});
