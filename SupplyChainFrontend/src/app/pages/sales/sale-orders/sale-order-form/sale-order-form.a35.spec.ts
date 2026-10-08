import { signal } from '@angular/core';
import { ComponentFixture, TestBed, fakeAsync, flush } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of } from 'rxjs';

import { SaleOrderFormComponent } from './sale-order-form.component';
import { SaleOrderService, SaleOrderModel } from '../../../../services/sale-order.service';
import { FulfillmentRoutesService } from '../../../../services/fulfillment-routes.service';
import { SalesPreorderService } from '../../../../services/sales-preorder.service';
import { BusinessPartnerService, BusinessPartnerModel } from '../../../../services/business-partner.service';
import { InventoryService } from '../../../../services/inventory.service';
import { PricingRuleService } from '../../../../services/pricing-rule.service';
import { AddressService } from '../../../../services/address.service';
import { CurrenciesService } from '../../../../services/currencies.service';
import { FinanceSetupService } from '../../../../services/finance-setup.service';
import { LeadTimeService } from '../../../../services/lead-time.service';
import { AuthService } from '../../../service/auth.service';
import { CurrentTenant, TenantService } from '../../../service/tenant.service';
import { TEST_AED, TEST_PKR, TEST_USD, provideTestOrgCurrencies } from '../../../../shared/doc-currency/doc-currency.testing';

// A35 P3-13 on the sale order form: the currency picker offers the organization's active currencies (api/currencies) and
// follows the customer's default sale currency (D-9, D-14); the header says the rate is locked at confirmation and names
// the sale base. Base amounts only exist once confirmed, so the dual columns are on the detail page.

const ORDER_UUID = '35353535-0000-0000-0000-000000000001';

function ok<T>(result: T) {
  return of({ success: true, message: '', result } as any);
}

const ARAB = { uuid: 'cust-ae', companyName: 'Al Rashid Trading LLC', defaultSaleCurrencyId: 'cur-aed' } as unknown as BusinessPartnerModel;
const LOCAL = { uuid: 'cust-pk', companyName: 'Punjab Group' } as BusinessPartnerModel;

function order(overrides: Partial<SaleOrderModel> = {}): SaleOrderModel {
  return {
    uuid: ORDER_UUID, traceId: 't-1', soNumber: 'SO-2026-0501', partnerId: 'cust-pk', orderDate: '2026-10-07T00:00:00',
    currencyId: 'cur-usd', currencyCode: 'USD', subtotal: 0, taxAmount: 0, discountAmount: 0, grandTotal: 0, status: 'DRAFT',
    requiresShipment: false, deliveryMode: 'SELF_PICKUP', createdDate: '2026-10-07T00:00:00', lines: [], routesEnabled: false,
    confirmBlockers: [], ...overrides
  };
}

