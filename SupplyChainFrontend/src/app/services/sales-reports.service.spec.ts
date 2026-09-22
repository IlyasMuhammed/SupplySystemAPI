import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { SalesReportsService } from './sales-reports.service';
import { environment } from '../../environments/environment';

const BASE = `${environment.apiUrl}/reports/sales`;

describe('SalesReportsService', () => {
  let service: SalesReportsService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(SalesReportsService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('reads a report from its own address, with the parameters it is given', () => {
    service.getReport('order-register', { status: 'CONFIRMED', page: 2, pageSize: 50 }).subscribe();

    const req = http.expectOne(r => r.url === `${BASE}/order-register`);
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('status')).toBe('CONFIRMED');
    expect(req.request.params.get('page')).toBe('2');
    expect(req.request.params.get('pageSize')).toBe('50');
    req.flush({ success: true, message: '', result: {} });
  });

  it('leaves out a parameter that is empty, undefined or null, but keeps a zero', () => {
    service.getReport('aging-receivables', { asOf: '2026-09-21', partnerId: undefined, status: '', page: 0, x: null as any }).subscribe();

    const req = http.expectOne(r => r.url === `${BASE}/aging-receivables`);
    expect(req.request.params.get('asOf')).toBe('2026-09-21');
    expect(req.request.params.has('partnerId')).toBeFalse();
    expect(req.request.params.has('status')).toBeFalse();
    expect(req.request.params.has('x')).toBeFalse();
    expect(req.request.params.get('page')).toBe('0');
    req.flush({});
  });

  it('addresses every report by the segment the server routes it under', () => {
    const keys = [
      'order-register', 'customer-ledger', 'aging-receivables', 'sales-by-product', 'sales-by-customer',
      'fulfillment-status', 'margin-analysis', 'sales-vs-purchase', 'product-ledger', 'product-profitability'
    ] as const;

    for (const key of keys) {
      service.getReport(key, {}).subscribe();
      http.expectOne(r => r.url === `${BASE}/${key}`).flush({});
    }
  });

  it('downloads a PDF as a blob from the pdf address', () => {
    service.download('sales-by-product', 'pdf', { dateFrom: '2026-09-01' }).subscribe(blob => expect(blob.size).toBe(4));

    const req = http.expectOne(r => r.url === `${BASE}/sales-by-product/pdf`);
    expect(req.request.method).toBe('GET');
    expect(req.request.responseType).toBe('blob');
    expect(req.request.params.get('dateFrom')).toBe('2026-09-01');
    req.flush(new Blob(['%PDF']));
  });

  it('downloads a workbook from the excel address', () => {
    service.download('margin-analysis', 'excel', {}).subscribe();

    const req = http.expectOne(r => r.url === `${BASE}/margin-analysis/excel`);
    expect(req.request.responseType).toBe('blob');
    req.flush(new Blob(['PK']));
  });

  it('drops the paging from a download, since the server carries every row', () => {
    service.download('order-register', 'pdf', { status: 'CONFIRMED', page: 3, pageSize: 20 }).subscribe();

    const req = http.expectOne(r => r.url === `${BASE}/order-register/pdf`);
    expect(req.request.params.get('status')).toBe('CONFIRMED');
    expect(req.request.params.has('page')).toBeFalse();
    expect(req.request.params.has('pageSize')).toBeFalse();
    req.flush(new Blob([]));
  });
});
