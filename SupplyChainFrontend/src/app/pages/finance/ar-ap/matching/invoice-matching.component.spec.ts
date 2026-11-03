import { of, throwError } from 'rxjs';

import { InvoiceMatchingComponent, matchTotals } from './invoice-matching.component';
import { MatchingListComponent } from './matching-list.component';
import { match, matchSummary, mount, page } from '../ar-ap.testing';

describe('InvoiceMatchingComponent (A48 tasks 571–575)', () => {
  const setup = (perms: string[], over = {}, prep: (api: any) => void = () => {}) => mount(InvoiceMatchingComponent, {
    perms, params: { uuid: 'i1' }, prepare: api => { api.getMatch.and.returnValue(of(match(over))); prep(api); }
  });

  it('PO vs GRN vs invoice per line, variances highlighted, totals from the lines (572–573)', async () => {
    const m = await setup(['AP_VIEW']);
    expect(m.api.getMatch).toHaveBeenCalledWith('i1');
    expect(m.text('status')).toBe('Partial match');
    expect(m.text('line-status-2')).toContain('Price mismatch');
    expect(m.text('var-2')).toContain('Price +0.50/unit (+3.33%)');
    expect(m.text('line-status-1')).toContain('Matched');
    expect(m.text('po-total')).toBe('7,500.00');
    expect(m.text('inv-total')).toContain('7,600.00');
    expect(m.text('inv-total')).toContain('total 7,980.00 PKR');
    expect(m.text('variance')).toContain('+100.00');
    expect(m.text('tolerance')).toContain('DEFAULT');
    expect(matchTotals(match({ variancePercent: null })).percent).toBe(1.33);
  });

  it('a line with no PO behind it says so; an auto-approval refusal is shown', async () => {
    const m = await setup(['AP_VIEW'], {
      autoApproveMessage: 'No exchange rate for USD on 2026-10-25.',
      lines: [{ lineNo: 1, itemDescription: 'Freight', poQuantity: null, poUnitPrice: null, poLineTotal: null, invoiceQuantity: 1, invoiceUnitPrice: 50,
        invoiceLineTotal: 50, lineStatus: 'Unmatched' }]
    });
    expect(m.text('line-1')).toContain('no PO line');
    expect(m.text('line-status-1')).toContain('No PO line');
    expect(m.text('auto-approve-message')).toContain('No exchange rate');
  });

  it('reject with a reason (MATCH_APPROVE, §6)', async () => {
    const m = await setup(['MATCH_APPROVE'], {}, api => api.rejectMatch.and.returnValue(of(match({ matchStatus: 'Rejected' }))));
    m.c.openReason('reject');
    m.c.submitOverride();
    expect(m.c.dialogError).toBe('Say why the invoice is being rejected.');
    m.c.reason = 'x'.repeat(301);
    m.c.submitOverride();
    expect(m.c.dialogError).toBe('The reason can be at most 300 characters.');
    m.fixture.detectChanges();
    expect(document.querySelector('#im-reason')?.getAttribute('maxlength')).toBe('300');
    m.c.reason = 'Wrong prices';
    m.c.submitOverride();
    m.fixture.detectChanges();
    expect(m.api.rejectMatch).toHaveBeenCalledWith('i1', 'Wrong prices');
    expect(m.text('status')).toBe('Rejected');
    expect(m.q('reject')).toBeNull();
  });

  it('override needs MATCH_OVERRIDE and a reason (574); approve needs MATCH_APPROVE once overridden (575)', async () => {
    const viewer = await setup(['AP_VIEW']);
    expect(viewer.q('override')).toBeNull();
    expect(viewer.q('approve')).toBeNull();

    const m = await setup(['AP_VIEW', 'MATCH_OVERRIDE', 'MATCH_APPROVE'], {}, api => {
      api.overrideMatch.and.returnValue(of(match({ overrideReason: 'Agreed price increase', overriddenByName: 'J. Smith' })));
      api.approveMatch.and.returnValue(of(match({ matchStatus: 'Approved', overrideReason: 'Agreed price increase' })));
    });
    expect(m.q('override')).not.toBeNull();
    expect(m.q('approve')).toBeNull();
    m.c.openOverride();
    m.c.submitOverride();
    expect(m.c.dialogError).toBe('An override needs a reason.');
    m.c.reason = 'Agreed price increase';
    m.c.submitOverride();
    m.fixture.detectChanges();
    expect(m.api.overrideMatch).toHaveBeenCalledWith('i1', 'Agreed price increase');
    expect(m.text('override-note')).toContain('J. Smith');
    expect(m.text('status')).toBe('Overridden');
    m.c.confirmApprove();
    m.fixture.detectChanges();
    expect(m.api.approveMatch).toHaveBeenCalledWith('i1');
    expect(m.q('approved-note')).not.toBeNull();
  });

  it('re-match 3-way / 2-way with MATCH_EXECUTE; a refusal is a toast', async () => {
    const m = await setup(['MATCH_EXECUTE'], {}, api => api.match.and.returnValue(throwError(() => ({ status: 409, error: { message: 'Invoice INV-0089 is Approved; only a pending, matched or variance invoice can be matched.' } }))));
    m.c.rematch('TwoWay');
    expect(m.api.match).toHaveBeenCalledWith('i1', 'TwoWay');
    expect(m.toast).toHaveBeenCalledWith(jasmine.objectContaining({ severity: 'error' }));
  });

  it('REV: 2-way is offered only when no goods were received; the approve 409 is shown', async () => {
    const received = await setup(['MATCH_EXECUTE']);
    expect(received.q('rematch')).not.toBeNull();
    expect(received.q('rematch-2way')).toBeNull();
    const none = await setup(['MATCH_EXECUTE'], { grnUuid: null, grnNumber: null, lines: match().lines.map(l => ({ ...l, grnQuantity: null })) });
    expect(none.q('rematch-2way')).not.toBeNull();
    const m = await setup(['MATCH_APPROVE'], { matchStatus: 'Matched', lineMatchResult: 'Matched' }, api => api.approveMatch.and.returnValue(throwError(() => ({
      status: 409, error: { message: 'Invoice INV-0089 no longer matches its purchase order (price changed); match it again.' } }))));
    m.c.confirmApprove();
    expect(m.toast).toHaveBeenCalledWith(jasmine.objectContaining({ severity: 'error', detail: jasmine.stringContaining('no longer matches its purchase order') }));
  });

  it('a matched invoice is approvable without override', async () => {
    const m = await setup(['MATCH_APPROVE'], { matchStatus: 'Matched', lineMatchResult: 'Matched', withinTolerance: true });
    expect(m.q('approve')).not.toBeNull();
    expect(m.c.overridable).toBeFalse();
  });
});

describe('MatchingListComponent (A48 task 576)', () => {
  it('pending list paged on the server, filtered by result and searched', async () => {
    const m = await mount(MatchingListComponent, { perms: ['AP_VIEW'], prepare: api =>
      api.getPendingMatches.and.returnValue(page([matchSummary(), matchSummary({ invoiceUuid: 'i2', invoiceNumber: 'INV-0090', overridden: true })])) });
    m.c.onLazy({ first: 0, rows: 25 });
    m.fixture.detectChanges();
    expect(m.text('match-i1')).toContain('INV-0089');
    expect(m.text('match-i1')).toContain('Partial match');
    expect(m.text('match-i1')).toContain('7,980.00');
    expect(m.text('match-i2')).toContain('Overridden');
    m.c.setResult('Mismatch');
    expect(m.api.getPendingMatches).toHaveBeenCalledWith({ result: 'Mismatch', search: '', page: 1, pageSize: 25 });
    m.c.search = 'PO-0234';
    m.c.load(1);
    expect(m.api.getPendingMatches).toHaveBeenCalledWith({ result: 'Mismatch', search: 'PO-0234', page: 1, pageSize: 25 });
  });
});
