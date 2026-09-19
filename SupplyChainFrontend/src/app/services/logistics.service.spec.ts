import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import {
  LogisticsService,
  DeliveryFilter,
  CreateDeliveryFromSourceRequest,
  CreateDeliveryRequest
} from './logistics.service';
import { environment } from '../../environments/environment';

const BASE = `${environment.apiUrl}/logistics`;

describe('LogisticsService — deliveries', () => {
  let service: LogisticsService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()]
    });
    service = TestBed.inject(LogisticsService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  // ── TC-18.1 ────────────────────────────────────────────────────────────────

  it('requests the delivery list with the filter as query parameters', () => {
    const filter: DeliveryFilter = {
      status: 'RELEASED',
      direction: 'OUTBOUND',
      sourceType: 'PO',
      search: 'DLV-2026',
      page: 3,
      pageSize: 50
    };

    service.getDeliveries(filter).subscribe();

    const req = http.expectOne(r => r.url === `${BASE}/deliveries`);
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('status')).toBe('RELEASED');
    expect(req.request.params.get('direction')).toBe('OUTBOUND');
    expect(req.request.params.get('sourceType')).toBe('PO');
    expect(req.request.params.get('search')).toBe('DLV-2026');
    expect(req.request.params.get('page')).toBe('3');
    expect(req.request.params.get('pageSize')).toBe('50');

    req.flush({ success: true, message: '', result: { data: [], totalRecords: 0, page: 3, pageSize: 50, totalPages: 0 } });
  });

  // ── TC-18.2 ────────────────────────────────────────────────────────────────

  it('omits filters that were not set rather than sending them empty', () => {
    // An empty value sent as a parameter filters for the empty string, which matches nothing —
    // silently returning no rows instead of all of them.
    service.getDeliveries({ status: 'DRAFT' }).subscribe();

    const req = http.expectOne(r => r.url === `${BASE}/deliveries`);
    expect(req.request.params.has('status')).toBeTrue();
    expect(req.request.params.has('direction')).toBeFalse();
    expect(req.request.params.has('sourceType')).toBeFalse();
    expect(req.request.params.has('search')).toBeFalse();
    expect(req.request.params.has('fromDate')).toBeFalse();
    expect(req.request.params.has('toDate')).toBeFalse();

    req.flush({ success: true, message: '', result: null });
  });

  it('always sends paging, defaulting to the first page of twenty', () => {
    service.getDeliveries().subscribe();

    const req = http.expectOne(r => r.url === `${BASE}/deliveries`);
    expect(req.request.params.get('page')).toBe('1');
    expect(req.request.params.get('pageSize')).toBe('20');

    req.flush({ success: true, message: '', result: null });
  });

  it('treats an empty search string as no search at all', () => {
    service.getDeliveries({ search: '' }).subscribe();

    const req = http.expectOne(r => r.url === `${BASE}/deliveries`);
    expect(req.request.params.has('search')).toBeFalse();

    req.flush({ success: true, message: '', result: null });
  });

  // ── TC-18.3 ────────────────────────────────────────────────────────────────

  it('posts a from-source request to the from-source endpoint', () => {
    const req: CreateDeliveryFromSourceRequest = {
      sourceType: 'PO',
      sourceUuid: '11111111-1111-1111-1111-111111111111'
    };

    service.createDeliveryFromSource(req).subscribe();

    const call = http.expectOne(`${BASE}/deliveries/from-source`);
    expect(call.request.method).toBe('POST');
    expect(call.request.body).toEqual(req);

    call.flush({ success: true, message: '', result: 'new-uuid' });
  });

  it('carries a partial line selection through unchanged', () => {
    const req: CreateDeliveryFromSourceRequest = {
      sourceType: 'PO',
      sourceUuid: '11111111-1111-1111-1111-111111111111',
      lines: [{ sourceLineUuid: '22222222-2222-2222-2222-222222222222', qty: 30 }]
    };

    service.createDeliveryFromSource(req).subscribe();

    const call = http.expectOne(`${BASE}/deliveries/from-source`);
    expect(call.request.body.lines.length).toBe(1);
    expect(call.request.body.lines[0].qty).toBe(30);

    call.flush({ success: true, message: '', result: 'new-uuid' });
  });

  it('posts a manual delivery to the plain create endpoint, not from-source', () => {
    // TRANSFER and MANUAL have no source document to copy lines from; the server rejects them
    // on from-source and names this endpoint.
    const req: CreateDeliveryRequest = {
      sourceType: 'TRANSFER',
      shipFromWarehouseUuid: 'aaaa1111-1111-1111-1111-111111111111',
      shipToWarehouseUuid: 'bbbb2222-2222-2222-2222-222222222222',
      lines: [{ itemDescription: '4mm cable', qtyOrdered: 50 }]
    };

    service.createDelivery(req).subscribe();

    const call = http.expectOne(`${BASE}/deliveries`);
    expect(call.request.method).toBe('POST');
    expect(call.request.body.sourceType).toBe('TRANSFER');

    call.flush({ success: true, message: '', result: 'new-uuid' });
  });

  // ── TC-18.4 ────────────────────────────────────────────────────────────────

  it('surfaces a server error to the caller instead of swallowing it', () => {
    let error: unknown = null;
    let next: unknown = null;

    service.getDeliveries().subscribe({ next: v => (next = v), error: e => (error = e) });

    http.expectOne(r => r.url === `${BASE}/deliveries`)
        .flush({ success: false, message: 'Boom' }, { status: 500, statusText: 'Server Error' });

    expect(error).toBeTruthy();
    expect(next).toBeNull();
  });

  it('surfaces a rejected write so the screen can show why', () => {
    // The server refuses, for example, cancelling a delivery whose stock has already left, and
    // the message explains it. Swallowing that would leave the user staring at a button that
    // silently does nothing.
    let message: string | null = null;

    service.cancelDelivery('some-uuid', { reason: 'no longer needed' })
           .subscribe({ error: e => (message = e.error?.message ?? null) });

    http.expectOne(`${BASE}/deliveries/some-uuid/cancel`)
        .flush({ success: false, message: 'A delivery in GOODS_ISSUED cannot move to CANCELLED.' },
               { status: 409, statusText: 'Conflict' });

    expect(message).toContain('GOODS_ISSUED');
  });

  // ── Status actions ─────────────────────────────────────────────────────────

  it('sends the reason with hold, cancel and short-close', () => {
    const uuid = 'abc';
    const body = { reason: 'Site access blocked' };

    service.holdDelivery(uuid, body).subscribe();
    const hold = http.expectOne(`${BASE}/deliveries/${uuid}/hold`);
    expect(hold.request.body).toEqual(body);
    hold.flush({ success: true, message: '' });

    service.cancelDelivery(uuid, body).subscribe();
    const cancel = http.expectOne(`${BASE}/deliveries/${uuid}/cancel`);
    expect(cancel.request.body).toEqual(body);
    cancel.flush({ success: true, message: '' });

    service.shortCloseDelivery(uuid, body).subscribe();
    const shortClose = http.expectOne(`${BASE}/deliveries/${uuid}/short-close`);
    expect(shortClose.request.body).toEqual(body);
    shortClose.flush({ success: true, message: '' });
  });

  it('posts resume with an empty body rather than no body', () => {
    // A POST with a null body is sent without a Content-Type, which ASP.NET rejects before the
    // action is reached.
    service.resumeDelivery('abc').subscribe();

    const call = http.expectOne(`${BASE}/deliveries/abc/resume`);
    expect(call.request.body).toEqual({});

    call.flush({ success: true, message: '' });
  });

  // ── Consignments ───────────────────────────────────────────────────────────

  it('books a consignment manually with its airway bill', () => {
    service.bookConsignmentManually('c1', { awb: 'TCS-99', carrierReference: 'REF-7' }).subscribe();

    const call = http.expectOne(`${BASE}/consignments/c1/book-manual`);
    expect(call.request.method).toBe('POST');
    expect(call.request.body.awb).toBe('TCS-99');

    call.flush({ success: true, message: '' });
  });

  it('attaches a delivery to a consignment by both ids', () => {
    service.attachDeliveryToConsignment('c1', 'd1').subscribe();

    const call = http.expectOne(`${BASE}/consignments/c1/deliveries/d1`);
    expect(call.request.method).toBe('POST');

    call.flush({ success: true, message: '' });
  });

  // ── The legacy endpoints are untouched ─────────────────────────────────────

  it('still calls the legacy shipment endpoints unchanged', () => {
    // Four screens depend on these until the cockpit replaces them.
    service.getShipments({ status: 'Delivered' }).subscribe();

    const call = http.expectOne(r => r.url === `${BASE}/shipments`);
    expect(call.request.params.get('status')).toBe('Delivered');

    call.flush({ success: true, message: '', result: null });
  });
});