describe('SaleOrderFormComponent — A35 currency', () => {
  let fixture: ComponentFixture<SaleOrderFormComponent>;
  let component: SaleOrderFormComponent;

  const q = (id: string): HTMLElement | null => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);
  const text = (el: Element | null) => (el?.textContent ?? '').replace(/\s+/g, ' ').trim();

  function setup(opts: { uuid?: string; model?: SaleOrderModel } = {}) {
    const sales = jasmine.createSpyObj<SaleOrderService>('SaleOrderService',
      ['getSaleOrderById', 'createSaleOrder', 'updateSaleOrder', 'getDefaults', 'checkCustomerPo', 'previewDeliveries']);
    sales.getDefaults.and.returnValue(ok({ deliveryMode: 'SELF_PICKUP', selfPickupEnabled: true }));
    sales.getSaleOrderById.and.returnValue(ok(opts.model ?? order()));
    sales.checkCustomerPo.and.returnValue(ok([]));
    sales.previewDeliveries.and.returnValue(ok({ routesEnabled: false, canConfirm: true, deliveryCount: 0, lines: [], groups: [], blockers: [] }));
    const routes = jasmine.createSpyObj<FulfillmentRoutesService>('FulfillmentRoutesService', ['getRoutes']);
    routes.getRoutes.and.returnValue(ok([]));
    const preorder = jasmine.createSpyObj<SalesPreorderService>('SalesPreorderService', ['getQuotations', 'getQuotation']);
    const partners = jasmine.createSpyObj<BusinessPartnerService>('BusinessPartnerService', ['getPartners', 'getPartnerById']);
    partners.getPartners.and.returnValue(ok({ data: [ARAB, LOCAL], totalRecords: 2, page: 1, pageSize: 20, totalPages: 1 }));
    partners.getPartnerById.and.returnValue(ok(LOCAL));
    const inventory = jasmine.createSpyObj<InventoryService>('InventoryService', ['getProducts']);
    inventory.getProducts.and.returnValue(ok({ data: [], totalRecords: 0, page: 1, pageSize: 500, totalPages: 1 }));
    const pricing = jasmine.createSpyObj<PricingRuleService>('PricingRuleService', ['resolvePrice']);
    pricing.resolvePrice.and.returnValue(ok({ found: true, unitPrice: 40 }));
    const addresses = jasmine.createSpyObj<AddressService>('AddressService', ['getAddresses', 'getAddress']);
    addresses.getAddresses.and.returnValue(ok([]));
    addresses.getAddress.and.returnValue(ok(null));
    // The global catalogue has a currency the organization never set up (EUR).
    const currencies = jasmine.createSpyObj<CurrenciesService>('CurrenciesService', ['getAll']);
    currencies.getAll.and.returnValue(ok([
      { id: 'cur-pkr', name: 'Pakistani Rupee', code: 'PKR' }, { id: 'cur-aed', name: 'UAE Dirham', code: 'AED' },
      { id: 'cur-usd', name: 'US Dollar', code: 'USD' }, { id: 'cur-eur', name: 'Euro', code: 'EUR' }
    ]));
    const setupSvc = jasmine.createSpyObj<FinanceSetupService>('FinanceSetupService', ['getTaxCodes', 'quoteExchangeRate']);
    setupSvc.getTaxCodes.and.returnValue(ok([]));
    setupSvc.quoteExchangeRate.and.returnValue(ok(null));
    const tenant = signal<CurrentTenant | null>({
      id: 'org-1', orgCode: 'SCM', orgName: 'SCM', plan: 'BASIC', baseCurrency: 'cur-pkr',
      enabledFeatureCodes: ['MODULE_DEMAND'], isSuperAdmin: false, roleName: 'Sales', permissions: []
    });

    TestBed.resetTestingModule().configureTestingModule({
      imports: [SaleOrderFormComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), MessageService,
        // USD is set up but deactivated.
        provideTestOrgCurrencies([TEST_PKR, TEST_AED, { ...TEST_USD, isActive: false }]),
        { provide: SaleOrderService, useValue: sales },
        { provide: LeadTimeService, useValue: jasmine.createSpyObj('LeadTimeService', ['calculate']) },
        { provide: FulfillmentRoutesService, useValue: routes },
        { provide: SalesPreorderService, useValue: preorder },
        { provide: BusinessPartnerService, useValue: partners },
        { provide: InventoryService, useValue: inventory },
        { provide: PricingRuleService, useValue: pricing },
        { provide: AddressService, useValue: addresses },
        { provide: CurrenciesService, useValue: currencies },
        { provide: FinanceSetupService, useValue: setupSvc },
        { provide: TenantService, useValue: { tenant } },
        { provide: AuthService, useValue: { hasPermission: () => true } },
        { provide: ActivatedRoute, useValue: { snapshot: {
          paramMap: new Map(opts.uuid ? [['uuid', opts.uuid]] : []), queryParamMap: convertToParamMap({})
        } } }
      ]
    });
    fixture = TestBed.createComponent(SaleOrderFormComponent);
    component = fixture.componentInstance;
    spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    fixture.detectChanges();
  }

  it('offers only the organization\'s active currencies, starting in the sale base', fakeAsync(() => {
    setup();
    expect(component.currencyOptions.map(o => o.value)).toEqual(['cur-pkr', 'cur-aed']);
    expect(component.form.get('currencyId')!.value).toBe('cur-pkr');
    flush();
  }));

  it('takes the customer\'s default sale currency, unless the user already chose one', fakeAsync(() => {
    setup();
    component.form.get('customer')!.setValue(ARAB);
    component.onCustomerSelected(ARAB);
    expect(component.form.get('currencyId')!.value).toBe('cur-aed');

    // A customer without a default goes back to the sale base.
    component.form.get('customer')!.setValue(LOCAL);
    component.onCustomerSelected(LOCAL);
    expect(component.form.get('currencyId')!.value).toBe('cur-pkr');

    component.form.get('currencyId')!.setValue('cur-aed');
    component.form.get('currencyId')!.markAsDirty();
    component.onCustomerSelected(LOCAL);
    expect(component.form.get('currencyId')!.value).withContext('chosen by hand').toBe('cur-aed');
    flush();
  }));

  it('says the rate is locked at confirmation and names the sale base', fakeAsync(() => {
    setup();
    component.form.get('currencyId')!.setValue('cur-aed');
    fixture.detectChanges();
    expect(text(q('dc-currency'))).toBe('AED - UAE Dirham');
    expect(text(q('dc-rate'))).toBe('Locked at confirmation');
    expect(text(q('dc-base'))).toBe('PKR');
    flush();
  }));

  it('a draft in a currency deactivated since keeps it on offer, and keeps it', fakeAsync(() => {
    setup({ uuid: ORDER_UUID });
    expect(component.currencyOptions.map(o => o.value)).toEqual(['cur-pkr', 'cur-aed', 'cur-usd']);
    expect(component.form.get('currencyId')!.value).toBe('cur-usd');
    flush();
  }));
});
