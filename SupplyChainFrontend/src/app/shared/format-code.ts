/**
 * Codes whose generic spelling reads badly. A40: opening balances brought in by the data import — customer ledger
 * OPENING_BAL, invoice sourceType OPENING, product/stock ledger OPENING_STOCK.
 */
export const CODE_LABELS: Record<string, string> = {
  OPENING_BAL: 'Opening balance',
  OPENING: 'Opening balance',
  OPENING_STOCK: 'Opening stock'
};

/** Turns PARTIALLY_FULFILLED into "Partially Fulfilled" (named exceptions in CODE_LABELS). */
export function formatCode(code?: string | null): string {
  if (!code) return '';
  if (CODE_LABELS[code]) return CODE_LABELS[code];
  return code
    .split('_')
    .map(word => word.charAt(0) + word.slice(1).toLowerCase())
    .join(' ');
}
