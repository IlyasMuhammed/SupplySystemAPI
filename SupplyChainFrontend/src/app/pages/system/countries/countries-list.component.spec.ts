import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';
import { CountriesListComponent } from './countries-list.component';
import { CountriesAdminService, CountryAdmin } from '../../../services/countries-admin.service';

/** A51 P1 - super admin Countries page. */
describe('CountriesListComponent', () => {
  let fixture: ComponentFixture<CountriesListComponent>;
  let c: CountriesListComponent;
  let api: jasmine.SpyObj<CountriesAdminService>;
  const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);

  const country = (code: string, name: string, extra: Partial<CountryAdmin> = {}): CountryAdmin => ({
    id: 'id-' + code, code, name, currencyCode: 'PKR', defaultTimezone: 'Asia/Karachi', fiscalYearStartMonth: 7, dateFormat: 'DD/MM/YYYY',
    phoneCode: '+92', isActive: true, sortOrder: 1, ...extra
  });
  const list = () => [country('PK', 'Pakistan'), country('AE', 'United Arab Emirates', { currencyCode: 'AED', fiscalYearStartMonth: 1, isActive: false })];
  const ok = <T>(result: T) => of({ success: true, message: '', result });

  async function setup(listResult: any = ok(list())) {
    api = jasmine.createSpyObj<CountriesAdminService>('CountriesAdminService', ['list', 'create', 'update', 'delete', 'activate', 'deactivate']);
    api.list.and.returnValue(listResult);
    await TestBed.configureTestingModule({
      imports: [CountriesListComponent],
      providers: [provideRouter([]), provideNoopAnimations(), MessageService, { provide: CountriesAdminService, useValue: api }]
    }).compileComponents();
    fixture = TestBed.createComponent(CountriesListComponent);
    c = fixture.componentInstance;
    fixture.detectChanges();
  }

  it('lists the countries with currency, timezone, fiscal start and status', async () => {
    await setup();
    expect(c.rows.length).toBe(2);
    const pk = q('row-PK').textContent;
    expect(pk).toContain('Pakistan');
    expect(pk).toContain('PKR');
    expect(pk).toContain('Asia/Karachi');
    expect(pk).toContain('July');
    expect(q('status-PK').textContent).toContain('Active');
    expect(q('status-AE').textContent).toContain('Inactive');
  });

  it('shows a retryable error when the list fails', async () => {
    await setup(throwError(() => ({ error: { message: 'boom' } })));
    expect(q('error')).toBeTruthy();
    expect(c.rows.length).toBe(0);
  });

  it('add: requires a code and a name, then creates with the typed values', async () => {
    await setup();
    c.openNew();
    expect(c.canSave).toBeFalse();
    c.form = { ...c.form, code: 'qa', name: 'Qatar', currencyCode: 'QAR', defaultTimezone: 'Asia/Qatar', phoneCode: '+974', fiscalYearStartMonth: 1 };
    expect(c.canSave).toBeTrue();
    api.create.and.returnValue(ok(country('QA', 'Qatar')));
    c.save();
    expect(api.create).toHaveBeenCalledTimes(1);
    const body = api.create.calls.mostRecent().args[0];
    expect(body.code).toBe('qa');
    expect(body.name).toBe('Qatar');
    expect(body.currencyCode).toBe('QAR');
    expect(body.phoneCode).toBe('+974');
    expect(c.dialogOpen).toBeFalse();
    expect(api.list).toHaveBeenCalledTimes(2); // reloaded
  });

  it('edit: the code is locked and update is called with the id', async () => {
    await setup();
    c.openEdit(c.rows[0]);
    expect(c.form.code).toBe('PK');
    expect(c.editing?.id).toBe('id-PK');
    c.form.fiscalYearStartMonth = 4;
    api.update.and.returnValue(ok(country('PK', 'Pakistan', { fiscalYearStartMonth: 4 })));
    c.save();
    expect(api.update).toHaveBeenCalledWith('id-PK', jasmine.objectContaining({ fiscalYearStartMonth: 4, name: 'Pakistan' }));
    expect(api.create).not.toHaveBeenCalled();
  });

  it('a save failure keeps the dialog open and shows the server message', async () => {
    await setup();
    c.openNew();
    c.form = { ...c.form, code: 'PK', name: 'Dup' };
    api.create.and.returnValue(throwError(() => ({ error: { message: "A country with code 'PK' already exists." } })));
    c.save();
    expect(c.dialogOpen).toBeTrue();
    expect(c.dialogError).toContain('already exists');
    expect(c.saving).toBeFalse();
  });

  it('activate / deactivate toggles the status in place', async () => {
    await setup();
    api.deactivate.and.returnValue(ok(country('PK', 'Pakistan', { isActive: false })));
    c.toggleActive(c.rows[0]);
    expect(api.deactivate).toHaveBeenCalledWith('id-PK');
    expect(c.rows[0].isActive).toBeFalse();

    api.activate.and.returnValue(ok(country('AE', 'United Arab Emirates', { isActive: true })));
    c.toggleActive(c.rows[1]);
    expect(api.activate).toHaveBeenCalledWith('id-AE');
    expect(c.rows[1].isActive).toBeTrue();
  });

  it('delete removes the row on success', async () => {
    await setup();
    api.delete.and.returnValue(ok(true));
    c.remove(c.rows[1]);
    expect(api.delete).toHaveBeenCalledWith('id-AE');
    expect(c.rows.map(r => r.code)).toEqual(['PK']);
  });
});
