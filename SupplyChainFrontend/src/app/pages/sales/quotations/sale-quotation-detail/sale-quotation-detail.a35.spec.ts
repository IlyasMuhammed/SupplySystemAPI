import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { signal } from '@angular/core';
import { MessageService } from 'primeng/api';

import { SaleQuotationDetailComponent } from './sale-quotation-detail.component';
import { SalesPreorderService, SaleQuotation } from '../../../../services/sales-preorder.service';
import { SaleOrderService } from '../../../../services/sale-order.service';
import { AddressService } from '../../../../services/address.service';
import { AttachmentService } from '../../../../services/attachment.service';
import { InventoryService } from '../../../../services/inventory.service';
import { FinanceSetupService } from '../../../../services/finance-setup.service';
import { PricingRuleService } from '../../../../services/pricing-rule.service';
import { TenantService } from '../../../service/tenant.service';
import { AuthService } from '../../../service/auth.service';
import { provideTestOrgCurrencies } from '../../../../shared/doc-currency/doc-currency.testing';
import { ok, fail, quotation, qLine } from '../sale-quotation.fixtures.spec';

// A35 — customer quotation: currency, the rate locked when SENT, the sale base, each line's total in the base (and their
// sum), and the D-5 refusal on send.

const MISSING = 'No exchange rate for AED on 2026-10-07. Add one under Settings → Exchange Rates.';

function sentInAed(): SaleQuotation {
  return quotation({
    status: 'SENT', isEditable: false, allowedActions: [], currencyId: 'cur-aed', currencyCode: 'AED', exchangeRate: 76.3,
    baseCurrencyId: 'cur-pkr', baseCurrencyCode: 'PKR', rateLockedAt: '2026-10-07T09:12:00Z', sentAt: '2026-10-07T09:12:00Z',
    subtotal: 10550, grandTotal: 10550,
    lines: [
      qLine({ unitPrice: 120, quantity: 50, lineTotal: 6000, unitPriceBase: 9156, lineTotalBase: 457800 }),
      qLine({ uuid: 'l-2', lineNumber: 2, unitPrice: 45.5, quantity: 100, lineTotal: 4550, unitPriceBase: 3471.65, lineTotalBase: 347165 })
    ]
  });
}

describe('SaleQuotationDetailComponent — A35 currency', () => {
  let fixture: ComponentFixture<SaleQuotationDetailComponent>;
  let component: SaleQuotationDetailComponent;
  let service: jasmine.SpyObj<SalesPreorderService>;
  let toasts: jasmine.Spy;

  const q = (id: string): HTMLElement | null => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);
  const qa = (id: string): HTMLElement[] => Array.from(fixture.nativeElement.querySelectorAll(`[data-testid="${id}"]`));
  const text = (el: Element | null) => (el?.textContent ?? '').replace(/\s+/g, ' ').trim();

  async function setup(model: SaleQuotation) {
    service = jasmine.createSpyObj<SalesPreorderService>('SalesPreorderService', ['getQuotation', 'sendQuotation', 'getRejectionReasons']);
    service.getQuotation.and.returnValue(ok(model));
    service.getRejectionReasons.and.returnValue(ok([]));
    const saleOrders = jasmine.createSpyObj<SaleOrderService>('SaleOrderService', ['getDefaults']);
    saleOrders.getDefaults.and.returnValue(ok({ deliveryMode: 'SHIP', selfPickupEnabled: true }));
    const addresses = jasmine.createSpyObj<AddressService>('AddressService', ['getAddresses']);
    addresses.getAddresses.and.returnValue(ok([]));
    const attachments = jasmine.createSpyObj<AttachmentService>('AttachmentService', ['getAttachments', 'resolveUrl', 'isApiUrl']);
    attachments.getAttachments.and.returnValue(ok([]));
    const inventory = jasmine.createSpyObj<InventoryService>('InventoryService', ['getProducts']);
    inventory.getProducts.and.returnValue(ok({ data: [], totalRecords: 0 }));
    const setupSvc = jasmine.createSpyObj<FinanceSetupService>('FinanceSetupService', ['getTaxCodes']);
    setupSvc.getTaxCodes.and.returnValue(ok([]));

    await TestBed.configureTestingModule({
      imports: [SaleQuotationDetailComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), MessageService,
        provideTestOrgCurrencies(),
        { provide: SalesPreorderService, useValue: service },
        { provide: SaleOrderService, useValue: saleOrders },
        { provide: AddressService, useValue: addresses },
        { provide: AttachmentService, useValue: attachments },
        { provide: InventoryService, useValue: inventory },
        { provide: FinanceSetupService, useValue: setupSvc },
        { provide: PricingRuleService, useValue: jasmine.createSpyObj('PricingRuleService', ['resolvePrice']) },
        { provide: TenantService, useValue: { tenant: signal({ id: 'org-1', baseCurrency: 'cur-pkr' }), hasFeature: () => true } },
        { provide: AuthService, useValue: { hasPermission: () => true, hasAnyPermission: () => true } },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['uuid', 'sq-1']]) } } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(SaleQuotationDetailComponent);
    component = fixture.componentInstance;
    toasts = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
    fixture.detectChanges();
  }

  it('shows the currency, the rate locked when sent, the base, and the grand total in the base', async () => {
    await setup(sentInAed());
    expect(text(q('dc-currency'))).toBe('AED - UAE Dirham');
    expect(text(q('dc-rate'))).toBe('76.3000 (locked 7 Oct 2026)');
    expect(text(q('dc-base'))).toBe('PKR');
    expect(text(q('quotation-base-total'))).toBe('PKR 804,965.00');
  });

  it('shows each line\'s total in the base', async () => {
    await setup(sentInAed());
    expect(qa('line-total-base').map(text)).toEqual(['PKR 457,800.00', 'PKR 347,165.00']);
  });

  it('a draft says the rate locks when sent, with no base column', async () => {
    await setup(quotation({ currencyId: 'cur-aed', currencyCode: 'AED' }));
    expect(text(q('dc-rate'))).toBe('Locked at sending');
    expect(qa('line-total-base').length).toBe(0);
    expect(q('quotation-base-total')).toBeNull();
  });

  it('sending without a rate says so plainly (D-5)', async () => {
    await setup(quotation({ currencyId: 'cur-aed', currencyCode: 'AED' }));
    service.sendQuotation.and.returnValue(fail(400, MISSING));
    component.confirmSend();
    const toast = toasts.calls.mostRecent().args[0];
    expect(toast.summary).toBe('No exchange rate');
    expect(toast.detail).toBe(MISSING);
  });
});
