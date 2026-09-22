import { WritableSignal, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute, Router } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError, Subject } from 'rxjs';

import { SaleOrderFormComponent } from './sale-order-form.component';
import {
  SaleOrderService, SaleOrderModel, SaleOrderLineModel, CreateSaleOrderRequest, UpdateSaleOrderRequest
} from '../../../../services/sale-order.service';
import { BusinessPartnerService, BusinessPartnerModel } from '../../../../services/business-partner.service';
import { InventoryService } from '../../../../services/inventory.service';
import { PricingRuleService } from '../../../../services/pricing-rule.service';
import { AddressService } from '../../../../services/address.service';
import { CurrenciesService } from '../../../../services/currencies.service';
import { AddressModel } from '../../../../services/logistics.service';
import { AuthService } from '../../../service/auth.service';
import { CurrentTenant, TenantService } from '../../../service/tenant.service';

const ORDER_UUID = '11111111-1111-1111-1111-111111111111';
const CUSTOMER: BusinessPartnerModel = { uuid: 'cust-1', companyName: 'Acme Ltd' } as BusinessPartnerModel;
const OTHER_CUSTOMER: BusinessPartnerModel = { uuid: 'cust-2', companyName: 'Globex Corp' } as BusinessPartnerModel;

function address(overrides: Partial<AddressModel> = {}): AddressModel {
  return {
    uuid: 'addr-1', line1: 'Plot 12', cityName: 'Karachi', postalCode: '74900', countryName: 'Pakistan',
    addressType: 'CUSTOMER', validationStatus: 'VALID',
    ...overrides
  };
}

function line(overrides: Partial<SaleOrderLineModel> = {}): SaleOrderLineModel {
  return {
    uuid: 'l1', variantUuid: 'v1', itemDescription: '4mm cable (CAB-4MM)', variantSku: 'CAB-4MM', unitOfMeasure: 'M',
    quantity: 100, unitPrice: 40, discountPercent: 5, taxPercent: 17, lineTotal: 4446,
    fulfilledQty: 0, invoicedQty: 0, fulfillmentMode: 'IN_STOCK', status: 'OPEN',
    ...overrides
  };
}

function order(overrides: Partial<SaleOrderModel> = {}): SaleOrderModel {
  return {
    uuid: ORDER_UUID, traceId: 't-1', soNumber: 'SO-2026-00042', partnerId: 'cust-1',
    orderDate: '2026-09-01T00:00:00Z', expectedDeliveryDate: '2026-09-30T00:00:00Z', currencyId: 'cur-usd',
    subtotal: 4000, taxAmount: 0, discountAmount: 0, grandTotal: 4000,
    status: 'DRAFT', requiresShipment: true, deliveryMode: 'SHIP', shippingAddressId: 'addr-1',
    intimationDepartmentId: 7, notes: 'Handle with care', createdDate: '2026-09-01T00:00:00Z',
    lines: [line()],
    ...overrides
  };
}

function ok<T>(result: T) {
  return of({ success: true, message: '', result } as any);
}

const USD = { id: 'cur-usd', name: 'US Dollar', code: 'USD', symbol: '$' };
const PKR = { id: 'cur-pkr', name: 'Pakistani Rupee', code: 'PKR', symbol: 'Rs' };

function tenantWith(baseCurrency: string | null): CurrentTenant {
  return {
    id: 'org-1', orgCode: 'SCM-DEMO', orgName: 'Supply Chain Demo', plan: 'BASIC', baseCurrency,
    enabledFeatureCodes: [], isSuperAdmin: false, roleName: 'Sales', permissions: []
  };
}

