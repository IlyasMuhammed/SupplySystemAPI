import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { ServiceOrderService, readinessIcon, serviceStatusTone } from './service-order.service';
import { environment } from '../../environments/environment';

const BASE = `${environment.apiUrl}/service-orders`;

// A36 — every endpoint of docs/service-orders/API-CONTRACT.md §3.
describe('ServiceOrderService', () => {
  let service: ServiceOrderService;
  let http: HttpTestingController;
  const flushOk = (req: ReturnType<HttpTestingController['expectOne']>, result: unknown = null) =>
    req.flush({ success: true, message: '', result });

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(ServiceOrderService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('#1 creates and reads the uuid back', () => {
    let uuid = '';
    service.create({ serviceProductUuid: 'p', customerUuid: 'c', quantity: 2, warehouseUuid: 'w', scheduledDate: '2026-10-12', scheduledTime: '09:30' })
      .subscribe(r => uuid = r.result);
    const req = http.expectOne(BASE);
    expect(req.request.method).toBe('POST');
    expect(req.request.body.scheduledTime).toBe('09:30');
    flushOk(req, 'so-1');
    expect(uuid).toBe('so-1');
  });

  it('#2 lists with comma-separated statuses and every filter, 25 a page by default', () => {
    service.getList({ status: ['DRAFT', 'WAITING'], customerUuid: 'c', assignedUserId: 7, fromDate: '2026-10-01', toDate: '2026-10-31', priority: 0, search: 'ac' }).subscribe();
    const req = http.expectOne(r => r.url === BASE);
    const p = req.request.params;
    expect(p.get('status')).toBe('DRAFT,WAITING');
    expect(p.get('customerUuid')).toBe('c');
    expect(p.get('assignedUserId')).toBe('7');
    expect(p.get('fromDate')).toBe('2026-10-01');
    expect(p.get('toDate')).toBe('2026-10-31');
    expect(p.get('priority')).toBe('0');
    expect(p.get('search')).toBe('ac');
    expect(p.get('page')).toBe('1');
    expect(p.get('pageSize')).toBe('25');
    flushOk(req, { data: [], totalRecords: 0, page: 1, pageSize: 25, totalPages: 0 });
  });

  it('#2 leaves out filters that are not set', () => {
    service.getList().subscribe();
    const req = http.expectOne(r => r.url === BASE);
    expect(req.request.params.keys().sort()).toEqual(['page', 'pageSize']);
    flushOk(req, { data: [] });
  });

  it('#3/#4 reads and updates with the row version', () => {
    service.getById('so-1').subscribe();
    flushOk(http.expectOne(`${BASE}/so-1`), {});
    service.update('so-1', { customerUuid: 'c', quantity: 1, warehouseUuid: 'w', priority: 1, rowVersion: 'AAAA' }).subscribe();
    const req = http.expectOne(`${BASE}/so-1`);
    expect(req.request.method).toBe('PUT');
    expect(req.request.body.rowVersion).toBe('AAAA');
    flushOk(req, {});
  });

  it('#5–#8a posts the lifecycle actions', () => {
    service.plan('so-1').subscribe();
    service.start('so-1').subscribe();
    service.close('so-1').subscribe();
    service.cancel('so-1', { reason: 'Customer called off' }).subscribe();
    service.complete('so-1', { consumedMaterials: [{ smrUuid: 'm1', consumedQuantity: 1.5 }], actualHours: 3, customerSignature: true }).subscribe();
    for (const action of ['plan', 'start', 'close']) {
      const req = http.expectOne(`${BASE}/so-1/${action}`);
      expect(req.request.method).toBe('POST');
      flushOk(req, {});
    }
    const cancel = http.expectOne(`${BASE}/so-1/cancel`);
    expect(cancel.request.body).toEqual({ reason: 'Customer called off' });
    flushOk(cancel, {});
    const complete = http.expectOne(`${BASE}/so-1/complete`);
    expect(complete.request.body.consumedMaterials).toEqual([{ smrUuid: 'm1', consumedQuantity: 1.5 }]);
    expect(complete.request.body.customerSignature).toBeTrue();
    flushOk(complete, {});
  });

  it('#9–#11a materials: list, add ad-hoc, remove, reserve all', () => {
    service.getMaterials('so-1').subscribe();
    flushOk(http.expectOne(`${BASE}/so-1/materials`), []);
    service.addMaterial('so-1', { variantUuid: 'v', quantity: 2, notes: 'extra pipe' }).subscribe();
    const add = http.expectOne(`${BASE}/so-1/materials`);
    expect(add.request.method).toBe('POST');
    expect(add.request.body).toEqual({ variantUuid: 'v', quantity: 2, notes: 'extra pipe' });
    flushOk(add, {});
    service.removeMaterial('so-1', 'm1').subscribe();
    const del = http.expectOne(`${BASE}/so-1/materials/m1`);
    expect(del.request.method).toBe('DELETE');
    flushOk(del, {});
    service.allocate('so-1').subscribe();
    const alloc = http.expectOne(`${BASE}/so-1/materials/allocate`);
    expect(alloc.request.method).toBe('POST');
    flushOk(alloc, {});
  });

  it('#12/#13 ledger and dashboard', () => {
    service.getLedger('so-1').subscribe();
    flushOk(http.expectOne(`${BASE}/so-1/ledger`), { entries: [], netByProduct: [] });
    service.getDashboard().subscribe();
    flushOk(http.expectOne(`${BASE}/dashboard`), {});
  });

  it('tones every status as the screens expect', () => {
    expect(['DRAFT', 'PLANNED', 'MATERIAL_PENDING', 'WAITING', 'READY', 'IN_PROGRESS', 'COMPLETED', 'CLOSED', 'CANCELLED'].map(serviceStatusTone))
      .toEqual(['', 'in', 'wn', 'wn', 'ok', 'te', 'ok', '', 'er']);
    expect(readinessIcon('SHORTAGE').tone).toBe('er');
    expect(readinessIcon(undefined).label).toBe('Not checked yet');
  });
});
