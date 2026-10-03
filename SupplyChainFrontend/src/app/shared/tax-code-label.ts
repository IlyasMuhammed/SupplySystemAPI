/**
 * How a sales line writes its tax code (SAP alignment S-3): "GST17 · 17%" — the code and the rate the line is
 * taxed at. Shared by the sale order form and the sale quotation line editor so the two cannot drift.
 */
export function taxCodeLabel(code: string, ratePercent: number): string {
  return `${code} · ${Number(ratePercent.toFixed(2))}%`;
}

/** Appended to a code a loaded line carries that the organization no longer offers. */
export const RETIRED_TAX_CODE_SUFFIX = ' (no longer active)';
