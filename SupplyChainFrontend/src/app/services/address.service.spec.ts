import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { AddressService } from './address.service';
import { environment } from '../../environments/environment';

const BASE = `${environment.apiUrl}/addresses`;

describe('AddressService', () => {
  let service: AddressService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(AddressService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('lists a customers addresses by consigneeUuid', () => {
    service.getAddresses('cust-1').subscribe();

    const req = http.expectOne(r => r.url === BASE);
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('consigneeUuid')).toBe('cust-1');
    req.flush({ success: true, message: '', result: [] });
  });

  it('reads one address by uuid', () => {
    service.getAddress('addr-1').subscribe();

    const req = http.expectOne(`${BASE}/addr-1`);
    expect(req.request.method).toBe('GET');
    req.flush({ success: true, message: '', result: null });
  });

  it('posts a new address, with the customer it is for', () => {
    const body = { line1: '7 Canal Rd', cityName: 'Lahore', countryName: 'Pakistan', addressType: 'CUSTOMER', consigneeUuid: 'cust-1' };

    service.createAddress(body).subscribe();

    const req = http.expectOne(BASE);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual(body);
    req.flush({ success: true, message: '', result: null });
  });
});
