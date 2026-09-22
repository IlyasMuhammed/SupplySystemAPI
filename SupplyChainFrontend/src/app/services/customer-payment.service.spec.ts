import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { CustomerPaymentService, CUSTOMER_PAYMENT_METHODS, RecordCustomerPaymentRequest } from './customer-payment.service';
import { environment } from '../../environments/environment';

const BASE = `${environment.apiUrl}/customer-payments`;

describe('CustomerPaymentService', () => {
  let service: CustomerPaymentService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(CustomerPaymentService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('offers exactly the methods the server accepts', () => {
    expect(CUSTOMER_PAYMENT_METHODS.map(m => m.value)).toEqual(['CASH', 'CHEQUE', 'BANK_TRANSFER', 'CARD', 'ONLINE']);
  });

  it('posts a payment to the collection', () => {
    const body: RecordCustomerPaymentRequest = {
      partnerId: 'cust-1', amount: 500, method: 'CHEQUE', currencyCode: 'PKR', paymentDate: '2026-09-20', chequeNumber: '004512'
    };

    service.recordPayment(body).subscribe(res => expect(res.result!.paymentNumber).toBe('CPAY-20260920-0001'));

    const req = http.expectOne(BASE);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual(body);
    req.flush({ success: true, message: '', result: { paymentNumber: 'CPAY-20260920-0001' } });
  });

  it('sends the allocations exactly as given, an empty list included', () => {
    service.recordPayment({
      partnerId: 'c', amount: 1, method: 'CASH', currencyCode: 'PKR', allocations: []
    }).subscribe();

    const req = http.expectOne(BASE);
    expect(req.request.body.allocations).toEqual([]);
    req.flush({});
  });

  it('requests the list with only the filters that are set, and defaults the paging', () => {
    service.getPayments({ status: 'RECEIVED', unallocated: true, search: 'CPAY' }).subscribe();

    const req = http.expectOne(r => r.url === BASE);
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('status')).toBe('RECEIVED');
    expect(req.request.params.get('unallocated')).toBe('true');
    expect(req.request.params.get('search')).toBe('CPAY');
    expect(req.request.params.has('method')).toBeFalse();
    expect(req.request.params.has('partnerId')).toBeFalse();
    expect(req.request.params.get('page')).toBe('1');
    expect(req.request.params.get('pageSize')).toBe('20');
    req.flush({});
  });

  it('sends unallocated=false when asked for fully applied payments, and none when it is left out', () => {
    service.getPayments({ unallocated: false }).subscribe();
    let req = http.expectOne(r => r.url === BASE);
    expect(req.request.params.get('unallocated')).toBe('false');
    req.flush({});

    service.getPayments({}).subscribe();
    req = http.expectOne(r => r.url === BASE);
    expect(req.request.params.has('unallocated')).toBeFalse();
    req.flush({});
  });

  it('sends every filter it is given', () => {
    service.getPayments({
      partnerId: 'p', method: 'CARD', dateFrom: '2026-01-01', dateTo: '2026-12-31', page: 3, pageSize: 50
    }).subscribe();

    const p = http.expectOne(r => r.url === BASE).request.params;
    expect([p.get('partnerId'), p.get('method'), p.get('dateFrom'), p.get('dateTo'), p.get('page'), p.get('pageSize')])
      .toEqual(['p', 'CARD', '2026-01-01', '2026-12-31', '3', '50']);
    http.match(BASE);
  });

  it('reads one payment by uuid', () => {
    service.getPayment('pay-1').subscribe();

    const req = http.expectOne(`${BASE}/pay-1`);
    expect(req.request.method).toBe('GET');
    req.flush({});
  });

  it('allocates oldest first with an empty body, and as written with a list', () => {
    service.allocatePayment('pay-1').subscribe();
    let req = http.expectOne(`${BASE}/pay-1/allocate`);
    expect([req.request.method, req.request.body]).toEqual(['POST', {}]);
    req.flush({});

    service.allocatePayment('pay-1', [{ invoiceUuid: 'i1', amount: 40 }]).subscribe();
    req = http.expectOne(`${BASE}/pay-1/allocate`);
    expect(req.request.body).toEqual({ allocations: [{ invoiceUuid: 'i1', amount: 40 }] });
    req.flush({});
  });
});
