import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting, TestRequest } from '@angular/common/http/testing';
import { Observable } from 'rxjs';

import {
  QboApiError, QuickBooksIntegrationService, UpdateIntegrationSettingsRequest, qboErrorMessage
} from './quickbooks-integration.service';
import { environment } from '../../environments/environment';

const BASE = `${environment.apiUrl}/integrations/quickbooks`;

function ok<T>(result: T) {
  return { success: true, message: 'Request successful', result };
}

describe('QuickBooksIntegrationService', () => {
  let service: QuickBooksIntegrationService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(QuickBooksIntegrationService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  /**
   * Subscribes, answers the one request with `result`, and hands back the request and what the
   * caller received — so each case checks the address, the verb, the body and the unwrapping.
   */
  function call<T>(obs: Observable<T>, url: string, result: unknown): { req: TestRequest; got: T | undefined } {
    let got: T | undefined;
    obs.subscribe(v => (got = v));
    const req = http.expectOne(r => r.url === url);
    req.flush(ok(result));
    return { req, got };
  }

  // ── Connection ─────────────────────────────────────────────────────────────────────────────

  it('GET /connection and unwraps the status', () => {
    const status = { status: 'Connected', isConnected: true, appConfigured: true };
    const r = call(service.getConnection(), `${BASE}/connection`, status);
    expect(r.req.request.method).toBe('GET');
    expect(r.got).toEqual(status as any);
  });

  it('POST /connect with an empty body and returns the consent URL', () => {
    const r = call(service.connect(), `${BASE}/connect`, { consentUrl: 'https://appcenter.intuit.com/x' });
    expect(r.req.request.method).toBe('POST');
    expect(r.req.request.body).toEqual({});
    expect(r.got!.consentUrl).toBe('https://appcenter.intuit.com/x');
  });

  it('POST /test', () => {
    const r = call(service.testConnection(), `${BASE}/test`, { ok: true, companyName: 'Acme' });
    expect(r.req.request.method).toBe('POST');
    expect(r.got!.ok).toBeTrue();
  });

  it('DELETE /connection and returns the new status', () => {
    const r = call(service.disconnect(), `${BASE}/connection`, { status: 'NotConnected', isConnected: false });
    expect(r.req.request.method).toBe('DELETE');
    expect(r.got!.status).toBe('NotConnected');
  });

  // ── Reference & preflight ──────────────────────────────────────────────────────────────────

  it('GET /reference', () => {
    const r = call(service.getReference(), `${BASE}/reference`, { accounts: [], taxCodes: [], terms: [], currencies: [] });
    expect(r.req.request.method).toBe('GET');
    expect(r.got!.accounts).toEqual([]);
  });

  it('POST /reference/refresh', () => {
    const r = call(service.refreshReference(), `${BASE}/reference/refresh`, { accounts: [{ id: '1' }], taxCodes: [], terms: [], currencies: [] });
    expect(r.req.request.method).toBe('POST');
    expect(r.got!.accounts.length).toBe(1);
  });

  it('GET /preflight', () => {
    const r = call(service.getPreflight(), `${BASE}/preflight`, { passed: true, canGoLive: false, checks: [] });
    expect(r.req.request.method).toBe('GET');
    expect(r.got!.passed).toBeTrue();
  });

  // ── Settings & mappings ────────────────────────────────────────────────────────────────────

  it('GET /settings', () => {
    const r = call(service.getSettings(), `${BASE}/settings`, { mode: 'DryRun' });
    expect(r.req.request.method).toBe('GET');
    expect(r.got!.mode).toBe('DryRun');
  });

  it('PUT /settings with the whole request as the body', () => {
    const body: UpdateIntegrationSettingsRequest = {
      autoPushCustomers: true, autoPushVendors: false, autoPushItems: true, autoPushSalesInvoices: true, autoPushBills: false,
      itemTypeDefault: 'Service', partnerScope: 'AllActive', defaultIncomeAccountId: '79', defaultExpenseAccountId: '80',
      freightExpenseAccountId: null, discountAccountId: '86', defaultPurchaseTaxCodeId: null, documentStartDate: '2026-10-01'
    };
    const r = call(service.updateSettings(body), `${BASE}/settings`, { mode: 'DryRun' });
    expect(r.req.request.method).toBe('PUT');
    expect(r.req.request.body).toEqual(body);
  });

  it('GET and PUT /tax-mappings', () => {
    const g = call(service.getTaxMappings(), `${BASE}/tax-mappings`, [{ taxPercent: 17, timesSeen: 3 }]);
    expect(g.req.request.method).toBe('GET');
    expect(g.got!.length).toBe(1);

    const body = { mappings: [{ taxPercent: 17, qboTaxCodeId: '5' }, { taxPercent: 0, qboTaxCodeId: null }] };
    const p = call(service.saveTaxMappings(body), `${BASE}/tax-mappings`, []);
    expect(p.req.request.method).toBe('PUT');
    expect(p.req.request.body).toEqual(body);
  });

  it('GET and PUT /tax-mappings carry SMS tax code rows (S-11) next to percent rows', () => {
    const rows = [
      { sourceTaxCode: 'GST17', taxPercent: 17, timesSeen: 4, suggestedQboTaxCodeId: '5', suggestedQboTaxCodeName: 'GST 17%' },
      { taxPercent: 0, qboTaxCodeId: '6', timesSeen: 1 }
    ];
    const g = call(service.getTaxMappings(), `${BASE}/tax-mappings`, rows);
    expect(g.got).toEqual(rows);

    const body = { mappings: [{ sourceTaxCode: 'GST17', taxPercent: 17, qboTaxCodeId: '5' }, { taxPercent: 0, qboTaxCodeId: '6' }] };
    const p = call(service.saveTaxMappings(body), `${BASE}/tax-mappings`, []);
    expect(p.req.request.body).toEqual(body);
  });

  it('GET and PUT /term-mappings', () => {
    const g = call(service.getTermMappings(), `${BASE}/term-mappings`, []);
    expect(g.req.request.method).toBe('GET');

    const body = { mappings: [{ paymentTermExternalId: 'aa', paymentTermName: 'Net 30', qboTermId: '3' }] };
    const p = call(service.saveTermMappings(body), `${BASE}/term-mappings`, []);
    expect(p.req.request.method).toBe('PUT');
    expect(p.req.request.body).toEqual(body);
  });

  it('POST /mode with { mode }', () => {
    const r = call(service.setMode('Live'), `${BASE}/mode`, { mode: 'Live' });
    expect(r.req.request.method).toBe('POST');
    expect(r.req.request.body).toEqual({ mode: 'Live' });
    expect(r.got!.mode).toBe('Live');
  });

  it('POST /matching/complete with { confirmed: true }', () => {
    const r = call(service.confirmMatchingComplete(), `${BASE}/matching/complete`, { matchingConfirmedAt: '2026-09-30T10:00:00Z' });
    expect(r.req.request.method).toBe('POST');
    expect(r.req.request.body).toEqual({ confirmed: true });
  });

  // ── API clients ────────────────────────────────────────────────────────────────────────────

  it('GET /api-clients', () => {
    const r = call(service.getApiClients(), `${BASE}/api-clients`, [{ id: 'c1', name: 'POS', scopes: [], keys: [] }]);
    expect(r.req.request.method).toBe('GET');
    expect(r.got!.length).toBe(1);
  });

  it('POST /api-clients with name and scopes', () => {
    const body = { name: 'POS', scopes: ['customers:write', 'status:read'] };
    const r = call(service.createApiClient(body), `${BASE}/api-clients`, { id: 'c1', name: 'POS' });
    expect(r.req.request.method).toBe('POST');
    expect(r.req.request.body).toEqual(body);
  });

  it('POST /api-clients/{id}/keys and returns the key once', () => {
    const r = call(service.issueApiKey('c1', { expiresAt: null }), `${BASE}/api-clients/c1/keys`,
      { keyId: 'k1', apiKey: 'sqb_secret', keyPrefix: 'sqb_abcdefgh', tenantId: 't1' });
    expect(r.req.request.method).toBe('POST');
    expect(r.req.request.body).toEqual({ expiresAt: null });
    expect(r.got!.apiKey).toBe('sqb_secret');
  });

  it('DELETE /api-clients/{id}/keys/{keyId}', () => {
    const r = call(service.revokeApiKey('c1', 'k1'), `${BASE}/api-clients/c1/keys/k1`, { id: 'c1' });
    expect(r.req.request.method).toBe('DELETE');
  });

  it('DELETE /api-clients/{id}', () => {
    const r = call(service.deactivateApiClient('c1'), `${BASE}/api-clients/c1`, { id: 'c1', isActive: false });
    expect(r.req.request.method).toBe('DELETE');
    expect(r.got!.isActive).toBeFalse();
  });

  // ── Matching ───────────────────────────────────────────────────────────────────────────────

  it('POST /matching/{kind}/scan', () => {
    const r = call(service.scanMatches('Vendor'), `${BASE}/matching/Vendor/scan`, { kind: 'Vendor', exact: 2 });
    expect(r.req.request.method).toBe('POST');
    expect(r.got!.exact).toBe(2);
  });

  it('GET /matching/{kind}, with ?decision= only when one is asked for', () => {
    const all = call(service.getMatchCandidates('Customer'), `${BASE}/matching/Customer`, []);
    expect(all.req.request.method).toBe('GET');
    expect(all.req.request.params.has('decision')).toBeFalse();

    const pending = call(service.getMatchCandidates('Item', 'Pending'), `${BASE}/matching/Item`, []);
    expect(pending.req.request.params.get('decision')).toBe('Pending');
  });

  it('POST /matching/{kind}/confirm with the decisions', () => {
    const body = { decisions: [{ candidateId: 'm1', decision: 'Link' as const, remoteId: '58' }] };
    const r = call(service.confirmMatches('Customer', body), `${BASE}/matching/Customer/confirm`, []);
    expect(r.req.request.method).toBe('POST');
    expect(r.req.request.body).toEqual(body);
  });

  // ── Sync dashboard ─────────────────────────────────────────────────────────────────────────

  it('GET /sync/summary', () => {
    const r = call(service.getSyncSummary(), `${BASE}/sync/summary`, { kinds: [], queueDepth: 4 });
    expect(r.req.request.method).toBe('GET');
    expect(r.got!.queueDepth).toBe(4);
  });

  it('GET /sync/items sends only the filters that are set, with page and pageSize', () => {
    const page = { data: [{ id: 's1' }], totalRecords: 1, page: 2, pageSize: 50, totalPages: 1, hasNext: false, hasPrevious: true };
    const r = call(service.getSyncItems({ kind: 'Bill', state: 'Blocked', search: '  acme ', page: 2, pageSize: 50 }),
      `${BASE}/sync/items`, page);
    const p = r.req.request.params;
    expect(r.req.request.method).toBe('GET');
    expect(p.get('kind')).toBe('Bill');
    expect(p.get('state')).toBe('Blocked');
    expect(p.get('search')).toBe('acme');
    expect(p.get('page')).toBe('2');
    expect(p.get('pageSize')).toBe('50');
    expect(r.got!.data.length).toBe(1);

    const d = call(service.getSyncItems(), `${BASE}/sync/items`, page);
    expect(d.req.request.params.keys().sort()).toEqual(['page', 'pageSize']);
    expect(d.req.request.params.get('page')).toBe('1');
    expect(d.req.request.params.get('pageSize')).toBe('25');
  });

  it('GET /sync/items/{id}/log', () => {
    const r = call(service.getSyncLog('s1'), `${BASE}/sync/items/s1/log`, [{ id: 'l1' }]);
    expect(r.req.request.method).toBe('GET');
    expect(r.got!.length).toBe(1);
  });

  it('POST /sync/items/{id}/retry', () => {
    const r = call(service.retrySyncItem('s1'), `${BASE}/sync/items/s1/retry`, { id: 's1', state: 'Pending' });
    expect(r.req.request.method).toBe('POST');
    expect(r.got!.state).toBe('Pending');
  });

  it('POST /sync/items/{id}/resolve with the action', () => {
    const body = { action: 'LinkRemote' as const, remoteId: '145' };
    const r = call(service.resolveSyncItem('s1', body), `${BASE}/sync/items/s1/resolve`, { id: 's1' });
    expect(r.req.request.method).toBe('POST');
    expect(r.req.request.body).toEqual(body);
  });

  it('POST /sync/push with kind and ids', () => {
    const r = call(service.pushNow('Customer', ['p1', 'p2']), `${BASE}/sync/push`, { requested: 2 });
    expect(r.req.request.method).toBe('POST');
    expect(r.req.request.body).toEqual({ kind: 'Customer', externalIds: ['p1', 'p2'] });
    expect(r.got!.requested).toBe(2);
  });

  it('POST /sync/backfill/{kind}', () => {
    const r = call(service.backfill('SalesInvoice'), `${BASE}/sync/backfill/SalesInvoice`, { kind: 'SalesInvoice', sent: 12 });
    expect(r.req.request.method).toBe('POST');
    expect(r.got!.sent).toBe(12);
  });

  it('POST /status/lookup with kind and ids, and unwraps the items', () => {
    const items = [{ kind: 'Item', externalId: 'v1', state: 'Synced' }];
    const r = call(service.lookupStatus('Item', ['v1', 'v2']), `${BASE}/status/lookup`, { items });
    expect(r.req.request.method).toBe('POST');
    expect(r.req.request.body).toEqual({ kind: 'Item', externalIds: ['v1', 'v2'] });
    expect(r.got!.items).toEqual(items as any);
  });

  it('escapes ids placed in the path', () => {
    service.getSyncLog('a/b').subscribe();
    http.expectOne(`${BASE}/sync/items/a%2Fb/log`).flush(ok([]));
  });

  // ── Failures ───────────────────────────────────────────────────────────────────────────────

  it('turns a { success: false } answer into a QboApiError carrying the server message', () => {
    let error: unknown;
    service.getSettings().subscribe({ error: e => (error = e) });
    http.expectOne(`${BASE}/settings`).flush({ success: false, message: 'Not connected.', result: null });

    expect(error instanceof QboApiError).toBeTrue();
    expect(qboErrorMessage(error, 'fallback')).toBe('Not connected.');
  });

  it('passes an HTTP error through, and qboErrorMessage reads its message', () => {
    let error: unknown;
    service.setMode('Live').subscribe({ error: e => (error = e) });
    http.expectOne(`${BASE}/mode`).flush({ success: false, message: 'Preflight has not passed.' }, { status: 400, statusText: 'Bad Request' });

    expect(error instanceof HttpErrorResponse).toBeTrue();
    expect(qboErrorMessage(error, 'fallback')).toBe('Preflight has not passed.');
  });
});

describe('qboErrorMessage', () => {
  it('reads the first of a gateway-style errors list', () => {
    const err = new HttpErrorResponse({ status: 400, error: { code: 'invalid', errors: [{ field: 'name', message: 'Name is too long.' }] } });
    expect(qboErrorMessage(err, 'x')).toBe('Name is too long.');
  });

  it('has words for the statuses that carry no message', () => {
    expect(qboErrorMessage(new HttpErrorResponse({ status: 403 }), 'x')).toContain('permission');
    expect(qboErrorMessage(new HttpErrorResponse({ status: 0 }), 'x')).toContain('could not be reached');
    expect(qboErrorMessage(new HttpErrorResponse({ status: 500 }), 'Something failed.')).toBe('Something failed.');
    expect(qboErrorMessage(undefined, 'Something failed.')).toBe('Something failed.');
  });
});
