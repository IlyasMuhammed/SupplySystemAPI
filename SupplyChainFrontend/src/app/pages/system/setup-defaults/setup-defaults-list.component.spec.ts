import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ConfirmationService, MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';
import { SetupDefaultsListComponent, packKey, parsePackFile } from './setup-defaults-list.component';
import { SetupDefaultsService } from '../../../services/setup-defaults.service';

/** A50 P1-M tasks 155–157, 164–166 — super admin Setup Defaults list. */
describe('SetupDefaultsListComponent', () => {
  let fixture: ComponentFixture<SetupDefaultsListComponent>;
  let c: SetupDefaultsListComponent;
  let api: jasmine.SpyObj<SetupDefaultsService>;
  const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);

  const row = (uuid: string, countryCode: string, industryType: string, dataCategory: string, extra: any = {}) => ({
    uuid, countryCode, industryType, dataCategory, displayName: `${countryCode} ${industryType} ${dataCategory}`, version: 1, isActive: true,
    notes: null, isSystemPack: true, recordCount: 6, appliedCount: 0, rowVersion: 'rv', ...extra
  });
  const rows = () => [row('a', 'PK', 'Trading', 'COA', { version: 3 }), row('b', 'PK', 'GLOBAL', 'TAX'), row('c', 'GLOBAL', 'GLOBAL', 'TAX', { isActive: false })];

  async function setup(list: any = of({ success: true, result: rows() })) {
    api = jasmine.createSpyObj<SetupDefaultsService>('SetupDefaultsService', ['list', 'meta', 'publish', 'unpublish', 'delete', 'export', 'import']);
    api.list.and.returnValue(list);
    api.meta.and.returnValue(of({ success: true, message: '', result: { categories: [{ code: 'COA', hasSeeder: true, hasSchema: true }, { code: 'TAX', hasSeeder: true, hasSchema: true }], countries: ['GLOBAL', 'PK'], industries: ['GLOBAL', 'Trading'] } }));
    await TestBed.configureTestingModule({
      imports: [SetupDefaultsListComponent],
      providers: [provideRouter([]), provideNoopAnimations(), MessageService, { provide: SetupDefaultsService, useValue: api }]
    }).compileComponents();
    fixture = TestBed.createComponent(SetupDefaultsListComponent);
    c = fixture.componentInstance;
    fixture.detectChanges();
  }

  it('lists every default with records, version and status', async () => {
    await setup();
    expect(c.filtered.length).toBe(3);
    expect(q('row-a').textContent).toContain('v3');
    expect(q('status-c').textContent).toContain('Unpublished');
    expect(c.categoryOptions.map(o => o.value)).toEqual(['COA', 'TAX']);
  });

  it('filters by country, industry, category, version and active', async () => {
    await setup();
    c.fCountry = 'PK'; expect(c.filtered.map(r => r.uuid)).toEqual(['a', 'b']);
    c.fIndustry = 'GLOBAL'; expect(c.filtered.map(r => r.uuid)).toEqual(['b']);
    c.fCountry = null; c.fIndustry = null; c.fCategory = 'TAX'; expect(c.filtered.map(r => r.uuid)).toEqual(['b', 'c']);
    c.fActive = false; expect(c.filtered.map(r => r.uuid)).toEqual(['c']);
    c.fCategory = null; c.fActive = null; c.fVersion = 3; expect(c.filtered.map(r => r.uuid)).toEqual(['a']);
    expect(c.versionOptions).toEqual([1, 3]);
  });

  it('publish / unpublish toggles IsActive', async () => {
    await setup();
    api.unpublish.and.returnValue(of({ success: true, message: '', result: { ...rows()[0], isActive: false } as any }));
    c.togglePublish(c.rows[0]);
    expect(api.unpublish).toHaveBeenCalledWith('a');
    expect(c.rows[0].isActive).toBeFalse();
    api.publish.and.returnValue(of({ success: true, message: '', result: { ...rows()[2], isActive: true } as any }));
    c.togglePublish(c.rows[2]);
    expect(c.rows[2].isActive).toBeTrue();
  });

  it('delete refused with 409 says to unpublish instead', async () => {
    await setup();
    const msgs = TestBed.inject(MessageService);
    spyOn(msgs, 'add');
    api.delete.and.returnValue(throwError(() => ({ status: 409, error: {} })));
    c.delete(c.rows[0]);
    expect(c.rows.length).toBe(3);
    expect((msgs.add as jasmine.Spy).calls.mostRecent().args[0].detail).toContain('Unpublish it instead');
    api.delete.and.returnValue(of({ success: true, message: '', result: null }));
    c.delete(c.rows[1]);
    expect(c.rows.map(r => r.uuid)).toEqual(['a', 'c']);
  });

  it('confirmDelete asks first', async () => {
    await setup();
    const confirm = fixture.debugElement.injector.get(ConfirmationService);
    spyOn(confirm, 'confirm');
    c.confirmDelete(c.rows[0]);
    expect((confirm.confirm as jasmine.Spy).calls.mostRecent().args[0].message).toContain('PK/Trading/COA');
  });

  it('import: parse → check (dry run) → confirm', async () => {
    await setup();
    c.openImport();
    c.readText(JSON.stringify({ format: 'sms-setup-defaults', defaults: [{ countryCode: 'AE', industryType: 'Trading', dataCategory: 'TAX', displayName: 'AE tax', payload: { taxCodes: [{}, {}] } }] }));
    fixture.detectChanges();
    expect(c.importItems.length).toBe(1);
    expect(c.count(c.importItems[0])).toBe(2);
    expect(c.canConfirmImport).toBeFalse();
    api.import.and.returnValue(of({ success: true, message: '', result: { dryRun: true, created: 1, updated: 0, unchanged: 0, errors: [], items: [{ key: 'AE/Trading/TAX', action: 'Created' }] } }));
    c.runImport(true);
    expect(api.import.calls.mostRecent().args[1]).toBeTrue();
    expect(c.actionOf(c.importItems[0])).toBe('Created');
    expect(c.canConfirmImport).toBeTrue();
    api.import.and.returnValue(of({ success: true, message: '', result: { dryRun: false, created: 1, updated: 0, unchanged: 0, errors: [], items: [] } }));
    c.runImport(false);
    expect(api.import.calls.mostRecent().args[1]).toBeFalse();
    expect(c.importOpen).toBeFalse();
    expect(api.list).toHaveBeenCalledTimes(2);
  });

  it('import: a refused file shows its per-item errors and cannot be confirmed', async () => {
    await setup();
    c.openImport();
    c.readText(JSON.stringify([{ countryCode: 'AE', industryType: 'Trading', dataCategory: 'TAX', payload: {} }]));
    api.import.and.returnValue(throwError(() => ({ status: 400, error: { message: 'Invalid', result: {
      dryRun: true, created: 0, updated: 0, unchanged: 0, errors: [{ index: 0, key: 'AE/Trading/TAX', errors: ['taxCodes required'] }], items: [] } } })));
    c.runImport(true);
    expect(c.actionOf(c.importItems[0])).toBe('Invalid');
    expect(c.canConfirmImport).toBeFalse();
    expect(c.importError).toBe('Invalid');
  });

  it('the toolbar offers Import from JSON and Export All', async () => {
    await setup();
    expect(q('import').textContent).toContain('Import from JSON');
    expect(q('export').textContent).toContain('Export All');
  });

  it('import: skipping invalid records lets the valid ones be confirmed and is sent to the API', async () => {
    await setup();
    c.openImport();
    c.readText(JSON.stringify([
      { countryCode: 'AE', industryType: 'RETAIL', dataCategory: 'TAX', payload: {} },
      { countryCode: 'PK', industryType: 'RETAIL', dataCategory: 'TAX', payload: {} }]));
    const dry = {
      dryRun: true, created: 1, updated: 0, unchanged: 0, invalid: 1, errors: [{ index: 0, key: 'AE/RETAIL/TAX', errors: ['bad'] }],
      items: [{ key: 'AE/RETAIL/TAX', action: 'Invalid' }, { key: 'PK/RETAIL/TAX', action: 'Created' }]
    };
    api.import.and.returnValue(of({ success: true, message: '', result: dry as any }));
    c.skipInvalid = true;
    c.runImport(true);
    expect(api.import.calls.mostRecent().args[3]).toBeTrue();
    expect(c.actionOf(c.importItems[0])).toBe('Invalid');
    expect(c.actionOf(c.importItems[1])).toBe('Created');
    expect(c.canConfirmImport).toBeTrue();
    c.skipInvalid = false;
    expect(c.canConfirmImport).toBeFalse();
  });

  it('parsePackFile accepts the pack file or a bare array and rejects the rest', () => {
    expect(parsePackFile('[{"countryCode":"PK","industryType":"GLOBAL","dataCategory":"TAX","payload":{}}]').length).toBe(1);
    expect(() => parsePackFile('nope')).toThrowError(/not valid JSON/);
    expect(() => parsePackFile('{"x":1}')).toThrowError(/Expected/);
    expect(() => parsePackFile('{"defaults":[]}')).toThrowError(/no defaults/);
    expect(() => parsePackFile('[{"countryCode":"PK"}]')).toThrowError(/Item 1/);
    expect(packKey({ countryCode: 'PK', industryType: 'Trading', dataCategory: 'COA' })).toBe('PK/Trading/COA');
  });

  it('export downloads the file', async () => {
    await setup();
    api.export.and.returnValue(of(new Blob(['{}'], { type: 'application/json' })));
    const click = jasmine.createSpy('click');
    const realCreate = document.createElement.bind(document);
    spyOn(document, 'createElement').and.callFake((tag: string) => {
      const el = realCreate(tag);
      if (tag === 'a') (el as HTMLAnchorElement).click = click;
      return el;
    });
    c.exportAll();
    expect(api.export).toHaveBeenCalled();
    expect(click).toHaveBeenCalled();
  });

  it('duplicate opens New with ?from', async () => {
    await setup();
    const router = TestBed.inject(Router);
    spyOn(router, 'navigate');
    c.duplicate(c.rows[0]);
    expect(router.navigate).toHaveBeenCalledWith(['/portal/pages/system/setup-defaults', 'new'], { queryParams: { from: 'a' } });
  });

  it('shows the load error', async () => {
    await setup(throwError(() => ({ status: 403 })));
    expect(q('error').textContent).toContain('permission');
  });
});
