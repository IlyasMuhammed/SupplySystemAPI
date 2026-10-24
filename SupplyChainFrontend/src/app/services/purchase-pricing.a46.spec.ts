import { conversionTooltip, currencyMismatchText, price2, price4, samePrice, PriceSource, PRICE_SOURCE_LABELS } from './purchase-pricing.service';

// A46 C3 / C10 - pure display helpers.

describe('purchase-pricing helpers (A46)', () => {
  it('price2 / price4: 12.3456 reads 12.35 and 12.3456; null reads empty', () => {
    expect(price2(12.3456)).toBe('12.35');
    expect(price4(12.3456)).toBe('12.3456');
    expect(price4(12.3)).toBe('12.30');
    expect(price2(1234.5)).toBe('1,234.50');
    expect(price2(null)).toBe('');
  });

  it('samePrice compares at 4 decimals (not the old 2)', () => {
    expect(samePrice(12.3456, 12.3456)).toBeTrue();
    expect(samePrice(12.3456, 12.3457)).toBeFalse();
    expect(samePrice(12.34, 12.3400)).toBeTrue();
    expect(samePrice(null, 0)).toBeTrue();
  });

  it('conversionTooltip: "Original: {code} {price} @ {rate} ({date})"', () => {
    expect(conversionTooltip({ code: 'EUR', price: 10, rate: 1.08, date: '2026-10-01' })).toBe('Original: EUR 10 @ 1.08 (2026-10-01)');
    expect(conversionTooltip({ code: 'EUR', price: 11.4321, rate: 1.0812, date: '2026-10-01T00:00:00Z' })).toBe('Original: EUR 11.4321 @ 1.0812 (2026-10-01)');
    expect(conversionTooltip({ code: 'EUR', price: 10 })).toBe('Original: EUR 10');
    expect(conversionTooltip(null)).toBe('');
  });

  it('currencyMismatchText matches the spec wording', () => {
    expect(currencyMismatchText('EUR', 'USD', '2026-10-01')).toBe('No exchange rate available for EUR → USD on 2026-10-01. Enter price manually.');
  });

  it('BlanketOrder (4) is a known source with a label', () => {
    expect(PriceSource.BlanketOrder).toBe(4);
    expect(PRICE_SOURCE_LABELS[4]).toBe('Blanket');
  });
});
