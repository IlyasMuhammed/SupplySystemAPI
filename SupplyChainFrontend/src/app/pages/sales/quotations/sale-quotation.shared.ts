import { SaleQuotationLine, SaleQuotationStatus } from '../../../services/sales-preorder.service';

// A32 C2 — what the sale quotation pages share: status colours, the line order with alternatives under the line
// they replace, and the routes between the documents of the pipeline.

export type Severity = 'success' | 'info' | 'warn' | 'danger' | 'secondary' | 'contrast';

/** Severity for every sale quotation status the server can return. */
export const SALE_QUOTATION_STATUS_SEVERITY: Record<SaleQuotationStatus, Severity> = {
  DRAFT:     'secondary',
  SENT:      'info',
  ACCEPTED:  'success',
  REJECTED:  'danger',
  EXPIRED:   'warn',
  CONVERTED: 'contrast'
};

export const QUOTATIONS_ROUTE = '/portal/pages/sales/quotations';
/** FE-INQ's inquiry detail. */
export const INQUIRIES_ROUTE = '/portal/pages/sales/inquiries';
export const SALE_ORDERS_ROUTE = '/portal/pages/sales/orders';

/** One row of a quotation's lines table: the line, where it sits and what it is called there. */
export interface QuotationLineRow {
  line: SaleQuotationLine;
  /** "3", or "3a", "3b" for the alternatives offered for line 3 (§10.3). */
  label: string;
  /** An alternative, shown under the rejected line it replaces. */
  indent: boolean;
}

/**
 * The lines in the order §10.3 shows them: by line number, each REJECTED line followed by its alternatives
 * (lettered a, b, … in their own line-number order). An alternative whose rejected line is not on the quotation
 * (it cannot happen through the API) keeps its own number, at its own place.
 */
export function quotationLineRows(lines: SaleQuotationLine[]): QuotationLineRow[] {
  const byNumber = [...lines].sort((a, b) => a.lineNumber - b.lineNumber);
  const rejected = new Set(byNumber.filter(l => l.lineType === 'REJECTED').map(l => l.uuid));
  const isPlacedAlternative = (l: SaleQuotationLine) =>
    l.lineType === 'ALTERNATIVE' && !!l.alternativeForLineUuid && rejected.has(l.alternativeForLineUuid);

  const rows: QuotationLineRow[] = [];
  for (const line of byNumber) {
    if (isPlacedAlternative(line)) continue;
    rows.push({ line, label: String(line.lineNumber), indent: false });
    if (line.lineType !== 'REJECTED') continue;
    byNumber
      .filter(a => isPlacedAlternative(a) && a.alternativeForLineUuid === line.uuid)
      .forEach((alt, i) => rows.push({ line: alt, label: `${line.lineNumber}${letter(i)}`, indent: true }));
  }
  return rows;
}

/** a … z, then aa, ab … */
function letter(i: number): string {
  const a = 'a'.charCodeAt(0);
  return i < 26 ? String.fromCharCode(a + i) : letter(Math.floor(i / 26) - 1) + String.fromCharCode(a + (i % 26));
}

/** The lines a customer answers for: everything the seller did not reject. */
export function respondableLines(lines: SaleQuotationLine[]): SaleQuotationLine[] {
  return lines.filter(l => l.lineType !== 'REJECTED');
}
