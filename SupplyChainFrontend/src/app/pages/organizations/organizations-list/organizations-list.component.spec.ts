import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { of } from 'rxjs';

import { OrganizationsListComponent } from './organizations-list.component';
import {
  OrganizationDetailModel, OrganizationsService, baseCurrencyChange
} from '../../../services/organizations.service';
import { CountriesService } from '../../../services/countries.service';
import { CurrenciesService } from '../../../services/currencies.service';

const PKR = 'cur-pkr';
const USD = 'cur-usd';

function detail(overrides: Partial<OrganizationDetailModel> = {}): OrganizationDetailModel {
  return {
    id: 'org-1', orgCode: 'ACME', orgName: 'Acme', plan: 'BASIC', isActive: true, contactEmail: 'a@acme.test',
    country: 'Pakistan', timeZone: 'Asia/Karachi', baseCurrency: PKR, createdBy: 1, createdDate: '2026-09-01T00:00:00Z',
    ...overrides
  };
}

describe('baseCurrencyChange', () => {
  it('sends a newly chosen currency, an explicit clear when one was removed, and nothing when it is unchanged', () => {
    expect(baseCurrencyChange(PKR, USD)).toEqual({ baseCurrency: USD });
    expect(baseCurrencyChange(null, USD)).toEqual({ baseCurrency: USD });
    expect(baseCurrencyChange(PKR, PKR)).toEqual({});
    expect(baseCurrencyChange('A1B2-guid', 'a1b2-GUID')).toEqual({});
    expect(baseCurrencyChange(PKR, null)).toEqual({ clearBaseCurrency: true });
    expect(baseCurrencyChange(PKR, '')).toEqual({ clearBaseCurrency: true });
    expect(baseCurrencyChange(null, null)).toEqual({});
    expect(baseCurrencyChange(undefined, '')).toEqual({});
  });
});

