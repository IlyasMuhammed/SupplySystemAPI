import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import {
  PosAdminService, dailyTotals, isHexColor, isValidPin, newLocalId, posFlagLabel, shiftVarianceExceeded, storeUpsertFrom
} from './pos-admin.service';
import { environment } from '../../environments/environment';
import { shiftFixture, storeFixture } from '../pages/pos/pos-fixtures.testing';

const BASE = `${environment.apiUrl}/pos`;

/** A38 — the back-office client of api/pos/* (API-CONTRACT §2, §4–§6, §8). */
describe('PosAdminService (A38)', () => {
  let service: PosAdminService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(PosAdminService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('lists stores with paging, search and the active filter; leaves empty filters out', () => {
    service.getStores({ search: 'mall', isActive: true, page: 2, pageSize: 50 }).subscribe();
    const req = http.expectOne(r => r.url === `${BASE}/stores`);
    expect(req.request.params.get('search')).toBe('mall');
    expect(req.request.params.get('isActive')).toBe('true');
    expect(req.request.params.get('page')).toBe('2');
    expect(req.request.params.get('pageSize')).toBe('50');
    req.flush({ success: true, message: '', result: { data: [], totalRecords: 0 } });

    service.getStores({ search: '', isActive: null }).subscribe();
    const req2 = http.expectOne(r => r.url === `${BASE}/stores`);
    expect(req2.request.params.has('search')).toBeFalse();
    expect(req2.request.params.has('isActive')).toBeFalse();
    req2.flush({ success: true, message: '', result: { data: [], totalRecords: 0 } });
  });

  it('store create / update / status use POST, PUT with rowVersion and PATCH', () => {
    const upsert = storeUpsertFrom(storeFixture());
    service.createStore(upsert).subscribe();
    expect(http.expectOne(`${BASE}/stores`).request.method).toBe('POST');
    service.updateStore('s-1', upsert).subscribe();
    const put = http.expectOne(`${BASE}/stores/s-1`);
    expect(put.request.method).toBe('PUT');
    expect(put.request.body.rowVersion).toBe('rv1');
    expect(put.request.body.code).toBe('S1');
    expect(put.request.body.warehouseName).toBeUndefined();
    service.setStoreStatus('s-1', false).subscribe();
    const patch = http.expectOne(`${BASE}/stores/s-1/status`);
    expect(patch.request.method).toBe('PATCH');
    expect(patch.request.body).toEqual({ isActive: false });
  });

  it('terminal routes and the admin triggers (§2, §8)', () => {
    service.getTerminals('s-1').subscribe();
    http.expectOne(`${BASE}/stores/s-1/terminals`).flush({ success: true, result: [] });
    service.createTerminal('s-1', { code: 'T1', name: 'Till 1' }).subscribe();
    expect(http.expectOne(`${BASE}/stores/s-1/terminals`).request.body).toEqual({ code: 'T1', name: 'Till 1' });
    service.updateTerminal('t-1', { code: 'T1', name: 'Till', isActive: false, rowVersion: 'x' }).subscribe();
    expect(http.expectOne(`${BASE}/terminals/t-1`).request.method).toBe('PUT');
    service.unbindTerminal('t-1').subscribe();
    expect(http.expectOne(`${BASE}/terminals/t-1/unbind`).request.method).toBe('POST');
    service.unlockTerminal('t-1').subscribe();
    expect(http.expectOne(`${BASE}/terminals/t-1/unlock`).request.method).toBe('POST');
    service.bindTerminal('t-1', 'dev-9').subscribe();
    expect(http.expectOne(`${BASE}/terminals/t-1/bind`).request.body).toEqual({ deviceUuid: 'dev-9' });
    service.forceSync('t-1', 'FULL').subscribe();
    expect(http.expectOne(`${BASE}/terminals/t-1/force-sync`).request.body).toEqual({ syncType: 'FULL' });
    service.forceLogout('t-1', 'End of day').subscribe();
    expect(http.expectOne(`${BASE}/terminals/t-1/force-logout`).request.body).toEqual({ reason: 'End of day' });
    service.getTerminalStatus('t-1').subscribe();
    expect(http.expectOne(`${BASE}/terminals/t-1/status`).request.method).toBe('GET');
  });

  it('staff: candidates search, PUT without a pin unless one is set, DELETE', () => {
    service.getStaffCandidates('s-1', 'ali').subscribe();
    const cand = http.expectOne(r => r.url === `${BASE}/stores/s-1/staff/candidates`);
    expect(cand.request.params.get('search')).toBe('ali');
    cand.flush({ success: true, result: [] });

    service.saveStaff('s-1', 7, { posRole: 'SUPERVISOR', pin: '' }).subscribe();
    expect(http.expectOne(`${BASE}/stores/s-1/staff/7`).request.body).toEqual({ posRole: 'SUPERVISOR' });
    service.saveStaff('s-1', 7, { posRole: 'CASHIER', pin: '1234' }).subscribe();
    const put = http.expectOne(`${BASE}/stores/s-1/staff/7`);
    expect(put.request.method).toBe('PUT');
    expect(put.request.body).toEqual({ posRole: 'CASHIER', pin: '1234' });
    service.removeStaff('s-1', 7).subscribe();
    expect(http.expectOne(`${BASE}/stores/s-1/staff/7`).request.method).toBe('DELETE');
  });

  it('shifts: filters, detail, summary, reconcile and note (§4)', () => {
    service.getShifts({ storeUuid: 's-1', status: 'DISPUTED', from: '2026-10-01', to: '2026-10-10' }).subscribe();
    const list = http.expectOne(r => r.url === `${BASE}/shifts`);
    expect(list.request.params.get('storeUuid')).toBe('s-1');
    expect(list.request.params.get('status')).toBe('DISPUTED');
    expect(list.request.params.get('from')).toBe('2026-10-01');
    expect(list.request.params.get('to')).toBe('2026-10-10');
    expect(list.request.params.has('terminalUuid')).toBeFalse();
    list.flush({ success: true, result: { data: [], totalRecords: 0 } });

    service.getShift('sh-1').subscribe();
    http.expectOne(`${BASE}/shifts/sh-1`).flush({ success: true, result: null });
    service.getShiftSummary('sh-1').subscribe();
    http.expectOne(`${BASE}/shifts/sh-1/summary`).flush({ success: true, result: null });
    service.reconcileShift('sh-1', 'Counted twice').subscribe();
    const rec = http.expectOne(`${BASE}/shifts/sh-1/reconcile`);
    expect(rec.request.method).toBe('PATCH');
    expect(rec.request.body).toEqual({ note: 'Counted twice' });
    service.appendShiftNote('sh-1', 'Late note').subscribe();
    expect(http.expectOne(`${BASE}/shifts/sh-1/note`).request.body).toEqual({ note: 'Late note' });
  });

  it('orders: filters, void and return (§5)', () => {
    service.getOrders({ storeUuid: 's-1', customerUuid: 'c-1', orderType: 'RETURN', status: '', search: 'POS-S1' }).subscribe();
    const list = http.expectOne(r => r.url === `${BASE}/orders`);
    expect(list.request.params.get('orderType')).toBe('RETURN');
    expect(list.request.params.get('customerUuid')).toBe('c-1');
    expect(list.request.params.get('search')).toBe('POS-S1');
    expect(list.request.params.has('status')).toBeFalse();
    list.flush({ success: true, result: { data: [], totalRecords: 0 } });

    service.voidOrder('o-1', 'Wrong item').subscribe();
    const v = http.expectOne(`${BASE}/orders/o-1/void`);
    expect(v.request.method).toBe('PATCH');
    expect(v.request.body).toEqual({ reason: 'Wrong item' });
    const ret = { localId: 'l-1', reason: 'Damaged', lines: [{ originalLineUuid: 'ln-1', quantity: 1 }], refunds: [{ method: 'CASH' as const, amount: 50 }] };
    service.returnOrder('o-1', ret).subscribe();
    const r = http.expectOne(`${BASE}/orders/o-1/return`);
    expect(r.request.method).toBe('POST');
    expect(r.request.body).toEqual(ret);
  });

  it('product and customer POS settings (§6)', () => {
    service.getProductSettings('p-1').subscribe();
    http.expectOne(`${BASE}/products/p-1/settings`).flush({ success: true, result: null });
    service.saveProductSettings('p-1', { isWeighable: false, isFavorite: true, sortOrder: 1, isPosActive: true }).subscribe();
    expect(http.expectOne(`${BASE}/products/p-1/settings`).request.method).toBe('PUT');
    service.getCustomerSettings('c-1').subscribe();
    http.expectOne(`${BASE}/customers/c-1/settings`).flush({ success: true, result: null });
    service.saveCustomerSettings('c-1', { allowPosCredit: false, requiresIdForCredit: false }).subscribe();
    expect(http.expectOne(`${BASE}/customers/c-1/settings`).request.method).toBe('PUT');
  });

  it('helpers: PIN, colour, flags, variance, local ids', () => {
    expect(isValidPin('1234')).toBeTrue();
    expect(isValidPin('123456')).toBeTrue();
    expect(isValidPin('123')).toBeFalse();
    expect(isValidPin('1234567')).toBeFalse();
    expect(isValidPin('12a4')).toBeFalse();
    expect(isValidPin(null)).toBeFalse();
    expect(isHexColor('#1E88e5')).toBeTrue();
    expect(isHexColor('1E88E5')).toBeFalse();
    expect(isHexColor('#1E88E')).toBeFalse();
    expect(posFlagLabel('CREDIT_OVER_LIMIT')).toBe('Over credit limit');
    expect(posFlagLabel('SOMETHING_NEW')).toBe('SOMETHING_NEW');
    expect(shiftVarianceExceeded({ cashVariance: -150, varianceThreshold: 100 })).toBeTrue();
    expect(shiftVarianceExceeded({ cashVariance: 100, varianceThreshold: 100 })).toBeFalse();
    expect(shiftVarianceExceeded({ cashVariance: null, varianceThreshold: 0 })).toBeFalse();
    expect(newLocalId()).not.toBe(newLocalId());
  });

  it('dailyTotals groups shifts per local day, newest first', () => {
    const rows = dailyTotals([
      shiftFixture({ openedAt: '2026-10-09T09:00:00', netTotal: 100, totalCashSales: 60, cashVariance: -5, orderCount: 3 }),
      shiftFixture({ openedAt: '2026-10-10T08:00:00', netTotal: 200.1, totalCashSales: 100.05, cashVariance: 0, orderCount: 4 }),
      shiftFixture({ openedAt: '2026-10-10T15:00:00', netTotal: 300.2, totalCashSales: 0.1, cashVariance: null, orderCount: 5 })
    ]);
    expect(rows.map(r => r.date)).toEqual(['2026-10-10', '2026-10-09']);
    expect(rows[0].shifts).toBe(2);
    expect(rows[0].orders).toBe(9);
    expect(rows[0].net).toBe(500.3);
    expect(rows[0].cash).toBe(100.15);
    expect(rows[1].variance).toBe(-5);
  });
});
