import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
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
import { ok, quotation } from '../sale-quotation.fixtures.spec';
import { environment } from '../../../../../environments/environment';

// A50 D-25 / D-26 — quotation "Download PDF" (SALE_QUOTATION_VIEW) and "Email to customer" (SALE_QUOTATION_SEND).

describe('SaleQuotationDetailComponent — A50 PDF and email', () => {
  let fixture: ComponentFixture<SaleQuotationDetailComponent>;
  let http: HttpTestingController;
  let toasts: jasmine.Spy;
  let perms: string[];

  const q = (id: string): HTMLElement | null => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);

  async function setup(model: SaleQuotation, permissions: string[]) {
    perms = permissions;
    const service = jasmine.createSpyObj<SalesPreorderService>('SalesPreorderService', ['getQuotation', 'getRejectionReasons']);
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
        { provide: AuthService, useValue: { hasPermission: (c: string) => perms.includes(c), hasAnyPermission: (...c: string[]) => c.some(x => perms.includes(x)) } },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['uuid', 'sq-1']]) } } }
      ]
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(SaleQuotationDetailComponent);
    toasts = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
    fixture.detectChanges();
  }

  it('Download PDF needs SALE_QUOTATION_VIEW; email needs SALE_QUOTATION_SEND', async () => {
    await setup(quotation({ status: 'SENT' }), ['SALE_QUOTATION_VIEW']);
    expect(q('action-pdf')).not.toBeNull();
    expect(q('action-email')).toBeNull();
    TestBed.resetTestingModule();
    await setup(quotation({ status: 'SENT' }), ['SALE_QUOTATION_SEND']);
    expect(q('action-pdf')).toBeNull();
    expect(q('action-email')).not.toBeNull();
  });

  it('a failed PDF shows the server message read from the blob', async () => {
    const model = quotation({ status: 'SENT' });
    await setup(model, ['SALE_QUOTATION_VIEW']);
    fixture.componentInstance.downloadPdf();
    const req = http.expectOne(`${environment.apiUrl}/sale-quotations/${model.uuid}/pdf`);
    expect(req.request.responseType).toBe('blob');
    req.flush(new Blob([JSON.stringify({ message: 'Quotation not found.' })], { type: 'application/json' }),
      { status: 404, statusText: 'Not Found' });
    await new Promise(r => setTimeout(r, 100));
    expect(toasts).toHaveBeenCalledWith(jasmine.objectContaining({ severity: 'error', detail: 'Quotation not found.' }));
    expect(fixture.componentInstance.isDownloadingPdf).toBeFalse();
  });

  it('a send where nobody was reached is reported as a failure', async () => {
    const model = quotation({ status: 'SENT' });
    await setup(model, ['SALE_QUOTATION_SEND']);
    fixture.componentInstance.emailVisible = true;
    fixture.detectChanges();
    (document.querySelector('[data-testid="email-send"] button') as HTMLButtonElement).click();
    const req = http.expectOne(`${environment.apiUrl}/sale-quotations/${model.uuid}/email`);
    expect(req.request.body).toEqual({});
    req.flush({ success: true, message: '', result: { sent: false, sentTo: [], cc: [], failed: [{ email: 'a@b.com', error: 'SMTP down' }] } });
    expect(toasts).toHaveBeenCalledWith(jasmine.objectContaining({ severity: 'error', detail: 'a@b.com: SMTP down' }));
  });
});
