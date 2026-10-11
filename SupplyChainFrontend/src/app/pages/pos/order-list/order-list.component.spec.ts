import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';

import { PosOrderListComponent, orderRow } from './order-list.component';
import { PosAdminService, PosOrderListItem } from '../../../services/pos-admin.service';
import { CustomerService } from '../../../services/customer.service';
import { AuthService } from '../../service/auth.service';
import { ok, storeFixture, terminalFixture } from '../pos-fixtures.testing';

const item = (over: Partial<PosOrderListItem> = {}): PosOrderListItem => ({
  uuid: 'o-1', orderNumber: 'POS-S1-20261010-0001', orderType: 'SALE', status: 'COMPLETED', storeName: 'Mall', terminalName: 'Till 1',
  cashierName: 'Sara', customerName: 'Acme', grandTotal: 334, paymentStatus: 'PAID', flags: [], createdAt: '2026-10-10T10:00:00',
  isOffline: false, financePosted: true, stockPosted: true, ...over
});

/** A38 FSD §17.7.4 — the POS orders list: filters, flag pills, posting state. */
describe('PosOrderListComponent (A38)', () => {
  let fixture: ComponentFixture<PosOrderListComponent>;
  let component: PosOrderListComponent;
  let el: HTMLElement;
  let pos: jasmine.SpyObj<PosAdminService>;
  let customers: jasmine.SpyObj<CustomerService>;

  async function setup(query: Record<string, string> = {}, perms = ['POS_ORDER_VIEW', 'POS_TERMINAL_VIEW', 'CUSTOMER_VIEW']) {
    pos = jasmine.createSpyObj<PosAdminService>('PosAdminService', ['getOrders', 'getStores', 'getTerminals']);
    pos.getStores.and.returnValue(ok({ data: [storeFixture()], totalRecords: 1 }));
    pos.getTerminals.and.returnValue(ok([terminalFixture()]));
    pos.getOrders.and.returnValue(ok({ data: [
      item({ flags: ['CREDIT_OVER_LIMIT', 'NEGATIVE_STOCK', 'OFFLINE', 'PRICE_CHANGED', 'DISCOUNT_UNAUTHORIZED'], isOffline: true,
             financePosted: false, paymentStatus: 'CREDIT_SALE' }),
      item({ uuid: 'o-2', orderNumber: 'RET-S1-20261010-0001', orderType: 'RETURN', status: 'COMPLETED', paymentStatus: 'REFUNDED' })
    ], totalRecords: 2 }));
    customers = jasmine.createSpyObj<CustomerService>('CustomerService', ['getCustomers']);
    customers.getCustomers.and.returnValue(ok({ data: [{ uuid: 'c-1', code: 'C-00001', name: 'Acme' }], totalRecords: 1 }));
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [PosOrderListComponent],
      providers: [
        provideNoopAnimations(), provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(query) } } },
        { provide: PosAdminService, useValue: pos },
        { provide: CustomerService, useValue: customers },
        { provide: AuthService, useValue: {
          hasPermission: (c: string) => perms.includes(c),
          hasAnyPermission: (...c: string[]) => c.some(x => perms.includes(x))
        } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(PosOrderListComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    el = fixture.nativeElement;
  }

  it('shows the flags as pills and the posting state', async () => {
    await setup();
    const rows = Array.from(el.querySelectorAll('[data-testid="order-row"]'));
    expect(rows.length).toBe(2);
    const flags = Array.from(rows[0].querySelectorAll('[data-flag]')).map(f => f.getAttribute('data-flag'));
    expect(flags).toEqual(['CREDIT_OVER_LIMIT', 'NEGATIVE_STOCK', 'OFFLINE', 'PRICE_CHANGED', 'DISCOUNT_UNAUTHORIZED']);
    expect(rows[0].querySelector('[data-testid="flags"]')!.textContent).toContain('Over credit limit');
    expect(rows[0].querySelector('[data-testid="posting"]')!.textContent).toContain('Stock only');
    expect(rows[1].textContent).toContain('Return');
    expect(rows[1].querySelector('[data-testid="posting"]')!.textContent).toContain('Posted');
  });

  it('A39: exchanges get a pill and a type filter; the cross-store flag is a pill', async () => {
    await setup();
    expect(component.typeOptions.map(o => o.value)).toContain('EXCHANGE');
    pos.getOrders.and.returnValue(ok({ data: [item({ uuid: 'x-1', orderNumber: 'EXC-1', orderType: 'EXCHANGE', flags: ['CROSS_STORE'] })], totalRecords: 1 }));
    component.orderType = 'EXCHANGE';
    component.applyFilters();
    fixture.detectChanges();
    expect(pos.getOrders.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({ orderType: 'EXCHANGE' }));
    expect(el.querySelector('[data-testid="exchange-pill"]')).not.toBeNull();
    expect(el.querySelector('[data-flag="CROSS_STORE"]')!.textContent).toContain('Cross-store');
  });

  it('orderRow labels each posting combination', () => {
    expect(orderRow(item({ stockPosted: false, financePosted: false })).postingLabel).toBe('Not posted');
    expect(orderRow(item({ stockPosted: false, financePosted: true })).postingLabel).toBe('Finance only');
    expect(orderRow(item()).postingTone).toBe('ok');
  });

  it('sends every filter; a store loads its terminals and clears the terminal', async () => {
    await setup();
    component.storeUuid = 's-1';
    component.onStoreChange();
    expect(pos.getTerminals).toHaveBeenCalledWith('s-1');
    expect(component.terminalOptions[0].value).toBe('t-1');
    component.terminalUuid = 't-1';
    component.customerUuid = 'c-1';
    component.orderType = 'RETURN';
    component.search = ' RET- ';
    component.setStatus('VOIDED');
    component.dateRange = [new Date(2026, 9, 10), null as any];
    component.onDateChange();   // waits for the end date
    component.dateRange = [new Date(2026, 9, 10), new Date(2026, 9, 11)];
    component.onDateChange();
    expect(pos.getOrders.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({
      storeUuid: 's-1', terminalUuid: 't-1', customerUuid: 'c-1', orderType: 'RETURN', status: 'VOIDED', search: 'RET-',
      from: '2026-10-10', to: '2026-10-11', page: 1
    }));
    component.onStoreChange();
    expect(component.terminalUuid).toBeNull();
    expect(component.customerOptions[0].value).toBe('c-1');
  });

  it('?search= (from a POS sales invoice) pre-fills the order-number search', async () => {
    await setup({ search: 'POS-S1-20261010-0001' });
    expect(pos.getOrders).toHaveBeenCalledWith(jasmine.objectContaining({ search: 'POS-S1-20261010-0001' }));
  });

  it('?shift= limits to one shift until cleared; no customer list without CUSTOMER_VIEW', async () => {
    await setup({ shift: 'sh-1' }, ['POS_ORDER_VIEW']);
    expect(pos.getOrders).toHaveBeenCalledWith(jasmine.objectContaining({ shiftUuid: 'sh-1' }));
    expect(el.querySelector('[data-testid="shift-filter"]')).not.toBeNull();
    component.clearShift();
    expect(pos.getOrders.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({ shiftUuid: null }));
    expect(customers.getCustomers).not.toHaveBeenCalled();
  });
});
