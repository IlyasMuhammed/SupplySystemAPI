import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { of, Subject } from 'rxjs';

import { OrganizationsListComponent } from './organizations-list.component';
import { CreateOrganizationResult, OrganizationsService, packLevelLabel } from '../../../services/organizations.service';
import { CountriesService } from '../../../services/countries.service';
import { CurrenciesService } from '../../../services/currencies.service';
import { UserService } from '../../../services/user.service';
import { FeaturesService } from '../../../services/features.service';
import { CountriesAdminService } from '../../../services/countries-admin.service';
import { FeaturesAdminService } from '../../../services/features-admin.service';
import { IndustriesService } from '../../../services/industries.service';
import { COMMON_TIMEZONES, timezoneOptions } from '../../../shared/timezones';
import { AE, PK, a51Doubles, country, industry, plan } from './organizations-list.testing';

describe('timezoneOptions', () => {
  it('offers the country default first, then the common zones, without duplicates', () => {
    const opts = timezoneOptions('Asia/Karachi');
    expect(opts[0]).toEqual({ value: 'Asia/Karachi', label: 'Asia/Karachi (country default)' });
    expect(opts.filter(o => o.value === 'Asia/Karachi').length).toBe(1);
    expect(opts.length).toBe(COMMON_TIMEZONES.length);
  });

  it('keeps a value that is in neither list, and works without a country', () => {
    expect(timezoneOptions(null, 'Pacific/Fiji').some(o => o.value === 'Pacific/Fiji')).toBeTrue();
    expect(timezoneOptions(null, 'Europe/London').length).toBe(COMMON_TIMEZONES.length);
    expect(timezoneOptions('', '  ')[0].value).toBe('UTC');
  });
});

describe('packLevelLabel', () => {
  it('words every level of the fallback chain', () => {
    expect(packLevelLabel('Exact')).toContain('country + industry');
    expect(packLevelLabel('Country')).toContain('country pack');
    expect(packLevelLabel('Industry')).toContain('industry pack');
    expect(packLevelLabel('Global')).toContain('general pack');
    expect(packLevelLabel('None')).toBe('no pack');
  });
});

