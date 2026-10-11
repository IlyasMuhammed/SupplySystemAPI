import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter, Router } from '@angular/router';

import { PosStoreListComponent } from './store-list.component';
import { StoreFormDialogComponent } from '../store-form-dialog/store-form-dialog.component';
import { PosAdminService } from '../../../services/pos-admin.service';
import { InventoryService } from '../../../services/inventory.service';
import { FinanceSetupService } from '../../../services/finance-setup.service';
import { AuthService } from '../../service/auth.service';
import { fail400, ok, storeFixture } from '../pos-fixtures.testing';

const lookups = () => [
  { provide: InventoryService, useValue: { getWarehouses: () => ok([
    { id: 1, uuid: 'w-1', code: 'WH1', name: 'Main', isActive: true },
    { id: 2, uuid: 'w-2', code: 'OLD', name: 'Old', isActive: false }
  ]) } },
  { provide: FinanceSetupService, useValue: { getTaxCodes: () => ok([
    { uuid: 'tx-1', code: 'GST17', name: 'GST', ratePercent: 17, usage: 'SALES', isActive: true },
    { uuid: 'tx-2', code: 'BOTH5', name: 'Both', ratePercent: 5, usage: 'BOTH', isActive: true },
    { uuid: 'tx-3', code: 'PUR', name: 'Purchase', ratePercent: 10, usage: 'PURCHASE', isActive: true }
  ]) } }
];

/** A38 FSD §17.7.1 — the stores list and the New store dialog. */
describe('PosStoreListComponent (A38)', () => {
  let fixture: ComponentFixture<PosStoreListComponent>;
  let component: PosStoreListComponent;
  let el: HTMLElement;
  let pos: jasmine.SpyObj<PosAdminService>;

  async function setup(perms = ['POS_STORE_VIEW', 'POS_STORE_MANAGE']) {
    pos = jasmine.createSpyObj<PosAdminService>('PosAdminService', ['getStores', 'createStore']);
    pos.getStores.and.returnValue(ok({ data: [storeFixture(), storeFixture({ uuid: 's-2', code: 'S2', name: 'Airport', city: null, isActive: false,
      terminalCount: 0, activeShiftCount: 0 })], totalRecords: 2 }));
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [PosStoreListComponent],
      providers: [
        provideNoopAnimations(), provideRouter([]),
        { provide: PosAdminService, useValue: pos },
        { provide: AuthService, useValue: { hasPermission: (c: string) => perms.includes(c) } },
        ...lookups()
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(PosStoreListComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    el = fixture.nativeElement;
  }

  const q = (id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  it('shows code, name, warehouse, terminals, active shifts, city and status', async () => {
    await setup();
    const headers = Array.from(el.querySelectorAll('th')).map(h => h.textContent!.trim());
    expect(headers).toEqual(['Code', 'Name', 'Warehouse', 'Terminals', 'Active shifts', 'City', 'Status']);
    const rows = Array.from(el.querySelectorAll('[data-testid="store-row"]'));
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain('S1');
    expect(rows[0].textContent).toContain('Main WH');
    expect(rows[0].textContent).toContain('Lahore');
    expect(rows[1].textContent).toContain('Inactive');
    expect(q('store-count')!.textContent).toContain('2');
    expect(rows[0].querySelector('a')!.getAttribute('href')).toContain('/portal/pages/pos/stores/s-1');
  });

  it('filters by status chip and search', async () => {
    await setup();
    q('status-chip-ACTIVE')!.click();
    expect(pos.getStores).toHaveBeenCalledWith(jasmine.objectContaining({ isActive: true, page: 1 }));
    q('status-chip-INACTIVE')!.click();
    expect(pos.getStores).toHaveBeenCalledWith(jasmine.objectContaining({ isActive: false }));
    component.search = ' mall ';
    component.applyFilters();
    expect(pos.getStores).toHaveBeenCalledWith(jasmine.objectContaining({ search: 'mall', isActive: false }));
    component.resetFilters();
    expect(pos.getStores.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({ search: null, isActive: null }));
  });

  it('New store only with POS_STORE_MANAGE; a created store opens', async () => {
    await setup(['POS_STORE_VIEW']);
    expect(q('new-store')).toBeNull();
    await setup();
    expect(q('new-store')).not.toBeNull();
    const nav = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    component.onCreated(storeFixture({ uuid: 's-9' }));
    expect(nav).toHaveBeenCalledWith(['/portal/pages/pos', 'stores', 's-9']);
  });
});

describe('StoreFormDialogComponent (A38)', () => {
  let fixture: ComponentFixture<StoreFormDialogComponent>;
  let component: StoreFormDialogComponent;
  let pos: jasmine.SpyObj<PosAdminService>;

  async function setup() {
    pos = jasmine.createSpyObj<PosAdminService>('PosAdminService', ['createStore', 'updateStore']);
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [StoreFormDialogComponent],
      providers: [provideNoopAnimations(), { provide: PosAdminService, useValue: pos }, ...lookups()]
    }).compileComponents();
    fixture = TestBed.createComponent(StoreFormDialogComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('visible', true);
    fixture.detectChanges();
    await fixture.whenStable();
  }

  it('offers active warehouses and sales tax codes (SALES or BOTH) only', async () => {
    await setup();
    expect(component.warehouseOptions.map(o => o.value)).toEqual(['w-1']);
    expect(component.taxOptions.map(o => o.value)).toEqual(['tx-1', 'tx-2']);
  });

  it('creates with the defaults and shows a 400 verbatim', async () => {
    await setup();
    expect(component.canSave).toBeFalse();
    Object.assign(component.model, { code: 's1 ', name: ' Mall ', warehouseUuid: 'w-1', defaultTaxCodeUuid: 'tx-1' });
    expect(component.canSave).toBeTrue();

    pos.createStore.and.returnValue(fail400('Warehouse is already linked to store S0.'));
    component.save();
    expect(component.error).toBe('Warehouse is already linked to store S0.');
    const req = pos.createStore.calls.mostRecent().args[0];
    expect(req).toEqual(jasmine.objectContaining({
      code: 'S1', name: 'Mall', warehouseUuid: 'w-1', defaultTaxCodeUuid: 'tx-1',
      offlineCreditTolerancePct: 0, autoCloseShiftHours: 12, varianceThreshold: 0, isActive: true
    }));
    expect(req.timezone).toBeTruthy();

    const saved = spyOn(component.saved, 'emit');
    pos.createStore.and.returnValue(ok(storeFixture()));
    component.save();
    expect(saved).toHaveBeenCalled();
    expect(component.visible).toBeFalse();
  });

  it('edit sends a PUT with the rowVersion', async () => {
    await setup();
    fixture.componentRef.setInput('store', storeFixture());
    fixture.detectChanges();
    pos.updateStore.and.returnValue(ok(storeFixture()));
    component.save();
    expect(pos.updateStore).toHaveBeenCalledWith('s-1', jasmine.objectContaining({ rowVersion: 'rv1', code: 'S1' }));
  });
});
