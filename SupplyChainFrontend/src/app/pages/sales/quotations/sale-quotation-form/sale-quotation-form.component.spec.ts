import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { signal } from '@angular/core';
import { MessageService } from 'primeng/api';

import { SaleQuotationFormComponent } from './sale-quotation-form.component';
import { SalesPreorderService, SaleQuotation } from '../../../../services/sales-preorder.service';
import { BusinessPartnerService } from '../../../../services/business-partner.service';
import { CurrenciesService } from '../../../../services/currencies.service';
import { TenantService } from '../../../service/tenant.service';
import { AuthService } from '../../../service/auth.service';
import { ok, fail, quotation } from '../sale-quotation.fixtures.spec';

describe('SaleQuotationFormComponent (new / edit header)', () => {
  let fixture: ComponentFixture<SaleQuotationFormComponent>;
  let component: SaleQuotationFormComponent;
  let service: jasmine.SpyObj<SalesPreorderService>;
  let partners: jasmine.SpyObj<BusinessPartnerService>;
  let router: Router;

  async function setup(editing: SaleQuotation | null = null) {
    service = jasmine.createSpyObj<SalesPreorderService>('SalesPreorderService', ['createQuotation', 'getQuotation', 'updateQuotation']);
    service.createQuotation.and.returnValue(ok('sq-new'));
    service.updateQuotation.and.returnValue(ok(null));
    service.getQuotation.and.returnValue(ok(editing));
    partners = jasmine.createSpyObj<BusinessPartnerService>('BusinessPartnerService', ['getPartners']);
    partners.getPartners.and.returnValue(ok({ data: [{ uuid: 'p-1', companyName: 'GlobalTech Co' }], totalRecords: 1 }));
    const currencies = jasmine.createSpyObj<CurrenciesService>('CurrenciesService', ['getAll']);
    currencies.getAll.and.returnValue(ok([
      { id: 'cur-usd', name: 'US Dollar', code: 'USD', symbol: '$' },
      { id: 'cur-pkr', name: 'Pakistani Rupee', code: 'PKR', symbol: 'Rs' }
    ]));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [SaleQuotationFormComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), MessageService,
        { provide: SalesPreorderService, useValue: service },
        { provide: BusinessPartnerService, useValue: partners },
        { provide: CurrenciesService, useValue: currencies },
        { provide: TenantService, useValue: { tenant: signal({ baseCurrency: 'cur-pkr' }), hasFeature: () => true } },
        { provide: AuthService, useValue: { hasPermission: () => true } },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map(editing ? [['uuid', editing.uuid]] : []) } } }
      ]
    }).compileComponents();

    router = TestBed.inject(Router);
    fixture = TestBed.createComponent(SaleQuotationFormComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  it('starts a new quotation today, in the organization\'s base currency', async () => {
    await setup();
    const from = component.form.value.validFrom as Date;
    const today = new Date();
    expect([from.getFullYear(), from.getMonth(), from.getDate()]).toEqual([today.getFullYear(), today.getMonth(), today.getDate()]);
    expect(component.form.value.currencyId).toBe('cur-pkr');
  });

  it('offers only customers', async () => {
    await setup();
    component.searchCustomers({ query: 'glo' } as any);
    expect(partners.getPartners).toHaveBeenCalledWith(jasmine.objectContaining({ isCustomer: true, active: true, search: 'glo' }));
  });

  it('needs a picked customer and a valid-until on or after valid-from', async () => {
    await setup();
    component.form.patchValue({ customer: 'GlobalTech', validFrom: new Date(2026, 9, 10), validTo: new Date(2026, 9, 9) });
    component.save();
    expect(service.createQuotation).not.toHaveBeenCalled();
    expect(component.form.hasError('validToBeforeFrom')).toBeTrue();

    spyOn(router, 'navigate').and.resolveTo(true);
    component.form.patchValue({ customer: { uuid: 'p-1', companyName: 'GlobalTech Co' }, validTo: new Date(2026, 9, 10) });
    component.save();
    expect(service.createQuotation).toHaveBeenCalled();
  });

  it('creates the draft with calendar-day dates and opens it', async () => {
    await setup();
    const navigate = spyOn(router, 'navigate').and.resolveTo(true);
    component.form.patchValue({
      customer: { uuid: 'p-1', companyName: 'GlobalTech Co' }, customerReference: ' RFQ-778 ',
      customerReferenceDate: new Date(2026, 9, 1), currencyId: 'cur-usd',
      validFrom: new Date(2026, 9, 3), validTo: new Date(2026, 9, 31, 23, 0),
      paymentTerms: 'Net 30', deliveryTerms: 'FOB Karachi', notes: 'Prices exclude freight', internalNotes: ''
    });
    component.save();

    expect(service.createQuotation).toHaveBeenCalledOnceWith(jasmine.objectContaining({
      partnerId: 'p-1', customerReference: 'RFQ-778', customerReferenceDate: '2026-10-01', currencyId: 'cur-usd',
      validFrom: '2026-10-03', validTo: '2026-10-31', paymentTerms: 'Net 30', deliveryTerms: 'FOB Karachi',
      notes: 'Prices exclude freight', internalNotes: null
    }));
    expect(navigate).toHaveBeenCalledWith(['/portal/pages/sales/quotations', 'sq-new']);
  });

  it('shows the server\'s refusal', async () => {
    await setup();
    service.createQuotation.and.returnValue(fail(400, 'The partner is not an active customer.'));
    component.form.patchValue({ customer: { uuid: 'p-1', companyName: 'GlobalTech Co' }, validTo: new Date(2099, 0, 1) });
    component.save();
    expect(component.error).toBe('The partner is not an active customer.');
  });

  it('edits a draft\'s header, keeping its customer, with its saved days', async () => {
    await setup(quotation());
    const navigate = spyOn(router, 'navigate').and.resolveTo(true);
    expect(component.form.get('customer')!.disabled).toBeTrue();
    expect((component.form.value.validTo ?? component.form.getRawValue().validTo as Date).getDate()).toBe(31);

    component.form.patchValue({ paymentTerms: 'Net 45' });
    component.save();
    expect(service.updateQuotation).toHaveBeenCalledOnceWith('sq-1', jasmine.objectContaining({
      validFrom: '2026-10-03', validTo: '2026-10-31', paymentTerms: 'Net 45', currencyId: 'cur-usd', customerReference: 'RFQ-778'
    }));
    expect(navigate).toHaveBeenCalledWith(['/portal/pages/sales/quotations', 'sq-1']);
  });

  it('will not edit a quotation past DRAFT', async () => {
    await setup(quotation({ status: 'SENT', isEditable: false }));
    expect(component.notEditable).toBeTrue();
    component.save();
    expect(service.updateQuotation).not.toHaveBeenCalled();
  });
});
