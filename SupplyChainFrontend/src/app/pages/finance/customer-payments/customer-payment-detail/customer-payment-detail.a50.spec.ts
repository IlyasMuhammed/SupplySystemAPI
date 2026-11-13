import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of } from 'rxjs';

import { CustomerPaymentDetailComponent } from './customer-payment-detail.component';
import { CustomerPaymentService, CustomerPaymentDetailModel } from '../../../../services/customer-payment.service';
import { SalesInvoiceService } from '../../../../services/sales-invoice.service';
import { AuthService } from '../../../service/auth.service';
import { TenantService } from '../../../service/tenant.service';
import { provideTestOrgCurrencies } from '../../../../shared/doc-currency/doc-currency.testing';
import { environment } from '../../../../../environments/environment';

// A50 D-25 — the customer payment receipt PDF (CUSTOMER_PAYMENT_VIEW).

const payment = {
  uuid: 'cp-50', paymentNumber: 'RCPT-2026-00050', partnerId: 'p-1', partnerName: 'Acme', paymentDate: '2026-11-01T00:00:00Z',
  amount: 100, allocatedAmount: 100, unallocatedAmount: 0, paymentMethod: 'CASH', currencyCode: 'PKR', status: 'RECEIVED',
  createdBy: 1, createdDate: '2026-11-01T00:00:00Z', allocations: []
} as unknown as CustomerPaymentDetailModel;

describe('CustomerPaymentDetailComponent — A50 receipt PDF', () => {
  let fixture: ComponentFixture<CustomerPaymentDetailComponent>;
  let http: HttpTestingController;
  let perms: string[];

  const q = (id: string): HTMLElement | null => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);

  async function setup(permissions: string[]) {
    perms = permissions;
    const payments = jasmine.createSpyObj<CustomerPaymentService>('CustomerPaymentService', ['getPayment']);
    payments.getPayment.and.returnValue(of({ success: true, message: '', result: payment } as any));
    const invoices = jasmine.createSpyObj<SalesInvoiceService>('SalesInvoiceService', ['getOpenInvoices']);
    invoices.getOpenInvoices.and.returnValue(of({ invoices: [], truncated: false } as any));
    await TestBed.configureTestingModule({
      imports: [CustomerPaymentDetailComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), provideTestOrgCurrencies(),
        { provide: CustomerPaymentService, useValue: payments },
        { provide: SalesInvoiceService, useValue: invoices },
        { provide: AuthService, useValue: { hasPermission: (c: string) => perms.includes(c) } },
        { provide: TenantService, useValue: { tenant: signal({ id: 'org-1' }), hasFeature: () => false } },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['uuid', 'cp-50']]) } } }
      ]
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(CustomerPaymentDetailComponent);
    fixture.detectChanges();
  }

  it('offers Download PDF with CUSTOMER_PAYMENT_VIEW and fetches the receipt as a blob', async () => {
    await setup(['CUSTOMER_PAYMENT_VIEW']);
    expect(q('action-pdf')).not.toBeNull();
    const toasts = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
    fixture.componentInstance.downloadReceipt();
    expect(fixture.componentInstance.isDownloadingPdf).toBeTrue();
    const req = http.expectOne(`${environment.apiUrl}/customer-payments/cp-50/pdf`);
    expect(req.request.responseType).toBe('blob');
    req.flush(new Blob(['x']), { status: 500, statusText: 'Server Error' });
    await new Promise(r => setTimeout(r, 100));
    expect(toasts).toHaveBeenCalledWith(jasmine.objectContaining({ severity: 'error', detail: 'The receipt PDF could not be generated.' }));
  });

  it('is hidden without CUSTOMER_PAYMENT_VIEW', async () => {
    await setup([]);
    expect(q('action-pdf')).toBeNull();
  });
});
