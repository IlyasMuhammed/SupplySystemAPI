import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { SaleOrderFormComponent } from './sale-order-form.component';
import {
  SaleOrderService, SaleOrderModel, CreateSaleOrderRequest, UpdateSaleOrderRequest
} from '../../../../services/sale-order.service';
import {
  SalesPreorderService, SaleQuotation, SaleQuotationLine, SaleQuotationListItem
} from '../../../../services/sales-preorder.service';
import { BusinessPartnerService, BusinessPartnerModel } from '../../../../services/business-partner.service';
import { InventoryService } from '../../../../services/inventory.service';
import { PricingRuleService } from '../../../../services/pricing-rule.service';
import { AddressService } from '../../../../services/address.service';
import { CurrenciesService } from '../../../../services/currencies.service';
import { FinanceSetupService } from '../../../../services/finance-setup.service';
import { AddressModel } from '../../../../services/logistics.service';
import { AuthService } from '../../../service/auth.service';
import { CurrentTenant, TenantService } from '../../../service/tenant.service';

// A32 PD-07 — a new sale order is made directly (today's form, plus the customer's PO reference and date) or
// from an ACCEPTED sale quotation through POST /api/sale-quotations/{uuid}/convert-to-order. The pre-A32 form
// is pinned in sale-order-form.component.spec.ts.

const ORDER_UUID = '11111111-1111-1111-1111-111111111111';
const CUSTOMER: BusinessPartnerModel = { uuid: 'cust-1', companyName: 'GlobalTech Co' } as BusinessPartnerModel;

function ok<T>(result: T, message = '') {
  return of({ success: true, message, result } as any);
}

function address(): AddressModel {
  return { uuid: 'addr-1', line1: 'Plot 12', cityName: 'Karachi', countryName: 'Pakistan', addressType: 'CUSTOMER' } as AddressModel;
}

function order(overrides: Partial<SaleOrderModel> = {}): SaleOrderModel {
  return {
    uuid: ORDER_UUID, traceId: 't-1', soNumber: 'SO-2026-00042', partnerId: 'cust-1',
    orderDate: '2026-09-01T00:00:00Z', currencyId: 'cur-pkr',
    subtotal: 0, taxAmount: 0, discountAmount: 0, grandTotal: 0,
    status: 'DRAFT', requiresShipment: false, deliveryMode: 'SELF_PICKUP', createdDate: '2026-09-01T00:00:00Z',
    lines: [{
      uuid: 'l1', variantUuid: 'v1', itemDescription: 'Cable', quantity: 10, unitPrice: 40, discountPercent: 0,
      taxPercent: 0, lineTotal: 400, fulfilledQty: 0, invoicedQty: 0, status: 'OPEN'
    }],
    ...overrides
  };
}

function listItem(overrides: Partial<SaleQuotationListItem> = {}): SaleQuotationListItem {
  return {
    uuid: 'q-15', quotationNumber: 'SQ-2026-00015', partnerId: 'cust-1', partnerName: 'GlobalTech Co',
    currencyId: 'cur-usd', currencyCode: 'USD', validFrom: '2026-10-01T00:00:00Z', validTo: '2026-10-31T00:00:00Z',
    status: 'ACCEPTED', grandTotal: 1234.5, lineCount: 3, createdDate: '2026-10-01T00:00:00Z',
    ...overrides
  };
}

function qLine(overrides: Partial<SaleQuotationLine> = {}): SaleQuotationLine {
  return {
    uuid: 'ql-1', lineNumber: 1, variantUuid: 'v1', variantSku: 'ROD-10', variantName: 'Steel rod 10mm',
    productDescription: 'Steel rod 10mm', quantity: 500, unitPrice: 2.5, discountPercent: 0, taxPercent: 17,
    taxAmount: 212.5, lineTotal: 1462.5, lineType: 'NORMAL', customerResponse: 'ACCEPTED',
    ...overrides
  };
}

