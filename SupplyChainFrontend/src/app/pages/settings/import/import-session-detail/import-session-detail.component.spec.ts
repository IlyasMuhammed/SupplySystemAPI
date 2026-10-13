import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { MessageService } from 'primeng/api';
import { Subject, of, throwError } from 'rxjs';

import { ImportSessionDetailComponent } from './import-session-detail.component';
import { ImportProgress, ImportService, SessionDetail } from '../../../../services/import.service';
import { AuthService } from '../../../service/auth.service';

function session(o: Partial<SessionDetail> = {}): SessionDetail {
  return {
    uuid: 's1', entityType: 'PRODUCT', entityName: 'Products', fileName: 'products.xlsx', status: 'COMPLETED_WITH_ERRORS', totalRows: 10,
    validRows: 8, invalidRows: 2, importedRows: 7, updatedRows: 1, skippedRows: 0, errorRows: 0, createdAt: '2026-10-11T08:00:00Z',
    createdByName: 'Ayesha', fileType: 'XLSX', fileSizeBytes: 3 * 1024, sheetName: 'Data', sheetNames: ['Data'], duplicateStrategy: 'UPDATE',
    notes: 'legacy', importStartedAt: '2026-10-11T08:01:00Z', importCompletedAt: '2026-10-11T08:01:30Z',
    mappings: [
      { sourceColumnIndex: 1, sourceColumnName: 'Name', targetFieldName: 'name', targetDisplayName: 'Product name', isAutoMapped: true, confidenceScore: 1 },
      { sourceColumnIndex: 0, sourceColumnName: 'SKU', targetFieldName: 'sku', targetDisplayName: 'SKU', isAutoMapped: true, confidenceScore: 0.9 },
      { sourceColumnIndex: 2, sourceColumnName: 'Junk', targetFieldName: null, isAutoMapped: false, confidenceScore: null }
    ],
    unmappedColumns: ['Junk'], unmappedRequiredFields: [], ...o
  };
}

