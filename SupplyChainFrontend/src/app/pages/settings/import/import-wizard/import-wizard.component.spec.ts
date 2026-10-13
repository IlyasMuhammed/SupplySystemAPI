import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { Router, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { HttpErrorResponse, HttpResponse } from '@angular/common/http';
import { WritableSignal, signal } from '@angular/core';
import { MessageService } from 'primeng/api';
import { Subject, of, throwError } from 'rxjs';

import { ImportWizardComponent } from './import-wizard.component';
import {
  ColumnMapping, ImportProfileDetail, ImportProgressEvent, ImportService, SessionDetail, UploadEvent, ValidationResult, watchImportProgress
} from '../../../../services/import.service';
import { ModuleService } from '../../../../services/module.service';
import { AuthService } from '../../../service/auth.service';

const FIELDS = [
  { fieldName: 'code', displayName: 'Customer code', fieldType: 'TEXT', isRequired: true, isKey: true, sortOrder: 1 },
  { fieldName: 'name', displayName: 'Name', fieldType: 'TEXT', isRequired: true, isKey: false, sortOrder: 2 },
  { fieldName: 'email', displayName: 'Email', fieldType: 'EMAIL', isRequired: false, isKey: false, sortOrder: 3 },
  { fieldName: 'external_id', displayName: 'External ID', fieldType: 'TEXT', isRequired: false, isKey: true, sortOrder: 4 }
];

function profile(o: Partial<ImportProfileDetail> = {}): ImportProfileDetail {
  return {
    entityType: 'CUSTOMER', name: 'Customers', importOrder: 7, isOpeningBalance: false, matchFields: ['code'], duplicateStrategy: 'SKIP',
    maxRowsPerFile: 50000, maxFileSizeMb: 10, allowUpdate: true, requiresOpeningBalancePermission: false, completedSessions: 0,
    fields: FIELDS, ...o
  };
}

const MAPPINGS: ColumnMapping[] = [
  { sourceColumnIndex: 0, sourceColumnName: 'Customer Code', targetFieldName: 'code', isAutoMapped: true, confidenceScore: 1 },
  { sourceColumnIndex: 1, sourceColumnName: 'Cust Name', targetFieldName: 'name', isAutoMapped: true, confidenceScore: 0.82 },
  { sourceColumnIndex: 2, sourceColumnName: 'Mail', targetFieldName: null, isAutoMapped: false, confidenceScore: null }
];

function session(o: Partial<SessionDetail> = {}): SessionDetail {
  return {
    uuid: 's1', entityType: 'CUSTOMER', entityName: 'Customers', fileName: 'customers.xlsx', status: 'MAPPING', totalRows: 3, validRows: 0,
    invalidRows: 0, importedRows: 0, updatedRows: 0, skippedRows: 0, errorRows: 0, createdAt: '2026-10-11T08:00:00Z', createdByName: 'Ayesha',
    fileType: 'XLSX', fileSizeBytes: 2048, sheetName: 'Sheet1', sheetNames: ['Sheet1'], duplicateStrategy: 'SKIP',
    mappings: MAPPINGS, unmappedColumns: ['Mail'], unmappedRequiredFields: [], ...o
  };
}

const VALIDATION: ValidationResult = {
  session: session({ status: 'VALIDATED', validRows: 2, invalidRows: 1 }),
  errors: [{ rowNumber: 4, fieldName: 'email', errorCode: 'INVALID_EMAIL', message: 'Not an email address.', sourceValue: 'abc' }],
  errorCountByCode: { INVALID_EMAIL: 1, REQUIRED: 3 }
};

describe('ImportWizardComponent (A40 4-step wizard)', () => {
  let harness: RouterTestingHarness;
  let c: ImportWizardComponent;
  let api: jasmine.SpyObj<ImportService>;
  let permissions: string[];
  let features: string[];
  let hubEvents: Subject<ImportProgressEvent>;
  let hubConnected: WritableSignal<boolean>;
  let toasts: jasmine.Spy;

  const el = () => harness.routeNativeElement as HTMLElement;
  const q = (id: string) => el().querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
  const qa = (id: string) => Array.from(el().querySelectorAll(`[data-testid="${id}"]`)) as HTMLElement[];
  const btn = (id: string) => q(id)?.querySelector('button') as HTMLButtonElement | null;
  const detect = () => harness.detectChanges();

  async function setup(o: { profile?: ImportProfileDetail; perms?: string[]; feats?: string[]; url?: string; openingDate?: string | null;
                            resumed?: SessionDetail } = {}) {
    permissions = o.perms ?? ['IMPORT_PROFILE_VIEW', 'IMPORT_UPLOAD', 'IMPORT_EXECUTE'];
    features = o.feats ?? ['FEATURE_OPENING_BALANCE_IMPORT'];
    hubEvents = new Subject<ImportProgressEvent>();
    hubConnected = signal(true);
    const hub = { connect: () => {}, connected: hubConnected, events$: hubEvents.asObservable() };

    api = jasmine.createSpyObj<ImportService>('ImportService', [
      'getProfile', 'getPresets', 'getOpeningBalanceDate', 'upload', 'getSession', 'updateMappings', 'savePreset', 'getPreview', 'validate',
      'execute', 'cancel', 'getProgress', 'watchProgress', 'downloadTemplate', 'downloadErrors', 'downloadResults'
    ]);
    api.getProfile.and.returnValue(of({ success: true, message: '', result: o.profile ?? profile() }));
    api.getPresets.and.returnValue(of({ success: true, message: '', result: [{ name: 'Old ERP', lastUsedAt: null }] }));
    api.getOpeningBalanceDate.and.returnValue(of({ success: true, message: '', result: { date: o.openingDate === undefined ? '2026-01-01' : o.openingDate } }));
    api.upload.and.returnValue(of<UploadEvent[]>({ type: 'progress', percent: 40 }, { type: 'done', session: session() }));
    // Like the server: answers with the new mapping and the required fields (without a default) still unmapped.
    const fields = (o.profile ?? profile()).fields;
    api.updateMappings.and.callFake((_u, m) => of({ success: true, message: '', result: session({
      mappings: m.map(x => ({ ...MAPPINGS[x.sourceColumnIndex], targetFieldName: x.targetFieldName, isAutoMapped: false, confidenceScore: null })),
      unmappedRequiredFields: fields.filter(f => f.isRequired && !m.some(x => x.targetFieldName === f.fieldName)).map(f => f.fieldName) }) }));
    api.savePreset.and.returnValue(of({ success: true, message: '', result: null }));
    api.getPreview.and.returnValue(of({ success: true, message: '', result: {
      columns: [{ fieldName: 'code', displayName: 'Customer code' }, { fieldName: 'email', displayName: 'Email' }],
      rows: [{ rowNumber: 2, values: { code: 'C1', email: 'a@b.c' } }, { rowNumber: 4, values: { code: 'C3', email: 'abc' }, issues: { email: 'Not an email address.' } }]
    } }));
    api.validate.and.returnValue(of({ success: true, message: '', result: VALIDATION }));
    api.execute.and.returnValue(of({ success: true, message: '', result: { isBackground: false, session: session({
      status: 'COMPLETED_WITH_ERRORS', validRows: 2, invalidRows: 1, importedRows: 2, importStartedAt: '2026-10-11T08:00:00Z',
      importCompletedAt: '2026-10-11T08:00:04Z' }) } }));
    api.cancel.and.returnValue(of({ success: true, message: '', result: session({ status: 'CANCELLED' }) }));
    api.getProgress.and.returnValue(of({ success: true, message: '', result: { processed: 0, total: 900, status: 'IMPORTING' } }));
    api.watchProgress.and.callFake((uuid: string) => watchImportProgress(hub, id => api.getProgress(id), uuid, 20));
    api.getSession.and.returnValue(of({ success: true, message: '', result: o.resumed ?? session() }));

    TestBed.resetTestingModule().configureTestingModule({
      providers: [
        provideNoopAnimations(),
        provideRouter([{ path: 'import/:entityType', component: ImportWizardComponent }]),
        { provide: ImportService, useValue: api },
        { provide: AuthService, useValue: { hasPermission: (p: string) => permissions.includes(p) } },
        { provide: ModuleService, useValue: { isFeatureEnabled: (x: string) => features.includes(x) } }
      ]
    });
    harness = await RouterTestingHarness.create();
    c = await harness.navigateByUrl(o.url ?? '/import/CUSTOMER', ImportWizardComponent);
    toasts = spyOn(harness.fixture.debugElement.query(sel => sel.componentInstance === c).injector.get(MessageService), 'add');
    spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    detect();
  }

  function pickFile(name = 'customers.xlsx', size = 10) {
    c.setFile(new File([new Uint8Array(size)], name));
    detect();
  }

  async function toMap() {
    pickFile();
    c.upload();
    detect();
  }

  async function toValidate() {
    await toMap();
    c.nextFromMap();
    detect();
  }

  // ── Stages ──────────────────────────────────────────────────────────────

  it('shows the four stages with Upload current', async () => {
    await setup();
    expect(c.stages.map(s => s.label)).toEqual(['Upload', 'Map', 'Validate', 'Results']);
    expect(c.stages.map(s => s.state)).toEqual(['on', 'todo', 'todo', 'todo']);
    expect(q('constraints')!.textContent).toContain('10 MB');
    expect(q('constraints')!.textContent).toContain('50,000 rows');
  });

  // ── Upload ──────────────────────────────────────────────────────────────

  it('duplicate strategies: Skip (default) / Update / Error', async () => {
    await setup();
    expect(c.strategyOptions.map(s => s.value)).toEqual(['SKIP', 'UPDATE', 'ERROR']);
    expect(c.duplicateStrategy).toBe('SKIP');
    expect(q('strategy-UPDATE')).not.toBeNull();
  });

  it('hides Update when the profile does not allow updates', async () => {
    await setup({ profile: profile({ allowUpdate: false }) });
    expect(c.strategyOptions.map(s => s.value)).toEqual(['SKIP', 'ERROR']);
    expect(q('strategy-UPDATE')).toBeNull();
  });

  it('takes the org\'s own default strategy when it is offered', async () => {
    await setup({ profile: profile({ duplicateStrategy: 'UPDATE' }) });
    expect(c.duplicateStrategy).toBe('UPDATE');
  });

  it('Upload is disabled until a valid file is chosen; wrong type and size give the server\'s sentences', async () => {
    await setup();
    expect(btn('upload')!.disabled).toBeTrue();
    pickFile('customers.xls');
    expect(q('file-error')!.textContent).toContain('Only .xlsx and .csv files are accepted. Please save the file as .xlsx (Excel 2007+).');
    expect(btn('upload')!.disabled).toBeTrue();
    c.profile!.maxFileSizeMb = 1;
    pickFile('big.csv', 1024 * 1024 + 1);
    expect(q('file-error')!.textContent).toContain('File exceeds the maximum size of 1 MB.');
    c.profile!.maxFileSizeMb = 10;
    pickFile('customers.csv');
    expect(q('file-error')).toBeNull();
    expect(btn('upload')!.disabled).toBeFalse();
  });

  it('accepts a dropped file', async () => {
    await setup();
    const file = new File(['x'], 'dropped.csv');
    c.onDrop({ preventDefault: () => {}, dataTransfer: { files: [file] } } as any);
    detect();
    expect(q('file-name')!.textContent).toContain('dropped.csv');
  });

  it('uploads with strategy, preset and notes, then moves to Map', async () => {
    await setup();
    expect(c.presetOptions).toEqual([{ label: 'Old ERP', value: 'Old ERP' }]);
    pickFile();
    c.duplicateStrategy = 'ERROR';
    c.mappingPreset = 'Old ERP';
    c.notes = 'from legacy';
    c.upload();
    detect();
    expect(api.upload).toHaveBeenCalledOnceWith(jasmine.objectContaining({
      entityType: 'CUSTOMER', duplicateStrategy: 'ERROR', mappingPreset: 'Old ERP', notes: 'from legacy', sheetName: null }));
    expect(c.step).toBe(1);
    expect(c.stages.map(s => s.state)).toEqual(['done', 'on', 'todo', 'todo']);
  });

  it('shows the server\'s refusal verbatim and stays on Upload', async () => {
    await setup();
    api.upload.and.returnValue(throwError(() => new HttpErrorResponse({ status: 400, error: { message: 'File exceeds the maximum of 50000 rows.' } })));
    pickFile();
    c.upload();
    detect();
    expect(q('upload-error')!.textContent).toContain('File exceeds the maximum of 50000 rows.');
    expect(c.step).toBe(0);
  });

  it('sheet picker: several sheets → choose one → uploaded again with sheetName → Map', async () => {
    await setup();
    api.upload.and.returnValues(
      of<UploadEvent[]>({ type: 'done', session: session({ sheetNames: ['Notes', 'Customers'], sheetName: 'Notes' }) }),
      of<UploadEvent[]>({ type: 'done', session: session({ sheetNames: ['Notes', 'Customers'], sheetName: 'Customers' }) }));
    pickFile();
    c.upload();
    detect();
    expect(q('sheet-picker')).not.toBeNull();
    expect(c.step).toBe(0);
    expect(c.sheetChoice).toBe('Notes');
    expect(q('use-sheet')).not.toBeNull();
    c.sheetChoice = 'Customers';
    c.useSheet();
    detect();
    expect(api.upload.calls.mostRecent().args[0].sheetName).toBe('Customers');
    expect(c.step).toBe(1);
  });

  it('sheet picker: keeping the sheet the server read does not upload again', async () => {
    await setup();
    api.upload.and.returnValue(of<UploadEvent[]>({ type: 'done', session: session({ sheetNames: ['Data', 'Lists'], sheetName: 'Data' }) }));
    pickFile();
    c.upload();
    c.useSheet();
    expect(api.upload).toHaveBeenCalledTimes(1);
    expect(c.step).toBe(1);
  });

  it('downloads the template', async () => {
    await setup();
    spyOn(URL, 'createObjectURL').and.returnValue('blob:x');
    spyOn(URL, 'revokeObjectURL');
    const anchors: HTMLAnchorElement[] = [];
    spyOn(HTMLAnchorElement.prototype, 'click').and.callFake(function (this: HTMLAnchorElement) { anchors.push(this); });
    api.downloadTemplate.and.returnValue(of(new HttpResponse({ body: new Blob(['x']) })));
    c.downloadTemplate();
    expect(api.downloadTemplate).toHaveBeenCalledWith('CUSTOMER');
    expect(anchors[0].download).toBe('customer-import-template.xlsx');
  });

  // ── Map ─────────────────────────────────────────────────────────────────

  it('shows each column with its target and match mark', async () => {
    await setup();
    await toMap();
    expect(q('file-info')!.textContent).toContain('customers.xlsx');
    expect(q('mark-0')!.textContent).toContain('✓');
    expect(q('mark-1')!.textContent).toContain('~');
    expect(q('mark-1')!.textContent).toContain('82%');
    expect(q('mark-2')!.textContent).toContain('✗');
    expect(c.targetOptions[0]).toEqual({ label: '— Ignore —', value: null });
    expect(btn('map-next')!.disabled).toBeFalse();
  });

  it('Next is disabled while a required field is unmapped, and the field is highlighted', async () => {
    await setup();
    await toMap();
    c.onTargetChange(c.rows[1], null);
    detect();
    expect(q('missing-name')).not.toBeNull();
    expect(qa('map-problem').map(e => e.textContent!.trim())).toContain('Map required field: Name.');
    expect(btn('map-next')!.disabled).toBeTrue();
  });

  it('a required field the server does not ask for (it has a default) does not block Next', async () => {
    const withCountry = [...FIELDS, { fieldName: 'country', displayName: 'Country', fieldType: 'TEXT', isRequired: true, isKey: false, sortOrder: 5 }];
    await setup({ profile: profile({ fields: withCountry }) });
    api.upload.and.returnValue(of<UploadEvent[]>({ type: 'done', session: session({ unmappedRequiredFields: [] }) }));
    await toMap();
    expect(c.missingRequired).toEqual([]);
    expect(c.problems).toEqual([]);
    expect(btn('map-next')!.disabled).toBeFalse();
  });

  it('Next needs a key field (IMP-MAP-02)', async () => {
    await setup({ profile: profile({ fields: FIELDS.map(f => f.fieldName === 'code' ? { ...f, isRequired: false } : f) }) });
    await toMap();
    c.onTargetChange(c.rows[0], null);
    detect();
    expect(c.problems).toContain('Map at least one key field: Customer code, External ID.');
    expect(btn('map-next')!.disabled).toBeTrue();
  });

  it('PUTs the whole mapping on every change; a manual choice shows —', async () => {
    await setup();
    await toMap();
    c.onTargetChange(c.rows[2], 'email');
    detect();
    expect(api.updateMappings).toHaveBeenCalledOnceWith('s1', [
      { sourceColumnIndex: 0, targetFieldName: 'code' }, { sourceColumnIndex: 1, targetFieldName: 'name' },
      { sourceColumnIndex: 2, targetFieldName: 'email' }]);
    expect(q('mark-2')!.textContent).toContain('—');
  });

  it('a field mapped twice: flagged, not sent, Next disabled; resolving it saves', async () => {
    await setup();
    await toMap();
    c.onTargetChange(c.rows[2], 'name');
    detect();
    expect(api.updateMappings).not.toHaveBeenCalled();
    expect(c.problems).toContain("Field 'Name' is mapped twice.");
    expect(q('map-row-1')!.className).toContain('imp-dup');
    expect(btn('map-next')!.disabled).toBeTrue();
    c.onTargetChange(c.rows[2], 'email');
    expect(api.updateMappings).toHaveBeenCalledTimes(1);
    detect();
    expect(btn('map-next')!.disabled).toBeFalse();
  });

  it('a refused PUT shows the server\'s sentence and keeps the choice', async () => {
    await setup();
    await toMap();
    api.updateMappings.and.returnValue(throwError(() => new HttpErrorResponse({ status: 400, error: { message: 'Mappings cannot change once the import has started.' } })));
    c.onTargetChange(c.rows[2], 'email');
    detect();
    expect(q('mapping-error')!.textContent).toContain('Mappings cannot change once the import has started.');
    expect(c.rows[2].target).toBe('email');
    expect(c.mappingDirty).toBeTrue();
  });

  it('"Save mapping as…" posts the preset name and reloads the presets', async () => {
    await setup();
    await toMap();
    c.presetName = '  Old ERP v2 ';
    c.savePreset();
    expect(api.savePreset).toHaveBeenCalledOnceWith('s1', 'Old ERP v2');
    expect(api.getPresets).toHaveBeenCalledTimes(2);
  });

  // ── Validate ────────────────────────────────────────────────────────────

  it('Validate: preview with issue markers, Run validation, summary, errors by code, import button', async () => {
    await setup();
    await toValidate();
    expect(c.step).toBe(2);
    expect(api.getPreview).toHaveBeenCalledWith('s1', 20);
    expect(qa('preview-row').length).toBe(2);
    expect(el().querySelector('td[data-issue="Not an email address."]')).not.toBeNull();
    expect(btn('execute')!.disabled).toBeTrue();

    c.runValidation();
    detect();
    expect(api.validate).toHaveBeenCalledOnceWith('s1');
    expect(q('valid-rows')!.textContent).toContain('2');
    expect(q('invalid-rows')!.textContent).toContain('1');
    expect(q('total-rows')!.textContent).toContain('3');
    expect(c.errorGroups).toEqual([{ code: 'REQUIRED', count: 3 }, { code: 'INVALID_EMAIL', count: 1 }]);
    expect(qa('first-error').length).toBe(1);
    expect(q('execute')!.textContent).toContain('Import 2 valid rows');
    expect(btn('execute')!.disabled).toBeFalse();
    expect(q('download-errors')).not.toBeNull();
  });

  it('without IMPORT_EXECUTE: no Run validation, no Import, a note', async () => {
    await setup({ perms: ['IMPORT_PROFILE_VIEW', 'IMPORT_UPLOAD'] });
    await toValidate();
    expect(q('run-validation')).toBeNull();
    expect(q('execute')).toBeNull();
    expect(q('cancel')).toBeNull();
    expect(q('no-execute')).not.toBeNull();
    c.runValidation();
    expect(api.validate).not.toHaveBeenCalled();
  });

  it('an import refused for the opening date shows the sentence with a link to Finance settings', async () => {
    await setup();
    await toValidate();
    c.runValidation();
    api.execute.and.returnValue(throwError(() => new HttpErrorResponse({ status: 400, error: { message: 'Set the opening balance date in Finance settings first.' } })));
    c.executeImport();
    detect();
    expect(q('action-error')!.textContent).toContain('Set the opening balance date in Finance settings first.');
    expect(q('action-error')!.querySelector('a')!.getAttribute('href')).toContain('finance-setup/currency-configuration');
    expect(c.step).toBe(2);
  });

  it('Cancel import cancels the session and returns to the dashboard', async () => {
    await setup();
    await toValidate();
    c.cancelImport();
    expect(api.cancel).toHaveBeenCalledOnceWith('s1');
    expect(TestBed.inject(Router).navigate).toHaveBeenCalledWith(['/portal/pages/settings/import']);
  });

  // ── Results ─────────────────────────────────────────────────────────────

  it('a synchronous import shows the result cards and the duration at once', async () => {
    await setup();
    await toValidate();
    c.runValidation();
    c.executeImport();
    detect();
    expect(c.step).toBe(3);
    expect(c.stages.every(s => s.state === 'done')).toBeTrue();
    expect(q('result-imported')!.textContent).toContain('2');
    expect(q('result-errors')!.textContent).toContain('1');
    expect(q('result-status')!.textContent).toContain('completed with errors');
    expect(q('duration')!.textContent).toContain('4 s');
    expect(api.watchProgress).not.toHaveBeenCalled();
  });

  it('a background import follows the hub\'s ImportProgress, then loads the final session', async () => {
    await setup();
    await toValidate();
    c.runValidation();
    api.execute.and.returnValue(of({ success: true, message: '', result: { isBackground: true, jobId: 'j1', session: session({ status: 'IMPORTING', validRows: 900 }) } }));
    api.getSession.and.returnValue(of({ success: true, message: '', result: session({ status: 'COMPLETED', importedRows: 900, validRows: 900 }) }));
    c.executeImport();
    detect();
    expect(q('progress')).not.toBeNull();
    hubEvents.next({ sessionUuid: 's1', processed: 450, total: 900, status: 'IMPORTING' });
    detect();
    expect(q('progress-text')!.textContent).toContain('450 of 900');
    expect(q('progress-bar')!.style.width).toBe('50%');
    hubEvents.next({ sessionUuid: 's1', processed: 900, total: 900, status: 'COMPLETED' });
    detect();
    expect(api.getSession).toHaveBeenCalledWith('s1');
    expect(q('progress')).toBeNull();
    expect(q('result-imported')!.textContent).toContain('900');
  });

  it('falls back to polling /progress when the hub is not connected', async () => {
    await setup();
    hubConnected.set(false);
    await toValidate();
    c.runValidation();
    api.execute.and.returnValue(of({ success: true, message: '', result: { isBackground: true, session: session({ status: 'IMPORTING', validRows: 600 }) } }));
    api.getProgress.and.returnValues(
      of({ success: true, message: '', result: { processed: 100, total: 600, status: 'IMPORTING' } }),
      of({ success: true, message: '', result: { processed: 600, total: 600, status: 'COMPLETED' } }));
    api.getSession.and.returnValue(of({ success: true, message: '', result: session({ status: 'COMPLETED', importedRows: 600 }) }));
    c.executeImport();
    detect();
    expect(c.progressProcessed).toBe(100);
    await new Promise(r => setTimeout(r, 80));
    detect();
    expect(api.getProgress).toHaveBeenCalledTimes(2);
    expect(c.background).toBeFalse();
    expect(q('result-imported')!.textContent).toContain('600');
  });

  it('"Import more" starts again on Upload', async () => {
    await setup();
    await toValidate();
    c.runValidation();
    c.executeImport();
    c.importMore();
    detect();
    expect(c.step).toBe(0);
    expect(c.file).toBeNull();
    expect(c.session).toBeNull();
    expect(q('upload')).not.toBeNull();
  });

  // ── Opening balances / resume ───────────────────────────────────────────

  it('opening balances: only the Error strategy, and Upload is blocked until the opening date is set', async () => {
    await setup({ profile: profile({ entityType: 'OPENING_STOCK', isOpeningBalance: true, allowUpdate: false, duplicateStrategy: 'ERROR' }),
                  perms: ['IMPORT_UPLOAD', 'IMPORT_EXECUTE', 'IMPORT_OPENING_BALANCE'], url: '/import/OPENING_STOCK', openingDate: null });
    expect(c.strategyOptions.map(s => s.value)).toEqual(['ERROR']);
    expect(q('opening-date-missing')!.textContent).toContain('Set the opening balance date in Finance settings first.');
    pickFile();
    expect(btn('upload')!.disabled).toBeTrue();
  });

  it('opening balances without IMPORT_OPENING_BALANCE: refused before anything is uploaded', async () => {
    await setup({ profile: profile({ entityType: 'OPENING_STOCK', isOpeningBalance: true }), url: '/import/OPENING_STOCK' });
    expect(q('access-problem')).not.toBeNull();
    expect(q('drop-zone')).toBeNull();
  });

  it('?session=<uuid> resumes on the session\'s step', async () => {
    await setup({ url: '/import/CUSTOMER?session=s9' });
    expect(api.getSession).toHaveBeenCalledWith('s9');
    expect(c.step).toBe(1);
    expect(c.rows.length).toBe(3);
  });

  it('?revalidate=1 reopens an interrupted (FAILED) session on Validate', async () => {
    await setup({ url: '/import/CUSTOMER?session=s9&revalidate=1', resumed: session({ status: 'FAILED' }) });
    expect(c.step).toBe(2);
    c.runValidation();
    expect(api.validate).toHaveBeenCalledWith('s1');
  });

  it('a profile that cannot be loaded shows the server\'s words', async () => {
    await setup();
    api.getProfile.and.returnValue(throwError(() => new HttpErrorResponse({ status: 404, error: { message: 'Unknown import type.' } })));
    c.load();
    detect();
    expect(q('load-error')!.textContent).toContain('Unknown import type.');
    expect(toasts).not.toHaveBeenCalled();
  });
});
