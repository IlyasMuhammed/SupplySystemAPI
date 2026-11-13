import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { SetupDefaultsService } from './setup-defaults.service';
import { environment } from '../../environments/environment';

/** A50 — super admin api/system/setup-defaults (API-CONTRACT D-CORE). */
describe('SetupDefaultsService', () => {
  let svc: SetupDefaultsService;
  let http: HttpTestingController;
  const base = `${environment.apiUrl}/system/setup-defaults`;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    svc = TestBed.inject(SetupDefaultsService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('list sends only the filters given', () => {
    svc.list({ country: 'PK', category: 'TAX', active: false }).subscribe();
    const r = http.expectOne(x => x.url === base);
    expect(r.request.params.get('country')).toBe('PK');
    expect(r.request.params.get('category')).toBe('TAX');
    expect(r.request.params.get('active')).toBe('false');
    expect(r.request.params.has('industry')).toBeFalse();
    r.flush({ success: true, result: [] });
  });

  it('meta, get, create, update, delete, publish, unpublish, validate hit their routes', () => {
    svc.meta().subscribe(); http.expectOne(`${base}/meta`).flush({ success: true, result: {} });
    svc.get('u1').subscribe(); http.expectOne(`${base}/u1`).flush({ success: true, result: {} });

    svc.create({ countryCode: 'AE', industryType: 'Trading', dataCategory: 'TAX', displayName: 'x', notes: null, isActive: true, dataPayload: { taxCodes: [] } }).subscribe();
    const c = http.expectOne(base);
    expect(c.request.method).toBe('POST');
    expect(c.request.body.dataPayload).toEqual({ taxCodes: [] });
    c.flush({ success: true, result: {} });

    svc.update('u1', { displayName: 'x', notes: null, isActive: true, dataPayload: {}, rowVersion: 'AAA=' }).subscribe();
    const u = http.expectOne(`${base}/u1`);
    expect(u.request.method).toBe('PUT');
    expect(u.request.body.rowVersion).toBe('AAA=');
    u.flush({ success: true, result: {} });

    svc.delete('u1').subscribe();
    expect(http.expectOne(`${base}/u1`).request.method).toBe('DELETE');
    svc.publish('u1').subscribe(); http.expectOne(`${base}/u1/publish`).flush({ success: true, result: {} });
    svc.unpublish('u1').subscribe(); http.expectOne(`${base}/u1/unpublish`).flush({ success: true, result: {} });
    svc.validate('TAX', {}).subscribe();
    expect(http.expectOne(`${base}/validate`).request.body).toEqual({ dataCategory: 'TAX', dataPayload: {} });
  });

  it('import passes dryRun and mode; export asks for a blob', () => {
    svc.import({ defaults: [] }, true, 'insertOnly').subscribe();
    const i = http.expectOne(x => x.url === `${base}/import`);
    expect(i.request.params.get('dryRun')).toBe('true');
    expect(i.request.params.get('mode')).toBe('insertOnly');
    i.flush({ success: true, result: {} });

    svc.export({ country: 'PK' }).subscribe();
    const e = http.expectOne(x => x.url === `${base}/export`);
    expect(e.request.responseType).toBe('blob');
    expect(e.request.params.get('country')).toBe('PK');
    e.flush(new Blob(['{}']));
  });
});
