import { TestBed } from '@angular/core/testing';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideHttpClient } from '@angular/common/http';
import { AllocationService } from './allocation.service';
import { environment } from '../../environments/environment';

describe('AllocationService', () => {
  let service: AllocationService;
  let http: HttpTestingController;
  const base = `${environment.apiUrl}/allocations`;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(AllocationService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('lists allocations with only the filters that were given', () => {
    service.getAllocations({ variantUuid: 'v1', status: 'ACTIVE', page: 2 }).subscribe();

    const req = http.expectOne(r => r.url === base);
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('variantUuid')).toBe('v1');
    expect(req.request.params.get('status')).toBe('ACTIVE');
    expect(req.request.params.get('page')).toBe('2');
    expect(req.request.params.has('warehouseUuid')).toBeFalse();
    req.flush({ success: true, result: { items: [], total: 0, page: 2, pageSize: 50 } });
  });

  it('runs the engine for a variant, with the warehouse as null when none is chosen', () => {
    service.run('v1').subscribe();

    const req = http.expectOne(`${base}/run`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ variantUuid: 'v1', warehouseUuid: null });
    req.flush({ success: true });
  });

  it('releases and moves an allocation with a reason', () => {
    service.release('a1', 'Held by mistake.').subscribe();
    const release = http.expectOne(`${base}/a1/release`);
    expect(release.request.body).toEqual({ reason: 'Held by mistake.' });
    release.flush({ success: true });

    service.reallocate('a1', 'd2', 3, 'Board decision.').subscribe();
    const move = http.expectOne(`${base}/a1/reallocate`);
    expect(move.request.body).toEqual({ toDemandUuid: 'd2', quantity: 3, reason: 'Board decision.' });
    move.flush({ success: true });
  });

  it('asks for availability and open demands by variant', () => {
    service.getAvailability('v1', 'w1').subscribe();
    const availability = http.expectOne(r => r.url === `${base}/availability`);
    expect(availability.request.params.get('variantUuid')).toBe('v1');
    expect(availability.request.params.get('warehouseUuid')).toBe('w1');
    availability.flush({ success: true });

    service.getDemands('v1').subscribe();
    const demands = http.expectOne(r => r.url === `${base}/demands`);
    expect(demands.request.params.get('variantUuid')).toBe('v1');
    expect(demands.request.params.get('openOnly')).toBe('true');
    demands.flush({ success: true, result: [] });
  });

  it('reads and replaces the rules', () => {
    service.getRules().subscribe();
    http.expectOne(`${base}/rules`).flush({ success: true, result: [] });

    const rules = [{ ruleName: 'Date', priorityOrder: 1, demandTypeFilter: null, sortField: 'REQUIRED_DATE', sortDirection: 'ASC' as const, isActive: true }];
    service.setRules(rules).subscribe();
    const put = http.expectOne(`${base}/rules`);
    expect(put.request.method).toBe('PUT');
    expect(put.request.body).toEqual({ rules });
    put.flush({ success: true, result: rules });
  });
});
