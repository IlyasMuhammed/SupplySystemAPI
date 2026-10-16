import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import {
  FinancialPostingService, docTypeLabel, entryTypeLabel, isActionable, journalViews, lineTotals, sourceDocRoute, summaryTiles,
  targetStatusLabel, targetTone
} from './financial-posting.service';
import { environment } from '../../environments/environment';

const BASE = `${environment.apiUrl}/finance/posting`;
const ok = <T>(result: T) => ({ success: true, message: '', result });

describe('FinancialPostingService (A44 D-8 / D-12)', () => {
  let service: FinancialPostingService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(FinancialPostingService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('config: GET and PUT { internalAccountingEnabled, autoPostToGL }', () => {
    let c: any;
    service.getConfig().subscribe(x => c = x);
    http.expectOne(`${BASE}/config`).flush(ok({ internalAccountingEnabled: false, autoPostToGL: true }));
    expect(c.autoPostToGL).toBeTrue();

    service.updateConfig({ internalAccountingEnabled: true, autoPostToGL: false }).subscribe();
    const put = http.expectOne(`${BASE}/config`);
    expect(put.request.method).toBe('PUT');
    expect(put.request.body).toEqual({ internalAccountingEnabled: true, autoPostToGL: false });
    put.flush(ok({ internalAccountingEnabled: true, autoPostToGL: false }));
  });

  it('log: filters as query parameters, blanks left out; a page or a bare list', () => {
    let page: any;
    service.getLog({ status: 'Failed', targetCode: 'quickbooks', docType: 'Payment', from: '2026-10-01', to: null, search: '  ', page: 2, pageSize: 50 }).subscribe(p => page = p);
    const req = http.expectOne(r => r.url === `${BASE}/log`);
    expect(req.request.params.get('status')).toBe('Failed');
    expect(req.request.params.get('targetCode')).toBe('quickbooks');
    expect(req.request.params.get('docType')).toBe('Payment');
    expect(req.request.params.get('from')).toBe('2026-10-01');
    expect(req.request.params.has('to')).toBeFalse();
    expect(req.request.params.has('search')).toBeFalse();
    expect(req.request.params.get('page')).toBe('2');
    req.flush(ok({ data: [{ uuid: 'a', documentType: 'Payment', sourceDocumentNumber: 'CPAY-1' }], totalRecords: 51, page: 2, pageSize: 50 }));
    expect(page.totalRecords).toBe(51);
    expect(page.data[0].targets).toEqual([]);

    service.getLog().subscribe(p => page = p);
    http.expectOne(r => r.url === `${BASE}/log`).flush(ok([{ uuid: 'b', documentType: 'SalesInvoice', sourceDocumentNumber: 'SINV-1', targets: [] }]));
    expect(page.totalRecords).toBe(1);
  });

  it('entry, retry and skip by uuid', () => {
    service.getEntry('u1').subscribe();
    http.expectOne(`${BASE}/log/u1`).flush(ok({ uuid: 'u1', targets: [] }));
    service.retry('u1').subscribe();
    expect(http.expectOne(`${BASE}/log/u1/retry`).request.method).toBe('POST');
    service.skip('u1').subscribe();
    expect(http.expectOne(`${BASE}/log/u1/skip`).request.method).toBe('POST');
  });

  it('summary: the flat per-target counts of the contract (Details), totals ignored', () => {
    let s: any;
    service.getSummary().subscribe(x => s = x);
    http.expectOne(`${BASE}/summary`).flush(ok({
      targets: [
        { targetCode: 'internal_gl', pending: 2, posted: 10, queued: 0, held: 0, failed: 0, deadLetter: 1, skipped: 3, total: 16 },
        { targetCode: 'quickbooks', pending: 0, posted: 5, queued: 1, held: 3, failed: 2, deadLetter: 0, skipped: 0, total: 11 }
      ],
      postings: 16, needsAttention: 3, lastPostingAt: '2026-10-16T08:59:00Z'
    }));
    expect(summaryTiles(s)).toEqual({ Posted: 15, Queued: 3, Held: 3, Failed: 2, DeadLetter: 1 });
    expect(s.needsAttention).toBe(3);
    expect(s.targets[0].counts.Skipped).toBe(3);
  });

  it('summary: a counts map or a bare list is read too', () => {
    let s: any;
    service.getSummary().subscribe(x => s = x);
    http.expectOne(`${BASE}/summary`).flush(ok([{ targetCode: 'quickbooks', counts: { Posted: 1, DEAD_LETTER: 2 } }]));
    expect(summaryTiles(s)).toEqual({ Posted: 1, Queued: 0, Held: 0, Failed: 0, DeadLetter: 2 });
  });

  it('retry / skip send only the fields given', () => {
    service.retry('u1', { targetCode: 'internal_gl' }).subscribe();
    expect(http.expectOne(`${BASE}/log/u1/retry`).request.body).toEqual({ targetCode: 'internal_gl' });
    service.skip('u1', { targetCode: 'quickbooks', reason: 'Entered by hand', }).subscribe();
    expect(http.expectOne(`${BASE}/log/u1/skip`).request.body).toEqual({ targetCode: 'quickbooks', reason: 'Entered by hand' });
    service.skip('u2', { reason: '' }).subscribe();
    expect(http.expectOne(`${BASE}/log/u2/skip`).request.body).toEqual({});
  });

  it('a { success: false } answer is an error', () => {
    let error: any;
    service.getConfig().subscribe({ error: e => error = e });
    http.expectOne(`${BASE}/config`).flush({ success: false, message: 'GL_NOT_READY', result: null });
    expect(error.error.message).toBe('GL_NOT_READY');
  });

  it('by-source (D-33): the postings of one document, [] when it never reached the gateway', () => {
    let list: any;
    service.getBySource('inv 1').subscribe(x => list = x);
    http.expectOne(`${BASE}/by-source/inv%201`).flush(ok([{ uuid: 'p1', targets: [{ targetCode: 'quickbooks', status: 'Failed', canAct: true }] }]));
    expect(list[0].targets[0].canAct).toBeTrue();
    service.getBySource('inv-2').subscribe(x => list = x);
    http.expectOne(`${BASE}/by-source/inv-2`).flush(ok([]));
    expect(list).toEqual([]);
    service.getBySource('inv-3').subscribe(x => list = x);
    http.expectOne(`${BASE}/by-source/inv-3`).flush(ok(null));
    expect(list).toEqual([]);
  });

  it('detail (D-36): lines and journals default to empty lists', () => {
    let d: any;
    service.getEntry('u1').subscribe(x => d = x);
    http.expectOne(`${BASE}/log/u1`).flush(ok({ uuid: 'u1', targets: [] }));
    expect(d.lines).toEqual([]);
    expect(d.journals).toEqual([]);
  });

  it('journal helpers: reversed-by from the others, 2 dp totals with the balance check, entry type labels', () => {
    const views = journalViews([
      { uuid: 'a', entryNumber: 'JE-1', entryType: 'PAYMENT', entryDate: '2026-10-15', status: 'Posted', totalDebit: 5, totalCredit: 5, isReversed: true },
      { uuid: 'b', entryNumber: 'JE-2', entryType: 'PAYMENT', entryDate: '2026-10-16', status: 'Posted', totalDebit: 5, totalCredit: 5, reversesEntryNumber: 'JE-1' }
    ]);
    expect(views[0].reversedBy).toBe('JE-2');
    expect(views[1].reversedBy).toBeNull();
    expect(views[1].lines).toEqual([]);
    expect(journalViews(null)).toEqual([]);

    expect(lineTotals([{ debit: 1234.5, credit: 0 }, { debit: 0, credit: 1046.19 }, { debit: 0, credit: 188.31 }]))
      .toEqual({ debit: 1234.5, credit: 1234.5, balanced: true });
    expect(lineTotals([{ debit: 10, credit: 0 }, { debit: 0, credit: 9.99 }]).balanced).toBeFalse();
    expect(entryTypeLabel('SALES_INVOICE')).toBe('Sales invoice');
    expect(entryTypeLabel(null)).toBe('');
  });

  it('helpers: tones, labels, actionable, source routes', () => {
    expect(targetTone('Posted')).toBe('ok');
    expect(targetTone('pending')).toBe('in');
    expect(targetTone('Held')).toBe('wn');
    expect(targetTone('DeadLetter')).toBe('er');
    expect(targetTone('Skipped')).toBe('');
    expect(targetStatusLabel('DeadLetter')).toBe('Dead letter');
    expect(isActionable('Failed')).toBeTrue();
    expect(isActionable('Queued')).toBeFalse();
    expect(docTypeLabel('BillPayment')).toBe('Bill payment');
    expect(docTypeLabel('Unknown')).toBe('Unknown');
    expect(sourceDocRoute({ documentType: 'Payment', sourceDocumentUuid: 'u' })).toEqual(['/portal/pages/finance/customer-payments', 'u']);
    expect(sourceDocRoute({ documentType: 'PurchaseBill', sourceDocumentUuid: 'u' })).toEqual(['/portal/pages/finance/invoices', 'u']);
    expect(sourceDocRoute({ documentType: 'VendorCredit', sourceDocumentUuid: 'u' })).toBeNull();
    expect(sourceDocRoute({ documentType: 'Payment', sourceDocumentUuid: null })).toBeNull();
  });
});
