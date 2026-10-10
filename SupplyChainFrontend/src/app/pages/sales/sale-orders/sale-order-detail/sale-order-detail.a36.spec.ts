import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of } from 'rxjs';

import { SaleOrderDetailComponent } from './sale-order-detail.component';
import { SaleOrderService, SaleOrderModel, SaleOrderLineModel } from '../../../../services/sale-order.service';
import { FulfillmentRoutesService } from '../../../../services/fulfillment-routes.service';
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
import { provideTestOrgCurrencies } from '../../../../shared/doc-currency/doc-currency.testing';
import { lineRouteDisplay } from '../fulfillment-route-display';

// A36-P5-07 — service lines show their service order instead of delivery info; the Fulfilment panel sums the
// order's service orders (docs/service-orders/API-CONTRACT.md §4).

const UUID = '36363636-3636-3636-3636-363636363636';

function line(overrides: Partial<SaleOrderLineModel> = {}): SaleOrderLineModel {
  return {
    uuid: 'l1', variantUuid: 'v1', itemDescription: 'Widget', quantity: 5, unitPrice: 100, discountPercent: 0,
    taxPercent: 0, lineTotal: 500, fulfilledQty: 0, invoicedQty: 0, status: 'OPEN', routeSource: 'ORG_DEFAULT',
    effectiveRouteSteps: [], ...overrides
  };
}

function order(overrides: Partial<SaleOrderModel> = {}): SaleOrderModel {
  return {
    uuid: UUID, traceId: 't-1', soNumber: 'SO-2026-0600', partnerId: 'p-1', orderDate: '2026-10-10T00:00:00',
    currencyId: 'cur-pkr', currencyCode: 'PKR', subtotal: 2500, taxAmount: 0, discountAmount: 0, grandTotal: 2500,
    status: 'CONFIRMED', requiresShipment: false, deliveryMode: 'SELF_PICKUP', createdDate: '2026-10-10T00:00:00',
    sourceType: 'MANUAL', routesEnabled: false, confirmBlockers: [], productionOrders: [],
    lines: [
      line(),
      line({ uuid: 'l2', variantUuid: 'v2', itemDescription: 'AC installation', isService: true }),
      line({ uuid: 'l3', variantUuid: 'v3', itemDescription: 'AC servicing', isService: true }),
      line({ uuid: 'l4', variantUuid: 'v4', itemDescription: 'Inspection', isService: true })
    ],
    serviceOrders: [
      { serviceOrderUuid: 's-2', serviceNumber: 'SVC-2026-0001', soLineUuid: 'l2', lineNumber: 2, status: 'COMPLETED', quantity: 1 },
      { serviceOrderUuid: 's-3', serviceNumber: 'SVC-2026-0002', soLineUuid: 'l3', lineNumber: 3, status: 'WAITING', quantity: 1 }
    ],
    ...overrides
  };
}

const ok = <T>(result: T) => of({ success: true, message: '', result } as any);

