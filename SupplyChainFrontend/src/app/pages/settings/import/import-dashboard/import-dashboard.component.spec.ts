import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { HttpErrorResponse, HttpResponse } from '@angular/common/http';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { ImportDashboardComponent } from './import-dashboard.component';
import { ImportProfile, ImportService, SessionListItem } from '../../../../services/import.service';
import { ModuleService } from '../../../../services/module.service';
import { AuthService } from '../../../service/auth.service';

export function profile(o: Partial<ImportProfile>): ImportProfile {
  return {
    entityType: 'UNIT', name: 'Units', description: 'Units of measure', importOrder: 1, isOpeningBalance: false, matchFields: ['code'],
    duplicateStrategy: 'SKIP', maxRowsPerFile: 50000, maxFileSizeMb: 10, allowUpdate: true, requiresOpeningBalancePermission: false,
    completedSessions: 0, ...o
  };
}

export const PROFILES: ImportProfile[] = [
  profile({ entityType: 'PRODUCT', name: 'Products', importOrder: 3 }),
  profile({ entityType: 'UNIT', name: 'Units', importOrder: 1, completedSessions: 2, lastCompletedAt: '2026-10-03T10:00:00Z', lastImportedRows: 120 }),
  profile({ entityType: 'PRODUCT_CATEGORY', name: 'Product categories', importOrder: 2 }),
  profile({ entityType: 'OPENING_STOCK', name: 'Opening stock', importOrder: 11, isOpeningBalance: true, allowUpdate: false,
            duplicateStrategy: 'ERROR', requiresOpeningBalancePermission: true }),
  profile({ entityType: 'CUSTOMER_OPENING_BALANCE', name: 'Customer opening balances', importOrder: 12, isOpeningBalance: true,
            allowUpdate: false, duplicateStrategy: 'ERROR', requiresOpeningBalancePermission: true })
];

function session(o: Partial<SessionListItem>): SessionListItem {
  return {
    uuid: 's', entityType: 'UNIT', entityName: 'Units', fileName: 'units.xlsx', status: 'COMPLETED', totalRows: 10, validRows: 10,
    invalidRows: 0, importedRows: 10, updatedRows: 0, skippedRows: 0, errorRows: 0, createdAt: '2026-10-10T09:00:00Z', createdByName: 'Ayesha',
    ...o
  };
}

