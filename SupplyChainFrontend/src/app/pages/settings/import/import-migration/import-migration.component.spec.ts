import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { Router, provideRouter } from '@angular/router';
import { of } from 'rxjs';

import { ImportMigrationComponent } from './import-migration.component';
import { ImportProfile, ImportService, SessionListItem } from '../../../../services/import.service';
import { ModuleService } from '../../../../services/module.service';
import { AuthService } from '../../../service/auth.service';

function profile(o: Partial<ImportProfile>): ImportProfile {
  return {
    entityType: 'UNIT', name: 'Units', importOrder: 1, isOpeningBalance: false, matchFields: ['code'], duplicateStrategy: 'SKIP',
    maxRowsPerFile: 50000, maxFileSizeMb: 10, allowUpdate: true, requiresOpeningBalancePermission: false, completedSessions: 0, ...o
  };
}

const PROFILES = [
  profile({ entityType: 'PRODUCT_CATEGORY', name: 'Product categories', importOrder: 2, completedSessions: 1, lastImportedRows: 40,
            lastCompletedAt: '2026-10-02T10:00:00Z' }),
  profile({ entityType: 'UNIT', name: 'Units', importOrder: 1, completedSessions: 2, lastImportedRows: 12, lastCompletedAt: '2026-10-01T10:00:00Z' }),
  profile({ entityType: 'PRODUCT', name: 'Products', importOrder: 3 }),
  profile({ entityType: 'WAREHOUSE', name: 'Warehouses', importOrder: 4 }),
  profile({ entityType: 'OPENING_STOCK', name: 'Opening stock', importOrder: 11, isOpeningBalance: true })
];

describe('ImportMigrationComponent (A40 migration wizard)', () => {
  let fixture: ComponentFixture<ImportMigrationComponent>;
  let component: ImportMigrationComponent;
  let api: jasmine.SpyObj<ImportService>;
  let permissions: string[];

  const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  async function setup(perms = ['IMPORT_PROFILE_VIEW', 'IMPORT_UPLOAD', 'IMPORT_OPENING_BALANCE'], openingDate: string | null = '2026-01-01',
                       profiles = PROFILES) {
    permissions = perms;
    api = jasmine.createSpyObj<ImportService>('ImportService', ['getProfiles', 'getOpeningBalanceDate', 'getSessions', 'downloadMigrationWorkbook']);
    api.getProfiles.and.returnValue(of({ success: true, message: '', result: profiles }));
    api.getOpeningBalanceDate.and.returnValue(of({ success: true, message: '', result: { date: openingDate } }));
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [ImportMigrationComponent],
      providers: [
        provideNoopAnimations(), provideRouter([]),
        { provide: ImportService, useValue: api },
        { provide: AuthService, useValue: { hasPermission: (p: string) => permissions.includes(p) } },
        { provide: ModuleService, useValue: { isFeatureEnabled: () => true } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(ImportMigrationComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  afterEach(() => fixture?.destroy());

  it('lists master data in import order: ✓ done with counts, ◉ the next one, ○ the rest', async () => {
    await setup();
    expect(component.items.map(i => i.profile.entityType)).toEqual(['UNIT', 'PRODUCT_CATEGORY', 'PRODUCT', 'WAREHOUSE']);
    expect(component.items.map(i => i.symbol)).toEqual(['✓', '✓', '◉', '○']);
    expect(q('state-UNIT')!.textContent).toContain('✓');
    expect(q('step-UNIT')!.textContent).toContain('12 rows imported');
    expect(q('step-UNIT')!.textContent).toContain('2 imports');
    expect(q('view-UNIT')).not.toBeNull();
    expect(q('view-PRODUCT')).toBeNull();
    expect(q('import-PRODUCT')!.textContent).toContain('Import');
    expect(q('import-UNIT')!.textContent).toContain('Import again');
    expect(q('done-count')!.textContent).toContain('2 / 5');
  });

  it('opening balances in their own section; ◉ only after master data is done', async () => {
    await setup();
    expect(q('opening-list')).not.toBeNull();
    expect(component.openingItems[0].state).toBe('todo');

    await setup(undefined, undefined, PROFILES.map(p => p.isOpeningBalance ? p : { ...p, completedSessions: 1 }));
    expect(component.openingItems[0].state).toBe('next');
    expect(q('state-OPENING_STOCK')!.textContent).toContain('◉');
  });

  it('opening balances are blocked with a link while the opening date is not set', async () => {
    await setup(undefined, null);
    expect(q('blocked-OPENING_STOCK')!.textContent).toContain('Set the opening balance date in Finance settings first.');
  });

  it('hides the opening balances without IMPORT_OPENING_BALANCE', async () => {
    await setup(['IMPORT_PROFILE_VIEW', 'IMPORT_UPLOAD']);
    expect(q('opening-list')).toBeNull();
    expect(component.totalCount).toBe(4);
  });

  it('no Import buttons or workbook without IMPORT_UPLOAD', async () => {
    await setup(['IMPORT_PROFILE_VIEW']);
    expect(q('import-UNIT')).toBeNull();
    expect(q('download-workbook')).toBeNull();
  });

  it('View opens the newest completed session of that entity', async () => {
    await setup();
    const navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    const s = (uuid: string, status: string) => ({ uuid, status } as SessionListItem);
    api.getSessions.and.returnValue(of({ success: true, message: '', result: { data: [s('x', 'FAILED'), s('y', 'COMPLETED'), s('z', 'COMPLETED')],
      totalRecords: 3, page: 1, pageSize: 20 } }));
    component.view(component.items[0]);
    expect(api.getSessions).toHaveBeenCalledWith({ entityType: 'UNIT', page: 1, pageSize: 20 });
    expect(navigate).toHaveBeenCalledWith(['/portal/pages/settings/import', 'sessions', 'y']);
  });
});
