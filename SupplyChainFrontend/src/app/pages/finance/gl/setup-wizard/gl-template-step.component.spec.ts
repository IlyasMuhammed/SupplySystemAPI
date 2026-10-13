import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { HttpErrorResponse } from '@angular/common/http';
import { ConfirmationService, MessageService } from 'primeng/api';
import { throwError } from 'rxjs';

import { GlTemplateStepComponent, MAX_UPLOAD_BYTES } from './gl-template-step.component';
import { AccountingService, CoaTemplate } from '../../../../services/accounting.service';
import { fakeApi, okRes } from '../gl.testing';

const tpl = (code: string, count: number): CoaTemplate => ({
  uuid: 't-' + code, templateCode: code, templateName: code + ' chart', countryCode: 'PAK', businessType: 'General', currencyCode: 'PKR',
  description: 'desc', accountCount: count, version: 1, isSystem: true
});

describe('GlTemplateStepComponent (A42 step 1)', () => {
  let fixture: ComponentFixture<GlTemplateStepComponent>;
  let c: GlTemplateStepComponent;
  let api: jasmine.SpyObj<AccountingService>;

  const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
  const qa = (id: string) => Array.from(fixture.nativeElement.querySelectorAll(`[data-testid="${id}"]`)) as HTMLElement[];

  async function setup(inputs: Partial<GlTemplateStepComponent> = {}) {
    api = fakeApi();
    api.getTemplates.and.returnValue(okRes([tpl('PK_GENERAL', 65), tpl('AE_TRADING', 0)]));
    api.getTemplateFilters.and.returnValue(okRes({ countries: [{ code: 'PAK', name: 'Pakistan' }], businessTypes: ['General', 'Trading'] }));
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [GlTemplateStepComponent],
      providers: [provideNoopAnimations(), MessageService, { provide: AccountingService, useValue: api }]
    }).compileComponents();
    fixture = TestBed.createComponent(GlTemplateStepComponent);
    c = fixture.componentInstance;
    Object.assign(c, { canManage: true, ...inputs });
    fixture.detectChanges();
  }

  afterEach(() => fixture?.destroy());

  it('lists cards with name, count and currency; filters come from /filters and reload the list', async () => {
    await setup();
    expect(q('tpl-PK_GENERAL')!.textContent).toContain('PK_GENERAL chart');
    expect(q('tpl-PK_GENERAL')!.textContent).toContain('65 accounts');
    expect(q('tpl-PK_GENERAL')!.textContent).toContain('PKR');
    expect(c.countryOptions).toEqual([{ label: 'Pakistan', value: 'PAK' }]);
    c.countryCode = 'PAK';
    c.load();
    expect(api.getTemplates).toHaveBeenCalledWith(jasmine.objectContaining({ countryCode: 'PAK' }));
  });

  it('a template with no accounts yet: Apply off and "Accounts for this template are coming"', async () => {
    await setup();
    const card = q('tpl-AE_TRADING')!;
    expect(card.textContent).toContain('Accounts for this template are coming');
    expect((card.querySelector('[data-testid="apply-AE_TRADING"] button') as HTMLButtonElement).disabled).toBeTrue();
    expect((q('apply-PK_GENERAL')!.querySelector('button') as HTMLButtonElement).disabled).toBeFalse();
  });

  it('Apply asks first (warning about the existing chart), then applies and emits', async () => {
    await setup({ existingAccounts: 12 });
    const confirm = fixture.debugElement.injector.get(ConfirmationService);
    const spy = spyOn(confirm, 'confirm').and.callFake((o: any) => { o.accept(); return confirm; });
    api.applyTemplate.and.returnValue(okRes({ accountsCreated: 65, defaultsAssigned: 12, status: 'AccountsCreated' as const }));
    let emitted = 0;
    c.applied.subscribe(() => emitted++);
    c.confirmApply(c.templates[0]);
    expect(spy.calls.mostRecent().args[0].message).toContain('replaces the 12 accounts');
    expect(api.applyTemplate).toHaveBeenCalledWith('t-PK_GENERAL');
    expect(emitted).toBe(1);
  });

  it('locked (fiscal year set): no apply, no upload', async () => {
    await setup({ locked: true });
    expect(q('tpl-locked')).not.toBeNull();
    c.confirmApply(c.templates[0]);
    expect(api.applyTemplate).not.toHaveBeenCalled();
  });

  it('preview opens the side panel with the flat list', async () => {
    await setup();
    api.previewTemplate.and.returnValue(okRes([
      { accountCode: '1', accountName: 'Assets', level: 1, rootType: 'Asset', isGroup: true, isDefaultAccount: false },
      { accountCode: '1111', accountName: 'Cash', level: 4, rootType: 'Asset', isGroup: false, accountType: 'Cash', isDefaultAccount: true, defaultAccountKey: 'DEFAULT_CASH' }
    ] as any));
    c.preview(c.templates[0]);
    expect(c.previewVisible).toBeTrue();
    expect(c.previewItems.length).toBe(2);
  });

  it('upload zone takes .csv/.json up to 5 MB only', async () => {
    await setup();
    c.pick(new File(['x'], 'chart.xlsx'));
    expect(c.fileError).toBe('Only .csv or .json files can be uploaded.');
    expect(c.file).toBeNull();
    const big = new File(['x'], 'chart.csv');
    Object.defineProperty(big, 'size', { value: MAX_UPLOAD_BYTES + 1 });
    c.pick(big);
    expect(c.fileError).toContain('5 MB');
    c.pick(new File(['x'], 'chart.JSON'));
    expect(c.fileError).toBe('');
    expect(c.file!.name).toBe('chart.JSON');
  });

  it('a 400 lists every error with its line number, in line order', async () => {
    await setup();
    c.pick(new File(['x'], 'chart.csv'));
    api.uploadChart.and.returnValue(throwError(() => new HttpErrorResponse({ status: 400, error: {
      message: 'The file has 2 errors.',
      errors: [{ line: 9, code: '9999', rule: 'R4', message: 'Account code 9999 must start with 1.' },
               { line: 3, code: '1100', rule: 'R2', message: 'Account code 1100 already exists.' }]
    } })));
    c.upload();
    fixture.detectChanges();
    expect(q('upload-error')!.textContent).toContain('The file has 2 errors.');
    const lines = qa('upload-error-line').map(e => e.textContent!.replace(/\s+/g, ' ').trim());
    expect(lines[0]).toContain('Line 3');
    expect(lines[0]).toContain('already exists');
    expect(lines[1]).toContain('Line 9');
  });

  it('a successful upload emits like Apply', async () => {
    await setup();
    c.pick(new File(['x'], 'chart.csv'));
    api.uploadChart.and.returnValue(okRes({ accountsCreated: 80, defaultsAssigned: 0, status: 'AccountsCreated' as const }));
    let emitted = 0;
    c.applied.subscribe(() => emitted++);
    c.confirmUpload();
    expect(emitted).toBe(1);
    expect(c.file).toBeNull();
  });

  it('Start with a blank chart: confirmation, POST start-blank, emits (wizard → step 2)', async () => {
    await setup({ existingAccounts: 60 });
    expect(q('start-blank')!.textContent).toContain('Start with a blank chart (add accounts yourself)');
    const confirm = fixture.debugElement.injector.get(ConfirmationService);
    const spy = spyOn(confirm, 'confirm').and.callFake((o: any) => { o.accept(); return confirm; });
    api.startBlankChart.and.returnValue(okRes({ accountsCreated: 8, defaultsAssigned: 1, status: 'AccountsCreated' as const }));
    let emitted = 0;
    c.applied.subscribe(() => emitted++);
    c.confirmBlank();
    expect(spy.calls.mostRecent().args[0].message).toContain('replaces the 60 accounts');
    expect(api.startBlankChart).toHaveBeenCalled();
    expect(emitted).toBe(1);
  });

  it('a 409 on apply / start-blank (fiscal year exists) is shown inline in the server words', async () => {
    await setup();
    const msg = 'The chart can only be replaced before the fiscal year is set and before opening balances are posted.';
    api.startBlankChart.and.returnValue(throwError(() => new HttpErrorResponse({ status: 409, error: { success: false, message: msg } })));
    c.startBlank();
    fixture.detectChanges();
    expect(q('action-error')!.textContent).toContain(msg);
    api.applyTemplate.and.returnValue(throwError(() => new HttpErrorResponse({ status: 409, error: { success: false, message: msg } })));
    c.actionError = '';
    c.apply(c.templates[0]);
    expect(c.actionError).toBe(msg);
  });

  it('read-only users see no Apply and no upload zone', async () => {
    await setup({ canManage: false });
    expect(q('start-blank')).toBeNull();
    expect(q('apply-PK_GENERAL')).toBeNull();
    expect(q('drop-zone')).toBeNull();
    expect(q('preview-PK_GENERAL')).not.toBeNull();
  });
});
