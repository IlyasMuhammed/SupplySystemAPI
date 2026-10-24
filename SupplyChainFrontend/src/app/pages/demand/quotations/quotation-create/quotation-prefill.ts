import { Params } from '@angular/router';

/**
 * A46 C2 - what the RFQ create page reads from its query string. A45's "Create RFQ" link sends
 * supplierId / productId / variantUuid / qty / productName; the A46 spec names the same things
 * vendorId / productId / quantity / sourcePO, so both spellings are accepted (the spec's wins).
 * Nothing here throws or reports: an unusable value is simply absent (the page then validates the
 * ids it does get against what it loaded).
 */
export interface RfqPrefill {
  supplierId: string | null;
  productId: string | null;
  variantUuid: string | null;
  /** null = not given; an invalid value (NaN, <= 0, not numeric) becomes 1. */
  quantity: number | null;
  productName: string | null;
  sourcePo: string | null;
}

const text = (v: unknown, max = 200): string | null => {
  const s = Array.isArray(v) ? v[0] : v;
  if (typeof s !== 'string') return null;
  const t = s.trim();
  return t ? t.slice(0, max) : null;
};

export function parseRfqPrefill(p: Params | null | undefined): RfqPrefill {
  const q = p ?? {};
  const rawQty = text(q['quantity'], 40) ?? text(q['qty'], 40);
  let quantity: number | null = null;
  if (rawQty !== null) {
    const n = Number(rawQty);
    quantity = Number.isFinite(n) && n > 0 && n <= 999999 ? n : 1;
  }
  return {
    supplierId: text(q['vendorId'], 64) ?? text(q['supplierId'], 64),
    productId: text(q['productId'], 64),
    variantUuid: text(q['variantUuid'], 64),
    quantity,
    productName: text(q['productName']),
    sourcePo: text(q['sourcePO']) ?? text(q['sourcePo'])
  };
}