describe('SaleOrderDetailComponent — A36 service lines', () => {
  let fixture: ComponentFixture<SaleOrderDetailComponent>;
  let component: SaleOrderDetailComponent;
  let perms: string[];

  const qa = (id: string): HTMLElement[] => Array.from(fixture.nativeElement.querySelectorAll(`[data-testid="${id}"]`));
  const q = (id: string): HTMLElement | null => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);
  const text = (el: Element | null) => (el?.textContent ?? '').replace(/\s+/g, ' ').trim();

  async function setup(model: SaleOrderModel, permissions = ['SALE_ORDER_VIEW', 'SERVICE_ORDER_VIEW']) {
    perms = permissions;
    const service = jasmine.createSpyObj<SaleOrderService>('SaleOrderService', [
      'getSaleOrderById', 'getDeliveries', 'getAvailability', 'checkCustomerPo', 'getDeliveryPreview'
    ]);
    service.getSaleOrderById.and.returnValue(ok(model));
    service.getDeliveries.and.returnValue(ok([]));
    service.getAvailability.and.returnValue(ok([]));
    service.checkCustomerPo.and.returnValue(ok([]));
    service.getDeliveryPreview.and.returnValue(ok({ routesEnabled: false, canConfirm: true, deliveryCount: 0, lines: [], groups: [], blockers: [] }));
    const routes = jasmine.createSpyObj<FulfillmentRoutesService>('FulfillmentRoutesService', ['getRoutes']);
    routes.getRoutes.and.returnValue(ok([]));
    const partners = jasmine.createSpyObj<BusinessPartnerService>('BusinessPartnerService', ['getPartnerById']);
    partners.getPartnerById.and.returnValue(ok({ uuid: 'p-1', companyName: 'Cool Air Ltd' }));
    const invoices = jasmine.createSpyObj<SalesInvoiceService>('SalesInvoiceService', ['getInvoices', 'getInvoice']);
    invoices.getInvoices.and.returnValue(ok({ data: [], totalRecords: 0, page: 1, pageSize: 100, totalPages: 0 }));
    const addresses = jasmine.createSpyObj<AddressService>('AddressService', ['getAddress']);
    addresses.getAddress.and.returnValue(ok(null));
    const timeline = jasmine.createSpyObj<TimelineService>('TimelineService', ['getByTraceId', 'getByDocument']);
    timeline.getByTraceId.and.returnValue(ok(null));
    const production = jasmine.createSpyObj<ProductionOrderService>('ProductionOrderService', ['getList']);
    production.getList.and.returnValue(ok({ data: [], totalRecords: 0, page: 1, pageSize: 100, totalPages: 0 }));
    const attachments = jasmine.createSpyObj<AttachmentService>('AttachmentService', ['getAttachments', 'isApiUrl']);
    attachments.getAttachments.and.returnValue(ok([]));
    attachments.isApiUrl.and.returnValue(false);
    const policy = jasmine.createSpyObj<AttachmentPolicyService>('AttachmentPolicyService', ['ruleFor']);
    policy.ruleFor.and.returnValue(of(null));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [SaleOrderDetailComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), MessageService,
        provideTestOrgCurrencies(),
        { provide: SaleOrderService, useValue: service },
        { provide: FulfillmentRoutesService, useValue: routes },
        { provide: LogisticsService, useValue: jasmine.createSpyObj('LogisticsService', ['getDeliveryById']) },
        { provide: BusinessPartnerService, useValue: partners },
        { provide: SalesInvoiceService, useValue: invoices },
        { provide: AddressService, useValue: addresses },
        { provide: TimelineService, useValue: timeline },
        { provide: ProductionOrderService, useValue: production },
        { provide: AttachmentService, useValue: attachments },
        { provide: AttachmentPolicyService, useValue: policy },
        { provide: AuthService, useValue: { hasPermission: (c: string) => perms.includes(c) } },
        { provide: TenantService, useValue: { tenant: signal({ id: 'org-1', enabledFeatureCodes: ['MODULE_DEMAND', 'MODULE_SERVICES'] }) } },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['uuid', UUID]]) } } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(SaleOrderDetailComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  it('shows a service pill instead of delivery info on service lines, toned by the service order status', async () => {
    await setup(order());
    const pills = qa('line-service-indicator');
    expect(pills.map(text)).toEqual(['Service completed', 'Service waiting', 'Service pending']);
    expect(pills[0].classList).toContain('ok');
    expect(pills[1].classList).toContain('wn');
    for (const t of ['ok', 'wn', 'in', 'er']) expect(pills[2].classList).withContext('pending is grey').not.toContain(t);
    // The goods line keeps its delivery-date popover; the three service lines do not.
    expect(qa('line-delivery').length).toBe(4);
    expect(fixture.nativeElement.querySelectorAll('app-lead-time-popover').length).toBe(1);
  });

  it('maps every service status to its indicator', async () => {
    await setup(order());
    const l2 = component.order!.lines[1];
    const tone = (status: string) => {
      component.order!.serviceOrders = [{ serviceOrderUuid: 's', serviceNumber: 'SVC', soLineUuid: 'l2', status, quantity: 1 }];
      return component.serviceIndicator(l2);
    };
    expect(tone('DRAFT')).toEqual({ label: 'Service pending', tone: '' });
    expect(tone('PLANNED')).toEqual({ label: 'Service pending', tone: '' });
    expect(tone('IN_PROGRESS')).toEqual({ label: 'Service in progress', tone: 'in' });
    expect(tone('MATERIAL_PENDING')).toEqual({ label: 'Service waiting', tone: 'wn' });
    expect(tone('CLOSED')).toEqual({ label: 'Service completed', tone: 'ok' });
    expect(tone('CANCELLED')).toEqual({ label: 'Service cancelled', tone: 'er' });
  });

  it('links each service line to its service order when the user may view them', async () => {
    await setup(order());
    const links = qa('line-service-link') as HTMLAnchorElement[];
    expect(links.length).toBe(2);
    expect(text(links[0])).toBe('View service order');
    expect(links[0].getAttribute('href')).toContain('/portal/pages/services/service-orders/s-2');
  });

  it('shows the number without a link to someone without SERVICE_ORDER_VIEW', async () => {
    await setup(order(), ['SALE_ORDER_VIEW']);
    expect(qa('line-service-link').length).toBe(0);
    expect(fixture.nativeElement.textContent).toContain('SVC-2026-0001');
  });

  it('sums the service orders in the Fulfilment panel: completed + closed of all', async () => {
    await setup(order());
    expect(text(q('service-figures'))).toContain('1 of 2 completed');
    expect(text(q('service-percent'))).toBe('50%');
    expect(qa('service-row').length).toBe(2);
  });

  it('has no service summary for an order without service orders', async () => {
    await setup(order({ serviceOrders: [], lines: [line()] }));
    expect(q('service-summary')).toBeNull();
    expect(qa('line-service-indicator').length).toBe(0);
  });

  it('treats a service line as route-exempt', () => {
    expect(lineRouteDisplay({ isService: true, routeSource: 'NONE' }).kind).toBe('exempt');
  });
});