describe('OrganizationsListComponent — base currency', () => {
  let fixture: ComponentFixture<OrganizationsListComponent>;
  let component: OrganizationsListComponent;
  let service: jasmine.SpyObj<OrganizationsService>;

  function query(testId: string): HTMLElement | null {
    return document.querySelector(`[data-testid="${testId}"]`);
  }

  async function setup(org: OrganizationDetailModel = detail()) {
    service = jasmine.createSpyObj<OrganizationsService>('OrganizationsService', ['getList', 'getById', 'update', 'create']);
    service.getList.and.returnValue(of({
      success: true, message: '',
      result: { data: [{ id: org.id, orgCode: org.orgCode, orgName: org.orgName, plan: org.plan, isActive: true, createdDate: org.createdDate }],
                totalRecords: 1, page: 1, pageSize: 100, totalPages: 1 }
    }));
    service.getById.and.returnValue(of({ success: true, message: '', result: org }));
    service.update.and.returnValue(of({ success: true, message: 'Record updated successfully.', result: null }));
    service.create.and.returnValue(of({ success: true, message: '', result: { organizationId: 'org-2', adminUserId: 5 } }));

    const countries = jasmine.createSpyObj<CountriesService>('CountriesService', ['getAllCountries']);
    countries.getAllCountries.and.returnValue(of({ success: true, message: '', result: [] } as any));
    const currencies = jasmine.createSpyObj<CurrenciesService>('CurrenciesService', ['getAll']);
    currencies.getAll.and.returnValue(of({
      success: true, message: '',
      result: [
        { id: USD, name: 'US Dollar', code: 'USD', symbol: '$' },
        { id: PKR, name: 'Pakistani Rupee', code: 'PKR', symbol: 'Rs' },
        { id: 'cur-none', name: 'No code', code: null, symbol: null }
      ]
    }));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [OrganizationsListComponent],
      providers: [
        provideNoopAnimations(), provideRouter([]),
        { provide: OrganizationsService, useValue: service },
        { provide: CountriesService, useValue: countries },
        { provide: CurrenciesService, useValue: currencies }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(OrganizationsListComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  afterEach(() => fixture?.destroy());

  it('offers the catalog currencies that have a code, by code', async () => {
    await setup();
    expect(component.currencyOptions).toEqual([
      { label: 'PKR — Pakistani Rupee', value: PKR },
      { label: 'USD — US Dollar', value: USD }
    ]);
  });

  it('opens an edit with the organization’s base currency chosen', async () => {
    await setup();
    component.openEdit({ id: 'org-1', orgCode: 'ACME', orgName: 'Acme', plan: 'BASIC', isActive: true, createdDate: '' });
    fixture.detectChanges();

    expect(component.form.get('baseCurrency')!.value).toBe(PKR);
    expect(component.originalBaseCurrency).toBe(PKR);
    expect(query('base-currency')).not.toBeNull();
  });

  it('an edit that does not touch the base currency sends nothing about it — no clear, so the server keeps it', async () => {
    await setup();
    component.openEdit({ id: 'org-1', orgCode: 'ACME', orgName: 'Acme', plan: 'BASIC', isActive: true, createdDate: '' });
    component.form.patchValue({ orgName: 'Acme Corporation' });

    component.save();

    const [, body] = service.update.calls.mostRecent().args;
    expect(body.orgName).toBe('Acme Corporation');
    expect(body.baseCurrency ?? null).toBeNull();
    expect(body.clearBaseCurrency ?? false).toBeFalse();
  });

  it('choosing a different currency and then the original one again sends nothing about it', async () => {
    await setup();
    component.openEdit({ id: 'org-1', orgCode: 'ACME', orgName: 'Acme', plan: 'BASIC', isActive: true, createdDate: '' });
    component.form.patchValue({ baseCurrency: USD });
    component.form.patchValue({ baseCurrency: PKR });

    component.save();

    const body = service.update.calls.mostRecent().args[1];
    expect('baseCurrency' in body).toBeFalse();
    expect('clearBaseCurrency' in body).toBeFalse();
  });

  it('does not offer a catalog currency without a code', async () => {
    await setup();
    expect(component.currencyOptions.some(o => o.value === 'cur-none')).toBeFalse();
  });

  it('changing the base currency sends the new one', async () => {
    await setup();
    component.openEdit({ id: 'org-1', orgCode: 'ACME', orgName: 'Acme', plan: 'BASIC', isActive: true, createdDate: '' });
    component.form.patchValue({ baseCurrency: USD });

    component.save();

    expect(service.update.calls.mostRecent().args[1]).toEqual(jasmine.objectContaining({ baseCurrency: USD }));
  });

  it('emptying the field sends an explicit clear and warns before saving', async () => {
    await setup();
    component.openEdit({ id: 'org-1', orgCode: 'ACME', orgName: 'Acme', plan: 'BASIC', isActive: true, createdDate: '' });
    component.form.patchValue({ baseCurrency: null });
    fixture.detectChanges();
    expect(query('base-currency-clearing')).not.toBeNull();

    component.save();

    const body = service.update.calls.mostRecent().args[1];
    expect(body.clearBaseCurrency).toBeTrue();
    expect(body.baseCurrency ?? null).toBeNull();
  });

  it('an organization without a base currency that stays without one sends nothing about it', async () => {
    await setup(detail({ baseCurrency: null }));
    component.openEdit({ id: 'org-1', orgCode: 'ACME', orgName: 'Acme', plan: 'BASIC', isActive: true, createdDate: '' });

    component.save();

    const body = service.update.calls.mostRecent().args[1];
    expect('baseCurrency' in body).toBeFalse();
    expect('clearBaseCurrency' in body).toBeFalse();
  });

  it('flags a base currency that is no longer in the list', async () => {
    await setup(detail({ baseCurrency: 'cur-gone' }));
    component.openEdit({ id: 'org-1', orgCode: 'ACME', orgName: 'Acme', plan: 'BASIC', isActive: true, createdDate: '' });
    fixture.detectChanges();

    expect(component.baseCurrencyMissing).toBeTrue();
    expect(query('base-currency-missing')).not.toBeNull();
  });

  it('renaming an organization whose base currency left the catalog does not send that currency back', async () => {
    // The server re-checks any baseCurrency it is sent ("not in the currency list, or has no code" → 400),
    // so echoing the unchanged, stale id would make every edit of this organization fail.
    await setup(detail({ baseCurrency: 'cur-gone' }));
    component.openEdit({ id: 'org-1', orgCode: 'ACME', orgName: 'Acme', plan: 'BASIC', isActive: true, createdDate: '' });
    component.form.patchValue({ orgName: 'Acme Holdings' });

    component.save();

    const body = service.update.calls.mostRecent().args[1];
    expect(body.orgName).toBe('Acme Holdings');
    expect(body.baseCurrency ?? null).toBeNull();
    expect(body.clearBaseCurrency ?? false).toBeFalse();
  });

  it('a new organization can be created with a base currency', async () => {
    await setup();
    component.openNew();
    component.form.patchValue({
      orgCode: 'NEW', orgName: 'New Org', adminFirstName: 'Jane', adminEmail: 'jane@new.test', baseCurrency: USD
    });

    component.save();

    expect(service.create).toHaveBeenCalledWith(jasmine.objectContaining({ orgCode: 'NEW', baseCurrency: USD }));
  });
});
