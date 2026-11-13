import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of } from 'rxjs';

import { SalesInvoiceDetailComponent } from './sales-invoice-detail.component';
import { SalesInvoiceService, SalesInvoiceDetailModel } from '../../../../services/sales-invoice.service';
import { AttachmentService } from '../../../../services/attachment.service';
import { AuthService } from '../../../service/auth.service';
import { TenantService } from '../../../service/tenant.service';
import { provideTestOrgCurrencies } from '../../../../shared/doc-currency/doc-currency.testing';
import { environment } from '../../../../../environments/environment';

// A50 D-26 — "Email to customer" on a sales invoice: SALES_INVOICE_MANAGE, not a draft or a cancelled invoice.

const UUID = '50000000-0000-0000-0000-0000000000a1';

function invoice(overrides: Partial<SalesInvoiceDetailModel> = {}): SalesInvoiceDetailModel {
  return {
    uuid: UUID, invoiceNumber: 'SINV-2026-00050', partnerId: 'p-1', partnerName: 'Acme',
    invoiceDate: '2026-11-01T00:00:00Z', dueDate: '2026-12-01T00:00:00Z', grandTotal: 100, amountPaid: 0, balanceDue: 100,
    status: 'ISSUED', currencyCode: 'PKR', subtotal: 100, discountAmount: 0, taxAmount: 0, createdDate: '2026-11-01T00:00:00Z',
    lines: [], payments: [], ...overrides
  } as SalesInvoiceDetailModel;
}

describe('SalesInvoiceDetailComponent — A50 email to customer', () => {
  let fixture: ComponentFixture<SalesInvoiceDetailComponent>;
  let http: HttpTestingController;
  let toasts: jasmine.Spy;
  let perms: string[];

  const q = (id: string): HTMLElement | null => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);

  async function setup(model: SalesInvoiceDetailModel, permissions = ['SALES_INVOICE_MANAGE', 'SALES_INVOICE_VIEW']) {
    perms = permissions;
    const invoices = jasmine.createSpyObj<SalesInvoiceService>('SalesInvoiceService', ['getInvoice']);
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
        { provide: AuthService, useValue: { hasPermission: (c: string) => perms.includes(c), hasAnyPermission: (...c: string[]) => c.some(x => perms.includes(x)) } },
        { provide: TenantService, useValue: { tenant: signal({ id: 'org-1' }), hasFeature: () => false } },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['uuid', UUID]]) } } }
      ]
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(SalesInvoiceDetailComponent);
    toasts = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
    fixture.detectChanges();
  }

  it('offers "Email to customer" on an issued invoice and posts To / Cc / message', async () => {
    await setup(invoice());
    const btn = q('action-email');
    expect(btn).not.toBeNull();
    (btn!.querySelector('button') as HTMLButtonElement).click();
    fixture.detectChanges();
    await fixture.whenStable();

    const dlg = fixture.componentInstance;
    expect(dlg.emailVisible).toBeTrue();
    const toInput = document.querySelector('[data-testid="email-to"]') as HTMLInputElement;
    expect(toInput.value).toBe('');                       // never prefilled: the server defaults it (D-TPL)
    toInput.value = 'buyer@acme.com'; toInput.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    (document.querySelector('[data-testid="email-send"] button') as HTMLButtonElement).click();

    const req = http.expectOne(`${environment.apiUrl}/sales-invoices/${UUID}/email`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ to: 'buyer@acme.com' });
    req.flush({ success: true, message: '', result: { sent: true, sentTo: ['buyer@acme.com'], cc: [], failed: [] } });
    expect(toasts).toHaveBeenCalledWith(jasmine.objectContaining({ severity: 'success', detail: 'Sent to buyer@acme.com.' }));
  });

  it('is not offered on a draft, a cancelled invoice, or without SALES_INVOICE_MANAGE', async () => {
    await setup(invoice({ status: 'DRAFT' }));
    expect(q('action-email')).toBeNull();
    TestBed.resetTestingModule();
    await setup(invoice({ status: 'CANCELLED' }));
    expect(q('action-email')).toBeNull();
    TestBed.resetTestingModule();
    await setup(invoice(), ['SALES_INVOICE_VIEW']);
    expect(q('action-email')).toBeNull();
  });
});
