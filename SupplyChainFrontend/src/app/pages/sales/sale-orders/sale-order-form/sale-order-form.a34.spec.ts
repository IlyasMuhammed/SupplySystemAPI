import { signal } from '@angular/core';
import { ComponentFixture, TestBed, fakeAsync, flush, tick } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of } from 'rxjs';

import { SaleOrderFormComponent, PREVIEW_DEBOUNCE_MS } from './sale-order-form.component';
import {
  SaleOrderService, SaleOrderModel, SaleOrderLineModel, CreateSaleOrderRequest, UpdateSaleOrderRequest,
  SaleOrderDeliveryPreviewModel
} from '../../../../services/sale-order.service';
import { FulfillmentRoutesService, FulfillmentRouteModel } from '../../../../services/fulfillment-routes.service';
import { SalesPreorderService } from '../../../../services/sales-preorder.service';
import { BusinessPartnerService, BusinessPartnerModel } from '../../../../services/business-partner.service';
import { InventoryService } from '../../../../services/inventory.service';
import { PricingRuleService } from '../../../../services/pricing-rule.service';
import { AddressService } from '../../../../services/address.service';
import { CurrenciesService } from '../../../../services/currencies.service';
import { FinanceSetupService } from '../../../../services/finance-setup.service';
import { LeadTimeService, LeadTimeResultModel } from '../../../../services/lead-time.service';
import { AddressModel } from '../../../../services/logistics.service';
import { AuthService } from '../../../service/auth.service';
import { CurrentTenant, TenantService } from '../../../service/tenant.service';
import { LeadTimePopoverComponent } from '../../lead-time-popover/lead-time-popover.component';

// A34 PC-07/08/09 on the sale order form — the ⏱ per unsaved line calls POST api/lead-time/calculate (API-CONTRACT.md
// §4.6, §5.2), on demand only; the calculated and manual dates go back with every save, a draft's lines being rebuilt
// (C-14, contract §5.2 "Round-trips").

const ORDER_UUID = '33333333-3333-3333-3333-333333333333';
const CUSTOMER: BusinessPartnerModel = { uuid: 'cust-1', companyName: 'GlobalTech Co' } as BusinessPartnerModel;

const LEAD: LeadTimeResultModel = {
  totalLeadTimeDays: 13, earliestDeliveryDate: '2026-10-17T00:00:00', routeUuid: 'r-mfg', routeCode: 'MFG_PICK_SHIP',
  routeCategory: 'MANUFACTURE', calculatedAt: '2026-10-04T08:00:00Z',
  components: [{ code: 'MANUFACTURING', name: 'Manufacturing', days: 5, source: 'BOM' }]
};

function ok<T>(result: T, message = '') {
  return of({ success: true, message, result } as any);
}

function preview(): SaleOrderDeliveryPreviewModel {
  return {
    routesEnabled: true, canConfirm: true, deliveryCount: 0, groups: [], blockers: [],
    lines: [{ lineNumber: 1, variantUuid: 'v1', itemDescription: 'Gear', quantity: 10, effectiveRouteUuid: 'r-mfg',
              effectiveRouteCode: 'MFG_PICK_SHIP', effectiveRouteName: 'Manufacture → Pick & Ship',
              effectiveRouteSteps: ['PICK', 'GOODS_ISSUE', 'SHIP'], routeSource: 'VARIANT', routeBlocker: null,
              effectiveRouteCategory: 'MANUFACTURE' }],
    productionLines: []
  };
}

function soLine(overrides: Partial<SaleOrderLineModel> = {}): SaleOrderLineModel {
  return {
    uuid: 'l1', variantUuid: 'v1', itemDescription: 'Gear', quantity: 10, unitPrice: 40, discountPercent: 0,
    taxPercent: 0, lineTotal: 400, fulfilledQty: 0, invoicedQty: 0, status: 'OPEN',
    ...overrides
  };
}

