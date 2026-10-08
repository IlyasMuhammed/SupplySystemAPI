import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { SalesInvoiceDetailComponent } from './sales-invoice-detail.component';
import { SalesInvoiceService, SalesInvoiceDetailModel } from '../../../../services/sales-invoice.service';
import { AttachmentService } from '../../../../services/attachment.service';
import { AuthService } from '../../../service/auth.service';
import { TenantService } from '../../../service/tenant.service';
import { provideTestOrgCurrencies } from '../../../../shared/doc-currency/doc-currency.testing';

// A35-E-06 — sales invoice: currency, the rate locked at issue, the base, the realized exchange difference of each
// payment applied and in total, and the D-5 refusal on issue. Contract §7.

const UUID = '35000000-0000-0000-0000-00000000005a';
const MISSING = 'No exchange rate for AED on 2026-10-07. Add one under Settings → Exchange Rates.';

function invoice(overrides: Partial<SalesInvoiceDetailModel> = {}): SalesInvoiceDetailModel {
  return {
    uuid: UUID, invoiceNumber: 'SINV-2026-00041', saleOrderUuid: 'so-1', saleOrderNumber: 'SO-2026-0501', partnerId: 'p-1',
    partnerName: 'Al Rashid Trading LLC', invoiceDate: '2026-10-07T00:00:00Z', dueDate: '2026-11-06T00:00:00Z',
    grandTotal: 11077.5, amountPaid: 10550, balanceDue: 527.5, status: 'PARTIALLY_PAID', currencyCode: 'AED',
    currencyId: 'cur-aed', exchangeRate: 76.3, baseCurrencyId: 'cur-pkr', baseCurrencyCode: 'PKR', baseGrandTotal: 845213.25,
    exchangeRateLockedAt: '2026-10-07T09:12:00Z', traceId: 't-1', subtotal: 10550, discountAmount: 0, taxAmount: 527.5,
    createdDate: '2026-10-07T09:00:00Z', lines: [],
    payments: [{
      allocationUuid: 'a-1', paymentUuid: 'cp-7', paymentNumber: 'RCPT-2026-00007', paymentDate: '2026-10-14T00:00:00Z',
      paymentMethod: 'BANK_TRANSFER', paymentStatus: 'RECEIVED', allocatedAmount: 10550, allocatedAt: '2026-10-14T09:12:00Z',
      allocatedBy: 1, exchangeDifference: 1582.5
    }],
    realizedExchangeDifference: 1582.5,
    ...overrides
  } as SalesInvoiceDetailModel;
}

describe('SalesInvoiceDetailComponent — A35 currency and exchange differences', () => {
  let fixture: ComponentFixture<SalesInvoiceDetailComponent>;
  let component: SalesInvoiceDetailComponent;
  let invoices: jasmine.SpyObj<SalesInvoiceService>;
  let toasts: jasmine.Spy;

  const q = (id: string): HTMLElement | null => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);
  const qa = (id: string): HTMLElement[] => Array.from(fixture.nativeElement.querySelectorAll(`[data-testid="${id}"]`));
  const text = (el: Element | null) => (el?.textContent ?? '').replace(/\s+/g, ' ').trim();

  async function setup(model: SalesInvoiceDetailModel) {
    invoices = jasmine.createSpyObj<SalesInvoiceService>('SalesInvoiceService', ['getInvoice', 'issueInvoice']);
    invoices.getInvoice.and.returnValue(of({ success: true, message: '', result: model } as any));
    const attachments = jasmine.createSpyObj<AttachmentService>('AttachmentService', ['getAttachments', 'resolveUrl', 'isApiUrl']);
    attachments.getAttachments.and.returnValue(of({ success: true, result: [] } as any));
    await TestBed.configureTestingModule({
      imports: [SalesInvoiceDetailComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), MessageService,
        provideTestOrgCurrencies(),
        { provide: SalesInvoiceService, useValue: invoices },
        { provide: AttachmentService, useValue: attachments },
        { provide: AuthService, useValue: { hasPermission: () => true } },
        { provide: TenantService, useValue: { tenant: signal({ id: 'org-1' }) } },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['uuid', UUID]]) } } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(SalesInvoiceDetailComponent);
    component = fixture.componentInstance;
    toasts = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
    fixture.detectChanges();
  }

  it('shows the currency, the rate locked at issue and the base', async () => {
    await setup(invoice());
    expect(text(q('dc-currency'))).toBe('AED - UAE Dirham');
    expect(text(q('dc-rate'))).toBe('76.3000 (locked 7 Oct 2026)');
    expect(text(q('dc-base'))).toBe('PKR');
    expect(q('dc-toggle')).withContext('the base total has its own row').toBeNull();
  });

  it('shows each payment\'s realized difference and the total', async () => {
    await setup(invoice());
    expect(qa('payment-fx').map(text)).toEqual(['+PKR 1,582.50']);
    expect(text(q('realized-fx'))).toBe('+PKR 1,582.50');
  });

  it('no difference fields (older server, or an invoice in the base): no column and no total', async () => {
    await setup(invoice({ realizedExchangeDifference: undefined, payments: [{ ...invoice().payments[0], exchangeDifference: undefined }] }));
    expect(qa('payment-fx').length).toBe(0);
    expect(q('realized-fx')).toBeNull();
  });

  it('issuing without a rate says so plainly (D-5)', async () => {
    await setup(invoice({ status: 'DRAFT', exchangeRate: null, baseGrandTotal: null, exchangeRateLockedAt: null, payments: [] }));
    invoices.issueInvoice.and.returnValue(throwError(() => ({ status: 400, error: { success: false, message: MISSING } })));
    component.issue();
    const toast = toasts.calls.mostRecent().args[0];
    expect(toast.summary).toBe('No exchange rate');
    expect(toast.detail).toBe(MISSING);
  });
});
