import { formatCode } from './format-code';
import { fromDateOnly, toDateOnly } from './date-only';

describe('date-only', () => {
  it('writes the day a reader picked, whatever their time zone', () => {
    expect(toDateOnly(new Date(2026, 8, 5))).toBe('2026-09-05');
    expect(toDateOnly(new Date(2026, 0, 31, 23, 59))).toBe('2026-01-31');
    expect(toDateOnly(new Date(2026, 11, 1, 0, 0))).toBe('2026-12-01');
  });

  it('reads a server timestamp as the calendar day it names', () => {
    const d = fromDateOnly('2026-09-30T00:00:00Z');
    expect([d.getFullYear(), d.getMonth(), d.getDate()]).toEqual([2026, 8, 30]);
  });

  it('round-trips', () => {
    expect(toDateOnly(fromDateOnly('2026-02-28'))).toBe('2026-02-28');
  });
});

describe('formatCode', () => {
  it('turns a status code into words, and nothing into nothing', () => {
    expect(formatCode('PARTIALLY_PAID')).toBe('Partially Paid');
    expect(formatCode('CASH')).toBe('Cash');
    expect(formatCode(null)).toBe('');
    expect(formatCode(undefined)).toBe('');
    expect(formatCode('')).toBe('');
  });
});
