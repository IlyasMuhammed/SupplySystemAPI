import {
  INQUIRY_STATUS_COLOR, INQUIRY_LINE_STATUS_COLOR, LineDraft, draftFromLine, emptyLineDraft, lineProblems, toLineRequest,
  toNewLineRequest, undecidedLineCount, lineSummary
} from './sale-inquiry.shared';
import {
  SALE_INQUIRY_STATUSES, SALE_INQUIRY_LINE_STATUSES, SaleInquiryLine
} from '../../../services/sales-preorder.service';

function line(overrides: Partial<SaleInquiryLine> = {}): SaleInquiryLine {
  return {
    uuid: 'l-1', lineNumber: 1, productDescription: 'Copper Wire 2mm', requestedQuantity: 1000, requestedUomCode: 'M',
    requestedDeliveryDate: '2026-10-20T00:00:00', lineStatus: 'PENDING', requiresProcurement: false,
    ...overrides
  };
}

function draft(overrides: Partial<LineDraft> = {}): LineDraft {
  return { ...emptyLineDraft(), productDescription: 'Copper Wire 2mm', requestedQuantity: 1000, ...overrides };
}

describe('Sale inquiry shared rules', () => {

  // ── Colours (spec §10.1 / §10.2) ────────────────────────────────────────────

  it('colours every inquiry status as the spec says', () => {
    expect(SALE_INQUIRY_STATUSES.map(s => INQUIRY_STATUS_COLOR[s]))
      .toEqual(['blue', 'orange', 'green', 'purple', 'red']);
  });

  it('colours every line status as the spec says', () => {
    expect(SALE_INQUIRY_LINE_STATUSES.map(s => INQUIRY_LINE_STATUS_COLOR[s]))
      .toEqual(['grey', 'green', 'yellow', 'red', 'orange']);
  });

  // ── Client validation mirrors BR-C1-04 / BR-C1-05 ────────────────────────────

  it('needs a description and a quantity above zero', () => {
    expect(lineProblems(draft({ productDescription: '  ' })).productDescription).toBeDefined();
    expect(lineProblems(draft({ requestedQuantity: 0 })).requestedQuantity).toBeDefined();
    expect(lineProblems(draft({ requestedQuantity: null })).requestedQuantity).toBeDefined();
    expect(lineProblems(draft())).toEqual({});
  });

  it('CAN_SUPPLY needs an estimated delivery date', () => {
    expect(lineProblems(draft({ lineStatus: 'CAN_SUPPLY' })).estimatedDeliveryDate).toBeDefined();
    expect(lineProblems(draft({ lineStatus: 'CAN_SUPPLY', estimatedDeliveryDate: new Date(2026, 9, 25) }))).toEqual({});
  });

  it('PARTIAL needs 0 < can-supply < requested and a date (BR-C1-05)', () => {
    const date = new Date(2026, 9, 25);
    expect(lineProblems(draft({ lineStatus: 'PARTIAL', estimatedDeliveryDate: date })).canSupplyQuantity).toBeDefined();
    expect(lineProblems(draft({ lineStatus: 'PARTIAL', canSupplyQuantity: 0, estimatedDeliveryDate: date })).canSupplyQuantity).toBeDefined();
    expect(lineProblems(draft({ lineStatus: 'PARTIAL', canSupplyQuantity: 1000, estimatedDeliveryDate: date })).canSupplyQuantity)
      .withContext('equal to requested is not partial').toBeDefined();
    expect(lineProblems(draft({ lineStatus: 'PARTIAL', canSupplyQuantity: 600 })).estimatedDeliveryDate).toBeDefined();
    expect(lineProblems(draft({ lineStatus: 'PARTIAL', canSupplyQuantity: 600, estimatedDeliveryDate: date }))).toEqual({});
  });

  it('CANNOT_SUPPLY needs a rejection reason (BR-C1-04)', () => {
    expect(lineProblems(draft({ lineStatus: 'CANNOT_SUPPLY' })).rejectionReasonUuid).toBeDefined();
    expect(lineProblems(draft({ lineStatus: 'CANNOT_SUPPLY', rejectionReasonUuid: 'r-dis' }))).toEqual({});
  });

  it('PENDING and UNDER_REVIEW need nothing more', () => {
    expect(lineProblems(draft({ lineStatus: 'PENDING' }))).toEqual({});
    expect(lineProblems(draft({ lineStatus: 'UNDER_REVIEW' }))).toEqual({});
  });

  it('caps the text fields at the column lengths', () => {
    expect(lineProblems(draft({ productDescription: 'x'.repeat(501) })).productDescription).toBeDefined();
    expect(lineProblems(draft({ notes: 'x'.repeat(1001) })).notes).toBeDefined();
    expect(lineProblems(draft({ lineStatus: 'CANNOT_SUPPLY', rejectionReasonUuid: 'r', rejectionNotes: 'x'.repeat(501) })).rejectionNotes).toBeDefined();
  });

  it('refuses a negative or fractional procurement lead time', () => {
    expect(lineProblems(draft({ requiresProcurement: true, procurementLeadDays: -1 })).procurementLeadDays).toBeDefined();
    expect(lineProblems(draft({ requiresProcurement: true, procurementLeadDays: 2.5 })).procurementLeadDays).toBeDefined();
    expect(lineProblems(draft({ requiresProcurement: true, procurementLeadDays: 14 }))).toEqual({});
  });

  // ── What goes to the server ─────────────────────────────────────────────────

  it('sends picked dates as the days picked, never through UTC', () => {
    const req = toLineRequest(draft({
      lineStatus: 'PARTIAL', canSupplyQuantity: 600, estimatedDeliveryDate: new Date(2026, 9, 1),
      requestedDeliveryDate: new Date(2026, 9, 20)
    }));
    expect(req.estimatedDeliveryDate).toBe('2026-10-01');
    expect(req.requestedDeliveryDate).toBe('2026-10-20');
  });

  it('drops the evaluation fields that do not apply to the chosen status', () => {
    const full = {
      canSupplyQuantity: 600, estimatedDeliveryDate: new Date(2026, 9, 25), rejectionReasonUuid: 'r-dis', rejectionNotes: 'gone',
      alternativeProductUuid: 'p-alt', alternativeVariantUuid: 'v-alt', alternativeNotes: '316L instead'
    };

    const can = toLineRequest(draft({ ...full, lineStatus: 'CAN_SUPPLY' }));
    expect([can.canSupplyQuantity, can.rejectionReasonUuid, can.alternativeVariantUuid]).toEqual([null, null, null]);
    expect(can.estimatedDeliveryDate).toBe('2026-10-25');

    const partial = toLineRequest(draft({ ...full, lineStatus: 'PARTIAL' }));
    expect(partial.canSupplyQuantity).toBe(600);
    expect(partial.rejectionReasonUuid).toBeNull();

    const cannot = toLineRequest(draft({ ...full, lineStatus: 'CANNOT_SUPPLY' }));
    expect(cannot.canSupplyQuantity).toBeNull();
    expect([cannot.rejectionReasonUuid, cannot.rejectionNotes, cannot.alternativeVariantUuid, cannot.alternativeNotes])
      .toEqual(['r-dis', 'gone', 'v-alt', '316L instead']);
    expect(cannot.estimatedDeliveryDate).withContext('kept for the alternative').toBe('2026-10-25');

    const cannotNoAlt = toLineRequest(draft({ ...full, alternativeProductUuid: null, alternativeVariantUuid: null, lineStatus: 'CANNOT_SUPPLY' }));
    expect(cannotNoAlt.estimatedDeliveryDate).withContext('no alternative, no date').toBeNull();

    const pending = toLineRequest(draft({ ...full, lineStatus: 'PENDING' }));
    expect([pending.canSupplyQuantity, pending.estimatedDeliveryDate, pending.rejectionReasonUuid, pending.alternativeVariantUuid])
      .toEqual([null, null, null, null]);
  });

  it('keeps procurement for every status and drops the lead time when procurement is off', () => {
    expect(toLineRequest(draft({ lineStatus: 'PENDING', requiresProcurement: true, procurementLeadDays: 14 })).procurementLeadDays).toBe(14);
    expect(toLineRequest(draft({ requiresProcurement: false, procurementLeadDays: 14 })).procurementLeadDays).toBeNull();
  });

  it('trims text and sends blanks as null', () => {
    const req = toLineRequest(draft({ productDescription: '  Bolt M8x40 ', requestedUomCode: ' ', notes: '' }));
    expect(req.productDescription).toBe('Bolt M8x40');
    expect(req.requestedUomCode).toBeNull();
    expect(req.notes).toBeNull();
  });

  it('a new line carries only what the customer asked for', () => {
    const req = toNewLineRequest(draft({ variantUuid: 'v-1', productUuid: 'p-1', requestedDeliveryDate: new Date(2026, 9, 1), lineStatus: 'CAN_SUPPLY' }));
    expect(req).toEqual({
      productUuid: 'p-1', variantUuid: 'v-1', productDescription: 'Copper Wire 2mm', requestedQuantity: 1000,
      requestedUomCode: null, requestedDeliveryDate: '2026-10-01', notes: null
    });
  });

  // ── Reading a saved line ─────────────────────────────────────────────────────

  it('reads a saved line\'s dates as their own days and round-trips them unchanged', () => {
    const d = draftFromLine(line({ lineStatus: 'PARTIAL', canSupplyQuantity: 600, estimatedDeliveryDate: '2026-10-25T00:00:00Z' }));
    expect(d.estimatedDeliveryDate?.getDate()).toBe(25);
    expect(d.requestedDeliveryDate?.getDate()).toBe(20);
    expect(toLineRequest(d).estimatedDeliveryDate).toBe('2026-10-25');
    expect(toLineRequest(d).requestedDeliveryDate).toBe('2026-10-20');
  });

  it('counts the lines that stand between review and complete (PENDING and UNDER_REVIEW)', () => {
    expect(undecidedLineCount([line(), line({ lineStatus: 'UNDER_REVIEW' }), line({ lineStatus: 'CAN_SUPPLY' })])).toBe(2);
  });

  it('summarises an evaluated line for its sub-row', () => {
    expect(lineSummary(line({ lineStatus: 'PARTIAL', canSupplyQuantity: 600, estimatedDeliveryDate: '2026-10-25' })))
      .toContain('Can supply: 600 M by 25 Oct 2026');
    const cannot = lineSummary(line({
      lineStatus: 'CANNOT_SUPPLY', rejectionReasonCode: 'DIS', rejectionReasonDescription: 'Product discontinued',
      alternativeVariantName: 'Stainless Steel Sheet 316L', alternativeVariantSku: 'SS-316L'
    }));
    expect(cannot).toContain('Reason: DIS — Product discontinued');
    expect(cannot).toContain('Alternative: Stainless Steel Sheet 316L (SS-316L)');
    expect(lineSummary(line())).toEqual([]);
  });
});
