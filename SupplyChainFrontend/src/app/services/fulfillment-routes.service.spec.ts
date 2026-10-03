import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import {
  FulfillmentRoutesService, FulfillmentRouteStepRequest, routeStepsText, validateRouteSteps
} from './fulfillment-routes.service';
import { environment } from '../../environments/environment';

const BASE = `${environment.apiUrl}/fulfillment-routes`;
const ID = '11111111-1111-1111-1111-111111111111';

const steps = (...codes: string[]): FulfillmentRouteStepRequest[] =>
  codes.map((c, i) => ({ stepCode: c as FulfillmentRouteStepRequest['stepCode'], stepOrder: i + 1, isMandatory: true }));

describe('FulfillmentRoutesService', () => {
  let service: FulfillmentRoutesService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(FulfillmentRoutesService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('lists active routes without a filter parameter', () => {
    service.getRoutes().subscribe();
    const req = http.expectOne(r => r.url === BASE);
    expect(req.request.method).toBe('GET');
    expect(req.request.params.has('includeInactive')).toBeFalse();
    req.flush({ success: true, message: '', result: [] });
  });

  it('asks for inactive routes too for the settings screen', () => {
    service.getRoutes(true).subscribe();
    const req = http.expectOne(r => r.url === BASE);
    expect(req.request.params.get('includeInactive')).toBe('true');
    req.flush({ success: true, message: '', result: [] });
  });

  it('creates with POST and updates with PUT on the route address', () => {
    service.createRoute({ code: 'HV', name: 'High value', steps: steps('PICK', 'GOODS_ISSUE') }).subscribe();
    const create = http.expectOne(BASE);
    expect(create.request.method).toBe('POST');
    expect(create.request.body.code).toBe('HV');
    create.flush({ success: true, message: '', result: {} });

    service.updateRoute(ID, { name: 'High value', displayOrder: 40, steps: null }).subscribe();
    const update = http.expectOne(`${BASE}/${ID}`);
    expect(update.request.method).toBe('PUT');
    expect(update.request.body.steps).toBeNull();
    update.flush({ success: true, message: '', result: {} });
  });

  it('uses PATCH for activate, deactivate, set-default and clear-default', () => {
    service.deactivateRoute(ID).subscribe();
    service.activateRoute(ID).subscribe();
    service.setDefault(ID).subscribe();
    service.clearDefault(ID).subscribe();
    for (const action of ['deactivate', 'activate', 'set-default', 'clear-default']) {
      const req = http.expectOne(`${BASE}/${ID}/${action}`);
      expect(req.request.method).toBe('PATCH');
      req.flush({ success: true, message: '', result: {} });
    }
  });

  it('deletes and bulk-assigns by category', () => {
    service.deleteRoute(ID).subscribe();
    const del = http.expectOne(`${BASE}/${ID}`);
    expect(del.request.method).toBe('DELETE');
    del.flush({ success: true, message: '', result: null });

    service.assignByCategory(ID, { categoryId: 15 }).subscribe(res => expect(res.result.updated).toBe(2));
    const assign = http.expectOne(`${BASE}/${ID}/assign-by-category`);
    expect(assign.request.method).toBe('POST');
    expect(assign.request.body).toEqual({ categoryId: 15 });
    assign.flush({ success: true, message: '', result: { updated: 2, skipped: 1, total: 3 } });
  });
});

describe('route step helpers', () => {
  it('words a route the way every screen shows it', () =>
    expect(routeStepsText(['PICK', 'PACK', 'GOODS_ISSUE', 'SHIP'])).toBe('Pick → Pack → Goods Issue → Ship'));

  it('accepts the three seeded routes and a full custom chain', () => {
    expect(validateRouteSteps(steps('PICK', 'GOODS_ISSUE'))).toBeNull();
    expect(validateRouteSteps(steps('PICK', 'GOODS_ISSUE', 'SHIP'))).toBeNull();
    expect(validateRouteSteps(steps('PICK', 'PACK', 'GOODS_ISSUE', 'SHIP'))).toBeNull();
    expect(validateRouteSteps(steps('PICK', 'PACK', 'STAGE', 'APPROVAL', 'GOODS_ISSUE', 'SHIP'))).toBeNull();
  });

  it('explains the server rules before a save', () => {
    expect(validateRouteSteps(steps('GOODS_ISSUE'))).toContain('PICK');               // T-C1-03
    expect(validateRouteSteps(steps('PICK', 'SHIP'))).toContain('GOODS_ISSUE');        // T-C1-04
    expect(validateRouteSteps(steps('PICK', 'SHIP', 'GOODS_ISSUE'))).toContain('before SHIP'); // T-C1-05
    expect(validateRouteSteps(steps('PACK', 'PICK', 'GOODS_ISSUE'))).toContain('first');
    expect(validateRouteSteps(steps('PICK', 'STAGE', 'PACK', 'GOODS_ISSUE'))).toContain('must follow the order');
    expect(validateRouteSteps([{ stepCode: 'PICK', stepOrder: 1, isMandatory: true },
                               { stepCode: 'GOODS_ISSUE', stepOrder: 3, isMandatory: true }])).toContain('no gaps');
    expect(validateRouteSteps(steps('PICK', 'PICK', 'GOODS_ISSUE'))).toContain('more than once');
  });
});