describe('OrganizationsListComponent — A51 P3 create form', () => {
  let fixture: ComponentFixture<OrganizationsListComponent>;
  let component: OrganizationsListComponent;
  let service: jasmine.SpyObj<OrganizationsService>;
  let doubles: ReturnType<typeof a51Doubles>;

  const q = (id: string) => document.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  async function setup(createResult: CreateOrganizationResult = { organizationId: 'org-9', adminUserId: 5 }, d = a51Doubles()) {
    doubles = d;
    service = jasmine.createSpyObj<OrganizationsService>('OrganizationsService', ['getList', 'getById', 'update', 'create']);
    service.getList.and.returnValue(of({ success: true, message: '', result: { data: [], totalRecords: 0, page: 1, pageSize: 100, totalPages: 0 } }));
    service.create.and.returnValue(of({ success: true, message: '', result: createResult }));

    const countries = jasmine.createSpyObj<CountriesService>('CountriesService', ['getAllCountries']);
    countries.getAllCountries.and.returnValue(of({ success: true, message: '', result: [] } as any));
    const currencies = jasmine.createSpyObj<CurrenciesService>('CurrenciesService', ['getAll']);
    currencies.getAll.and.returnValue(of({ success: true, message: '', result: [] }));
    const features = jasmine.createSpyObj<FeaturesService>('FeaturesService', ['getCatalog', 'getPlanTemplates']);
    features.getCatalog.and.returnValue(of({ success: true, message: '', result: [
      { id: 'm2', featureCode: 'MODULE_SUPPLIERS', featureName: 'Supplier Management', category: 'MODULE', isCore: false, displayOrder: 20, description: 'Suppliers' }
    ] }));
    features.getPlanTemplates.and.returnValue(of({ success: true, message: '', result: [
      { plan: 'BASIC', features: [{ featureCode: 'MODULE_SUPPLIERS', isEnabledByDefault: true }] }
    ] }));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [OrganizationsListComponent],
      providers: [
        provideNoopAnimations(), provideRouter([]),
        { provide: OrganizationsService, useValue: service },
        { provide: UserService, useValue: jasmine.createSpyObj<UserService>('UserService', ['setUserPassword']) },
        { provide: CountriesService, useValue: countries },
        { provide: CurrenciesService, useValue: currencies },
        { provide: FeaturesService, useValue: features },
        { provide: CountriesAdminService, useValue: d.countriesAdmin },
        { provide: IndustriesService, useValue: d.industries },
        { provide: FeaturesAdminService, useValue: d.featuresAdmin }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(OrganizationsListComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  function fillBasics() {
    component.form.patchValue({ orgCode: 'ACME', orgName: 'Acme', adminFirstName: 'Jane', adminEmail: 'jane@acme.test' });
  }

  const choose = (code: string | null) => component.form.get('countryCode')!.setValue(code);

  afterEach(() => fixture?.destroy());

  it('lists only active countries that have a code, and shows the plans from the Plans master with BASIC as Default', async () => {
    await setup(undefined, a51Doubles(
      [PK, country('XX', 'Retired', 'UTC', { isActive: false }), country('', 'No code', null), AE],
      {}, [plan('BASIC', 'Basic'), plan('GOLD', 'Gold', { sortOrder: 5 }), plan('OLD', 'Old', { isActive: false })]));

    expect(component.newCountryOptions.map(c => c.value)).toEqual(['PK', 'AE']);
    expect(component.planOptions).toEqual([{ label: 'Default', value: 'BASIC' }, { label: 'Gold', value: 'GOLD' }]);
  });

  it('keeps the built-in plan list when the Plans master cannot be read', async () => {
    const d = a51Doubles();
    d.featuresAdmin.listPlans.and.returnValue(new Subject<never>().asObservable());
    await setup(undefined, d);
    expect(component.planOptions.map(p => p.value)).toEqual(['BASIC', 'STANDARD', 'ENTERPRISE']);
  });

  it('shows the hint that country and industry may be left empty', async () => {
    await setup();
    component.openNew();
    fixture.detectChanges();
    expect(q('country-industry-hint')?.textContent).toContain('Leave empty to let the organization admin choose in the setup wizard');
  });

  it('choosing a country loads its industries and fills the timezone from the country default', async () => {
    await setup();
    component.openNew();
    choose('PK');

    expect(doubles.industries.byCountry).toHaveBeenCalledWith('PK');
    expect(component.industryOptions).toEqual([{ label: 'Trading', value: 'TRADING' }, { label: 'Retail', value: 'RETAIL' }]);
    expect(component.form.get('timeZone')!.value).toBe('Asia/Karachi');
    expect(component.timezoneChoices[0].value).toBe('Asia/Karachi');
  });

  it('changing the country resets the industry and refills the timezone, even over a zone the user picked', async () => {
    await setup();
    component.openNew();
    choose('PK');
    component.form.patchValue({ industryType: 'RETAIL', timeZone: 'Europe/London' });

    choose('AE');

    expect(component.form.get('industryType')!.value).toBeNull();
    expect(component.industryOptions).toEqual([{ label: 'Trading', value: 'TRADING' }]);
    expect(component.form.get('timeZone')!.value).toBe('Asia/Dubai');
    expect(component.timezoneChoices[0].value).toBe('Asia/Dubai');
  });

  it('the timezone stays editable after it was filled', async () => {
    await setup();
    component.openNew();
    choose('PK');
    component.form.get('timeZone')!.setValue('Asia/Dubai');
    expect(component.form.get('timeZone')!.value).toBe('Asia/Dubai');
    expect(component.timezoneChoices.some(o => o.value === 'Asia/Dubai')).toBeTrue();
  });

  it('clearing the country empties the industries and the timezone', async () => {
    await setup();
    component.openNew();
    choose('PK');
    component.form.patchValue({ industryType: 'TRADING' });

    choose(null);

    expect(component.industryOptions).toEqual([]);
    expect(component.form.get('industryType')!.value).toBeNull();
    expect(component.form.get('timeZone')!.value).toBe('');
  });

  it('a country with no timezone on file leaves the timezone empty', async () => {
    await setup(undefined, a51Doubles([country('ZZ', 'Zedland', null)], { ZZ: [] }));
    component.openNew();
    component.form.patchValue({ timeZone: 'UTC' });
    choose('ZZ');
    expect(component.form.get('timeZone')!.value).toBe('');
    expect(component.countryTimezone).toBeNull();
  });

  it('ignores a late industries answer for a country that is no longer chosen', async () => {
    const slow = new Subject<any>();
    const d = a51Doubles();
    d.industries.byCountry.and.callFake((c: string) => c === 'PK' ? slow.asObservable() : of({ success: true, message: '', result: [industry('TRADING', 'Trading')] }));
    await setup(undefined, d);
    component.openNew();
    choose('PK');
    choose('AE');
    slow.next({ success: true, message: '', result: [industry('RETAIL', 'Retail')] });
    slow.complete();

    expect(component.industryOptions).toEqual([{ label: 'Trading', value: 'TRADING' }]);
  });

  it('reports a failed industries load instead of leaving an empty list', async () => {
    const d = a51Doubles();
    d.industries.byCountry.and.returnValue(new Subject<never>().asObservable());
    await setup(undefined, d);
    component.openNew();
    choose('PK');
    expect(component.isLoadingIndustries).toBeTrue();
  });

  it('sends the chosen country code, industry, plan and timezone on create', async () => {
    await setup();
    component.openNew();
    fillBasics();
    choose('PK');
    component.form.patchValue({ industryType: 'TRADING', plan: 'STANDARD' });

    component.save();
    component.confirmCreate();

    const body = service.create.calls.mostRecent().args[0];
    expect(body.countryCode).toBe('PK');
    expect(body.industryType).toBe('TRADING');
    expect(body.plan).toBe('STANDARD');
    expect(body.timeZone).toBe('Asia/Karachi');
    expect('country' in body).toBeFalse();
  });

  it('sends no country or industry when they are left empty (the wizard asks later)', async () => {
    await setup();
    component.openNew();
    fillBasics();

    component.save();
    component.confirmCreate();

    const body = service.create.calls.mostRecent().args[0];
    expect(body.countryCode).toBeUndefined();
    expect(body.industryType).toBeUndefined();
    expect(body.timeZone).toBeUndefined();
    expect(body.plan).toBe('BASIC');
    expect(component.showResultDialog).toBeFalse();
  });

  it('still sends the modules picked at creation', async () => {
    await setup();
    component.openNew();
    fillBasics();
    choose('PK');

    component.save();
    expect(component.selectedModules.has('MODULE_SUPPLIERS')).toBeTrue();
    component.confirmCreate();

    expect(service.create.calls.mostRecent().args[0].moduleCodes).toEqual(['MODULE_SUPPLIERS']);
  });

  it('A51 P8: Coming Soon / hidden modules are not pre-ticked, cannot be ticked, are tagged and never sent', async () => {
    await setup();
    component.openNew();
    fillBasics();
    component.save();
    component.moduleChoices = [
      ...component.moduleChoices,
      { id: 'p', featureCode: 'MODULE_POS', featureName: 'Point of Sale', category: 'MODULE', isCore: false, displayOrder: 90, status: 'ComingSoon', isVisible: false }
    ];
    (component as any).planDefaults.get('BASIC').add('MODULE_POS');

    component.useAllPlanDefaults();
    expect(component.selectedModules.has('MODULE_POS')).toBeFalse();
    expect(component.selectedModules.has('MODULE_SUPPLIERS')).toBeTrue();
    component.toggleModule('MODULE_POS', true);
    expect(component.selectedModules.has('MODULE_POS')).toBeFalse();

    fixture.detectChanges();
    const el: HTMLElement = fixture.nativeElement.ownerDocument.body;
    const tagged = el.querySelector('[data-testid="pick-coming-soon-MODULE_POS"]') as HTMLElement;
    expect(tagged).toBeTruthy();
    expect(tagged.getAttribute('title')).toContain('license it later from the organization');
    expect(tagged.textContent).toContain('Coming soon');

    component.confirmCreate();
    expect(service.create.calls.mostRecent().args[0].moduleCodes).toEqual(['MODULE_SUPPLIERS']);
  });

  it('never sends an industry without a country', async () => {
    await setup();
    component.openNew();
    fillBasics();
    component.form.patchValue({ industryType: 'TRADING' });

    component.save();
    component.confirmCreate();

    expect(service.create.calls.mostRecent().args[0].industryType).toBeUndefined();
  });

  it('shows the create result with the per-category outcome when default data was applied', async () => {
    await setup({
      organizationId: 'org-9', adminUserId: 5, plan: 'BASIC', country: 'Pakistan', countryCode: 'PK', industryType: 'TRADING', timeZone: 'Asia/Karachi',
      defaults: {
        attempted: true, completed: false, setupStatus: 'InProgress', packLevel: 'Country', fallbackUsed: true, message: 'Default data was applied with problems (TAX).',
        categories: [
          { category: 'CURRENCY', status: 'Applied', ok: true, created: 2, skipped: 0, packLevel: 'Country' },
          { category: 'TAX', status: 'Failed', ok: false, created: 0, skipped: 0, packLevel: 'Country', message: 'boom' }
        ]
      }
    });
    component.openNew();
    fillBasics();
    choose('PK');
    component.form.patchValue({ industryType: 'TRADING' });

    component.save();
    component.confirmCreate();
    fixture.detectChanges();

    expect(component.showResultDialog).toBeTrue();
    expect(component.categoryProblems.map(c => c.category)).toEqual(['TAX']);
    expect(q('res-country')?.textContent).toContain('Pakistan');
    expect(q('res-timezone')?.textContent).toContain('Asia/Karachi');
    expect(q('res-plan')?.textContent).toContain('Default');
    expect(q('res-pack')?.textContent).toContain('country pack');
    expect(q('res-TAX')?.textContent).toContain('Failed');
    expect(q('res-TAX')?.textContent).toContain('boom');
    expect(q('res-problems')).not.toBeNull();
    expect(q('res-status')?.textContent).toContain('Needs attention');
  });

  it('shows a complete setup as complete', async () => {
    await setup({
      organizationId: 'org-9', adminUserId: 5, country: 'Pakistan', timeZone: 'Asia/Karachi', plan: 'BASIC',
      defaults: { attempted: true, completed: true, setupStatus: 'Completed', packLevel: 'Exact', fallbackUsed: false, categories: [] }
    });
    component.openNew();
    fillBasics();
    choose('PK');
    component.form.patchValue({ industryType: 'TRADING' });
    component.save();
    component.confirmCreate();
    fixture.detectChanges();

    expect(q('res-status')?.textContent).toContain('Setup complete');
    expect(q('res-problems')).toBeNull();
  });

  it('editing still uses the organization’s country name and a free-text timezone, never the create fields', async () => {
    await setup();
    service.getById.and.returnValue(of({ success: true, message: '', result: {
      id: 'org-1', orgCode: 'ACME', orgName: 'Acme', plan: 'BASIC', isActive: true, country: 'Pakistan', timeZone: 'Asia/Karachi', createdBy: 1, createdDate: ''
    } }));
    service.update.and.returnValue(of({ success: true, message: 'ok', result: null }));

    component.openEdit({ id: 'org-1', orgCode: 'ACME', orgName: 'Acme', plan: 'BASIC', isActive: true, createdDate: '' });
    fixture.detectChanges();
    component.save();

    const body = service.update.calls.mostRecent().args[1];
    expect(body.country).toBe('Pakistan');
    expect(body.timeZone).toBe('Asia/Karachi');
    expect(q('new-country')).toBeNull();
    expect(doubles.industries.byCountry).not.toHaveBeenCalled();
  });
});
