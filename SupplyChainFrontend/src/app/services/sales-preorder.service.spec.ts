import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { SalesPreorderService } from './sales-preorder.service';
import { environment } from '../../environments/environment';

// A34 PC-07 — the inquiry and quotation line lead-time endpoints (API-CONTRACT.md §5.2).

describe('SalesPreorderService — A34 line lead times', () => {
  let service: SalesPreorderService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(SalesPreorderService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('calculates an inquiry line\'s lead time with an empty body on the line\'s own address', () => {
    service.calculateInquiryLineLeadTime('inq-1', 'l-1').subscribe(res => {
      expect(res.result!.line.calculatedDeliveryDate).toBe('2026-10-17T00:00:00');
      expect(res.result!.leadTime.totalLeadTimeDays).toBe(13);
    });

    const req = http.expectOne(`${environment.apiUrl}/sale-inquiries/inq-1/lines/l-1/lead-time`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({});
    req.flush({ success: true, message: '', result: {
      line: { uuid: 'l-1', calculatedDeliveryDate: '2026-10-17T00:00:00', calculatedLeadTimeDays: 13 },
      leadTime: { totalLeadTimeDays: 13 } } });
  });

  it('calculates a quotation line\'s lead time with an empty body on the line\'s own address', () => {
    service.calculateQuotationLineLeadTime('q-1', 'l-2').subscribe(res => expect(res.result!.line.uuid).toBe('l-2'));

    const req = http.expectOne(`${environment.apiUrl}/sale-quotations/q-1/lines/l-2/lead-time`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({});
    req.flush({ success: true, message: '', result: { line: { uuid: 'l-2' }, leadTime: { totalLeadTimeDays: 4 } } });
  });
});
