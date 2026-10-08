import { signal } from '@angular/core';
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
  SaleOrderService, SaleOrderModel, SaleOrderLineModel, SaleOrderDeliveryPreviewModel, SaleOrderProductionOrderModel
} from '../../../../services/sale-order.service';
import { FulfillmentRoutesService, FulfillmentRouteModel } from '../../../../services/fulfillment-routes.service';
import { TenantService } from '../../../service/tenant.service';
import { LogisticsService } from '../../../../services/logistics.service';
import { BusinessPartnerService } from '../../../../services/business-partner.service';
import { SalesInvoiceService } from '../../../../services/sales-invoice.service';
import { AddressService } from '../../../../services/address.service';
import { TimelineService } from '../../../../services/timeline.service';
import { ProductionOrderService } from '../../../../services/production-order.service';
import { AttachmentService } from '../../../../services/attachment.service';
import { AttachmentPolicyService } from '../../../../services/attachment-policy.service';
import { AuthService } from '../../../service/auth.service';
import { LeadTimeResultModel } from '../../../../services/lead-time.service';

// A34 — sale order detail: the ⏱ lead time and delivery date per line (PC-07/08/09), the D-5 make-to-order blockers,
// the preview's production group and the Confirm tooltip (PD-08), the confirm result's production orders with the
// failure banner and "Create production orders" (D-17), the Production tab with route and delivery (D-25 / PD-07),
// "N lines awaiting production", the route-category tag and shortfall badge (D-21), the cancel cascade (D-22),
// "Make to order" sourcing (R-15) and the D-28 manual-reserve warning. Contract: docs/route-classification/API-CONTRACT.md.

const UUID = '22222222-2222-2222-2222-222222222222';

const ALL = ['SALE_ORDER_VIEW', 'SALE_ORDER_EDIT', 'SALE_ORDER_CONFIRM', 'SALE_ORDER_CANCEL', 'SALE_ORDER_RESERVE',
             'DELIVERY_VIEW', 'DELIVERY_CREATE', 'PROD_VIEW'];

function line(overrides: Partial<SaleOrderLineModel> = {}): SaleOrderLineModel {
  return {
    uuid: 'l1', variantUuid: 'v1', itemDescription: 'Steel Pipes', variantSku: 'SP-1', unitOfMeasure: 'M',
    quantity: 500, unitPrice: 450, discountPercent: 0, taxPercent: 0, lineTotal: 225000, fulfilledQty: 0, invoicedQty: 0,
    status: 'OPEN', fulfillmentRouteUuid: null, effectiveRouteUuid: 'r-ps', effectiveRouteCode: 'PICK_AND_SHIP',
    effectiveRouteName: 'Pick & Ship', effectiveRouteSteps: ['PICK', 'GOODS_ISSUE', 'SHIP'], routeSource: 'ORG_DEFAULT',
    routeBlocker: null, effectiveRouteCategory: 'STOCK', deliveryDateSource: 'NONE',
    ...overrides
  };
}

const GEAR = line({
  uuid: 'l2', variantUuid: 'v2', itemDescription: 'Custom Gear Assy', variantSku: 'CG-1', quantity: 50, unitPrice: 1200,
  lineTotal: 60000, effectiveRouteUuid: 'r-mfg', effectiveRouteCode: 'MFG_PICK_PACK_SHIP',
  effectiveRouteName: 'Manufacture → Pick, Pack & Ship', routeSource: 'VARIANT', effectiveRouteCategory: 'MANUFACTURE'
});

function po(overrides: Partial<SaleOrderProductionOrderModel> = {}): SaleOrderProductionOrderModel {
  return {
    productionOrderUuid: 'po-1', productionNumber: 'PROD-2026-00085', soLineUuid: 'l2', lineNumber: 2, status: 'IN_PROGRESS',
    plannedQuantity: 50, acceptedQuantity: 0, isMakeToOrder: true, fulfillmentRouteUuid: 'r-mfg',
    fulfillmentRouteCode: 'MFG_PICK_PACK_SHIP', fulfillmentRouteName: 'Manufacture → Pick, Pack & Ship',
    deliveryOrderUuid: null, deliveryNumber: null, created: false,
    ...overrides
  };
}

