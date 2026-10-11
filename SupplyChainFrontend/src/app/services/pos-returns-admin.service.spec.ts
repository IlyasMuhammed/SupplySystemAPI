import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import {
  DEFAULT_RETURN_POLICY, PosReturnsAdminService, cleanPolicy, isValidReasonCode, managerNeedLabel, managerRequired,
  policyProblem, policySummary, policyUpsertFrom, reasonUpsertFrom
} from './pos-returns-admin.service';
import { environment } from '../../environments/environment';
import { policyFixture } from '../pages/pos/pos-fixtures.testing';

const BASE = `${environment.apiUrl}/pos`;
const ok = { success: true, message: '', result: null };

/** A39 — the back-office client of the returns / store credit API (API-CONTRACT §1, §4). */
describe('PosReturnsAdminService (A39)', () => {
  let service: PosReturnsAdminService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(PosReturnsAdminService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('reasons: list with includeInactive / appliesTo, create, update, status', () => {
    service.getReasons({ includeInactive: true }).subscribe();
    const list = http.expectOne(r => r.url === `${BASE}/return-reasons`);
    expect(list.request.params.get('includeInactive')).toBe('true');
    expect(list.request.params.has('appliesTo')).toBeFalse();
    list.flush(ok);
    service.getReasons({ appliesTo: 'RETURN' }).subscribe();
    expect(http.expectOne(r => r.url === `${BASE}/return-reasons`).request.params.get('appliesTo')).toBe('RETURN');

    const body = { code: 'X', name: 'X', requiresNote: false, requiresManager: false, appliesTo: 'BOTH' as const, sortOrder: 1, isActive: true };
    service.createReason(body).subscribe();
    expect(http.expectOne(`${BASE}/return-reasons`).request.method).toBe('POST');
    service.updateReason('r-1', body).subscribe();
    expect(http.expectOne(`${BASE}/return-reasons/r-1`).request.method).toBe('PUT');
    service.setReasonStatus('r-1', false).subscribe();
    const st = http.expectOne(`${BASE}/return-reasons/r-1/status`);
    expect(st.request.method).toBe('PATCH');
    expect(st.request.body).toEqual({ isActive: false });
  });

  it('policies: store GET / PUT / DELETE, org list and PUT default', () => {
    service.getStorePolicy('s-1').subscribe();
    expect(http.expectOne(`${BASE}/stores/s-1/return-policy`).request.method).toBe('GET');
    service.saveStorePolicy('s-1', { ...DEFAULT_RETURN_POLICY, rowVersion: 'rv' }).subscribe();
    const put = http.expectOne(`${BASE}/stores/s-1/return-policy`);
    expect(put.request.method).toBe('PUT');
    expect(put.request.body.rowVersion).toBe('rv');
    service.deleteStorePolicy('s-1').subscribe();
    expect(http.expectOne(`${BASE}/stores/s-1/return-policy`).request.method).toBe('DELETE');
    service.getPolicies().subscribe();
    expect(http.expectOne(`${BASE}/return-policies`).request.method).toBe('GET');
    service.saveDefaultPolicy(DEFAULT_RETURN_POLICY).subscribe();
    expect(http.expectOne(`${BASE}/return-policies/default`).request.method).toBe('PUT');
  });

  it('store credit: filtered list, detail, transactions, customer list, issue, cancel', () => {
    service.getStoreCredits({ status: 'ACTIVE', customerUuid: 'c-1', storeUuid: '', search: 'CN-', page: 2 }).subscribe();
    const list = http.expectOne(r => r.url === `${BASE}/credit-notes`);
    expect(list.request.params.get('status')).toBe('ACTIVE');
    expect(list.request.params.get('customerUuid')).toBe('c-1');
    expect(list.request.params.has('storeUuid')).toBeFalse();
    expect(list.request.params.get('search')).toBe('CN-');
    expect(list.request.params.get('page')).toBe('2');
    list.flush(ok);
    service.getStoreCredit('cn-1').subscribe();
    http.expectOne(`${BASE}/credit-notes/cn-1`);
    service.getStoreCreditTransactions('cn-1').subscribe();
    http.expectOne(`${BASE}/credit-notes/cn-1/transactions`);
    service.getCustomerStoreCredits('c-1').subscribe();
    http.expectOne(r => r.url === `${BASE}/customers/c-1/credit-notes`);
    service.issueStoreCredit({ storeUuid: 's-1', amount: 50 }).subscribe();
    expect(http.expectOne(`${BASE}/credit-notes`).request.method).toBe('POST');
    service.cancelStoreCredit('cn-1', 'Lost').subscribe();
    const c = http.expectOne(`${BASE}/credit-notes/cn-1/cancel`);
    expect(c.request.method).toBe('PATCH');
    expect(c.request.body).toEqual({ note: 'Lost' });
  });

  it('policy helpers: upsert copy with / without rowVersion, validation, blanks to null, summary', () => {
    const p = policyFixture();
    expect(policyUpsertFrom(p).rowVersion).toBe('prv');
    expect('rowVersion' in policyUpsertFrom(p, false)).toBeFalse();
    expect(policyUpsertFrom(null).name).toBe(DEFAULT_RETURN_POLICY.name);

    const u = policyUpsertFrom(p);
    expect(policyProblem(u)).toBeNull();
    expect(policyProblem({ ...u, name: ' ' })).toBe('A name is required.');
    expect(policyProblem({ ...u, restockingFeePct: 101 })).toBe('The restocking fee must be between 0 and 100%.');
    expect(policyProblem({ ...u, returnWindowDays: -1 })).toBe('The return window cannot be negative.');
    expect(policyProblem({ ...u, maxReturnNoAuth: -5 })).toContain('cannot be negative');
    expect(cleanPolicy({ ...u, name: ' X ', returnWindowDays: '' as any }).returnWindowDays).toBeNull();
    expect(cleanPolicy({ ...u, name: ' X ' }).name).toBe('X');

    expect(policySummary(p)).toBe('30-day returns · exchanges 30 days · receipt required · no restocking fee');
    expect(policySummary(policyFixture({ returnWindowDays: null, exchangeWindowDays: 0, restockingFeePct: 10, allowCrossStoreReturn: true })))
      .toBe('returns any time · no exchanges · receipt required · 10% restocking fee · accepts other stores\' sales');
  });

  it('reason helpers and the manager-required reading of an error', () => {
    expect(isValidReasonCode('PRICE_MATCH')).toBeTrue();
    expect(isValidReasonCode('price')).toBeFalse();
    expect(isValidReasonCode('A'.repeat(21))).toBeFalse();
    const r = reasonUpsertFrom({ uuid: 'r', code: 'X', name: 'X', requiresNote: true, requiresManager: false, appliesTo: 'RETURN',
      restockingFeeOverride: 0, sortOrder: 3, isActive: true, modifiedAt: 'm' });
    expect(r).toEqual(jasmine.objectContaining({ code: 'X', restockingFeeOverride: 0, sortOrder: 3 }));
    expect('uuid' in r).toBeFalse();

    const err = { status: 400, error: { message: 'Manager authorization required.', result: { requiresManager: true, reasons: ['AMOUNT', 'WINDOW'] } } };
    expect(managerRequired(err)).toEqual({ requiresManager: true, reasons: ['AMOUNT', 'WINDOW'] });
    expect(managerRequired({ status: 400, error: { message: 'x' } }).requiresManager).toBeFalse();
    expect(managerNeedLabel('WINDOW')).toBe('the return window has passed');
    expect(managerNeedLabel('ODD')).toBe('ODD');
  });
});
