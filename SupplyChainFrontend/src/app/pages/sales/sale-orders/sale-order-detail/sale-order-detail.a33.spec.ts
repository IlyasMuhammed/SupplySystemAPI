import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { Tooltip } from 'primeng/tooltip';
import { of, throwError } from 'rxjs';

import { SaleOrderDetailComponent } from './sale-order-detail.component';
import {
  SaleOrderService, SaleOrderModel, SaleOrderLineModel, SaleOrderDeliveryPreviewModel,
  SaleOrderConfirmResultModel, SaleOrderCancelResultModel
} from '../../../../services/sale-order.service';
import { FulfillmentRoutesService, FulfillmentRouteModel } from '../../../../services/fulfillment-routes.service';
import { LogisticsService, DeliveryListItemModel, DeliveryDetailModel, DeliveryLineModel } from '../../../../services/logistics.service';
import { BusinessPartnerService } from '../../../../services/business-partner.service';
import { SalesInvoiceService } from '../../../../services/sales-invoice.service';
import { AddressService } from '../../../../services/address.service';
import { TimelineService } from '../../../../services/timeline.service';
import { ProductionOrderService } from '../../../../services/production-order.service';
import { AttachmentService } from '../../../../services/attachment.service';
import { AttachmentPolicyService } from '../../../../services/attachment-policy.service';
import { AuthService } from '../../../service/auth.service';

// A33 — sale order detail: Route column with its source (PC-07), the GET delivery preview on a draft (PC-08), the
// Confirm gate (PC-09) and the Deliveries tab as the fulfillment view (PD-06): route per delivery, expandable lines,
// "N of M lines delivered", the recovery "Create deliveries" button and the D-15 cancel result.
// Contract: docs/fulfillment-routes/API-CONTRACT.md §5–§6.

const UUID = '11111111-1111-1111-1111-111111111111';

const ALL = ['SALE_ORDER_VIEW', 'SALE_ORDER_EDIT', 'SALE_ORDER_CONFIRM', 'SALE_ORDER_CANCEL', 'DELIVERY_VIEW', 'DELIVERY_CREATE'];

function line(overrides: Partial<SaleOrderLineModel> = {}): SaleOrderLineModel {
  return {
    uuid: 'l1', variantUuid: 'v1', itemDescription: 'Steel Pipes', variantSku: 'SP-1', unitOfMeasure: 'M',
    quantity: 500, unitPrice: 450, discountPercent: 0, taxPercent: 17, taxCodeUuid: 'tc-gst', taxCode: 'GST17',
    lineTotal: 263250, fulfilledQty: 0, invoicedQty: 0, status: 'OPEN',
    fulfillmentRouteUuid: null, effectiveRouteUuid: 'r-po', effectiveRouteCode: 'PICK_ONLY', effectiveRouteName: 'Pick Only',
    effectiveRouteSteps: ['PICK', 'GOODS_ISSUE'], routeSource: 'VARIANT', routeBlocker: null,
    ...overrides
  };
}

const COPPER = line({
  uuid: 'l2', variantUuid: 'v2', itemDescription: 'Copper Wire', quantity: 600, unitPrice: 820, discountPercent: 5,
  taxCodeUuid: null, taxCode: null, taxPercent: 0,
  fulfillmentRouteUuid: 'r-pps', effectiveRouteUuid: 'r-pps', effectiveRouteCode: 'PICK_PACK_SHIP',
  effectiveRouteName: 'Pick, Pack & Ship', routeSource: 'LINE_OVERRIDE'
});

const GASKET = line({
  uuid: 'l4', variantUuid: 'v4', itemDescription: 'Custom Gasket', quantity: 50, effectiveRouteUuid: null,
  effectiveRouteCode: null, effectiveRouteName: null, effectiveRouteSteps: [], routeSource: 'NONE', routeBlocker: 'ROUTE_MISSING'
});