function quotation(overrides: Partial<SaleQuotation> = {}): SaleQuotation {
  return {
    uuid: 'q-15', traceId: 't-q', quotationNumber: 'SQ-2026-00015', partnerId: 'cust-1', partnerName: 'GlobalTech Co',
    sourceInquiry: { uuid: 'i-42', number: 'INQ-2026-00042', status: 'QUOTED' },
    currencyId: 'cur-usd', currencyCode: 'USD', validFrom: '2026-10-01T00:00:00Z', validTo: '2026-10-31T00:00:00Z',
    status: 'ACCEPTED', subtotal: 0, taxAmount: 0, discountAmount: 0, grandTotal: 1462.5,
    createdBy: 1, createdDate: '2026-10-01T00:00:00Z', isEditable: false, allowedActions: ['CONVERT', 'COPY'],
    lines: [
      qLine(),
      qLine({ uuid: 'ql-2', lineNumber: 2, productDescription: 'Bolt M8', lineType: 'REJECTED', variantUuid: null, customerResponse: 'PENDING' }),
      qLine({ uuid: 'ql-3', lineNumber: 3, productDescription: 'Copper wire', customerResponse: 'COUNTER', customerCounterPrice: 2 }),
      qLine({ uuid: 'ql-4', lineNumber: 4, productDescription: 'SS sheet', customerResponse: 'REJECTED' })
    ],
    ...overrides
  };
}

