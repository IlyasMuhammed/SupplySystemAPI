import {
  addDays, agingCsv, blankLevel, blockedRows, bucketLabels, bucketsProblem, canApproveMatch, canOverrideMatch, creditBlockOf, creditModeHelp,
  creditStatus, creditWarningOf, criteriaProblem,
  applicationProblem, arApErrorAsync, creditMemoProblem, csvCell, daysBetween, dueText, effectiveLimit, groupLive, labelOf, matchState, parseBuckets, priceVariance, procedureProblem,
  reasonProblem, renumber, reportLabels, runCancellable, runEditable, runProgress, runTotals, stepForStatus, tempLimitProblem, termProblem, termShort,
  toleranceProblem
} from './ar-ap.shared';
import { aging, run } from './ar-ap.testing';
import { DunningProcedureSave } from '../../../services/ar-ap.service';

describe('A48 ar-ap.shared', () => {
  it('dates: addDays across a month end, daysBetween', () => {
    expect(addDays('2026-10-30', 3)).toBe('2026-11-02');
    expect(daysBetween('2026-10-25', '2026-11-04')).toBe(10);
    expect(daysBetween('2026-10-25', '2026-10-20')).toBe(-5);
  });

  it('buckets (BR-AG-005/006, contract §5 texts)', () => {
    expect(bucketLabels([30, 60, 90, 120])).toEqual(['Current', '1-30', '31-60', '61-90', '91-120', '120+']);
    expect(parseBuckets('30, 60 90')).toEqual([30, 60, 90]);
    expect(parseBuckets('30, x')).toBeNull();
    expect(bucketsProblem([30])).toBe('At least 3 buckets are needed (2 boundaries).');
    expect(bucketsProblem(null)).toBe('At least 3 buckets are needed (2 boundaries).');
    expect(bucketsProblem([60, 30])).toBe('Bucket boundaries must be positive and ascending.');
    expect(bucketsProblem([0, 30])).toBe('Bucket boundaries must be positive and ascending.');
    expect(bucketsProblem([30, 60])).toBeNull();
    expect(reportLabels(aging({ bucketLabels: [] }))).toEqual(['Current', '1-30', '31-60', '61-90', '91-120', '120+']);
  });

  it('REV-24 — CSV cells: formula-looking text is prefixed with \', numbers (also negative) are not', () => {
    expect(csvCell('=HYPERLINK("x")')).toBe(`"'=HYPERLINK(""x"")"`);
    expect(csvCell('+1')).toBe("'+1");
    expect(csvCell('-cmd')).toBe("'-cmd");
    expect(csvCell('@SUM(A1)')).toBe("'@SUM(A1)");
    expect(csvCell('\tx')).toBe("'\tx");
    expect(csvCell('-12.00')).toBe('-12.00');
    expect(csvCell(-12)).toBe('-12');
    expect(csvCell('Acme')).toBe('Acme');
    const csv = agingCsv(aging({ lines: [{ partner: { uuid: 'x', name: '=cmd|calc', code: '@C1' }, bucketAmounts: [-5], totalOutstanding: -5 }] }), 'Customer');
    expect(csv.split('\r\n')[1]).toBe("'=cmd|calc,'@C1,-5.00,0.00,0.00,0.00,0.00,0.00,-5.00");
  });

  it('aging CSV: header, a row per partner, total; quotes commas', () => {
    const csv = agingCsv(aging({ lines: [{ partner: { uuid: 'x', name: 'Acme, Inc', code: 'C1' }, bucketAmounts: [1, 2], totalOutstanding: 3 }] }), 'Customer');
    const rows = csv.trim().split('\r\n');
    expect(rows[0]).toBe('Customer,Code,Current,1-30,31-60,61-90,91-120,120+,Total');
    expect(rows[1]).toBe('"Acme, Inc",C1,1.00,2.00,0.00,0.00,0.00,0.00,3.00');
    expect(rows[2].startsWith('TOTAL,')).toBeTrue();
  });

  it('credit (BR-CR-002, contract §2 texts)', () => {
    expect(effectiveLimit(100, 120, '2026-11-30', '2026-10-25')).toBe(120);
    expect(effectiveLimit(100, 120, '2026-10-01', '2026-10-25')).toBe(100);
    expect(effectiveLimit(null, null, null)).toBeNull();
    expect(creditStatus({ isOnHold: true, isOverLimit: true }).label).toBe('On hold');
    expect(creditStatus({ isOnHold: false, isOverLimit: true }).label).toBe('Over limit');
    expect(reasonProblem('  ')).toBe('A reason is required.');
    expect(reasonProblem('', 'An override needs a reason.')).toBe('An override needs a reason.');
    expect(tempLimitProblem(0, '2026-12-01', 'x', 100, 'Acme', '2026-10-25')).toBe('The temporary limit must be greater than zero.');
    expect(tempLimitProblem(200, '2026-12-01', 'x', 0, 'Acme', '2026-10-25'))
      .toBe('Acme has no permanent credit limit; set one on the customer before granting a temporary one.');
    expect(tempLimitProblem(100, '2026-12-01', 'x', 100, 'Acme', '2026-10-25')).toBe('The temporary limit must be above the permanent limit (100.00).');
    expect(tempLimitProblem(200, '2026-10-25', 'x', 100, 'Acme', '2026-10-25')).toBe('The expiry date must be after today.');
    expect(tempLimitProblem(200, '2026-12-01', '', 100, 'Acme', '2026-10-25')).toBe('A reason is required.');
    expect(tempLimitProblem(200, '2026-12-01', 'Large order', 100, 'Acme', '2026-10-25')).toBeNull();
  });

  it('dunning procedure rules (BR-DN-004, contract §3)', () => {
    const d: DunningProcedureSave = { code: 'STD', name: 'Standard', isDefault: true, isActive: true, graceDays: 5, minimumAmount: 0, blockSalesAtLevel: 2,
      levels: [blankLevel(1), blankLevel(2)] };
    expect(procedureProblem(d)).toBeNull();
    expect(procedureProblem({ ...d, code: 'a b' })).toContain('letters, digits');
    expect(procedureProblem({ ...d, levels: [blankLevel(1), blankLevel(3)] })).toBe('Dunning levels must be numbered 1 to 2 without gaps.');
    expect(procedureProblem({ ...d, blockSalesAtLevel: 3 })).toContain('between 1 and 2');
    expect(procedureProblem({ ...d, levels: [] })).toBe('Add at least one level.');
    expect(procedureProblem({ ...d, levels: [blankLevel(1), { ...blankLevel(2), daysAfterPrevious: 0 }] })).toContain('at least 1');
    expect(renumber([blankLevel(3), blankLevel(5)]).map(l => l.levelNumber)).toEqual([1, 2]);
  });

  it('tolerance and early-payment term rules (contract §6 / §7 texts, BR-EP-004)', () => {
    const t = { name: 'X', vendorUuid: null, priceTolerancePercent: 2, priceToleranceAmount: 0, quantityTolerancePercent: 0, totalTolerancePercent: 0,
      totalToleranceAmount: 0, autoApproveWithin: false, isDefault: false, isActive: true };
    expect(toleranceProblem(t)).toBeNull();
    expect(toleranceProblem({ ...t, priceToleranceAmount: -1 })).toBe('Tolerances cannot be negative.');
    expect(toleranceProblem({ ...t, quantityTolerancePercent: 101 })).toBe('A percentage cannot exceed 100.');
    const e = { code: '2-10', name: '2/10 Net 30', discountPercent: 2, discountDays: 10, netDays: 30, isActive: true };
    expect(termProblem(e)).toBeNull();
    expect(termProblem({ ...e, discountDays: 30 })).toBe('Discount days must be fewer than net days.');
    expect(termProblem({ ...e, discountPercent: 0 })).toBe('The discount percent must be greater than 0 and less than 100.');
    expect(termShort(e)).toBe('2/10 Net 30');
  });

  it('payment run arithmetic: removed and blocked items do not count (BR-PR-002/003)', () => {
    const r = run();
    expect(runTotals(r)).toEqual({ vendors: 2, items: 3, total: 18000, discount: 102, net: 17898 });
    r.vendorGroups[1].items[0].status = 'Removed';
    expect(runTotals(r)).toEqual({ vendors: 1, items: 2, total: 13300, discount: 102, net: 13198 });
    expect(groupLive(r.vendorGroups[1])).toEqual({ items: 0, net: 0 });
    expect(blockedRows(r).map(b => [b.vendorName, b.item.invoiceNumber, b.item.blockReason])).toEqual([['Supplier C', 'INV-c1', 'Disputed']]);
    r.vendorGroups[0].batchStatus = 'Paid';
    r.vendorGroups[1].batchStatus = 'Failed';
    expect(runProgress(r)).toEqual({ done: 2, paid: 1, failed: 1, total: 2 });
    expect(stepForStatus('Proposed')).toBe(2);
    expect(stepForStatus('Approved')).toBe(3);
    expect(stepForStatus('Completed')).toBe(4);
    expect(runEditable('Reviewing')).toBeTrue();
    expect(runEditable('Approved')).toBeFalse();
    expect(runCancellable('Approved')).toBeTrue();
    expect(runCancellable('Completed')).toBeFalse();
    expect(dueText(-3)).toBe('3d late');
    expect(dueText(0)).toBe('due today');
    expect(criteriaProblem('2026-10-01', '2026-10-31', '2026-10-25')).toBe('The payment date cannot be before today.');
    expect(criteriaProblem('2026-10-28', '', '2026-10-25')).toBe('Choose the due-date cutoff.');
  });

  it('match state / override / approve (contract §6)', () => {
    expect(matchState({ matchStatus: 'Variance', lineMatchResult: 'PartialMatch' })).toBe('PartialMatch');
    expect(matchState({ matchStatus: 'Variance', lineMatchResult: 'Mismatch', overrideReason: 'ok' })).toBe('Overridden');
    expect(matchState({ matchStatus: 'Approved', lineMatchResult: 'Matched' })).toBe('Approved');
    expect(matchState({ matchStatus: 'Pending' })).toBe('Pending');
    expect(canOverrideMatch({ matchStatus: 'Variance', lineMatchResult: 'Mismatch' })).toBeTrue();
    expect(canOverrideMatch({ matchStatus: 'Matched', lineMatchResult: 'Matched' })).toBeFalse();
    expect(canOverrideMatch({ matchStatus: 'Variance', lineMatchResult: 'Mismatch', overrideReason: 'x' })).toBeFalse();
    expect(canApproveMatch({ matchStatus: 'Matched', lineMatchResult: 'Matched' })).toBeTrue();
    expect(canApproveMatch({ matchStatus: 'Variance', lineMatchResult: 'Mismatch' })).toBeFalse();
    expect(canApproveMatch({ matchStatus: 'Variance', lineMatchResult: 'Mismatch', overrideReason: 'x' })).toBeTrue();
    expect(canApproveMatch({ matchStatus: 'Approved', lineMatchResult: 'Matched' })).toBeFalse();
    expect(priceVariance(15, 15.5)).toEqual({ amount: 0.5, percent: 3.33 });
  });

  it('memo application and creation checks (contract §4 texts)', () => {
    const rows = [{ invoiceUuid: 'a', invoiceNumber: 'SI-1', open: 100, amount: 60 }, { invoiceUuid: 'b', invoiceNumber: 'SI-2', open: 50, amount: 50 }];
    expect(applicationProblem(rows, 200, 'SCN-1')).toBeNull();
    expect(applicationProblem(rows, 100, 'SCN-1')).toBe('SCN-1 has only 100.00 left to apply.');
    expect(applicationProblem([{ ...rows[0], amount: 120 }], 500, 'SCN-1')).toBe('Invoice SI-1 has only 100.00 open.');
    expect(applicationProblem([{ ...rows[0], amount: null }], 500, 'SCN-1')).toBe('Enter an amount against at least one invoice.');
    expect(creditMemoProblem('', [{ description: 'x', amount: 1 }])).toBe('A reason is required.');
    expect(creditMemoProblem('Return', [{ description: '', amount: 1 }])).toBe('Line 1: enter the description.');
    expect(creditMemoProblem('Return', [{ description: 'x', amount: 0 }])).toBe('Line 1: the amount must be greater than zero.');
    expect(creditMemoProblem('Return', [{ description: 'x', amount: 5 }])).toBeNull();
  });

  it('arApErrorAsync reads the JSON refusal out of a blob error (failed downloads)', async () => {
    const blob = new Blob([JSON.stringify({ success: false, message: 'Payment run PR-1 is Proposed; it cannot be exported yet.' })], { type: 'application/json' });
    expect(await arApErrorAsync({ status: 409, error: blob }, 'fallback')).toBe('Payment run PR-1 is Proposed; it cannot be exported yet.');
    expect(await arApErrorAsync({ status: 500, error: new Blob(['<html>']) }, 'fallback')).toBe('fallback');
    expect(await arApErrorAsync({ status: 400, error: { message: 'Plain' } }, 'fallback')).toBe('Plain');
  });

  it('match state reads the list\'s overridden flag', () => {
    expect(matchState({ matchStatus: 'Variance', lineMatchResult: 'Mismatch', overridden: true })).toBe('Overridden');
  });

  it('credit block / warning readers (contract §2) and the mode help', () => {
    const msg = 'Credit check failed: Acme is on credit hold: Late. Confirming needs the CREDIT_OVERRIDE permission and an override reason.';
    expect(creditBlockOf({ status: 409, error: { message: msg, result: { creditCheck: { onHold: true } } } })).toEqual({ message: msg, check: { onHold: true } as any });
    expect(creditBlockOf({ status: 400, error: { message: 'Line 1 has no price.' } })).toBeNull();
    expect(creditWarningOf({ result: { creditWarning: 'Over the limit.' } })).toBe('Over the limit.');
    expect(creditWarningOf({ message: 'Invoice raised. Exceeds the credit limit of 100.' })).toContain('credit limit');
    expect(creditWarningOf({ message: 'Draft invoice SI-1 raised.' })).toBeNull();
    expect(creditModeHelp('Off')).toContain('not checked');
  });

  it('labels: upper-snake codes and A48 camel words', () => {
    expect(labelOf('BANK_TRANSFER')).toBe('Bank Transfer');
    expect(labelOf('TempIncrease')).toBe('Temporary limit');
    expect(labelOf('PartiallyPaid')).toBe('Partially paid');
    expect(labelOf(null)).toBe('');
  });
});
