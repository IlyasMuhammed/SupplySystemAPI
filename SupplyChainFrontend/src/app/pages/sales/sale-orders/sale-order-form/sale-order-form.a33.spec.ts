import { signal } from '@angular/core';
import { ComponentFixture, TestBed, fakeAsync, flush, tick } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { SaleOrderFormComponent, PREVIEW_DEBOUNCE_MS } from './sale-order-form.component';
import {
  SaleOrderService, SaleOrderModel, SaleOrderLineModel, CreateSaleOrderRequest, UpdateSaleOrderRequest,
  SaleOrderDeliveryPreviewModel, SaleOrderDeliveryPreviewRequest, DeliveryPreviewLineModel
} from '../../../../services/sale-order.service';
import { FulfillmentRoutesService, FulfillmentRouteModel } from '../../../../services/fulfillment-routes.service';
import { SalesPreorderService } from '../../../../services/sales-preorder.service';
import { BusinessPartnerService, BusinessPartnerModel } from '../../../../services/business-partner.service';
import { InventoryService } from '../../../../services/inventory.service';
import { PricingRuleService } from '../../../../services/pricing-rule.service';
import { AddressService } from '../../../../services/address.service';
import { CurrenciesService } from '../../../../services/currencies.service';
import { FinanceSetupService } from '../../../../services/finance-setup.service';
import { AddressModel } from '../../../../services/logistics.service';
import { AuthService } from '../../../service/auth.service';
import { CurrentTenant, TenantService } from '../../../service/tenant.service';

// A33 PC-07 / PC-08 — the form's Route field per line (override or inherit, with where the route comes from) and the
// live delivery preview, both from POST /api/sale-orders/delivery-preview (API-CONTRACT.md §5). No Route field and no
// preview call for an organization without the Logistics module (D-11).

const ORDER_UUID = '11111111-1111-1111-1111-111111111111';
const CUSTOMER: BusinessPartnerModel = { uuid: 'cust-1', companyName: 'GlobalTech Co' } as BusinessPartnerModel;

function ok<T>(result: T, message = '') {
  return of({ success: true, message, result } as any);
}

function route(uuid: string, code: string, name: string, requiresShipping = false): FulfillmentRouteModel {
  return {
    uuid, code, name, isDefault: false, isActive: true, isSystem: true, requiresPacking: false, requiresShipping,
    displayOrder: 10, steps: [], stepsText: '', statusPath: [], createdDate: '2026-10-03T00:00:00Z'
  };
}

const ROUTES = [
  route('r-po', 'PICK_ONLY', 'Pick Only'),
  route('r-ps', 'PICK_AND_SHIP', 'Pick & Ship', true),
  route('r-pps', 'PICK_PACK_SHIP', 'Pick, Pack & Ship', true)
];

function pLine(overrides: Partial<DeliveryPreviewLineModel> = {}): DeliveryPreviewLineModel {
  return {
    lineNumber: 1, variantUuid: 'v1', itemDescription: 'Cable 4mm', quantity: 10,
    effectiveRouteUuid: 'r-po', effectiveRouteCode: 'PICK_ONLY', effectiveRouteName: 'Pick Only',
    effectiveRouteSteps: ['PICK', 'GOODS_ISSUE'], routeSource: 'VARIANT', routeBlocker: null,
    ...overrides
  };
}

function preview(overrides: Partial<SaleOrderDeliveryPreviewModel> = {}): SaleOrderDeliveryPreviewModel {
  return {
    routesEnabled: true, canConfirm: true, deliveryCount: 1,
    lines: [pLine()],
    groups: [{
      routeUuid: 'r-po', routeCode: 'PICK_ONLY', routeName: 'Pick Only', steps: ['PICK', 'GOODS_ISSUE'],
      stepsText: 'Pick → Goods Issue', requiresShipping: false, deliveryMode: 'SELF_PICKUP', lineNumbers: [1]
    }],
    blockers: [],
    ...overrides
  };
}

