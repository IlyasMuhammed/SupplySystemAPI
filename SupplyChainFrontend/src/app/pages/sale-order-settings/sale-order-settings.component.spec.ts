import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { TabViewChangeEvent } from 'primeng/tabview';
import { Subject, of, throwError } from 'rxjs';

import { SaleOrderSettingsComponent } from './sale-order-settings.component';
import {
  SaleOrderConfigService, SaleOrderConfigModel, SaleOrderConfigAuditModel, DepartmentOptionModel
} from '../../services/sale-order-config.service';
import { AuthService } from '../service/auth.service';

function config(overrides: Partial<SaleOrderConfigModel> = {}): SaleOrderConfigModel {
  return {
    uuid: 'cfg-1', autoPoEnabled: true, supplierSelectionMode: 'BEST_MATCH', autoPoApprovalMode: 'REQUIRE_WORKFLOW',
    dropShipEnabled: false, selfPickupEnabled: true, defaultFulfillmentMode: 'IN_STOCK', reservationTtlHours: 72,
    partialFulfillmentAllowed: true, emailIntimationEnabled: true, intimationDepartmentId: null,
    intimationCcEmails: null, shipmentRequiredDefault: true, updatedBy: null, updatedAt: null,
    ...overrides
  };
}

const DEPARTMENTS: DepartmentOptionModel[] = [
  { departmentId: 1, name: 'Supply', code: 'SUP', hasHead: true },
  { departmentId: 2, name: 'Warehouse', code: null, hasHead: false }
];

function auditRow(overrides: Partial<SaleOrderConfigAuditModel> = {}): SaleOrderConfigAuditModel {
  return {
    id: 1, fieldChanged: 'AutoPoEnabled', oldValue: 'True', newValue: 'False',
    changedBy: 7, changedByName: 'Sara Khan', changedAt: '2026-09-21T10:00:00Z',
    ...overrides
  };
}

function ok<T>(result: T) {
  return of({ success: true, message: '', result } as any);
}

function page(rows: SaleOrderConfigAuditModel[], total = rows.length, pageNo = 1) {
  return ok({ data: rows, totalRecords: total, page: pageNo, pageSize: 20, totalPages: Math.ceil(total / 20) });
}

