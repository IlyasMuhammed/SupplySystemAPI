import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import {
  FulfillmentRouteModel, FulfillmentRoutesService, FulfillmentRouteStepRequest, MANUFACTURE_ROUTE_NOTE, RESERVED_CATEGORY_TOOLTIP,
  routeCategoryLabel, routeCategoryOf, routeCategoryOptions, routeCategorySeverity, routesForVariant, routesVisibleToOrg,
  routeStepsText, validateRouteSteps
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

  it('filters the list by route category with ?category= (A34-PA-05)', () => {
    service.getRoutes(false, 'MANUFACTURE').subscribe();
    const req = http.expectOne(r => r.url === BASE);
    expect(req.request.params.get('category')).toBe('MANUFACTURE');
    expect(req.request.params.has('includeInactive')).toBeFalse();
    req.flush({ success: true, message: '', result: [] });
  });

  it('sends the category on create, and on update only when given (null = unchanged)', () => {
    service.createRoute({ code: 'MTO', name: 'Make to order', routeCategory: 'MANUFACTURE', steps: steps('PICK', 'GOODS_ISSUE') }).subscribe();
    const create = http.expectOne(BASE);
    expect(create.request.body.routeCategory).toBe('MANUFACTURE');
    create.flush({ success: true, message: '', result: {} });

    service.updateRoute(ID, { name: 'Make to order', displayOrder: 40, steps: null, routeCategory: null }).subscribe();
    const update = http.expectOne(`${BASE}/${ID}`);
    expect(update.request.body.routeCategory).toBeNull();
    update.flush({ success: true, message: '', result: {} });
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

describe('route category helpers (A34 C1)', () => {
  const r = (code: string, routeCategory?: FulfillmentRouteModel['routeCategory']): FulfillmentRouteModel => ({
    uuid: 'r-' + code, code, name: code, description: null, isDefault: false, isActive: true, isSystem: false,
    requiresPacking: false, requiresShipping: true, displayOrder: 10, steps: [], stepsText: '', statusPath: [], createdDate: '',
    routeCategory
  });
  const routes = [r('PICK_AND_SHIP', 'STOCK'), r('LEGACY'), r('MFG_PICK_SHIP', 'MANUFACTURE')];

  it('reads a route with no category as STOCK (routes saved before A34)', () => {
    expect(routeCategoryOf(r('LEGACY'))).toBe('STOCK');
    expect(routeCategoryOf(r('X', 'MANUFACTURE'))).toBe('MANUFACTURE');
    expect(routeCategoryOf(null)).toBe('STOCK');
  });

  it('labels and colours the categories: STOCK green, MANUFACTURE orange', () => {
    expect(routeCategoryLabel('STOCK')).toBe('Stock');
    expect(routeCategoryLabel('MANUFACTURE')).toBe('Manufacture');
    expect(routeCategoryLabel('DROPSHIP')).toBe('Drop ship');
    expect(routeCategorySeverity('STOCK')).toBe('success');
    expect(routeCategorySeverity('MANUFACTURE')).toBe('warn');
    expect(routeCategorySeverity('BUY')).toBe('secondary');
  });

  it('offers STOCK and MANUFACTURE; BUY and DROPSHIP are shown disabled as reserved (D-7)', () => {
    const options = routeCategoryOptions(true);
    expect(options.map(o => o.value)).toEqual(['STOCK', 'MANUFACTURE', 'BUY', 'DROPSHIP']);
    expect(options.filter(o => !o.disabled).map(o => o.value)).toEqual(['STOCK', 'MANUFACTURE']);
    expect(options.filter(o => o.disabled).every(o => o.tooltip === RESERVED_CATEGORY_TOOLTIP)).toBeTrue();
    expect(RESERVED_CATEGORY_TOOLTIP).toBe('Reserved for future release');
  });

  it('leaves MANUFACTURE out without MODULE_MANUFACTURING, unless the route already has it (D-9)', () => {
    expect(routeCategoryOptions(false).map(o => o.value)).toEqual(['STOCK', 'BUY', 'DROPSHIP']);
    expect(routeCategoryOptions(false, 'MANUFACTURE').map(o => o.value)).toContain('MANUFACTURE');
  });

  it('carries the §3.6 note for MANUFACTURE routes', () => {
    expect(MANUFACTURE_ROUTE_NOTE).toBe('Products with this route will trigger a Production Order at Sale Order confirmation. ' +
      'Delivery is created after production completes.');
  });

  it('hides MANUFACTURE routes from an organization without MODULE_MANUFACTURING (D-9)', () => {
    expect(routesVisibleToOrg(routes, true).map(x => x.code)).toEqual(['PICK_AND_SHIP', 'LEGACY', 'MFG_PICK_SHIP']);
    expect(routesVisibleToOrg(routes, false).map(x => x.code)).toEqual(['PICK_AND_SHIP', 'LEGACY']);
  });

  it('offers MANUFACTURE routes to a variant only when its product is manufactured and the org manufactures (D-3, D-9)', () => {
    expect(routesForVariant(routes, true, true).map(x => x.code)).toEqual(['PICK_AND_SHIP', 'LEGACY', 'MFG_PICK_SHIP']);
    expect(routesForVariant(routes, false, true).map(x => x.code)).toEqual(['PICK_AND_SHIP', 'LEGACY']);
    expect(routesForVariant(routes, true, false).map(x => x.code)).toEqual(['PICK_AND_SHIP', 'LEGACY']);
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