describe('SaleOrderFormComponent', () => {
  let fixture: ComponentFixture<SaleOrderFormComponent>;
  let component: SaleOrderFormComponent;
  let sales: jasmine.SpyObj<SaleOrderService>;
  let partners: jasmine.SpyObj<BusinessPartnerService>;
  let inventory: jasmine.SpyObj<InventoryService>;
  let pricing: jasmine.SpyObj<PricingRuleService>;
  let addresses: jasmine.SpyObj<AddressService>;
  let currencies: jasmine.SpyObj<CurrenciesService>;
  let tenant: WritableSignal<CurrentTenant | null>;
  let baseCurrency: string | null;
  let router: Router;
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

  async function setup(routeUuid: string | null = null, model: SaleOrderModel | null = order()) {
    sales = jasmine.createSpyObj<SaleOrderService>('SaleOrderService', ['getSaleOrderById', 'createSaleOrder', 'updateSaleOrder', 'getDefaults']);
    sales.getDefaults.and.returnValue(ok({ deliveryMode: 'SHIP', selfPickupEnabled: true }));
    sales.getSaleOrderById.and.returnValue(model ? ok(model) : of({ success: false, message: 'no', result: null } as any));
    sales.createSaleOrder.and.returnValue(ok('new-order-uuid'));
    sales.updateSaleOrder.and.returnValue(ok(null));

    partners = jasmine.createSpyObj<BusinessPartnerService>('BusinessPartnerService', ['getPartners', 'getPartnerById']);
    partners.getPartners.and.returnValue(ok({ data: [CUSTOMER, OTHER_CUSTOMER], totalRecords: 2, page: 1, pageSize: 20, totalPages: 1 }));
    partners.getPartnerById.and.returnValue(ok(CUSTOMER));

    inventory = jasmine.createSpyObj<InventoryService>('InventoryService', ['getProducts', 'getProductById']);
    inventory.getProducts.and.returnValue(ok({ data: [], totalRecords: 0, page: 1, pageSize: 500, totalPages: 1 }));

    pricing = jasmine.createSpyObj<PricingRuleService>('PricingRuleService', ['resolvePrice']);
    pricing.resolvePrice.and.returnValue(ok({ found: true, unitPrice: 40 }));

    addresses = jasmine.createSpyObj<AddressService>('AddressService', ['getAddresses', 'getAddress', 'createAddress']);
    addresses.getAddresses.and.returnValue(ok([address()]));
    addresses.getAddress.and.returnValue(ok(address()));
    addresses.createAddress.and.returnValue(ok(address({ uuid: 'addr-new', line1: '7 Canal Rd' })));

    currencies = jasmine.createSpyObj<CurrenciesService>('CurrenciesService', ['getAll']);
    currencies.getAll.and.returnValue(ok([USD, PKR]));
    tenant = signal<CurrentTenant | null>(tenantWith(baseCurrency));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [SaleOrderFormComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), MessageService,
        { provide: SaleOrderService, useValue: sales },
        { provide: BusinessPartnerService, useValue: partners },
        { provide: InventoryService, useValue: inventory },
        { provide: PricingRuleService, useValue: pricing },
        { provide: AddressService, useValue: addresses },
        { provide: CurrenciesService, useValue: currencies },
        { provide: TenantService, useValue: { tenant } },
        { provide: AuthService, useValue: auth },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map(routeUuid ? [['uuid', routeUuid]] : []) } } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(SaleOrderFormComponent);
    component = fixture.componentInstance;
    router = TestBed.inject(Router);
    navigate = spyOn(router, 'navigate').and.resolveTo(true);
    toasts = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
  }

  /** Picks a customer the way the autocomplete does: the control takes the partner, then the select event fires. */
  function pickCustomer(partner: BusinessPartnerModel = CUSTOMER) {
    component.form.get('customer')!.setValue(partner);
    component.onCustomerSelected(partner);
  }

  /** Fills line `i` as if an item had been picked and a quantity typed and left (which is when the price is asked for). */
  function fillLine(i: number, quantity = 10, variantUuid = 'v1') {
    component.onLineVariantSelected(i, {
      productUuid: 'p1', productName: 'Cable', variantId: 1, variantUuid, variantSku: 'CAB-4MM',
      variantName: '4mm', purchasePrice: 30, uomCode: 'M'
    });
    component.lineControl(i, 'quantity').setValue(quantity);
    component.refreshPrice(i);
  }

  beforeEach(() => {
    permissions = ['SALE_ORDER_VIEW', 'SALE_ORDER_CREATE', 'SALE_ORDER_EDIT', 'INVENTORY_VIEW'];
    baseCurrency = USD.id;
  });

  // ── A new order ────────────────────────────────────────────────────────────

  it('starts a new order shipping, with one empty line and nothing loaded for edit', async () => {
    await setup();
    fixture.detectChanges();

    expect(component.isEdit).toBeFalse();
    expect(component.isShip).toBeTrue();
    expect(component.lines.length).toBe(1);
    expect(sales.getSaleOrderById).not.toHaveBeenCalled();
    expect(inventory.getProducts).toHaveBeenCalledOnceWith({ activeOnly: true, pageSize: 500 });
    expect(fixture.nativeElement.textContent).toContain('New sale order');
    expect(query('save')!.textContent).toContain('Save draft');
  });

  // ── What the organization's settings say about delivery ────────────────────

  it('opens on the delivery mode the organization starts orders as, and then needs no address', async () => {
    await setup();
    sales.getDefaults.and.returnValue(ok({ deliveryMode: 'SELF_PICKUP', selfPickupEnabled: true }));
    fixture.detectChanges();

    expect(component.form.get('deliveryMode')!.value).toBe('SELF_PICKUP');
    expect(component.isShip).toBeFalse();
    expect(query('address-field')).toBeNull();
    expect(component.pickupOff).toBeFalse();
  });

  it('opens shipped when that is the default', async () => {
    await setup();
    fixture.detectChanges();

    expect(component.form.get('deliveryMode')!.value).toBe('SHIP');
    expect(query('address-field')).not.toBeNull();
  });

  it('does not change a mode the user has already chosen when the defaults arrive late', async () => {
    await setup();
    const late = new Subject<any>();
    sales.getDefaults.and.returnValue(late);
    fixture.detectChanges();

    const mode = component.form.get('deliveryMode')!;
    mode.setValue('SELF_PICKUP');
    mode.markAsDirty();
    late.next({ success: true, message: '', result: { deliveryMode: 'SHIP', selfPickupEnabled: true } });

    expect(mode.value).toBe('SELF_PICKUP');
  });

  it('no longer offers pickup, and says why, when the organization has it switched off', async () => {
    await setup();
    sales.getDefaults.and.returnValue(ok({ deliveryMode: 'SHIP', selfPickupEnabled: false }));
    fixture.detectChanges();

    expect(component.pickupOff).toBeTrue();
    expect(component.modeOptions.find(o => o.value === 'SELF_PICKUP')!.disabled).toBeTrue();
    expect(component.modeOptions.find(o => o.value === 'SHIP')!.disabled).toBeFalsy();
    expect(query('pickup-off')!.textContent).toContain('Customer pickup is switched off');
  });

  it('offers pickup, and says nothing, when it is on', async () => {
    await setup();
    fixture.detectChanges();

    expect(component.modeOptions.find(o => o.value === 'SELF_PICKUP')!.disabled).toBeFalse();
    expect(query('pickup-off')).toBeNull();
  });

  it('keeps the mode an order being edited already has, whatever the default is', async () => {
    await setup(ORDER_UUID, order({ deliveryMode: 'SELF_PICKUP', shippingAddressId: undefined }));
    sales.getDefaults.and.returnValue(ok({ deliveryMode: 'SHIP', selfPickupEnabled: true }));
    fixture.detectChanges();

    expect(component.form.get('deliveryMode')!.value).toBe('SELF_PICKUP');
  });

  it('still works when the defaults cannot be read: orders start as shipped, and no error is shown', async () => {
    await setup();
    sales.getDefaults.and.returnValue(throwError(() => ({ status: 403 })));
    fixture.detectChanges();

    expect(component.form.get('deliveryMode')!.value).toBe('SHIP');
    expect(component.pickupOff).toBeFalse();
    const errors = toasts.calls.allArgs().filter(a => (a[0] as any).severity === 'error');
    expect(errors).toEqual([]);
  });

  it('says so when the product list cannot be loaded', async () => {
    await setup();
    inventory.getProducts.and.returnValue(throwError(() => ({ status: 500 })));
    fixture.detectChanges();

    expect(lastToast().severity).toBe('error');
    expect(lastToast().detail).toContain('product list');
  });

  it('searches active customers only, by what was typed', async () => {
    await setup();
    fixture.detectChanges();

    component.searchCustomers({ query: 'acm' } as any);

    expect(partners.getPartners).toHaveBeenCalledOnceWith({ isCustomer: true, active: true, search: 'acm', pageSize: 20 });
    expect(component.customerSuggestions.map(c => c.companyName)).toEqual(['Acme Ltd', 'Globex Corp']);

    partners.getPartners.and.returnValue(throwError(() => ({ status: 500 })));
    component.searchCustomers({ query: 'x' } as any);
    expect(component.customerSuggestions).toEqual([]);
  });

  it('refuses to save without a customer, and does not call the server', async () => {
    await setup();
    fixture.detectChanges();
    fillLine(0);

    component.save();

    expect(lastToast().severity).toBe('warn');
    expect(lastToast().detail).toBe('Choose the customer from the list.');
    expect(sales.createSaleOrder).not.toHaveBeenCalled();
  });

  it('does not accept customer text that was typed rather than picked', async () => {
    await setup();
    fixture.detectChanges();
    fillLine(0);
    component.form.get('customer')!.setValue('Acme');

    expect(component.customer).toBeNull();
    component.save();
    expect(lastToast().detail).toBe('Choose the customer from the list.');
    expect(sales.createSaleOrder).not.toHaveBeenCalled();
  });

  it('refuses a shipped order with no address, and a collected one needs none', async () => {
    await setup();
    fixture.detectChanges();
    pickCustomer();
    fillLine(0);

    component.save();
    expect(lastToast().detail).toBe('A shipped order needs a shipping address.');
    expect(sales.createSaleOrder).not.toHaveBeenCalled();

    component.form.get('deliveryMode')!.setValue('SELF_PICKUP');
    component.onModeChange();
    component.save();
    expect(sales.createSaleOrder).toHaveBeenCalledTimes(1);
  });

  it('drops the address when the order becomes a collection, and asks for one again when it ships', async () => {
    await setup();
    fixture.detectChanges();
    pickCustomer();
    component.form.get('shippingAddressId')!.setValue('addr-1');

    component.form.get('deliveryMode')!.setValue('SELF_PICKUP');
    component.onModeChange();
    expect(component.form.get('shippingAddressId')!.value).toBeNull();
    expect(component.form.get('shippingAddressId')!.valid).toBeTrue();
    fixture.detectChanges();
    expect(query('address-field')).toBeNull();

    component.form.get('deliveryMode')!.setValue('SHIP');
    component.onModeChange();
    expect(component.form.get('shippingAddressId')!.valid).toBeFalse();
    fixture.detectChanges();
    expect(query('address-field')).not.toBeNull();
  });

  it('refuses a line with no item or no quantity, and says which line', async () => {
    await setup();
    fixture.detectChanges();
    pickCustomer();
    component.form.get('deliveryMode')!.setValue('SELF_PICKUP');
    component.onModeChange();

    component.save();
    expect(lastToast().detail).toBe('Line 1 is incomplete: choose an item and a quantity above zero.');

    component.addLine();
    fillLine(0);
    component.save();
    expect(lastToast().detail).toBe('Line 2 is incomplete: choose an item and a quantity above zero.');

    fillLine(1, 0);
    component.save();
    expect(lastToast().detail).withContext('zero is not a quantity').toContain('Line 2');
    expect(sales.createSaleOrder).not.toHaveBeenCalled();
  });

  it('refuses an order with no lines, and shows why on the page', async () => {
    await setup();
    fixture.detectChanges();
    pickCustomer();
    component.form.get('deliveryMode')!.setValue('SELF_PICKUP');
    component.onModeChange();

    component.removeLine(0);
    fixture.detectChanges();

    expect(query('no-lines')).not.toBeNull();
    component.save();
    expect(lastToast().detail).toBe('Add at least one line.');
    expect(sales.createSaleOrder).not.toHaveBeenCalled();
  });

  it('adds and removes lines from the page', async () => {
    await setup();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelectorAll('[data-testid="line"]').length).toBe(1);

    query('add-line')!.querySelector('button')!.click();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelectorAll('[data-testid="line"]').length).toBe(2);

    fixture.nativeElement.querySelector('[data-testid="remove-line"] button').click();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelectorAll('[data-testid="line"]').length).toBe(1);
  });

  // ── The request ────────────────────────────────────────────────────────────

  it('sends the customer, mode, address, date, notes and each line, and no prices', async () => {
    await setup();
    fixture.detectChanges();
    pickCustomer();
    component.form.patchValue({ shippingAddressId: 'addr-1', expectedDeliveryDate: new Date(2026, 8, 5), notes: '  Deliver before noon  ' });
    fillLine(0, 25, 'v1');
    component.lineControl(0, 'discountPercent').setValue(10);
    component.lineControl(0, 'taxPercent').setValue(17);
    component.addLine();
    fillLine(1, 2.5, 'v2');

    const request = component.buildRequest() as CreateSaleOrderRequest;

    expect(request).toEqual({
      partnerId: 'cust-1',
      expectedDeliveryDate: '2026-09-05',
      currencyId: 'cur-usd',
      deliveryMode: 'SHIP',
      shippingAddressId: 'addr-1',
      notes: 'Deliver before noon',
      lines: [
        { variantUuid: 'v1', quantity: 25,  discountPercent: 10, taxPercent: 17 },
        { variantUuid: 'v2', quantity: 2.5, discountPercent: 0,  taxPercent: 0 }
      ]
    });
    expect(JSON.stringify(request)).not.toContain('unitPrice');
  });

  it('leaves the date, notes and address out when there are none, and never sends an address for a collection', async () => {
    await setup();
    fixture.detectChanges();
    pickCustomer();
    component.form.patchValue({ deliveryMode: 'SELF_PICKUP', notes: '   ' });
    component.onModeChange();
    fillLine(0);

    const request = component.buildRequest() as CreateSaleOrderRequest;

    expect(request.expectedDeliveryDate).toBeUndefined();
    expect(request.notes).toBeUndefined();
    expect(request.shippingAddressId).toBeUndefined();
    expect(request.deliveryMode).toBe('SELF_PICKUP');
  });

  it('saves the draft and opens it', async () => {
    await setup();
    fixture.detectChanges();
    pickCustomer();
    component.form.patchValue({ shippingAddressId: 'addr-1' });
    fillLine(0);

    component.save();

    expect(sales.createSaleOrder).toHaveBeenCalledTimes(1);
    expect(navigate).toHaveBeenCalledWith(['/portal/pages/sales/orders', 'new-order-uuid']);
    expect(component.isSaving).toBeFalse();
    expect(lastToast().severity).toBe('success');
  });

  it('shows the servers reason when the save is refused, stays on the form, and can be tried again', async () => {
    await setup();
    fixture.detectChanges();
    pickCustomer();
    component.form.patchValue({ shippingAddressId: 'addr-1' });
    fillLine(0);
    sales.createSaleOrder.and.returnValue(throwError(() => ({ error: { message: 'No sale price rule applies to line 1.' } })));

    component.save();

    expect(lastToast().severity).toBe('error');
    expect(lastToast().detail).toBe('No sale price rule applies to line 1.');
    expect(component.isSaving).toBeFalse();
    expect(navigate).not.toHaveBeenCalled();

    sales.createSaleOrder.and.returnValue(ok('new-order-uuid'));
    component.save();
    expect(navigate).toHaveBeenCalledTimes(1);
  });

  it('saves once however often the button is pressed while it is working', async () => {
    await setup();
    fixture.detectChanges();
    pickCustomer();
    component.form.patchValue({ shippingAddressId: 'addr-1' });
    fillLine(0);
    sales.createSaleOrder.and.returnValue(new Subject<any>());

    component.save();
    component.save();

    expect(sales.createSaleOrder).toHaveBeenCalledTimes(1);
  });

  // ── Customer and addresses ─────────────────────────────────────────────────

  it('loads the customers addresses when one is picked, and clears an address chosen for another', async () => {
    await setup();
    fixture.detectChanges();
    component.form.get('shippingAddressId')!.setValue('addr-of-someone-else');

    pickCustomer();

    expect(addresses.getAddresses).toHaveBeenCalledOnceWith('cust-1');
    expect(component.addresses.map(a => a.uuid)).toEqual(['addr-1']);
    expect(component.form.get('shippingAddressId')!.value).toBeNull();
    expect(component.addressOptions).toEqual([{ label: 'Plot 12, Karachi 74900, Pakistan', value: 'addr-1' }]);
  });

  it('empties the addresses when the customer is cleared', async () => {
    await setup();
    fixture.detectChanges();
    pickCustomer();
    component.form.get('shippingAddressId')!.setValue('addr-1');

    component.onCustomerCleared();

    expect(component.addresses).toEqual([]);
    expect(component.form.get('shippingAddressId')!.value).toBeNull();
  });

  it('carries on with no addresses when they cannot be loaded, so one can still be added', async () => {
    await setup();
    addresses.getAddresses.and.returnValue(throwError(() => ({ status: 500 })));
    fixture.detectChanges();

    pickCustomer();

    expect(component.addresses).toEqual([]);
    expect(component.isLoadingAddresses).toBeFalse();
    expect(component.canAddAddress).toBeTrue();
  });

  // ── The new-address dialog ─────────────────────────────────────────────────

  it('cannot add an address before a customer is picked, or without permission to create orders', async () => {
    await setup();
    fixture.detectChanges();
    component.openAddressDialog();
    expect(component.addressDialogVisible).toBeFalse();

    permissions = ['SALE_ORDER_VIEW'];
    pickCustomer();
    expect(component.canAddAddress).toBeFalse();
    component.openAddressDialog();
    expect(component.addressDialogVisible).toBeFalse();

    permissions = ['SALE_ORDER_CREATE'];
    expect(component.canAddAddress).toBeTrue();
    component.openAddressDialog();
    expect(component.addressDialogVisible).toBeTrue();
  });

  it('will not save an address missing its line, city or country, or with a bad email', async () => {
    await setup();
    fixture.detectChanges();
    pickCustomer();
    component.openAddressDialog();

    component.saveAddress();
    expect(addresses.createAddress).not.toHaveBeenCalled();

    component.addressForm.patchValue({ line1: '7 Canal Rd', cityName: 'Lahore', countryName: 'Pakistan', contactEmail: 'not an email' });
    component.saveAddress();
    expect(addresses.createAddress).not.toHaveBeenCalled();

    component.addressForm.patchValue({ contactEmail: 'ops@acme.example' });
    component.saveAddress();
    expect(addresses.createAddress).toHaveBeenCalledTimes(1);
  });

  it('saves the address for this customer as a customer address, trimmed, and chooses it', async () => {
    await setup();
    fixture.detectChanges();
    pickCustomer();
    component.openAddressDialog();
    component.addressForm.patchValue({
      line1: '  7 Canal Rd  ', line2: '  ', cityName: ' Lahore ', postalCode: ' 54000 ', countryName: ' Pakistan ', contactName: 'Bilal'
    });

    component.saveAddress();

    expect(addresses.createAddress).toHaveBeenCalledOnceWith({
      line1: '7 Canal Rd', line2: undefined, cityName: 'Lahore', state: undefined, postalCode: '54000', countryName: 'Pakistan',
      contactName: 'Bilal', contactPhone: undefined, contactEmail: undefined,
      addressType: 'CUSTOMER', consigneeUuid: 'cust-1'
    });
    expect(component.form.get('shippingAddressId')!.value).toBe('addr-new');
    expect(component.addresses[0].uuid).withContext('newest first').toBe('addr-new');
    expect(component.addressDialogVisible).toBeFalse();
    expect(component.isSavingAddress).toBeFalse();
  });

  it('tells the user when a saved address could not be fully checked, and stays quiet when it could', async () => {
    await setup();
    fixture.detectChanges();
    pickCustomer();
    component.openAddressDialog();
    component.addressForm.patchValue({ line1: 'x', cityName: 'y', countryName: 'z' });

    component.saveAddress();
    expect(toasts).not.toHaveBeenCalled();

    addresses.createAddress.and.returnValue(ok(address({ uuid: 'addr-2', validationStatus: 'UNVALIDATED', validationNotes: 'City not recognised.' })));
    component.openAddressDialog();
    component.addressForm.patchValue({ line1: 'x', cityName: 'y', countryName: 'z' });
    component.saveAddress();

    expect(lastToast().severity).toBe('info');
    expect(lastToast().detail).toBe('City not recognised.');
    expect(component.form.get('shippingAddressId')!.value).toBe('addr-2');
  });

  it('shows the servers reason when an address is refused, and keeps the dialog open', async () => {
    await setup();
    fixture.detectChanges();
    pickCustomer();
    component.openAddressDialog();
    component.addressForm.patchValue({ line1: 'x', cityName: 'y', countryName: 'z' });
    addresses.createAddress.and.returnValue(throwError(() => ({ error: { message: 'That is not a usable address.' } })));

    component.saveAddress();

    expect(lastToast().detail).toBe('That is not a usable address.');
    expect(component.addressDialogVisible).toBeTrue();
    expect(component.isSavingAddress).toBeFalse();
  });

  // ── Price preview ──────────────────────────────────────────────────────────

  it('previews the price rule for the customer and quantity, and totals the line', async () => {
    await setup();
    fixture.detectChanges();
    pickCustomer();
    pricing.resolvePrice.and.returnValue(ok({ found: true, unitPrice: 40 }));

    fillLine(0, 3);
    component.lineControl(0, 'discountPercent').setValue(10);
    component.lineControl(0, 'taxPercent').setValue(10);
    component.refreshPrice(0);

    expect(pricing.resolvePrice).toHaveBeenCalledWith('v1', 'cust-1', 3);
    expect(component.lineControl(0, 'priceState').value).toBe('found');
    expect(component.lineTotal(0)).withContext('3 x 40 less 10% plus 10%').toBe(118.8);
    expect(component.estimatedTotal).toBe(118.8);
    expect(component.unpricedLines).toBe(0);
  });

  it('rounds a line to the cent', async () => {
    await setup();
    fixture.detectChanges();
    component.lineControl(0, 'unitPrice').setValue(0.335);
    component.lineControl(0, 'quantity').setValue(3);

    expect(component.lineTotal(0)).toBe(1.01);
  });

  it('warns when no rule prices the line, and counts it as unpriced', async () => {
    await setup();
    fixture.detectChanges();
    pickCustomer();
    pricing.resolvePrice.and.returnValue(ok({ found: false }));

    fillLine(0, 3);
    fixture.detectChanges();

    expect(component.lineControl(0, 'priceState').value).toBe('missing');
    expect(component.lineTotal(0)).toBeNull();
    expect(component.unpricedLines).toBe(1);
    expect(query('price-missing')).not.toBeNull();
  });

  it('does not preview a price until there is a customer, an item and a quantity', async () => {
    await setup();
    fixture.detectChanges();

    fillLine(0, 3);
    expect(pricing.resolvePrice).withContext('no customer yet').not.toHaveBeenCalled();
    expect(component.lineControl(0, 'priceState').value).toBe('none');

    pickCustomer();
    expect(pricing.resolvePrice).toHaveBeenCalledTimes(1);

    component.lineControl(0, 'quantity').setValue(null);
    component.refreshPrice(0);
    expect(pricing.resolvePrice).toHaveBeenCalledTimes(1);
    expect(component.lineControl(0, 'priceState').value).toBe('none');
  });

  it('reprices every line when the customer changes, since prices are per customer', async () => {
    await setup();
    fixture.detectChanges();
    pickCustomer(CUSTOMER);
    component.addLine();
    fillLine(0, 1, 'v1');
    fillLine(1, 2, 'v2');
    pricing.resolvePrice.calls.reset();

    pickCustomer(OTHER_CUSTOMER);

    expect(pricing.resolvePrice.calls.allArgs()).toEqual([['v1', 'cust-2', 1], ['v2', 'cust-2', 2]]);
  });

  it('says the price is set when saved, and makes no request, for someone who cannot read pricing', async () => {
    permissions = ['SALE_ORDER_CREATE'];
    await setup();
    fixture.detectChanges();
    pickCustomer();

    fillLine(0, 3);

    expect(pricing.resolvePrice).not.toHaveBeenCalled();
    expect(component.lineControl(0, 'priceState').value).toBe('unavailable');
    expect(component.unpricedLines).toBe(1);
  });

  it('falls back to "priced when saved" when the preview fails, and saving is not blocked by it', async () => {
    await setup();
    fixture.detectChanges();
    pickCustomer();
    pricing.resolvePrice.and.returnValue(throwError(() => ({ status: 500 })));

    fillLine(0, 3);

    expect(component.lineControl(0, 'priceState').value).toBe('unavailable');
    component.form.patchValue({ shippingAddressId: 'addr-1' });
    component.save();
    expect(sales.createSaleOrder).toHaveBeenCalledTimes(1);
  });

  it('ignores a slow answer for an old quantity once a newer one has been asked for', async () => {
    await setup();
    fixture.detectChanges();
    pickCustomer();
    const slow = new Subject<any>();
    const fast = new Subject<any>();
    pricing.resolvePrice.and.returnValues(slow, fast);

    fillLine(0, 1);
    component.lineControl(0, 'quantity').setValue(500);
    component.refreshPrice(0);
    fast.next({ success: true, result: { found: true, unitPrice: 35 } });
    slow.next({ success: true, result: { found: true, unitPrice: 40 } });

    expect(component.lineControl(0, 'unitPrice').value).withContext('the newer answer stands').toBe(35);
  });

  it('forgets the price when the item is changed', async () => {
    await setup();
    fixture.detectChanges();
    pickCustomer();
    fillLine(0, 3);
    expect(component.lineControl(0, 'unitPrice').value).toBe(40);

    pricing.resolvePrice.and.returnValue(ok({ found: false }));
    component.onLineVariantSelected(0, {
      productUuid: 'p2', productName: 'Wire', variantId: 2, variantUuid: 'v9', variantSku: 'W-1', variantName: 'Default',
      purchasePrice: 1, uomCode: 'M'
    });

    expect(component.lineControl(0, 'unitPrice').value).toBeNull();
    expect(component.lineControl(0, 'label').value).withContext('a default variant is not named').toBe('Wire');
  });

  it('names a line by product and variant when the variant is not the default one', async () => {
    await setup();
    fixture.detectChanges();

    fillLine(0, 1);

    expect(component.lineControl(0, 'label').value).toBe('Cable — 4mm');
    expect(component.lineControl(0, 'sku').value).toBe('CAB-4MM');
  });

  // ── Currency ───────────────────────────────────────────────────────────────

  /** A new order that is otherwise ready to save, so only the currency can stop it. */
  function readyToSave() {
    pickCustomer();
    component.form.patchValue({ shippingAddressId: 'addr-1' });
    fillLine(0);
  }

  it('starts a new order in the base currency of the organization, and offers every currency by name and code', async () => {
    await setup();
    fixture.detectChanges();

    expect(currencies.getAll).toHaveBeenCalledTimes(1);
    expect(component.form.get('currencyId')!.value).toBe('cur-usd');
    expect(component.currencyOptions).toEqual([
      { label: 'US Dollar (USD)', value: 'cur-usd' },
      { label: 'Pakistani Rupee (PKR)', value: 'cur-pkr' }
    ]);
    expect(query('currency-hint')).toBeNull();
    expect(query('currency-field')!.textContent).toContain('Currency');
  });

  it('asks for a currency when the organization has no base currency, and does not save until one is chosen', async () => {
    baseCurrency = null;
    await setup();
    fixture.detectChanges();
    readyToSave();

    expect(component.form.get('currencyId')!.value).toBeNull();
    expect(query('currency-hint')!.textContent).toContain('no default currency');

    component.save();

    expect(sales.createSaleOrder).not.toHaveBeenCalled();
    expect(lastToast().severity).toBe('warn');
    expect(lastToast().detail).toBe('Choose the currency.');
    fixture.detectChanges();
    expect(query('currency-error')).not.toBeNull();

    component.form.get('currencyId')!.setValue('cur-pkr');
    component.save();

    expect(sales.createSaleOrder).toHaveBeenCalledTimes(1);
    expect((sales.createSaleOrder.calls.mostRecent().args[0] as CreateSaleOrderRequest).currencyId).toBe('cur-pkr');
  });

  it('sends the currency that was chosen rather than the default', async () => {
    await setup();
    fixture.detectChanges();
    readyToSave();
    component.form.get('currencyId')!.setValue('cur-pkr');

    expect((component.buildRequest() as CreateSaleOrderRequest).currencyId).toBe('cur-pkr');
  });

  it('takes the base currency when the organization arrives after the currency list', async () => {
    baseCurrency = null;
    await setup();
    fixture.detectChanges();
    expect(component.form.get('currencyId')!.value).toBeNull();

    tenant.set(tenantWith('cur-pkr'));
    fixture.detectChanges();

    expect(component.form.get('currencyId')!.value).toBe('cur-pkr');
  });

  it('does not replace a currency that has already been chosen when the organization arrives', async () => {
    baseCurrency = null;
    await setup();
    fixture.detectChanges();
    component.form.get('currencyId')!.setValue('cur-pkr');

    tenant.set(tenantWith('cur-usd'));
    fixture.detectChanges();

    expect(component.form.get('currencyId')!.value).toBe('cur-pkr');
  });

  it('does not preselect a base currency that the currency list does not contain', async () => {
    baseCurrency = 'cur-deleted';
    await setup();
    fixture.detectChanges();

    expect(component.form.get('currencyId')!.value).toBeNull();
  });

  it('says so when the currency list cannot be loaded', async () => {
    await setup();
    currencies.getAll.and.returnValue(throwError(() => ({ status: 500 })));
    fixture.detectChanges();

    expect(lastToast().severity).toBe('error');
    expect(lastToast().detail).toBe('The currency list could not be loaded.');
    expect(component.isLoadingCurrencies).toBeFalse();
  });

  it('keeps the currency of the draft when editing, and does not swap in the base currency', async () => {
    baseCurrency = 'cur-pkr';
    await setup(ORDER_UUID, order({ currencyId: 'cur-usd' }));
    fixture.detectChanges();

    expect(component.form.get('currencyId')!.value).toBe('cur-usd');
    expect((component.buildRequest() as UpdateSaleOrderRequest).currencyId).toBe('cur-usd');
    expect(query('currency-hint')).toBeNull();
  });

  it('lets the currency of a draft be changed', async () => {
    await setup(ORDER_UUID, order({ currencyId: 'cur-usd' }));
    fixture.detectChanges();
    component.form.get('currencyId')!.setValue('cur-pkr');

    component.save();

    expect((sales.updateSaleOrder.calls.mostRecent().args[1] as UpdateSaleOrderRequest).currencyId).toBe('cur-pkr');
  });

  // ── Editing a draft ────────────────────────────────────────────────────────

  it('loads a draft into the form, with its lines, address and the customer fixed', async () => {
    await setup(ORDER_UUID, order({ lines: [line(), line({ uuid: 'gone', variantUuid: 'v-gone', status: 'CANCELLED' })] }));
    fixture.detectChanges();

    expect(component.isEdit).toBeTrue();
    expect(sales.getSaleOrderById).toHaveBeenCalledOnceWith(ORDER_UUID);
    expect(component.form.get('deliveryMode')!.value).toBe('SHIP');
    expect(component.form.get('shippingAddressId')!.value).toBe('addr-1');
    expect(component.form.get('notes')!.value).toBe('Handle with care');
    const expected = component.form.get('expectedDeliveryDate')!.value as Date;
    expect([expected.getFullYear(), expected.getMonth(), expected.getDate()]).toEqual([2026, 8, 30]);
    expect(component.lines.length).withContext('a cancelled line is not carried over').toBe(1);
    expect(component.lineControl(0, 'quantity').value).toBe(100);
    expect(component.lineControl(0, 'label').value).toBe('4mm cable (CAB-4MM)');
    expect(component.customer!.companyName).toBe('Acme Ltd');
    expect(component.form.get('customer')!.disabled).toBeTrue();
    expect(addresses.getAddresses).toHaveBeenCalledOnceWith('cust-1');
    expect(fixture.nativeElement.textContent).toContain('SO-2026-00042');
    expect(query('save')!.textContent).toContain('Save changes');
  });

  it('shows an existing line as what it is, until Change is pressed', async () => {
    await setup(ORDER_UUID);
    fixture.detectChanges();

    expect(query('line-label')!.textContent).toContain('4mm cable (CAB-4MM)');
    expect(fixture.nativeElement.querySelector('app-product-variant-picker')).toBeNull();

    query('change-item')!.querySelector('button')!.click();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('app-product-variant-picker')).not.toBeNull();
  });

  it('sends an update with the currency, the intimation department it was loaded with, and no customer', async () => {
    await setup(ORDER_UUID);
    fixture.detectChanges();
    component.lineControl(0, 'quantity').setValue(120);

    const request = component.buildRequest() as UpdateSaleOrderRequest;

    expect(request).toEqual({
      expectedDeliveryDate: '2026-09-30',
      currencyId: 'cur-usd',
      deliveryMode: 'SHIP',
      shippingAddressId: 'addr-1',
      intimationDepartmentId: 7,
      notes: 'Handle with care',
      lines: [{ variantUuid: 'v1', quantity: 120, discountPercent: 5, taxPercent: 17 }]
    });
    expect((request as any).partnerId).toBeUndefined();
  });

  it('saves the update and goes back to the order', async () => {
    await setup(ORDER_UUID);
    fixture.detectChanges();

    component.save();

    expect(sales.updateSaleOrder).toHaveBeenCalledTimes(1);
    expect(sales.updateSaleOrder.calls.mostRecent().args[0]).toBe(ORDER_UUID);
    expect(sales.createSaleOrder).not.toHaveBeenCalled();
    expect(navigate).toHaveBeenCalledWith(['/portal/pages/sales/orders', ORDER_UUID]);
  });

  it('shows the servers reason when the update is refused', async () => {
    await setup(ORDER_UUID);
    fixture.detectChanges();
    sales.updateSaleOrder.and.returnValue(throwError(() => ({ error: { message: 'Only a draft can be edited.' } })));

    component.save();

    expect(lastToast().detail).toBe('Only a draft can be edited.');
    expect(component.isSaving).toBeFalse();
  });

  it('keeps the draft address in the list even when the address book no longer offers it', async () => {
    await setup(ORDER_UUID, order({ shippingAddressId: 'addr-old' }));
    addresses.getAddresses.and.returnValue(ok([address({ uuid: 'addr-1' })]));
    addresses.getAddress.and.returnValue(ok(address({ uuid: 'addr-old', line1: 'Old Depot' })));
    fixture.detectChanges();

    expect(addresses.getAddress).toHaveBeenCalledOnceWith('addr-old');
    expect(component.addresses.map(a => a.uuid)).toEqual(['addr-old', 'addr-1']);
    expect(component.form.get('shippingAddressId')!.value).toBe('addr-old');
  });

  it('does not fetch the draft address again when the book already lists it', async () => {
    await setup(ORDER_UUID);
    fixture.detectChanges();

    expect(addresses.getAddress).not.toHaveBeenCalled();
  });

  it('a collection draft loads with no address field', async () => {
    await setup(ORDER_UUID, order({ deliveryMode: 'SELF_PICKUP', shippingAddressId: undefined }));
    fixture.detectChanges();

    expect(component.isShip).toBeFalse();
    expect(query('address-field')).toBeNull();
    expect((component.buildRequest() as UpdateSaleOrderRequest).shippingAddressId).toBeUndefined();
  });

  it('still lets the draft be edited when the customer name cannot be fetched', async () => {
    await setup(ORDER_UUID);
    partners.getPartnerById.and.returnValue(throwError(() => ({ status: 500 })));
    fixture.detectChanges();

    expect(component.customer!.uuid).toBe('cust-1');
    expect(component.form.get('customer')!.disabled).toBeTrue();
    component.save();
    expect(sales.updateSaleOrder).toHaveBeenCalledTimes(1);
  });

  it('will not open an order that is no longer a draft', async () => {
    await setup(ORDER_UUID, order({ status: 'CONFIRMED' }));
    fixture.detectChanges();

    expect(component.notEditable).toBeTrue();
    expect(query('not-editable')).not.toBeNull();
    expect(query('save')).toBeNull();
    expect(component.lines.length).toBe(0);
  });

  it('says so when the order does not exist, and shows no form', async () => {
    await setup(ORDER_UUID, null);
    fixture.detectChanges();

    expect(query('not-found')).not.toBeNull();
    expect(query('save')).toBeNull();
  });

  it('says so when the order cannot be loaded, without calling it not found', async () => {
    await setup(ORDER_UUID);
    sales.getSaleOrderById.and.returnValue(throwError(() => ({ status: 500 })));
    fixture.detectChanges();

    expect(component.notFound).toBeFalse();
    expect(component.isLoading).toBeFalse();
    expect(lastToast().severity).toBe('error');
  });

  it('a 404 is not found', async () => {
    await setup(ORDER_UUID);
    sales.getSaleOrderById.and.returnValue(throwError(() => ({ status: 404 })));
    fixture.detectChanges();

    expect(component.notFound).toBeTrue();
  });

  // ── Navigation ─────────────────────────────────────────────────────────────

  it('goes back to the list for a new order and to the order for an edit', async () => {
    await setup();
    expect(component.backLink).toEqual(['/portal/pages/sales/orders']);

    await setup(ORDER_UUID);
    fixture.detectChanges();
    expect(component.backLink).toEqual(['/portal/pages/sales/orders', ORDER_UUID]);
  });
});