function order(overrides: Partial<SaleOrderModel> = {}): SaleOrderModel {
  return {
    uuid: UUID, traceId: 't-1', soNumber: 'SO-2026-02001', partnerId: 'p-1',
    orderDate: '2026-10-01T00:00:00', expectedDeliveryDate: '2026-10-25T00:00:00', currencyId: 'c-1',
    subtotal: 0, taxAmount: 0, discountAmount: 0, grandTotal: 0,
    status: 'DRAFT', requiresShipment: true, deliveryMode: 'SHIP', shippingAddressId: 'addr-1',
    createdDate: '2026-10-01T00:00:00', sourceType: 'MANUAL',
    lines: [line(), GEAR], routesEnabled: true, confirmBlockers: [], productionOrders: [], productionCreationPending: false,
    ...overrides
  };
}

function preview(overrides: Partial<SaleOrderDeliveryPreviewModel> = {}): SaleOrderDeliveryPreviewModel {
  return {
    routesEnabled: true, canConfirm: true, deliveryCount: 1,
    lines: [
      { lineUuid: 'l1', lineNumber: 1, variantUuid: 'v1', itemDescription: 'Steel Pipes', quantity: 500, effectiveRouteUuid: 'r-ps',
        effectiveRouteName: 'Pick & Ship', effectiveRouteSteps: ['PICK', 'GOODS_ISSUE', 'SHIP'], routeSource: 'ORG_DEFAULT',
        effectiveRouteCategory: 'STOCK' },
      { lineUuid: 'l2', lineNumber: 2, variantUuid: 'v2', itemDescription: 'Custom Gear Assy', quantity: 50, effectiveRouteUuid: 'r-mfg',
        effectiveRouteName: 'Manufacture → Pick, Pack & Ship', effectiveRouteSteps: ['PICK', 'PACK', 'GOODS_ISSUE', 'SHIP'],
        routeSource: 'VARIANT', effectiveRouteCategory: 'MANUFACTURE' }
    ],
    groups: [{ routeUuid: 'r-ps', routeCode: 'PICK_AND_SHIP', routeName: 'Pick & Ship', steps: ['PICK', 'GOODS_ISSUE', 'SHIP'],
               stepsText: 'Pick → Goods Issue → Ship', requiresShipping: true, deliveryMode: 'SHIP', lineNumbers: [1] }],
    blockers: [],
    productionLines: [{ lineUuid: 'l2', lineNumber: 2, variantUuid: 'v2', itemDescription: 'Custom Gear Assy', quantity: 50,
                        routeUuid: 'r-mfg', routeCode: 'MFG_PICK_PACK_SHIP', routeName: 'Manufacture → Pick, Pack & Ship',
                        steps: ['PICK', 'PACK', 'GOODS_ISSUE', 'SHIP'],
                        message: 'A production order will be created; its delivery follows when production completes.' }],
    ...overrides
  };
}

const LEAD: LeadTimeResultModel = {
  totalLeadTimeDays: 13, earliestDeliveryDate: '2026-10-17T00:00:00', routeCategory: 'MANUFACTURE', calculatedAt: '2026-10-04T08:00:00Z',
  components: [{ code: 'MANUFACTURING', name: 'Manufacturing', days: 5, source: 'BOM' }]
};

function ok<T>(result: T, message = '') {
  return of({ success: true, message, result } as any);
}

