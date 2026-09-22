import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { SaleOrderListComponent, SALE_ORDER_STATUS_SEVERITY, formatCode } from './sale-order-list.component';
import { SaleOrderService, SaleOrderModel } from '../../../../services/sale-order.service';
import { BusinessPartnerService } from '../../../../services/business-partner.service';
import { AuthService } from '../../../service/auth.service';

function order(overrides: Partial<SaleOrderModel> = {}): SaleOrderModel {
  return {
    uuid: 'so-1', traceId: 't-1', soNumber: 'SO-2026-00042', partnerId: 'p-1',
    orderDate: '2026-09-01T00:00:00Z', currencyId: 'c-1',
    subtotal: 4000, taxAmount: 0, discountAmount: 0, grandTotal: 4000,
    status: 'CONFIRMED', requiresShipment: true, deliveryMode: 'SHIP',
    createdDate: '2026-09-01T00:00:00Z', lines: [],
    ...overrides
  };
}

describe('SaleOrderListComponent', () => {
  let fixture: ComponentFixture<SaleOrderListComponent>;
  let component: SaleOrderListComponent;
  let service: jasmine.SpyObj<SaleOrderService>;
  let partners: jasmine.SpyObj<BusinessPartnerService>;
  let permissions: string[];

  const auth = { hasPermission: (code: string) => permissions.includes(code) } as unknown as AuthService;

  async function setup(orders: SaleOrderModel[] = [
    order(),
    order({ uuid: 'so-2', soNumber: 'SO-2026-00043', partnerId: 'p-2', deliveryMode: 'SELF_PICKUP', status: 'FULFILLED' })
  ]) {
    service = jasmine.createSpyObj<SaleOrderService>('SaleOrderService', ['getSaleOrders']);
    service.getSaleOrders.and.returnValue(of({
      success: true, message: '',
      result: { data: orders, totalRecords: orders.length, page: 1, pageSize: 20, totalPages: 1 }
    } as any));
    partners = jasmine.createSpyObj<BusinessPartnerService>('BusinessPartnerService', ['getPartnerById']);
    partners.getPartnerById.and.callFake((id: string) =>
      of({ success: true, message: '', result: { uuid: id, companyName: id === 'p-1' ? 'Acme Ltd' : 'Globex Corp' } } as any));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [SaleOrderListComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), MessageService,
        { provide: SaleOrderService, useValue: service },
        { provide: BusinessPartnerService, useValue: partners },
        { provide: AuthService, useValue: auth }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(SaleOrderListComponent);
    component = fixture.componentInstance;
  }

  beforeEach(() => { permissions = ['SALE_ORDER_VIEW']; });

  it('loads page one through the table and renders the orders', async () => {
    await setup();
    fixture.detectChanges();
    component.onPageChange({ first: 0, rows: 20 });

    expect(service.getSaleOrders).toHaveBeenCalledWith(jasmine.objectContaining({ page: 1, pageSize: 20 }));
    fixture.detectChanges();
    const text = fixture.nativeElement.textContent;
    expect(text).toContain('SO-2026-00042');
    expect(text).toContain('SO-2026-00043');
    expect(text).toContain('Self Pickup');
    expect(component.isLoading).toBeFalse();
  });

  it('sends only the filters that are set', async () => {
    await setup();
    component.selectedStatus = 'FULFILLED';
    component.searchText = '';
    component.load();

    const filter = service.getSaleOrders.calls.mostRecent().args[0]!;
    expect(filter.status).toBe('FULFILLED');
    expect(filter.search).toBeUndefined();
  });

  it('reloads from the first page when the status filter changes', async () => {
    await setup();
    component.currentPage = 3;
    component.selectedStatus = 'DRAFT';

    component.onFilterChange();

    expect(service.getSaleOrders).toHaveBeenCalledWith(jasmine.objectContaining({ page: 1, status: 'DRAFT' }));
  });

  it('offers every status the server can return as a filter', async () => {
    await setup();

    expect(component.statusOptions.map(o => o.value))
      .toEqual(['', 'DRAFT', 'CONFIRMED', 'PARTIALLY_FULFILLED', 'FULFILLED', 'INVOICED', 'CLOSED', 'CANCELLED']);
  });

  it('clears the loading flag and empties the list when the request fails', async () => {
    await setup();
    service.getSaleOrders.and.returnValue(throwError(() => ({ status: 500 })));
    component.load();

    expect(component.isLoading).toBeFalse();
    expect(component.orders).toEqual([]);
  });

  it('maps every status the server can return to a severity', () => {
    for (const status of ['DRAFT', 'CONFIRMED', 'PARTIALLY_FULFILLED', 'FULFILLED', 'INVOICED', 'CLOSED', 'CANCELLED'])
      expect(SALE_ORDER_STATUS_SEVERITY[status]).withContext(status).toBeDefined();
    expect(formatCode('PARTIALLY_FULFILLED')).toBe('Partially Fulfilled');
  });

  // ── New order ──────────────────────────────────────────────────────────────

  it('offers a new order only to someone who may create one', async () => {
    await setup();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="new-order"]')).toBeNull();

    permissions = ['SALE_ORDER_VIEW', 'SALE_ORDER_CREATE'];
    await setup();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="new-order"]')).not.toBeNull();
  });

  // ── Customers ──────────────────────────────────────────────────────────────

  it('names each customer once however many orders they have, and shows a dash until the name is known', async () => {
    await setup([order({ uuid: 'a', partnerId: 'p-1' }), order({ uuid: 'b', partnerId: 'p-1' }), order({ uuid: 'c', partnerId: 'p-2' })]);

    expect(component.customerName(component.orders[0] ?? order())).toBe('—');
    component.load();

    expect(partners.getPartnerById).toHaveBeenCalledTimes(2);
    expect(component.customerName(order({ partnerId: 'p-1' }))).toBe('Acme Ltd');
    expect(component.customerName(order({ partnerId: 'p-2' }))).toBe('Globex Corp');

    component.load();
    expect(partners.getPartnerById).withContext('already asked for').toHaveBeenCalledTimes(2);
  });

  it('shows a dash for a customer that cannot be found and does not ask again', async () => {
    await setup();
    partners.getPartnerById.and.returnValue(throwError(() => ({ status: 404 })));

    component.load();
    fixture.detectChanges();

    expect(component.customerName(order({ partnerId: 'p-1' }))).toBe('—');
    const asked = partners.getPartnerById.calls.count();
    component.load();
    expect(partners.getPartnerById.calls.count()).toBe(asked);
  });

  it('renders the customer beside the order', async () => {
    await setup();
    fixture.detectChanges();
    component.onPageChange({ first: 0, rows: 20 });
    fixture.detectChanges();

    const customers = Array.from(fixture.nativeElement.querySelectorAll('[data-testid="customer"]')).map((e: any) => e.textContent.trim());
    expect(customers).toEqual(['Acme Ltd', 'Globex Corp']);
  });

  // ── Edit ───────────────────────────────────────────────────────────────────

  it('lets a draft be edited by someone who may edit, and no other order', async () => {
    permissions = ['SALE_ORDER_VIEW', 'SALE_ORDER_EDIT'];
    await setup([order({ status: 'DRAFT' }), order({ uuid: 'so-2', status: 'CONFIRMED' })]);

    expect(component.canEdit(order({ status: 'DRAFT' }))).toBeTrue();
    expect(component.canEdit(order({ status: 'CONFIRMED' }))).toBeFalse();

    permissions = ['SALE_ORDER_VIEW'];
    expect(component.canEdit(order({ status: 'DRAFT' }))).withContext('viewing is not editing').toBeFalse();
  });
});
