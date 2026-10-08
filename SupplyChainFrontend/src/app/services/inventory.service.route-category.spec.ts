import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { InventoryService } from './inventory.service';
import { environment } from '../../environments/environment';

/** A34-PA-10 — GET api/products?routeCategory=STOCK|MANUFACTURE (API-CONTRACT §4.3). */
describe('InventoryService — product list route category (A34)', () => {
  let service: InventoryService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(InventoryService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('sends routeCategory only when asked', () => {
    service.getProducts({ routeCategory: 'MANUFACTURE', page: 1 }).subscribe();
    const filtered = http.expectOne(r => r.url === `${environment.apiUrl}/products`);
    expect(filtered.request.params.get('routeCategory')).toBe('MANUFACTURE');
    filtered.flush({ success: true, message: '', result: { data: [], totalRecords: 0 } });

    service.getProducts({}).subscribe();
    const plain = http.expectOne(r => r.url === `${environment.apiUrl}/products`);
    expect(plain.request.params.has('routeCategory')).toBeFalse();
    plain.flush({ success: true, message: '', result: { data: [], totalRecords: 0 } });
  });
});
