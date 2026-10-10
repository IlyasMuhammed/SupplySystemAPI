import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';

import { OrganizationsListComponent } from './organizations-list.component';
import {
  OrganizationDetailModel, OrganizationListItemModel, OrganizationsService, OrgUserSummary, ORG_ADMIN_ROLE_ID, baseCurrencyChange
} from '../../../services/organizations.service';
import { CountriesService } from '../../../services/countries.service';
import { CurrenciesService } from '../../../services/currencies.service';
import { UserService } from '../../../services/user.service';

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

    const users = jasmine.createSpyObj<UserService>('UserService', ['setUserPassword']);

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
        { provide: UserService, useValue: users },
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

describe('OrganizationsListComponent — admin status, resend invite, set password', () => {
  let fixture: ComponentFixture<OrganizationsListComponent>;
  let component: OrganizationsListComponent;
  let service: jasmine.SpyObj<OrganizationsService>;
  let users: jasmine.SpyObj<UserService>;

  const ORG: OrganizationListItemModel = { id: 'org-1', orgCode: 'ACME', orgName: 'Acme', plan: 'BASIC', isActive: true, createdDate: '' };

  function admin(overrides: Partial<OrgUserSummary> = {}): OrgUserSummary {
    return {
      userId: 7, firstName: 'Ada', lastName: 'Admin', email: 'ada@acme.tset', roleId: ORG_ADMIN_ROLE_ID, roleName: 'Organization Admin',
      isActive: false, invitePending: true, inviteExpiresAt: '2099-01-15T10:00:00Z', ...overrides
    };
  }

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`) ?? document.querySelector(`[data-testid="${testId}"]`);
  }

  function submitButton(): HTMLButtonElement {
    return query('set-password-submit')!.querySelector('button') as HTMLButtonElement;
  }

  async function setup(orgUsers: OrgUserSummary[]) {
    service = jasmine.createSpyObj<OrganizationsService>('OrganizationsService',
      ['getList', 'getById', 'update', 'create', 'getOrgUsers', 'updateAdmin', 'reinviteOrgUser']);
    service.getList.and.returnValue(of({ success: true, message: '', result: { data: [ORG], totalRecords: 1, page: 1, pageSize: 100, totalPages: 1 } }));
    service.getOrgUsers.and.returnValue(of({ success: true, message: '', result: orgUsers }));
    users = jasmine.createSpyObj<UserService>('UserService', ['setUserPassword']);

    const countries = jasmine.createSpyObj<CountriesService>('CountriesService', ['getAllCountries']);
    countries.getAllCountries.and.returnValue(of({ success: true, message: '', result: [] } as any));
    const currencies = jasmine.createSpyObj<CurrenciesService>('CurrenciesService', ['getAll']);
    currencies.getAll.and.returnValue(of({ success: true, message: '', result: [] }));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [OrganizationsListComponent],
      providers: [
        provideNoopAnimations(), provideRouter([]),
        { provide: OrganizationsService, useValue: service },
        { provide: UserService, useValue: users },
        { provide: CountriesService, useValue: countries },
        { provide: CurrenciesService, useValue: currencies }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(OrganizationsListComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    component.manageAdmin(ORG);
    fixture.detectChanges();
  }

  afterEach(() => fixture?.destroy());

  it('shows a pending invite with its expiry, and offers Resend invite prefilled with the current address', async () => {
    await setup([admin()]);

    const status = query('admin-status')!;
    expect(status.textContent).toContain('Invite pending');
    expect(status.textContent).toContain('link expires');
    expect(status.textContent).toContain('2099');
    expect(status.classList).toContain('wn');
    expect(query('reinvite-section')).not.toBeNull();
    expect(component.reinviteEmail).toBe('ada@acme.tset');
  });

  it('shows Active for an admin who has set up their account, and hides Resend invite', async () => {
    await setup([admin({ isActive: true, invitePending: false, inviteExpiresAt: null })]);

    const status = query('admin-status')!;
    expect(status.textContent).toContain('Active');
    expect(status.classList).toContain('ok');
    expect(query('reinvite-section')).toBeNull();
    expect(query('set-password-section')).not.toBeNull();
  });

  it('shows Inactive for a deactivated admin without a pending invite, and still offers Resend invite', async () => {
    await setup([admin({ isActive: false, invitePending: false, inviteExpiresAt: null })]);

    expect(query('admin-status')!.textContent).toContain('Inactive');
    expect(query('reinvite-section')).not.toBeNull();
  });

  it('resends to a corrected e-mail and updates the shown admin from the reply', async () => {
    await setup([admin()]);
    service.reinviteOrgUser.and.returnValue(of({
      success: true, message: 'A new invite was sent to ada@acme.test',
      result: admin({ email: 'ada@acme.test', inviteExpiresAt: '2099-02-01T10:00:00Z' })
    }));

    component.reinviteEmail = 'ada@acme.test';
    component.sendReinvite();
    fixture.detectChanges();

    expect(service.reinviteOrgUser).toHaveBeenCalledWith('org-1', 7, 'ada@acme.test');
    expect(component.currentAdmin!.email).toBe('ada@acme.test');
    expect(component.currentAdmin!.inviteExpiresAt).toBe('2099-02-01T10:00:00Z');
    expect(component.reinviteError).toBe('');
  });

  it('resending to the unchanged address sends no e-mail correction', async () => {
    await setup([admin()]);
    service.reinviteOrgUser.and.returnValue(of({ success: true, message: 'A new invite was sent to ada@acme.tset', result: admin() }));

    component.sendReinvite();

    expect(service.reinviteOrgUser).toHaveBeenCalledWith('org-1', 7, null);
  });

  it('shows the server message inline when the resend is refused', async () => {
    await setup([admin()]);
    service.reinviteOrgUser.and.returnValue(throwError(() => ({ error: { message: 'That e-mail address is already in use.' } })));

    component.reinviteEmail = 'taken@acme.test';
    component.sendReinvite();
    fixture.detectChanges();

    expect(query('reinvite-error')!.textContent).toContain('That e-mail address is already in use.');
  });

  it('keeps Set password disabled until the rule is met and the confirmation matches', async () => {
    await setup([admin()]);
    expect(submitButton().disabled).toBeTrue();

    component.newPassword = 'weakpass';
    component.confirmPassword = 'weakpass';
    fixture.detectChanges();
    expect(component.canSetPassword).toBeFalse();
    expect(submitButton().disabled).toBeTrue();

    component.newPassword = 'Str0ng!pw';
    component.confirmPassword = 'Str0ng!px';
    fixture.detectChanges();
    expect(component.canSetPassword).toBeFalse();
    expect(submitButton().disabled).toBeTrue();
    expect(query('set-password-rule-match')!.classList).not.toContain('ok');
    expect(query('set-password-rule-special')!.classList).toContain('ok');

    component.confirmPassword = 'Str0ng!pw';
    fixture.detectChanges();
    expect(component.canSetPassword).toBeTrue();
    expect(submitButton().disabled).toBeFalse();
  });

  it('sets the admin password, clears the fields and reloads the users (status flips to Active)', async () => {
    await setup([admin()]);
    users.setUserPassword.and.returnValue(of({ success: true, message: "Password set. The user's other sessions were signed out.", result: null }));
    service.getOrgUsers.and.returnValue(of({ success: true, message: '', result: [admin({ isActive: true, invitePending: false, inviteExpiresAt: null })] }));

    component.newPassword = 'Str0ng!pw';
    component.confirmPassword = 'Str0ng!pw';
    component.setAdminPassword();
    fixture.detectChanges();

    expect(users.setUserPassword).toHaveBeenCalledWith(7, 'Str0ng!pw');
    expect(service.getOrgUsers).toHaveBeenCalledTimes(2);
    expect(component.newPassword).toBe('');
    expect(component.confirmPassword).toBe('');
    expect(query('admin-status')!.textContent).toContain('Active');
    expect(query('reinvite-section')).toBeNull();
  });

  it('shows the server message inline when setting the password fails', async () => {
    await setup([admin()]);
    users.setUserPassword.and.returnValue(throwError(() => ({ status: 403, error: { message: 'Use Change Password to change your own password.' } })));

    component.newPassword = 'Str0ng!pw';
    component.confirmPassword = 'Str0ng!pw';
    component.setAdminPassword();
    fixture.detectChanges();

    expect(query('set-password-error')!.textContent).toContain('Use Change Password to change your own password.');
    expect(service.getOrgUsers).toHaveBeenCalledTimes(1);
    expect(component.newPassword).toBe('Str0ng!pw');
  });
});
