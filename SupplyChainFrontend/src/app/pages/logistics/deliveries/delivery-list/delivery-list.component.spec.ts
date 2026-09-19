import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { DeliveryListComponent, DELIVERY_STATUS_SEVERITY } from './delivery-list.component';
import { LogisticsService, DeliveryListItemModel } from '../../../../services/logistics.service';

function row(overrides: Partial<DeliveryListItemModel> = {}): DeliveryListItemModel {
  return {
    uuid: 'd1',
    deliveryNumber: 'DLV-2026-00001',
    direction: 'OUTBOUND',
    sourceType: 'MANUAL',
    status: 'DRAFT',
    priority: 'NORMAL',
    lineCount: 3,
    linesUnknown: false,
    createdDate: '2026-09-01T00:00:00Z',
    ...overrides
  };
}

function page(data: DeliveryListItemModel[], totalRecords = data.length) {
  return of({
    success: true,
    message: '',
    result: { data, totalRecords, page: 1, pageSize: 20, totalPages: 1 }
  } as any);
}

describe('DeliveryListComponent', () => {
  let fixture: ComponentFixture<DeliveryListComponent>;
  let component: DeliveryListComponent;
  let service: jasmine.SpyObj<LogisticsService>;

  beforeEach(async () => {
    service = jasmine.createSpyObj<LogisticsService>('LogisticsService', ['getDeliveries']);
    service.getDeliveries.and.returnValue(page([row()]));

    await TestBed.configureTestingModule({
      imports: [DeliveryListComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        provideNoopAnimations(),
        MessageService,
        { provide: LogisticsService, useValue: service }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(DeliveryListComponent);
    component = fixture.componentInstance;
  });

  // ── TC-19.1 ────────────────────────────────────────────────────────────────

  it('loads once on init and clears the loading flag', () => {
    expect(component.isLoading).withContext('starts loading before any response').toBeTrue();

    fixture.detectChanges();

    expect(service.getDeliveries).toHaveBeenCalledTimes(1);
    expect(component.deliveries.length).toBe(1);
    expect(component.isLoading).toBeFalse();
  });

  it('asks for the first page of twenty and no filters', () => {
    fixture.detectChanges();

    const filter = service.getDeliveries.calls.mostRecent().args[0]!;
    expect(filter.page).toBe(1);
    expect(filter.pageSize).toBe(20);
    expect(filter.status).toBeUndefined();
    expect(filter.direction).toBeUndefined();
    expect(filter.sourceType).toBeUndefined();
    expect(filter.search).toBeUndefined();
  });

  // ── TC-19.2 ────────────────────────────────────────────────────────────────

  it('debounces typing into a single request', fakeAsync(() => {
    fixture.detectChanges();
    service.getDeliveries.calls.reset();

    component.searchText = 'D';   component.onSearchChange();
    component.searchText = 'DL';  component.onSearchChange();
    component.searchText = 'DLV'; component.onSearchChange();

    tick(399);
    expect(service.getDeliveries).withContext('nothing before the debounce elapses').not.toHaveBeenCalled();

    tick(1);
    expect(service.getDeliveries).toHaveBeenCalledTimes(1);
    expect(service.getDeliveries.calls.mostRecent().args[0]!.search).toBe('DLV');
  }));

  it('returns to the first page when a search changes', fakeAsync(() => {
    fixture.detectChanges();
    component.currentPage = 5;

    component.searchText = 'DLV';
    component.onSearchChange();
    tick(400);

    // Staying on page 5 of a narrower result set shows an empty table and looks like "no
    // matches" when there are plenty.
    expect(component.currentPage).toBe(1);
    expect(service.getDeliveries.calls.mostRecent().args[0]!.page).toBe(1);
  }));

  // ── TC-19.3 ────────────────────────────────────────────────────────────────

  it('returns to the first page when a filter changes', () => {
    fixture.detectChanges();
    component.currentPage = 4;

    component.selectedStatus = 'RELEASED';
    component.onFilterChange();

    expect(component.currentPage).toBe(1);
    const filter = service.getDeliveries.calls.mostRecent().args[0]!;
    expect(filter.status).toBe('RELEASED');
    expect(filter.page).toBe(1);
  });

  it('sends every filter that is set', () => {
    fixture.detectChanges();

    component.selectedStatus     = 'IN_TRANSIT';
    component.selectedDirection  = 'INBOUND';
    component.selectedSourceType = 'PO';
    component.onFilterChange();

    const filter = service.getDeliveries.calls.mostRecent().args[0]!;
    expect(filter.status).toBe('IN_TRANSIT');
    expect(filter.direction).toBe('INBOUND');
    expect(filter.sourceType).toBe('PO');
  });

  it('clears every filter on reset', () => {
    fixture.detectChanges();
    component.searchText = 'x';
    component.selectedStatus = 'DRAFT';
    component.selectedDirection = 'INBOUND';
    component.selectedSourceType = 'PO';

    component.resetFilters();

    const filter = service.getDeliveries.calls.mostRecent().args[0]!;
    expect(filter.status).toBeUndefined();
    expect(filter.direction).toBeUndefined();
    expect(filter.sourceType).toBeUndefined();
    expect(filter.search).toBeUndefined();
    expect(component.currentPage).toBe(1);
  });

  // ── TC-19.4 ────────────────────────────────────────────────────────────────

  it('converts the table row offset into a page number', () => {
    fixture.detectChanges();

    component.onPageChange({ first: 40, rows: 20 });

    const filter = service.getDeliveries.calls.mostRecent().args[0]!;
    expect(filter.page).withContext('rows 40-59 is the third page of twenty').toBe(3);
    expect(filter.pageSize).toBe(20);
  });

  it('honours a change of page size', () => {
    fixture.detectChanges();

    component.onPageChange({ first: 0, rows: 50 });

    expect(service.getDeliveries.calls.mostRecent().args[0]!.pageSize).toBe(50);
  });

  // ── TC-19.5 ────────────────────────────────────────────────────────────────

  it('has a severity for every status the server can send', () => {
    // Mirrors DeliveryStatus in the backend. An unmapped status falls back to plain grey, which
    // reads as "nothing notable" — the wrong signal for CANCELLED or SHORT_CLOSED.
    const serverStatuses = [
      'DRAFT', 'RELEASED', 'PICKING', 'PICKED', 'PACKED', 'STAGED', 'PENDING_APPROVAL',
      'GOODS_ISSUED', 'IN_TRANSIT', 'DELIVERED', 'CLOSED', 'ON_HOLD', 'PARTIALLY_DELIVERED',
      'SHORT_CLOSED', 'CANCELLED'
    ];

    for (const status of serverStatuses) {
      expect(DELIVERY_STATUS_SEVERITY[status])
        .withContext(`${status} needs an explicit severity`).toBeDefined();
    }

    expect(Object.keys(DELIVERY_STATUS_SEVERITY).sort()).toEqual(serverStatuses.sort());
  });

  it('flags the states that need attention rather than colouring everything alike', () => {
    expect(component.getStatusSeverity('CANCELLED')).toBe('danger');
    expect(component.getStatusSeverity('SHORT_CLOSED')).toBe('danger');
    expect(component.getStatusSeverity('ON_HOLD')).toBe('warn');
    expect(component.getStatusSeverity('DELIVERED')).toBe('success');
  });

  it('falls back to neutral rather than crashing on an unknown status', () => {
    expect(component.getStatusSeverity('SOMETHING_NEW')).toBe('secondary');
  });

  it('renders status codes as words', () => {
    expect(component.formatStatus('GOODS_ISSUED')).toBe('Goods Issued');
    expect(component.formatStatus('PARTIALLY_DELIVERED')).toBe('Partially Delivered');
    expect(component.formatStatus('')).toBe('');
  });

  // ── TC-19.6 ────────────────────────────────────────────────────────────────

  it('marks a backfilled delivery whose lines were never recorded', () => {
    service.getDeliveries.and.returnValue(page([row({ linesUnknown: true, lineCount: 0 })]));

    fixture.detectChanges();

    const marker = fixture.nativeElement.querySelector('[data-testid="lines-unknown"]');
    expect(marker).withContext('a header-only delivery must not look complete').not.toBeNull();
    expect(marker.textContent).toContain('Unknown');
  });

  it('shows the line count for a normal delivery', () => {
    service.getDeliveries.and.returnValue(page([row({ lineCount: 4 })]));

    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="lines-unknown"]')).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('4');
  });

  // ── TC-19.7 ────────────────────────────────────────────────────────────────

  it('reports a failed load and stops the spinner', () => {
    // The component declares MessageService in its own providers, so it has an instance of its
    // own — spying on the root one would watch something the component never touches.
    const messages = fixture.debugElement.injector.get(MessageService);
    spyOn(messages, 'add');
    service.getDeliveries.and.returnValue(throwError(() => new Error('network down')));

    fixture.detectChanges();

    expect(messages.add).toHaveBeenCalled();
    // Leaving isLoading set would spin for ever, which reads as "still working".
    expect(component.isLoading).toBeFalse();
    expect(component.deliveries).toEqual([]);
    expect(component.totalRecords).toBe(0);
  });

  it('empties the grid when a response comes back unsuccessful', () => {
    service.getDeliveries.and.returnValue(of({ success: false, message: 'nope', result: null } as any));

    fixture.detectChanges();

    expect(component.deliveries).toEqual([]);
    expect(component.isLoading).toBeFalse();
  });

  // ── Teardown ───────────────────────────────────────────────────────────────

  it('does not fire a pending search after the screen is destroyed', fakeAsync(() => {
    fixture.detectChanges();
    service.getDeliveries.calls.reset();

    component.searchText = 'DLV';
    component.onSearchChange();
    fixture.destroy();

    tick(400);

    expect(service.getDeliveries).not.toHaveBeenCalled();
  }));
});