describe('SaleOrderDetailComponent — A34 make to order and lead times', () => {
  let fixture: ComponentFixture<SaleOrderDetailComponent>;
  let component: SaleOrderDetailComponent;
  let service: jasmine.SpyObj<SaleOrderService>;
  let production: jasmine.SpyObj<ProductionOrderService>;
  let toasts: jasmine.Spy;
  let permissions: string[];

  const auth = { hasPermission: (code: string) => permissions.includes(code) } as unknown as AuthService;

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  function queryAll(testId: string): HTMLElement[] {
    return Array.from(fixture.nativeElement.querySelectorAll(`[data-testid="${testId}"]`));
  }

  function text(el: Element | null): string {
    return (el?.textContent ?? '').replace(/\s+/g, ' ').trim();
  }

  function tooltipOf(testId: string): string {
    const el = fixture.debugElement.query(By.css(`[data-testid="${testId}"]`));
    return String(el.injector.get(Tooltip).content ?? '');
  }

  function toastsWith(severity: string) {
    return toasts.calls.allArgs().map(a => a[0]).filter((t: any) => t.severity === severity);
  }

  function clickIn(parent: HTMLElement, testId: string) {
    const el = parent.querySelector(`[data-testid="${testId}"]`) as HTMLElement;
    expect(el).withContext(testId).not.toBeNull();
    (el.tagName === 'P-BUTTON' ? el.querySelector('button')! : el).click();
    fixture.detectChanges();
  }

  let routeList: FulfillmentRouteModel[];
  let features: string[];

  async function setup(model: SaleOrderModel = order(), pv: SaleOrderDeliveryPreviewModel = preview()) {
    service = jasmine.createSpyObj<SaleOrderService>('SaleOrderService', [
      'getSaleOrderById', 'getDeliveries', 'createDelivery', 'confirmSaleOrder', 'cancelSaleOrder', 'getAvailability',
      'updateCustomerPo', 'checkCustomerPo', 'reserveLine', 'releaseLine', 'reserveAll', 'updateSaleOrder',
      'getDeliveryPreview', 'createDeliveries', 'setLineFulfillmentRoute',
      'calculateLineLeadTime', 'setLineDeliveryDate', 'createProductionOrders'
    ]);
    service.getSaleOrderById.and.returnValue(ok(model));
    service.getDeliveries.and.returnValue(ok([]));
    service.getAvailability.and.returnValue(ok([]));
    service.checkCustomerPo.and.returnValue(ok([]));
    service.getDeliveryPreview.and.returnValue(ok(pv));
    service.confirmSaleOrder.and.returnValue(ok(null));
    service.cancelSaleOrder.and.returnValue(ok(null));
    service.createProductionOrders.and.returnValue(ok({ productionOrders: [], productionCreationFailed: false }));

    const routes = jasmine.createSpyObj<FulfillmentRoutesService>('FulfillmentRoutesService', ['getRoutes']);
    routes.getRoutes.and.returnValue(ok(routeList));
    const logistics = jasmine.createSpyObj<LogisticsService>('LogisticsService', ['getDeliveryById']);
    const partners = jasmine.createSpyObj<BusinessPartnerService>('BusinessPartnerService', ['getPartnerById']);
    partners.getPartnerById.and.returnValue(ok({ uuid: 'p-1', companyName: 'Punjab Group' }));
    const invoices = jasmine.createSpyObj<SalesInvoiceService>('SalesInvoiceService', ['getInvoices', 'getInvoice', 'downloadPdf']);
    invoices.getInvoices.and.returnValue(ok({ data: [], totalRecords: 0, page: 1, pageSize: 100, totalPages: 0 }));
    const addresses = jasmine.createSpyObj<AddressService>('AddressService', ['getAddress']);
    addresses.getAddress.and.returnValue(ok(null));
    const timeline = jasmine.createSpyObj<TimelineService>('TimelineService', ['getByTraceId', 'getByDocument']);
    timeline.getByTraceId.and.returnValue(ok(null));
    production = jasmine.createSpyObj<ProductionOrderService>('ProductionOrderService', ['getList']);
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
        { provide: TenantService, useValue: { tenant: signal({ enabledFeatureCodes: features }) } },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['uuid', UUID]]) } } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(SaleOrderDetailComponent);
    component = fixture.componentInstance;
    toasts = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
    fixture.detectChanges();
  }

  beforeEach(() => {
    permissions = [...ALL];
    routeList = [];
    features = ['MODULE_DEMAND', 'MODULE_LOGISTICS', 'MODULE_MANUFACTURING'];
  });

  // ── D-9: MANUFACTURE routes in the line's route picker ──────────────────────

  it('offers MANUFACTURE routes on a line only when the organization has manufacturing (D-9)', async () => {
    const r = (uuid: string, name: string, routeCategory: 'STOCK' | 'MANUFACTURE') => ({
      uuid, code: uuid, name, isDefault: false, isActive: true, isSystem: true, requiresPacking: false, requiresShipping: true,
      displayOrder: 10, steps: [], stepsText: '', statusPath: [], createdDate: '2026-10-03T00:00:00Z', routeCategory
    } as FulfillmentRouteModel);
    routeList = [r('r-ps', 'Pick & Ship', 'STOCK'), r('r-mfg', 'Manufacture → Pick & Ship', 'MANUFACTURE')];

    await setup();
    expect(component.routeOptionsFor(component.order!.lines[0]).map(o => o.label)).toEqual(['Pick & Ship', 'Manufacture → Pick & Ship']);

    features = ['MODULE_DEMAND', 'MODULE_LOGISTICS'];
    await setup();
    expect(component.routeOptionsFor(component.order!.lines[0]).map(o => o.label)).toEqual(['Pick & Ship']);
  });

  // ── PC-07/08/09: lead time per line ─────────────────────────────────────────

  it('shows each line\'s delivery date and calculates a draft line\'s lead time on demand, through the line\'s own endpoint', async () => {
    await setup();
    expect(service.calculateLineLeadTime).not.toHaveBeenCalled();
    const cells = queryAll('line-delivery');
    expect(cells.length).toBe(2);
    expect(text(cells[1])).toContain('— Not calculated');

    service.calculateLineLeadTime.and.returnValue(ok({
      line: { ...GEAR, calculatedLeadTimeDays: 13, calculatedDeliveryDate: '2026-10-17T00:00:00',
              leadTimeCalculatedAt: '2026-10-04T08:00:00Z', effectiveDeliveryDate: '2026-10-17T00:00:00', deliveryDateSource: 'CALCULATED' },
      leadTime: LEAD
    }));
    clickIn(cells[1], 'lt-calculate');

    expect(service.calculateLineLeadTime).toHaveBeenCalledOnceWith(UUID, 'l2');
    expect(text(cells[1].querySelector('[data-testid="lt-total"]'))).toContain('Total: 13 days');
    expect(text(queryAll('line-delivery')[1].querySelector('[data-testid="lt-indicator"]'))).toContain('⏱ Calculated');
    expect(text(queryAll('line-delivery')[1].querySelector('[data-testid="lt-date"]'))).toContain('17 Oct 2026');
  });

  it('sets and clears a confirmed line\'s manual date through the delivery-date endpoint, and passes on the reschedule warning', async () => {
    await setup(order({
      status: 'CONFIRMED', productionOrders: [po()],
      lines: [line({ fulfillmentMode: 'IN_STOCK' }),
              { ...GEAR, fulfillmentMode: 'MAKE_TO_ORDER', calculatedDeliveryDate: '2026-10-17T00:00:00', calculatedLeadTimeDays: 13 }]
    }));
    const cell = queryAll('line-delivery')[1];
    expect(cell.querySelector('[data-testid="lt-calculate"]')).withContext('no calculation once confirmed').toBeNull();

    service.setLineDeliveryDate.and.returnValue(ok({
      line: { ...GEAR, manualDeliveryDate: '2026-10-20T00:00:00', calculatedDeliveryDate: '2026-10-17T00:00:00', deliveryDateSource: 'MANUAL' },
      productionNotRescheduled: true, warning: 'PROD-2026-00085 was planned for the earlier date and is not rescheduled.'
    }));
    clickIn(cell, 'lt-set-date');
    const popover = fixture.debugElement.queryAll(By.css('app-lead-time-popover'))[1].componentInstance;
    popover.overrideValue = new Date(2026, 9, 20);
    fixture.detectChanges();
    clickIn(cell, 'lt-override-save');

    expect(service.setLineDeliveryDate).toHaveBeenCalledOnceWith(UUID, 'l2', '2026-10-20');
    expect(text(queryAll('line-delivery')[1].querySelector('[data-testid="lt-indicator"]'))).toContain('✎ Manual');
    expect(toastsWith('warn').some((t: any) => t.detail.includes('not rescheduled'))).toBeTrue();

    service.setLineDeliveryDate.calls.reset();
    service.setLineDeliveryDate.and.returnValue(ok({ line: { ...GEAR, manualDeliveryDate: null, calculatedDeliveryDate: '2026-10-17T00:00:00' },
                                                     productionNotRescheduled: false }));
    clickIn(queryAll('line-delivery')[1], 'lt-clear');
    expect(service.setLineDeliveryDate).toHaveBeenCalledOnceWith(UUID, 'l2', null);
  });

  it('offers no date change without SALE_ORDER_EDIT, nor on a cancelled order', async () => {
    permissions = ALL.filter(c => c !== 'SALE_ORDER_EDIT');
    await setup();
    expect(queryAll('line-delivery')[0].querySelector('[data-testid="lt-calculate"]')).toBeNull();
    expect(queryAll('line-delivery')[0].querySelector('[data-testid="lt-set-date"]')).toBeNull();

    permissions = [...ALL];
    await setup(order({ status: 'CANCELLED' }));
    expect(queryAll('line-delivery')[0].querySelector('[data-testid="lt-set-date"]')).toBeNull();
  });

  // ── D-5: the four make-to-order blockers ────────────────────────────────────

  it('names a make-to-order line that cannot be made, on the banner, the line and the Confirm tooltip', async () => {
    const blocked = { ...GEAR, routeBlocker: 'BOM_MISSING' as const };
    await setup(order({
      lines: [line(), blocked],
      confirmBlockers: [{ lineUuid: 'l2', lineNumber: 2, code: 'BOM_MISSING',
                          message: 'Line 2: Custom Gear Assy has no active bill of materials, so it can\'t be made to order. Activate a BOM, or choose a stock route for this line.' }]
    }), preview({ canConfirm: false, productionLines: [], lines: [preview().lines[0], { ...preview().lines[1], routeBlocker: 'BOM_MISSING' }] }));

    expect(text(query('confirm-blockers-banner'))).toContain('Line 2 · Custom Gear Assy: no active bill of materials, so it can\'t be made to order');
    expect(text(queryAll('line-route-hint')[1])).toContain('bill of materials');
    expect(tooltipOf('confirm-wrap')).toContain("1 line can't be made to order (line 2)");
    expect(text(query('delivery-preview-unroutable'))).toContain('no active bill of materials');
  });

  // ── PD-08: preview and Confirm tooltip ──────────────────────────────────────

  it('groups the make-to-order lines as production in the preview, and says so on Confirm', async () => {
    await setup();

    const group = query('delivery-preview-production')!;
    expect(text(group)).toContain('Production orders (1 line)');
    expect(text(query('delivery-preview-production-note'))).toBe('Production will be triggered. Delivery is created after production completes.');
    expect(text(group)).toContain('Line 2: Custom Gear Assy × 50 — Manufacture → Pick, Pack & Ship');
    expect(text(query('delivery-preview-headline'))).toBe('On confirmation, 1 delivery order and 1 production order will be created:');
    expect(tooltipOf('confirm-wrap')).toBe('Will create 1 delivery order and 1 production order');
  });

  // ── D-17: confirm result, failure banner, recovery ──────────────────────────

  it('lists the production orders confirming created', async () => {
    await setup();
    service.confirmSaleOrder.and.returnValue(ok({
      status: 'CONFIRMED', deliveries: [{ deliveryUuid: 'd1', deliveryNumber: 'DLV-2026-00001', routeUuid: 'r-ps', routeCode: 'PICK_AND_SHIP',
                                          deliveryMode: 'SHIP', lineCount: 1 }],
      skippedLines: [{ soLineUuid: 'l2', reason: 'Line 2 is made to order: its delivery is created when its production order completes.' }],
      deliveryCreationFailed: false, productionOrders: [po({ status: 'PLANNED', created: true })], productionCreationFailed: false
    }));
    service.getSaleOrderById.and.returnValue(ok(order({ status: 'CONFIRMED', productionOrders: [po({ status: 'PLANNED' })] })));
    component.openConfirmDialog();
    component.confirmOrder();
    fixture.detectChanges();

    expect(text(query('confirm-production-result'))).toContain('PROD-2026-00085');
    expect(text(query('confirm-production-result'))).toContain('Line 2');
    const success = toastsWith('success').map((t: any) => t.detail).join(' ');
    expect(success).toContain('1 production order created: PROD-2026-00085');
  });

  it('shows the failure banner when production was not created, and creates the orders on the recovery button', async () => {
    await setup();
    service.confirmSaleOrder.and.returnValue(ok({
      status: 'CONFIRMED', deliveries: [], skippedLines: [], deliveryCreationFailed: false,
      productionOrders: [], productionCreationFailed: true, productionMessage: 'Planning failed: no BOM revision is effective today.'
    }));
    service.getSaleOrderById.and.returnValue(ok(order({ status: 'CONFIRMED', productionCreationPending: true })));
    component.confirmOrder();
    fixture.detectChanges();

    const banner = query('production-creation-failed')!;
    expect(text(banner)).toContain('Planning failed: no BOM revision is effective today.');
    expect(toastsWith('warn').length).toBeGreaterThan(0);

    service.createProductionOrders.and.returnValue(ok({ productionOrders: [po({ status: 'DRAFT', created: true })], productionCreationFailed: false }));
    service.getSaleOrderById.and.returnValue(ok(order({ status: 'CONFIRMED', productionOrders: [po({ status: 'DRAFT' })] })));
    clickIn(banner, 'create-production-orders');

    expect(service.createProductionOrders).toHaveBeenCalledOnceWith(UUID);
    expect(query('production-creation-failed')).toBeNull();
    expect(toastsWith('success').some((t: any) => t.detail.includes('PROD-2026-00085'))).toBeTrue();
  });

  it('shows the pending banner on a confirmed order read from the server, with the button for PROD_CREATE alone', async () => {
    permissions = ['SALE_ORDER_VIEW', 'PROD_CREATE'];
    await setup(order({ status: 'CONFIRMED', productionCreationPending: true }));
    expect(query('production-creation-failed')).not.toBeNull();
    expect(query('create-production-orders')).not.toBeNull();

    permissions = ['SALE_ORDER_VIEW'];
    await setup(order({ status: 'CONFIRMED', productionCreationPending: true }));
    expect(query('production-creation-failed')).not.toBeNull();
    expect(query('create-production-orders')).toBeNull();
  });

  it('keeps the banner up when the recovery fails again, with the server\'s reason', async () => {
    await setup(order({ status: 'CONFIRMED', productionCreationPending: true }));
    service.createProductionOrders.and.returnValue(throwError(() => ({ status: 400, error: { message: 'Manufacturing is not enabled for your organization.' } })));
    clickIn(query('production-creation-failed')!, 'create-production-orders');
    expect(query('production-creation-failed')).not.toBeNull();
    expect(toastsWith('error')[0].detail).toContain('Manufacturing is not enabled');
  });

  // ── D-25 / PD-07: Production tab, Deliveries tab ────────────────────────────

  it('lists the order\'s production orders from the detail itself, with route and delivery, without PROD_VIEW', async () => {
    permissions = ['SALE_ORDER_VIEW', 'DELIVERY_VIEW'];
    await setup(order({
      status: 'PARTIALLY_FULFILLED',
      productionOrders: [
        po(),
        po({ productionOrderUuid: 'po-2', productionNumber: 'PROD-2026-00086', status: 'COMPLETED', acceptedQuantity: 48,
             deliveryOrderUuid: 'd-5', deliveryNumber: 'DLV-2026-00005' }),
        po({ productionOrderUuid: 'po-3', productionNumber: 'PROD-2026-00090', soLineUuid: 'l1', lineNumber: 1, isMakeToOrder: false,
             fulfillmentRouteUuid: null, fulfillmentRouteCode: null, fulfillmentRouteName: null })
      ]
    }));
    component.activeTab = 5;
    fixture.detectChanges();

    expect(production.getList).not.toHaveBeenCalled();
    expect(query('production-no-access')).toBeNull();
    const rows = queryAll('production-row');
    expect(rows.length).toBe(3);
    expect(text(rows[0])).toContain('Line 2 · Custom Gear Assy');
    expect(text(rows[0].querySelector('[data-testid="production-route"]'))).toBe('Manufacture → Pick, Pack & Ship');
    expect(text(rows[0].querySelector('[data-testid="production-delivery"]'))).toBe('Delivery: pending');
    expect(text(rows[1].querySelector('[data-testid="production-delivery"]'))).toBe('DLV-2026-00005');
    expect(rows[1].querySelector('a[data-testid="production-delivery-link"]')).not.toBeNull();
    expect(text(rows[2].querySelector('[data-testid="production-route"]'))).toContain('—');
    expect(text(rows[2].querySelector('[data-testid="production-delivery"]'))).toBe('—');
    expect(query('open-production-order')).withContext('the production order page needs PROD_VIEW').toBeNull();
  });

  it('falls back to the production list for an older server that sends no productionOrders', async () => {
    await setup(order({ productionOrders: undefined }));
    expect(production.getList).toHaveBeenCalled();
  });

  it('notes on the Deliveries tab how many lines are waiting on production', async () => {
    await setup(order({
      status: 'CONFIRMED', productionOrders: [po()],
      lines: [line({ fulfillmentMode: 'IN_STOCK', status: 'RESERVED' }), { ...GEAR, fulfillmentMode: 'MAKE_TO_ORDER' }]
    }));
    component.activeTab = 1;
    fixture.detectChanges();
    expect(text(query('awaiting-production'))).toContain('1 line awaiting production');
  });

  // ── Lines: category tag, shortfall, sourcing ────────────────────────────────

  it('tags each line with its route category, shows a shortfall and labels MAKE_TO_ORDER "Make to order"', async () => {
    await setup(order({
      status: 'PARTIALLY_FULFILLED', productionOrders: [po({ status: 'COMPLETED', acceptedQuantity: 45 })],
      lines: [line({ fulfillmentMode: 'IN_STOCK' }), { ...GEAR, fulfillmentMode: 'MAKE_TO_ORDER', productionShortfallQty: 5 }]
    }));

    expect(queryAll('line-category').map(text)).toEqual(['Stock', 'Manufacture']);
    expect(text(query('line-shortfall'))).toContain('Short 5');
    expect(queryAll('line-sourcing').map(text)).toEqual(['In Stock', 'Make to order']);
    expect(text(query('sourcing-header'))).toBe('Sourcing');
  });

  // ── D-22: cancel ────────────────────────────────────────────────────────────

  it('lists the production orders cancelling stopped, and the ones still running', async () => {
    await setup(order({ status: 'CONFIRMED' }));
    service.cancelSaleOrder.and.returnValue(ok({
      cancelledDeliveries: [], issuedDeliveries: [],
      cancelledProductionOrders: [po({ status: 'CANCELLED' })],
      runningProductionOrders: [po({ productionOrderUuid: 'po-2', productionNumber: 'PROD-2026-00086', status: 'IN_PROGRESS' })],
      cancelledAllocationDemands: 2
    }));
    component.openCancelDialog();
    component.cancelOrder();
    fixture.detectChanges();

    expect(text(query('cancelled-production'))).toContain('PROD-2026-00085');
    expect(text(query('running-production'))).toContain('PROD-2026-00086');
    expect(toastsWith('warn')[0].detail).toContain('1 production order still running');
  });

  // ── D-28: manual reserve on a make-to-order line ────────────────────────────

  it('warns that the production order still makes the full quantity when reserving a make-to-order line', async () => {
    const mto = { ...GEAR, fulfillmentMode: 'MAKE_TO_ORDER', reservableQty: 50, reservedQty: 0 };
    await setup(order({ status: 'CONFIRMED', productionOrders: [po()], lines: [line({ fulfillmentMode: 'IN_STOCK' }), mto] }));

    expect(component.reserveTooltip(mto)).toContain('PROD-2026-00085 still makes the full quantity');
    service.reserveLine.and.returnValue(ok({
      lineUuid: 'l2', variantUuid: 'v2', outcome: 'RESERVED', requestedQty: 50, changedQty: 20, availableQty: 20,
      reservedQty: 20, reservableQty: 30, deliveryIndicator: 'YELLOW', lineStatus: 'OPEN'
    }));
    component.reserveLine(mto);

    expect(toastsWith('warn').some((t: any) => t.detail.includes('PROD-2026-00085 still makes the full quantity'))).toBeTrue();
  });
});
