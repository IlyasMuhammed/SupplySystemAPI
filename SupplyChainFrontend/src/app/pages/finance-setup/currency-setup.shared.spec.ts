import {
  OrgCurrencyDraft, RateDraft, currencyLockText, domainLabel, inverseText, orgCurrencyProblem, previousRateNotice, rateProblem
} from './currency-setup.shared';
import { CurrencyRateModel, OrgCurrencyModel } from '../../services/org-currency.service';

const USD: OrgCurrencyModel = {
  currencyId: 'usd', code: 'USD', name: 'US Dollar', symbol: '$', decimalPlaces: 2, rounding: 0.01, symbolPosition: 'before',
  isActive: true, displayOrder: 2, baseFor: [], isRateCurrency: false
};
const PKR: OrgCurrencyModel = { ...USD, currencyId: 'pkr', code: 'PKR', name: 'Pakistani Rupee', symbol: '₨', displayOrder: 1, baseFor: ['SALE', 'PURCHASE', 'SERVICE'], isRateCurrency: true };

function draft(o: Partial<OrgCurrencyDraft> = {}): OrgCurrencyDraft {
  return { code: 'MXN', name: 'Mexican Peso', symbol: '$', decimalPlaces: 2, rounding: 0.01, symbolPosition: 'before', displayOrder: 3, isActive: true, ...o };
}

describe('orgCurrencyProblem (BR-C1-01/02, §1 errors)', () => {
  it('accepts a valid new currency (T-C1-01)', () => {
    expect(orgCurrencyProblem(draft(), [USD, PKR], null, true)).toBeNull();
  });

  it('needs exactly three letters (T-C1-02) A–Z', () => {
    expect(orgCurrencyProblem(draft({ code: 'US' }), [], null, true)).toBe('Currency code must be exactly 3 characters.');
    expect(orgCurrencyProblem(draft({ code: 'U1D' }), [], null, true)).toBe('Currency code must be letters A–Z.');
  });

  it('refuses a code the org already has (T-C1-04), any case', () => {
    expect(orgCurrencyProblem(draft({ code: 'usd' }), [USD], null, true)).toBe('Currency USD already exists.');
    expect(orgCurrencyProblem(draft({ code: 'USD' }), [USD], 'usd', true)).toBeNull();
  });

  it('needs a name and symbol only for a code the global catalog does not have', () => {
    expect(orgCurrencyProblem(draft({ name: '', symbol: '' }), [], null, true)).toBe('Name is required (max 60).');
    expect(orgCurrencyProblem(draft({ symbol: '' }), [], null, true)).toBe('Symbol is required (max 5).');
    expect(orgCurrencyProblem(draft({ name: '', symbol: '' }), [], null, false)).toBeNull();
    expect(orgCurrencyProblem(draft({ symbol: 'ABCDEF' }), [], null, false)).toBe('Symbol is required (max 5).');
  });

  it('keeps decimals 0–3, rounding > 0 and a known symbol position', () => {
    expect(orgCurrencyProblem(draft({ decimalPlaces: 4 }), [], null, true)).toBe('Decimal places must be between 0 and 3.');
    expect(orgCurrencyProblem(draft({ decimalPlaces: null }), [], null, true)).toBe('Decimal places must be between 0 and 3.');
    expect(orgCurrencyProblem(draft({ rounding: 0 }), [], null, true)).toBe('Rounding must be greater than 0.');
    expect(orgCurrencyProblem(draft({ symbolPosition: 'middle' }), [], null, true)).toBe("Symbol position must be 'before' or 'after'.");
  });

  it('refuses to deactivate a base or the rate currency (BR-C1-03, T-C1-07)', () => {
    const pkrDraft = draft({ code: 'PKR', isActive: false });
    expect(orgCurrencyProblem(pkrDraft, [PKR], 'pkr', false)).toBe('Cannot deactivate — used as sale base currency.');
    const rateOnly = { ...PKR, baseFor: [] };
    expect(orgCurrencyProblem(pkrDraft, [rateOnly], 'pkr', false)).toBe("Cannot deactivate — it is the organization's rate currency.");
  });
});

describe('currencyLockText / domainLabel', () => {
  it('says why a currency is locked', () => {
    expect(currencyLockText(PKR)).toBe('Used as sale, purchase and service base currency; the rate currency (rate 1.0)');
    expect(currencyLockText({ ...USD, baseFor: ['PURCHASE'] })).toBe('Used as purchase base currency');
    expect(currencyLockText(USD)).toBeNull();
    expect(domainLabel('SERVICE')).toBe('service');
  });
});

