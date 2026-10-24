import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { Subject, of, throwError } from 'rxjs';

import { GlJournalEntriesComponent } from './gl-journal-entries.component';
import { GlJournalService, JournalEntryListItem } from '../../../../services/gl-journal.service';
import { AccountingService } from '../../../../services/accounting.service';
import { AuthService } from '../../../service/auth.service';
import { fakeApi, fakeAuth, okRes } from '../gl.testing';

const row = (n: string, over: Partial<JournalEntryListItem> = {}): JournalEntryListItem => ({
  uuid: 'u' + n, entryNumber: n, journalType: { uuid: 't', code: 'MISC', name: 'Miscellaneous Journal', type: 'Miscellaneous' },
  entryDate: '2026-10-20', status: 'Posted', totalDebit: 2200, totalCredit: 2200, currencyCode: 'PKR', isManual: true, entryType: 'MANUAL_JOURNAL', ...over
});

describe('GlJournalEntriesComponent (A47 tasks 251–255)', () => {
  let fixture: ComponentFixture<GlJournalEntriesComponent>;
  let c: GlJournalEntriesComponent;
  let api: jasmine.SpyObj<GlJournalService>;
  const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  async function setup(rows: JournalEntryListItem[], perms = ['GL_VIEW', 'GL_JOURNAL_CREATE', 'GL_JOURNAL_EDIT'], query: Record<string, string> = {}) {
    api = jasmine.createSpyObj<GlJournalService>('GlJournalService', ['getEntries', 'getJournalTypes']);
    api.getEntries.and.returnValue(of({ data: rows, totalRecords: 148, page: 1, pageSize: 25 }));
    api.getJournalTypes.and.returnValue(of([{ uuid: 't', code: 'MISC', name: 'Misc', sortOrder: 1 } as any]));
    const acc = fakeApi();
    acc.getFiscalYears.and.returnValue(okRes([{ uuid: 'y', fiscalYearName: 'FY 2026', startDate: '2026-01-01', endDate: '2026-12-31',
      periods: [{ uuid: 'p10', periodNumber: 10, periodName: 'Oct 2026' }] } as any]));
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [GlJournalEntriesComponent],
      providers: [provideNoopAnimations(), provideRouter([]), { provide: GlJournalService, useValue: api }, { provide: AccountingService, useValue: acc },
        { provide: AuthService, useValue: fakeAuth(perms) },
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(query) } } }]
    }).compileComponents();
    fixture = TestBed.createComponent(GlJournalEntriesComponent);
    c = fixture.componentInstance;
    fixture.detectChanges();
  }

  afterEach(() => fixture?.destroy());

  it('rows with number, type, amount and the status pill tone; old JE- numbers and gateway sources too', async () => {
    await setup([row('JV/2026/0003', { status: 'Draft' }), row('JE-2026-00012', { status: 'Reversed', isManual: false, entryType: 'SALES_INVOICE' })]);
    expect(c.total).toBe(148);
    expect(q('status-JV/2026/0003')!.className).toContain('in');
    expect(q('status-JE-2026-00012')!.className).toContain('er');
    expect(q('row-JE-2026-00012')!.textContent).toContain('Sales invoice');
    expect(q('row-JV/2026/0003')!.textContent).toContain('2,200.00');
    // A manual draft opens for editing; anything else for viewing.
    expect(q('edit-JV/2026/0003')).not.toBeNull();
    expect(q('view-JE-2026-00012')).not.toBeNull();
    expect(c.periodOptions.map(o => o.label)).toContain('Oct 2026 · FY 2026');
  });

  it('filters go to the server and reset to page 1; clearing removes them', async () => {
    await setup([row('A')]);
    c.typeUuid = 't';
    c.periodUuid = 'p10';
    c.from = '2026-10-01';
    c.setStatus('Draft');
    expect(api.getEntries).toHaveBeenCalledWith(jasmine.objectContaining({ journalTypeUuid: 't', fiscalPeriodUuid: 'p10', status: 'Draft',
      fromDate: '2026-10-01', toDate: null, page: 1 }));
    c.onLazyLoad({ first: 50, rows: 25 });
    expect(api.getEntries.calls.mostRecent().args[0]!.page).toBe(3);
    c.clearFilters();
    expect(api.getEntries.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({ status: null, journalTypeUuid: null, page: 1 }));
  });

  it('REV-18: a slow earlier answer does not overwrite the latest filter\'s rows', async () => {
    await setup([row('A')]);
    const slow = new Subject<any>();
    api.getEntries.and.returnValues(slow, of({ data: [row('NEW')], totalRecords: 1, page: 1, pageSize: 25 }));
    c.setStatus('Draft');
    c.setStatus('Posted');
    slow.next({ data: [row('OLD')], totalRecords: 9, page: 1, pageSize: 25 });
    expect(c.rows.map(r => r.entryNumber)).toEqual(['NEW']);
    expect(c.total).toBe(1);
  });

  it('?status=Draft&periodUuid= from the period-close page preset the filters', async () => {
    await setup([], undefined, { status: 'Draft', periodUuid: 'p10' });
    expect(c.status).toBe('Draft');
    expect(c.periodUuid).toBe('p10');
  });

  it('"Manual Entry" only with GL_JOURNAL_CREATE; errors shown', async () => {
    await setup([row('A', { status: 'Draft' })], ['GL_VIEW']);
    expect(q('new-entry')).toBeNull();
    expect(q('edit-A')).toBeNull();
    api.getEntries.and.returnValue(throwError(() => ({ status: 500, error: { message: 'Down.' } })));
    c.load(1);
    fixture.detectChanges();
    expect(q('load-error')!.textContent).toContain('Down.');
  });
});
