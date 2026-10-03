import { of, throwError } from 'rxjs';
import { SaleQuotation, SaleQuotationLine } from '../../../services/sales-preorder.service';
import { quotationLineRows } from './sale-quotation.shared';

// Shared test data for the sale quotation specs: the §10.3 wireframe's quotation.

export function ok<T>(result: T, message = '') {
  return of({ success: true, message, result } as any);
}

export function fail(status: number, message?: string) {
  return throwError(() => ({ status, error: message ? { success: false, message } : null }));
}

export function qLine(overrides: Partial<SaleQuotationLine> = {}): SaleQuotationLine {
  return {
    uuid: 'l-1', lineNumber: 1, productDescription: 'Steel Rod 10mm', variantUuid: 'v-rod', variantSku: 'ROD-10',
    quantity: 500, uomCode: 'PCS', unitPrice: 2.5, discountPercent: 0, taxPercent: 0, taxCodeUuid: null, taxCode: null,
    taxAmount: 0, lineTotal: 1250, promisedDeliveryDate: null, lineType: 'NORMAL', customerResponse: 'PENDING',
    ...overrides
  };
}

/** Lines 1, 2, 5 NORMAL; 3 REJECTED; 4 and 6 its alternatives (numbered out of order on purpose). */
export function wireframeLines(): SaleQuotationLine[] {
  return [
    qLine(),
    qLine({ uuid: 'l-2', lineNumber: 2, productDescription: 'Copper Wire 2mm', variantUuid: 'v-wire', quantity: 600, unitPrice: 1.8, lineTotal: 1080 }),
    qLine({ uuid: 'l-3', lineNumber: 3, productDescription: 'Titanium Sheet', variantUuid: null, quantity: 0, unitPrice: 0, lineTotal: 0,
            lineType: 'REJECTED', rejectionReasonUuid: 'r-dis', rejectionReasonCode: 'DIS', rejectionReasonDescription: 'Discontinued',
            rejectionNotes: 'Mill stopped the grade' }),
    qLine({ uuid: 'l-4', lineNumber: 4, productDescription: 'SS Sheet 316L', variantUuid: 'v-316', quantity: 200, unitPrice: 8.5, lineTotal: 1700,
            lineType: 'ALTERNATIVE', alternativeForLineUuid: 'l-3', alternativeForLineNumber: 3 }),
    qLine({ uuid: 'l-5', lineNumber: 5, productDescription: 'Bolt M8x40', variantUuid: 'v-bolt', quantity: 10000, unitPrice: 0.12, lineTotal: 1200 }),
    qLine({ uuid: 'l-6', lineNumber: 6, productDescription: 'SS Sheet 304', variantUuid: 'v-304', quantity: 200, unitPrice: 6.2, lineTotal: 1240,
            lineType: 'ALTERNATIVE', alternativeForLineUuid: 'l-3', alternativeForLineNumber: 3, alternativeNotes: 'lower grade option' })
  ];
}

export function quotation(overrides: Partial<SaleQuotation> = {}): SaleQuotation {
  return {
    uuid: 'sq-1', traceId: 't-1', quotationNumber: 'SQ-2026-00015', partnerId: 'p-1', partnerName: 'GlobalTech Co',
    customerReference: 'RFQ-778', customerReferenceDate: '2026-10-01T00:00:00',
    sourceInquiry: { uuid: 'inq-42', number: 'INQ-2026-00042', status: 'QUOTED' }, saleOrder: null,
    currencyId: 'cur-usd', currencyCode: 'USD', validFrom: '2026-10-03T00:00:00', validTo: '2026-10-31T00:00:00',
    status: 'DRAFT', paymentTerms: 'Net 30', deliveryTerms: 'FOB Karachi', subtotal: 6470, taxAmount: 0, discountAmount: 0,
    grandTotal: 6470, notes: 'Prices exclude freight', internalNotes: 'Margin thin on bolts', sentAt: null, sentByUserId: null,
    sentByUserName: null, createdBy: 7, createdDate: '2026-10-03T08:00:00Z', modifiedDate: null,
    isEditable: true, allowedActions: ['SEND', 'COPY'], lines: wireframeLines(),
    ...overrides
  };
}

describe('sale quotation line order (§10.3)', () => {
  it('puts each rejected line\'s alternatives under it, lettered, and everything else by number', () => {
    const rows = quotationLineRows(wireframeLines());
    expect(rows.map(r => r.label)).toEqual(['1', '2', '3', '3a', '3b', '5']);
    expect(rows.map(r => r.line.uuid)).toEqual(['l-1', 'l-2', 'l-3', 'l-4', 'l-6', 'l-5']);
    expect(rows.filter(r => r.indent).map(r => r.line.uuid)).toEqual(['l-4', 'l-6']);
  });

  it('keeps an alternative whose rejected line is missing at its own number', () => {
    const rows = quotationLineRows([qLine(), qLine({ uuid: 'x', lineNumber: 2, lineType: 'ALTERNATIVE', alternativeForLineUuid: 'gone' })]);
    expect(rows.map(r => r.label)).toEqual(['1', '2']);
    expect(rows[1].indent).toBeFalse();
  });
});