function rate(o: Partial<CurrencyRateModel>): CurrencyRateModel {
  return {
    id: 'r', currencyId: 'usd', currencyCode: 'USD', currencyName: 'US Dollar', rate: 278.05, inverseRate: 0.0035964754,
    effectiveFrom: '2026-10-07', effectiveTo: '9999-12-31', isCurrent: true, source: 'MANUAL', notes: null,
    rateCurrencyId: 'pkr', rateCurrencyCode: 'PKR', ...o
  };
}

function rdraft(o: Partial<RateDraft> = {}): RateDraft {
  return { currencyId: 'usd', rate: 278.12, effectiveFrom: new Date(2026, 9, 8), openEnded: true, effectiveTo: null, notes: '', ...o };
}

describe('rateProblem (BR-C2-*, §2 errors)', () => {
  const rates = [rate({ id: 'cur' }), rate({ id: 'old', rate: 277.92, effectiveFrom: '2026-10-06', effectiveTo: '2026-10-06', isCurrent: false })];

  it('accepts a new current rate after the current one (T-C2-02)', () => {
    expect(rateProblem(rdraft(), rates, null, 'pkr')).toBeNull();
  });

  it('needs a currency, a positive rate (T-C2-08) with ≤ 10 decimals, and a start date', () => {
    expect(rateProblem(rdraft({ currencyId: null }), rates, null, 'pkr')).toBe('Choose the currency.');
    expect(rateProblem(rdraft({ rate: -278.05 }), rates, null, 'pkr')).toBe('Rate must be positive.');
    expect(rateProblem(rdraft({ rate: 0 }), rates, null, 'pkr')).toBe('Rate must be positive.');
    expect(rateProblem(rdraft({ rate: null }), rates, null, 'pkr')).toBe('Enter the rate.');
    expect(rateProblem(rdraft({ rate: 1.00000000001 }), rates, null, 'pkr')).toBe('A rate can have at most 10 decimals.');
    expect(rateProblem(rdraft({ rate: 100000000 }), rates, null, 'pkr')).toBe('The rate is too large.');
    expect(rateProblem(rdraft({ effectiveFrom: null }), rates, null, 'pkr')).toBe('Choose the date the rate applies from.');
  });

  it('refuses the rate currency (T-C2-07)', () => {
    expect(rateProblem(rdraft({ currencyId: 'pkr' }), rates, null, 'pkr')).toBe('Base currency rate cannot be modified.');
  });

  it('needs a fixed end on or after the start', () => {
    expect(rateProblem(rdraft({ openEnded: false, effectiveTo: null }), rates, null, 'pkr')).toBe('Choose the last date of the rate, or make it current.');
    expect(rateProblem(rdraft({ openEnded: false, effectiveTo: new Date(2026, 9, 1) }), rates, null, 'pkr'))
      .toBe('Effective to must be on or after effective from.');
  });

  it('refuses a start already covered (T-C2-06) for a new current rate', () => {
    expect(rateProblem(rdraft({ effectiveFrom: new Date(2026, 9, 7) }), rates, null, 'pkr'))
      .toBe('Rate already exists for this date (USD 278.05 from 2026-10-07). Edit that rate instead.');
    expect(rateProblem(rdraft({ effectiveFrom: new Date(2026, 9, 6) }), rates, null, 'pkr'))
      .toBe('Rate already exists for this date (USD 277.92 from 2026-10-06). Edit that rate instead.');
  });

  it('refuses a fixed range that overlaps another rate, but allows a gap fill', () => {
    expect(rateProblem(rdraft({ effectiveFrom: new Date(2026, 9, 5), openEnded: false, effectiveTo: new Date(2026, 9, 6) }), rates, null, 'pkr'))
      .toBe('Rate already exists for this date (USD 277.92 from 2026-10-06). Edit that rate instead.');
    expect(rateProblem(rdraft({ effectiveFrom: new Date(2026, 9, 1), openEnded: false, effectiveTo: new Date(2026, 9, 5) }), rates, null, 'pkr')).toBeNull();
  });

  it('ignores the row being edited', () => {
    expect(rateProblem(rdraft({ effectiveFrom: new Date(2026, 9, 7) }), rates, 'cur', 'pkr')).toBeNull();
  });
});

describe('previousRateNotice / inverseText', () => {
  it('tells which current rate a new one will close (§11.4)', () => {
    const current = rate({});
    expect(previousRateNotice(current, new Date(2026, 9, 8)))
      .toBe('The previous active rate (278.05 from 7 Oct 2026) will be closed to 7 Oct 2026.');
    expect(previousRateNotice(current, new Date(2026, 9, 7))).toBeNull();
    expect(previousRateNotice(null, new Date(2026, 9, 8))).toBeNull();
  });

  it('writes the inverse with six significant decimals', () => {
    expect(inverseText(278.12, 'USD', 'PKR')).toBe('0.003596 USD/PKR');
    expect(inverseText(null, 'USD', 'PKR')).toBe('');
  });
});
