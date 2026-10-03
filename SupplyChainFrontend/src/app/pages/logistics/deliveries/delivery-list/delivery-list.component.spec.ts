import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute, convertToParamMap } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { DeliveryListComponent, DELIVERY_STATUS_SEVERITY } from './delivery-list.component';
import { LogisticsService, DeliveryListItemModel } from '../../../../services/logistics.service';
import { FulfillmentRoutesService, FulfillmentRouteModel } from '../../../../services/fulfillment-routes.service';

function routeModel(code: string, name: string, overrides: Partial<FulfillmentRouteModel> = {}): FulfillmentRouteModel {
  return {
    uuid: `route-${code}`, code, name, isDefault: false, isActive: true, isSystem: true,
    requiresPacking: false, requiresShipping: false, displayOrder: 10, steps: [], stepsText: '',
    statusPath: [], createdDate: '2026-10-01T00:00:00Z', ...overrides
  };
}

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
  let routes: jasmine.SpyObj<FulfillmentRoutesService>;

  beforeEach(async () => {
    service = jasmine.createSpyObj<LogisticsService>('LogisticsService', ['getDeliveries']);
    service.getDeliveries.and.returnValue(page([row()]));
    routes = jasmine.createSpyObj<FulfillmentRoutesService>('FulfillmentRoutesService', ['getRoutes']);
    routes.getRoutes.and.returnValue(of({
      success: true, message: '',
      result: [routeModel('PICK_ONLY', 'Pick Only'), routeModel('PICK_AND_SHIP', 'Pick & Ship')]
    } as any));

    await TestBed.configureTestingModule({
      imports: [DeliveryListComponent],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        provideNoopAnimations(),
        MessageService,
        { provide: LogisticsService, useValue: service },
        { provide: FulfillmentRoutesService, useValue: routes }
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

  // ── A33-PD-07 — source, sale order and route ───────────────────────────────

  describe('A33 source, sale order and route', () => {
    it('filters by the server\'s own source codes, sale orders included', () => {
      // The codes are DeliverySourceType's (PO, SRO, MIV, TRANSFER, MANUAL, SALE_ORDER): a label
      // sent as the code would filter for nothing and look like "no deliveries".
      expect(component.sourceTypeOptions.map(o => o.value))
        .toEqual(['', 'PO', 'SRO', 'MIV', 'TRANSFER', 'MANUAL', 'SALE_ORDER']);
      expect(component.sourceTypeOptions.find(o => o.value === 'SALE_ORDER')!.label).toBe('Sale Order');

      fixture.detectChanges();
      component.selectedSourceType = 'SALE_ORDER';
      component.onFilterChange();

      expect(service.getDeliveries.calls.mostRecent().args[0]!.sourceType).toBe('SALE_ORDER');
    });

    it('links a sale-order delivery to its order, showing the order number and customer', () => {
      service.getDeliveries.and.returnValue(page([row({
        sourceType: 'SALE_ORDER', sourceNumber: 'SO-2026-00085', saleOrderUuid: 'so-85', customerName: 'Punjab Group'
      })]));

      fixture.detectChanges();

      const link: HTMLAnchorElement = fixture.nativeElement.querySelector('[data-testid="sale-order-link"]');
      expect(link).withContext('the Sale Order column links to the order').not.toBeNull();
      expect(link.textContent!.trim()).toBe('SO-2026-00085');
      expect(link.getAttribute('href')).toBe('/portal/pages/sales/orders/so-85');
      expect(fixture.nativeElement.querySelector('[data-testid="sale-order-customer"]').textContent)
        .toContain('Punjab Group');
    });

    it('shows a dash in the Sale Order column for any other delivery', () => {
      service.getDeliveries.and.returnValue(page([row({ sourceType: 'PO', sourceNumber: 'PO-1' })]));

      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('[data-testid="sale-order-link"]')).toBeNull();
      expect(fixture.nativeElement.querySelector('[data-testid="sale-order-none"]').textContent.trim()).toBe('—');
    });

    it('badges the route code, named in full on hover', () => {
      service.getDeliveries.and.returnValue(page([row({
        sourceType: 'SALE_ORDER', saleOrderUuid: 'so-1', fulfillmentRouteUuid: 'route-PICK_ONLY',
        fulfillmentRouteCode: 'PICK_ONLY', fulfillmentRouteName: 'Pick Only'
      })]));

      fixture.detectChanges();

      const badge = fixture.nativeElement.querySelector('[data-testid="route-badge"]');
      expect(badge).not.toBeNull();
      expect(badge.textContent.trim()).toBe('PICK_ONLY');
      expect(component.routeTooltip(component.deliveries[0])).toBe('Pick Only');
    });

    it('shows a dash for a delivery that has no route (raised before routes, or not for a sale order)', () => {
      service.getDeliveries.and.returnValue(page([row()]));

      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('[data-testid="route-badge"]')).toBeNull();
      expect(fixture.nativeElement.querySelector('[data-testid="route-none"]').textContent.trim()).toBe('—');
    });

    it('spans every column when the grid is empty', () => {
      service.getDeliveries.and.returnValue(page([]));

      fixture.detectChanges();

      const headers = fixture.nativeElement.querySelectorAll('thead th').length;
      const empty = fixture.nativeElement.querySelector('[data-testid="empty-row"]');
      expect(Number(empty.getAttribute('colspan'))).toBe(headers);
    });

    it('offers every route, inactive ones too, as a filter and sends the chosen one', () => {
      fixture.detectChanges();

      // Inactive too: a delivery keeps the route it was raised on after the route is retired.
      expect(routes.getRoutes).toHaveBeenCalledOnceWith(true);
      expect(component.routeOptions.map(o => o.value)).toEqual(['', 'route-PICK_ONLY', 'route-PICK_AND_SHIP']);
      expect(component.routeOptions[1].label).toBe('PICK_ONLY — Pick Only');

      component.selectedRouteUuid = 'route-PICK_AND_SHIP';
      component.onFilterChange();

      const filter = service.getDeliveries.calls.mostRecent().args[0]!;
      expect(filter.fulfillmentRouteUuid).toBe('route-PICK_AND_SHIP');
      expect(filter.page).toBe(1);
    });

    it('sends no route filter until one is chosen, and clears it on reset', () => {
      fixture.detectChanges();
      expect(service.getDeliveries.calls.mostRecent().args[0]!.fulfillmentRouteUuid).toBeUndefined();

      component.selectedRouteUuid = 'route-PICK_ONLY';
      component.resetFilters();

      expect(component.selectedRouteUuid).toBe('');
      expect(service.getDeliveries.calls.mostRecent().args[0]!.fulfillmentRouteUuid).toBeUndefined();
    });

    it('hides the route filter, rather than failing the screen, when routes cannot be read', () => {
      routes.getRoutes.and.returnValue(throwError(() => ({ status: 403 })));

      fixture.detectChanges();

      expect(component.routeOptions.length).toBe(0);
      expect(fixture.nativeElement.querySelector('[data-testid="route-filter"]')).toBeNull();
      expect(component.deliveries.length).withContext('the deliveries still load').toBe(1);
    });
  });

  describe('A33 deliveries of one sale order (?saleOrderUuid=)', () => {
    async function build(query: Record<string, string>) {
      TestBed.resetTestingModule();
      await TestBed.configureTestingModule({
        imports: [DeliveryListComponent],
        providers: [
          provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), MessageService,
          { provide: LogisticsService, useValue: service },
          { provide: FulfillmentRoutesService, useValue: routes },
          { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(query) } } }
        ]
      }).compileComponents();
      fixture = TestBed.createComponent(DeliveryListComponent);
      component = fixture.componentInstance;
    }

    it('narrows to the order named in the link and says so', async () => {
      await build({ saleOrderUuid: 'so-85', saleOrderNumber: 'SO-2026-00085' });
      fixture.detectChanges();

      expect(service.getDeliveries.calls.mostRecent().args[0]!.saleOrderUuid).toBe('so-85');
      const chip = fixture.nativeElement.querySelector('[data-testid="sale-order-filter"]');
      expect(chip).not.toBeNull();
      expect(chip.textContent).toContain('SO-2026-00085');
    });

    it('drops the order filter when it is cleared, and on reset', async () => {
      await build({ saleOrderUuid: 'so-85' });
      fixture.detectChanges();

      component.clearSaleOrderFilter();

      expect(component.saleOrderUuid).toBe('');
      expect(service.getDeliveries.calls.mostRecent().args[0]!.saleOrderUuid).toBeUndefined();

      await build({ saleOrderUuid: 'so-85' });
      fixture.detectChanges();
      component.resetFilters();
      expect(service.getDeliveries.calls.mostRecent().args[0]!.saleOrderUuid).toBeUndefined();
    });

    it('sends no order filter when the link names none', async () => {
      await build({});
      fixture.detectChanges();

      expect(service.getDeliveries.calls.mostRecent().args[0]!.saleOrderUuid).toBeUndefined();
      expect(fixture.nativeElement.querySelector('[data-testid="sale-order-filter"]')).toBeNull();
    });
  });
});
