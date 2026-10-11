import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ActivatedRoute, provideRouter } from '@angular/router';

import { PosStoreDetailComponent, terminalRow } from './store-detail.component';
import { PosAdminService } from '../../../services/pos-admin.service';
import { InventoryService } from '../../../services/inventory.service';
import { FinanceSetupService } from '../../../services/finance-setup.service';
import { AuthService } from '../../service/auth.service';
import { PosReturnsAdminService } from '../../../services/pos-returns-admin.service';
import { effectivePolicyFixture, fail400, ok, shiftFixture, staffFixture, storeFixture, terminalFixture } from '../pos-fixtures.testing';

const ALL = ['POS_STORE_VIEW', 'POS_STORE_MANAGE', 'POS_TERMINAL_VIEW', 'POS_TERMINAL_MANAGE', 'POS_SHIFT_OPEN', 'POS_SHIFT_VIEW'];

/** A38 FSD §17.7 — the store object page: tabs, configuration, terminals, staff + PIN, shifts, daily totals. */
describe('PosStoreDetailComponent (A38)', () => {
  let fixture: ComponentFixture<PosStoreDetailComponent>;
  let component: PosStoreDetailComponent;
  let el: HTMLElement;
  let pos: jasmine.SpyObj<PosAdminService>;
  let returnsApi: jasmine.SpyObj<PosReturnsAdminService>;

  const recent = () => new Date(Date.now() - 5 * 60 * 1000).toISOString();
  const old = () => new Date(Date.now() - 60 * 60 * 1000).toISOString();
  const future = () => new Date(Date.now() + 4 * 60 * 1000).toISOString();

  async function setup(perms: string[] = ALL) {
    pos = jasmine.createSpyObj<PosAdminService>('PosAdminService', [
      'getStore', 'updateStore', 'setStoreStatus', 'getTerminals', 'createTerminal', 'updateTerminal', 'unbindTerminal', 'unlockTerminal',
      'forceSync', 'forceLogout', 'getStaff', 'getStaffCandidates', 'saveStaff', 'removeStaff', 'getShifts'
    ]);
    pos.getStore.and.returnValue(ok(storeFixture()));
    pos.getTerminals.and.returnValue(ok([
      terminalFixture({ lastSeenAt: recent(), pinLockedUntil: future() }),
      terminalFixture({ uuid: 't-2', code: 'T2', name: 'Till 2', deviceBound: false, deviceUuid: null, lastSeenAt: old() })
    ]));
    pos.getStaff.and.returnValue(ok([staffFixture(), staffFixture({ userId: 8, name: 'Omar', posRole: 'SUPERVISOR', hasPin: false })]));
    pos.getStaffCandidates.and.returnValue(ok([{ userId: 9, name: 'Zara', email: 'z@x.pk', roleName: 'POS Cashier' }]));
    pos.getShifts.and.returnValue(ok({ data: [
      shiftFixture(),
      shiftFixture({ uuid: 'sh-2', shiftNumber: 'SH-S1-20261009-01', status: 'DISPUTED', openedAt: '2026-10-09T09:00:00', cashVariance: -250 })
    ], totalRecords: 2 }));
    returnsApi = jasmine.createSpyObj<PosReturnsAdminService>('PosReturnsAdminService', ['getStorePolicy', 'saveStorePolicy', 'deleteStorePolicy']);
    returnsApi.getStorePolicy.and.returnValue(ok(effectivePolicyFixture()));
    for (const m of ['unbindTerminal', 'unlockTerminal', 'forceSync', 'forceLogout', 'removeStaff', 'createTerminal', 'updateTerminal'] as const) {
      (pos[m] as jasmine.Spy).and.returnValue(ok(null));
    }

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [PosStoreDetailComponent],
      providers: [
        provideNoopAnimations(), provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: { get: () => 's-1' } } } },
        { provide: PosAdminService, useValue: pos },
        { provide: PosReturnsAdminService, useValue: returnsApi },
        { provide: FinanceSetupService, useValue: { getTaxCodes: () => ok([{ uuid: 'tx-1', code: 'GST17', name: 'GST', ratePercent: 17, usage: 'SALES' }]) } },
        { provide: InventoryService, useValue: { getWarehouses: () => ok([]) } },
        { provide: AuthService, useValue: {
          hasPermission: (c: string) => perms.includes(c),
          hasAnyPermission: (...c: string[]) => c.some(x => perms.includes(x))
        } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(PosStoreDetailComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    el = fixture.nativeElement;
  }

  const q = (id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  it('has the seven tabs (A39 adds Returns) and shows only the selected one', async () => {
    await setup();
    expect(component.sections.map(s => s.label)).toEqual(['General', 'Configuration', 'Terminals', 'Staff', 'Shifts', 'Reports', 'Returns']);
    expect(component.sections.find(s => s.label === 'Terminals')!.count).toBe(2);
    expect(q('store-title')!.textContent).toContain('S1');
    expect(q('general-warehouse')!.textContent).toContain('Main WH');
    expect(q('section-general')!.style.display).toBe('');
    expect(q('section-staff')!.style.display).toBe('none');
    (q('anchor-sec-staff') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(q('section-staff')!.style.display).toBe('');
    expect(q('section-general')!.style.display).toBe('none');
  });

  it('terminals: online within 15 minutes, bound device id, locked pill', async () => {
    await setup();
    expect(component.terminals.map(t => [t.online, t.locked])).toEqual([[true, true], [false, false]]);
    const t1 = q('terminal-T1')!;
    expect(t1.querySelector('[data-testid="terminal-online"]')!.textContent).toContain('Online');
    expect(t1.querySelector('[data-testid="terminal-device"]')!.textContent).toContain('dev-1');
    expect(t1.querySelector('[data-testid="terminal-locked"]')).not.toBeNull();
    expect(t1.querySelector('[data-testid="terminal-unlock"]')).not.toBeNull();
    const t2 = q('terminal-T2')!;
    expect(t2.querySelector('[data-testid="terminal-online"]')!.textContent).toContain('Offline');
    expect(t2.querySelector('[data-testid="terminal-unbind"]')).toBeNull();
    expect(t2.querySelector('[data-testid="terminal-unlock"]')).toBeNull();
  });

  it('terminalRow trusts isOnline from the server too', () => {
    expect(terminalRow(terminalFixture({ isOnline: true })).online).toBeTrue();
    expect(terminalRow(terminalFixture({ pinLockedUntil: new Date(Date.now() - 1000).toISOString() })).locked).toBeFalse();
  });

  it('terminal actions: unbind asks first; unlock; force sync with a type; force logout needs a reason', async () => {
    await setup();
    const t = component.terminals[0];
    const confirm = spyOn((component as any).confirm, 'confirm').and.callFake((c: any) => c.accept());
    component.unbind(t);
    expect(confirm).toHaveBeenCalled();
    expect(pos.unbindTerminal).toHaveBeenCalledWith('t-1');

    component.unlock(t);
    expect(pos.unlockTerminal).toHaveBeenCalledWith('t-1');

    component.openForceSync(t);
    component.syncType = 'FULL';
    component.confirmForceSync();
    expect(pos.forceSync).toHaveBeenCalledWith('t-1', 'FULL');

    component.openForceLogout(t);
    component.confirmForceLogout();
    expect(pos.forceLogout).not.toHaveBeenCalled();
    component.logoutReason = ' Closing early ';
    component.confirmForceLogout();
    expect(pos.forceLogout).toHaveBeenCalledWith('t-1', 'Closing early');
  });

  it('terminal create / edit; a duplicate code is shown verbatim', async () => {
    await setup();
    component.openTerminal(null);
    component.terminalModel = { code: 't3', name: 'Till 3', isActive: true };
    pos.createTerminal.and.returnValue(fail400('Terminal code already exists in this store.'));
    component.saveTerminal();
    expect(pos.createTerminal).toHaveBeenCalledWith('s-1', { code: 'T3', name: 'Till 3' });
    expect(component.terminalError).toBe('Terminal code already exists in this store.');

    component.openTerminal(component.terminals[0]);
    component.terminalModel.isActive = false;
    component.saveTerminal();
    expect(pos.updateTerminal).toHaveBeenCalledWith('t-1', { code: 'T1', name: 'Till 1', isActive: false, rowVersion: 'trv' });
  });

  it('only view: no terminal actions, no staff actions, config read only', async () => {
    await setup(['POS_STORE_VIEW', 'POS_TERMINAL_VIEW']);
    expect(q('terminal-sync')).toBeNull();
    expect(q('terminal-edit')).toBeNull();
    expect(q('add-staff')).toBeNull();
    expect(q('staff-edit')).toBeNull();
    expect(q('save-config')).toBeNull();
    expect(q('edit-store')).toBeNull();
    expect(q('shifts-no-access')).not.toBeNull();
    expect(pos.getShifts).not.toHaveBeenCalled();
  });

  it('staff: add from candidates; PIN must be 4–6 digits and typed twice; 400 shown verbatim', async () => {
    await setup();
    component.openStaff(null);
    expect(pos.getStaffCandidates).toHaveBeenCalledWith('s-1', null);
    expect(component.candidates[0].value).toBe(9);
    expect(component.staffCanSave).toBeFalse();       // no user yet
    component.staffModel.userId = 9;
    expect(component.staffCanSave).toBeTrue();         // no PIN is allowed

    component.staffModel.pin = '123';
    expect(component.pinError).toBe('PIN must be 4 to 6 digits.');
    expect(component.staffCanSave).toBeFalse();
    component.staffModel.pin = '1234';
    component.staffModel.pinConfirm = '1235';
    expect(component.pinError).toBe('The two PINs do not match.');
    component.staffModel.pinConfirm = '1234';
    expect(component.pinError).toBeNull();
    expect(component.digitsOnly('12a3-4567')).toBe('123456');

    pos.saveStaff.and.returnValue(fail400('This PIN is already used by another staff member of this store.'));
    component.saveStaff();
    expect(pos.saveStaff).toHaveBeenCalledWith('s-1', 9, { posRole: 'CASHIER', pin: '1234' });
    expect(component.staffError).toBe('This PIN is already used by another staff member of this store.');
  });

  it('staff: change role without a PIN keeps the current one; remove asks first', async () => {
    await setup();
    component.openStaff(component.staff[1]);
    expect(pos.getStaffCandidates).not.toHaveBeenCalled();
    component.staffModel.posRole = 'CASHIER';
    pos.saveStaff.and.returnValue(ok(staffFixture()));
    component.saveStaff();
    expect(pos.saveStaff).toHaveBeenCalledWith('s-1', 8, { posRole: 'CASHIER', pin: null });
    expect(component.staffDialog).toBeFalse();

    spyOn((component as any).confirm, 'confirm').and.callFake((c: any) => c.accept());
    component.removeStaff(component.staff[0]);
    expect(pos.removeStaff).toHaveBeenCalledWith('s-1', 7);
  });

  it('removed (soft-deleted) staff stay listed as inactive, without Remove, and can be reactivated with a PUT', async () => {
    await setup();
    pos.getStaff.and.returnValue(ok([staffFixture({ isActive: false })]));
    component.loadStaff();
    fixture.detectChanges();
    const row = q('staff-7')!;
    expect(row.textContent).toContain('Inactive');
    expect(row.querySelector('[data-testid="staff-remove"]')).toBeNull();
    component.openStaff(component.staff[0]);
    pos.saveStaff.and.returnValue(ok(staffFixture()));
    component.saveStaff();
    expect(pos.saveStaff).toHaveBeenCalledWith('s-1', 7, { posRole: 'CASHIER', pin: null });
  });

  it('the staff PIN input is masked and numeric', async () => {
    await setup();
    component.openStaff(null);
    fixture.detectChanges();
    await fixture.whenStable();
    const input = document.querySelector('[data-testid="staff-pin"]') as HTMLInputElement;
    expect(input.type).toBe('password');
    expect(input.getAttribute('inputmode')).toBe('numeric');
    expect(input.maxLength).toBe(6);
  });

  it('deactivate asks first; the open-shift refusal is shown as it comes', async () => {
    await setup();
    spyOn((component as any).confirm, 'confirm').and.callFake((c: any) => c.accept());
    const toast = spyOn((component as any).messages, 'add');
    pos.setStoreStatus.and.returnValue(fail400('Close all shifts before deactivating the store.'));
    component.toggleStatus();
    expect(pos.setStoreStatus).toHaveBeenCalledWith('s-1', false);
    expect(toast).toHaveBeenCalledWith(jasmine.objectContaining({ detail: 'Close all shifts before deactivating the store.' }));
  });

  it('configuration saves the whole store with the rowVersion', async () => {
    await setup();
    component.config!.varianceThreshold = 250;
    component.config!.receiptFooter = '  Come again  ';
    pos.updateStore.and.returnValue(ok(storeFixture({ varianceThreshold: 250 })));
    component.saveConfig();
    expect(pos.updateStore).toHaveBeenCalledWith('s-1', jasmine.objectContaining({
      varianceThreshold: 250, receiptFooter: 'Come again', rowVersion: 'rv1', code: 'S1', warehouseUuid: 'w-1', taxInclusivePricing: true
    }));
    expect(component.store!.varianceThreshold).toBe(250);
  });

  it('shifts tab highlights a variance beyond the threshold; reports total per day', async () => {
    await setup();
    expect(pos.getShifts).toHaveBeenCalledWith(jasmine.objectContaining({ storeUuid: 's-1', pageSize: 25 }));
    const reportCall = pos.getShifts.calls.all().find(c => c.args[0]!.pageSize === 500)!;
    expect(reportCall.args[0]!.from).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    expect(component.shifts.map(s => s.varianceHigh)).toEqual([false, true]);
    expect(component.reportRows.map(r => r.date)).toEqual(['2026-10-10', '2026-10-09']);
    expect(component.reportTotal!.net).toBe(1660);
    expect(component.reportTotal!.shifts).toBe(2);
    expect(q('report-total')!.textContent).toContain('1,660.00');
  });

  it('a missing store shows the error', async () => {
    await setup();
    pos.getStore.and.returnValue(fail400('Store not found.'));
    component.load();
    fixture.detectChanges();
    expect(q('store-load-error')!.textContent).toContain('Store not found.');
  });

  it('A39: the Returns tab loads the store\'s effective policy; editing needs POS_RETURN_POLICY_MANAGE', async () => {
    await setup([...ALL, 'POS_RETURN_POLICY_MANAGE']);
    expect(returnsApi.getStorePolicy).toHaveBeenCalledWith('s-1');
    (q('anchor-sec-returns') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(q('section-returns')!.style.display).toBe('');
    expect(q('org-summary')!.textContent).toContain('organization default');
    expect((q('mode-store')!.querySelector('input') as HTMLInputElement).disabled).toBeFalse();

    await setup();
    await fixture.whenStable();
    fixture.detectChanges();
    expect((q('mode-store')!.querySelector('input') as HTMLInputElement).disabled).toBeTrue();
  });
});
