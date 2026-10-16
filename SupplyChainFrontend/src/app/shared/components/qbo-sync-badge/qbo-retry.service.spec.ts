import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { QboRetryOutcome, QboRetryService, retryActionLabel, retryToast, syncKindForPosting } from './qbo-retry.service';
import { AuthService } from '../../../pages/service/auth.service';
import { environment } from '../../../../environments/environment';

const QBO = `${environment.apiUrl}/integrations/quickbooks`;
const POSTING = `${environment.apiUrl}/finance/posting`;
const ok = <T>(result: T) => ({ success: true, message: '', result });

/** A44 D-33 — "Send to QuickBooks" / "Retry" from an SCM page. */
describe('QboRetryService', () => {
  let service: QboRetryService;
  let http: HttpTestingController;
  let permissions: string[];

  beforeEach(() => {
    permissions = ['INTEGRATION_VIEW', 'INTEGRATION_MANAGE', 'INTEGRATION_SYNC', 'GL_SETUP_MANAGE'];
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(),
        { provide: AuthService, useValue: { hasPermission: (c: string) => permissions.includes(c) } }]
    });
    service = TestBed.inject(QboRetryService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  function run(kind: any, id: string): QboRetryOutcome {
    let out!: QboRetryOutcome;
    service.run(kind, id).subscribe(o => out = o);
    return out;
  }

  it('retries every failed target the server says the user may act on, then pushes the record', () => {
    let out: QboRetryOutcome | undefined;
    service.run('SalesInvoice', 'inv-1').subscribe(o => out = o);
    http.expectOne(`${POSTING}/by-source/inv-1`).flush(ok([
      { uuid: 'p1', documentType: 'SalesInvoice', sourceDocumentNumber: 'SI-1', targets: [
        { targetCode: 'internal_gl', status: 'Posted', canAct: false },
        { targetCode: 'quickbooks', status: 'Failed', canAct: true }
      ] },
      { uuid: 'p2', documentType: 'SalesInvoice', sourceDocumentNumber: 'SI-1', kind: 'Void', targets: [
        { targetCode: 'internal_gl', status: 'DeadLetter', canAct: false }
      ] }
    ]));
    const retry = http.expectOne(`${POSTING}/log/p1/retry`);
    expect(retry.request.body).toEqual({ targetCode: 'quickbooks' });
    retry.flush(ok({ uuid: 'p1', targets: [] }));
    http.expectNone(`${POSTING}/log/p2/retry`);

    const push = http.expectOne(`${QBO}/sync/push`);
    expect(push.request.body).toEqual({ kind: 'SalesInvoice', externalIds: ['inv-1'] });
    push.flush(ok({ requested: 1 }));
    expect(out).toEqual({ result: 'queued', postingsRetried: 1, pushed: true });
  });

  it('without canAct from the server, falls back to the per-target permission rule', () => {
    permissions = ['INTEGRATION_MANAGE', 'INTEGRATION_SYNC'];
    service.run('Payment', 'pay-1').subscribe();
    http.expectOne(`${POSTING}/by-source/pay-1`).flush(ok([
      { uuid: 'p1', targets: [{ targetCode: 'internal_gl', status: 'Failed' }, { targetCode: 'quickbooks', status: 'DeadLetter' }] }
    ]));
    expect(http.match(`${POSTING}/log/p1/retry`).map(r => { r.flush(ok({ uuid: 'p1', targets: [] })); return r.request.body; }))
      .toEqual([{ targetCode: 'quickbooks' }]);
    http.expectOne(`${QBO}/sync/push`).flush(ok({ requested: 1 }));
  });

  it('a failed posting retry or by-source call does not stop the push', () => {
    let out: QboRetryOutcome | undefined;
    service.run('Bill', 'b-1').subscribe(o => out = o);
    http.expectOne(`${POSTING}/by-source/b-1`).flush({ message: 'boom' }, { status: 500, statusText: 'Server Error' });
    http.expectOne(`${QBO}/sync/push`).flush(ok({ requested: 1 }));
    expect(out!.result).toBe('queued');
    expect(out!.postingsRetried).toBe(0);
  });

  it('master data (customer, vendor, item) has no postings: only the push', () => {
    service.run('Customer', 'bp-1').subscribe();
    http.expectNone(r => r.url.startsWith(`${POSTING}/by-source`));
    http.expectOne(`${QBO}/sync/push`).flush(ok({ requested: 1 }));
  });

  it('skipped in matching, no permission and other errors come back as outcomes', () => {
    let out: QboRetryOutcome | undefined;
    service.run('Vendor', 'v-1').subscribe(o => out = o);
    http.expectOne(`${QBO}/sync/push`).flush(ok({ requested: 0, skippedInMatching: ['v-1'] }));
    expect(out!.result).toBe('skippedInMatching');

    service.run('Vendor', 'v-1').subscribe(o => out = o);
    http.expectOne(`${QBO}/sync/push`).flush({ message: 'Forbidden' }, { status: 403, statusText: 'Forbidden' });
    expect(out!.result).toBe('forbidden');

    service.run('Vendor', 'v-1').subscribe(o => out = o);
    http.expectOne(`${QBO}/sync/push`).flush({ message: 'QuickBooks is not connected.' }, { status: 409, statusText: 'Conflict' });
    expect(out).toEqual({ result: 'error', postingsRetried: 0, pushed: true, message: 'QuickBooks is not connected.' });
  });

  it('no GL / integration manage code: the posting log is not asked', () => {
    permissions = ['INTEGRATION_VIEW', 'INTEGRATION_SYNC'];
    service.run('SalesInvoice', 'inv-1').subscribe();
    http.expectNone(r => r.url.startsWith(`${POSTING}/by-source`));
    http.expectOne(`${QBO}/sync/push`).flush(ok({ requested: 1 }));
    expect(run).toBeDefined();
  });

  // ── REV-19: each step only when allowed ────────────────────────────────────

  it('INTEGRATION_SYNC only: no posting log, just the push', () => {
    permissions = ['INTEGRATION_VIEW', 'INTEGRATION_SYNC'];
    expect(service.canPush()).toBeTrue();
    expect(service.mayActOnPostings('SalesInvoice')).toBeFalse();
    let out: QboRetryOutcome | undefined;
    service.run('SalesInvoice', 'inv-1').subscribe(o => out = o);
    http.expectNone(r => r.url.startsWith(`${POSTING}/`));
    http.expectOne(`${QBO}/sync/push`).flush(ok({ requested: 1 }));
    expect(out).toEqual({ result: 'queued', postingsRetried: 0, pushed: true });
  });

  it('INTEGRATION_MANAGE only, with a canAct target: the posting retry, no push', () => {
    permissions = ['INTEGRATION_VIEW', 'INTEGRATION_MANAGE'];
    expect(service.canPush()).toBeFalse();
    let out: QboRetryOutcome | undefined;
    service.run('Payment', 'pay-1').subscribe(o => out = o);
    http.expectOne(`${POSTING}/by-source/pay-1`).flush(ok([{ uuid: 'p1', targets: [{ targetCode: 'quickbooks', status: 'Failed', canAct: true }] }]));
    http.expectOne(`${POSTING}/log/p1/retry`).flush(ok({ uuid: 'p1', targets: [] }));
    http.expectNone(`${QBO}/sync/push`);
    expect(out).toEqual({ result: 'queued', postingsRetried: 1, pushed: false });
    expect(retryToast(out!, 'CPAY-1')).toEqual(jasmine.objectContaining({ summary: 'Retried', detail: 'CPAY-1: 1 failed posting was queued again.' }));
  });

  it('INTEGRATION_MANAGE only, nothing the user may act on: forbidden, no push', () => {
    permissions = ['INTEGRATION_VIEW', 'INTEGRATION_MANAGE'];
    let out: QboRetryOutcome | undefined;
    service.run('Payment', 'pay-1').subscribe(o => out = o);
    http.expectOne(`${POSTING}/by-source/pay-1`).flush(ok([{ uuid: 'p1', targets: [{ targetCode: 'internal_gl', status: 'Failed', canAct: false }] }]));
    http.expectNone(`${QBO}/sync/push`);
    expect(out!.result).toBe('forbidden');
  });

  it('neither code: nothing is called', () => {
    permissions = ['INTEGRATION_VIEW'];
    let out: QboRetryOutcome | undefined;
    service.run('SalesInvoice', 'inv-1').subscribe(o => out = o);
    http.expectNone(() => true);
    expect(out).toEqual({ result: 'forbidden', postingsRetried: 0, pushed: false });
  });

  it('hasActableTarget: by-source canAct, false for master data, without a manage code, or on error', () => {
    permissions = ['INTEGRATION_VIEW', 'GL_SETUP_MANAGE'];
    let yes: boolean | undefined;
    service.hasActableTarget('SalesInvoice', 'inv-1').subscribe(v => yes = v);
    http.expectOne(`${POSTING}/by-source/inv-1`).flush(ok([{ uuid: 'p1', targets: [{ targetCode: 'internal_gl', status: 'DeadLetter', canAct: true }] }]));
    expect(yes).toBeTrue();
    service.hasActableTarget('SalesInvoice', 'inv-2').subscribe(v => yes = v);
    http.expectOne(`${POSTING}/by-source/inv-2`).flush({}, { status: 500, statusText: 'x' });
    expect(yes).toBeFalse();
    service.hasActableTarget('Customer', 'bp-1').subscribe(v => yes = v);
    expect(yes).toBeFalse();
    permissions = ['INTEGRATION_VIEW'];
    service.hasActableTarget('SalesInvoice', 'inv-3').subscribe(v => yes = v);
    http.expectNone(`${POSTING}/by-source/inv-3`);
    expect(yes).toBeFalse();
  });

  it('a document id in the push answer\'s notFound: "Nothing to send for this document." (info)', () => {
    let out: QboRetryOutcome | undefined;
    service.run('Customer', 'BP-1').subscribe(o => out = o);
    http.expectOne(`${QBO}/sync/push`).flush(ok({ requested: 0, skippedInMatching: [], notFound: ['bp-1'] }));
    expect(out!.result).toBe('notFound');
    expect(retryToast(out!)).toEqual({ severity: 'info', summary: 'Nothing to send', detail: 'Nothing to send for this document.' });
  });

  // ── REV-20: the kind a document was posted as ──────────────────────────────

  it('maps a posting\'s document type to the QuickBooks kind; POS types are not decisive', () => {
    expect(syncKindForPosting('SalesReceipt')).toBe('SalesReceipt');
    expect(syncKindForPosting('RefundReceipt')).toBe('RefundReceipt');
    expect(syncKindForPosting('CreditNote')).toBe('CreditMemo');
    expect(syncKindForPosting('SalesInvoice')).toBe('SalesInvoice');
    expect(syncKindForPosting('PurchaseBill')).toBe('Bill');
    expect(syncKindForPosting('POSSale')).toBeNull();
    expect(syncKindForPosting(null)).toBeNull();
  });

  it('postedKind reads the Post posting (not its Void), null without one or on error', () => {
    let k: unknown;
    service.postedKind('d-1').subscribe(v => k = v);
    http.expectOne(`${POSTING}/by-source/d-1`).flush(ok([
      { uuid: 'v', kind: 'Void', documentType: 'POSSale', targets: [] },
      { uuid: 'p', kind: 'Post', documentType: 'SalesReceipt', targets: [] }
    ]));
    expect(k).toBe('SalesReceipt');
    service.postedKind('d-2').subscribe(v => k = v);
    http.expectOne(`${POSTING}/by-source/d-2`).flush(ok([]));
    expect(k).toBeNull();
    service.postedKind('d-3').subscribe(v => k = v);
    http.expectOne(`${POSTING}/by-source/d-3`).flush({}, { status: 403, statusText: 'Forbidden' });
    expect(k).toBeNull();
  });

  it('labels and toasts', () => {
    expect(retryActionLabel('NotSynced')).toBe('Send to QuickBooks');
    expect(retryActionLabel('NotSynced', true)).toBeNull();
    expect(retryActionLabel('Failed')).toBe('Retry');
    expect(retryActionLabel('Blocked')).toBe('Retry');
    expect(retryActionLabel('NeedsResolution')).toBe('Retry');
    expect(retryActionLabel('Synced')).toBeNull();
    expect(retryActionLabel('Pending')).toBeNull();
    expect(retryToast({ result: 'queued', postingsRetried: 2 }, 'SI-1')).toEqual(jasmine.objectContaining({
      severity: 'success', detail: 'SI-1 is queued to be sent to QuickBooks. 2 failed postings were retried first.'
    }));
    expect(retryToast({ result: 'skippedInMatching', postingsRetried: 0 }).severity).toBe('warn');
    expect(retryToast({ result: 'forbidden', postingsRetried: 0 }).summary).toBe('No permission');
    expect(retryToast({ result: 'error', postingsRetried: 0, message: 'Nope.' }).detail).toBe('Nope.');
  });
});
