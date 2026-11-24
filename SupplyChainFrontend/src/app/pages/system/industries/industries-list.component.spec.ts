import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';
import { IndustriesListComponent } from './industries-list.component';
import { CountriesAdminService, CountryAdmin } from '../../../services/countries-admin.service';
import { Industry, IndustriesService } from '../../../services/industries.service';

/** A51 P1 - super admin Industries page. */
describe('IndustriesListComponent', () => {
  let fixture: ComponentFixture<IndustriesListComponent>;
  let c: IndustriesListComponent;
  let api: jasmine.SpyObj<IndustriesService>;
  let countriesApi: jasmine.SpyObj<CountriesAdminService>;
  const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);

  const industry = (id: number, code: string, name: string, countryCodes: string[], extra: Partial<Industry> = {}): Industry =>
    ({ id, code, name, description: null, isActive: true, sortOrder: id, countryCodes, ...extra });
  const country = (code: string, name: string): CountryAdmin => ({
    id: 'id-' + code, code, name, currencyCode: null, defaultTimezone: null, fiscalYearStartMonth: 1, dateFormat: 'DD/MM/YYYY', phoneCode: null, isActive: true, sortOrder: 0
  });
  const ok = <T>(result: T) => of({ success: true, message: '', result });
  const rows = () => [industry(1, 'TRADING', 'Trading', ['AE', 'PK']), industry(2, 'RETAIL', 'Retail', ['PK'], { isActive: false }), industry(3, 'MANUFACTURING', 'Manufacturing', [])];

  async function setup(listResult: any = ok(rows())) {
    api = jasmine.createSpyObj<IndustriesService>('IndustriesService', ['list', 'create', 'update', 'byCountry', 'assignToCountry', 'removeFromCountry', 'get']);
    api.list.and.returnValue(listResult);
    countriesApi = jasmine.createSpyObj<CountriesAdminService>('CountriesAdminService', ['list']);
    countriesApi.list.and.returnValue(ok([country('PK', 'Pakistan'), country('AE', 'United Arab Emirates'), country('SA', 'Saudi Arabia')]));
    await TestBed.configureTestingModule({
      imports: [IndustriesListComponent],
      providers: [provideRouter([]), provideNoopAnimations(), MessageService,
        { provide: IndustriesService, useValue: api }, { provide: CountriesAdminService, useValue: countriesApi }]
    }).compileComponents();
    fixture = TestBed.createComponent(IndustriesListComponent);
    c = fixture.componentInstance;
    fixture.detectChanges();
  }

  it('lists industries with their countries as tags', async () => {
    await setup();
    expect(c.rows.length).toBe(3);
    const tags = Array.from(q('countries-TRADING').querySelectorAll('.sf-pill')).map((e: any) => e.textContent.trim());
    expect(tags).toEqual(['AE', 'PK']);
    expect(q('countries-MANUFACTURING').textContent).toContain('None');
    expect(q('status-RETAIL').textContent).toContain('Inactive');
    expect(c.countries.length).toBe(3);
  });

  it('shows a retryable error when the list fails', async () => {
    await setup(throwError(() => ({ error: { message: 'boom' } })));
    expect(q('error')).toBeTruthy();
  });

  it('add: needs a valid code and a name, then creates with the chosen countries', async () => {
    await setup();
    c.openNew();
    expect(c.canSave).toBeFalse();
    c.form = { ...c.form, code: 'bad code', name: 'Health' };
    expect(c.canSave).toBeFalse();
    c.form = { ...c.form, code: 'HEALTH', countryCodes: ['PK', 'SA'] };
    expect(c.canSave).toBeTrue();
    api.create.and.returnValue(ok(industry(9, 'HEALTH', 'Health', ['PK', 'SA'])));
    c.save();
    const body = api.create.calls.mostRecent().args[0];
    expect(body.code).toBe('HEALTH');
    expect(body.countryCodes).toEqual(['PK', 'SA']);
    expect(c.dialogOpen).toBeFalse();
  });

  it('edit: the code is locked, the country selection starts from the saved one and is sent as the full list', async () => {
    await setup();
    c.openEdit(c.rows[0]);
    expect(c.editing?.id).toBe(1);
    expect(c.form.countryCodes).toEqual(['AE', 'PK']);
    c.form.countryCodes = ['PK', 'SA'];
    api.update.and.returnValue(ok(industry(1, 'TRADING', 'Trading', ['PK', 'SA'])));
    c.save();
    expect(api.update).toHaveBeenCalledWith(1, jasmine.objectContaining({ name: 'Trading', countryCodes: ['PK', 'SA'], isActive: true }));
    expect(api.create).not.toHaveBeenCalled();
  });

  it('the dialog renders one checkbox per country', async () => {
    await setup();
    c.openEdit(c.rows[0]);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const body = document.body;
    expect(body.querySelector('[data-testid="country-PK"]')).toBeTruthy();
    expect(body.querySelector('[data-testid="country-AE"]')).toBeTruthy();
    expect(body.querySelector('[data-testid="country-SA"]')).toBeTruthy();
  });

  it('a save failure keeps the dialog open with the server message', async () => {
    await setup();
    c.openNew();
    c.form = { ...c.form, code: 'TRADING', name: 'Dup' };
    api.create.and.returnValue(throwError(() => ({ error: { message: "An industry with code 'TRADING' already exists." } })));
    c.save();
    expect(c.dialogOpen).toBeTrue();
    expect(c.dialogError).toContain('already exists');
  });
});
