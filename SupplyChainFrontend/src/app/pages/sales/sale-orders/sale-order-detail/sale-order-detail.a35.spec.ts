import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { SaleOrderDetailComponent } from './sale-order-detail.component';
import { SaleOrderService, SaleOrderModel, SaleOrderLineModel } from '../../../../services/sale-order.service';
import { FulfillmentRoutesService } from '../../../../services/fulfillment-routes.service';
import { TenantService } from '../../../service/tenant.service';
import { LogisticsService } from '../../../../services/logistics.service';
import { BusinessPartnerService } from '../../../../services/business-partner.service';
import { SalesInvoiceService } from '../../../../services/sales-invoice.service';
import { AddressService } from '../../../../services/address.service';
import { TimelineService } from '../../../../services/timeline.service';
import { ProductionOrderService } from '../../../../services/production-order.service';
import { AttachmentService } from '../../../../services/attachment.service';
import { AttachmentPolicyService } from '../../../../services/attachment-policy.service';
import { AuthService } from '../../../service/auth.service';
import { provideTestOrgCurrencies } from '../../../../shared/doc-currency/doc-currency.testing';

// A35 P3-13 — sale order detail in another currency (FSD §11.5): currency, the rate locked at confirmation with its date,
// the base currency, the line and header totals in both currencies, the "Show in AED / Show in PKR" toggle, and the D-5
// missing-rate refusal on confirm. Contract: docs/multi-currency/API-CONTRACT.md §6.

const UUID = '35353535-3535-3535-3535-353535353535';
const ALL = ['SALE_ORDER_VIEW', 'SALE_ORDER_EDIT', 'SALE_ORDER_CONFIRM', 'SALE_ORDER_CANCEL', 'DELIVERY_VIEW'];
const MISSING = 'No exchange rate for AED on 2026-10-07. Add one under Settings → Exchange Rates.';

function line(overrides: Partial<SaleOrderLineModel> = {}): SaleOrderLineModel {
  return {
    uuid: 'l1', variantUuid: 'v1', itemDescription: 'Widget Pro', quantity: 50, unitPrice: 120, discountPercent: 0,
    taxPercent: 5, lineTotal: 6000, fulfilledQty: 0, invoicedQty: 0, status: 'OPEN', routeSource: 'ORG_DEFAULT',
    effectiveRouteSteps: [], ...overrides
  };
}

const LOCKED: Partial<SaleOrderModel> = {
  status: 'CONFIRMED', currencyId: 'cur-aed', currencyCode: 'AED', exchangeRate: 76.3, baseCurrencyId: 'cur-pkr',
  baseCurrencyCode: 'PKR', rateLockedAt: '2026-10-07T09:12:00Z',
  subtotal: 10550, taxAmount: 527.5, discountAmount: 0, grandTotal: 11077.5,
  subtotalBase: 804965, taxAmountBase: 40248.25, discountAmountBase: 0, grandTotalBase: 845213.25,
  lines: [
    line({ unitPriceBase: 9156, lineTotalBase: 457800 }),
    line({ uuid: 'l2', variantUuid: 'v2', itemDescription: 'Gadget Lite', quantity: 100, unitPrice: 45.5, lineTotal: 4550,
           unitPriceBase: 3471.65, lineTotalBase: 347165 })
  ]
};

function order(overrides: Partial<SaleOrderModel> = {}): SaleOrderModel {
  return {
    uuid: UUID, traceId: 't-1', soNumber: 'SO-2026-0501', partnerId: 'p-1', orderDate: '2026-10-07T00:00:00',
    currencyId: 'cur-aed', currencyCode: 'AED', subtotal: 10550, taxAmount: 527.5, discountAmount: 0, grandTotal: 11077.5,
    status: 'DRAFT', requiresShipment: false, deliveryMode: 'SELF_PICKUP', createdDate: '2026-10-07T00:00:00',
    sourceType: 'MANUAL', lines: [line()], routesEnabled: false, confirmBlockers: [], productionOrders: [],
    exchangeRate: null, baseCurrencyId: null, baseCurrencyCode: null, rateLockedAt: null,
    ...overrides
  };
}

