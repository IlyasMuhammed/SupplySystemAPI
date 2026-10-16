import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { QuickBooksIntegrationService, listOf } from './quickbooks-integration.service';
import { environment } from '../../environments/environment';

const BASE = `${environment.apiUrl}/integrations/quickbooks`;
const ok = <T>(result: T) => ({ success: true, message: '', result });

/** A44 (API-CONTRACT §4) — the CTRL endpoints the QuickBooks page calls. */
describe('QuickBooksIntegrationService — A44', () => {
  let service: QuickBooksIntegrationService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(QuickBooksIntegrationService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('listOf reads an array, or the array inside an object', () => {
    expect(listOf<number>([1], 'rows')).toEqual([1]);
    expect(listOf<number>({ rows: [2] }, 'rows')).toEqual([2]);
    expect(listOf<number>({ items: [3] }, 'rows')).toEqual([3]);
    expect(listOf<number>(null, 'rows')).toEqual([]);
  });

  it('sync control: GET, PUT { isEnabled, holdWhenDisabled, confirmRelease } → { row, released }, release', () => {
    let rows: unknown;
    service.getSyncControl().subscribe(r => rows = r);
    http.expectOne(`${BASE}/sync-control`).flush(ok({ rows: [{ documentType: 'POSSale', isEnabled: false, holdWhenDisabled: true }] }));
    expect((rows as any[]).length).toBe(1);

    let put: any;
    service.updateSyncControl('POSSale', { isEnabled: true, holdWhenDisabled: true, confirmRelease: true }).subscribe(r => put = r);
    const req = http.expectOne(`${BASE}/sync-control/POSSale`);
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ isEnabled: true, holdWhenDisabled: true, confirmRelease: true });
    req.flush(ok({ row: { documentType: 'POSSale', isEnabled: true, holdWhenDisabled: true }, released: 4 }));
    expect(put.released).toBe(4);

    // A bare row is accepted as { row, released: 0 }.
    service.updateSyncControl('Customer', { isEnabled: false, holdWhenDisabled: false, confirmRelease: false }).subscribe(r => put = r);
    http.expectOne(`${BASE}/sync-control/Customer`).flush(ok({ documentType: 'Customer', isEnabled: false, holdWhenDisabled: false }));
    expect(put).toEqual({ row: jasmine.objectContaining({ documentType: 'Customer' }), released: 0 });

    service.releaseHeld('POSSale').subscribe();
    expect(http.expectOne(`${BASE}/sync-control/POSSale/release`).request.method).toBe('POST');
  });

  it('provider defaults (an object with tracking; saved as { values }) and overrides', () => {
    let model: any;
    service.getProviderDefaults().subscribe(m => model = m);
    http.expectOne(`${BASE}/provider-defaults`).flush(ok({ classTrackingEnabled: true, departmentTrackingEnabled: null, defaults: [], documentTypes: ['Payment'] }));
    expect(model.classTrackingEnabled).toBeTrue();
    expect(model.documentTypes).toEqual(['Payment']);

    service.saveProviderDefaults({ values: [{ settingKey: 'ClassRef', settingValue: '5' }] }).subscribe(m => model = m);
    const put = http.expectOne(`${BASE}/provider-defaults`);
    expect(put.request.method).toBe('PUT');
    expect(put.request.body).toEqual({ values: [{ settingKey: 'ClassRef', settingValue: '5' }] });
    put.flush(ok({ defaults: null }));
    expect(model.defaults).toEqual([]);
    service.getDocumentOverrides().subscribe();
    http.expectOne(`${BASE}/document-overrides`).flush(ok({ overrides: [] }));
    service.saveDocumentOverrides({ overrides: [] }).subscribe();
    expect(http.expectOne(`${BASE}/document-overrides`).request.method).toBe('PUT');
  });

  it('accounts: GET (normalised), auto-map, PUT batch', () => {
    let model: any;
    service.getAccountMappings().subscribe(m => model = m);
    http.expectOne(`${BASE}/accounts`).flush(ok({ glAvailable: true, accounts: [{ accountUuid: 'a1', code: '4100', name: 'Sales', qboAccountId: null }] }));
    expect(model.glAvailable).toBeTrue();
    expect(model.unmapped).toBe(1);
    expect(model.qboAccounts).toEqual([]);

    service.autoMapAccounts().subscribe();
    const auto = http.expectOne(`${BASE}/accounts/auto-map`);
    expect(auto.request.method).toBe('POST');
    expect(auto.request.body).toEqual({});
    service.saveAccountMappings({ mappings: [{ accountUuid: 'a1', qboAccountId: '79' }] }).subscribe();
    const put = http.expectOne(`${BASE}/accounts`);
    expect(put.request.method).toBe('PUT');
    expect(put.request.body).toEqual({ mappings: [{ accountUuid: 'a1', qboAccountId: '79' }] });
  });

  it('tax import, remote records, link', () => {
    service.importTaxMappings().subscribe();
    expect(http.expectOne(`${BASE}/tax-mappings/import`).request.method).toBe('POST');
    service.importRemote('Customer').subscribe();
    expect(http.expectOne(`${BASE}/matching/Customer/import`).request.method).toBe('POST');

    let remote: any;
    service.getRemote('Vendor').subscribe(r => remote = r);
    http.expectOne(`${BASE}/matching/Vendor/remote?linked=false&page=1&pageSize=200`).flush(ok({ data: [{ remoteId: '9', name: 'V' }], totalRecords: 1 }));
    expect(remote.length).toBe(1);
    service.getRemote('Item', null).subscribe();
    http.expectOne(`${BASE}/matching/Item/remote?page=1&pageSize=200`).flush(ok([]));

    service.linkRemote('Customer', '91', { externalId: 'bp-1' }).subscribe();
    const link = http.expectOne(`${BASE}/matching/Customer/remote/91/link`);
    expect(link.request.body).toEqual({ externalId: 'bp-1' });
  });

  it('dead letter: deadLetter=true instead of a state; retry-failed, skip, unlink', () => {
    service.getSyncItems({ deadLetter: true, state: 'Failed' }).subscribe();
    const items = http.expectOne(r => r.url === `${BASE}/sync/items`);
    expect(items.request.params.get('deadLetter')).toBe('true');
    expect(items.request.params.has('state')).toBeFalse();

    service.retryFailed('Payment').subscribe();
    expect(http.expectOne(`${BASE}/sync/items/retry-failed`).request.body).toEqual({ kind: 'Payment' });
    service.retryFailed().subscribe();
    expect(http.expectOne(`${BASE}/sync/items/retry-failed`).request.body).toEqual({});
    service.skipSyncItem('s 1').subscribe();
    http.expectOne(`${BASE}/sync/items/s%201/skip`);
    service.unlinkSyncItem('s1').subscribe();
    http.expectOne(`${BASE}/sync/items/s1/unlink`);
  });

  it('call log (D-34, D-69a): failedOnly and unresolvedOnly always sent (both default true on the server)', () => {
    service.getCallLog({ unresolvedOnly: true, kind: 'Payment' }).subscribe();
    const a = http.expectOne(r => r.url === `${BASE}/sync/log`);
    expect(a.request.params.get('failedOnly')).toBe('true');
    expect(a.request.params.get('unresolvedOnly')).toBe('true');
    expect(a.request.params.get('kind')).toBe('Payment');
    a.flush(ok({ data: [], totalRecords: 0 }));
    service.getCallLog({ failedOnly: false, unresolvedOnly: false }).subscribe();
    const b = http.expectOne(r => r.url === `${BASE}/sync/log`);
    expect(b.request.params.get('failedOnly')).toBe('false');
    expect(b.request.params.get('unresolvedOnly')).toBe('false');
    b.flush(ok({ data: [], totalRecords: 0 }));
    service.getCallLog().subscribe();
    const c = http.expectOne(r => r.url === `${BASE}/sync/log`);
    expect(c.request.params.get('unresolvedOnly')).toBe('true');
    c.flush(ok({ data: [], totalRecords: 0 }));
    service.getCallLogEntry('l 1').subscribe();
    http.expectOne(`${BASE}/sync/log/l%201`).flush(ok({ id: 'l 1' }));
  });

  it('coverage: fills the lists the answer leaves out', () => {
    let c: any;
    service.getCoverage().subscribe(x => c = x);
    http.expectOne(`${BASE}/coverage`).flush(ok({ ready: true }));
    expect(c.ready).toBeTrue();
    expect(c.blockingIssues).toEqual([]);
    expect(c.warnings).toEqual([]);
    expect(c.entities).toEqual([]);
  });

  it('a { success: false } answer comes through the error channel', () => {
    let error: any;
    service.getSyncControl().subscribe({ error: e => error = e });
    http.expectOne(`${BASE}/sync-control`).flush({ success: false, message: 'Not connected.', result: null });
    expect(error.message).toBe('Not connected.');
  });
});