const UNROUTABLE = preview({
  canConfirm: false, deliveryCount: 0, groups: [],
  lines: [pLine({ effectiveRouteUuid: null, effectiveRouteCode: null, effectiveRouteName: null, effectiveRouteSteps: [],
                  routeSource: 'NONE', routeBlocker: 'ROUTE_MISSING' })],
  blockers: [{ lineNumber: 1, code: 'ROUTE_MISSING', message: 'Cannot confirm: lines 1 have no fulfillment route.' }]
});

function soLine(overrides: Partial<SaleOrderLineModel> = {}): SaleOrderLineModel {
  return {
    uuid: 'l1', variantUuid: 'v1', itemDescription: 'Cable 4mm', quantity: 10, unitPrice: 40, discountPercent: 0,
    taxPercent: 0, lineTotal: 400, fulfilledQty: 0, invoicedQty: 0, status: 'OPEN',
    ...overrides
  };
}

function order(overrides: Partial<SaleOrderModel> = {}): SaleOrderModel {
  return {
    uuid: ORDER_UUID, traceId: 't-1', soNumber: 'SO-2026-00042', partnerId: 'cust-1',
    orderDate: '2026-09-01T00:00:00Z', currencyId: 'cur-pkr',
    subtotal: 0, taxAmount: 0, discountAmount: 0, grandTotal: 0,
    status: 'DRAFT', requiresShipment: false, deliveryMode: 'SELF_PICKUP', createdDate: '2026-09-01T00:00:00Z',
    lines: [soLine()], routesEnabled: true, confirmBlockers: [],
    ...overrides
  };
}

