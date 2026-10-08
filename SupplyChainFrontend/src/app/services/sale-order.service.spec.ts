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

  it('sends each lines tax code with it, on a create and on an update alike', () => {
    const lines = [
      { variantUuid: 'v1', quantity: 5, discountPercent: 0, taxPercent: 17, taxCodeUuid: 'tc-gst17' },
      { variantUuid: 'v2', quantity: 1, discountPercent: 0, taxPercent: 8 }
    ];

    service.createSaleOrder({ partnerId: 'cust-1', deliveryMode: 'SELF_PICKUP', lines }).subscribe();
    const create = http.expectOne(BASE);
    expect(create.request.body.lines).toEqual(lines);
    create.flush({ success: true, message: '', result: 'new-uuid' });

    service.updateSaleOrder('so-1', { deliveryMode: 'SELF_PICKUP', lines }).subscribe();
    const update = http.expectOne(`${BASE}/so-1`);
    expect(update.request.body.lines).toEqual(lines);
    update.flush({ success: true });
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

  // ── A34 (API-CONTRACT.md §5.2, §6.5) ───────────────────────────────────────

  it('A34: calculates one saved line\'s lead time with an empty body on the line\'s own address', () => {
    service.calculateLineLeadTime('so-1', 'l-2').subscribe(res => expect(res.result!.leadTime.totalLeadTimeDays).toBe(13));

    const req = http.expectOne(`${BASE}/so-1/lines/l-2/lead-time`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({});
    req.flush({ success: true, message: '', result: { line: { uuid: 'l-2' }, leadTime: { totalLeadTimeDays: 13 } } });
  });

  it('A34: puts a manual delivery date as yyyy-MM-dd, and null to clear it', () => {
    service.setLineDeliveryDate('so-1', 'l-2', '2026-10-16').subscribe(res => expect(res.result!.productionNotRescheduled).toBeTrue());
    let req = http.expectOne(`${BASE}/so-1/lines/l-2/delivery-date`);
    expect(req.request.method).toBe('PUT');
    expect(req.request.body).toEqual({ manualDeliveryDate: '2026-10-16' });
    req.flush({ success: true, message: '', result: { line: { uuid: 'l-2' }, productionNotRescheduled: true, warning: 'PROD-1 …' } });

    service.setLineDeliveryDate('so-1', 'l-2', null).subscribe();
    req = http.expectOne(`${BASE}/so-1/lines/l-2/delivery-date`);
    expect(req.request.body).toEqual({ manualDeliveryDate: null });
    req.flush({ success: true });
  });

  it('A34: posts the production recovery with an empty body', () => {
    service.createProductionOrders('so-1').subscribe(res => expect(res.result!.productionOrders.length).toBe(1));

    const req = http.expectOne(`${BASE}/so-1/create-production-orders`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({});
    req.flush({ success: true, message: '', result: {
      productionOrders: [{ productionOrderUuid: 'po-1', productionNumber: 'PROD-1', status: 'DRAFT', plannedQuantity: 50,
                           acceptedQuantity: 0, isMakeToOrder: true, created: true }],
      productionCreationFailed: false } });
  });

  it('A34: sends a line\'s lead-time fields back on save, so a rebuilt draft line keeps them (C-14)', () => {
    const lines = [{
      variantUuid: 'v1', quantity: 50, discountPercent: 0, taxPercent: 0, manualDeliveryDate: '2026-10-20',
      calculatedLeadTimeDays: 13, calculatedDeliveryDate: '2026-10-17', leadTimeCalculatedAt: '2026-10-04T08:00:00Z'
    }];
    service.updateSaleOrder('so-1', { deliveryMode: 'SHIP', lines }).subscribe();
    const req = http.expectOne(`${BASE}/so-1`);
    expect(req.request.body.lines).toEqual(lines);
    req.flush({ success: true });
  });

  it('reads the stock preview from the availability endpoint', () => {
    service.getAvailability('so-1').subscribe(res => expect(res.result!.length).toBe(1));

    const req = http.expectOne(`${BASE}/so-1/availability`);
    expect(req.request.method).toBe('GET');
    req.flush({ success: true, message: '', result: [{ variantUuid: 'v1', orderedQty: 5, availableQty: 5, deficitQty: 0 }] });
  });
});
