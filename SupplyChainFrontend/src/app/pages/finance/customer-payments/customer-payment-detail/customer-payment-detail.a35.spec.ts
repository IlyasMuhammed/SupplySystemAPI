import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { CustomerPaymentDetailComponent } from './customer-payment-detail.component';
import { CustomerPaymentService, CustomerPaymentDetailModel } from '../../../../services/customer-payment.service';
import { SalesInvoiceService } from '../../../../services/sales-invoice.service';
import { AuthService } from '../../../service/auth.service';
import { TenantService } from '../../../service/tenant.service';
import { provideTestOrgCurrencies } from '../../../../shared/doc-currency/doc-currency.testing';

// A35-E-06 — customer payment: currency, the rate locked at posting, the amount in the sale base, and the realized
// exchange difference per allocation and in total (contract v1.4 §7).

const UUID = 'cp-7';

function payment(overrides: Partial<CustomerPaymentDetailModel> = {}): CustomerPaymentDetailModel {
  return {
    uuid: UUID, paymentNumber: 'RCPT-2026-00007', partnerId: 'p-1', partnerName: 'Al Rashid Trading LLC',
    paymentDate: '2026-10-14T00:00:00Z', amount: 10550, allocatedAmount: 10550, unallocatedAmount: 0, paymentMethod: 'BANK_TRANSFER',
    currencyCode: 'AED', status: 'RECEIVED', createdBy: 1, createdDate: '2026-10-14T09:12:00Z',
    currencyId: 'cur-aed', exchangeRate: 76.45, baseCurrencyId: 'cur-pkr', baseCurrencyCode: 'PKR', amountBase: 806547.5,
    exchangeDifference: 1582.5,
    allocations: [{
      allocationUuid: 'a-1', invoiceUuid: 'si-1', invoiceNumber: 'SINV-2026-00041', allocatedAmount: 10550,
      allocatedAt: '2026-10-14T09:12:00Z', allocatedBy: 1, invoiceBalanceDue: 527.5, invoiceStatus: 'PARTIALLY_PAID',
      exchangeDifference: 1582.5
    }],
    ...overrides
  };
}

describe('CustomerPaymentDetailComponent — A35 currency and exchange differences', () => {
  let fixture: ComponentFixture<CustomerPaymentDetailComponent>;

  const q = (id: string): HTMLElement | null => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);
  const qa = (id: string): HTMLElement[] => Array.from(fixture.nativeElement.querySelectorAll(`[data-testid="${id}"]`));
  const text = (el: Element | null) => (el?.textContent ?? '').replace(/\s+/g, ' ').trim();

  async function setup(model: CustomerPaymentDetailModel) {
    const payments = jasmine.createSpyObj<CustomerPaymentService>('CustomerPaymentService', ['getPayment', 'allocatePayment']);
    payments.getPayment.and.returnValue(of({ success: true, message: '', result: model } as any));
    const invoices = jasmine.createSpyObj<SalesInvoiceService>('SalesInvoiceService', ['getOpenInvoices']);
    invoices.getOpenInvoices.and.returnValue(of({ invoices: [], truncated: false } as any));
    await TestBed.configureTestingModule({
      imports: [CustomerPaymentDetailComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), provideTestOrgCurrencies(),
        { provide: CustomerPaymentService, useValue: payments },
        { provide: SalesInvoiceService, useValue: invoices },
        { provide: AuthService, useValue: { hasPermission: () => true } },
        { provide: TenantService, useValue: { tenant: signal({ id: 'org-1' }) } },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['uuid', UUID]]) } } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(CustomerPaymentDetailComponent);
    fixture.detectChanges();
  }

  it('shows the currency, the rate locked at posting, the base, and the amount in the base', async () => {
    await setup(payment());
    expect(text(q('dc-currency'))).toBe('AED - UAE Dirham');
    expect(text(q('dc-rate'))).toBe('76.4500 (locked)');
    expect(text(q('dc-base'))).toBe('PKR');
    expect(text(q('amount-base'))).toBe('PKR 806,547.50');
  });

  it('shows the realized difference per allocation and in total', async () => {
    await setup(payment());
    expect(qa('allocation-fx').map(text)).toEqual(['+PKR 1,582.50']);
    expect(text(q('payment-fx'))).toBe('+PKR 1,582.50');
  });

  it('a payment in the base currency (or from an older server) shows none of it', async () => {
    await setup(payment({
      currencyId: 'cur-pkr', currencyCode: 'PKR', exchangeRate: 1, amountBase: 10550, exchangeDifference: null,
      allocations: [{ ...payment().allocations[0], exchangeDifference: null }]
    }));
    expect(q('amount-base')).toBeNull();
    expect(qa('allocation-fx').length).toBe(0);
    expect(q('payment-fx')).toBeNull();
  });
});
