import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute, convertToParamMap } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { SupplierPaymentDetailComponent } from './supplier-payment-detail.component';
import { FinanceService, SupplierPaymentDetailModel } from '../../../../services/finance.service';
import { AuthService } from '../../../service/auth.service';
import { TenantService } from '../../../service/tenant.service';
import { provideTestOrgCurrencies } from '../../../../shared/doc-currency/doc-currency.testing';

// A35-E-06 — supplier payment: currency, the rate locked at posting (purchase base), the amount in the base, the
// realized difference per line and in total, and the D-5 refusal on post (contract v1.4 §7).

const MISSING = 'No exchange rate for AED on 2026-10-20. Add one under Settings → Exchange Rates.';

function payment(overrides: Partial<SupplierPaymentDetailModel> = {}): SupplierPaymentDetailModel {
  return {
    uuid: 'pay-1', paymentNumber: 'SPAY-2026-00001', supplierId: 'sup-1', supplierName: 'Gulf Metals', paymentDate: '2026-10-20T00:00:00',
    paymentMethod: 'BANK', totalAmount: 1000, status: 'POSTED', createdBy: 1, createdDate: '2026-10-20T09:00:00', paymentType: 'STANDARD',
    currencyCode: 'AED', currencyId: 'cur-aed', exchangeRate: 0.2738, baseCurrencyId: 'cur-usd', baseCurrencyCode: 'USD',
    amountBase: 273.8, exchangeDifference: -1.5,
    lines: [{ uuid: 'l-1', invoiceUuid: 'inv-3', invoiceNumber: 'INV-2026-00003', allocatedAmount: 1000, outstandingBeforeAllocation: 1000,
              exchangeDifference: -1.5 }],
    ...overrides
  };
}

describe('SupplierPaymentDetailComponent — A35 currency and exchange differences', () => {
  let fixture: ComponentFixture<SupplierPaymentDetailComponent>;
  let finance: jasmine.SpyObj<FinanceService>;

  const q = (id: string): HTMLElement | null => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);
  const qa = (id: string): HTMLElement[] => Array.from(fixture.nativeElement.querySelectorAll(`[data-testid="${id}"]`));
  const text = (el: Element | null) => (el?.textContent ?? '').replace(/\s+/g, ' ').trim();

  async function setup(p: SupplierPaymentDetailModel) {
    finance = jasmine.createSpyObj<FinanceService>('FinanceService', ['getSupplierPaymentById', 'postSupplierPayment']);
    finance.getSupplierPaymentById.and.returnValue(of({ success: true, message: '', result: p }));
    await TestBed.configureTestingModule({
      imports: [SupplierPaymentDetailComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), provideTestOrgCurrencies(),
        { provide: FinanceService, useValue: finance },
        { provide: AuthService, useValue: { hasPermission: () => true } },
        { provide: TenantService, useValue: { tenant: signal({ id: 'org-1' }) } },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ uuid: p.uuid }) } } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(SupplierPaymentDetailComponent);
    fixture.detectChanges();
  }

  it('shows the currency, the rate locked at posting, the purchase base and the amount in the base', async () => {
    await setup(payment());
    expect(text(q('dc-currency'))).toBe('AED - UAE Dirham');
    expect(text(q('dc-rate'))).toBe('0.2738 (locked)');
    expect(text(q('dc-base'))).toBe('USD');
    expect(text(q('amount-base'))).toBe('USD 273.80');
  });

  it('shows the realized difference per line and in total', async () => {
    await setup(payment());
    expect(qa('line-fx').map(text)).toEqual(['-USD 1.50']);
    expect(text(q('payment-fx'))).toBe('-USD 1.50');
  });

  it('a draft (no rate yet) shows when it locks and no base figures', async () => {
    await setup(payment({ status: 'APPROVED', exchangeRate: null, baseCurrencyId: null, baseCurrencyCode: null, amountBase: null,
                          exchangeDifference: null, lines: [{ ...payment().lines[0], exchangeDifference: null }] }));
    expect(text(q('dc-rate'))).toBe('Locked at posting');
    expect(q('amount-base')).toBeNull();
    expect(qa('line-fx').length).toBe(0);
  });

  it('posting without a rate says so plainly (D-5)', async () => {
    await setup(payment({ status: 'APPROVED', exchangeRate: null }));
    finance.postSupplierPayment.and.returnValue(throwError(() => ({ status: 400, error: { message: MISSING } })));
    const toasts = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
    (fixture.componentInstance as any).runAction(finance.postSupplierPayment('pay-1'), 'Payment posted.');
    expect(toasts.calls.mostRecent().args[0].summary).toBe('No exchange rate');
  });
});
