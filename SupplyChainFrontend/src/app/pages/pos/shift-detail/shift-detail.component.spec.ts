import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ActivatedRoute, provideRouter } from '@angular/router';

import { PosShiftDetailComponent, shiftStages } from './shift-detail.component';
import { PosAdminService, PosShiftDetail } from '../../../services/pos-admin.service';
import { AuthService } from '../../service/auth.service';
import { fail400, ok, shiftDetailFixture, shiftFixture } from '../pos-fixtures.testing';

/** A38 FSD §17.7.3 — the shift page: tiles, by method / cashier, movements; reconcile (DISPUTED) and append a note (RECONCILED). */
describe('PosShiftDetailComponent (A38)', () => {
  let fixture: ComponentFixture<PosShiftDetailComponent>;
  let component: PosShiftDetailComponent;
  let el: HTMLElement;
  let pos: jasmine.SpyObj<PosAdminService>;

  async function setup(shift: PosShiftDetail, perms = ['POS_SHIFT_VIEW', 'POS_SHIFT_RECONCILE', 'POS_ORDER_VIEW']) {
    pos = jasmine.createSpyObj<PosAdminService>('PosAdminService', ['getShift', 'getShiftSummary', 'reconcileShift', 'appendShiftNote']);
    pos.getShift.and.returnValue(ok(shift));
    pos.getShiftSummary.and.returnValue(ok({
      shift, byMethod: [{ method: 'CASH', sales: 500, refunds: 20, net: 480 }, { method: 'BANK_TRANSFER', sales: 10, refunds: 0, net: 10 }],
      byCashier: shift.sessions
    }));
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [PosShiftDetailComponent],
      providers: [
        provideNoopAnimations(), provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => shift.uuid } } } },
        { provide: PosAdminService, useValue: pos },
        { provide: AuthService, useValue: { hasPermission: (c: string) => perms.includes(c) } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(PosShiftDetailComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    el = fixture.nativeElement;
  }

  const q = (id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  it('renders the tiles, by-method and by-cashier tables and cash movements', async () => {
    await setup(shiftDetailFixture());
    expect(q('shift-title')!.textContent).toContain('SH-S1-20261010-01');
    expect(q('tile-expected')!.textContent).toContain('1,480.00');
    expect(q('tile-counted')!.textContent).toContain('1,480.00');
    expect(q('method-table')!.textContent).toContain('Bank transfer');
    expect(q('method-table')!.textContent).toContain('480.00');
    expect(q('cashier-table')!.textContent).toContain('Sara');
    expect(q('movement-table')!.textContent).toContain('Safe drop');
    expect(component.sections.map(s => s.label)).toEqual(['By payment method', 'By cashier', 'Cash movements', 'Details']);
  });

  it('A39: store credit issued / redeemed tiles when the shift reports them; STORE_CREDIT method row labelled', async () => {
    await setup(shiftDetailFixture());
    expect(q('tile-credit-issued')).toBeNull();
    await setup(shiftDetailFixture({ totalStoreCreditIssued: 105.3, totalStoreCreditRedeemed: 40 }));
    expect(q('tile-credit-issued')!.textContent).toContain('105.30');
    expect(q('tile-credit-redeemed')!.textContent).toContain('40.00');
    expect(component.methodLabel('STORE_CREDIT')).toBe('Store credit');
    expect(component.methodLabel('EXCHANGE')).toBe('Exchange');
  });

  it('a variance beyond the threshold is highlighted', async () => {
    await setup(shiftDetailFixture({ status: 'DISPUTED', actualCashClosing: 1230, cashVariance: -250 }));
    expect(component.varianceHigh).toBeTrue();
    expect(q('kpi-variance')!.querySelector('.v')!.classList).toContain('er');
  });

  it('stages: DISPUTED is the red last stage, RECONCILED all done', () => {
    expect(shiftStages('DISPUTED').map(s => s.state)).toEqual(['done', 'done', 'done', 'bad']);
    expect(shiftStages('DISPUTED')[3].label).toBe('Disputed');
    expect(shiftStages('RECONCILED').every(s => s.state === 'done')).toBeTrue();
    expect(shiftStages('OPEN')[0].state).toBe('on');
  });

  it('DISPUTED: Reconcile needs a note, then PATCHes reconcile', async () => {
    await setup(shiftDetailFixture({ status: 'DISPUTED', cashVariance: -250 }));
    expect(q('reconcile')).not.toBeNull();
    expect(q('add-note')).not.toBeNull();   // POSA: a note may also be appended while DISPUTED
    component.openNote('reconcile');
    expect(component.noteValid).toBeFalse();
    component.submitNote();
    expect(pos.reconcileShift).not.toHaveBeenCalled();
    component.noteText = '  Short change given  ';
    pos.reconcileShift.and.returnValue(ok(shiftFixture({ status: 'RECONCILED' })));
    component.submitNote();
    expect(pos.reconcileShift).toHaveBeenCalledWith('sh-1', 'Short change given');
    expect(component.noteMode).toBeNull();
  });

  it('RECONCILED: only "Add note" (append-only); a refusal is shown verbatim', async () => {
    await setup(shiftDetailFixture({ status: 'RECONCILED', varianceNote: 'Till short',
                                     reconcileNotes: '[2026-10-10 18:00 UTC] Omar: First note' }));
    expect(q('reconcile')).toBeNull();
    expect(q('add-note')).not.toBeNull();
    expect(q('shift-close-note')!.textContent).toContain('Till short');
    expect(q('shift-notes')!.textContent).toContain('Omar: First note');
    component.openNote('note');
    component.noteText = 'Later remark';
    pos.appendShiftNote.and.returnValue(fail400('Notes can only be added to a reconciled shift.'));
    component.submitNote();
    expect(pos.appendShiftNote).toHaveBeenCalledWith('sh-1', 'Later remark');
    expect(component.noteError).toBe('Notes can only be added to a reconciled shift.');
  });

  it('no reconcile actions on OPEN/CLOSING shifts or without POS_SHIFT_RECONCILE', async () => {
    await setup(shiftDetailFixture({ status: 'OPEN' }));
    expect(q('reconcile')).toBeNull();
    expect(q('add-note')).toBeNull();
    await setup(shiftDetailFixture({ status: 'CLOSING' }));
    expect(q('add-note')).toBeNull();
    await setup(shiftDetailFixture({ status: 'DISPUTED' }), ['POS_SHIFT_VIEW']);
    expect(q('reconcile')).toBeNull();
    expect(q('add-note')).toBeNull();
    expect(q('shift-orders-link')).toBeNull();
  });
});