describe('ImportSessionDetailComponent (A40 session detail)', () => {
  let fixture: ComponentFixture<ImportSessionDetailComponent>;
  let component: ImportSessionDetailComponent;
  let api: jasmine.SpyObj<ImportService>;
  let permissions: string[];
  let progress: Subject<ImportProgress>;
  let toasts: jasmine.Spy;

  const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
  const qa = (id: string) => Array.from(fixture.nativeElement.querySelectorAll(`[data-testid="${id}"]`)) as HTMLElement[];

  async function setup(s: SessionDetail = session(), perms = ['IMPORT_UPLOAD', 'IMPORT_EXECUTE']) {
    permissions = perms;
    progress = new Subject<ImportProgress>();
    api = jasmine.createSpyObj<ImportService>('ImportService', ['getSession', 'getErrors', 'cancel', 'watchProgress', 'downloadErrors', 'downloadResults']);
    api.getSession.and.returnValue(of({ success: true, message: '', result: s }));
    api.getErrors.and.returnValue(of({ success: true, message: '', result: { data: [
      { rowNumber: 3, status: 'INVALID', values: {}, errors: [{ field: 'sku', code: 'REQUIRED', message: 'SKU is required.' }] },
      { rowNumber: 7, status: 'INVALID', values: {}, errors: [{ field: 'price', code: 'OUT_OF_RANGE', message: 'Must be at least 0.' },
                                                         { field: 'uom', code: 'LOOKUP_NOT_FOUND', message: "Unit 'BOXX' not found." }] }
    ], totalRecords: 2, page: 1, pageSize: 25 } }));
    api.cancel.and.returnValue(of({ success: true, message: '', result: session({ status: 'CANCELLED' }) }));
    api.watchProgress.and.returnValue(progress.asObservable());

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [ImportSessionDetailComponent],
      providers: [
        provideNoopAnimations(), provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ uuid: 's1' }) } } },
        { provide: ImportService, useValue: api },
        { provide: AuthService, useValue: { hasPermission: (p: string) => permissions.includes(p) } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(ImportSessionDetailComponent);
    component = fixture.componentInstance;
    toasts = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
    fixture.detectChanges();
  }

  afterEach(() => fixture?.destroy());

  it('shows the summary, KPIs and the mappings in column order', async () => {
    await setup();
    expect(api.getSession).toHaveBeenCalledWith('s1');
    expect(q('status')!.textContent).toContain('Errors');
    expect(q('kpi-total')!.textContent).toContain('10');
    expect(q('kpi-imported')!.textContent).toContain('7');
    expect(q('kpi-errors')!.textContent).toContain('2');
    expect(q('summary')!.textContent).toContain('Update');
    expect(q('summary')!.textContent).toContain('3.0 KB');
    expect(component.duration).toBe('30 s');
    const cells = qa('mapping-row').map(r => Array.from(r.querySelectorAll('td')).map(td => td.textContent!.replace(/\s+/g, ' ').trim()));
    expect(cells).toEqual([
      ['SKU', 'SKU', '~ 90%'], ['Name', 'Product name', '✓ Exact'], ['Junk', '— Ignored —', '✗ Not mapped']]);
  });

  it('lists the errors page by page from the server', async () => {
    await setup();
    expect(api.getErrors).toHaveBeenCalledWith('s1', 1, 25);
    expect(qa('error-row').length).toBe(2);
    expect(qa('error-row')[1].textContent).toContain("price: Must be at least 0. · uom: Unit 'BOXX' not found.");
    component.onErrorsPage({ first: 50, rows: 25 });
    expect(api.getErrors).toHaveBeenCalledWith('s1', 3, 25);
  });

  it('offers both downloads once finished, no cancel', async () => {
    await setup();
    expect(q('download-errors')).not.toBeNull();
    expect(q('download-results')).not.toBeNull();
    expect(q('cancel')).toBeNull();
    expect(q('continue')).toBeNull();
  });

  it('a running import: cancel (EXECUTE or MANAGE), live progress, reload on completion', async () => {
    await setup(session({ status: 'IMPORTING', invalidRows: 0, importedRows: 0, updatedRows: 0, importCompletedAt: null }));
    expect(api.watchProgress).toHaveBeenCalledWith('s1');
    expect(q('cancel')).not.toBeNull();
    expect(q('download-results')).toBeNull();
    progress.next({ processed: 250, total: 1000, status: 'IMPORTING' });
    fixture.detectChanges();
    expect(q('progress-text')!.textContent).toContain('250 of 1,000');
    api.getSession.and.returnValue(of({ success: true, message: '', result: session({ status: 'COMPLETED' }) }));
    progress.complete();
    fixture.detectChanges();
    expect(api.getSession).toHaveBeenCalledTimes(2);
    expect(q('progress')).toBeNull();
    expect(q('status')!.textContent).toContain('Completed');
  });

  it('cancel calls the server and shows the cancelled session; a refusal is shown verbatim', async () => {
    await setup(session({ status: 'VALIDATED' }));
    component.cancel();
    fixture.detectChanges();
    expect(api.cancel).toHaveBeenCalledOnceWith('s1');
    expect(q('status')!.textContent).toContain('Cancelled');

    await setup(session({ status: 'VALIDATED' }));
    api.cancel.and.returnValue(throwError(() => new HttpErrorResponse({ status: 400, error: { message: 'The import has already finished.' } })));
    component.cancel();
    expect(toasts).toHaveBeenCalledWith(jasmine.objectContaining({ detail: 'The import has already finished.' }));
  });

  it('only IMPORT_UPLOAD: no cancel; a not-yet-imported session can be continued in the wizard', async () => {
    await setup(session({ status: 'MAPPING' }), ['IMPORT_UPLOAD']);
    expect(q('cancel')).toBeNull();
    const navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    component.continueInWizard();
    expect(navigate).toHaveBeenCalledWith(['/portal/pages/settings/import', 'PRODUCT'], { queryParams: { session: 's1' } });
  });

  it('an interrupted (FAILED) import can be re-validated in the wizard with IMPORT_EXECUTE (D-38/D-39)', async () => {
    await setup(session({ status: 'FAILED', errorMessage: 'Import interrupted — re-validate and re-run the remaining rows.' }));
    expect(q('error-message')!.textContent).toContain('Import interrupted');
    expect(q('revalidate')).not.toBeNull();
    const navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    component.revalidateInWizard();
    expect(navigate).toHaveBeenCalledWith(['/portal/pages/settings/import', 'PRODUCT'], { queryParams: { session: 's1', revalidate: 1 } });

    await setup(session({ status: 'FAILED' }), ['IMPORT_UPLOAD']);
    expect(q('revalidate')).toBeNull();
  });

  it('a session that cannot be loaded shows the server\'s words', async () => {
    await setup();
    api.getSession.and.returnValue(throwError(() => new HttpErrorResponse({ status: 404, error: { message: 'Import not found.' } })));
    component.s = null;
    component.load();
    fixture.detectChanges();
    expect(q('load-error')!.textContent).toContain('Import not found.');
  });
});
