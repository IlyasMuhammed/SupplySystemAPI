import { HttpErrorResponse } from '@angular/common/http';

import { ExchangeRateModel, TaxCodeModel } from '../../services/finance-setup.service';
import {
  ExchangeRateDraft, TaxCodeDraft, defaultsTakenOver, exchangeRateProblem, formatPercent, formatRate, fromIsoDate, normalizeTaxCode,
  roundExchangeRate, roundRatePercent, setupErrorMessage, taxCodeProblem, toIsoDate, usageAllows, usageLabel
} from './finance-setup.shared';

function code(overrides: Partial<TaxCodeModel> = {}): TaxCodeModel {
  return {
    uuid: 'u-' + (overrides.code ?? 'GST17'), code: 'GST17', name: 'GST 17%', description: null, ratePercent: 17, usage: 'SALES',
    isDefault: false, isActive: true, ...overrides
  };
}

function draft(overrides: Partial<TaxCodeDraft> = {}): TaxCodeDraft {
  return { code: 'VAT5', name: 'VAT 5%', description: '', ratePercent: 5, usage: 'BOTH', isDefault: false, isActive: true, ...overrides };
}

function rate(overrides: Partial<ExchangeRateModel> = {}): ExchangeRateModel {
  return {
    uuid: 'r1', fromCurrencyCode: 'USD', toCurrencyCode: 'PKR', rate: 278.5, effectiveDate: '2026-10-01', source: 'MANUAL',
    notes: null, createdDate: '2026-10-01T08:00:00Z', ...overrides
  };
}

function rateDraft(overrides: Partial<ExchangeRateDraft> = {}): ExchangeRateDraft {
  return { fromCurrencyCode: 'EUR', toCurrencyCode: 'PKR', rate: 301.25, effectiveDate: new Date(2026, 9, 1), notes: '', ...overrides };
}

