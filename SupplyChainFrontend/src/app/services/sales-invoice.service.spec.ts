import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { SalesInvoiceService } from './sales-invoice.service';
import { environment } from '../../environments/environment';

const BASE = `${environment.apiUrl}/sales-invoices`;

describe('SalesInvoiceService', () => {
  let service: SalesInvoiceService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(SalesInvoiceService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('asks for one orders invoices by the order uuid', () => {
    service.getInvoices({ saleOrderUuid: 'so-1', pageSize: 100 }).subscribe();

    const req = http.expectOne(r => r.url === BASE);
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('saleOrderUuid')).toBe('so-1');
    expect(req.request.params.get('pageSize')).toBe('100');
    expect(req.request.params.get('page')).toBe('1');
    expect(req.request.params.has('status')).toBeFalse();
    req.flush({});
  });

  it('sends every filter it is given', () => {
    service.getInvoices({
      partnerId: 'p', status: 'PAID', dateFrom: '2026-01-01', dateTo: '2026-12-31', search: 'INV', page: 2, pageSize: 5
    }).subscribe();

    const req = http.expectOne(r => r.url === BASE);
    const p = req.request.params;
    expect([p.get('partnerId'), p.get('status'), p.get('dateFrom'), p.get('dateTo'), p.get('search'), p.get('page'), p.get('pageSize')])
      .toEqual(['p', 'PAID', '2026-01-01', '2026-12-31', 'INV', '2', '5']);
    req.flush({});
  });

  it('reads one invoice by uuid', () => {
    service.getInvoice('inv-1').subscribe();

    const req = http.expectOne(`${BASE}/inv-1`);
    expect(req.request.method).toBe('GET');
    req.flush({});
  });

  it('downloads the PDF as a blob', () => {
    service.downloadPdf('inv-1').subscribe(blob => expect(blob.size).toBe(4));

    const req = http.expectOne(`${BASE}/inv-1/pdf`);
    expect(req.request.method).toBe('GET');
    expect(req.request.responseType).toBe('blob');
    req.flush(new Blob(['%PDF']));
  });

  it('raises an invoice by posting the delivery', () => {
    service.createFromDelivery('dlv-1').subscribe(res => expect(res.result!.invoiceNumber).toBe('SINV-20260920-0001'));

    const req = http.expectOne(BASE);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ deliveryUuid: 'dlv-1' });
    req.flush({ success: true, message: '', result: { invoiceUuid: 'i', invoiceNumber: 'SINV-20260920-0001', grandTotal: 1, currencyCode: 'PKR', alreadyExisted: false } });
  });

  it('issues, edits, deletes and files the PDF of an invoice at its own address', () => {
    service.issueInvoice('inv-1').subscribe();
    let req = http.expectOne(`${BASE}/inv-1/issue`);
    expect([req.request.method, req.request.body]).toEqual(['POST', {}]);
    req.flush({ success: true });

    service.updateInvoice('inv-1', { dueDate: '2026-10-15', notes: 'Net 30' }).subscribe();
    req = http.expectOne(`${BASE}/inv-1`);
    expect([req.request.method, req.request.body]).toEqual(['PUT', { dueDate: '2026-10-15', notes: 'Net 30' }]);
    req.flush({ success: true });

    service.deleteInvoice('inv-1').subscribe();
    req = http.expectOne(`${BASE}/inv-1`);
    expect(req.request.method).toBe('DELETE');
    req.flush({ success: true });

    service.attachPdf('inv-1').subscribe();
    req = http.expectOne(`${BASE}/inv-1/attach-pdf`);
    expect([req.request.method, req.request.body]).toEqual(['POST', {}]);
    req.flush({ success: true });
  });

  // ── Open invoices: what a payment can be applied to ────────────────────────

  function invoice(uuid: string, overrides: Record<string, unknown> = {}) {
    return {
      uuid, invoiceNumber: `INV-${uuid}`, invoiceDate: '2026-09-01T00:00:00Z', dueDate: '2026-10-01T00:00:00Z',
      grandTotal: 100, amountPaid: 0, balanceDue: 100, status: 'ISSUED', currencyCode: 'PKR', ...overrides
    };
  }

  function answer(status: string, data: unknown[], totalRecords = data.length) {
    const req = http.expectOne(r => r.url === BASE && r.params.get('status') === status);
    expect(req.request.params.get('partnerId')).toBe('cust-1');
    expect(req.request.params.get('pageSize')).toBe('100');
    req.flush({ success: true, message: '', result: { data, totalRecords, page: 1, pageSize: 100, totalPages: 1 } });
  }

  it('asks once for each status a payment can be applied to, and joins the answers oldest first', () => {
    let result: any;
    service.getOpenInvoices('cust-1').subscribe(r => result = r);

    answer('ISSUED',         [invoice('c', { invoiceDate: '2026-09-20T00:00:00Z' })]);
    answer('PARTIALLY_PAID', [invoice('a', { invoiceDate: '2026-08-01T00:00:00Z', balanceDue: 40 })]);
    answer('OVERDUE',        [invoice('b', { invoiceDate: '2026-08-15T00:00:00Z' })]);

    expect(result.invoices.map((i: any) => i.uuid)).toEqual(['a', 'b', 'c']);
    expect(result.truncated).toBeFalse();
  });

  it('breaks a tie on the invoice date by invoice number', () => {
    let result: any;
    service.getOpenInvoices('cust-1').subscribe(r => result = r);

    answer('ISSUED',         [invoice('2'), invoice('1')]);
    answer('PARTIALLY_PAID', []);
    answer('OVERDUE',        []);

    expect(result.invoices.map((i: any) => i.uuid)).toEqual(['1', '2']);
  });

  it('keeps only the invoices in the currency asked for, and drops any with nothing owing', () => {
    let result: any;
    service.getOpenInvoices('cust-1', 'PKR').subscribe(r => result = r);

    answer('ISSUED', [invoice('pkr'), invoice('usd', { currencyCode: 'USD' }), invoice('settled', { balanceDue: 0 })]);
    answer('PARTIALLY_PAID', []);
    answer('OVERDUE', []);

    expect(result.invoices.map((i: any) => i.uuid)).toEqual(['pkr']);
  });

  it('keeps every currency when none is asked for', () => {
    let result: any;
    service.getOpenInvoices('cust-1').subscribe(r => result = r);

    answer('ISSUED', [invoice('pkr'), invoice('usd', { currencyCode: 'USD' })]);
    answer('PARTIALLY_PAID', []);
    answer('OVERDUE', []);

    expect(result.invoices.length).toBe(2);
  });

  it('says so when a status had more invoices than a page holds', () => {
    let result: any;
    service.getOpenInvoices('cust-1').subscribe(r => result = r);

    answer('ISSUED', [invoice('a')], 250);
    answer('PARTIALLY_PAID', []);
    answer('OVERDUE', []);

    expect(result.truncated).toBeTrue();
  });

  it('fails as a whole when any one of the three requests fails', () => {
    let failed = false;
    service.getOpenInvoices('cust-1').subscribe({ error: () => failed = true });

    http.expectOne(r => r.params.get('status') === 'ISSUED').flush({}, { status: 500, statusText: 'Server Error' });
    // The other two are cancelled with it.
    http.match(r => r.url === BASE);

    expect(failed).toBeTrue();
  });
});