function order(overrides: Partial<SaleOrderModel> = {}): SaleOrderModel {
  return {
    uuid: UUID, traceId: 't-1', soNumber: 'SO-2026-01085', partnerId: 'p-1',
    orderDate: '2026-10-01T00:00:00Z', expectedDeliveryDate: '2026-10-20T00:00:00Z', currencyId: 'c-1',
    subtotal: 0, taxAmount: 0, discountAmount: 0, grandTotal: 0,
    status: 'DRAFT', requiresShipment: true, deliveryMode: 'SHIP', shippingAddressId: 'addr-1',
    intimationDepartmentId: 7, notes: 'Gate 2', customerPoReference: 'PB-77', customerPoDate: '2026-09-30T00:00:00Z',
    createdDate: '2026-10-01T00:00:00Z', sourceType: 'MANUAL',
    lines: [line(), COPPER], routesEnabled: true, confirmBlockers: [],
    ...overrides
  };
}

function route(uuid: string, code: string, name: string): FulfillmentRouteModel {
  return {
    uuid, code, name, isDefault: false, isActive: true, isSystem: true, requiresPacking: false, requiresShipping: false,
    displayOrder: 10, steps: [], stepsText: '', statusPath: [], createdDate: '2026-10-03T00:00:00Z'
  };
}

const ROUTES = [route('r-po', 'PICK_ONLY', 'Pick Only'), route('r-ps', 'PICK_AND_SHIP', 'Pick & Ship'), route('r-pps', 'PICK_PACK_SHIP', 'Pick, Pack & Ship')];

function preview(overrides: Partial<SaleOrderDeliveryPreviewModel> = {}): SaleOrderDeliveryPreviewModel {
  return {
    routesEnabled: true, canConfirm: true, deliveryCount: 2,
    lines: [
      { lineUuid: 'l1', lineNumber: 1, variantUuid: 'v1', itemDescription: 'Steel Pipes', quantity: 500, effectiveRouteUuid: 'r-po',
        effectiveRouteName: 'Pick Only', effectiveRouteSteps: ['PICK', 'GOODS_ISSUE'], routeSource: 'VARIANT' },
      { lineUuid: 'l2', lineNumber: 2, variantUuid: 'v2', itemDescription: 'Copper Wire', quantity: 600, effectiveRouteUuid: 'r-pps',
        effectiveRouteName: 'Pick, Pack & Ship', effectiveRouteSteps: ['PICK', 'PACK', 'GOODS_ISSUE', 'SHIP'], routeSource: 'LINE_OVERRIDE' }
    ],
    groups: [
      { routeUuid: 'r-po', routeCode: 'PICK_ONLY', routeName: 'Pick Only', steps: ['PICK', 'GOODS_ISSUE'], stepsText: 'Pick → Goods Issue',
        requiresShipping: false, deliveryMode: 'SELF_PICKUP', lineNumbers: [1] },
      { routeUuid: 'r-pps', routeCode: 'PICK_PACK_SHIP', routeName: 'Pick, Pack & Ship', steps: ['PICK', 'PACK', 'GOODS_ISSUE', 'SHIP'],
        stepsText: 'Pick → Pack → Goods Issue → Ship', requiresShipping: true, deliveryMode: 'SHIP', lineNumbers: [2] }
    ],
    blockers: [],
    ...overrides
  };
}

function delivery(overrides: Partial<DeliveryListItemModel> = {}): DeliveryListItemModel {
  return {
    uuid: 'd1', deliveryNumber: 'DLV-2026-00001', direction: 'OUTBOUND', sourceType: 'SALE_ORDER', sourceNumber: 'SO-2026-01085',
    deliveryMode: 'SELF_PICKUP', status: 'PICKED', priority: 'NORMAL', lineCount: 1, linesUnknown: false,
    createdDate: '2026-10-03T00:00:00Z', saleOrderUuid: UUID, fulfillmentRouteUuid: 'r-po', fulfillmentRouteCode: 'PICK_ONLY',
    fulfillmentRouteName: 'Pick Only', lineSummary: 'Steel Pipes × 500',
    ...overrides
  };
}

