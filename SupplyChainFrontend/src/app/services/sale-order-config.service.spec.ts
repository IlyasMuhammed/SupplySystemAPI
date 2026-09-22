import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { SaleOrderConfigService, UpdateSaleOrderConfigRequest } from './sale-order-config.service';
import { environment } from '../../environments/environment';

const BASE = `${environment.apiUrl}/sale-order-config`;

describe('SaleOrderConfigService', () => {
  let service: SaleOrderConfigService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(SaleOrderConfigService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('reads the policy from the collection address', () => {
    service.getConfig().subscribe(res => expect(res.result.reservationTtlHours).toBe(72));

    const req = http.expectOne(BASE);
    expect(req.request.method).toBe('GET');
    req.flush({ success: true, message: '', result: { reservationTtlHours: 72 } });
  });

  it('puts the whole policy back to the same address', () => {
    const body: UpdateSaleOrderConfigRequest = {
      autoPoEnabled: true, supplierSelectionMode: 'BEST_MATCH', autoPoApprovalMode: 'REQUIRE_WORKFLOW',
      dropShipEnabled: false, selfPickupEnabled: true, defaultFulfillmentMode: 'IN_STOCK', reservationTtlHours: 72,
      partialFulfillmentAllowed: true, emailIntimationEnabled: true, intimationDepartmentId: null,
      intimationCcEmails: null, shipmentRequiredDefault: true
    };

    service.updateConfig(body).subscribe();

    const req = http.expectOne(BASE);
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual(body);
    req.flush({ success: true, message: '', result: {} });
  });

  it('lists the departments to choose from', () => {
    service.getDepartments().subscribe(res => expect(res.result.length).toBe(1));

    const req = http.expectOne(`${BASE}/departments`);
    expect(req.request.method).toBe('GET');
    req.flush({ success: true, message: '', result: [{ departmentId: 1, name: 'Supply', hasHead: true }] });
  });

  it('asks for one page of the change history, newest first as the server orders it', () => {
    service.getAudit(3, 20).subscribe();

    const req = http.expectOne(r => r.url === `${BASE}/audit`);
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('page')).toBe('3');
    expect(req.request.params.get('pageSize')).toBe('20');
    req.flush({ success: true, message: '', result: { data: [], totalRecords: 0, page: 3, pageSize: 20, totalPages: 0 } });
  });

  it('defaults to the first page of twenty', () => {
    service.getAudit().subscribe();

    const req = http.expectOne(r => r.url === `${BASE}/audit`);
    expect(req.request.params.get('page')).toBe('1');
    expect(req.request.params.get('pageSize')).toBe('20');
    req.flush({ success: true, message: '', result: { data: [], totalRecords: 0, page: 1, pageSize: 20, totalPages: 0 } });
  });
});
