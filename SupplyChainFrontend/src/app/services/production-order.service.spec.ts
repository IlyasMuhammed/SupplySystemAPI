import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { ProductionOrderService } from './production-order.service';
import { environment } from '../../environments/environment';

const BASE = `${environment.apiUrl}/production-orders`;

// A34 PE-06 — "Create delivery now" (API-CONTRACT.md §7).

describe('ProductionOrderService — A34 create delivery', () => {
  let service: ProductionOrderService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(ProductionOrderService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('posts "Create delivery now" with an empty body and reads the handoff back', () => {
    service.createDelivery('po-1').subscribe(res => {
      expect(res.result!.quantityCreated).toBe(48);
      expect(res.result!.deliveryNumber).toBe('DLV-2026-00005');
    });

    const req = http.expectOne(`${BASE}/po-1/create-delivery`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({});
    req.flush({ success: true, message: '', result: {
      productionOrderUuid: 'po-1', quantityCreated: 48, deliveryUuid: 'd-5', deliveryNumber: 'DLV-2026-00005',
      latestDeliveryUuid: 'd-5', latestDeliveryNumber: 'DLV-2026-00005' } });
  });

  it('surfaces the server\'s refusal to the caller', () => {
    let message = '';
    service.createDelivery('po-1').subscribe({ error: e => message = e.error.message });

    http.expectOne(`${BASE}/po-1/create-delivery`)
      .flush({ success: false, message: 'PROD-1 has no accepted quantity yet.' }, { status: 400, statusText: 'Bad Request' });
    expect(message).toContain('no accepted quantity');
  });
});
