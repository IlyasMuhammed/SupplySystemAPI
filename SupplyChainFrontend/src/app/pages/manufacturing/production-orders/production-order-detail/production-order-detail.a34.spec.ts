import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { ProductionOrderDetailComponent } from './production-order-detail.component';
import { ProductionOrderDetail, ProductionOrderService } from '../../../../services/production-order.service';
import { AllocationService } from '../../../../services/allocation.service';
import { DeliveryListItemModel, LogisticsService } from '../../../../services/logistics.service';
import { AuthService } from '../../../service/auth.service';

// A34 PE-06 — the production order detail for a make-to-order order: the sale order line and route, planned / accepted /
// yield, the "Auto-created delivery" card with its live status (GET api/logistics/deliveries?productionOrderUuid=), the
// shortfall note, and "Create delivery now" (DELIVERY_CREATE, D-20). Contract: docs/route-classification/API-CONTRACT.md §7, §8.3.

function ok<T>(result: T, message = '') {
  return of({ success: true, message, result } as any);
}

function po(overrides: Partial<ProductionOrderDetail> = {}): ProductionOrderDetail {
  return {
    uuid: 'po-1', traceId: 't-po', productionNumber: 'PROD-2026-00085', productUuid: 'p-1', productName: 'Custom Gear Assembly',
    productSku: 'CGA', productVariantUuid: 'v-1', variantName: 'Type A', bomUuid: 'b-1', bomNumber: 'BOM-1', bomVersion: 1,
    plannedQuantity: 50, producedQuantity: 50, acceptedQuantity: 48, rejectedQuantity: 2, scrappedQuantity: 0,
    warehouseUuid: 'wh-1', warehouseName: 'Plant', sourceType: 'SALES_ORDER', sourceUuid: 'so-1', sourceLineUuid: 'l-2',
    sourceReference: 'SO-2026-02001', priority: 2, requiredDate: '2026-10-16T00:00:00', status: 'COMPLETED',
    materialReadiness: 'READY', materialCount: 0, shortMaterialCount: 0, createdAt: '2026-10-03T00:00:00Z',
    updatedAt: '2026-10-16T00:00:00Z', createdBy: 7, materials: [], supplyRequirements: [], issues: [], childOrders: [],
    isMakeToOrder: true, fulfillmentRouteUuid: 'r-mfg', fulfillmentRouteCode: 'MFG_PICK_PACK_SHIP',
    fulfillmentRouteName: 'Manufacture → Pick, Pack & Ship', fulfillmentRouteCategory: 'MANUFACTURE',
    deliveryOrderUuid: 'd-5', deliveryNumber: 'DLV-2026-00005', deliveryCreationPending: false, saleOrderLineNumber: 2,
    shortfallQuantity: 2,
    ...overrides
  };
}

function delivery(overrides: Partial<DeliveryListItemModel> = {}): DeliveryListItemModel {
  return {
    uuid: 'd-5', deliveryNumber: 'DLV-2026-00005', direction: 'OUTBOUND', sourceType: 'SALE_ORDER', status: 'RELEASED',
    priority: 'NORMAL', lineCount: 1, linesUnknown: false, createdDate: '2026-10-16T00:00:00Z', productionOrderUuid: 'po-1',
    fulfillmentRouteName: 'Manufacture → Pick, Pack & Ship', lineSummary: 'Custom Gear Assembly × 48',
    ...overrides
  };
}

