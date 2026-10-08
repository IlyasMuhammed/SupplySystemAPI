import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute, convertToParamMap } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { InvoiceDetailComponent } from './invoice-detail.component';
import { FinanceService, InvoiceDetailModel, SupplierPaymentListItemModel } from '../../../../services/finance.service';
import { FinanceSetupService } from '../../../../services/finance-setup.service';
import { AuthService } from '../../../service/auth.service';
import { TenantService } from '../../../service/tenant.service';
import { provideTestOrgCurrencies } from '../../../../shared/doc-currency/doc-currency.testing';

// A35-E-06 — supplier invoice: currency, the rate locked at approval against the purchase base, each supplier payment's
// realized difference (supplierPayments[], contract v1.4 §7) and the total, and the D-5 refusal on approve.

const MISSING = 'No exchange rate for AED on 2026-10-07. Add one under Settings → Exchange Rates.';

function ok<T>(result: T) {
  return of({ success: true, message: '', result } as any);
}

function invoice(overrides: Partial<InvoiceDetailModel> = {}): InvoiceDetailModel {
  return {
    uuid: 'inv-3', invoiceNumber: 'INV-2026-00003', supplierId: 'sup-1', supplierName: 'Gulf Metals', invoiceDate: '2026-10-07T00:00:00',
    receivedDate: '2026-10-07T00:00:00', dueDate: '2026-11-06T00:00:00', currency: 'AED', subtotal: 1000, taxAmount: 0,
    totalAmount: 1000, matchedPoValue: 1000, matchedGrnValue: 1000, varianceAmount: 0, matchStatus: 'Approved',
    paymentStatus: 'Paid', paidAmount: 1000, approvedAt: '2026-10-07T10:00:00', createdDate: '2026-10-07T09:00:00',
    currencyId: 'cur-aed', exchangeRate: 0.2723, baseCurrencyId: 'cur-usd', baseCurrencyCode: 'USD', baseTotalAmount: 272.3,
    exchangeRateLockedAt: '2026-10-07T10:00:00Z', realizedExchangeDifference: -1.5,
    supplierPayments: [{ paymentUuid: 'pay-1', paymentNumber: 'SPAY-2026-00001', paymentDate: '2026-10-20', paymentMethod: 'BANK',
                         status: 'POSTED', currencyCode: 'AED', allocatedAmount: 1000, exchangeDifference: -1.5 }],
    lines: [], payments: [], debitNotes: [], creditNotes: [],
    ...overrides
  };
}

const PAYMENT: SupplierPaymentListItemModel = {
  uuid: 'pay-1', paymentNumber: 'SPAY-2026-00001', supplierId: 'sup-1', supplierName: 'Gulf Metals', paymentDate: '2026-10-20',
  paymentMethod: 'BANK', totalAmount: 1000, status: 'POSTED', paymentType: 'STANDARD', lineCount: 1, attachmentCount: 0
};

describe('InvoiceDetailComponent — A35 currency and exchange differences', () => {
  let fixture: ComponentFixture<InvoiceDetailComponent>;
  let component: InvoiceDetailComponent;
  let finance: jasmine.SpyObj<FinanceService>;
  let toasts: jasmine.Spy;

  const q = (id: string): HTMLElement | null => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);
  const qa = (id: string): HTMLElement[] => Array.from(fixture.nativeElement.querySelectorAll(`[data-testid="${id}"]`));
  const text = (el: Element | null) => (el?.textContent ?? '').replace(/\s+/g, ' ').trim();

  async function setup(inv: InvoiceDetailModel) {
    finance = jasmine.createSpyObj<FinanceService>('FinanceService', ['getInvoiceById', 'getSupplierPayments', 'approveInvoice']);
    finance.getInvoiceById.and.returnValue(ok(inv));
    finance.getSupplierPayments.and.returnValue(ok({ data: [PAYMENT], totalRecords: 1 }));
    const setupService = jasmine.createSpyObj<FinanceSetupService>('FinanceSetupService', ['getTaxCodes']);
    setupService.getTaxCodes.and.returnValue(ok([]));
    await TestBed.configureTestingModule({
      imports: [InvoiceDetailComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), provideTestOrgCurrencies(),
        { provide: FinanceService, useValue: finance },
        { provide: FinanceSetupService, useValue: setupService },
        { provide: AuthService, useValue: { hasPermission: () => true } },
        { provide: TenantService, useValue: { tenant: signal({ id: 'org-1' }) } },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ uuid: inv.uuid }) } } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(InvoiceDetailComponent);
    component = fixture.componentInstance;
    toasts = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
    fixture.detectChanges();
  }

  it('shows the currency, the rate locked at approval and the purchase base', async () => {
    await setup(invoice());
    expect(text(q('dc-currency'))).toBe('AED - UAE Dirham');
    expect(text(q('dc-rate'))).toBe('0.2723 (locked 7 Oct 2026)');
    expect(text(q('dc-base'))).toBe('USD');
  });

  it('shows each supplier payment\'s realized difference and the total', async () => {
    await setup(invoice());
    expect(qa('supplier-payment-fx').map(text)).toEqual(['-USD 1.50']);
    expect(text(q('realized-fx'))).toBe('-USD 1.50');
  });

  it('no difference fields: no column, no total', async () => {
    await setup(invoice({ realizedExchangeDifference: undefined, supplierPayments: undefined }));
    expect(qa('supplier-payment-fx').length).toBe(0);
    expect(q('realized-fx')).toBeNull();
  });

  it('approving without a rate says so plainly (D-5)', async () => {
    await setup(invoice({ matchStatus: 'Matched', approvedAt: undefined, exchangeRate: null, baseTotalAmount: null }));
    finance.approveInvoice.and.returnValue(throwError(() => ({ status: 400, error: { success: false, message: MISSING } })));
    component.approve();
    const toast = toasts.calls.mostRecent().args[0];
    expect(toast.summary).toBe('No exchange rate');
    expect(toast.detail).toBe(MISSING);
  });
});
