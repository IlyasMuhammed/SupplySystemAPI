import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, Router } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of } from 'rxjs';

import { SaleInquiryFormComponent } from './sale-inquiry-form.component';
import { SalesPreorderService, CreateSaleInquiryRequest } from '../../../../services/sales-preorder.service';
import { BusinessPartnerService } from '../../../../services/business-partner.service';
import { UserService } from '../../../../services/user.service';
import { AuthService } from '../../../service/auth.service';
import { TenantService } from '../../../service/tenant.service';
import { provideTestOrgCurrencies } from '../../../../shared/doc-currency/doc-currency.testing';

// A35 — a new inquiry is in a currency: the sale base to start with, the customer's default sale currency once picked
// (D-9, D-14), sent as currencyId. Inquiries carry no prices, so no rate.

describe('SaleInquiryFormComponent — A35 currency', () => {
  let fixture: ComponentFixture<SaleInquiryFormComponent>;
  let component: SaleInquiryFormComponent;
  let service: jasmine.SpyObj<SalesPreorderService>;

  beforeEach(async () => {
    service = jasmine.createSpyObj<SalesPreorderService>('SalesPreorderService', ['createInquiry']);
    service.createInquiry.and.returnValue(of({ success: true, message: '', result: 'inq-new' } as any));
    const partners = jasmine.createSpyObj<BusinessPartnerService>('BusinessPartnerService', ['getPartners']);
    partners.getPartners.and.returnValue(of({ success: true, message: '', result: { data: [], totalRecords: 0 } } as any));
    const users = jasmine.createSpyObj<UserService>('UserService', ['getUsers']);
    users.getUsers.and.returnValue(of({ success: true, message: '', result: { items: [] } } as any));

    await TestBed.configureTestingModule({
      imports: [SaleInquiryFormComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), MessageService,
        provideTestOrgCurrencies(),
        { provide: SalesPreorderService, useValue: service },
        { provide: BusinessPartnerService, useValue: partners },
        { provide: UserService, useValue: users },
        { provide: AuthService, useValue: { hasPermission: () => true, getUserData: () => ({ userId: 7 }) } },
        { provide: TenantService, useValue: { tenant: signal({ id: 'org-1' }) } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(SaleInquiryFormComponent);
    component = fixture.componentInstance;
    spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    fixture.detectChanges();
  });

  it('starts in the sale base, follows the customer\'s default, and sends it', () => {
    expect(component.currencyControl.value).toBe('cur-pkr');
    const customer = { uuid: 'p-1', companyName: 'Al Rashid Trading LLC', defaultSaleCurrencyId: 'cur-aed' } as any;
    component.draft.customer = customer;
    component.onCustomerSelected(customer);
    expect(component.currencyControl.value).toBe('cur-aed');

    component.save();
    const sent = service.createInquiry.calls.mostRecent().args[0] as CreateSaleInquiryRequest;
    expect(sent.currencyId).toBe('cur-aed');
  });
});
