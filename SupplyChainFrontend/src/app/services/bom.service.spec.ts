import { TestBed } from '@angular/core/testing';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideHttpClient } from '@angular/common/http';
import { BomService } from './bom.service';
import { environment } from '../../environments/environment';

describe('BomService', () => {
  let service: BomService;
  let http: HttpTestingController;
  const base = `${environment.apiUrl}/boms`;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(BomService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('lists with only the filters given', () => {
    service.getBoms({ status: 'ACTIVE', search: 'shirt', page: 2 }).subscribe();

    const req = http.expectOne(r => r.url === base);
    expect(req.request.params.get('status')).toBe('ACTIVE');
    expect(req.request.params.get('search')).toBe('shirt');
    expect(req.request.params.get('page')).toBe('2');
    expect(req.request.params.has('productUuid')).toBeFalse();
    req.flush({ success: true, result: { data: [], totalRecords: 0, page: 2, pageSize: 20, totalPages: 0 } });
  });

  it('posts the recipe on create and puts on update', () => {
    const create = { productUuid: 'p1', baseQuantity: 1, lines: [{ materialVariantUuid: 'v1', quantity: 2, scrapPercentage: 0, isCritical: true }] };
    service.createBom(create).subscribe();
    const post = http.expectOne(base);
    expect(post.request.method).toBe('POST');
    expect(post.request.body).toEqual(create);
    post.flush({ success: true, result: 'b1' });

    service.updateBom('b1', { baseQuantity: 5 }).subscribe();
    const put = http.expectOne(`${base}/b1`);
    expect(put.request.method).toBe('PUT');
    put.flush({ success: true });
  });

  it('walks the workflow with the right verbs and bodies', () => {
    service.submit('b1').subscribe();
    http.expectOne(`${base}/b1/submit`).flush({ success: true });

    service.approve('b1').subscribe();
    http.expectOne(`${base}/b1/approve`).flush({ success: true });

    service.reject('b1', 'Ink is wrong.').subscribe();
    const reject = http.expectOne(`${base}/b1/reject`);
    expect(reject.request.body).toEqual({ reason: 'Ink is wrong.' });
    reject.flush({ success: true });

    service.activate('b1').subscribe();
    http.expectOne(`${base}/b1/activate`).flush({ success: true });

    service.obsolete('b1').subscribe();
    const obsolete = http.expectOne(`${base}/b1/obsolete`);
    expect(obsolete.request.body).toEqual({ reason: null });
    obsolete.flush({ success: true });

    service.newVersion('b1').subscribe();
    http.expectOne(`${base}/b1/new-version`).flush({ success: true, result: 'b2' });
  });

  it('reads versions by product, a comparison, and a cost', () => {
    service.getVersions('p1').subscribe();
    http.expectOne(r => r.url === `${environment.apiUrl}/products/p1/boms`).flush({ success: true, result: [] });

    service.compare('b1', 'b2').subscribe();
    http.expectOne(`${base}/b1/compare/b2`).flush({ success: true });

    service.getCost('b1').subscribe();
    http.expectOne(`${base}/b1/cost`).flush({ success: true });
  });
});