describe('finance setup shared rules', () => {
  describe('tax codes', () => {
    it('accepts a good draft', () => {
      expect(taxCodeProblem(draft(), [code()], null)).toBeNull();
    });

    it('needs a code of at most 20 letters, digits, _ and -', () => {
      expect(taxCodeProblem(draft({ code: '  ' }), [], null)).toContain('Give the tax code a code');
      expect(taxCodeProblem(draft({ code: 'A'.repeat(21) }), [], null)).toContain('at most 20');
      expect(taxCodeProblem(draft({ code: 'GST 17' }), [], null)).toContain('only the letters');
      expect(taxCodeProblem(draft({ code: 'gst-17_a' }), [], null)).toBeNull();
    });

    it('refuses a code already taken, whatever its case, except the code being edited', () => {
      const others = [code(), code({ code: 'OLD', uuid: 'u-old', isActive: false })];
      expect(taxCodeProblem(draft({ code: 'gst17' }), others, null)).toBe('There is already a tax code GST17.');
      expect(taxCodeProblem(draft({ code: 'old' }), others, null)).toContain('deactivated — reactivate it instead');
      expect(taxCodeProblem(draft({ code: 'GST17' }), others, 'u-GST17')).toBeNull();
    });

    it('needs a name and keeps name and description within their lengths', () => {
      expect(taxCodeProblem(draft({ name: ' ' }), [], null)).toContain('name');
      expect(taxCodeProblem(draft({ name: 'n'.repeat(101) }), [], null)).toContain('100');
      expect(taxCodeProblem(draft({ description: 'd'.repeat(301) }), [], null)).toContain('300');
    });

    it('needs a rate from 0 to 100 with at most two decimals', () => {
      expect(taxCodeProblem(draft({ ratePercent: null }), [], null)).toContain('Enter the rate');
      expect(taxCodeProblem(draft({ ratePercent: -1 }), [], null)).toContain('between 0 and 100');
      expect(taxCodeProblem(draft({ ratePercent: 100.5 }), [], null)).toContain('between 0 and 100');
      expect(taxCodeProblem(draft({ ratePercent: 17.125 }), [], null)).toContain('two decimals');
      expect(taxCodeProblem(draft({ ratePercent: 0 }), [], null)).toBeNull();
      expect(taxCodeProblem(draft({ ratePercent: 100 }), [], null)).toBeNull();
      expect(taxCodeProblem(draft({ ratePercent: 0.1 + 0.2 }), [], null)).toBeNull();
    });

    it('needs a usage', () => {
      expect(taxCodeProblem(draft({ usage: null }), [], null)).toContain('Choose where');
    });

    it('upper-cases lower-case input instead of refusing it, and refuses spaces and letters outside A–Z', () => {
      expect(taxCodeProblem(draft({ code: '  vat_5-a  ' }), [], null)).toBeNull();
      expect(taxCodeProblem(draft({ code: 'GST 17' }), [], null)).toContain('only the letters');
      expect(taxCodeProblem(draft({ code: 'TVA5é' }), [], null)).toContain('only the letters');
      expect(taxCodeProblem(draft({ code: 'STEUER٣' }), [], null)).toContain('only the letters');
      // JavaScript upper-cases these into plain A–Z ('ß' → 'SS', 'ﬁ' → 'FI'); the server's ToUpperInvariant
      // does not, and refuses them. The code would silently become a different text than was typed.
      expect(taxCodeProblem(draft({ code: 'straße' }), [], null)).toContain('only the letters');
      expect(taxCodeProblem(draft({ code: 'ﬁx' }), [], null)).toContain('only the letters');
      expect(taxCodeProblem(draft({ code: 'A'.repeat(20) }), [], null)).toBeNull();
    });

    it('counts a rate’s decimals as the server does, forgiving float noise from arithmetic', () => {
      expect(taxCodeProblem(draft({ ratePercent: 17.255 }), [], null)).toContain('two decimals');
      expect(taxCodeProblem(draft({ ratePercent: 17.25 }), [], null)).toBeNull();
      // What PrimeNG's arrow-key spin produces from 0.57: 1.5699999999999998.
      expect(taxCodeProblem(draft({ ratePercent: 0.57 + 1 }), [], null)).toBeNull();
      expect(roundRatePercent(0.57 + 1)).toBe(1.57);
      expect(roundRatePercent(0.1 + 0.2)).toBe(0.3);
    });

    it('works out whose default a new default takes, as the server does', () => {
      const s = code({ code: 'S', uuid: 's', usage: 'SALES', isDefault: true });
      const p = code({ code: 'P', uuid: 'p', usage: 'PURCHASE', isDefault: true });
      const b = code({ code: 'B', uuid: 'b', usage: 'BOTH', isDefault: true });

      expect(defaultsTakenOver(draft({ usage: 'SALES', isDefault: true }), [s, p], null).map(c => c.code)).toEqual(['S']);
      expect(defaultsTakenOver(draft({ usage: 'BOTH', isDefault: true }), [s, p], null).map(c => c.code)).toEqual(['S', 'P']);
      expect(defaultsTakenOver(draft({ usage: 'PURCHASE', isDefault: true }), [b], null).map(c => c.code)).toEqual(['B']);
      expect(defaultsTakenOver(draft({ usage: 'SALES', isDefault: true }), [s], 's')).toEqual([]);
      expect(defaultsTakenOver(draft({ usage: 'SALES', isDefault: false }), [s], null)).toEqual([]);
      expect(defaultsTakenOver(draft({ usage: 'SALES', isDefault: true, isActive: false }), [s], null)).toEqual([]);
    });

    it('labels usages and says where a code may be used', () => {
      expect(usageLabel('BOTH')).toBe('Sales & purchases');
      expect(usageAllows('BOTH', 'SALES')).toBeTrue();
      expect(usageAllows('PURCHASE', 'SALES')).toBeFalse();
      expect(normalizeTaxCode('  gst17 ')).toBe('GST17');
    });
  });

  describe('exchange rates', () => {
    it('accepts a good draft', () => {
      expect(exchangeRateProblem(rateDraft(), [rate()], null)).toBeNull();
    });

    it('needs two different currencies', () => {
      expect(exchangeRateProblem(rateDraft({ fromCurrencyCode: null }), [], null)).toContain('convert from');
      expect(exchangeRateProblem(rateDraft({ toCurrencyCode: '' }), [], null)).toContain('convert to');
      expect(exchangeRateProblem(rateDraft({ toCurrencyCode: 'eur' }), [], null)).toContain('two different currencies');
    });

    it('needs a positive rate with at most eight decimals', () => {
      expect(exchangeRateProblem(rateDraft({ rate: null }), [], null)).toContain('Enter the rate');
      expect(exchangeRateProblem(rateDraft({ rate: 0 }), [], null)).toContain('greater than 0');
      expect(exchangeRateProblem(rateDraft({ rate: 0.000000001 }), [], null)).toContain('8 decimals');
      expect(exchangeRateProblem(rateDraft({ rate: 0.00359066 }), [], null)).toBeNull();
      expect(exchangeRateProblem(rateDraft({ rate: 0.123456789 }), [], null)).toContain('8 decimals');
      expect(exchangeRateProblem(rateDraft({ rate: 278.12345678 }), [], null)).toBeNull();
    });

    it('accepts big rates with a few decimals, which the server accepts too', () => {
      // Read at twelve places these show float noise (9000.300000000000182…), so they were taken for
      // twelve-decimal rates and refused — e.g. a rupiah or dong rate with a decimal part.
      expect(exchangeRateProblem(rateDraft({ rate: 9000.3 }), [], null)).toBeNull();
      expect(exchangeRateProblem(rateDraft({ rate: 12345.6789 }), [], null)).toBeNull();
      expect(exchangeRateProblem(rateDraft({ rate: 99999.99 }), [], null)).toBeNull();
      expect(exchangeRateProblem(rateDraft({ rate: 1234567890.12 }), [], null)).toBeNull();
      expect(exchangeRateProblem(rateDraft({ rate: 9000.000000001 }), [], null)).toContain('8 decimals');
    });

    it('accepts the largest rate the server holds and refuses a larger one', () => {
      expect(exchangeRateProblem(rateDraft({ rate: 9_999_999_999 }), [], null)).toBeNull();
      expect(exchangeRateProblem(rateDraft({ rate: 10_000_000_001 }), [], null)).toContain('too large');
    });

    it('rounds a rate to the eight decimals the server keeps', () => {
      expect(roundExchangeRate(0.57 + 1)).toBe(1.57);
      expect(roundExchangeRate(9000.3)).toBe(9000.3);
      expect(roundExchangeRate(0.00359066)).toBe(0.00359066);
    });

    it('needs a date and short notes', () => {
      expect(exchangeRateProblem(rateDraft({ effectiveDate: null }), [], null)).toContain('date');
      expect(exchangeRateProblem(rateDraft({ notes: 'n'.repeat(301) }), [], null)).toContain('300');
    });

    it('refuses a second rate for the same pair and day, except the one being edited', () => {
      const existing = rate();
      const same = rateDraft({ fromCurrencyCode: 'usd', toCurrencyCode: 'PKR', effectiveDate: new Date(2026, 9, 1) });
      expect(exchangeRateProblem(same, [existing], null)).toContain('already a USD → PKR rate for this date (278.5)');
      expect(exchangeRateProblem(same, [existing], 'r1')).toBeNull();
      expect(exchangeRateProblem({ ...same, effectiveDate: new Date(2026, 9, 2) }, [existing], null)).toBeNull();
    });
  });

  describe('dates and numbers', () => {
    it('writes a local date as yyyy-MM-dd and reads it back without moving a day', () => {
      const late = new Date(2026, 9, 1, 23, 59, 59);
      expect(toIsoDate(late)).toBe('2026-10-01');
      // Local midnight is what a date picker hands over; through toISOString it is the day before east of UTC.
      const midnight = new Date(2026, 9, 1);
      const local = `${midnight.getFullYear()}-${String(midnight.getMonth() + 1).padStart(2, '0')}-${String(midnight.getDate()).padStart(2, '0')}`;
      expect(toIsoDate(midnight)).toBe(local);
      expect(toIsoDate(midnight)).toBe('2026-10-01');
      expect(toIsoDate(new Date(2027, 0, 1))).toBe('2027-01-01');
      const back = fromIsoDate('2026-10-01')!;
      expect([back.getFullYear(), back.getMonth(), back.getDate(), back.getHours()]).toEqual([2026, 9, 1, 0]);
      expect(fromIsoDate('2026-10-01T00:00:00Z')!.getDate()).toBe(1);
      expect(fromIsoDate('01/10/2026')).toBeNull();
      expect(fromIsoDate(null)).toBeNull();
    });

    it('shows rates and percents without trailing zeros or exponents', () => {
      expect(formatRate(278.5)).toBe('278.5');
      expect(formatRate(0.00000001)).toBe('0.00000001');
      expect(formatRate(100)).toBe('100');
      expect(formatPercent(17)).toBe('17');
      expect(formatPercent(7.5)).toBe('7.5');
      expect(formatPercent(null)).toBe('');
    });
  });

  describe('errors', () => {
    it('prefers the server’s message, then a plain one by status', () => {
      expect(setupErrorMessage(new HttpErrorResponse({ status: 409, error: { message: 'There is already a tax code GST17.' } }), 'x'))
        .toBe('There is already a tax code GST17.');
      expect(setupErrorMessage(new HttpErrorResponse({ status: 403, error: null }), 'x')).toContain('permission');
      expect(setupErrorMessage(new HttpErrorResponse({ status: 500, error: null }), 'fallback')).toBe('fallback');
    });
  });
});
