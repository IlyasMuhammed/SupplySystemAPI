import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { CustomerLedgerService } from './customer-ledger.service';
import { environment } from '../../environments/environment';

describe('CustomerLedgerService', () => {
  let service: CustomerLedgerService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(CustomerLedgerService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('reads the ledger from the partners own address, with default paging', () => {
    service.getLedger('cust-1').subscribe();

    const req = http.expectOne(r => r.url === `${environment.apiUrl}/partners/cust-1/ledger`);
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('page')).toBe('1');
    expect(req.request.params.get('pageSize')).toBe('20');
    expect(req.request.params.has('dateFrom')).toBeFalse();
    expect(req.request.params.has('dateTo')).toBeFalse();
    req.flush({});
  });

  it('sends the date range and the page it is given', () => {
    service.getLedger('cust-1', { dateFrom: '2026-09-01', dateTo: '2026-09-30', page: 2, pageSize: 50 }).subscribe();

    const p = http.expectOne(r => r.url.endsWith('/ledger')).request.params;
    expect([p.get('dateFrom'), p.get('dateTo'), p.get('page'), p.get('pageSize')]).toEqual(['2026-09-01', '2026-09-30', '2', '50']);
    http.match(r => r.url.endsWith('/ledger'));
  });
});
