import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { signal } from '@angular/core';
import { MessageService } from 'primeng/api';

import { SaleQuotationFormComponent } from './sale-quotation-form.component';
import { SalesPreorderService, SaleQuotation } from '../../../../services/sales-preorder.service';
import { TEST_AED, TEST_PKR, TEST_USD, provideTestOrgCurrencies } from '../../../../shared/doc-currency/doc-currency.testing';
import { BusinessPartnerService } from '../../../../services/business-partner.service';
import { CurrenciesService } from '../../../../services/currencies.service';
import { TenantService } from '../../../service/tenant.service';
import { AuthService } from '../../../service/auth.service';
import { ok, quotation } from '../sale-quotation.fixtures.spec';

// A35 D-9 / D-14 — a new quotation follows the customer's default sale currency until the user picks one.
// A35 D-1 — the picker offers the org's active currencies, plus the quotation's own even if deactivated since.

describe('SaleQuotationFormComponent — A35 org-active currencies', () => {
  async function setup(editing: SaleQuotation | null) {
    const service = jasmine.createSpyObj<SalesPreorderService>('SalesPreorderService', ['createQuotation', 'getQuotation']);
    service.getQuotation.and.returnValue(ok(editing));
    const partners = jasmine.createSpyObj<BusinessPartnerService>('BusinessPartnerService', ['getPartners']);
    partners.getPartners.and.returnValue(ok({ data: [], totalRecords: 0 }));
    const currencies = jasmine.createSpyObj<CurrenciesService>('CurrenciesService', ['getAll']);
    currencies.getAll.and.returnValue(ok([
      { id: 'cur-pkr', name: 'Pakistani Rupee', code: 'PKR' }, { id: 'cur-aed', name: 'UAE Dirham', code: 'AED' },
      { id: 'cur-usd', name: 'US Dollar', code: 'USD' }, { id: 'cur-eur', name: 'Euro', code: 'EUR' }
    ]));
    await TestBed.configureTestingModule({
      imports: [SaleQuotationFormComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), MessageService,
        // USD deactivated; EUR never set up for the org.
        provideTestOrgCurrencies([TEST_PKR, TEST_AED, { ...TEST_USD, isActive: false }]),
        { provide: SalesPreorderService, useValue: service },
        { provide: BusinessPartnerService, useValue: partners },
        { provide: CurrenciesService, useValue: currencies },
        { provide: TenantService, useValue: { tenant: signal({ id: 'org-1', baseCurrency: 'cur-pkr' }), hasFeature: () => true } },
        { provide: AuthService, useValue: { hasPermission: () => true } },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map(editing ? [['uuid', editing.uuid]] : []) } } }
      ]
    }).compileComponents();
    const fixture = TestBed.createComponent(SaleQuotationFormComponent);
    fixture.detectChanges();
    return fixture.componentInstance;
  }

  it('a new quotation is offered only the active org currencies, starting in the base', async () => {
    const component = await setup(null);
    expect(component.currencyOptions.map(o => o.value)).toEqual(['cur-pkr', 'cur-aed']);
    expect(component.form.get('currencyId')!.value).toBe('cur-pkr');
  });

  it('a draft in a currency deactivated since keeps it on offer, and keeps it', async () => {
    const component = await setup(quotation({ currencyId: 'cur-usd', currencyCode: 'USD', lines: [] }));
    expect(component.currencyOptions.map(o => o.value)).toEqual(['cur-pkr', 'cur-aed', 'cur-usd']);
    expect(component.form.get('currencyId')!.value).toBe('cur-usd');
  });
});

describe('SaleQuotationFormComponent — A35 customer currency', () => {
  let fixture: ComponentFixture<SaleQuotationFormComponent>;
  let component: SaleQuotationFormComponent;

  beforeEach(async () => {
    const service = jasmine.createSpyObj<SalesPreorderService>('SalesPreorderService', ['createQuotation', 'getQuotation']);
    const partners = jasmine.createSpyObj<BusinessPartnerService>('BusinessPartnerService', ['getPartners']);
    partners.getPartners.and.returnValue(ok({ data: [], totalRecords: 0 }));
    const currencies = jasmine.createSpyObj<CurrenciesService>('CurrenciesService', ['getAll']);
    currencies.getAll.and.returnValue(ok([
      { id: 'cur-aed', name: 'UAE Dirham', code: 'AED' }, { id: 'cur-pkr', name: 'Pakistani Rupee', code: 'PKR' }
    ]));
    await TestBed.configureTestingModule({
      imports: [SaleQuotationFormComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), MessageService,
        { provide: SalesPreorderService, useValue: service },
        { provide: BusinessPartnerService, useValue: partners },
        { provide: CurrenciesService, useValue: currencies },
        { provide: TenantService, useValue: { tenant: signal({ baseCurrency: 'cur-pkr' }), hasFeature: () => true } },
        { provide: AuthService, useValue: { hasPermission: () => true } },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map() } } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(SaleQuotationFormComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('takes the customer\'s default sale currency, and goes back to the base for a customer with none', () => {
    component.onCustomerSelected({ uuid: 'p-1', companyName: 'Al Rashid', defaultSaleCurrencyId: 'cur-aed' } as any);
    expect(component.form.get('currencyId')!.value).toBe('cur-aed');
    component.onCustomerSelected({ uuid: 'p-2', companyName: 'Punjab Group' } as any);
    expect(component.form.get('currencyId')!.value).toBe('cur-pkr');
  });

  it('leaves a currency picked by hand alone', () => {
    component.form.get('currencyId')!.setValue('cur-pkr');
    component.form.get('currencyId')!.markAsDirty();
    component.onCustomerSelected({ uuid: 'p-1', companyName: 'Al Rashid', defaultSaleCurrencyId: 'cur-aed' } as any);
    expect(component.form.get('currencyId')!.value).toBe('cur-pkr');
  });
});