describe('SaleOrderSettingsComponent', () => {
  let fixture: ComponentFixture<SaleOrderSettingsComponent>;
  let component: SaleOrderSettingsComponent;
  let service: jasmine.SpyObj<SaleOrderConfigService>;
  let toasts: jasmine.Spy;
  let permissions: string[];

  const auth = { hasPermission: (code: string) => permissions.includes(code) } as unknown as AuthService;

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  function inner<T extends HTMLElement>(testId: string, selector: string): T {
    return query(testId)!.querySelector(selector) as T;
  }

  function lastToast() {
    return toasts.calls.mostRecent().args[0] as { severity: string; summary: string; detail: string };
  }

  function set(name: string, value: unknown) {
    component.form.get(name)!.setValue(value);
    fixture.detectChanges();
  }

  async function setup(model: SaleOrderConfigModel = config(), departments: DepartmentOptionModel[] = DEPARTMENTS) {
    service = jasmine.createSpyObj<SaleOrderConfigService>('SaleOrderConfigService',
      ['getConfig', 'updateConfig', 'getDepartments', 'getAudit']);
    service.getConfig.and.returnValue(ok(model));
    service.getDepartments.and.returnValue(ok(departments));
    service.getAudit.and.returnValue(page([]));
    service.updateConfig.and.callFake((body: any) => ok({ ...model, ...body, uuid: model.uuid, updatedAt: '2026-09-21T12:00:00Z' }));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [SaleOrderSettingsComponent],
      providers: [
        provideNoopAnimations(),
        { provide: SaleOrderConfigService, useValue: service },
        { provide: AuthService, useValue: auth }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(SaleOrderSettingsComponent);
    component = fixture.componentInstance;
    toasts = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
  }

  beforeEach(() => { permissions = ['SALE_ORDER_CONFIG_READ', 'SALE_ORDER_CONFIG_WRITE']; });

  // ── Loading ────────────────────────────────────────────────────────────────

  it('loads the saved policy into the form, with nothing unsaved and nothing to save', async () => {
    await setup(config({
      autoPoEnabled: false, supplierSelectionMode: 'MANUAL', autoPoApprovalMode: 'DRAFT_ONLY', dropShipEnabled: true,
      defaultFulfillmentMode: 'BACK_TO_BACK', reservationTtlHours: 96, intimationDepartmentId: 1,
      intimationCcEmails: 'a@x.com;b@x.com'
    }));
    fixture.detectChanges();

    expect(service.getConfig).toHaveBeenCalledTimes(1);
    expect(service.getDepartments).toHaveBeenCalledTimes(1);
    expect(component.form.getRawValue()).toEqual({
      autoPoEnabled: false, supplierSelectionMode: 'MANUAL', autoPoApprovalMode: 'DRAFT_ONLY',
      defaultFulfillmentMode: 'BACK_TO_BACK', dropShipEnabled: true, selfPickupEnabled: true,
      partialFulfillmentAllowed: true, shipmentRequiredDefault: true, reservationTtlHours: 96,
      emailIntimationEnabled: true, intimationDepartmentId: 1, intimationCcEmails: 'a@x.com, b@x.com'
    });
    expect(component.dirty).toBeFalse();
    expect(query('all-saved')).not.toBeNull();
    expect(inner<HTMLButtonElement>('save', 'button').disabled).toBeTrue();
    expect(inner<HTMLButtonElement>('discard', 'button').disabled).toBeTrue();
  });

  it('says when it was last changed, or that it is still the defaults', async () => {
    await setup(config({ updatedAt: '2026-09-20T09:30:00Z' }));
    fixture.detectChanges();
    expect(query('last-changed')!.textContent).toContain('Last changed');

    await setup(config({ updatedAt: null }));
    fixture.detectChanges();
    expect(query('last-changed')!.textContent).toContain('Still on the default settings');
  });

  it('shows why it could not load and tries again when asked', async () => {
    await setup();
    service.getConfig.and.returnValue(throwError(() => ({ status: 500 })));
    fixture.detectChanges();

    expect(query('load-failed')).not.toBeNull();
    expect(query('settings-form')).toBeNull();

    service.getConfig.and.returnValue(ok(config()));
    inner<HTMLButtonElement>('retry', 'button').click();
    fixture.detectChanges();

    expect(query('load-failed')).toBeNull();
    expect(query('settings-form')).not.toBeNull();
  });

  it('treats an answer without a policy as a failure to load', async () => {
    await setup();
    service.getConfig.and.returnValue(of({ success: false, message: 'no', result: null } as any));
    fixture.detectChanges();

    expect(component.loadFailed).toBeTrue();
  });

  // ── Editing ────────────────────────────────────────────────────────────────

  it('counts what has been changed, and lets it be thrown away', async () => {
    await setup();
    fixture.detectChanges();

    set('reservationTtlHours', 24);
    set('selfPickupEnabled', false);

    expect(component.dirty).toBeTrue();
    expect(query('unsaved')!.textContent).toContain('2 unsaved changes');
    expect(inner<HTMLButtonElement>('save', 'button').disabled).toBeFalse();

    inner<HTMLButtonElement>('discard', 'button').click();
    fixture.detectChanges();

    expect(component.form.get('reservationTtlHours')!.value).toBe(72);
    expect(component.form.get('selfPickupEnabled')!.value).toBeTrue();
    expect(component.dirty).toBeFalse();
    expect(query('all-saved')).not.toBeNull();
  });

  it('says one change is one change', async () => {
    await setup();
    fixture.detectChanges();

    set('partialFulfillmentAllowed', false);

    expect(query('unsaved')!.textContent).toContain('1 unsaved change');
    expect(query('unsaved')!.textContent).not.toContain('changes');
  });

  it('is no longer changed when a setting is put back', async () => {
    await setup();
    fixture.detectChanges();

    set('reservationTtlHours', 24);
    set('reservationTtlHours', 72);

    expect(component.dirty).toBeFalse();
  });

  it('sets a behaviour by clicking its card', async () => {
    await setup();
    fixture.detectChanges();

    (query('supplierSelectionMode-MANUAL') as HTMLInputElement).click();
    fixture.detectChanges();

    expect(component.form.get('supplierSelectionMode')!.value).toBe('MANUAL');
    expect(query('supplierSelectionMode-MANUAL')!.closest('label')!.classList).toContain('selected');
    expect(query('supplierSelectionMode-BEST_MATCH')!.closest('label')!.classList).not.toContain('selected');
  });

  it('turns a switch by clicking it', async () => {
    await setup();
    fixture.detectChanges();

    inner<HTMLInputElement>('dropShipEnabled', 'input').click();
    fixture.detectChanges();

    expect(component.form.get('dropShipEnabled')!.value).toBeTrue();
  });

  it('marks the recommended supplier selection, and the approval that skips review', async () => {
    await setup();
    fixture.detectChanges();

    expect(query('supplierSelectionMode-BEST_MATCH')!.closest('label')!.textContent).toContain('Recommended');
    expect(query('autoPoApprovalMode-AUTO_SEND')!.closest('label')!.textContent).toContain('No human review');
    expect(query('autoPoApprovalMode-DRAFT_ONLY')!.closest('label')!.textContent).not.toContain('No human review');
  });

  // ── Automatic purchase orders off ──────────────────────────────────────────

  it('sets aside the two purchase order choices while automatic purchase orders are off, and keeps their values', async () => {
    await setup(config({ supplierSelectionMode: 'DEFAULT_SUPPLIER', autoPoApprovalMode: 'DRAFT_ONLY' }));
    fixture.detectChanges();
    expect(query('auto-po-off-note')).toBeNull();

    set('autoPoEnabled', false);

    expect(component.form.get('supplierSelectionMode')!.disabled).toBeTrue();
    expect(component.form.get('autoPoApprovalMode')!.disabled).toBeTrue();
    expect(query('auto-po-off-note')).not.toBeNull();
    expect(component.value.supplierSelectionMode).toBe('DEFAULT_SUPPLIER');
    expect(component.value.autoPoApprovalMode).toBe('DRAFT_ONLY');
    expect((query('supplierSelectionMode-MANUAL') as HTMLInputElement).disabled).toBeTrue();

    set('autoPoEnabled', true);

    expect(component.form.get('supplierSelectionMode')!.enabled).toBeTrue();
    expect(query('auto-po-off-note')).toBeNull();
  });

  it('starts with the two choices set aside when the saved policy has automatic purchase orders off', async () => {
    await setup(config({ autoPoEnabled: false }));
    fixture.detectChanges();

    expect(component.form.get('supplierSelectionMode')!.disabled).toBeTrue();
    expect(component.form.get('autoPoApprovalMode')!.disabled).toBeTrue();
  });

  // ── Drop ship as the default ───────────────────────────────────────────────

  it('will not offer drop ship as the default while drop shipping is off', async () => {
    await setup();
    fixture.detectChanges();

    expect((query('defaultFulfillmentMode-DROP_SHIP') as HTMLInputElement).disabled).toBeTrue();
    expect((query('defaultFulfillmentMode-IN_STOCK') as HTMLInputElement).disabled).toBeFalse();

    set('dropShipEnabled', true);

    expect((query('defaultFulfillmentMode-DROP_SHIP') as HTMLInputElement).disabled).toBeFalse();
  });

  it('refuses to save drop ship as the default once drop shipping is turned off, and says why', async () => {
    await setup(config({ dropShipEnabled: true, defaultFulfillmentMode: 'DROP_SHIP' }));
    fixture.detectChanges();

    set('dropShipEnabled', false);

    expect(query('drop-ship-default-error')).not.toBeNull();

    component.reviewAndSave();

    expect(component.reviewVisible).toBeFalse();
    expect(service.updateConfig).not.toHaveBeenCalled();
    expect(lastToast().severity).toBe('warn');
    expect(lastToast().detail).toBe('Drop ship is the default fulfilment, so drop shipping has to be turned on.');
  });

  it('refuses to save customer pickup as the starting point once pickup is turned off, and says why', async () => {
    await setup(config({ shipmentRequiredDefault: false, selfPickupEnabled: true }));
    fixture.detectChanges();

    set('selfPickupEnabled', false);

    expect(query('pickup-default-error')).not.toBeNull();

    component.reviewAndSave();

    expect(component.reviewVisible).toBeFalse();
    expect(service.updateConfig).not.toHaveBeenCalled();
    expect(lastToast().detail).toBe('New orders start as customer pickup, so customer pickup has to be turned on.');

    set('shipmentRequiredDefault', true);

    expect(query('pickup-default-error')).toBeNull();
  });

  // ── Reservation hold ───────────────────────────────────────────────────────

  it('shows a long hold in days beside the hours', async () => {
    await setup();
    fixture.detectChanges();
    expect(query('hold-days')!.textContent).toContain('3 days');

    set('reservationTtlHours', 24);
    expect(query('hold-days')).toBeNull();
  });

  it('will not save a hold that is empty, below the minimum or above the maximum', async () => {
    await setup();
    fixture.detectChanges();

    for (const bad of [null, 0, -5, 8761]) {
      set('reservationTtlHours', bad);
      component.reviewAndSave();

      expect(component.reviewVisible).withContext(`${bad}`).toBeFalse();
      expect(lastToast().detail).withContext(`${bad}`).toBe('Hold reservations for between 1 and 8760 hours.');
    }
    expect(query('hold-error')).not.toBeNull();
    expect(service.updateConfig).not.toHaveBeenCalled();
  });

  it('accepts the two ends of the range', async () => {
    await setup();
    fixture.detectChanges();

    for (const good of [1, 8760]) {
      set('reservationTtlHours', good);
      expect(component.form.get('reservationTtlHours')!.valid).withContext(`${good}`).toBeTrue();
    }
  });

  // ── Copy emails ────────────────────────────────────────────────────────────

  it('will not save a copy list with an address that is not one, and names it', async () => {
    await setup();
    fixture.detectChanges();

    set('intimationCcEmails', 'good@x.com, nope');
    component.reviewAndSave();
    fixture.detectChanges();

    expect(component.reviewVisible).toBeFalse();
    expect(lastToast().detail).toBe('These copy addresses are not valid: nope.');
    expect(query('cc-error')!.textContent).toContain('nope');
    expect(service.updateConfig).not.toHaveBeenCalled();
  });

  it('will not save a copy list that is too long', async () => {
    await setup();
    fixture.detectChanges();

    set('intimationCcEmails', Array.from({ length: 60 }, (_, i) => `person${i}@example.com`).join(', '));
    component.reviewAndSave();
    fixture.detectChanges();

    expect(component.reviewVisible).toBeFalse();
    expect(lastToast().detail).toContain('too long');
    expect(query('cc-too-long')).not.toBeNull();
  });

  // ── Departments ────────────────────────────────────────────────────────────

  it('offers the departments, and says which have nobody at their head', async () => {
    await setup();
    fixture.detectChanges();

    expect(component.departmentOptions).toEqual([
      { label: 'Supply (SUP)', value: 1 },
      { label: 'Warehouse, no head assigned', value: 2 }
    ]);
    expect(query('no-departments')).toBeNull();
  });

  it('says so when the organization has no departments at all', async () => {
    await setup(config(), []);
    fixture.detectChanges();

    expect(component.departmentOptions).toEqual([]);
    expect(query('no-departments')).not.toBeNull();
  });

  it('keeps the saved department when the departments cannot be loaded, and says so', async () => {
    await setup(config({ intimationDepartmentId: 12 }));
    service.getDepartments.and.returnValue(throwError(() => ({ status: 500 })));
    fixture.detectChanges();

    expect(query('departments-failed')).not.toBeNull();
    expect(query('no-departments')).toBeNull();
    expect(component.departmentOptions).toEqual([{ label: 'Department 12', value: 12 }]);
    expect(component.form.get('intimationDepartmentId')!.value).toBe(12);
  });

  it('shows a saved department that no longer exists rather than an empty box', async () => {
    await setup(config({ intimationDepartmentId: 99 }));
    fixture.detectChanges();

    expect(component.departmentOptions[0]).toEqual({ label: 'Department 99 (not found)', value: 99 });
  });

  it('warns when the chosen department has nobody at its head', async () => {
    await setup();
    fixture.detectChanges();
    expect(query('no-head')).toBeNull();

    set('intimationDepartmentId', 2);
    expect(query('no-head')).not.toBeNull();

    set('intimationDepartmentId', 1);
    expect(query('no-head')).toBeNull();

    set('intimationDepartmentId', null);
    expect(query('no-head')).toBeNull();
  });

  // ── Reviewing and saving ───────────────────────────────────────────────────

  it('does nothing when there is nothing to save', async () => {
    await setup();
    fixture.detectChanges();

    component.reviewAndSave();

    expect(component.reviewVisible).toBeFalse();
    expect(lastToast().severity).toBe('info');
    expect(service.updateConfig).not.toHaveBeenCalled();
  });

  it('shows what will change, from and to, before anything is sent', async () => {
    await setup(config({ intimationDepartmentId: 1 }));
    fixture.detectChanges();
    set('supplierSelectionMode', 'MANUAL');
    set('reservationTtlHours', 168);

    component.reviewAndSave();
    fixture.detectChanges();

    expect(component.reviewVisible).toBeTrue();
    expect(service.updateConfig).not.toHaveBeenCalled();
    const rows = Array.from(fixture.nativeElement.querySelectorAll('[data-testid="review-row"]'))
      .map((r: any) => Array.from(r.querySelectorAll('td')).map((td: any) => td.textContent.trim()));
    expect(rows).toEqual([
      ['Supplier selection', 'Best match', 'Manual'],
      ['Reservation hold', '3 days', '7 days']
    ]);
  });

  it('warns in the review when purchase orders will be approved without review', async () => {
    await setup();
    fixture.detectChanges();
    set('autoPoApprovalMode', 'AUTO_SEND');

    component.reviewAndSave();
    fixture.detectChanges();

    expect(query('review-notes')!.textContent).toContain('no human review');
    expect(query('review-notes')!.querySelector('li.warn')).not.toBeNull();
  });

  it('goes back to editing without sending anything, and keeps the changes', async () => {
    await setup();
    fixture.detectChanges();
    set('selfPickupEnabled', false);
    component.reviewAndSave();
    fixture.detectChanges();

    inner<HTMLButtonElement>('review-cancel', 'button').click();
    fixture.detectChanges();

    expect(component.reviewVisible).toBeFalse();
    expect(service.updateConfig).not.toHaveBeenCalled();
    expect(component.dirty).toBeTrue();
  });

  it('saves the whole policy, then shows it as saved with the time it was changed', async () => {
    await setup(config({ intimationDepartmentId: 1 }));
    fixture.detectChanges();
    set('autoPoApprovalMode', 'DRAFT_ONLY');
    set('intimationCcEmails', 'a@x.com; b@x.com');
    component.reviewAndSave();

    component.confirmSave();
    fixture.detectChanges();

    expect(service.updateConfig).toHaveBeenCalledOnceWith({
      autoPoEnabled: true, supplierSelectionMode: 'BEST_MATCH', autoPoApprovalMode: 'DRAFT_ONLY',
      dropShipEnabled: false, selfPickupEnabled: true, defaultFulfillmentMode: 'IN_STOCK', reservationTtlHours: 72,
      partialFulfillmentAllowed: true, emailIntimationEnabled: true, intimationDepartmentId: 1,
      intimationCcEmails: 'a@x.com, b@x.com', shipmentRequiredDefault: true
    });
    expect(lastToast().severity).toBe('success');
    expect(component.reviewVisible).toBeFalse();
    expect(component.isSaving).toBeFalse();
    expect(component.dirty).toBeFalse();
    expect(query('all-saved')).not.toBeNull();
    expect(query('last-changed')!.textContent).toContain('Last changed');
  });

  it('sends "none" for an empty department and an empty copy list', async () => {
    await setup();
    fixture.detectChanges();
    set('selfPickupEnabled', false);
    component.reviewAndSave();

    component.confirmSave();

    const body = service.updateConfig.calls.mostRecent().args[0];
    expect(body.intimationDepartmentId).toBeNull();
    expect(body.intimationCcEmails).toBeNull();
  });

  it('measures later changes against what was just saved', async () => {
    await setup();
    fixture.detectChanges();
    set('selfPickupEnabled', false);
    component.reviewAndSave();
    component.confirmSave();
    fixture.detectChanges();

    set('selfPickupEnabled', true);

    expect(component.dirty).toBeTrue();
    expect(component.changes.map(c => c.key)).toEqual(['selfPickupEnabled']);
    expect(component.changes[0].from).toBe('Off');
  });

  it('shows the servers reason when a save is refused, keeps the changes, and can be tried again', async () => {
    await setup();
    fixture.detectChanges();
    set('selfPickupEnabled', false);
    component.reviewAndSave();
    service.updateConfig.and.returnValue(throwError(() => ({ status: 400, error: { message: 'Not a valid mode.' } })));

    component.confirmSave();

    expect(lastToast().severity).toBe('error');
    expect(lastToast().summary).toBe('Not saved');
    expect(lastToast().detail).toBe('Not a valid mode.');
    expect(component.reviewVisible).toBeFalse();
    expect(component.isSaving).toBeFalse();
    expect(component.dirty).toBeTrue();

    service.updateConfig.and.callFake((body: any) => ok({ ...config(), ...body }));
    component.reviewAndSave();
    component.confirmSave();

    expect(component.dirty).toBeFalse();
    expect(lastToast().severity).toBe('success');
  });

  it('says who may change the settings when the server refuses with 403', async () => {
    await setup();
    fixture.detectChanges();
    set('selfPickupEnabled', false);
    component.reviewAndSave();
    service.updateConfig.and.returnValue(throwError(() => ({ status: 403 })));

    component.confirmSave();

    expect(lastToast().detail).toBe('Only the Supply Department Administrator can change these settings.');
  });

  it('says nothing was changed when the server gives no reason', async () => {
    await setup();
    fixture.detectChanges();
    set('selfPickupEnabled', false);
    component.reviewAndSave();
    service.updateConfig.and.returnValue(throwError(() => ({ status: 500 })));

    component.confirmSave();

    expect(lastToast().detail).toBe('The settings could not be saved. Nothing was changed.');
  });

  it('treats a refusal in a 200 answer as a failure and keeps the changes', async () => {
    await setup();
    fixture.detectChanges();
    set('selfPickupEnabled', false);
    component.reviewAndSave();
    service.updateConfig.and.returnValue(of({ success: false, message: 'Refused.', result: null } as any));

    component.confirmSave();

    expect(lastToast().detail).toBe('Refused.');
    expect(component.dirty).toBeTrue();
  });

  it('sends once however often Save is pressed while it is working', async () => {
    await setup();
    fixture.detectChanges();
    set('selfPickupEnabled', false);
    component.reviewAndSave();
    service.updateConfig.and.returnValue(new Subject<any>());

    component.confirmSave();
    component.confirmSave();

    expect(service.updateConfig).toHaveBeenCalledTimes(1);
    expect(component.isSaving).toBeTrue();
  });

  // ── Who can change it ──────────────────────────────────────────────────────

  it('is view only without the write permission: no fields to change, no way to save, and it says so', async () => {
    permissions = ['SALE_ORDER_CONFIG_READ'];
    await setup(config({ autoPoEnabled: false }));
    fixture.detectChanges();

    expect(component.canEdit).toBeFalse();
    expect(component.form.disabled).toBeTrue();
    expect(query('view-only')).not.toBeNull();
    expect(query('read-only-note')!.textContent).toContain('Only the Supply Department Administrator');
    expect(query('action-bar')).toBeNull();
    expect((query('supplierSelectionMode-MANUAL') as HTMLInputElement).disabled).toBeTrue();

    component.reviewAndSave();
    component.confirmSave();

    expect(component.reviewVisible).toBeFalse();
    expect(service.updateConfig).not.toHaveBeenCalled();
  });

  it('can be changed with the write permission, and shows neither the note nor the tag', async () => {
    await setup();
    fixture.detectChanges();

    expect(component.form.enabled).toBeTrue();
    expect(query('view-only')).toBeNull();
    expect(query('read-only-note')).toBeNull();
    expect(query('action-bar')).not.toBeNull();
  });

  // ── History ────────────────────────────────────────────────────────────────

  it('does not ask for the history until its tab is opened, and then only once', async () => {
    await setup();
    fixture.detectChanges();
    expect(service.getAudit).not.toHaveBeenCalled();

    component.onTabChange({ index: 1 } as TabViewChangeEvent);
    component.onTabChange({ index: 0 } as TabViewChangeEvent);
    component.onTabChange({ index: 1 } as TabViewChangeEvent);

    expect(service.getAudit).toHaveBeenCalledOnceWith(1, 20);
  });

  it('lists the changes in words, with who made them', async () => {
    await setup();
    service.getAudit.and.returnValue(page([
      auditRow({ id: 4 }),
      auditRow({ id: 3, fieldChanged: 'SupplierSelectionMode', oldValue: 'BEST_MATCH', newValue: 'MANUAL', changedByName: null, changedBy: 9 }),
      auditRow({ id: 2, fieldChanged: 'IntimationDepartmentId', oldValue: null, newValue: '1' }),
      auditRow({ id: 1, fieldChanged: 'ReservationTtlHours', oldValue: '72', newValue: '96' })
    ]));
    fixture.detectChanges();

    component.onTabChange({ index: 1 } as TabViewChangeEvent);
    fixture.detectChanges();

    const rows = Array.from(fixture.nativeElement.querySelectorAll('[data-testid="audit-row"]'))
      .map((r: any) => Array.from(r.querySelectorAll('td')).slice(1).map((td: any) => td.textContent.trim()));
    expect(rows).toEqual([
      ['Automatic purchase orders', 'On', 'Off', 'Sara Khan'],
      ['Supplier selection', 'Best match', 'Manual', 'User 9'],
      ['Notification department', 'None', 'Supply (SUP)', 'Sara Khan'],
      ['Reservation hold', '3 days', '4 days', 'Sara Khan']
    ]);
  });

  it('says so when nothing has been changed yet', async () => {
    await setup();
    fixture.detectChanges();

    component.onTabChange({ index: 1 } as TabViewChangeEvent);
    fixture.detectChanges();

    expect(query('audit-empty')!.textContent).toContain('No change has been made yet');
  });

  it('shows why the history could not load, and tries again', async () => {
    await setup();
    fixture.detectChanges();
    service.getAudit.and.returnValue(throwError(() => ({ status: 500 })));

    component.onTabChange({ index: 1 } as TabViewChangeEvent);
    fixture.detectChanges();
    expect(query('audit-failed')).not.toBeNull();

    service.getAudit.and.returnValue(page([auditRow()]));
    component.loadAudit(1);
    fixture.detectChanges();

    expect(query('audit-failed')).toBeNull();
    expect(fixture.nativeElement.querySelectorAll('[data-testid="audit-row"]').length).toBe(1);
  });

  it('pages through a long history', async () => {
    await setup();
    service.getAudit.and.callFake(((p: number) => page([auditRow({ id: p })], 45, p)) as any);
    fixture.detectChanges();

    component.onTabChange({ index: 1 } as TabViewChangeEvent);
    fixture.detectChanges();
    expect(query('audit-paginator')).not.toBeNull();

    component.onAuditPage({ first: 20, rows: 20 });

    expect(service.getAudit).toHaveBeenCalledWith(2, 20);
    expect(component.auditPage).toBe(2);
    expect(component.auditTotal).toBe(45);
  });

  it('shows no pager when the history fits on one page', async () => {
    await setup();
    service.getAudit.and.returnValue(page([auditRow()], 1));
    fixture.detectChanges();

    component.onTabChange({ index: 1 } as TabViewChangeEvent);
    fixture.detectChanges();

    expect(query('audit-paginator')).toBeNull();
  });

  it('refreshes the history after a save when it is open, and reads it fresh when opened after one', async () => {
    await setup();
    fixture.detectChanges();
    component.onTabChange({ index: 1 } as TabViewChangeEvent);
    expect(service.getAudit).toHaveBeenCalledTimes(1);

    set('selfPickupEnabled', false);
    component.reviewAndSave();
    component.confirmSave();
    expect(service.getAudit).toHaveBeenCalledTimes(2);

    component.onTabChange({ index: 0 } as TabViewChangeEvent);
    set('selfPickupEnabled', true);
    component.reviewAndSave();
    component.confirmSave();
    expect(service.getAudit).toHaveBeenCalledTimes(2);

    component.onTabChange({ index: 1 } as TabViewChangeEvent);
    expect(service.getAudit).toHaveBeenCalledTimes(3);
  });

  it('words a department in the history by name once the departments are known', async () => {
    await setup();
    fixture.detectChanges();

    expect(component.auditTo(auditRow({ fieldChanged: 'IntimationDepartmentId', newValue: '2' }))).toBe('Warehouse');
    expect(component.auditFrom(auditRow({ fieldChanged: 'IntimationDepartmentId', oldValue: '8' }))).toBe('Department 8');
  });
});