describe('SaleOrderFormComponent — A33 route per line and delivery preview', () => {
  let fixture: ComponentFixture<SaleOrderFormComponent>;
  let component: SaleOrderFormComponent;
  let sales: jasmine.SpyObj<SaleOrderService>;
  let routes: jasmine.SpyObj<FulfillmentRoutesService>;
  let permissions: string[];
  let features: string[];

  const auth = { hasPermission: (code: string) => permissions.includes(code) } as unknown as AuthService;

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  function queryAll(testId: string): HTMLElement[] {
    return Array.from(fixture.nativeElement.querySelectorAll(`[data-testid="${testId}"]`));
  }

  function lastPreviewRequest(): SaleOrderDeliveryPreviewRequest {
    return sales.previewDeliveries.calls.mostRecent().args[0];
  }

  function setup(opts: { uuid?: string; model?: SaleOrderModel } = {}) {
    sales = jasmine.createSpyObj<SaleOrderService>('SaleOrderService',
      ['getSaleOrderById', 'createSaleOrder', 'updateSaleOrder', 'getDefaults', 'checkCustomerPo', 'previewDeliveries']);
    sales.getDefaults.and.returnValue(ok({ deliveryMode: 'SELF_PICKUP', selfPickupEnabled: true }));
    sales.getSaleOrderById.and.returnValue(ok(opts.model ?? order()));
    sales.createSaleOrder.and.returnValue(ok('new-order-uuid'));
    sales.updateSaleOrder.and.returnValue(ok(null));
    sales.checkCustomerPo.and.returnValue(ok([]));
    sales.previewDeliveries.and.returnValue(ok(preview()));

    routes = jasmine.createSpyObj<FulfillmentRoutesService>('FulfillmentRoutesService', ['getRoutes']);
    routes.getRoutes.and.returnValue(ok(ROUTES));

    const preorder = jasmine.createSpyObj<SalesPreorderService>('SalesPreorderService',
      ['getQuotations', 'getQuotation', 'convertQuotationToOrder']);
    const partners = jasmine.createSpyObj<BusinessPartnerService>('BusinessPartnerService', ['getPartners', 'getPartnerById']);
    partners.getPartners.and.returnValue(ok({ data: [CUSTOMER], totalRecords: 1, page: 1, pageSize: 20, totalPages: 1 }));
    partners.getPartnerById.and.returnValue(ok(CUSTOMER));
    const inventory = jasmine.createSpyObj<InventoryService>('InventoryService', ['getProducts', 'getProductById']);
    inventory.getProducts.and.returnValue(ok({ data: [], totalRecords: 0, page: 1, pageSize: 500, totalPages: 1 }));
    const pricing = jasmine.createSpyObj<PricingRuleService>('PricingRuleService', ['resolvePrice']);
    pricing.resolvePrice.and.returnValue(ok({ found: true, unitPrice: 40 }));
    const addresses = jasmine.createSpyObj<AddressService>('AddressService', ['getAddresses', 'getAddress', 'createAddress']);
    const addr = { uuid: 'addr-1', line1: 'Plot 12', cityName: 'Karachi', countryName: 'Pakistan' } as AddressModel;
    addresses.getAddresses.and.returnValue(ok([addr]));
    addresses.getAddress.and.returnValue(ok(addr));
    const currencies = jasmine.createSpyObj<CurrenciesService>('CurrenciesService', ['getAll']);
    currencies.getAll.and.returnValue(ok([{ id: 'cur-pkr', name: 'Pakistani Rupee', code: 'PKR' }]));
    const setupSvc = jasmine.createSpyObj<FinanceSetupService>('FinanceSetupService', ['getTaxCodes', 'quoteExchangeRate']);
    setupSvc.getTaxCodes.and.returnValue(ok([]));
    setupSvc.quoteExchangeRate.and.returnValue(ok(null));
    const tenant = signal<CurrentTenant | null>({
      id: 'org-1', orgCode: 'SCM', orgName: 'SCM', plan: 'BASIC', baseCurrency: 'cur-pkr',
      enabledFeatureCodes: features, isSuperAdmin: false, roleName: 'Sales', permissions: []
    });

    TestBed.resetTestingModule().configureTestingModule({
      imports: [SaleOrderFormComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), MessageService,
        { provide: SaleOrderService, useValue: sales },
        { provide: FulfillmentRoutesService, useValue: routes },
        { provide: SalesPreorderService, useValue: preorder },
        { provide: BusinessPartnerService, useValue: partners },
        { provide: InventoryService, useValue: inventory },
        { provide: PricingRuleService, useValue: pricing },
        { provide: AddressService, useValue: addresses },
        { provide: CurrenciesService, useValue: currencies },
        { provide: FinanceSetupService, useValue: setupSvc },
        { provide: TenantService, useValue: { tenant } },
        { provide: AuthService, useValue: auth },
        { provide: ActivatedRoute, useValue: { snapshot: {
          paramMap: new Map(opts.uuid ? [['uuid', opts.uuid]] : []),
          queryParamMap: convertToParamMap({})
        } } }
      ]
    });

    fixture = TestBed.createComponent(SaleOrderFormComponent);
    component = fixture.componentInstance;
    spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    fixture.detectChanges();
  }

  /** Picks the customer and an item, and types a quantity, on line `i`. */
  function fillLine(i = 0, variantUuid = 'v1', quantity = 10) {
    if (!component.customer) {
      component.form.get('customer')!.setValue(CUSTOMER);
      component.onCustomerSelected(CUSTOMER);
    }
    component.onLineVariantSelected(i, {
      productUuid: 'p1', productName: 'Cable', variantId: 1, variantUuid, variantSku: 'CAB', variantName: '4mm',
      purchasePrice: 30, uomCode: 'M'
    });
    component.lineControl(i, 'quantity').setValue(quantity);
  }

  /** Lets the debounce run out and the page redraw. */
  function settle() {
    tick(PREVIEW_DEBOUNCE_MS);
    fixture.detectChanges();
  }

  beforeEach(() => {
    permissions = ['SALE_ORDER_VIEW', 'SALE_ORDER_CREATE', 'SALE_ORDER_EDIT', 'INVENTORY_VIEW'];
    features = ['MODULE_DEMAND', 'MODULE_LOGISTICS'];
  });

  // ── D-11 ───────────────────────────────────────────────────────────────────

  it('shows no Route field and asks for no preview when the organization has no Logistics module (D-11)', fakeAsync(() => {
    features = ['MODULE_DEMAND'];
    setup();
    fillLine();
    settle();

    expect(component.routesEnabled).toBeFalse();
    expect(query('route-field')).toBeNull();
    expect(query('delivery-preview')).toBeNull();
    expect(sales.previewDeliveries).not.toHaveBeenCalled();
    expect(routes.getRoutes).not.toHaveBeenCalled();
    flush();
  }));

  it('hides the Route field again when the server says routes are off for the organization', fakeAsync(() => {
    setup();
    sales.previewDeliveries.and.returnValue(ok(preview({ routesEnabled: false, lines: [], groups: [], deliveryCount: 0 })));
    fillLine();
    settle();

    expect(component.routesEnabled).toBeFalse();
    expect(query('route-field')).toBeNull();
    expect(query('delivery-preview')).toBeNull();
    flush();
  }));

  // ── PC-07: the Route field ─────────────────────────────────────────────────

  it('offers the active routes on each line, and an empty choice that inherits', fakeAsync(() => {
    setup();
    fillLine();
    settle();

    expect(routes.getRoutes).toHaveBeenCalledTimes(1);
    expect(query('route-field')).not.toBeNull();
    expect(component.routeOptionsFor(0).map(o => o.label)).toEqual(['Pick Only', 'Pick & Ship', 'Pick, Pack & Ship']);
    expect(component.lineControl(0, 'fulfillmentRouteUuid').value).toBeNull();
    flush();
  }));

  it('shows the inherited route, marked ⓥ, with where it comes from', fakeAsync(() => {
    setup();
    fillLine();
    settle();

    expect(component.inheritedRouteLabel(0)).toContain('Pick Only');
    const source = query('route-source')!;
    expect(source.textContent!.trim()).toBe('ⓥ');
    expect(source.getAttribute('data-source')).toBe('VARIANT');
    expect(query('route-hint')!.textContent).toContain('variant');
    expect(query('route-legend')!.textContent).toContain('ⓥ');
    expect(query('route-legend')!.textContent).toContain('✎');
    expect(query('route-legend')!.textContent).toContain('⊙');
    expect(query('route-legend')!.textContent).toContain('⚠');
    flush();
  }));

  it('marks an organization default ⊙', fakeAsync(() => {
    setup();
    sales.previewDeliveries.and.returnValue(ok(preview({ lines: [pLine({ routeSource: 'ORG_DEFAULT', effectiveRouteName: 'Pick & Ship' })] })));
    fillLine();
    settle();

    expect(query('route-source')!.textContent!.trim()).toBe('⊙');
    expect(component.inheritedRouteLabel(0)).toContain('Pick & Ship');
    flush();
  }));

  it('flags a line with no route ⚠ as blocking, with the placeholder asking for one', fakeAsync(() => {
    setup();
    sales.previewDeliveries.and.returnValue(ok(UNROUTABLE));
    fillLine();
    settle();

    expect(query('route-source')!.textContent!.trim()).toBe('⚠');
    expect(query('route-hint')!.classList).toContain('blocking');
    expect(query('route-hint')!.textContent).toContain('cannot be confirmed');
    expect(component.inheritedRouteLabel(0)).toContain('Select route');
    flush();
  }));

  it('marks an override ✎ at once, and sends it in the preview and when saving', fakeAsync(() => {
    setup();
    fillLine();
    settle();
    sales.previewDeliveries.and.returnValue(ok(preview({ lines: [pLine({ routeSource: 'LINE_OVERRIDE', effectiveRouteUuid: 'r-pps', effectiveRouteName: 'Pick, Pack & Ship' })] })));

    component.lineControl(0, 'fulfillmentRouteUuid').setValue('r-pps');
    fixture.detectChanges();
    expect(query('route-source')!.textContent!.trim()).withContext('before the server answers').toBe('✎');

    settle();
    expect(lastPreviewRequest().lines).toEqual([{ variantUuid: 'v1', quantity: 10, fulfillmentRouteUuid: 'r-pps' }]);
    expect((component.buildRequest() as CreateSaleOrderRequest).lines[0].fulfillmentRouteUuid).toBe('r-pps');
    flush();
  }));

  it('leaves the route out of the saved line when it inherits, so the server resolves it', fakeAsync(() => {
    setup();
    fillLine();
    component.lineControl(0, 'fulfillmentRouteUuid').setValue('r-pps');
    component.lineControl(0, 'fulfillmentRouteUuid').setValue(null);
    settle();

    expect('fulfillmentRouteUuid' in (component.buildRequest() as CreateSaleOrderRequest).lines[0]).toBeFalse();
    flush();
  }));

  it('sends the route even when the field is read-only, so saving never drops an override', fakeAsync(() => {
    permissions = ['SALE_ORDER_VIEW', 'INVENTORY_VIEW'];
    setup({ uuid: ORDER_UUID, model: order({ lines: [soLine({ fulfillmentRouteUuid: 'r-pps', routeSource: 'LINE_OVERRIDE' })] }) });
    settle();

    expect(component.canChangeRoute).toBeFalse();
    expect(component.lineControl(0, 'fulfillmentRouteUuid').disabled).toBeTrue();
    expect((component.buildRequest() as UpdateSaleOrderRequest).lines[0].fulfillmentRouteUuid).toBe('r-pps');
    flush();
  }));

  it('lets a creator choose routes on a new order, and an editor on a draft (CREATE / EDIT, contract §2)', fakeAsync(() => {
    permissions = ['SALE_ORDER_CREATE', 'INVENTORY_VIEW'];
    setup();
    expect(component.canChangeRoute).toBeTrue();
    flush();

    permissions = ['SALE_ORDER_CREATE', 'INVENTORY_VIEW'];
    setup({ uuid: ORDER_UUID });
    settle();
    expect(component.canChangeRoute).withContext('changing a saved draft is an edit').toBeFalse();
    flush();
  }));

  it('loads a draft with its own override, and keeps a deactivated one on show', fakeAsync(() => {
    setup({ uuid: ORDER_UUID, model: order({ lines: [
      soLine({ fulfillmentRouteUuid: 'r-old', effectiveRouteUuid: 'r-old', effectiveRouteName: 'Old route',
               routeSource: 'LINE_OVERRIDE', routeBlocker: 'ROUTE_INACTIVE' })
    ] }) });
    settle();

    expect(component.lineControl(0, 'fulfillmentRouteUuid').value).toBe('r-old');
    const options = component.routeOptionsFor(0);
    expect(options.map(o => o.value)).toContain('r-old');
    expect(options.find(o => o.value === 'r-old')!.label).toContain('inactive');
    flush();
  }));

  // ── PC-08: the live preview ────────────────────────────────────────────────

  it('previews an unsaved order through the POST, with the mode, address and complete lines', fakeAsync(() => {
    setup();
    fillLine();
    settle();

    expect(sales.previewDeliveries).toHaveBeenCalledTimes(1);
    const req = lastPreviewRequest();
    expect(req.saleOrderUuid ?? null).toBeNull();
    expect(req.deliveryMode).toBe('SELF_PICKUP');
    expect(req.lines).toEqual([{ variantUuid: 'v1', quantity: 10, fulfillmentRouteUuid: null }]);
    flush();
  }));

  it('previews a draft being edited against the saved order (saleOrderUuid)', fakeAsync(() => {
    setup({ uuid: ORDER_UUID });
    settle();

    expect(sales.previewDeliveries).toHaveBeenCalled();
    expect(lastPreviewRequest().saleOrderUuid).toBe(ORDER_UUID);
    flush();
  }));

  it('asks once for a burst of changes (debounced), with the last of them', fakeAsync(() => {
    setup();
    fillLine();
    component.lineControl(0, 'quantity').setValue(11);
    tick(PREVIEW_DEBOUNCE_MS / 2);
    component.lineControl(0, 'quantity').setValue(12);
    settle();

    expect(sales.previewDeliveries).toHaveBeenCalledTimes(1);
    expect(lastPreviewRequest().lines[0].quantity).toBe(12);
    flush();
  }));

  it('does not ask again for a change that does not touch the preview (a price arriving)', fakeAsync(() => {
    setup();
    fillLine();
    settle();
    const calls = sales.previewDeliveries.calls.count();

    component.lineControl(0, 'discountPercent').setValue(5);
    settle();

    expect(sales.previewDeliveries.calls.count()).toBe(calls);
    flush();
  }));

  it('asks again when the delivery mode changes, since the default route depends on it (D-4)', fakeAsync(() => {
    setup();
    fillLine();
    settle();

    component.form.patchValue({ deliveryMode: 'SHIP', shippingAddressId: 'addr-1' });
    component.onModeChange();
    component.form.patchValue({ shippingAddressId: 'addr-1' });
    settle();

    expect(lastPreviewRequest().deliveryMode).toBe('SHIP');
    expect(lastPreviewRequest().shippingAddressId).toBe('addr-1');
    flush();
  }));

  it('asks nothing while no line has an item and a quantity, and leaves incomplete lines out', fakeAsync(() => {
    setup();
    settle();
    expect(sales.previewDeliveries).not.toHaveBeenCalled();
    expect(query('delivery-preview-headline')).toBeNull();

    component.addLine();
    fillLine(1, 'v2', 5);
    settle();
    expect(lastPreviewRequest().lines).toEqual([{ variantUuid: 'v2', quantity: 5, fulfillmentRouteUuid: null }]);
    flush();
  }));

  it('maps the server line numbers back to the form lines when an incomplete line is left out', fakeAsync(() => {
    setup();
    component.addLine();
    sales.previewDeliveries.and.returnValue(ok(UNROUTABLE));
    fillLine(1, 'v2', 5);
    settle();

    expect(component.lineRouteSource(0)).withContext('line 1 has no item yet').toBeNull();
    expect(component.lineRouteSource(1)).toBe('NONE');
    expect(query('delivery-preview-unroutable')!.textContent).toContain('Line 2');
    flush();
  }));

  it('says how many delivery orders confirming will create, grouped by route, and which lines cannot go', fakeAsync(() => {
    setup();
    component.addLine();
    sales.previewDeliveries.and.returnValue(ok(preview({
      canConfirm: false, deliveryCount: 1,
      lines: [pLine(), pLine({ lineNumber: 2, variantUuid: 'v2', itemDescription: 'Gasket', routeSource: 'NONE',
                               routeBlocker: 'ROUTE_MISSING', effectiveRouteName: null, effectiveRouteUuid: null })],
      blockers: [{ lineNumber: 2, code: 'ROUTE_MISSING', message: 'Cannot confirm: lines 2 have no fulfillment route.' }]
    })));
    fillLine(0);
    fillLine(1, 'v2', 5);
    settle();

    expect(query('delivery-preview-headline')!.textContent).toContain('On confirmation, 1 delivery order will be created');
    const groups = queryAll('delivery-preview-group');
    expect(groups.length).toBe(1);
    expect(groups[0].textContent).toContain('Pick Only');
    expect(groups[0].textContent).toContain('Pick → Goods Issue');
    expect(query('delivery-preview-unroutable')!.textContent).toContain('Line 2');
    flush();
  }));

  it('collapses and opens again', fakeAsync(() => {
    setup();
    fillLine();
    settle();
    expect(query('delivery-preview-body')).not.toBeNull();

    query('delivery-preview-toggle')!.click();
    fixture.detectChanges();
    expect(query('delivery-preview-body')).toBeNull();
    expect(query('delivery-preview')).withContext('still there, folded').not.toBeNull();

    query('delivery-preview-toggle')!.click();
    fixture.detectChanges();
    expect(query('delivery-preview-body')).not.toBeNull();
    flush();
  }));

  // ── REV-03 / D-4: a line routed to Ship needs an address, whatever the header mode ──

  const SHIPS = preview({
    canConfirm: false,
    lines: [pLine({ effectiveRouteUuid: 'r-pps', effectiveRouteCode: 'PICK_PACK_SHIP', effectiveRouteName: 'Pick, Pack & Ship',
                    effectiveRouteSteps: ['PICK', 'PACK', 'GOODS_ISSUE', 'SHIP'] })],
    groups: [{ routeUuid: 'r-pps', routeCode: 'PICK_PACK_SHIP', routeName: 'Pick, Pack & Ship', steps: ['PICK', 'PACK', 'GOODS_ISSUE', 'SHIP'],
               stepsText: 'Pick → Pack → Goods Issue → Ship', requiresShipping: true, deliveryMode: 'SHIP', lineNumbers: [1] }],
    blockers: [{ code: 'SHIPPING_ADDRESS_REQUIRED', message: 'Line 1 ships, so the order needs a shipping address.' }]
  });

  it('offers the shipping address on a collected order when a line routes to Ship, and sends it (D-4)', fakeAsync(() => {
    setup();
    sales.previewDeliveries.and.returnValue(ok(SHIPS));
    fillLine();
    settle();

    expect(component.isShip).toBeFalse();
    expect(query('address-field')).not.toBeNull();
    expect(query('address-route-hint')).not.toBeNull();
    expect(component.form.get('shippingAddressId')!.valid).withContext('a draft may still be saved without it').toBeTrue();

    component.form.patchValue({ shippingAddressId: 'addr-1' });
    settle();
    expect(lastPreviewRequest().shippingAddressId).toBe('addr-1');
    expect((component.buildRequest() as CreateSaleOrderRequest).shippingAddressId).toBe('addr-1');
    flush();
  }));

  it('keeps a collected draft\'s stored address when a line routes to Ship, and sends it back', fakeAsync(() => {
    setup({ uuid: ORDER_UUID, model: order({ deliveryMode: 'SELF_PICKUP', shippingAddressId: 'addr-1', lines: [soLine({
      effectiveRouteUuid: 'r-pps', effectiveRouteName: 'Pick, Pack & Ship', effectiveRouteSteps: ['PICK', 'PACK', 'GOODS_ISSUE', 'SHIP'],
      routeSource: 'VARIANT'
    })] }) });
    sales.previewDeliveries.and.returnValue(ok(SHIPS));
    settle();

    expect(component.form.get('shippingAddressId')!.value).toBe('addr-1');
    expect(query('address-field')).not.toBeNull();
    expect(lastPreviewRequest().shippingAddressId).toBe('addr-1');
    expect((component.buildRequest() as UpdateSaleOrderRequest).shippingAddressId).toBe('addr-1');
    flush();
  }));

  it('does not clear the address on switching to collection while a line still routes to Ship', fakeAsync(() => {
    setup();
    sales.previewDeliveries.and.returnValue(ok(SHIPS));
    fillLine();                                   // picking the customer clears the address, so it comes first
    component.form.patchValue({ deliveryMode: 'SHIP' });
    component.onModeChange();
    component.form.patchValue({ shippingAddressId: 'addr-1' });
    settle();

    component.form.patchValue({ deliveryMode: 'SELF_PICKUP' });
    component.onModeChange();

    expect(component.form.get('shippingAddressId')!.value).toBe('addr-1');
    flush();
  }));

  it('sends the address when converting a quotation by the same rule as saving: shipped, or a line ships (D-4)', fakeAsync(() => {
    permissions.push('SALE_QUOTATION_VIEW');
    setup();
    component.setCreateMode('QUOTATION');
    component.form.patchValue({ deliveryMode: 'SELF_PICKUP', shippingAddressId: 'addr-1' });

    expect(component.buildConvertRequest().shippingAddressId).withContext('collected, nothing ships').toBeUndefined();

    spyOnProperty(component, 'routesNeedShipping', 'get').and.returnValue(true);
    expect(component.showAddress).toBeTrue();
    expect(component.buildConvertRequest().shippingAddressId).withContext('collected, a line ships').toBe('addr-1');
    flush();
  }));

  it('still clears and hides the address on a collected order when nothing ships', fakeAsync(() => {
    setup();
    fillLine();
    component.form.patchValue({ deliveryMode: 'SHIP' });
    component.onModeChange();
    component.form.patchValue({ shippingAddressId: 'addr-1' });
    settle();
    expect(component.form.get('shippingAddressId')!.value).withContext('set while shipping').toBe('addr-1');

    component.form.patchValue({ deliveryMode: 'SELF_PICKUP' });
    component.onModeChange();
    fixture.detectChanges();

    expect(component.form.get('shippingAddressId')!.value).toBeNull();
    expect(query('address-field')).toBeNull();
    flush();
  }));

  it('says so when the preview cannot be worked out, and the form still saves', fakeAsync(() => {
    setup();
    sales.previewDeliveries.and.returnValue(throwError(() => ({ status: 500 })));
    fillLine();
    settle();

    expect(component.previewFailed).toBeTrue();
    expect(query('delivery-preview-failed')).not.toBeNull();

    component.save();
    expect(sales.createSaleOrder).toHaveBeenCalled();
    flush();
  }));
});
