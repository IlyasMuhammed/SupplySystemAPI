import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { SaleOrderService, CreateSaleOrderRequest, UpdateSaleOrderRequest } from './sale-order.service';
import { environment } from '../../environments/environment';

const BASE = `${environment.apiUrl}/sale-orders`;

describe('SaleOrderService', () => {
  let service: SaleOrderService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(SaleOrderService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('requests the list with only the filters that are set, and defaults the paging', () => {
    service.getSaleOrders({ status: 'DRAFT', search: 'SO-2026' }).subscribe();

    const req = http.expectOne(r => r.url === BASE);
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('status')).toBe('DRAFT');
    expect(req.request.params.get('search')).toBe('SO-2026');
    expect(req.request.params.has('partnerId')).toBeFalse();
    expect(req.request.params.get('page')).toBe('1');
    expect(req.request.params.get('pageSize')).toBe('20');
    req.flush({});
  });

  it('reads what a new order starts as from the defaults address, which is not an order id', () => {
    service.getDefaults().subscribe(res => expect(res.result.deliveryMode).toBe('SELF_PICKUP'));

    const req = http.expectOne(`${BASE}/defaults`);
    expect(req.request.method).toBe('GET');
    req.flush({ success: true, message: '', result: { deliveryMode: 'SELF_PICKUP', selfPickupEnabled: true } });
  });

  it('posts a new order to the collection', () => {
    const body: CreateSaleOrderRequest = {
      partnerId: 'cust-1', deliveryMode: 'SHIP', shippingAddressId: 'addr-1',
      lines: [{ variantUuid: 'v1', quantity: 5, discountPercent: 0, taxPercent: 17 }]
    };

    service.createSaleOrder(body).subscribe(res => expect(res.result).toBe('new-uuid'));

    const req = http.expectOne(BASE);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual(body);
    req.flush({ success: true, message: '', result: 'new-uuid' });
  });

  it('puts an update to the orders own address', () => {
    const body: UpdateSaleOrderRequest = { deliveryMode: 'SELF_PICKUP', currencyId: 'cur-1', lines: [] };

    service.updateSaleOrder('so-1', body).subscribe();

    const req = http.expectOne(`${BASE}/so-1`);
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual(body);
    req.flush({ success: true });
  });

  it('confirms with an empty body', () => {
    service.confirmSaleOrder('so-1').subscribe();

    const req = http.expectOne(`${BASE}/so-1/confirm`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({});
    req.flush({ success: true });
  });

  it('cancels with the reason, or a null one when none is given', () => {
    service.cancelSaleOrder('so-1', 'Customer withdrew').subscribe();
    let req = http.expectOne(`${BASE}/so-1/cancel`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ reason: 'Customer withdrew' });
    req.flush({ success: true });

    service.cancelSaleOrder('so-1').subscribe();
    req = http.expectOne(`${BASE}/so-1/cancel`);
    expect(req.request.body).toEqual({ reason: null });
    req.flush({ success: true });

    service.cancelSaleOrder('so-1', '').subscribe();
    req = http.expectOne(`${BASE}/so-1/cancel`);
    expect(req.request.body).withContext('an empty reason is no reason').toEqual({ reason: null });
    req.flush({ success: true });
  });

  it('reads the stock preview from the availability endpoint', () => {
    service.getAvailability('so-1').subscribe(res => expect(res.result!.length).toBe(1));

    const req = http.expectOne(`${BASE}/so-1/availability`);
    expect(req.request.method).toBe('GET');
    req.flush({ success: true, message: '', result: [{ variantUuid: 'v1', orderedQty: 5, availableQty: 5, deficitQty: 0 }] });
  });
});
