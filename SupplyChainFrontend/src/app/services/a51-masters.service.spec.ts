import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { CountriesAdminService } from './countries-admin.service';
import { IndustriesService } from './industries.service';
import { environment } from '../../environments/environment';

/** A51 P1 - the exact routes of the country and industry masters. */
describe('A51 masters services', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('CountriesAdminService hits api/system/countries', () => {
    const svc = TestBed.inject(CountriesAdminService);
    const base = `${environment.apiUrl}/system/countries`;
    svc.list().subscribe(); http.expectOne(base).flush({ success: true, result: [] });
    svc.get('g1').subscribe(); http.expectOne(`${base}/g1`).flush({ success: true, result: {} });
    svc.create({ code: 'PK', name: 'Pakistan', currencyCode: 'PKR', defaultTimezone: null, fiscalYearStartMonth: 7, dateFormat: 'DD/MM/YYYY', phoneCode: null }).subscribe();
    const c = http.expectOne(base); expect(c.request.method).toBe('POST'); expect(c.request.body.code).toBe('PK'); c.flush({ success: true, result: {} });
    svc.update('g1', { name: 'x', currencyCode: null, defaultTimezone: null, fiscalYearStartMonth: 1, dateFormat: 'D', phoneCode: null }).subscribe();
    expect(http.expectOne(`${base}/g1`).request.method).toBe('PUT');
    svc.delete('g1').subscribe(); expect(http.expectOne(`${base}/g1`).request.method).toBe('DELETE');
    svc.activate('g1').subscribe(); expect(http.expectOne(`${base}/g1/activate`).request.method).toBe('POST');
    svc.deactivate('g1').subscribe(); expect(http.expectOne(`${base}/g1/deactivate`).request.method).toBe('POST');
  });

  it('IndustriesService: management under api/system/industries, the country read on api/industries', () => {
    const svc = TestBed.inject(IndustriesService);
    const base = `${environment.apiUrl}/system/industries`;
    svc.list().subscribe(); http.expectOne(base).flush({ success: true, result: [] });
    svc.get(4).subscribe(); http.expectOne(`${base}/4`).flush({ success: true, result: {} });
    svc.create({ code: 'X', name: 'X', description: null, countryCodes: ['PK'] }).subscribe();
    const c = http.expectOne(base); expect(c.request.method).toBe('POST'); expect(c.request.body.countryCodes).toEqual(['PK']);
    svc.update(4, { name: 'X', description: null }).subscribe(); expect(http.expectOne(`${base}/4`).request.method).toBe('PUT');
    svc.assignToCountry(4, 'AE').subscribe();
    const a = http.expectOne(`${base}/4/countries`); expect(a.request.method).toBe('POST'); expect(a.request.body).toEqual({ countryCode: 'AE' });
    svc.removeFromCountry(4, 'AE').subscribe(); expect(http.expectOne(`${base}/4/countries/AE`).request.method).toBe('DELETE');

    svc.byCountry('PK').subscribe();
    const r = http.expectOne(x => x.url === `${environment.apiUrl}/industries`);
    expect(r.request.params.get('country')).toBe('PK');
  });
});