function ok<T>(result: T) {
  return of({ success: true, message: '', result } as any);
}

describe('SaleOrderDetailComponent — A35 currency and dual amounts', () => {
  let fixture: ComponentFixture<SaleOrderDetailComponent>;
  let component: SaleOrderDetailComponent;
  let service: jasmine.SpyObj<SaleOrderService>;
  let toasts: jasmine.Spy;

  const auth = { hasPermission: (code: string) => ALL.includes(code) } as unknown as AuthService;
  const q = (id: string): HTMLElement | null => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);
  const qa = (id: string): HTMLElement[] => Array.from(fixture.nativeElement.querySelectorAll(`[data-testid="${id}"]`));
  const text = (el: Element | null) => (el?.textContent ?? '').replace(/\s+/g, ' ').trim();

  async function setup(model: SaleOrderModel) {
    service = jasmine.createSpyObj<SaleOrderService>('SaleOrderService', [
      'getSaleOrderById', 'getDeliveries', 'confirmSaleOrder', 'getAvailability', 'checkCustomerPo', 'getDeliveryPreview'
    ]);
    service.getSaleOrderById.and.returnValue(ok(model));
    service.getDeliveries.and.returnValue(ok([]));
    service.getAvailability.and.returnValue(ok([]));
    service.checkCustomerPo.and.returnValue(ok([]));
    service.getDeliveryPreview.and.returnValue(ok({ routesEnabled: false, canConfirm: true, deliveryCount: 0, lines: [], groups: [], blockers: [] }));

    const routes = jasmine.createSpyObj<FulfillmentRoutesService>('FulfillmentRoutesService', ['getRoutes']);
    routes.getRoutes.and.returnValue(ok([]));
    const partners = jasmine.createSpyObj<BusinessPartnerService>('BusinessPartnerService', ['getPartnerById']);
    partners.getPartnerById.and.returnValue(ok({ uuid: 'p-1', companyName: 'Al Rashid Trading LLC' }));
    const invoices = jasmine.createSpyObj<SalesInvoiceService>('SalesInvoiceService', ['getInvoices', 'getInvoice']);
    invoices.getInvoices.and.returnValue(ok({ data: [], totalRecords: 0, page: 1, pageSize: 100, totalPages: 0 }));
    const addresses = jasmine.createSpyObj<AddressService>('AddressService', ['getAddress']);
    addresses.getAddress.and.returnValue(ok(null));
    const timeline = jasmine.createSpyObj<TimelineService>('TimelineService', ['getByTraceId', 'getByDocument']);
    timeline.getByTraceId.and.returnValue(ok(null));
    const production = jasmine.createSpyObj<ProductionOrderService>('ProductionOrderService', ['getList']);
    production.getList.and.returnValue(ok({ data: [], totalRecords: 0, page: 1, pageSize: 100, totalPages: 0 }));
    const attachments = jasmine.createSpyObj<AttachmentService>('AttachmentService', ['getAttachments', 'isApiUrl']);
    attachments.getAttachments.and.returnValue(ok([]));
    attachments.isApiUrl.and.returnValue(false);
    const policy = jasmine.createSpyObj<AttachmentPolicyService>('AttachmentPolicyService', ['ruleFor']);
    policy.ruleFor.and.returnValue(of(null));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [SaleOrderDetailComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), MessageService,
        provideTestOrgCurrencies(),
        { provide: SaleOrderService, useValue: service },
        { provide: FulfillmentRoutesService, useValue: routes },
        { provide: LogisticsService, useValue: jasmine.createSpyObj('LogisticsService', ['getDeliveryById']) },
        { provide: BusinessPartnerService, useValue: partners },
        { provide: SalesInvoiceService, useValue: invoices },
        { provide: AddressService, useValue: addresses },
        { provide: TimelineService, useValue: timeline },
        { provide: ProductionOrderService, useValue: production },
        { provide: AttachmentService, useValue: attachments },
        { provide: AttachmentPolicyService, useValue: policy },
        { provide: AuthService, useValue: auth },
        { provide: TenantService, useValue: { tenant: signal({ id: 'org-1', enabledFeatureCodes: ['MODULE_DEMAND'] }) } },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['uuid', UUID]]) } } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(SaleOrderDetailComponent);
    component = fixture.componentInstance;
    toasts = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
    fixture.detectChanges();
  }

  it('shows the currency, the rate locked at confirmation with its date, and the base currency', async () => {
    await setup(order(LOCKED));
    expect(text(q('dc-currency'))).toBe('AED - UAE Dirham');
    expect(text(q('dc-rate'))).toBe('76.3000 (locked 7 Oct 2026)');
    expect(text(q('dc-base'))).toBe('PKR');
  });

  it('puts each line total and the header totals in both currencies, the base ones as the server sent them', async () => {
    await setup(order(LOCKED));
    expect(qa('line-total-primary').map(text)).toEqual(['AED 6,000.00', 'AED 4,550.00']);
    expect(qa('line-total-secondary').map(text)).toEqual(['PKR 457,800.00', 'PKR 347,165.00']);
    expect(text(q('total-primary-header'))).toBe('Total (AED)');
    expect(text(q('total-secondary-header'))).toBe('Total (PKR)');
    expect(text(q('order-subtotal'))).toBe('AED 10,550.00 PKR 804,965.00');
    expect(text(q('order-tax'))).toBe('AED 527.50 PKR 40,248.25');
    expect(text(q('order-grand-total'))).toBe('AED 11,077.50 PKR 845,213.25');
  });

  it('"Show in PKR" puts the base amounts first: prices, line totals and header totals', async () => {
    await setup(order(LOCKED));
    const toPkr = Array.from(q('dc-toggle')!.querySelectorAll('button'))[1] as HTMLButtonElement;
    toPkr.click();
    fixture.detectChanges();

    expect(component.amountView).toBe('BASE');
    expect(qa('line-unit-price').map(text)).toEqual(['PKR 9,156.00', 'PKR 3,471.65']);
    expect(qa('line-total-primary').map(text)).toEqual(['PKR 457,800.00', 'PKR 347,165.00']);
    expect(qa('line-total-secondary').map(text)).toEqual(['AED 6,000.00', 'AED 4,550.00']);
    expect(text(q('total-primary-header'))).toBe('Total (PKR)');
    expect(text(q('order-grand-total'))).toBe('PKR 845,213.25 AED 11,077.50');
  });

  it('a draft has no rate yet: no base column, no toggle, and says the rate locks at confirmation', async () => {
    await setup(order());
    expect(text(q('dc-rate'))).toBe('Locked at confirmation');
    expect(text(q('dc-base'))).withContext('the sale base, from the org currencies').toBe('PKR');
    expect(q('dc-toggle')).toBeNull();
    expect(q('total-secondary-header')).toBeNull();
    expect(qa('line-total-primary').map(text)).toEqual(['AED 6,000.00']);
    expect(text(q('order-grand-total'))).toBe('AED 11,077.50');
  });

  it('an order in the base currency shows one set of amounts', async () => {
    await setup(order({
      ...LOCKED, currencyId: 'cur-pkr', currencyCode: 'PKR', exchangeRate: 1, subtotal: 804965, grandTotal: 845213.25,
      taxAmount: 40248.25, subtotalBase: 804965, grandTotalBase: 845213.25
    }));
    expect(q('dc-toggle')).toBeNull();
    expect(q('total-secondary-header')).toBeNull();
    expect(text(q('order-grand-total'))).toBe('PKR 845,213.25');
  });

  it('confirming without a rate says so plainly and points to the exchange rates (D-5)', async () => {
    await setup(order());
    service.confirmSaleOrder.and.returnValue(throwError(() => ({ status: 400, error: { success: false, message: MISSING } })));
    component.confirmDialogVisible = true;
    component.confirmOrder();
    fixture.detectChanges();

    expect(component.missingRate?.code).toBe('AED');
    const toast = toasts.calls.mostRecent().args[0];
    expect(toast.summary).toBe('No exchange rate');
    expect(toast.detail).toBe(MISSING);
    expect(component.confirmDialogVisible).withContext('the dialog stays open').toBeTrue();
  });
});