function order(overrides: Partial<SaleOrderModel> = {}): SaleOrderModel {
  return {
    uuid: ORDER_UUID, traceId: 't-1', soNumber: 'SO-2026-00042', partnerId: 'cust-1',
    orderDate: '2026-09-01T00:00:00', expectedDeliveryDate: '2026-10-25T00:00:00', currencyId: 'cur-pkr',
    subtotal: 0, taxAmount: 0, discountAmount: 0, grandTotal: 0,
    status: 'DRAFT', requiresShipment: false, deliveryMode: 'SELF_PICKUP', createdDate: '2026-09-01T00:00:00',
    lines: [soLine()], routesEnabled: true, confirmBlockers: [],
    ...overrides
  };
}

describe('SaleOrderFormComponent — A34 lead time per line', () => {
  let fixture: ComponentFixture<SaleOrderFormComponent>;
  let component: SaleOrderFormComponent;
  let sales: jasmine.SpyObj<SaleOrderService>;
  let leadTimes: jasmine.SpyObj<LeadTimeService>;
  let permissions: string[];

  const auth = { hasPermission: (code: string) => permissions.includes(code) } as unknown as AuthService;

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  function click(testId: string) {
    const el = query(testId) as HTMLElement;
    expect(el).withContext(testId).not.toBeNull();
    (el.tagName === 'P-BUTTON' ? el.querySelector('button')! : el).click();
    fixture.detectChanges();
  }

  function popover(): LeadTimePopoverComponent {
    return fixture.debugElement.query(By.directive(LeadTimePopoverComponent)).componentInstance;
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

    leadTimes = jasmine.createSpyObj<LeadTimeService>('LeadTimeService', ['calculate']);
    leadTimes.calculate.and.returnValue(ok(LEAD));

    const routes = jasmine.createSpyObj<FulfillmentRoutesService>('FulfillmentRoutesService', ['getRoutes']);
    routes.getRoutes.and.returnValue(ok(routeList));
    const preorder = jasmine.createSpyObj<SalesPreorderService>('SalesPreorderService', ['getQuotations', 'getQuotation', 'convertQuotationToOrder']);
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
        { provide: LeadTimeService, useValue: leadTimes },
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

  function fillLine(i = 0, variantUuid = 'v1', quantity = 10) {
    if (!component.customer) {
      component.form.get('customer')!.setValue(CUSTOMER);
      component.onCustomerSelected(CUSTOMER);
    }
    component.onLineVariantSelected(i, {
      productUuid: 'p1', productName: 'Gear', variantId: 1, variantUuid, variantSku: 'GR', variantName: 'A', purchasePrice: 30, uomCode: 'EA'
    });
    component.lineControl(i, 'quantity').setValue(quantity);
  }

  function settle() {
    tick(PREVIEW_DEBOUNCE_MS);
    fixture.detectChanges();
  }

  let routeList: FulfillmentRouteModel[];
  let features: string[];

  beforeEach(() => {
    permissions = ['SALE_ORDER_VIEW', 'SALE_ORDER_CREATE', 'SALE_ORDER_EDIT', 'INVENTORY_VIEW'];
    routeList = [];
    features = ['MODULE_DEMAND', 'MODULE_LOGISTICS', 'MODULE_MANUFACTURING'];
  });

  it('calculates an unsaved line on demand only, with its variant, quantity, effective route and the expected date', fakeAsync(() => {
    setup();
    fillLine();
    component.form.get('expectedDeliveryDate')!.setValue(new Date(2026, 9, 25));
    settle();
    expect(leadTimes.calculate).not.toHaveBeenCalled();

    click('lt-calculate');

    expect(leadTimes.calculate).toHaveBeenCalledOnceWith({
      variantUuid: 'v1', quantity: 10, routeUuid: 'r-mfg', requestedDate: '2026-10-25'
    });
    expect(query('lt-total')!.textContent).toContain('Total: 13 days');
    fixture.detectChanges();
    expect(query('lt-indicator')!.textContent).toContain('⏱ Calculated');

    const line = (component.buildRequest() as CreateSaleOrderRequest).lines[0];
    expect(line.calculatedLeadTimeDays).toBe(13);
    expect(line.calculatedDeliveryDate).toBe('2026-10-17');
    expect(line.leadTimeCalculatedAt).toBe('2026-10-04T08:00:00Z');
    expect(line.manualDeliveryDate).toBeUndefined();
    flush();
  }));

  it('sends the line\'s own route override when it has one', fakeAsync(() => {
    setup();
    fillLine();
    component.lineControl(0, 'fulfillmentRouteUuid').setValue('r-own');
    settle();
    click('lt-calculate');
    expect(leadTimes.calculate.calls.mostRecent().args[0].routeUuid).toBe('r-own');
    flush();
  }));

  it('keeps an overridden date as the line\'s manual date, and Apply goes back to the calculated one', fakeAsync(() => {
    setup();
    fillLine();
    settle();
    click('lt-calculate');
    click('lt-override');
    popover().overrideValue = new Date(2026, 9, 30);
    fixture.detectChanges();
    click('lt-override-save');

    expect((component.buildRequest() as CreateSaleOrderRequest).lines[0].manualDeliveryDate).toBe('2026-10-30');
    expect(query('lt-indicator')!.textContent).toContain('✎ Manual');

    click('lt-calculate');
    click('lt-apply');
    expect((component.buildRequest() as CreateSaleOrderRequest).lines[0].manualDeliveryDate).toBeUndefined();
    flush();
  }));

  it('drops a calculation that no longer matches the line once its quantity changes', fakeAsync(() => {
    setup();
    fillLine();
    settle();
    click('lt-calculate');
    component.lineControl(0, 'quantity').setValue(25);
    settle();

    const line = (component.buildRequest() as CreateSaleOrderRequest).lines[0];
    expect(line.calculatedDeliveryDate).toBeUndefined();
    expect(query('lt-indicator')!.textContent).toContain('— Not calculated');
    flush();
  }));

  it('round-trips a loaded draft line\'s dates on save, so the rebuilt line keeps them (C-14)', fakeAsync(() => {
    setup({ uuid: ORDER_UUID, model: order({ lines: [soLine({
      manualDeliveryDate: '2026-10-30T00:00:00', calculatedLeadTimeDays: 13, calculatedDeliveryDate: '2026-10-17T00:00:00',
      leadTimeCalculatedAt: '2026-10-04T08:00:00Z'
    })] }) });
    settle();

    const line = (component.buildRequest() as UpdateSaleOrderRequest).lines[0];
    expect(line.manualDeliveryDate).toBe('2026-10-30');
    expect(line.calculatedLeadTimeDays).toBe(13);
    expect(line.calculatedDeliveryDate).toBe('2026-10-17');
    expect(line.leadTimeCalculatedAt).toBe('2026-10-04T08:00:00Z');
    expect(leadTimes.calculate).not.toHaveBeenCalled();
    flush();
  }));

  it('offers MANUFACTURE routes in the line\'s route picker only when the organization has manufacturing (D-9)', fakeAsync(() => {
    const r = (uuid: string, name: string, routeCategory: 'STOCK' | 'MANUFACTURE') => ({
      uuid, code: uuid, name, isDefault: false, isActive: true, isSystem: true, requiresPacking: false, requiresShipping: true,
      displayOrder: 10, steps: [], stepsText: '', statusPath: [], createdDate: '2026-10-03T00:00:00Z', routeCategory
    } as FulfillmentRouteModel);
    routeList = [r('r-ps', 'Pick & Ship', 'STOCK'), r('r-mfg', 'Manufacture → Pick & Ship', 'MANUFACTURE')];

    setup();
    fillLine();
    settle();
    expect(component.routeOptionsFor(0).map(o => o.label)).toEqual(['Pick & Ship', 'Manufacture → Pick & Ship']);
    flush();

    features = ['MODULE_DEMAND', 'MODULE_LOGISTICS'];
    setup();
    fillLine();
    settle();
    expect(component.routeOptionsFor(0).map(o => o.label)).toEqual(['Pick & Ship']);
    flush();
  }));

  it('offers no ⏱ without the right to create (new) or edit (existing) the order', fakeAsync(() => {
    permissions = ['SALE_ORDER_VIEW', 'SALE_ORDER_EDIT', 'INVENTORY_VIEW'];
    setup();
    fillLine();
    settle();
    expect(query('lt-calculate')).toBeNull();
    expect(query('lt-set-date')).toBeNull();
    flush();
  }));
});
