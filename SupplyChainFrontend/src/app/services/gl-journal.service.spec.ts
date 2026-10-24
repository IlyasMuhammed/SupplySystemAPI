import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { GlJournalService } from './gl-journal.service';
import { environment } from '../../environments/environment';

const BASE = `${environment.apiUrl}/finance`;
const ok = <T>(result: T) => ({ success: true, message: '', result });

describe('GlJournalService (A47 API-CONTRACT)', () => {
  let s: GlJournalService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    s = TestBed.inject(GlJournalService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('§1 journal types: list with activeOnly, create, update, deactivate / activate (PATCH), delete', () => {
    let list: any;
    s.getJournalTypes().subscribe(x => list = x);
    const get = http.expectOne(r => r.url === `${BASE}/journal-types`);
    expect(get.request.params.get('activeOnly')).toBe('false');
    get.flush(ok([{ uuid: 't1', code: 'SALE' }]));
    expect(list[0].code).toBe('SALE');

    s.getJournalTypes(true).subscribe();
    expect(http.expectOne(r => r.url === `${BASE}/journal-types`).request.params.get('activeOnly')).toBe('true');

    const body = { code: 'PROJ', name: 'Project', type: 'Miscellaneous', sequencePrefix: 'PROJ', requiresPartner: false, autoPost: false };
    s.createJournalType(body).subscribe();
    const post = http.expectOne(`${BASE}/journal-types`);
    expect(post.request.method).toBe('POST');
    expect(post.request.body).toEqual(body);
    post.flush(ok({}));

    s.updateJournalType('t1', body).subscribe();
    expect(http.expectOne(`${BASE}/journal-types/t1`).request.method).toBe('PUT');
    s.deactivateJournalType('t1').subscribe();
    expect(http.expectOne(`${BASE}/journal-types/t1/deactivate`).request.method).toBe('PATCH');
    s.activateJournalType('t1').subscribe();
    expect(http.expectOne(`${BASE}/journal-types/t1/activate`).request.method).toBe('PATCH');
    s.deleteJournalType('t1').subscribe();
    expect(http.expectOne(`${BASE}/journal-types/t1`).request.method).toBe('DELETE');
  });

  it('§2 entries: query string (blanks left out), paged answer', () => {
    let page: any;
    s.getEntries({ status: 'Draft', fiscalPeriodUuid: 'p1', fromDate: '2026-10-01', toDate: null, search: '  ', page: 2, pageSize: 50 }).subscribe(p => page = p);
    const req = http.expectOne(r => r.url === `${BASE}/journal-entries`);
    const p = req.request.params;
    expect([p.get('status'), p.get('fiscalPeriodUuid'), p.get('fromDate'), p.get('page'), p.get('pageSize')]).toEqual(['Draft', 'p1', '2026-10-01', '2', '50']);
    expect(p.has('toDate')).toBeFalse();
    expect(p.has('search')).toBeFalse();
    req.flush(ok({ data: [{ uuid: 'e1', entryNumber: 'JV/2026/0001' }], totalRecords: 51, page: 2, pageSize: 50, totalPages: 2 }));
    expect(page.totalRecords).toBe(51);
    expect(page.data[0].entryNumber).toBe('JV/2026/0001');
  });

  it('§2 entries: detail (lines defaulted), create, update, delete, post', () => {
    let d: any;
    s.getEntry('e1').subscribe(x => d = x);
    http.expectOne(`${BASE}/journal-entries/e1`).flush(ok({ uuid: 'e1' }));
    expect(d.lines).toEqual([]);

    s.createEntry({ journalTypeUuid: 't', entryDate: '2026-10-20', clientRequestId: 'c1', postImmediately: true, lines: [] }).subscribe();
    const c = http.expectOne(`${BASE}/journal-entries`);
    expect(c.request.method).toBe('POST');
    expect(c.request.body.clientRequestId).toBe('c1');
    c.flush(ok({}));

    s.updateEntry('e1', { entryDate: '2026-10-20', lines: [] }).subscribe();
    expect(http.expectOne(`${BASE}/journal-entries/e1`).request.method).toBe('PUT');
    s.deleteEntry('e1').subscribe();
    expect(http.expectOne(`${BASE}/journal-entries/e1`).request.method).toBe('DELETE');
    s.postEntry('e1').subscribe();
    expect(http.expectOne(`${BASE}/journal-entries/e1/post`).request.method).toBe('POST');
  });

  it('§0 a success:false envelope becomes an error carrying the body', () => {
    let err: any;
    s.getEntry('e1').subscribe({ error: e => err = e });
    http.expectOne(`${BASE}/journal-entries/e1`).flush({ success: false, message: 'Journal entry not found.', result: null });
    expect(err.error.message).toBe('Journal entry not found.');
  });

  it('§3 / C-1 reverse: trimmed reason, optional date; the result (or a bare detail) as { reversal }', () => {
    let r: any;
    s.reverseEntry('e1', { reason: '  wrong account ', reversalDate: null }).subscribe(x => r = x);
    const req = http.expectOne(`${BASE}/journal-entries/e1/reverse`);
    expect(req.request.body).toEqual({ reason: 'wrong account' });
    req.flush(ok({ reversal: { uuid: 'e2', entryNumber: 'JV/2026/0002' }, original: { uuid: 'e1', entryNumber: 'JV/2026/0001' }, reconcileGroupNumbers: ['REC/2026/0001'] }));
    expect(r.reversal.entryNumber).toBe('JV/2026/0002');

    s.reverseEntry('e1', { reason: 'x', reversalDate: '2026-10-31' }).subscribe(x => r = x);
    const bare = http.expectOne(`${BASE}/journal-entries/e1/reverse`);
    expect(bare.request.body.reversalDate).toBe('2026-10-31');
    bare.flush(ok({ uuid: 'e3', entryNumber: 'JV/2026/0003', lines: [] }));
    expect(r.reversal.uuid).toBe('e3');
    expect(r.reconcileGroupNumbers).toEqual([]);
  });

  it('§4 reconciliation: open items, suggest, reconcile, group, unreconcile', () => {
    s.getOpenItems({ accountUuid: 'a1', partnerUuid: null, fromDate: '2026-01-01' }).subscribe();
    const oi = http.expectOne(r => r.url === `${BASE}/reconciliation/open-items`);
    expect(oi.request.params.get('accountUuid')).toBe('a1');
    expect(oi.request.params.has('partnerUuid')).toBeFalse();
    expect(oi.request.params.get('fromDate')).toBe('2026-01-01');
    oi.flush(ok([]));

    s.suggestMatches('a1', ['l1']).subscribe();
    const sg = http.expectOne(`${BASE}/reconciliation/suggest`);
    expect(sg.request.body).toEqual({ accountUuid: 'a1', lineUuids: ['l1'], maxResults: 10 });
    sg.flush(ok([]));

    let g: any;
    s.reconcile(['l1', 'l2']).subscribe(x => g = x);
    const rc = http.expectOne(`${BASE}/reconciliation/reconcile`);
    expect(rc.request.body).toEqual({ lineUuids: ['l1', 'l2'] });
    rc.flush(ok({ uuid: 'g1', groupNumber: 'REC/2026/0001' }));
    expect(g.lines).toEqual([]);

    s.getReconcileGroup('g1').subscribe();
    http.expectOne(`${BASE}/reconciliation/g1`).flush(ok({ uuid: 'g1', lines: [] }));
    let groups: any;
    s.getReconcileGroups({ accountUuid: 'a1', partnerUuid: null, page: 2 }).subscribe(x => groups = x);
    const gl = http.expectOne(r => r.url === `${BASE}/reconciliation/groups`);
    expect(gl.request.params.get('accountUuid')).toBe('a1');
    expect(gl.request.params.has('partnerUuid')).toBeFalse();
    expect([gl.request.params.get('page'), gl.request.params.get('pageSize')]).toEqual(['2', '25']);
    gl.flush(ok({ data: [{ uuid: 'g1', groupNumber: 'REC/2026/0001', lineCount: 2 }], totalRecords: 26, page: 2, pageSize: 25 }));
    expect(groups.totalRecords).toBe(26);
    expect(groups.data[0].lineCount).toBe(2);

    s.unreconcile('g1').subscribe();
    expect(http.expectOne(`${BASE}/reconciliation/g1/unreconcile`).request.method).toBe('POST');
  });

  it('§5 trial balance, export (blob), account ledger (page nested)', () => {
    s.getTrialBalance({ fiscalYearUuid: 'y1', includeOpeningBalances: true, includeClosingEntries: false, includeZeroBalances: false }).subscribe();
    const tb = http.expectOne(r => r.url === `${BASE}/trial-balance`);
    expect(tb.request.params.get('fiscalYearUuid')).toBe('y1');
    expect(tb.request.params.get('includeClosingEntries')).toBe('false');
    expect(tb.request.params.has('fromDate')).toBeFalse();
    tb.flush(ok({ lines: [] }));

    s.exportTrialBalance({ fromDate: '2026-10-01', toDate: '2026-10-31' }).subscribe();
    const ex = http.expectOne(r => r.url === `${BASE}/trial-balance/export`);
    expect(ex.request.responseType).toBe('blob');
    ex.flush(new Blob(['x']));

    let l: any;
    s.getAccountLedger('a1', { fromDate: '2026-10-01', toDate: '2026-10-31', page: 2 }).subscribe(x => l = x);
    const lg = http.expectOne(r => r.url === `${BASE}/accounts/a1/ledger`);
    expect(lg.request.params.get('page')).toBe('2');
    expect(lg.request.params.get('pageSize')).toBe('50');
    lg.flush(ok({ openingBalance: 100, closingBalance: 50, page: { data: [{ lineUuid: 'x' }], totalRecords: 51, page: 2, pageSize: 50 } }));
    expect(l.page.totalRecords).toBe(51);
    expect(l.openingBalance).toBe(100);
  });

  it('§6 period close: overview sorted, close / reopen / final-close, year-end preview and execute', () => {
    let o: any;
    s.getPeriodCloseStatus('y1').subscribe(x => o = x);
    http.expectOne(`${BASE}/period-close/y1/status`).flush(ok({ periods: [{ periodNumber: 2 }, { periodNumber: 1 }] }));
    expect(o.periods.map((p: any) => p.periodNumber)).toEqual([1, 2]);

    s.closePeriod('p1', 'AdjustmentOnly').subscribe();
    expect(http.expectOne(`${BASE}/period-close/p1/close`).request.body).toEqual({ targetStatus: 'AdjustmentOnly' });
    s.reopenPeriod('p1').subscribe();
    http.expectOne(`${BASE}/period-close/p1/reopen`).flush(ok({}));
    s.finalClosePeriod('p1').subscribe();
    http.expectOne(`${BASE}/period-close/p1/final-close`).flush(ok({}));

    s.previewYearEnd('y1', 'a9').subscribe();
    const pv = http.expectOne(r => r.url === `${BASE}/year-end-close/y1/preview`);
    expect(pv.request.params.get('retainedEarningsAccountUuid')).toBe('a9');
    pv.flush(ok({ lines: [] }));
    s.executeYearEnd('y1', null).subscribe();
    expect(http.expectOne(`${BASE}/year-end-close/y1/execute`).request.body).toEqual({});
  });
});
