import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';
import { SetupDefaultEditorComponent, emptyPayload } from './setup-default-editor.component';
import { SetupDefaultsService } from '../../../services/setup-defaults.service';
import { AuthService } from '../../service/auth.service';
import { sampleCoa } from '../../onboarding/onboarding.testing';

/** A50 tasks 157–163 — add / edit one setup default with the category editors and the JSON fallback. */
describe('SetupDefaultEditorComponent', () => {
  let fixture: ComponentFixture<SetupDefaultEditorComponent>;
  let c: SetupDefaultEditorComponent;
  let api: jasmine.SpyObj<SetupDefaultsService>;
  const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);

  const detail = (dataCategory: string, dataPayload: any, extra: any = {}) => ({
    uuid: 'u1', countryCode: 'PK', industryType: 'Trading', dataCategory, displayName: `PK Trading — ${dataCategory}`, version: 2, isActive: true,
    notes: null, recordCount: 1, appliedCount: 3, rowVersion: 'rv1', dataPayload, ...extra
  });

  async function setup(params: { uuid?: string; from?: string } = {}, get: any = null) {
    api = jasmine.createSpyObj<SetupDefaultsService>('SetupDefaultsService', ['meta', 'get', 'create', 'update', 'validate']);
    api.meta.and.returnValue(of({ success: true, message: '', result: { categories: [], countries: ['PK'], industries: ['Trading'] } }));
    if (get) api.get.and.returnValue(get);
    await TestBed.configureTestingModule({
      imports: [SetupDefaultEditorComponent],
      providers: [
        provideRouter([]), provideNoopAnimations(), MessageService,
        { provide: SetupDefaultsService, useValue: api },
        { provide: AuthService, useValue: { getRoleList: () => of({ result: [] }), getRoleDetail: () => of(null) } },
        { provide: ActivatedRoute, useValue: { snapshot: {
          paramMap: convertToParamMap(params.uuid ? { uuid: params.uuid } : {}),
          queryParamMap: convertToParamMap(params.from ? { from: params.from } : {}) } } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(SetupDefaultEditorComponent);
    c = fixture.componentInstance;
    fixture.detectChanges();
  }

  it('new: validates the key, then creates with the payload object', async () => {
    await setup({ uuid: 'new' });
    expect(c.isNew).toBeTrue();
    c.countryCode = 'pak';
    expect(c.problem).toContain('Country');
    c.countryCode = 'AE';
    c.industryType = 'Trading';
    expect(c.problem).toContain('category');
    c.setCategory('TAX');
    c.displayName = 'AE Trading — TAX';
    expect(c.problem).toBeNull();
    fixture.detectChanges();
    expect(q('tax-editor')).toBeTruthy();
    api.create.and.returnValue(of({ success: true, message: '', result: detail('TAX', { taxCodes: [] }, { uuid: 'n1' }) as any }));
    const router = TestBed.inject(Router);
    spyOn(router, 'navigate');
    c.save();
    const body = api.create.calls.mostRecent().args[0];
    expect(body).toEqual(jasmine.objectContaining({ countryCode: 'AE', industryType: 'Trading', dataCategory: 'TAX', isActive: true }));
    expect(body.dataPayload).toEqual({ verified: false, taxCodes: [] });
    expect(router.navigate).toHaveBeenCalledWith(['/portal/pages/system/setup-defaults', 'n1']);
  });

  it('edit: the key is read-only; PUT sends the rowVersion; COA uses the tree editor', async () => {
    await setup({ uuid: 'u1' }, of({ success: true, message: '', result: detail('COA', { accounts: sampleCoa() }) }));
    expect(c.isNew).toBeFalse();
    fixture.detectChanges();
    expect((q('country') as HTMLInputElement).readOnly).toBeTrue();
    expect(q('coa-editor')).toBeTruthy();
    expect(c.recordCount).toBe(7);
    api.update.and.returnValue(of({ success: true, message: '', result: detail('COA', { accounts: sampleCoa() }, { version: 3 }) as any }));
    c.save();
    expect(api.update).toHaveBeenCalledWith('u1', jasmine.objectContaining({ rowVersion: 'rv1', displayName: 'PK Trading — COA' }));
    expect(c.row?.version).toBe(3);
  });

  it('a stale rowVersion (409) explains and keeps the edit', async () => {
    await setup({ uuid: 'u1' }, of({ success: true, message: '', result: detail('TAX', { taxCodes: [] }) }));
    api.update.and.returnValue(throwError(() => ({ status: 409, error: {} })));
    c.save();
    expect(c.saveError).toContain('Someone else changed');
  });

  it('categories without an editor use JSON; invalid JSON blocks Save', async () => {
    await setup({ uuid: 'u1' }, of({ success: true, message: '', result: detail('FISCAL', { startMonth: 7 }) }));
    expect(c.jsonMode).toBeTrue();
    fixture.detectChanges();
    expect(q('json')).toBeTruthy();
    expect(q('toggle-json')).toBeNull();
    c.jsonText = '{ "startMonth": ';
    c.onJsonText();
    expect(c.problem).toBe('Fix the JSON first.');
    c.jsonText = '[1]';
    c.onJsonText();
    expect(c.jsonError).toContain('JSON object');
    c.jsonText = '{ "startMonth": 1 }';
    c.onJsonText();
    expect(c.problem).toBeNull();
    expect(c.payload).toEqual({ startMonth: 1 });
  });

  it('any category can switch to JSON and back', async () => {
    await setup({ uuid: 'u1' }, of({ success: true, message: '', result: detail('UOM', { groups: [] }) }));
    expect(c.jsonMode).toBeFalse();
    c.toggleJson();
    expect(c.jsonMode).toBeTrue();
    expect(c.jsonText).toContain('"groups"');
    c.jsonText = '{ "groups": [ { "name": "Count", "units": [ { "code": "PCS", "name": "Piece", "isBase": true } ] } ] }';
    c.onJsonText();
    c.toggleJson();
    expect(c.jsonMode).toBeFalse();
    expect(c.payload.groups[0].units[0].code).toBe('PCS');
  });

  it('PRODUCT_CATEGORIES uses the tree editor and blocks Save on a duplicate code', async () => {
    await setup({ uuid: 'u1' }, of({ success: true, message: '', result: detail('PRODUCT_CATEGORIES', { categories: [{ code: 'A', name: 'Alpha' }] }) }));
    fixture.detectChanges();
    expect(c.hasEditor).toBeTrue();
    expect(q('pc-editor')).toBeTruthy();
    expect(c.problem).toBeNull();
    c.onPayload({ categories: [{ code: 'A', name: 'Alpha' }, { code: 'a', name: 'Again' }] });
    expect(c.problem).toContain('used twice');
  });

  it('JSON mode shows the syntax state and formats valid JSON', async () => {
    await setup({ uuid: 'u1' }, of({ success: true, message: '', result: detail('FISCAL', { startMonth: 7 }) }));
    fixture.detectChanges();
    expect(q('json-status').textContent).toContain('JSON is valid');
    c.jsonText = '{"startMonth":1}';
    c.onJsonText();
    c.formatJson();
    expect(c.jsonText).toBe('{\n  "startMonth": 1\n}');
    c.jsonText = '{ nope';
    c.onJsonText();
    fixture.detectChanges();
    expect(q('json-status').textContent).toContain('Invalid JSON');
    expect(c.jsonError).toContain('Not valid JSON');
  });

  it('Check calls the server validation and lists the errors', async () => {
    await setup({ uuid: 'u1' }, of({ success: true, message: '', result: detail('TAX', { taxCodes: [] }) }));
    api.validate.and.returnValue(of({ success: true, message: '', result: { valid: false, errors: ['$.taxCodes: required'] } }));
    c.validate();
    fixture.detectChanges();
    expect(api.validate).toHaveBeenCalledWith('TAX', { taxCodes: [] });
    expect(q('validation').textContent).toContain('$.taxCodes: required');
  });

  it('duplicate (?from) copies the payload as a new, unpublished default', async () => {
    await setup({ from: 'u1' }, of({ success: true, message: '', result: detail('ROLES', { roles: [] }) }));
    expect(c.isNew).toBeTrue();
    expect(c.displayName).toContain('(copy)');
    expect(c.isActive).toBeFalse();
    expect(c.dataCategory).toBe('ROLES');
  });

  it('load error', async () => {
    await setup({ uuid: 'u1' }, throwError(() => ({ status: 404 })));
    expect(q('load-error').textContent).toContain('not found');
  });

  it('emptyPayload gives a valid starting shape per category', () => {
    expect(emptyPayload('COA').accounts.length).toBe(5);
    expect(emptyPayload('TAX')).toEqual({ verified: false, taxCodes: [] });
    expect(emptyPayload('UNKNOWN')).toEqual({});
  });
});
