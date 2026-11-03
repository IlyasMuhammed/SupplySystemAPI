import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { ArApService } from './ar-ap.service';
import { environment } from '../../environments/environment';
import { runWire } from '../pages/finance/ar-ap/ar-ap.testing';

const BASE = `${environment.apiUrl}/finance`;
const ok = <T>(result: T) => ({ success: true, message: '', result });
const pg = <T>(data: T[]) => ok({ data, totalRecords: data.length, page: 1, pageSize: 25, totalPages: 1 });

describe('ArApService (A48 API-CONTRACT + models as built)', () => {
  let s: ArApService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    s = TestBed.inject(ArApService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('§1 profile: GET and PUT carry ?role, settings GET / PUT', () => {
    let p: any;
    s.getProfile('c1', 'Customer').subscribe(x => p = x);
    const get = http.expectOne(r => r.url === `${BASE}/partner-profiles/c1`);
    expect(get.request.params.get('role')).toBe('Customer');
    get.flush(ok({ partnerUuid: 'c1', partnerName: 'Acme' }));
    expect(p.partnerName).toBe('Acme');

    s.updateProfile('v1', 'Vendor', { paymentPriority: 'High', rowVersion: 'rv' }).subscribe();
    const put = http.expectOne(r => r.url === `${BASE}/partner-profiles/v1`);
    expect(put.request.method).toBe('PUT');
    expect(put.request.params.get('role')).toBe('Vendor');
    expect(put.request.body).toEqual({ paymentPriority: 'High', rowVersion: 'rv' });
    put.flush(ok({}));

    s.getSettings().subscribe();
    http.expectOne(`${BASE}/ar-ap/settings`).flush(ok({ creditControlMode: 'Off' }));
    s.updateSettings({ creditControlMode: 'Block' }).subscribe();
    const ps = http.expectOne(`${BASE}/ar-ap/settings`);
    expect(ps.request.method).toBe('PUT');
    expect(ps.request.body).toEqual({ creditControlMode: 'Block' });
  });

  it('§2 credit: exposure, list filters (false left out), hold / release / temporary limit (null clears) / risk tier, paged history', () => {
    s.getExposure('c1').subscribe();
    http.expectOne(`${BASE}/credit/c1/exposure`).flush(ok({}));

    let list: any;
    s.getExposures({ onHold: true, overLimit: false, search: ' ac ', page: 2 }).subscribe(x => list = x);
    const l = http.expectOne(r => r.url === `${BASE}/credit/exposures`);
    expect(l.request.params.get('onHold')).toBe('true');
    expect(l.request.params.has('overLimit')).toBeFalse();
    expect(l.request.params.get('search')).toBe('ac');
    expect(l.request.params.get('page')).toBe('2');
    l.flush(ok({ data: [{ customerUuid: 'c1' }], totalRecords: 26, page: 2, pageSize: 25 }));
    expect(list.totalRecords).toBe(26);

    s.placeHold('c1', 'Late').subscribe();
    expect(http.expectOne(`${BASE}/credit/c1/hold`).request.body).toEqual({ reason: 'Late' });
    s.releaseHold('c1', 'Paid').subscribe();
    expect(http.expectOne(`${BASE}/credit/c1/release`).request.body).toEqual({ reason: 'Paid' });
    s.setTemporaryLimit('c1', { amount: null, expiryDate: null, reason: 'Done' }).subscribe();
    expect(http.expectOne(`${BASE}/credit/c1/temporary-limit`).request.body.amount).toBeNull();
    s.setRiskTier('c1', 'High', 'Risky').subscribe();
    const rt = http.expectOne(`${BASE}/credit/c1/risk-tier`);
    expect(rt.request.method).toBe('PUT');
    expect(rt.request.body).toEqual({ riskTier: 'High', reason: 'Risky' });

    let hist: any;
    s.getCreditHistory('c1').subscribe(x => hist = x);
    http.expectOne(r => r.url === `${BASE}/credit/c1/history`).flush(pg([{ uuid: 'h1' }]));
    expect(hist.data.length).toBe(1);
  });

  it('§3 dunning: preview body, skip / execute / cancel routes, paged runs, reset with level, history model', () => {
    let run: any;
    s.previewDunningRun('2026-10-25').subscribe(x => run = x);
    const pv = http.expectOne(`${BASE}/dunning/runs/preview`);
    expect(pv.request.body).toEqual({ asOfDate: '2026-10-25' });
    pv.flush(ok({ uuid: 'r1', items: [{ uuid: 'i1', customerUuid: 'c1', customerName: 'Acme' }] }));
    expect(run.items[0].customerName).toBe('Acme');

    s.skipDunningItem('r1', 'i1', 'Disputed').subscribe();
    expect(http.expectOne(`${BASE}/dunning/runs/r1/items/i1/skip`).request.body).toEqual({ reason: 'Disputed' });
    s.executeDunningRun('r1').subscribe();
    http.expectOne(`${BASE}/dunning/runs/r1/execute`).flush(ok({ items: [] }));
    s.cancelDunningRun('r1').subscribe();
    http.expectOne(`${BASE}/dunning/runs/r1/cancel`).flush(ok({ items: [] }));
    s.getDunningRuns(2).subscribe();
    expect(http.expectOne(r => r.url === `${BASE}/dunning/runs`).request.params.get('page')).toBe('2');
    s.resetDunning('c1', 'Agreed').subscribe();
    expect(http.expectOne(`${BASE}/dunning/customers/c1/reset`).request.body).toEqual({ reason: 'Agreed', level: 0 });
    let hist: any;
    s.getCustomerDunningHistory('c1').subscribe(x => hist = x);
    http.expectOne(`${BASE}/dunning/customers/c1/history`).flush(ok({ customerUuid: 'c1', events: [{ kind: 'Notice' }] }));
    expect(hist.events[0].kind).toBe('Notice');

    let procs: any;
    s.getProcedures().subscribe(x => procs = x);
    http.expectOne(`${BASE}/dunning/procedures`).flush(ok([{ uuid: 'p', levels: [{ levelNumber: 2 }, { levelNumber: 1 }] }]));
    expect(procs[0].levels.map((l: any) => l.levelNumber)).toEqual([1, 2]);
  });

  it('§4 credit memos: create, apply, get, applications, open credits', () => {
    s.createCreditMemo({ customerUuid: 'c1', reason: 'Return', lines: [{ description: 'x', amount: 10 }] }).subscribe();
    const c = http.expectOne(`${BASE}/ar/credit-memo`);
    expect(c.request.method).toBe('POST');
    expect(c.request.body.customerUuid).toBe('c1');
    s.applyCreditMemo('n1', [{ invoiceUuid: 'i1', amount: 5 }]).subscribe();
    expect(http.expectOne(`${BASE}/ar/credit-memo/n1/apply`).request.body).toEqual({ applications: [{ invoiceUuid: 'i1', amount: 5 }] });
    s.getCreditMemo('n1').subscribe();
    http.expectOne(`${BASE}/ar/credit-memo/n1`);
    s.getCreditApplications('n1').subscribe();
    http.expectOne(`${BASE}/ar/credit-memo/n1/applications`);
    s.getOpenCredits('c1').subscribe();
    http.expectOne(`${BASE}/ar/customers/c1/open-credits`);
  });

  it('§5 aging: customerUuid / vendorUuid, buckets joined, pageSize 200; the flat partner becomes the line partner; export is a blob', () => {
    let r: any;
    s.getArAging({ asOfDate: '2026-10-25', buckets: [30, 60, 90], basis: 'DocumentDate', partnerUuid: 'c1', includeDetails: true }).subscribe(x => r = x);
    const a = http.expectOne(r => r.url === `${BASE}/ar/aging`);
    const p = a.request.params;
    expect([p.get('asOfDate'), p.get('buckets'), p.get('basis'), p.get('customerUuid'), p.get('includeDetails'), p.get('pageSize')])
      .toEqual(['2026-10-25', '30,60,90', 'DocumentDate', 'c1', 'true', '200']);
    a.flush(ok({ lines: [{ partnerId: 'c1', partnerName: 'Acme', partnerCode: 'C-1', bucketAmounts: [1], totalOutstanding: 1 }], totals: { bucketTotals: [1], grandTotal: 1 } }));
    expect(r.lines[0].partner).toEqual({ uuid: 'c1', name: 'Acme', code: 'C-1' });

    s.getApAging({ partnerUuid: 'v1' }).subscribe();
    expect(http.expectOne(r => r.url === `${BASE}/ap/aging`).request.params.get('vendorUuid')).toBe('v1');

    s.exportArAging({}, 'xlsx').subscribe();
    const ex = http.expectOne(r => r.url === `${BASE}/ar/aging/export`);
    expect(ex.request.params.get('format')).toBe('xlsx');
    expect(ex.request.responseType).toBe('blob');

    s.getCustomerOpenItems('c1', { includeCredits: false }).subscribe();
    expect(http.expectOne(r => r.url === `${BASE}/ar/customers/c1/open-items`).request.params.get('includeCredits')).toBe('false');
    s.getVendorOpenItems('v1').subscribe();
    expect(http.expectOne(r => r.url === `${BASE}/ap/vendors/v1/open-items`).request.params.get('includeCredits')).toBe('true');
  });

  it('§5 statements: from / to / type / format, email body with subject / message, batch body, log by role, reconciliation', () => {
    const q = { fromDate: '2026-07-01', toDate: '2026-09-30', type: 'Activity' as const };
    s.getCustomerStatement('c1', q).subscribe();
    const st = http.expectOne(r => r.url === `${BASE}/ar/customers/c1/statement`);
    expect([st.request.params.get('from'), st.request.params.get('to'), st.request.params.get('type'), st.request.params.get('format')])
      .toEqual(['2026-07-01', '2026-09-30', 'Activity', 'json']);
    st.flush(ok({ partnerId: 'c1', partnerName: 'Acme', sections: [{ currencyCode: 'PKR', lines: [] }] }));

    s.downloadCustomerStatementPdf('c1', q).subscribe();
    const pdf = http.expectOne(r => r.url === `${BASE}/ar/customers/c1/statement`);
    expect(pdf.request.params.get('format')).toBe('pdf');
    expect(pdf.request.responseType).toBe('blob');

    s.emailCustomerStatement('c1', q, { emailTo: 'ar@acme.test', message: 'Hi' }).subscribe();
    expect(http.expectOne(`${BASE}/ar/customers/c1/statement/email`).request.body).toEqual({
      from: '2026-07-01', to: '2026-09-30', type: 'Activity', currencyCode: null, includeAging: true, emailTo: 'ar@acme.test', subject: null, message: 'Hi'
    });
    s.batchStatements({ from: '2026-07-01', to: '2026-09-30', type: 'OpenItems', minimumBalance: 100, sendEmail: true }).subscribe();
    expect(http.expectOne(`${BASE}/ar/statements/batch`).request.body.sendEmail).toBeTrue();
    s.getStatementLog('Customer', 'c1').subscribe();
    const log = http.expectOne(r => r.url === `${BASE}/statements/log`);
    expect(log.request.params.get('role')).toBe('Customer');
    expect(log.request.params.get('partnerUuid')).toBe('c1');

    s.reconcileVendor('v1', { asOfDate: '2026-10-25', vendorClaimedBalance: 500 }).subscribe();
    expect(http.expectOne(`${BASE}/ap/vendors/v1/reconciliation`).request.body.vendorClaimedBalance).toBe(500);
  });

  it('§6 matching is keyed by the supplier invoice; reject; pending filters by result and search', () => {
    s.match('i1', 'TwoWay').subscribe();
    expect(http.expectOne(`${BASE}/matching/invoices/i1/match`).request.body).toEqual({ matchType: 'TwoWay' });
    s.match('i1').subscribe();
    expect(http.expectOne(`${BASE}/matching/invoices/i1/match`).request.body).toEqual({});
    s.getMatch('i1').subscribe();
    http.expectOne(`${BASE}/matching/invoices/i1`);
    s.overrideMatch('i1', 'Agreed').subscribe();
    expect(http.expectOne(`${BASE}/matching/invoices/i1/override`).request.body).toEqual({ reason: 'Agreed' });
    s.approveMatch('i1').subscribe();
    http.expectOne(`${BASE}/matching/invoices/i1/approve`);
    s.rejectMatch('i1', 'Wrong').subscribe();
    expect(http.expectOne(`${BASE}/matching/invoices/i1/reject`).request.body).toEqual({ reason: 'Wrong' });
    s.getPendingMatches({ result: 'Mismatch', search: ' PO-1 ' }).subscribe();
    const pm = http.expectOne(r => r.url === `${BASE}/matching/pending`);
    expect([pm.request.params.get('result'), pm.request.params.get('search')]).toEqual(['Mismatch', 'PO-1']);
    s.updateToleranceRule('t1', {} as any).subscribe();
    expect(http.expectOne(`${BASE}/matching/tolerances/t1`).request.method).toBe('PUT');
  });

  it('§7 schedules: upcoming from / to, discounts, cash-flow weekRows, block / unblock', () => {
    s.getUpcomingPayments('2026-10-25', '2026-11-25').subscribe();
    const u = http.expectOne(r => r.url === `${BASE}/payment-schedules/upcoming`);
    expect([u.request.params.get('from'), u.request.params.get('to')]).toEqual(['2026-10-25', '2026-11-25']);
    s.getDiscountOpportunities('2026-10-25', 7).subscribe();
    expect(http.expectOne(r => r.url === `${BASE}/payment-schedules/discounts`).request.params.get('lookAheadDays')).toBe('7');
    let f: any;
    s.getCashFlow('2026-10-25', 4).subscribe(x => f = x);
    const cf = http.expectOne(r => r.url === `${BASE}/payment-schedules/cash-flow`);
    expect(cf.request.params.get('weeks')).toBe('4');
    cf.flush(ok({ totalDue: 10 }));
    expect(f.weekRows).toEqual([]);
    s.blockSchedule('s1', 'Disputed').subscribe();
    expect(http.expectOne(`${BASE}/payment-schedules/s1/block`).request.body).toEqual({ reason: 'Disputed' });
    s.unblockSchedule('s1').subscribe();
    http.expectOne(`${BASE}/payment-schedules/s1/unblock`);
  });

  it('§8 payment runs: detail mapped from { run, vendorGroups (batches), removedItems, blockedItems, warnings }', () => {
    let r: any;
    s.getPaymentRun('r1').subscribe(x => r = x);
    http.expectOne(`${BASE}/payment-runs/r1`).flush(ok(runWire({ warnings: ['No discount account.'] })));
    expect(r.runNumber).toBe('PR-0042');
    expect(r.status).toBe('Proposed');
    expect(r.vendorGroups[0].vendor).toEqual({ uuid: 'v1', name: 'Supplier A' });
    expect(r.vendorGroups[0].items[0].uuid).toBe('a1');
    expect(r.blockedItems[0]).toEqual(jasmine.objectContaining({ invoiceNumber: 'INV-c1', vendorName: 'Supplier C', isBlocked: true }));
    expect(r.warnings).toEqual(['No discount account.']);

    s.proposePaymentRun({ paymentDate: '2026-10-28', dueDateCutoff: '2026-10-31', includeDiscountItems: true, minPriority: 'Normal' }).subscribe();
    const pr = http.expectOne(`${BASE}/payment-runs/propose`);
    expect(pr.request.body.minPriority).toBe('Normal');
    pr.flush(ok(runWire()));
    s.removePaymentRunItem('r1', 'i1', 'Wrong').subscribe();
    const rm = http.expectOne(`${BASE}/payment-runs/r1/items/i1/remove`);
    expect(rm.request.body).toEqual({ reason: 'Wrong' });
    rm.flush(ok(runWire()));
    s.cancelPaymentRun('r1', 'Not needed').subscribe();
    expect(http.expectOne(`${BASE}/payment-runs/r1/cancel`).request.body).toEqual({ reason: 'Not needed' });
    s.exportPaymentRun('r1').subscribe();
    expect(http.expectOne(r => r.url === `${BASE}/payment-runs/r1/export`).request.params.get('format')).toBe('csv');
    s.getPaymentRuns('Completed').subscribe();
    expect(http.expectOne(r => r.url === `${BASE}/payment-runs`).request.params.get('status')).toBe('Completed');
  });

  it('§8.1 execute / resume: 200 = done, 202 = accepted (executing in the background)', () => {
    let a: any, b: any;
    s.executePaymentRun('r1').subscribe(x => a = x);
    http.expectOne(`${BASE}/payment-runs/r1/execute`).flush(ok(runWire({}, 'Completed')), { status: 200, statusText: 'OK' });
    expect([a.accepted, a.run.status]).toEqual([false, 'Completed']);
    s.resumePaymentRun('r1').subscribe(x => b = x);
    const rs = http.expectOne(`${BASE}/payment-runs/r1/resume`);
    expect(rs.request.method).toBe('POST');
    rs.flush({ success: true, message: 'Payment run is being executed in the background.', result: runWire({}, 'Executing') }, { status: 202, statusText: 'Accepted' });
    expect([b.accepted, b.run.status, b.message]).toEqual([true, 'Executing', 'Payment run is being executed in the background.']);
  });

  it('§9 debit memos: create, apply, applications', () => {
    s.createDebitMemo({ vendorUuid: 'v1', reason: 'REBATE', amount: 50 }).subscribe();
    expect(http.expectOne(`${BASE}/ap/debit-memo`).request.body).toEqual({ vendorUuid: 'v1', reason: 'REBATE', amount: 50 });
    s.applyDebitMemo('d1', [{ invoiceUuid: 'i1', amount: 20 }]).subscribe();
    expect(http.expectOne(`${BASE}/ap/debit-memo/d1/apply`).request.body).toEqual({ applications: [{ invoiceUuid: 'i1', amount: 20 }] });
    s.getDebitApplications('d1').subscribe();
    http.expectOne(`${BASE}/ap/debit-memo/d1/applications`);
    let d: any;
    s.getDebitMemo('d1').subscribe(x => d = x);
    http.expectOne(`${BASE}/ap/debit-memo/d1`).flush(ok({ uuid: 'd1', remainingAmount: 5 }));
    expect(d.applications).toEqual([]);
    let list: any;
    s.getVendorDebitMemos('v1').subscribe(x => list = x);
    const l = http.expectOne(r => r.url === `${BASE}/ap/vendors/v1/debit-memos`);
    expect([l.request.params.get('openOnly'), l.request.params.get('pageSize')]).toEqual(['true', '200']);
    l.flush(pg([{ uuid: 'd1' }]));
    expect(list.data.length).toBe(1);
  });

  it('a success:false envelope is an error carrying the server message', () => {
    let msg = '';
    s.getExposure('c1').subscribe({ error: e => msg = e.message });
    http.expectOne(`${BASE}/credit/c1/exposure`).flush({ success: false, message: 'Nope', result: null });
    expect(msg).toBe('Nope');
  });
});