function deliveryLine(overrides: Partial<DeliveryLineModel> = {}): DeliveryLineModel {
  return {
    uuid: 'dl1', lineNo: 1, itemDescription: 'Steel Pipes', unitOfMeasure: 'M', qtyOrdered: 500, qtyPicked: 500, qtyPacked: 0,
    qtyShipped: 0, qtyDelivered: 0, qtyShort: 0, soLineUuid: 'l1', isHazardous: false, isFragile: false, isTemperatureControlled: false,
    ...overrides
  };
}

function ok<T>(result: T, message = '') {
  return of({ success: true, message, result } as any);
}

describe('SaleOrderDetailComponent — A33 routes, confirm gate and fulfillment', () => {
  let fixture: ComponentFixture<SaleOrderDetailComponent>;
  let component: SaleOrderDetailComponent;
  let service: jasmine.SpyObj<SaleOrderService>;
  let routes: jasmine.SpyObj<FulfillmentRoutesService>;
  let logistics: jasmine.SpyObj<LogisticsService>;
  let toasts: jasmine.Spy;
  let permissions: string[];

  const auth = { hasPermission: (code: string) => permissions.includes(code) } as unknown as AuthService;

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  function queryAll(testId: string): HTMLElement[] {
    return Array.from(fixture.nativeElement.querySelectorAll(`[data-testid="${testId}"]`));
  }

  function inBody(testId: string): HTMLElement[] {
    return Array.from(document.body.querySelectorAll(`[data-testid="${testId}"]`));
  }

  function tooltipOf(testId: string): string {
    const el = fixture.debugElement.query(By.css(`[data-testid="${testId}"]`));
    return String(el.injector.get(Tooltip).content ?? '');
  }

  function lastToast() {
    return toasts.calls.mostRecent().args[0] as { severity: string; summary: string; detail: string };
  }

  async function setup(model: SaleOrderModel = order(), deliveries: DeliveryListItemModel[] = []) {
    service = jasmine.createSpyObj<SaleOrderService>('SaleOrderService', [
      'getSaleOrderById', 'getDeliveries', 'createDelivery', 'confirmSaleOrder', 'cancelSaleOrder', 'getAvailability',
      'updateCustomerPo', 'checkCustomerPo', 'reserveLine', 'releaseLine', 'reserveAll',
      'updateSaleOrder', 'getDeliveryPreview', 'createDeliveries', 'setLineFulfillmentRoute'
    ]);
    service.setLineFulfillmentRoute.and.returnValue(ok(null));
    service.getSaleOrderById.and.returnValue(ok(model));
    service.getDeliveries.and.returnValue(ok(deliveries));
    service.getAvailability.and.returnValue(ok([]));
    service.checkCustomerPo.and.returnValue(ok([]));
    service.getDeliveryPreview.and.returnValue(ok(preview()));
    service.updateSaleOrder.and.returnValue(ok(null));
    service.confirmSaleOrder.and.returnValue(ok(null));
    service.cancelSaleOrder.and.returnValue(ok(null));
    service.createDeliveries.and.returnValue(ok({ created: [], skipped: [] }));

    routes = jasmine.createSpyObj<FulfillmentRoutesService>('FulfillmentRoutesService', ['getRoutes']);
    routes.getRoutes.and.returnValue(ok(ROUTES));
    logistics = jasmine.createSpyObj<LogisticsService>('LogisticsService', ['getDeliveryById']);
    logistics.getDeliveryById.and.returnValue(ok({ uuid: 'd1', lines: [deliveryLine()] } as DeliveryDetailModel));

    const partners = jasmine.createSpyObj<BusinessPartnerService>('BusinessPartnerService', ['getPartnerById']);
    partners.getPartnerById.and.returnValue(ok({ uuid: 'p-1', companyName: 'Punjab Group' }));
    const invoices = jasmine.createSpyObj<SalesInvoiceService>('SalesInvoiceService', ['getInvoices', 'getInvoice', 'downloadPdf']);
    invoices.getInvoices.and.returnValue(ok({ data: [], totalRecords: 0, page: 1, pageSize: 100, totalPages: 0 }));
    const addresses = jasmine.createSpyObj<AddressService>('AddressService', ['getAddress']);
    addresses.getAddress.and.returnValue(ok(null));
    const timeline = jasmine.createSpyObj<TimelineService>('TimelineService', ['getByTraceId', 'getByDocument']);
    timeline.getByTraceId.and.returnValue(ok(null));
    const production = jasmine.createSpyObj<ProductionOrderService>('ProductionOrderService', ['getList']);
    production.getList.and.returnValue(ok({ data: [], totalRecords: 0, page: 1, pageSize: 100, totalPages: 0 }));
    const attachments = jasmine.createSpyObj<AttachmentService>('AttachmentService',
      ['getAttachments', 'upload', 'deleteAttachment', 'resolveUrl', 'isApiUrl', 'download']);
    attachments.getAttachments.and.returnValue(ok([]));
    attachments.isApiUrl.and.returnValue(false);
    const policy = jasmine.createSpyObj<AttachmentPolicyService>('AttachmentPolicyService', ['ruleFor']);
    policy.ruleFor.and.returnValue(of(null));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [SaleOrderDetailComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), MessageService,
        { provide: SaleOrderService, useValue: service },
        { provide: FulfillmentRoutesService, useValue: routes },
        { provide: LogisticsService, useValue: logistics },
        { provide: BusinessPartnerService, useValue: partners },
        { provide: SalesInvoiceService, useValue: invoices },
        { provide: AddressService, useValue: addresses },
        { provide: TimelineService, useValue: timeline },
        { provide: ProductionOrderService, useValue: production },
        { provide: AttachmentService, useValue: attachments },
        { provide: AttachmentPolicyService, useValue: policy },
        { provide: AuthService, useValue: auth },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['uuid', UUID]]) } } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(SaleOrderDetailComponent);
    component = fixture.componentInstance;
    toasts = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
    fixture.detectChanges();
  }

  beforeEach(() => { permissions = [...ALL]; });

  // ── PC-07: the Route column ────────────────────────────────────────────────

  it('has no Route column, asks for no routes and no preview when the organization has no Logistics (D-11)', async () => {
    await setup(order({ routesEnabled: false }));

    expect(query('route-header')).toBeNull();
    expect(query('line-route')).toBeNull();
    expect(query('delivery-preview')).toBeNull();
    expect(routes.getRoutes).not.toHaveBeenCalled();
    expect(service.getDeliveryPreview).not.toHaveBeenCalled();
  });

  it('shows each confirmed line its route and where it came from, read-only', async () => {
    await setup(order({
      status: 'CONFIRMED',
      lines: [line(), COPPER, line({ uuid: 'l3', routeSource: 'ORG_DEFAULT', effectiveRouteName: 'Pick & Ship' })]
    }));

    expect(query('route-header')).not.toBeNull();
    expect(queryAll('line-route-name').map(e => e.textContent!.trim())).toEqual(['Pick Only', 'Pick, Pack & Ship', 'Pick & Ship']);
    expect(queryAll('line-route-source').map(e => e.textContent!.trim())).toEqual(['ⓥ', '✎', '⊙']);
    expect(queryAll('line-route-source').map(e => e.getAttribute('data-source'))).toEqual(['VARIANT', 'LINE_OVERRIDE', 'ORG_DEFAULT']);
    expect(query('line-route-select')).withContext('locked after confirm').toBeNull();
    expect(routes.getRoutes).not.toHaveBeenCalled();
    expect(query('route-legend')!.textContent).toContain('ⓥ');
  });

  it('shows no route for a drop-shipped line, which needs none (D-5)', async () => {
    await setup(order({ status: 'CONFIRMED', lines: [line({
      fulfillmentMode: 'DROP_SHIP', routeSource: 'NONE', routeBlocker: null, effectiveRouteName: null, effectiveRouteUuid: null
    })] }));

    expect(query('line-route-name')).toBeNull();
    expect(query('line-route-source')).toBeNull();
    expect(query('line-route-hint')!.textContent).toContain('Drop');
  });

  it('offers a route dropdown on each line of a draft to someone who may edit it', async () => {
    await setup();

    expect(routes.getRoutes).toHaveBeenCalledTimes(1);
    expect(queryAll('line-route-select').length).toBe(2);
    expect(component.routeOptionsFor(line()).map(o => o.label)).toEqual(['Pick Only', 'Pick & Ship', 'Pick, Pack & Ship']);
    expect(component.inheritedRouteLabel(line())).toContain('Pick Only');
  });

  it('shows the route read-only on a draft to someone who may not edit it (SALE_ORDER_EDIT)', async () => {
    permissions = ['SALE_ORDER_VIEW', 'SALE_ORDER_CONFIRM'];
    await setup();

    expect(component.canChangeRoute).toBeFalse();
    expect(query('line-route-select')).toBeNull();
    expect(queryAll('line-route-name').map(e => e.textContent!.trim())).toEqual(['Pick Only', 'Pick, Pack & Ship']);
  });

  // A route change goes through the route-only line endpoint. The full-draft PUT rebuilds and re-prices every
  // line, so a route change must never use it.
  it('changes one line\'s route through the route-only endpoint, never the full-draft PUT, then re-reads it', async () => {
    await setup();
    const reads = service.getSaleOrderById.calls.count();
    const previews = service.getDeliveryPreview.calls.count();

    component.changeLineRoute(line(), 'r-ps');

    expect(service.setLineFulfillmentRoute).toHaveBeenCalledOnceWith(UUID, 'l1', 'r-ps');
    expect(service.updateSaleOrder).withContext('the full-draft PUT re-prices the lines').not.toHaveBeenCalled();
    expect(service.getSaleOrderById.calls.count()).toBe(reads + 1);
    expect(service.getDeliveryPreview.calls.count()).withContext('the preview follows the change').toBe(previews + 1);
    expect(lastToast().severity).toBe('success');
  });

  it('clears an override back to inherit by sending a null route for that line only', async () => {
    await setup();

    component.changeLineRoute(COPPER, null);

    expect(service.setLineFulfillmentRoute).toHaveBeenCalledOnceWith(UUID, 'l2', null);
    expect(service.updateSaleOrder).not.toHaveBeenCalled();
  });

  it('keeps a collected order\'s stored address: a route change never sends the draft back (REV-03)', async () => {
    await setup(order({ deliveryMode: 'SELF_PICKUP', shippingAddressId: 'addr-1' }));

    component.changeLineRoute(line(), 'r-pps');

    expect(service.setLineFulfillmentRoute).toHaveBeenCalledOnceWith(UUID, 'l1', 'r-pps');
    expect(service.updateSaleOrder).not.toHaveBeenCalled();
  });

  it('shows where a collected order ships to when it has an address (a line routed to Ship, D-4)', async () => {
    await setup(order({ deliveryMode: 'SELF_PICKUP', shippingAddressId: 'addr-1' }));

    expect(query('ship-to')).not.toBeNull();
  });

  it('says nothing about re-pricing next to the route dropdowns', async () => {
    await setup();

    expect(query('route-legend')!.textContent).not.toContain('re-price');
  });

  it('does nothing for the route a line already has, or for someone who may not edit', async () => {
    await setup();
    component.changeLineRoute(COPPER, 'r-pps');
    expect(service.setLineFulfillmentRoute).not.toHaveBeenCalled();

    permissions = ['SALE_ORDER_VIEW'];
    await setup();
    component.changeLineRoute(line(), 'r-ps');
    expect(service.setLineFulfillmentRoute).not.toHaveBeenCalled();
    expect(service.updateSaleOrder).not.toHaveBeenCalled();
  });

  it('says why a route change was refused, and shows the line as it was', async () => {
    await setup();
    service.setLineFulfillmentRoute.and.returnValue(throwError(() => ({ status: 400, error: { message: "Route 'X' is inactive." } })));

    component.changeLineRoute(line(), 'r-ps');

    expect(lastToast().severity).toBe('error');
    expect(lastToast().detail).toBe("Route 'X' is inactive.");
    expect(component.order!.lines[0].fulfillmentRouteUuid ?? null).toBeNull();
    expect(component.savingRouteLineUuid).toBeNull();
  });

  // ── PC-08: the preview of a saved draft ────────────────────────────────────

  it('previews a saved draft through the GET and says how many deliveries confirming creates', async () => {
    await setup();

    expect(service.getDeliveryPreview).toHaveBeenCalledOnceWith(UUID);
    expect(query('delivery-preview-headline')!.textContent).toContain('On confirmation, 2 delivery orders will be created');
    expect(queryAll('delivery-preview-group').length).toBe(2);
  });

  it('shows no preview once the order is confirmed', async () => {
    await setup(order({ status: 'CONFIRMED' }));

    expect(service.getDeliveryPreview).not.toHaveBeenCalled();
    expect(query('delivery-preview')).toBeNull();
  });

  // ── PC-09: the Confirm gate ────────────────────────────────────────────────

  it('disables Confirm, without hiding it, while lines have no route, naming the count and the lines', async () => {
    await setup(order({
      lines: [line(), GASKET, COPPER, line({ uuid: 'l5', routeSource: 'NONE', routeBlocker: 'ROUTE_MISSING', effectiveRouteName: null })],
      confirmBlockers: [
        { lineUuid: 'l4', lineNumber: 2, code: 'ROUTE_MISSING', message: 'x' },
        { lineUuid: 'l5', lineNumber: 4, code: 'ROUTE_MISSING', message: 'x' }
      ]
    }));

    const confirm = query('action-confirm')!.querySelector('button') as HTMLButtonElement;
    expect(confirm).not.toBeNull();
    expect(confirm.disabled).toBeTrue();
    const tip = tooltipOf('confirm-wrap');
    expect(tip).toContain('2 lines');
    expect(tip).toContain('lines 2, 4');
  });

  it('shows a warning banner listing what blocks confirmation', async () => {
    await setup(order({
      lines: [line(), GASKET],
      confirmBlockers: [
        { lineUuid: 'l4', lineNumber: 2, code: 'ROUTE_MISSING', message: 'Cannot confirm: lines 2 have no fulfillment route.' },
        { code: 'SHIPPING_ADDRESS_REQUIRED', message: 'A line ships, so the order needs a shipping address.' }
      ]
    }));

    const banner = query('confirm-blockers-banner')!;
    expect(banner).not.toBeNull();
    expect(banner.textContent).toContain('Line 2');
    expect(banner.textContent).toContain('Custom Gasket');
    expect(banner.textContent).toContain('A line ships, so the order needs a shipping address.');
    expect(query('line-route-hint')).not.toBeNull();
  });

  it('keeps Confirm enabled and shows no banner when nothing blocks', async () => {
    await setup();

    const confirm = query('action-confirm')!.querySelector('button') as HTMLButtonElement;
    expect(confirm.disabled).toBeFalse();
    expect(query('confirm-blockers-banner')).toBeNull();
    // A34 PD-08: with nothing blocking, the tooltip says what confirming creates (no blocking reasons).
    expect(tooltipOf('confirm-wrap')).toBe('Will create 2 delivery orders');
  });

  it('will not open the confirm dialog, nor confirm, while blocked', async () => {
    await setup(order({ lines: [GASKET], confirmBlockers: [{ lineNumber: 1, code: 'ROUTE_MISSING', message: 'x' }] }));

    component.openConfirmDialog();
    component.confirmOrder();

    expect(component.confirmDialogVisible).toBeFalse();
    expect(service.confirmSaleOrder).not.toHaveBeenCalled();
  });

  it('shows each blocker of the server refusal on its own line', async () => {
    await setup();
    service.confirmSaleOrder.and.returnValue(throwError(() => ({ status: 400, error: {
      message: "Line 3: fulfillment route 'X' is inactive.\nCannot confirm: lines 2, 4 have no fulfillment route. Assign a route on each line or set a default route on the product variant."
    } })));

    component.openConfirmDialog();
    component.confirmOrder();
    fixture.detectChanges();

    expect(component.confirmErrors).toEqual([
      "Line 3: fulfillment route 'X' is inactive.",
      'Cannot confirm: lines 2, 4 have no fulfillment route. Assign a route on each line or set a default route on the product variant.'
    ]);
    expect(inBody('confirm-error-line').length).toBe(2);
    expect(component.confirmDialogVisible).toBeTrue();
  });

  it('names the deliveries confirming created', async () => {
    await setup();
    const result: SaleOrderConfirmResultModel = {
      status: 'CONFIRMED', skippedLines: [], deliveryCreationFailed: false,
      deliveries: [
        { deliveryUuid: 'd1', deliveryNumber: 'DLV-2026-00001', routeUuid: 'r-po', routeCode: 'PICK_ONLY', deliveryMode: 'SELF_PICKUP', lineCount: 1 },
        { deliveryUuid: 'd2', deliveryNumber: 'DLV-2026-00002', routeUuid: 'r-pps', routeCode: 'PICK_PACK_SHIP', deliveryMode: 'SHIP', lineCount: 1 }
      ]
    };
    service.confirmSaleOrder.and.returnValue(ok(result));

    component.openConfirmDialog();
    component.confirmOrder();

    expect(lastToast().severity).toBe('success');
    expect(lastToast().detail).toContain('2 delivery orders');
    expect(lastToast().detail).toContain('DLV-2026-00001');
  });

  it('says so when the order confirmed but its deliveries were not created, and offers to create them', async () => {
    await setup();
    service.confirmSaleOrder.and.returnValue(ok({
      status: 'CONFIRMED', deliveries: [], skippedLines: [], deliveryCreationFailed: true,
      deliveryMessage: 'The warehouse could not be reached.'
    }));
    service.getSaleOrderById.and.returnValue(ok(order({ status: 'CONFIRMED' })));

    component.openConfirmDialog();
    component.confirmOrder();
    fixture.detectChanges();

    expect(query('delivery-creation-failed')!.textContent).toContain('The warehouse could not be reached.');
    expect(query('create-deliveries-retry')).withContext('on the page, whichever tab is open').not.toBeNull();
  });

  // ── PD-06: the Deliveries tab as the fulfillment view ──────────────────────

  it('lists each delivery with its route, line count and status, and a link to it', async () => {
    await setup(order({ status: 'CONFIRMED' }), [
      delivery(),
      delivery({ uuid: 'd2', deliveryNumber: 'DLV-2026-00002', fulfillmentRouteName: 'Pick, Pack & Ship', status: 'PACKED', lineCount: 2 }),
      delivery({ uuid: 'd3', deliveryNumber: 'DLV-2026-00003', fulfillmentRouteUuid: null, fulfillmentRouteCode: null, fulfillmentRouteName: null })
    ]);
    component.activeTab = 1;
    fixture.detectChanges();

    expect(queryAll('delivery-route').map(e => e.textContent!.trim())).toEqual(['Pick Only', 'Pick, Pack & Ship', 'No route']);
    expect(queryAll('delivery-line-count').map(e => e.textContent!.trim())).toEqual(['1', '2', '1']);
    expect(queryAll('delivery-status').length).toBe(3);
    expect(queryAll('open-delivery').length).toBe(3);
    expect(query('deliveries-heading')!.textContent).toContain('Delivery orders (3)');
  });

  it('says how many of the order lines are delivered', async () => {
    await setup(order({ status: 'PARTIALLY_FULFILLED', lines: [
      line({ fulfilledQty: 500, status: 'FULFILLED' }),
      COPPER,
      line({ uuid: 'l3', quantity: 100, fulfilledQty: 40 }),
      line({ uuid: 'l9', status: 'CANCELLED' })
    ] }), [delivery()]);
    component.activeTab = 1;
    fixture.detectChanges();

    expect(component.linesDelivered).toBe(1);
    expect(component.linesToDeliver).toBe(3);
    expect(query('lines-delivered')!.textContent).toContain('1 of 3 lines delivered');
  });

  it('expands a delivery to show its lines, reading them once', async () => {
    await setup(order({ status: 'CONFIRMED' }), [delivery()]);
    component.activeTab = 1;
    fixture.detectChanges();
    expect(query('delivery-expansion')).toBeNull();

    component.toggleDelivery(delivery());
    fixture.detectChanges();
    expect(logistics.getDeliveryById).toHaveBeenCalledOnceWith('d1');
    expect(query('delivery-expansion')!.textContent).toContain('Steel Pipes × 500');
    expect(queryAll('delivery-line').length).toBe(1);
    expect(queryAll('delivery-line')[0].textContent).toContain('Steel Pipes');

    component.toggleDelivery(delivery());
    fixture.detectChanges();
    expect(query('delivery-expansion')).toBeNull();

    component.toggleDelivery(delivery());
    expect(logistics.getDeliveryById).toHaveBeenCalledTimes(1);
  });

  it('offers "Create deliveries" on a confirmed order with routes to someone with DELIVERY_CREATE only', async () => {
    await setup(order({ status: 'CONFIRMED' }));
    expect(component.canCreateRouteDeliveries).toBeTrue();
    component.activeTab = 1;
    fixture.detectChanges();
    expect(query('create-deliveries')).not.toBeNull();

    for (const status of ['DRAFT', 'FULFILLED', 'CANCELLED']) {
      await setup(order({ status }));
      expect(component.canCreateRouteDeliveries).withContext(status).toBeFalse();
    }

    await setup(order({ status: 'CONFIRMED', routesEnabled: false }));
    expect(component.canCreateRouteDeliveries).withContext('no routes (D-11)').toBeFalse();

    permissions = ALL.filter(p => p !== 'DELIVERY_CREATE');
    await setup(order({ status: 'CONFIRMED' }));
    expect(component.canCreateRouteDeliveries).withContext('DELIVERY_CREATE').toBeFalse();
  });

  it('creates the missing deliveries, says what it made and skipped, and re-reads the list', async () => {
    await setup(order({ status: 'CONFIRMED' }));
    service.createDeliveries.and.returnValue(ok({
      created: [{ deliveryUuid: 'd9', deliveryNumber: 'DLV-2026-00009', routeUuid: 'r-po', routeCode: 'PICK_ONLY', deliveryMode: 'SELF_PICKUP', lineCount: 1 }],
      skipped: [{ soLineUuid: 'l2', reason: 'Confirmed before routes: use Create delivery.' }]
    }));
    const lists = service.getDeliveries.calls.count();

    component.createRouteDeliveries();
    fixture.detectChanges();

    expect(service.createDeliveries).toHaveBeenCalledOnceWith(UUID);
    expect(lastToast().detail).toContain('DLV-2026-00009');
    expect(component.createDeliveriesSkipped.length).toBe(1);
    component.activeTab = 1;
    fixture.detectChanges();
    expect(query('create-deliveries-skipped')!.textContent).toContain('Copper Wire');
    expect(query('create-deliveries-skipped')!.textContent).toContain('Confirmed before routes');
    expect(service.getDeliveries.calls.count()).toBe(lists + 1);
  });

  it('after cancelling, says which deliveries were cancelled and which were kept (D-15)', async () => {
    await setup(order({ status: 'CONFIRMED' }));
    const result: SaleOrderCancelResultModel = {
      cancelledDeliveries: [{ deliveryUuid: 'd1', deliveryNumber: 'DLV-2026-00001', status: 'CANCELLED' }],
      issuedDeliveries: [{ deliveryUuid: 'd2', deliveryNumber: 'DLV-2026-00002', status: 'GOODS_ISSUED' }]
    };
    service.cancelSaleOrder.and.returnValue(ok(result));

    component.openCancelDialog();
    component.cancelOrder();
    fixture.detectChanges();

    expect(lastToast().detail).toContain('1 delivery cancelled');
    const panel = query('cancel-result')!;
    expect(panel.textContent).toContain('DLV-2026-00001');
    expect(panel.textContent).toContain('DLV-2026-00002');
    expect(queryAll('cancelled-delivery').length).toBe(1);
    expect(queryAll('issued-delivery').length).toBe(1);
  });

  it('shows no cancel result when the server sends none', async () => {
    await setup(order({ status: 'CONFIRMED' }));

    component.openCancelDialog();
    component.cancelOrder();
    fixture.detectChanges();

    expect(query('cancel-result')).toBeNull();
    expect(lastToast().severity).toBe('success');
  });
});
