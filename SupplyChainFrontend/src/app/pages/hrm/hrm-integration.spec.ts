import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { Router, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';
import { MessageService } from 'primeng/api';

import { QuickBooksIntegrationService } from '../../services/quickbooks-integration.service';
import { SyncControlTabComponent } from '../integrations/quickbooks/sync-control-tab/sync-control-tab.component';

import { ModuleAdminService } from '../../services/module.service';
import { completeSyncControl, SYNC_CONTROL_HINT, SYNC_CONTROL_LABEL } from '../integrations/quickbooks/a44.shared';
import { SYNC_CONTROL_HRM_TYPES } from '../../models/quickbooks-integration.models';
import { AuthService } from '../service/auth.service';
import { ModulesComponent } from '../settings/modules/modules.component';
import { HrmRecordsComponent } from './core/hrm-records.component';
import { mountHrm } from './core/hrm.testing';
import { COST_CENTER_MAP } from './configs/self.config';

describe('A49 sync control (332, G-15)', () => {
  it('no HRM row from the server: none shown, and never "no row = enabled"', () => {
    const rows = completeSyncControl([]);
    expect(rows.length).toBe(11);
    expect(rows.some(r => (SYNC_CONTROL_HRM_TYPES as readonly string[]).includes(r.documentType))).toBeFalse();
  });

  it('HRM rows the server sent come last, with their state, labelled and explained', () => {
    const rows = completeSyncControl([{ documentType: 'PayrollRun', isEnabled: false, holdWhenDisabled: true, heldCount: 2 }, { documentType: 'expense', isEnabled: false, holdWhenDisabled: true } as any]);
    expect(rows.slice(11).map(r => r.documentType)).toEqual(['PayrollRun', 'Expense']);
    expect(rows[11]).toEqual(jasmine.objectContaining({ isEnabled: false, holdWhenDisabled: true, heldCount: 2 }));
    for (const t of SYNC_CONTROL_HRM_TYPES) { expect(SYNC_CONTROL_LABEL[t]).toBeTruthy(); expect(SYNC_CONTROL_HINT[t]).toBeTruthy(); }
  });
});

describe('A49 sync control HRM_SYNC_NOT_READY (§10.5)', () => {
  it('lists result.problems on the refused row, and the next good save clears them', async () => {
    const service = jasmine.createSpyObj<QuickBooksIntegrationService>('QuickBooksIntegrationService', ['getSyncControl', 'updateSyncControl', 'releaseHeld']);
    service.getSyncControl.and.returnValue(of([{ documentType: 'PayrollRun', isEnabled: false, holdWhenDisabled: true, heldCount: 0 } as any]));
    service.updateSyncControl.and.returnValue(throwError(() => ({ status: 409, error: { message: 'HRM_SYNC_NOT_READY: Payroll runs cannot be turned on yet.', result: { documentType: 'PayrollRun', problems: ['Class tracking is off.', 'Cost center HO has no Class.'] } } })));
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [SyncControlTabComponent],
      providers: [provideNoopAnimations(), MessageService, { provide: QuickBooksIntegrationService, useValue: service }]
    }).compileComponents();
    const fixture = TestBed.createComponent(SyncControlTabComponent);
    fixture.componentInstance.canManage = true;
    fixture.detectChanges();
    const c = fixture.componentInstance;
    const row = c.rows.find(r => r.documentType === 'PayrollRun')!;
    c.setEnabled(row, true);
    c.save();
    fixture.detectChanges();
    const text = () => fixture.nativeElement.querySelector('[data-testid="sc-problems-PayrollRun"]')?.textContent ?? '';
    expect(text()).toContain('Class tracking is off.');
    expect(text()).toContain('Cost center HO has no Class.');
    service.updateSyncControl.and.returnValue(of({ row: { documentType: 'PayrollRun', isEnabled: true, holdWhenDisabled: true, heldCount: 0 }, released: 0 } as any));
    c.save();
    fixture.detectChanges();
    expect(text()).toBe('');
  });
});

describe('A49 cost center mapping (331)', () => {
  const ROW = { uuid: 'm1', costCenterCode: 'CC/1', costCenterName: 'Head office', isActive: true, isMapped: false };

  it('shows what is not mapped; edit and sync need the edit code; the code goes in the path escaped', async () => {
    const v = await mountHrm(HrmRecordsComponent, { perms: ['HRM_ACCOUNT_MAPPING_VIEW'], data: { config: COST_CENTER_MAP } });
    v.flush('integrations/quickbooks/cost-center-mappings', [ROW]);
    expect(v.text('row-m1')).toContain('Not mapped');
    expect(v.q('edit-m1')).toBeNull();
    expect(v.q('cmd-sync')).toBeNull();
    const m = await mountHrm(HrmRecordsComponent, { perms: ['HRM_ACCOUNT_MAPPING_EDIT'], data: { config: COST_CENTER_MAP } });
    m.flush('integrations/quickbooks/cost-center-mappings', [ROW]);
    (m.q('cmd-sync')!.querySelector('button') as HTMLElement).click();
    m.expectReq('integrations/quickbooks/cost-center-mappings/sync', 'POST');
    (m.c as any).openEdit(ROW);
    m.http.match(r => r.url.endsWith('/reference')).forEach(r => r.flush({ success: true, result: { connected: true, loaded: true, classes: [{ id: '77', name: 'Head office' }], departments: [] } }));
    await m.fixture.whenStable();
    expect((m.c as any).lookups.options.qboClasses).toEqual([{ label: 'Head office', value: '77' }]);
    (m.c as any).saveForm();
    expect((m.c as any).formError).toBe('Class is required.');
    (m.c as any).formValues.qboClassRef = '77';
    (m.c as any).saveForm();
    expect(m.expectReq('integrations/quickbooks/cost-center-mappings/CC%2F1', 'PUT').request.body).toEqual(jasmine.objectContaining({ qboClassRef: '77', qboClassName: 'Head office' }));
  });
});

describe('A49 Settings › Modules (FD-9)', () => {
  it('switching on HRM goes to the wizard and flips the switch back; other modules enable as before', async () => {
    const enable = jasmine.createSpy('enable').and.returnValue(of({ success: true }));
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [ModulesComponent],
      providers: [provideNoopAnimations(), provideRouter([]), provideHttpClient(), provideHttpClientTesting(),
        { provide: AuthService, useValue: { hasPermission: () => true, hasAnyPermission: () => true } },
        { provide: ModuleAdminService, useValue: { list: () => of({ result: [] }), enable } }]
    }).compileComponents();
    const c = TestBed.createComponent(ModulesComponent).componentInstance;
    c.canManage = true;
    const nav = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    const vm: any = { card: { code: 'MODULE_HRM', name: 'HRM' }, on: false, toggleable: true };
    c.onToggle(vm, true);
    expect(vm.on).toBeFalse();
    expect(nav).toHaveBeenCalledWith(['/portal/pages/hrm/setup']);
    expect(enable).not.toHaveBeenCalled();
    c.onToggle({ card: { code: 'MODULE_CRM', name: 'CRM' }, on: false, toggleable: true } as any, true);
    expect(enable).toHaveBeenCalled();
  });
});