describe('ProductionOrderDetailComponent — A34 make to order', () => {
  let fixture: ComponentFixture<ProductionOrderDetailComponent>;
  let component: ProductionOrderDetailComponent;
  let service: jasmine.SpyObj<ProductionOrderService>;
  let logistics: jasmine.SpyObj<LogisticsService>;
  let toasts: jasmine.Spy;
  let permissions: string[];

  const auth = { hasPermission: (code: string) => permissions.includes(code) } as unknown as AuthService;

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  function text(testId: string): string {
    return (query(testId)?.textContent ?? '').replace(/\s+/g, ' ').trim();
  }

  async function setup(model: ProductionOrderDetail = po(), deliveries: DeliveryListItemModel[] = [delivery()]) {
    service = jasmine.createSpyObj<ProductionOrderService>('ProductionOrderService',
      ['getById', 'createDelivery', 'getInspectionForOrder', 'getFgrsForOrder', 'getLedgerForOrder']);
    service.getById.and.returnValue(ok(model));
    logistics = jasmine.createSpyObj<LogisticsService>('LogisticsService', ['getDeliveries']);
    logistics.getDeliveries.and.returnValue(ok({ data: deliveries, totalRecords: deliveries.length, page: 1, pageSize: 20, totalPages: 1 }));
    const allocation = jasmine.createSpyObj<AllocationService>('AllocationService', ['run']);

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [ProductionOrderDetailComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(),
        { provide: ProductionOrderService, useValue: service },
        { provide: LogisticsService, useValue: logistics },
        { provide: AllocationService, useValue: allocation },
        { provide: AuthService, useValue: auth },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['uuid', 'po-1']]) } } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(ProductionOrderDetailComponent);
    component = fixture.componentInstance;
    toasts = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
    fixture.detectChanges();
  }

  beforeEach(() => { permissions = ['PROD_VIEW', 'DELIVERY_VIEW', 'DELIVERY_CREATE', 'SALE_ORDER_VIEW']; });

  it('names the sale order line and the route it was made to order for', async () => {
    await setup();
    expect(text('source-line')).toContain('SO-2026-02001');
    expect(text('source-line')).toContain('line 2');
    expect(text('fulfillment-route')).toContain('Manufacture → Pick, Pack & Ship');
    expect(text('fulfillment-route')).toContain('Make to order');
  });

  it('sums up planned, accepted and the yield', async () => {
    await setup();
    expect(text('production-summary')).toContain('Planned: 50');
    expect(text('production-summary')).toContain('Accepted: 48');
    expect(text('production-summary')).toContain('Yield: 96%');
  });

  it('shows the auto-created delivery with its live status, quantity, the shortfall note and a View link', async () => {
    await setup();
    expect(logistics.getDeliveries).toHaveBeenCalledOnceWith(jasmine.objectContaining({ productionOrderUuid: 'po-1' }));

    const card = text('auto-delivery');
    expect(card).toContain('DLV-2026-00005');
    expect(card).toContain('Released');
    expect(card).toContain('Custom Gear Assembly × 48');
    expect(text('shortfall-note')).toBe('Shortfall of 2 units (planned 50, produced 48)');
    expect(query('view-delivery')).not.toBeNull();
  });

  it('says the delivery is pending until production completes', async () => {
    await setup(po({ status: 'IN_PROGRESS', acceptedQuantity: 0, deliveryOrderUuid: null, deliveryNumber: null, shortfallQuantity: null }), []);
    expect(text('auto-delivery')).toContain('Pending — the delivery is created when production completes');
    expect(query('create-delivery-now')).toBeNull();
  });

  it('flags a delivery that failed to be created, and offers "Create delivery now"', async () => {
    await setup(po({ deliveryOrderUuid: null, deliveryNumber: null, deliveryCreationPending: true }), []);
    expect(text('auto-delivery')).toContain('could not be created yet');
    expect(query('create-delivery-now')).not.toBeNull();
  });

  it('"Create delivery now" creates it, says so, and reads the order again', async () => {
    await setup(po({ status: 'QUALITY_INSPECTION', deliveryOrderUuid: null, deliveryNumber: null }), []);
    service.createDelivery.and.returnValue(ok({ productionOrderUuid: 'po-1', quantityCreated: 48, deliveryUuid: 'd-5', deliveryNumber: 'DLV-2026-00005' }));
    (query('create-delivery-now')!.querySelector('button') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(service.createDelivery).toHaveBeenCalledOnceWith('po-1');
    expect(toasts.calls.mostRecent().args[0].detail).toContain('DLV-2026-00005');
    expect(toasts.calls.mostRecent().args[0].detail).toContain('48');
    expect(service.getById).toHaveBeenCalledTimes(2);
  });

  it('passes on why nothing was created, and the server\'s refusal', async () => {
    await setup();
    service.createDelivery.and.returnValue(ok({ productionOrderUuid: 'po-1', quantityCreated: 0, skippedReason: 'Nothing left to deliver from PROD-2026-00085' }));
    component.createDeliveryNow();
    expect(toasts.calls.mostRecent().args[0].severity).toBe('info');
    expect(toasts.calls.mostRecent().args[0].detail).toContain('Nothing left to deliver');

    service.createDelivery.and.returnValue(throwError(() => ({ status: 400, error: { message: 'PROD-2026-00085 has no accepted quantity yet.' } })));
    component.createDeliveryNow();
    expect(toasts.calls.mostRecent().args[0].severity).toBe('error');
    expect(toasts.calls.mostRecent().args[0].detail).toContain('no accepted quantity');
  });

  it('gates "Create delivery now" by DELIVERY_CREATE, and reads no live status without DELIVERY_VIEW', async () => {
    permissions = ['PROD_VIEW'];
    await setup();
    expect(query('create-delivery-now')).toBeNull();
    expect(logistics.getDeliveries).not.toHaveBeenCalled();
    expect(text('auto-delivery')).toContain('DLV-2026-00005');
    expect(query('view-delivery')).toBeNull();
  });

  it('shows no delivery card for an order that is not made to order', async () => {
    await setup(po({ isMakeToOrder: false, fulfillmentRouteUuid: null, fulfillmentRouteName: null, sourceType: 'MANUAL',
                     sourceUuid: null, deliveryOrderUuid: null, deliveryNumber: null }), []);
    expect(query('auto-delivery')).toBeNull();
    expect(query('fulfillment-route')).toBeNull();
    expect(query('create-delivery-now')).toBeNull();
    expect(logistics.getDeliveries).not.toHaveBeenCalled();
  });
});
