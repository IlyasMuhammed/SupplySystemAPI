import { formatCode } from './format-code';

describe('formatCode', () => {
  it('title-cases codes', () => {
    expect(formatCode('PARTIALLY_FULFILLED')).toBe('Partially Fulfilled');
    expect(formatCode(null)).toBe('');
  });

  it('A40 — opening balance codes read as words', () => {
    expect(formatCode('OPENING_BAL')).toBe('Opening balance');       // customer ledger entry type
    expect(formatCode('OPENING')).toBe('Opening balance');           // sales / supplier invoice sourceType
    expect(formatCode('OPENING_STOCK')).toBe('Opening stock');       // product / stock ledger entry type
  });
});
