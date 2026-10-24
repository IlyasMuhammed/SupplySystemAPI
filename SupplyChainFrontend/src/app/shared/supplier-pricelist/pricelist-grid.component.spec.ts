import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of, throwError } from 'rxjs';
import { ConfirmationService, MessageService } from 'primeng/api';

import { PricelistGridComponent } from './pricelist-grid.component';
import { InventoryService } from '../../services/inventory.service';
import { MoneyService } from '../../services/money.service';
import { SupplierService } from '../../services/supplier.service';
import { PricelistItem, SupplierPricelistService } from '../../services/supplier-pricelist.service';

const row = (over: Partial<PricelistItem> = {}): PricelistItem => ({
  uuid: 'u-1', productUuid: 'p-1', productName: 'Widget', productSku: 'W-1', supplierUuid: 's-1', supplierName: 'Acme', minQuantity: 1,
  unitPrice: 15, currencyId: 'c-1', currencyCode: 'USD', pricingDateBasis: 0, validFrom: '2026-01-01', validTo: '2026-12-31', priority: 0,
  isActive: true, rowVersion: 'RV', ...over
});

describe('PricelistGridComponent (A45 C4/C6)', () => {
  let fixture: ComponentFixture<PricelistGridComponent>;
  let cmp: PricelistGridComponent;
  let api: jasmine.SpyObj<SupplierPricelistService>;
  let messages: MessageService;
  let confirmation: ConfirmationService;
  let el: HTMLElement;

  async function setup(inputs: Partial<PricelistGridComponent> = {}, rows: PricelistItem[] = [row()]) {
    api = jasmine.createSpyObj<SupplierPricelistService>('SupplierPricelistService', ['list', 'create', 'update', 'deactivate']);
    api.list.and.returnValue(of({ items: rows, total: rows.length }));
    api.deactivate.and.returnValue(of(undefined));
    const inventory = { getProducts: () => of({ success: true, result: { data: [] } }) };
    const suppliers = { getSuppliers: () => of({ success: true, result: { data: [] } }) };
    const money = { load: () => of([]), currencies: () => [], find: () => null };

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [PricelistGridComponent],
      providers: [
        provideNoopAnimations(),
        { provide: SupplierPricelistService, useValue: api },
        { provide: InventoryService, useValue: inventory },
        { provide: SupplierService, useValue: suppliers },
        { provide: MoneyService, useValue: money }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(PricelistGridComponent);
    cmp = fixture.componentInstance;
    // the component provides its own MessageService / ConfirmationService
    messages = fixture.debugElement.injector.get(MessageService);
    confirmation = fixture.debugElement.injector.get(ConfirmationService);
    for (const [k, v] of Object.entries(inputs)) fixture.componentRef.setInput(k, v);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    el = fixture.nativeElement;
  }

  const headers = () => Array.from(el.querySelectorAll('th')).map(t => t.textContent!.trim());

  it('loads filtered by product and hides the Product column (vendor column stays)', async () => {
    await setup({ productUuid: 'p-1', productName: 'Widget' });
    const filter = api.list.calls.mostRecent().args[0]!;
    expect(filter.productUuid).toBe('p-1');
    expect(filter.activeOnly).toBeTrue();
    expect(headers()).toContain('Vendor');
    expect(headers()).not.toContain('Product');
    expect(headers()).toEqual(jasmine.arrayContaining(['Min qty', 'Price', 'Currency', 'Valid period', 'Date basis', 'Priority', 'Active']));
  });

  it('loads filtered by supplier and hides the Vendor column', async () => {
    await setup({ supplierUuid: 's-1', supplierName: 'Acme' });
    expect(api.list.calls.mostRecent().args[0]!.supplierUuid).toBe('s-1');
    expect(headers()).toContain('Product');
    expect(headers()).not.toContain('Vendor');
  });

  it('shows both columns on the standalone page, with filters', async () => {
    await setup({ showFilters: true });
    expect(headers()).toContain('Product');
    expect(headers()).toContain('Vendor');
    expect(el.querySelector('[data-testid="pg-search"]')).not.toBeNull();
    expect(el.querySelector('[data-testid="pg-filter-product"]')).not.toBeNull();
  });

  it('sorts by priority then valid-from descending', async () => {
    await setup({}, [
      row({ uuid: 'a', priority: 1 }),
      row({ uuid: 'b', priority: 0, validFrom: '2026-01-01' }),
      row({ uuid: 'c', priority: 0, validFrom: '2026-06-01' })
    ]);
    expect(cmp.rows.map(r => r.uuid)).toEqual(['c', 'b', 'a']);
  });

  it('shows the resolution hint', async () => {
    await setup();
    expect(el.querySelector('[data-testid="pg-info"]')?.textContent).toContain('lowest priority number wins');
  });

  it('hides add / edit / deactivate without the write permission', async () => {
    await setup({ canEdit: false });
    expect(el.querySelector('[data-testid="pg-add"]')).toBeNull();
    expect(el.querySelector('[data-testid="pl-edit-u-1"]')).toBeNull();
    await setup({ canEdit: true });
    expect(el.querySelector('[data-testid="pg-add"]')).not.toBeNull();
    expect(el.querySelector('[data-testid="pl-deactivate-u-1"]')).not.toBeNull();
  });

  it('toggling Active only reloads with activeOnly=false', async () => {
    await setup();
    cmp.toggleActive();
    expect(api.list.calls.mostRecent().args[0]!.activeOnly).toBeFalse();
  });

  it('shows the overlap warning as a warning toast and keeps the save (BR-11)', async () => {
    await setup({ canEdit: true });
    const added: any[] = [];
    messages.messageObserver.subscribe((m: any) => added.push(m));
    const before = api.list.calls.count();
    cmp.onSaved({ item: row(), overlapWarning: 'Overlaps priority 0, 1 Jan 2026 - 31 Dec 2026.' });
    const warn = added.find(m => m.severity === 'warn');
    expect(warn.detail).toContain('Overlaps priority 0');
    expect(added.some(m => m.severity === 'success')).toBeTrue();
    expect(api.list.calls.count()).toBe(before + 1);
  });

  it('shows no warning toast when nothing overlaps', async () => {
    await setup({ canEdit: true });
    const added: any[] = [];
    messages.messageObserver.subscribe((m: any) => added.push(m));
    cmp.onSaved({ item: row() });
    expect(added.some(m => m.severity === 'warn')).toBeFalse();
  });

  it('asks before deactivating, then calls DELETE and reloads', async () => {
    await setup({ canEdit: true });
    let asked: any;
    confirmation.requireConfirmation$.subscribe(c => asked = c);
    cmp.confirmDeactivate(row());
    expect(asked.message).toBe('Deactivate this price record?');
    expect(api.deactivate).not.toHaveBeenCalled();
    const before = api.list.calls.count();
    asked.accept();
    expect(api.deactivate).toHaveBeenCalledWith('u-1');
    expect(api.list.calls.count()).toBe(before + 1);
  });

  it('does not deactivate when the confirmation is declined', async () => {
    await setup({ canEdit: true });
    let asked: any;
    confirmation.requireConfirmation$.subscribe(c => asked = c);
    cmp.confirmDeactivate(row());
    asked.reject?.();
    expect(api.deactivate).not.toHaveBeenCalled();
  });

  it('reports a failed load', async () => {
    await setup();
    api.list.and.returnValue(throwError(() => ({ error: { message: 'boom' } })));
    const added: any[] = [];
    messages.messageObserver.subscribe((m: any) => added.push(m));
    cmp.reload();
    expect(added[0].severity).toBe('error');
    expect(cmp.rows).toEqual([]);
  });
});