describe('SaleOrderFormComponent — A32 source selection and customer PO', () => {
  let fixture: ComponentFixture<SaleOrderFormComponent>;
  let component: SaleOrderFormComponent;
  let sales: jasmine.SpyObj<SaleOrderService>;
  let preorder: jasmine.SpyObj<SalesPreorderService>;
  let addresses: jasmine.SpyObj<AddressService>;
  let navigate: jasmine.Spy;
  let toasts: jasmine.Spy;
  let permissions: string[];

  const auth = { hasPermission: (code: string) => permissions.includes(code) } as unknown as AuthService;

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  function lastToast() {
    return toasts.calls.mostRecent().args[0] as { severity: string; summary: string; detail: string };
  }

  async function setup(opts: { uuid?: string; model?: SaleOrderModel; query?: Record<string, string> } = {}) {
    sales = jasmine.createSpyObj<SaleOrderService>('SaleOrderService',
      ['getSaleOrderById', 'createSaleOrder', 'updateSaleOrder', 'getDefaults', 'checkCustomerPo']);
    sales.getDefaults.and.returnValue(ok({ deliveryMode: 'SHIP', selfPickupEnabled: true }));
    sales.getSaleOrderById.and.returnValue(ok(opts.model ?? order()));
    sales.createSaleOrder.and.returnValue(ok('new-order-uuid'));
    sales.updateSaleOrder.and.returnValue(ok(null));
    sales.checkCustomerPo.and.returnValue(ok([]));

    preorder = jasmine.createSpyObj<SalesPreorderService>('SalesPreorderService',
      ['getQuotations', 'getQuotation', 'convertQuotationToOrder']);
    preorder.getQuotations.and.returnValue(ok({ data: [listItem()], totalRecords: 1, page: 1, pageSize: 20, totalPages: 1 }));
    preorder.getQuotation.and.returnValue(ok(quotation()));
    preorder.convertQuotationToOrder.and.returnValue(ok('converted-order-uuid'));

    const partners = jasmine.createSpyObj<BusinessPartnerService>('BusinessPartnerService', ['getPartners', 'getPartnerById']);
    partners.getPartners.and.returnValue(ok({ data: [CUSTOMER], totalRecords: 1, page: 1, pageSize: 20, totalPages: 1 }));
    partners.getPartnerById.and.returnValue(ok(CUSTOMER));
    const inventory = jasmine.createSpyObj<InventoryService>('InventoryService', ['getProducts', 'getProductById']);
    inventory.getProducts.and.returnValue(ok({ data: [], totalRecords: 0, page: 1, pageSize: 500, totalPages: 1 }));
    const pricing = jasmine.createSpyObj<PricingRuleService>('PricingRuleService', ['resolvePrice']);
    pricing.resolvePrice.and.returnValue(ok({ found: true, unitPrice: 40 }));
    addresses = jasmine.createSpyObj<AddressService>('AddressService', ['getAddresses', 'getAddress', 'createAddress']);
    addresses.getAddresses.and.returnValue(ok([address()]));
    addresses.getAddress.and.returnValue(ok(address()));
    const currencies = jasmine.createSpyObj<CurrenciesService>('CurrenciesService', ['getAll']);
    currencies.getAll.and.returnValue(ok([{ id: 'cur-pkr', name: 'Pakistani Rupee', code: 'PKR' }, { id: 'cur-usd', name: 'US Dollar', code: 'USD' }]));
    const setupSvc = jasmine.createSpyObj<FinanceSetupService>('FinanceSetupService', ['getTaxCodes', 'quoteExchangeRate']);
    setupSvc.getTaxCodes.and.returnValue(ok([]));
    setupSvc.quoteExchangeRate.and.returnValue(ok(null));
    const tenant = signal<CurrentTenant | null>({
      id: 'org-1', orgCode: 'SCM', orgName: 'SCM', plan: 'BASIC', baseCurrency: 'cur-pkr',
      enabledFeatureCodes: [], isSuperAdmin: false, roleName: 'Sales', permissions: []
    });

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [SaleOrderFormComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), MessageService,
        { provide: SaleOrderService, useValue: sales },
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
          queryParamMap: convertToParamMap(opts.query ?? {})
        } } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(SaleOrderFormComponent);
    component = fixture.componentInstance;
    navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    toasts = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
    fixture.detectChanges();
  }

  /** Fills the direct form's one line so the order can be saved. */
  function fillDirectOrder() {
    component.form.get('customer')!.setValue(CUSTOMER);
    component.onCustomerSelected(CUSTOMER);
    component.form.patchValue({ deliveryMode: 'SELF_PICKUP' });
    component.onModeChange();
    component.onLineVariantSelected(0, {
      productUuid: 'p1', productName: 'Cable', variantId: 1, variantUuid: 'v1', variantSku: 'CAB', variantName: '4mm',
      purchasePrice: 30, uomCode: 'M'
    });
    component.lineControl(0, 'quantity').setValue(10);
  }

  async function pickQuotation() {
    component.setCreateMode('QUOTATION');
    component.onQuotationSelected(listItem());
    fixture.detectChanges();
  }

  beforeEach(() => { permissions = ['SALE_ORDER_CREATE', 'SALE_ORDER_EDIT', 'SALE_ORDER_VIEW', 'SALE_QUOTATION_VIEW']; });

  // ── Choosing how the order is made ─────────────────────────────────────────

  it('starts a new order as a direct one, offering both ways to make it', async () => {
    await setup();

    expect(component.createMode).toBe('DIRECT');
    expect(query('source-mode')).not.toBeNull();
    expect(component.sourceModeOptions.map(o => o.value)).toEqual(['DIRECT', 'QUOTATION']);
    expect(component.sourceModeOptions.find(o => o.value === 'QUOTATION')!.disabled).toBeFalsy();
    expect(query('line-items-card')).not.toBeNull();
    expect(query('quotation-card')).toBeNull();
  });

  it('offers the quotation route only to someone who may read quotations', async () => {
    permissions = ['SALE_ORDER_CREATE'];
    await setup();

    expect(component.sourceModeOptions.find(o => o.value === 'QUOTATION')!.disabled).toBeTrue();
    component.setCreateMode('QUOTATION');
    expect(component.createMode).toBe('DIRECT');
  });

  it('offers no choice of source when editing an order', async () => {
    await setup({ uuid: ORDER_UUID });
    expect(query('source-mode')).toBeNull();
  });

  // ── Direct: customer PO reference and date ─────────────────────────────────

  it('sends the customer PO reference, trimmed, and its date as the day picked', async () => {
    await setup();
    fillDirectOrder();
    component.form.patchValue({ customerPoReference: '  GT-PO-2026-4521 ', customerPoDate: new Date(2026, 9, 5) });

    const request = component.buildRequest() as CreateSaleOrderRequest;

    expect(request.customerPoReference).toBe('GT-PO-2026-4521');
    expect(request.customerPoDate).toBe('2026-10-05');
    expect(request.sourceType).toBeUndefined();
  });

  it('leaves the customer PO out when none is given', async () => {
    await setup();
    fillDirectOrder();

    const request = component.buildRequest() as CreateSaleOrderRequest;

    expect('customerPoReference' in request).toBeFalse();
    expect('customerPoDate' in request).toBeFalse();
  });

  it('refuses a reference over 50 characters before saving', async () => {
    await setup();
    fillDirectOrder();
    component.form.patchValue({ customerPoReference: 'X'.repeat(51) });

    component.save();

    expect(sales.createSaleOrder).not.toHaveBeenCalled();
    expect(lastToast().detail).toContain('50');
  });

  it("reads a draft's customer PO and sends it back with the update", async () => {
    await setup({ uuid: ORDER_UUID, model: order({ customerPoReference: 'GT-PO-1', customerPoDate: '2026-10-05T00:00:00Z' }) });

    expect(component.form.get('customerPoReference')!.value).toBe('GT-PO-1');
    expect((component.form.get('customerPoDate')!.value as Date).getDate()).toBe(5);

    const request = component.buildRequest() as UpdateSaleOrderRequest;
    expect(request.customerPoReference).toBe('GT-PO-1');
    expect(request.customerPoDate).toBe('2026-10-05');
  });

  it('warns, without blocking the save, when the customer PO is already on another order', async () => {
    await setup();
    fillDirectOrder();
    sales.checkCustomerPo.and.returnValue(ok([
      { uuid: 'so-9', soNumber: 'SO-2026-00009', partnerId: 'cust-1', status: 'CONFIRMED', orderDate: '2026-09-20T00:00:00Z' }
    ]));

    component.form.patchValue({ customerPoReference: ' GT-PO-1 ' });
    component.onCustomerPoBlur();
    fixture.detectChanges();

    expect(sales.checkCustomerPo).toHaveBeenCalledOnceWith('GT-PO-1', undefined);
    expect(query('po-duplicate-warning')!.textContent).toContain('SO-2026-00009');

    component.save();
    expect(sales.createSaleOrder).toHaveBeenCalledTimes(1);
  });

  it('checks a draft against every order but itself, and not at all with no reference', async () => {
    await setup({ uuid: ORDER_UUID, model: order({ customerPoReference: 'GT-PO-1' }) });
    component.onCustomerPoBlur();
    expect(sales.checkCustomerPo).toHaveBeenCalledOnceWith('GT-PO-1', ORDER_UUID);

    component.form.patchValue({ customerPoReference: '   ' });
    component.onCustomerPoBlur();
    expect(sales.checkCustomerPo).toHaveBeenCalledTimes(1);
    expect(component.poDuplicates).toEqual([]);
  });

  // ── From an accepted quotation ─────────────────────────────────────────────

  it('searches accepted quotations only', async () => {
    await setup();
    component.setCreateMode('QUOTATION');

    component.searchQuotations({ query: 'SQ-2026' } as any);

    expect(preorder.getQuotations).toHaveBeenCalledWith({ status: 'ACCEPTED', search: 'SQ-2026', pageSize: 20 });
    expect(component.quotationSuggestions.map(q => q.quotationNumber)).toEqual(['SQ-2026-00015']);
  });

  it('takes the customer, the currency and the accepted lines from the quotation picked', async () => {
    await setup();
    await pickQuotation();

    expect(preorder.getQuotation).toHaveBeenCalledWith('q-15');
    expect(component.selectedQuotation!.quotationNumber).toBe('SQ-2026-00015');
    // Only lines the customer accepted are carried over (a counter offer is not an acceptance).
    expect(component.acceptedLines.map(l => l.uuid)).toEqual(['ql-1']);
    expect(component.customer!.uuid).toBe('cust-1');
    expect(component.form.get('customer')!.disabled).toBeTrue();
    expect(addresses.getAddresses).toHaveBeenCalledWith('cust-1');

    expect(query('quotation-card')!.textContent).toContain('GlobalTech Co');
    expect(query('quotation-card')!.textContent).toContain('USD');
    expect(fixture.nativeElement.querySelectorAll('[data-testid="quotation-line"]').length).toBe(1);
    expect(query('line-items-card')).toBeNull();
  });

  it('converts through the quotation endpoint with the order details and customer PO, then opens the new order', async () => {
    await setup();
    await pickQuotation();
    component.form.patchValue({
      deliveryMode: 'SHIP', shippingAddressId: 'addr-1', expectedDeliveryDate: new Date(2026, 9, 20),
      notes: '  Call before delivery ', customerPoReference: ' GT-PO-2026-4521 ', customerPoDate: new Date(2026, 9, 5)
    });

    component.save();

    expect(sales.createSaleOrder).not.toHaveBeenCalled();
    expect(preorder.convertQuotationToOrder).toHaveBeenCalledOnceWith('q-15', {
      expectedDeliveryDate: '2026-10-20',
      deliveryMode: 'SHIP',
      shippingAddressId: 'addr-1',
      notes: 'Call before delivery',
      customerPoReference: 'GT-PO-2026-4521',
      customerPoDate: '2026-10-05'
    });
    expect(navigate).toHaveBeenCalledWith(['/portal/pages/sales/orders', 'converted-order-uuid']);
  });

  it('sends no address for a collected order converted from a quotation', async () => {
    await setup();
    await pickQuotation();
    component.form.patchValue({ deliveryMode: 'SELF_PICKUP' });
    component.onModeChange();

    component.save();

    expect(preorder.convertQuotationToOrder).toHaveBeenCalledOnceWith('q-15', { deliveryMode: 'SELF_PICKUP' });
  });

  it('will not convert before a quotation is picked, or one with no accepted line, or a shipped one with no address', async () => {
    await setup();
    component.setCreateMode('QUOTATION');
    component.save();
    expect(lastToast().detail).toBe('Choose an accepted quotation.');

    preorder.getQuotation.and.returnValue(ok(quotation({ lines: [qLine({ customerResponse: 'COUNTER' })] })));
    component.onQuotationSelected(listItem());
    component.save();
    expect(lastToast().detail).toContain('no line the customer accepted');

    preorder.getQuotation.and.returnValue(ok(quotation()));
    component.onQuotationSelected(listItem());
    component.form.patchValue({ deliveryMode: 'SHIP', shippingAddressId: null });
    component.save();
    expect(lastToast().detail).toBe('A shipped order needs a shipping address.');

    expect(preorder.convertQuotationToOrder).not.toHaveBeenCalled();
  });

  it('will not convert a quotation that is no longer accepted', async () => {
    await setup();
    component.setCreateMode('QUOTATION');
    preorder.getQuotation.and.returnValue(ok(quotation({ status: 'CONVERTED' })));
    component.onQuotationSelected(listItem());
    fixture.detectChanges();

    expect(component.selectedQuotation).toBeNull();
    expect(lastToast().detail).toContain('Converted');
  });

  it("shows the server's refusal when the conversion fails", async () => {
    await setup();
    await pickQuotation();
    component.form.patchValue({ deliveryMode: 'SELF_PICKUP' });
    component.onModeChange();
    preorder.convertQuotationToOrder.and.returnValue(throwError(() => ({ status: 409, error: { message: 'SQ-2026-00015 has already been converted.' } })));

    component.save();

    expect(lastToast().detail).toBe('SQ-2026-00015 has already been converted.');
    expect(component.isSaving).toBeFalse();
    expect(navigate).not.toHaveBeenCalled();
  });

  it('goes back to a direct order with the customer free to choose again', async () => {
    await setup();
    await pickQuotation();

    component.setCreateMode('DIRECT');

    expect(component.selectedQuotation).toBeNull();
    expect(component.form.get('customer')!.enabled).toBeTrue();
    expect(component.customer).toBeNull();
  });

  it('opens on the quotation route from ?source=quotation, and with that quotation from ?quotation=', async () => {
    await setup({ query: { source: 'quotation' } });
    expect(component.createMode).toBe('QUOTATION');
    expect(preorder.getQuotation).not.toHaveBeenCalled();

    await setup({ query: { quotation: 'q-15' } });
    expect(component.createMode).toBe('QUOTATION');
    expect(preorder.getQuotation).toHaveBeenCalledWith('q-15');
    expect(component.selectedQuotation!.uuid).toBe('q-15');
  });
});
