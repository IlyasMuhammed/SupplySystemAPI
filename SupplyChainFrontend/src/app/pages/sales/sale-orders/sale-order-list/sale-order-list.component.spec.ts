import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { SaleOrderListComponent, SALE_ORDER_STATUS_SEVERITY, formatCode } from './sale-order-list.component';
import { SaleOrderService, SaleOrderModel } from '../../../../services/sale-order.service';

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

  beforeEach(async () => {
    service = jasmine.createSpyObj<SaleOrderService>('SaleOrderService', ['getSaleOrders']);
    service.getSaleOrders.and.returnValue(of({
      success: true, message: '',
      result: { data: [order(), order({ uuid: 'so-2', soNumber: 'SO-2026-00043', deliveryMode: 'SELF_PICKUP', status: 'FULFILLED' })],
                totalRecords: 2, page: 1, pageSize: 20, totalPages: 1 }
    } as any));

    await TestBed.configureTestingModule({
      imports: [SaleOrderListComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(),
        MessageService, { provide: SaleOrderService, useValue: service }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(SaleOrderListComponent);
    component = fixture.componentInstance;
  });

  it('loads page one through the table and renders the orders', () => {
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

  it('sends only the filters that are set', () => {
    component.selectedStatus = 'FULFILLED';
    component.searchText = '';
    component.load();

    const filter = service.getSaleOrders.calls.mostRecent().args[0]!;
    expect(filter.status).toBe('FULFILLED');
    expect(filter.search).toBeUndefined();
  });

  it('clears the loading flag and empties the list when the request fails', () => {
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
});