describe('ImportDashboardComponent (A40 dashboard)', () => {
  let fixture: ComponentFixture<ImportDashboardComponent>;
  let component: ImportDashboardComponent;
  let api: jasmine.SpyObj<ImportService>;
  let toasts: jasmine.Spy;
  let permissions: string[];
  let features: string[];

  const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
  const qa = (id: string) => Array.from(fixture.nativeElement.querySelectorAll(`[data-testid="${id}"]`)) as HTMLElement[];

  async function setup(perms = ['IMPORT_PROFILE_VIEW', 'IMPORT_UPLOAD', 'IMPORT_OPENING_BALANCE'], feats = ['FEATURE_OPENING_BALANCE_IMPORT'],
                       openingDate: string | null = '2026-01-01') {
    permissions = perms;
    features = feats;
    api = jasmine.createSpyObj<ImportService>('ImportService', ['getProfiles', 'getSessions', 'getOpeningBalanceDate', 'downloadMigrationWorkbook']);
    api.getProfiles.and.returnValue(of({ success: true, message: '', result: PROFILES }));
    api.getSessions.and.returnValue(of({ success: true, message: '', result: { data: [
      session({ uuid: 'a', status: 'COMPLETED' }), session({ uuid: 'b', status: 'COMPLETED_WITH_ERRORS' }),
      session({ uuid: 'c', status: 'IMPORTING' }), session({ uuid: 'd', status: 'FAILED' }), session({ uuid: 'e', status: 'CANCELLED' })
    ], totalRecords: 5, page: 1, pageSize: 10 } }));
    api.getOpeningBalanceDate.and.returnValue(of({ success: true, message: '', result: { date: openingDate } }));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [ImportDashboardComponent],
      providers: [
        provideNoopAnimations(), provideRouter([]),
        { provide: ImportService, useValue: api },
        { provide: AuthService, useValue: { hasPermission: (p: string) => permissions.includes(p) } },
        { provide: ModuleService, useValue: { isFeatureEnabled: (c: string) => features.includes(c) } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(ImportDashboardComponent);
    component = fixture.componentInstance;
    toasts = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
    fixture.detectChanges();
  }

  afterEach(() => fixture?.destroy());

  it('shows the master-data cards in import order, last import or "Not imported yet"', async () => {
    await setup();
    expect(component.cards.map(c => c.profile.entityType)).toEqual(['UNIT', 'PRODUCT_CATEGORY', 'PRODUCT']);
    const master = q('section-master')!;
    expect(master.querySelector('[data-testid="card-OPENING_STOCK"]')).toBeNull();
    expect(q('last-UNIT')!.textContent).toContain('Last import');
    expect(q('last-UNIT')!.textContent).toContain('120 rows');
    expect(q('last-PRODUCT')!.textContent).toContain('Not imported yet');
    expect(q('import-UNIT')).not.toBeNull();
  });

  it('opening balances get their own section, enabled when the opening date is set', async () => {
    await setup();
    expect(component.openingCards.map(c => c.profile.entityType)).toEqual(['OPENING_STOCK', 'CUSTOMER_OPENING_BALANCE']);
    expect(q('section-opening')).not.toBeNull();
    expect(q('opening-date')!.textContent).toContain('01 Jan 2026');
    expect(q('blocked-OPENING_STOCK')).toBeNull();
  });

  it('opening cards link to Finance settings and cannot import while the date is not set', async () => {
    await setup(undefined, undefined, null);
    expect(q('blocked-OPENING_STOCK')!.textContent).toContain('Set the opening balance date in Finance settings first.');
    expect(q('set-date-OPENING_STOCK')!.getAttribute('href')).toContain('/portal/pages/finance-setup/currency-configuration');
    expect(component.openingCards.every(c => !!c.blocked)).toBeTrue();
  });

  it('hides the opening balances without IMPORT_OPENING_BALANCE', async () => {
    await setup(['IMPORT_PROFILE_VIEW', 'IMPORT_UPLOAD']);
    expect(q('section-opening')).toBeNull();
    expect(api.getOpeningBalanceDate).not.toHaveBeenCalled();
  });

  it('hides the opening balances when FEATURE_OPENING_BALANCE_IMPORT is off', async () => {
    await setup(undefined, []);
    expect(q('section-opening')).toBeNull();
  });

  it('recent imports with the five status pills, linking to the session', async () => {
    await setup();
    expect(api.getSessions).toHaveBeenCalledWith({ page: 1, pageSize: 10 });
    expect(qa('recent-status').map(e => e.textContent!.trim())).toEqual(['Completed', 'Errors', 'In progress', 'Failed', 'Cancelled']);
    expect(qa('recent-link')[0].getAttribute('href')).toBe('/portal/pages/settings/import/sessions/a');
  });

  it('without IMPORT_UPLOAD: no Import buttons, no workbook, no recent list', async () => {
    await setup(['IMPORT_PROFILE_VIEW']);
    expect(q('import-UNIT')).toBeNull();
    expect(q('download-workbook')).toBeNull();
    expect(q('recent')).toBeNull();
    expect(api.getSessions).not.toHaveBeenCalled();
  });

  it('downloads the migration workbook; a refusal is shown verbatim', async () => {
    await setup();
    spyOn(URL, 'createObjectURL').and.returnValue('blob:x');
    spyOn(URL, 'revokeObjectURL');
    spyOn(HTMLAnchorElement.prototype, 'click');
    api.downloadMigrationWorkbook.and.returnValue(of(new HttpResponse({ body: new Blob(['x']) })));
    component.downloadWorkbook();
    expect(HTMLAnchorElement.prototype.click).toHaveBeenCalled();

    api.downloadMigrationWorkbook.and.returnValue(throwError(() => new HttpErrorResponse({
      status: 403, error: new Blob([JSON.stringify({ message: 'Data import is not part of your plan.' })]) })));
    component.downloadWorkbook();
    for (let i = 0; i < 20 && !toasts.calls.count(); i++) await new Promise(r => setTimeout(r, 10));   // Blob.text() is async
    expect(toasts).toHaveBeenCalledWith(jasmine.objectContaining({ detail: 'Data import is not part of your plan.' }));
  });

  it('a failed profile load says so and offers a retry', async () => {
    await setup();
    api.getProfiles.and.returnValue(throwError(() => new HttpErrorResponse({ status: 500 })));
    component.load();
    fixture.detectChanges();
    expect(q('load-failed')).not.toBeNull();
  });
});
