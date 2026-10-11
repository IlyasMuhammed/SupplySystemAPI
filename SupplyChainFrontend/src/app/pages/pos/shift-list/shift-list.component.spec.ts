import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';

import { PosShiftListComponent } from './shift-list.component';
import { PosAdminService } from '../../../services/pos-admin.service';
import { ok, shiftFixture, storeFixture } from '../pos-fixtures.testing';

/** A38 FSD §17.7.3 — the shifts list: store / status / date filters, variance highlight. */
describe('PosShiftListComponent (A38)', () => {
  let fixture: ComponentFixture<PosShiftListComponent>;
  let component: PosShiftListComponent;
  let el: HTMLElement;
  let pos: jasmine.SpyObj<PosAdminService>;

  async function setup(query: Record<string, string> = {}) {
    pos = jasmine.createSpyObj<PosAdminService>('PosAdminService', ['getShifts', 'getStores']);
    pos.getStores.and.returnValue(ok({ data: [storeFixture()], totalRecords: 1 }));
    pos.getShifts.and.returnValue(ok({ data: [
      shiftFixture(),
      shiftFixture({ uuid: 'sh-2', shiftNumber: 'SH-S1-20261010-02', status: 'DISPUTED', cashVariance: 150.5 })
    ], totalRecords: 2 }));
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [PosShiftListComponent],
      providers: [
        provideNoopAnimations(), provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(query) } } },
        { provide: PosAdminService, useValue: pos }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(PosShiftListComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    el = fixture.nativeElement;
  }

  it('lists shifts and highlights a variance beyond the threshold', async () => {
    await setup();
    const rows = Array.from(el.querySelectorAll('[data-testid="shift-row"]'));
    expect(rows.length).toBe(2);
    expect(rows[0].getAttribute('data-variance-high')).toBe('false');
    expect(rows[1].getAttribute('data-variance-high')).toBe('true');
    expect(rows[1].querySelector('[data-testid="variance"]')!.classList).toContain('sl-var');
    expect(rows[1].querySelector('[data-testid="status"]')!.textContent).toContain('Disputed');
    expect(component.storeOptions[0].value).toBe('s-1');
  });

  it('filters by status, store and date; picks the store and status from the query', async () => {
    await setup({ store: 's-1', status: 'DISPUTED' });
    expect(pos.getShifts).toHaveBeenCalledWith(jasmine.objectContaining({ storeUuid: 's-1', status: 'DISPUTED' }));
    component.setStatus('OPEN');
    expect(pos.getShifts).toHaveBeenCalledWith(jasmine.objectContaining({ status: 'OPEN', page: 1 }));
    component.dateRange = [new Date(2026, 9, 1), new Date(2026, 9, 5)];
    component.onDateChange();
    expect(pos.getShifts).toHaveBeenCalledWith(jasmine.objectContaining({ from: '2026-10-01', to: '2026-10-05' }));
    component.resetFilters();
    expect(pos.getShifts.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({ storeUuid: null, status: '', from: null, to: null }));
  });
});
