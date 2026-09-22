import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute, Router } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError, Subject } from 'rxjs';

import { SaleOrderDetailComponent } from './sale-order-detail.component';
import { SaleOrderService, SaleOrderModel, SaleOrderLineModel } from '../../../../services/sale-order.service';
import { BusinessPartnerService } from '../../../../services/business-partner.service';
import {
  SalesInvoiceService, SalesInvoiceListItemModel, SalesInvoiceDetailModel, SalesInvoicePaymentModel
} from '../../../../services/sales-invoice.service';
import { AddressService } from '../../../../services/address.service';
import { TimelineService } from '../../../../services/timeline.service';
import { AddressModel, DeliveryListItemModel } from '../../../../services/logistics.service';
import { AuthService } from '../../../service/auth.service';

const UUID = '11111111-1111-1111-1111-111111111111';

const ALL_PERMISSIONS = [
  'SALE_ORDER_VIEW', 'SALE_ORDER_EDIT', 'SALE_ORDER_CONFIRM', 'SALE_ORDER_CANCEL',
  'DELIVERY_VIEW', 'DELIVERY_CREATE', 'SALES_INVOICE_VIEW'
];

function line(overrides: Partial<SaleOrderLineModel> = {}): SaleOrderLineModel {
  return {
    uuid: 'l1', variantUuid: 'v1', itemDescription: '4mm cable (CAB-4MM)', variantSku: 'CAB-4MM', unitOfMeasure: 'M',
    quantity: 100, unitPrice: 40, discountPercent: 0, taxPercent: 0, lineTotal: 4000,
    fulfilledQty: 0, invoicedQty: 0, fulfillmentMode: 'IN_STOCK', status: 'RESERVED',
    ...overrides
  };
}

function order(overrides: Partial<SaleOrderModel> = {}): SaleOrderModel {
  return {
    uuid: UUID, traceId: 't-1', soNumber: 'SO-2026-00042', partnerId: 'p-1',
    orderDate: '2026-09-01T00:00:00Z', currencyId: 'c-1',
    subtotal: 4000, taxAmount: 0, discountAmount: 0, grandTotal: 4000,
    status: 'CONFIRMED', requiresShipment: true, deliveryMode: 'SHIP',
    createdDate: '2026-09-01T00:00:00Z', lines: [line()],
    ...overrides
  };
}

function delivery(overrides: Partial<DeliveryListItemModel> = {}): DeliveryListItemModel {
  return {
    uuid: 'd1', deliveryNumber: 'DLV-2026-00001', direction: 'OUTBOUND', sourceType: 'SALE_ORDER',
    sourceNumber: 'SO-2026-00042', deliveryMode: 'SHIP', status: 'GOODS_ISSUED', priority: 'NORMAL',
    lineCount: 1, linesUnknown: false, createdDate: '2026-09-02T00:00:00Z',
    ...overrides
  };
}

function invoice(overrides: Partial<SalesInvoiceListItemModel> = {}): SalesInvoiceListItemModel {
  return {
    uuid: 'i1', invoiceNumber: 'INV-2026-00001', saleOrderUuid: UUID, saleOrderNumber: 'SO-2026-00042',
    partnerId: 'p-1', partnerName: 'Acme Ltd', invoiceDate: '2026-09-03T00:00:00Z', dueDate: '2026-10-03T00:00:00Z',
    grandTotal: 1000, amountPaid: 0, balanceDue: 1000, status: 'ISSUED', currencyCode: 'PKR',
    ...overrides
  };
}

function payment(overrides: Partial<SalesInvoicePaymentModel> = {}): SalesInvoicePaymentModel {
  return {
    allocationUuid: 'a1', paymentUuid: 'pay1', paymentNumber: 'RCPT-0001', paymentDate: '2026-09-10T00:00:00Z',
    paymentMethod: 'BANK_TRANSFER', paymentStatus: 'POSTED', allocatedAmount: 400, allocatedAt: '2026-09-10T08:00:00Z',
    allocatedBy: 1,
    ...overrides
  };
}

function invoiceDetail(base: Partial<SalesInvoiceListItemModel>, payments: SalesInvoicePaymentModel[]): SalesInvoiceDetailModel {
  return {
    ...invoice(base), traceId: 't-1', subtotal: 0, discountAmount: 0, taxAmount: 0,
    createdDate: '2026-09-03T00:00:00Z', lines: [], payments
  };
}

const address: AddressModel = {
  uuid: 'addr-1', line1: 'Plot 12', cityName: 'Karachi', postalCode: '74900', countryName: 'Pakistan'
} as AddressModel;

function ok<T>(result: T) {
  return of({ success: true, message: '', result } as any);
}

function page<T>(data: T[]) {
  return ok({ data, totalRecords: data.length, page: 1, pageSize: 100, totalPages: 1 });
}

describe('SaleOrderDetailComponent', () => {
  let fixture: ComponentFixture<SaleOrderDetailComponent>;
  let component: SaleOrderDetailComponent;
  let service: jasmine.SpyObj<SaleOrderService>;
  let partners: jasmine.SpyObj<BusinessPartnerService>;
  let invoices: jasmine.SpyObj<SalesInvoiceService>;
  let addresses: jasmine.SpyObj<AddressService>;
  let timeline: jasmine.SpyObj<TimelineService>;
  let router: Router;
  let permissions: string[];

  const auth = { hasPermission: (code: string) => permissions.includes(code) } as unknown as AuthService;

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  async function setup(model: SaleOrderModel | null = order(), deliveries: DeliveryListItemModel[] = []) {
    service = jasmine.createSpyObj<SaleOrderService>('SaleOrderService',
      ['getSaleOrderById', 'getDeliveries', 'createDelivery', 'confirmSaleOrder', 'cancelSaleOrder', 'getAvailability']);
    service.getSaleOrderById.and.returnValue(
      model ? ok(model) : of({ success: false, message: 'not found', result: null } as any));
    service.getDeliveries.and.returnValue(ok(deliveries));
    service.createDelivery.and.returnValue(ok('new-delivery-uuid'));
    service.confirmSaleOrder.and.returnValue(ok(null));
    service.cancelSaleOrder.and.returnValue(ok(null));
    service.getAvailability.and.returnValue(ok([]));

    partners = jasmine.createSpyObj<BusinessPartnerService>('BusinessPartnerService', ['getPartnerById']);
    partners.getPartnerById.and.returnValue(ok({ uuid: 'p-1', companyName: 'Acme Ltd' }));

    invoices = jasmine.createSpyObj<SalesInvoiceService>('SalesInvoiceService', ['getInvoices', 'getInvoice', 'downloadPdf']);
    invoices.getInvoices.and.returnValue(page<SalesInvoiceListItemModel>([]));
    invoices.getInvoice.and.returnValue(ok(invoiceDetail({}, [])));

    addresses = jasmine.createSpyObj<AddressService>('AddressService', ['getAddress']);
    addresses.getAddress.and.returnValue(ok(address));

    timeline = jasmine.createSpyObj<TimelineService>('TimelineService', ['getByTraceId', 'getByDocument']);
    timeline.getByTraceId.and.returnValue(ok({
      traceId: 't-1', firstEventAt: '2026-09-01T00:00:00Z', lastEventAt: '2026-09-01T00:00:00Z', totalEventCount: 1,
      events: [{ eventType: 'SO_CREATED', interfaceCode: 'SO', documentId: UUID, documentNumber: 'SO-2026-00042', occurredAt: '2026-09-01T00:00:00Z' }]
    }));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [SaleOrderDetailComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), MessageService,
        { provide: SaleOrderService, useValue: service },
        { provide: BusinessPartnerService, useValue: partners },
        { provide: SalesInvoiceService, useValue: invoices },
        { provide: AddressService, useValue: addresses },
        { provide: TimelineService, useValue: timeline },
        { provide: AuthService, useValue: auth },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['uuid', UUID]]) } } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(SaleOrderDetailComponent);
    component = fixture.componentInstance;
    router = TestBed.inject(Router);
  }

  beforeEach(() => { permissions = [...ALL_PERMISSIONS]; });

  it('loads the order, its customer and its deliveries, and renders them', async () => {
    await setup(order(), [delivery()]);
    fixture.detectChanges();

    expect(service.getSaleOrderById).toHaveBeenCalledOnceWith(UUID);
    expect(service.getDeliveries).toHaveBeenCalledOnceWith(UUID);
    expect(partners.getPartnerById).toHaveBeenCalledOnceWith('p-1');

    const text = fixture.nativeElement.textContent;
    expect(text).toContain('SO-2026-00042');
    expect(text).toContain('Acme Ltd');
    expect(text).toContain('4mm cable (CAB-4MM)');
    expect(component.deliveries.length).toBe(1);
  });

  it('renders without a customer name when the partner lookup fails', async () => {
    await setup();
    partners.getPartnerById.and.returnValue(throwError(() => ({ status: 500 })));
    fixture.detectChanges();

    expect(component.partnerName).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('SO-2026-00042');
  });

  it('shows a not-found state for a missing order', async () => {
    await setup(null);
    fixture.detectChanges();

    expect(query('not-found')).not.toBeNull();
    expect(service.getDeliveries).not.toHaveBeenCalled();
  });

  // ── Ship to ────────────────────────────────────────────────────────────────

  it('writes the shipping address out in words', async () => {
    await setup(order({ shippingAddressId: 'addr-1' }));
    fixture.detectChanges();

    expect(addresses.getAddress).toHaveBeenCalledOnceWith('addr-1');
    expect(query('ship-to')!.textContent).toContain('Plot 12, Karachi 74900, Pakistan');
  });

  it('says an address is on file when it cannot be read, and shows none for a pickup', async () => {
    await setup(order({ shippingAddressId: 'addr-1' }));
    addresses.getAddress.and.returnValue(throwError(() => ({ status: 403 })));
    fixture.detectChanges();
    expect(query('ship-to')!.textContent).toContain('Address on file');

    await setup(order({ deliveryMode: 'SELF_PICKUP', shippingAddressId: undefined }));
    fixture.detectChanges();
    expect(query('ship-to')).toBeNull();
    expect(addresses.getAddress).not.toHaveBeenCalled();
  });

  // ── Fulfilment figures ─────────────────────────────────────────────────────

  it('sums fulfilment over the open lines, capped at what was ordered', async () => {
    await setup(order({ lines: [
      line({ uuid: 'l1', quantity: 100, fulfilledQty: 40 }),
      line({ uuid: 'l2', quantity: 10, fulfilledQty: 12 }),
      line({ uuid: 'l3', quantity: 50, fulfilledQty: 0, status: 'CANCELLED' })
    ]}));
    fixture.detectChanges();

    expect(component.orderedQty).toBe(110);
    expect(component.fulfilledQty).toBe(50);
    expect(component.fulfilledPercent).toBe(45);
  });

  // ── Actions: what shows, and to whom ───────────────────────────────────────

  it('offers edit and confirm on a draft to someone who may, and no delivery', async () => {
    await setup(order({ status: 'DRAFT' }));
    fixture.detectChanges();

    expect(query('action-edit')).not.toBeNull();
    expect(query('action-confirm')).not.toBeNull();
    expect(query('action-cancel')).not.toBeNull();
    expect(query('action-create-delivery')).toBeNull();
  });

  it('offers cancel but neither edit nor confirm once the order is confirmed', async () => {
    await setup(order({ status: 'CONFIRMED' }));
    fixture.detectChanges();

    expect(query('action-edit')).toBeNull();
    expect(query('action-confirm')).toBeNull();
    expect(query('action-cancel')).not.toBeNull();
  });

  it('offers cancel while the order is open, and never once it is finished with', async () => {
    for (const status of ['DRAFT', 'CONFIRMED', 'PARTIALLY_FULFILLED', 'FULFILLED']) {
      await setup(order({ status }));
      fixture.detectChanges();
      expect(component.canCancel).withContext(status).toBeTrue();
    }
    for (const status of ['INVOICED', 'CLOSED', 'CANCELLED']) {
      await setup(order({ status }));
      fixture.detectChanges();
      expect(component.canCancel).withContext(status).toBeFalse();
      expect(query('action-cancel')).withContext(status).toBeNull();
    }
  });

  it('hides each action from someone without its permission', async () => {
    permissions = ['SALE_ORDER_VIEW'];
    await setup(order({ status: 'DRAFT' }));
    fixture.detectChanges();

    expect(query('action-edit')).toBeNull();
    expect(query('action-confirm')).toBeNull();
    expect(query('action-cancel')).toBeNull();

    permissions = ['SALE_ORDER_VIEW', 'SALE_ORDER_CONFIRM'];
    await setup(order({ status: 'DRAFT' }));
    fixture.detectChanges();
    expect(query('action-confirm')).not.toBeNull();
    expect(query('action-edit')).withContext('confirming is not editing').toBeNull();
    expect(query('action-cancel')).withContext('confirming is not cancelling').toBeNull();
  });

  // ── Confirm ────────────────────────────────────────────────────────────────

  it('shows what confirming would find before it asks, and names each line', async () => {
    await setup(order({ status: 'DRAFT', lines: [line({ variantUuid: 'v1' }), line({ uuid: 'l2', variantUuid: 'v2', itemDescription: '2mm cable' })] }));
    service.getAvailability.and.returnValue(ok([
      { variantUuid: 'v1', orderedQty: 100, availableQty: 100, deficitQty: 0, warehouseName: 'Main' },
      { variantUuid: 'v2', orderedQty: 50, availableQty: 20, deficitQty: 30, warehouseName: 'Main' }
    ]));
    fixture.detectChanges();

    component.openConfirmDialog();
    fixture.detectChanges();

    expect(service.getAvailability).toHaveBeenCalledOnceWith(UUID);
    expect(component.availability.map(a => a.description)).toEqual(['4mm cable (CAB-4MM)', '2mm cable']);
    expect(component.shortfalls.map(a => a.variantUuid)).toEqual(['v2']);
    expect(service.confirmSaleOrder).withContext('nothing is confirmed by looking').not.toHaveBeenCalled();
  });

  it('warns about shortfalls only when there are some', async () => {
    await setup(order({ status: 'DRAFT' }));
    service.getAvailability.and.returnValue(ok([{ variantUuid: 'v1', orderedQty: 100, availableQty: 100, deficitQty: 0 }]));
    fixture.detectChanges();
    component.openConfirmDialog();
    fixture.detectChanges();
    expect(component.shortfalls).toEqual([]);
    expect(document.body.querySelector('[data-testid="shortfall-note"]')).toBeNull();

    service.getAvailability.and.returnValue(ok([{ variantUuid: 'v1', orderedQty: 100, availableQty: 60, deficitQty: 40 }]));
    component.openConfirmDialog();
    fixture.detectChanges();
    expect(document.body.querySelector('[data-testid="shortfall-note"]')!.textContent).toContain('1 line is short');
  });

  it('still lets the order be confirmed when the stock check fails', async () => {
    await setup(order({ status: 'DRAFT' }));
    service.getAvailability.and.returnValue(throwError(() => ({ status: 500 })));
    fixture.detectChanges();

    component.openConfirmDialog();
    fixture.detectChanges();

    expect(component.availabilityFailed).toBeTrue();
    expect(component.isLoadingAvailability).toBeFalse();
    expect(document.body.querySelector('[data-testid="availability-failed"]')).not.toBeNull();

    component.confirmOrder();
    expect(service.confirmSaleOrder).toHaveBeenCalledOnceWith(UUID);
  });

  it('confirms the order, closes the dialog and reloads it', async () => {
    await setup(order({ status: 'DRAFT' }));
    fixture.detectChanges();
    const messages = fixture.debugElement.injector.get(MessageService);
    const add = spyOn(messages, 'add');

    component.openConfirmDialog();
    component.confirmOrder();

    expect(service.confirmSaleOrder).toHaveBeenCalledOnceWith(UUID);
    expect(component.confirmDialogVisible).toBeFalse();
    expect(component.isConfirming).toBeFalse();
    expect(service.getSaleOrderById).withContext('reloaded to show the new status').toHaveBeenCalledTimes(2);
    expect(add.calls.mostRecent().args[0].severity).toBe('success');
  });

  it('shows the servers reason when confirming is refused, and leaves the dialog open', async () => {
    await setup(order({ status: 'DRAFT' }));
    fixture.detectChanges();
    const messages = fixture.debugElement.injector.get(MessageService);
    const add = spyOn(messages, 'add');
    service.confirmSaleOrder.and.returnValue(throwError(() => ({ error: { message: 'Line 1 has no price.' } })));

    component.openConfirmDialog();
    component.confirmOrder();

    expect(add.calls.mostRecent().args[0].detail).toBe('Line 1 has no price.');
    expect(component.confirmDialogVisible).toBeTrue();
    expect(component.isConfirming).toBeFalse();
    expect(service.getSaleOrderById).toHaveBeenCalledTimes(1);
  });

  it('confirms once however often the button is pressed while it is working', async () => {
    await setup(order({ status: 'DRAFT' }));
    fixture.detectChanges();
    const pending = new Subject<any>();
    service.confirmSaleOrder.and.returnValue(pending);

    component.openConfirmDialog();
    component.confirmOrder();
    component.confirmOrder();

    expect(service.confirmSaleOrder).toHaveBeenCalledTimes(1);
  });

  it('will not confirm for someone without the permission, or an order that is not a draft', async () => {
    permissions = ['SALE_ORDER_VIEW'];
    await setup(order({ status: 'DRAFT' }));
    fixture.detectChanges();
    component.openConfirmDialog();
    component.confirmOrder();
    expect(component.confirmDialogVisible).toBeFalse();
    expect(service.getAvailability).not.toHaveBeenCalled();
    expect(service.confirmSaleOrder).not.toHaveBeenCalled();

    permissions = [...ALL_PERMISSIONS];
    await setup(order({ status: 'CONFIRMED' }));
    fixture.detectChanges();
    component.confirmOrder();
    expect(service.confirmSaleOrder).not.toHaveBeenCalled();
  });

  // ── Cancel ─────────────────────────────────────────────────────────────────

  it('cancels with the reason given, trimmed, and reloads', async () => {
    await setup(order({ status: 'CONFIRMED' }));
    fixture.detectChanges();

    component.openCancelDialog();
    component.cancelReason = '  Customer withdrew  ';
    component.cancelOrder();

    expect(service.cancelSaleOrder).toHaveBeenCalledOnceWith(UUID, 'Customer withdrew');
    expect(component.cancelDialogVisible).toBeFalse();
    expect(service.getSaleOrderById).toHaveBeenCalledTimes(2);
  });

  it('cancels without a reason when none is given, and starts each dialog empty', async () => {
    await setup(order({ status: 'CONFIRMED' }));
    fixture.detectChanges();

    component.openCancelDialog();
    component.cancelReason = '   ';
    component.cancelOrder();
    expect(service.cancelSaleOrder).toHaveBeenCalledOnceWith(UUID, undefined);

    component.cancelReason = 'left over';
    component.openCancelDialog();
    expect(component.cancelReason).toBe('');
  });

  it('shows the servers reason when cancelling is refused, and leaves the dialog open', async () => {
    await setup(order({ status: 'CONFIRMED' }));
    fixture.detectChanges();
    const messages = fixture.debugElement.injector.get(MessageService);
    const add = spyOn(messages, 'add');
    service.cancelSaleOrder.and.returnValue(throwError(() => ({ error: { message: 'A delivery has already left.' } })));

    component.openCancelDialog();
    component.cancelOrder();

    expect(add.calls.mostRecent().args[0].detail).toBe('A delivery has already left.');
    expect(component.cancelDialogVisible).toBeTrue();
    expect(component.isCancelling).toBeFalse();
  });

  it('will not cancel an order that is finished with, or for someone without the permission', async () => {
    await setup(order({ status: 'CLOSED' }));
    fixture.detectChanges();
    component.openCancelDialog();
    component.cancelOrder();
    expect(component.cancelDialogVisible).toBeFalse();
    expect(service.cancelSaleOrder).not.toHaveBeenCalled();

    permissions = ['SALE_ORDER_VIEW'];
    await setup(order({ status: 'CONFIRMED' }));
    fixture.detectChanges();
    component.openCancelDialog();
    component.cancelOrder();
    expect(service.cancelSaleOrder).not.toHaveBeenCalled();
  });

  // ── Create delivery ────────────────────────────────────────────────────────

  it('offers a delivery only while the order still has something to send', async () => {
    await setup(order({ status: 'CONFIRMED' }));
    fixture.detectChanges();
    expect(component.canCreateDelivery).toBeTrue();
    expect(query('action-create-delivery')).not.toBeNull();

    await setup(order({ status: 'DRAFT' }));
    fixture.detectChanges();
    expect(component.canCreateDelivery).withContext('a draft has reserved nothing').toBeFalse();

    await setup(order({ status: 'FULFILLED', lines: [line({ fulfilledQty: 100 })] }));
    fixture.detectChanges();
    expect(component.canCreateDelivery).toBeFalse();
    expect(query('action-create-delivery')).toBeNull();
  });

  it('does not offer a delivery to someone who may not create one', async () => {
    permissions = permissions.filter(p => p !== 'DELIVERY_CREATE');
    await setup(order({ status: 'CONFIRMED' }));
    fixture.detectChanges();

    expect(component.canCreateDelivery).toBeFalse();
    expect(query('action-create-delivery')).toBeNull();
  });

  it('proposes every deliverable line in full, leaving out what cannot go', async () => {
    await setup(order({ status: 'PARTIALLY_FULFILLED', deliveryMode: 'SELF_PICKUP', lines: [
      line({ uuid: 'open',      quantity: 100, fulfilledQty: 40 }),
      line({ uuid: 'done',      quantity: 10,  fulfilledQty: 10, status: 'FULFILLED' }),
      line({ uuid: 'cancelled', quantity: 5,   status: 'CANCELLED' }),
      line({ uuid: 'dropship',  quantity: 1,   fulfillmentMode: 'DROP_SHIP', status: 'OPEN' })
    ]}));
    fixture.detectChanges();

    component.openCreateDialog();

    expect(component.deliveryMode).toBe('SELF_PICKUP');
    expect(component.choices.map(c => c.line.uuid)).toEqual(['open']);
    expect(component.choices[0].qty).toBe(60);
    expect(component.choices[0].include).toBeTrue();
    expect(component.canSubmitDelivery).toBeTrue();
  });

  it('sends the chosen lines and quantities and opens the new delivery', async () => {
    await setup(order({ lines: [
      line({ uuid: 'l1', quantity: 100 }),
      line({ uuid: 'l2', quantity: 40 })
    ]}));
    fixture.detectChanges();
    const navigate = spyOn(router, 'navigate').and.resolveTo(true);

    component.openCreateDialog();
    component.deliveryMode = 'SELF_PICKUP';
    component.deliveryNotes = 'Customer collects Friday';
    component.choices[0].qty = 30;
    component.choices[1].include = false;
    component.submitDelivery();

    expect(service.createDelivery).toHaveBeenCalledOnceWith(UUID, {
      deliveryMode: 'SELF_PICKUP',
      notes: 'Customer collects Friday',
      lines: [{ sourceLineUuid: 'l1', qty: 30 }]
    });
    expect(navigate).toHaveBeenCalledWith(['/portal/pages/logistics/deliveries', 'new-delivery-uuid']);
    expect(component.createDialogVisible).toBeFalse();
  });

  it('refuses to submit nothing, or more than is outstanding', async () => {
    await setup(order({ lines: [line({ quantity: 100, fulfilledQty: 40 })] }));
    fixture.detectChanges();

    component.openCreateDialog();
    component.choices[0].qty = 61;
    expect(component.canSubmitDelivery).withContext('61 of 60 outstanding').toBeFalse();

    component.choices[0].qty = 60;
    component.choices[0].include = false;
    expect(component.canSubmitDelivery).withContext('nothing selected').toBeFalse();

    component.submitDelivery();
    expect(service.createDelivery).not.toHaveBeenCalled();
  });

  it('shows the server explanation when the delivery is refused', async () => {
    await setup();
    fixture.detectChanges();
    const messages = fixture.debugElement.injector.get(MessageService);
    const add = spyOn(messages, 'add');
    service.createDelivery.and.returnValue(throwError(() => ({
      error: { message: 'Line 1: only 60 is left to deliver, but 75 was requested.' }
    })));

    component.openCreateDialog();
    component.submitDelivery();

    expect((add.calls.mostRecent().args[0].detail as string)).toContain('only 60');
    expect(component.isSubmitting).toBeFalse();
  });

  // ── Deliveries tab ─────────────────────────────────────────────────────────

  it('lists the orders deliveries with a link to each', async () => {
    await setup(order(), [
      delivery({ uuid: 'd1', deliveryNumber: 'DLV-2026-00001', deliveryMode: 'SHIP', status: 'GOODS_ISSUED' }),
      delivery({ uuid: 'd2', deliveryNumber: 'DLV-2026-00002', deliveryMode: 'SELF_PICKUP', status: 'DELIVERED' })
    ]);
    fixture.detectChanges();

    expect(component.deliveries.map(d => d.deliveryNumber)).toEqual(['DLV-2026-00001', 'DLV-2026-00002']);
    expect(component.getDeliverySeverity('DELIVERED')).toBe('success');
  });

  it('does not ask for deliveries, and says so, when the user may not see them', async () => {
    permissions = permissions.filter(p => p !== 'DELIVERY_VIEW');
    await setup(order(), [delivery()]);
    fixture.detectChanges();

    expect(service.getDeliveries).not.toHaveBeenCalled();
    expect(query('deliveries-no-access')).not.toBeNull();
    expect(query('deliveries-table')).toBeNull();
  });

  // ── Invoices and payments ──────────────────────────────────────────────────

  it('asks for nothing about invoices until the Invoices or Payments tab is opened', async () => {
    await setup();
    fixture.detectChanges();

    expect(invoices.getInvoices).not.toHaveBeenCalled();
    expect(invoices.getInvoice).not.toHaveBeenCalled();
  });

  it('loads the orders invoices once, however many times its tabs are opened', async () => {
    await setup();
    invoices.getInvoices.and.returnValue(page([invoice({ uuid: 'i1' })]));
    invoices.getInvoice.and.returnValue(ok(invoiceDetail({ uuid: 'i1' }, [payment()])));
    fixture.detectChanges();

    component.onTabChange({ index: 2 } as any);
    component.onTabChange({ index: 3 } as any);
    component.onTabChange({ index: 2 } as any);

    expect(invoices.getInvoices).toHaveBeenCalledOnceWith({ saleOrderUuid: UUID, pageSize: 100 });
    expect(invoices.getInvoice).toHaveBeenCalledTimes(1);
  });

  it('other tabs do not load invoices', async () => {
    await setup();
    fixture.detectChanges();

    component.onTabChange({ index: 0 } as any);
    component.onTabChange({ index: 1 } as any);
    component.onTabChange({ index: 4 } as any);

    expect(invoices.getInvoices).not.toHaveBeenCalled();
  });

  it('totals only invoices that stand, a currency at a time', () => {
    const totals = SaleOrderDetailComponent.prototype.totalsOf.call({}, [
      invoice({ status: 'ISSUED',    currencyCode: 'PKR', grandTotal: 1000, amountPaid: 0,   balanceDue: 1000 }),
      invoice({ status: 'PAID',      currencyCode: 'PKR', grandTotal: 500,  amountPaid: 500, balanceDue: 0 }),
      invoice({ status: 'DRAFT',     currencyCode: 'PKR', grandTotal: 9999, amountPaid: 0,   balanceDue: 9999 }),
      invoice({ status: 'CANCELLED', currencyCode: 'PKR', grandTotal: 7777, amountPaid: 0,   balanceDue: 7777 }),
      invoice({ status: 'OVERDUE',   currencyCode: 'USD', grandTotal: 20,   amountPaid: 5,   balanceDue: 15 }),
      invoice({ status: 'PARTIALLY_PAID', currencyCode: 'USD', grandTotal: 30, amountPaid: 10, balanceDue: 20 })
    ]);

    expect(totals).toEqual([
      { currencyCode: 'PKR', invoiced: 1500, paid: 500, balance: 1000 },
      { currencyCode: 'USD', invoiced: 50,   paid: 15,  balance: 35 }
    ]);
  });

  it('shows the totals strip and one row per invoice on the Invoices tab', async () => {
    await setup();
    invoices.getInvoices.and.returnValue(page([
      invoice({ uuid: 'i1', invoiceNumber: 'INV-2026-00001' }),
      invoice({ uuid: 'i2', invoiceNumber: 'INV-2026-00002', status: 'PAID', amountPaid: 1000, balanceDue: 0 })
    ]));
    invoices.getInvoice.and.callFake((uuid: string) => ok(invoiceDetail({ uuid }, [])));
    fixture.detectChanges();

    component.onTabChange({ index: 2 } as any);
    fixture.detectChanges();

    expect(component.invoices.map(i => i.invoiceNumber)).toEqual(['INV-2026-00001', 'INV-2026-00002']);
    const text = query('invoices-table')!.textContent!;
    expect(text).toContain('INV-2026-00001');
    expect(text).toContain('INV-2026-00002');
    expect(query('invoice-totals')!.textContent).toContain('2,000.00');
    expect(query('invoice-totals')!.textContent).toContain('PKR');
  });

  it('does not fetch the detail of a draft invoice, which has nothing paid against it', async () => {
    await setup();
    invoices.getInvoices.and.returnValue(page([
      invoice({ uuid: 'draft', status: 'DRAFT' }),
      invoice({ uuid: 'issued', status: 'ISSUED' })
    ]));
    invoices.getInvoice.and.returnValue(ok(invoiceDetail({ uuid: 'issued' }, [])));
    fixture.detectChanges();

    component.ensureInvoicesLoaded();

    expect(invoices.getInvoice).toHaveBeenCalledOnceWith('issued');
  });

  it('gathers every payment applied to any invoice, oldest first, naming the invoice it went to', async () => {
    await setup();
    invoices.getInvoices.and.returnValue(page([
      invoice({ uuid: 'i1', invoiceNumber: 'INV-1', currencyCode: 'PKR' }),
      invoice({ uuid: 'i2', invoiceNumber: 'INV-2', currencyCode: 'USD' })
    ]));
    invoices.getInvoice.and.callFake((uuid: string) => ok(uuid === 'i1'
      ? invoiceDetail({ uuid, invoiceNumber: 'INV-1', currencyCode: 'PKR' }, [
          payment({ paymentNumber: 'RCPT-LATE', paymentDate: '2026-09-20T00:00:00Z', allocatedAmount: 300 })])
      : invoiceDetail({ uuid, invoiceNumber: 'INV-2', currencyCode: 'USD' }, [
          payment({ paymentNumber: 'RCPT-EARLY', paymentDate: '2026-09-05T00:00:00Z', allocatedAmount: 20 })])));
    fixture.detectChanges();

    component.onTabChange({ index: 3 } as any);
    fixture.detectChanges();

    expect(component.payments.map(p => [p.paymentNumber, p.invoiceNumber, p.currencyCode]))
      .toEqual([['RCPT-EARLY', 'INV-2', 'USD'], ['RCPT-LATE', 'INV-1', 'PKR']]);
    expect(query('payments-table')!.textContent).toContain('RCPT-EARLY');
    expect(component.invoicesFailed).toBeFalse();
  });

  it('keeps the payments it could read and says the list may be incomplete when an invoice cannot be read', async () => {
    await setup();
    invoices.getInvoices.and.returnValue(page([invoice({ uuid: 'i1', invoiceNumber: 'INV-1' }), invoice({ uuid: 'i2', invoiceNumber: 'INV-2' })]));
    invoices.getInvoice.and.callFake((uuid: string) => uuid === 'i2'
      ? throwError(() => ({ status: 500 }))
      : ok(invoiceDetail({ uuid, invoiceNumber: 'INV-1' }, [payment()])));
    fixture.detectChanges();

    component.onTabChange({ index: 3 } as any);
    fixture.detectChanges();

    expect(component.payments.length).toBe(1);
    expect(component.invoicesFailed).toBeTrue();
    expect(component.isLoadingInvoices).toBeFalse();
    expect(fixture.nativeElement.textContent).toContain('may be incomplete');
  });

  it('empties the tabs, says so, and asks again next time when the invoices cannot be loaded', async () => {
    await setup();
    invoices.getInvoices.and.returnValue(throwError(() => ({ status: 500 })));
    fixture.detectChanges();

    component.onTabChange({ index: 2 } as any);
    fixture.detectChanges();

    expect(component.invoicesFailed).toBeTrue();
    expect(component.isLoadingInvoices).toBeFalse();
    expect(query('invoices-table')!.textContent).toContain('could not be loaded');

    invoices.getInvoices.and.returnValue(page([]));
    component.onTabChange({ index: 3 } as any);
    expect(invoices.getInvoices).toHaveBeenCalledTimes(2);
    expect(component.invoicesFailed).toBeFalse();
  });

  it('says nothing has been invoiced when there are no invoices, without an error', async () => {
    await setup();
    fixture.detectChanges();

    component.onTabChange({ index: 2 } as any);
    fixture.detectChanges();

    expect(component.invoicesFailed).toBeFalse();
    expect(query('invoices-table')!.textContent).toContain('Nothing has been invoiced');
  });

  it('loads invoices again after the order changes, since it may have billed something', async () => {
    await setup(order({ status: 'DRAFT' }));
    fixture.detectChanges();
    component.onTabChange({ index: 2 } as any);
    expect(invoices.getInvoices).toHaveBeenCalledTimes(1);

    component.openConfirmDialog();
    component.confirmOrder();

    expect(invoices.getInvoices).withContext('the tab is showing, so it reloads').toHaveBeenCalledTimes(2);
  });

  it('does not ask for invoices, and says so, when the user may not see them', async () => {
    permissions = permissions.filter(p => p !== 'SALES_INVOICE_VIEW');
    await setup();
    fixture.detectChanges();

    component.onTabChange({ index: 2 } as any);
    component.onTabChange({ index: 3 } as any);
    fixture.detectChanges();

    expect(invoices.getInvoices).not.toHaveBeenCalled();
    expect(query('invoices-no-access')).not.toBeNull();
    expect(query('payments-no-access')).not.toBeNull();
    expect(query('invoices-table')).toBeNull();
    expect(query('payments-table')).toBeNull();
  });

  it('opens an invoice PDF in a new tab, and says so when it cannot', async () => {
    await setup();
    fixture.detectChanges();
    const messages = fixture.debugElement.injector.get(MessageService);
    const add = spyOn(messages, 'add');
    const open = spyOn(window, 'open');
    spyOn(URL, 'createObjectURL').and.returnValue('blob:pdf');
    invoices.downloadPdf.and.returnValue(of(new Blob(['%PDF'])));

    component.downloadInvoicePdf(invoice({ uuid: 'i1' }));
    expect(invoices.downloadPdf).toHaveBeenCalledOnceWith('i1');
    expect(open).toHaveBeenCalledWith('blob:pdf', '_blank');

    invoices.downloadPdf.and.returnValue(throwError(() => ({ status: 500 })));
    component.downloadInvoicePdf(invoice({ uuid: 'i1' }));
    expect(add.calls.mostRecent().args[0].severity).toBe('error');
  });

  it('marks a payment that bounced or was reversed, and one that stands', async () => {
    await setup();

    expect(component.getPaymentSeverity('POSTED')).toBe('success');
    expect(component.getPaymentSeverity('RECEIVED')).toBe('success');
    expect(component.getPaymentSeverity('BOUNCED')).toBe('danger');
    expect(component.getPaymentSeverity('REVERSED')).toBe('danger');
    expect(component.getPaymentSeverity('PENDING')).toBe('warn');
  });

  it('maps every invoice status the server can return to a severity', async () => {
    await setup();

    expect(component.getInvoiceSeverity('PAID')).toBe('success');
    expect(component.getInvoiceSeverity('OVERDUE')).toBe('danger');
    expect(component.getInvoiceSeverity('PARTIALLY_PAID')).toBe('warn');
    expect(component.getInvoiceSeverity('SOMETHING_NEW')).toBe('secondary');
  });

  it('links each invoice to its own page', async () => {
    await setup();
    invoices.getInvoices.and.returnValue(page([invoice({ uuid: 'i1' })]));
    invoices.getInvoice.and.returnValue(ok(invoiceDetail({ uuid: 'i1' }, [])));
    fixture.detectChanges();

    component.onTabChange({ index: 2 } as any);
    fixture.detectChanges();

    expect(query('invoice-link')!.getAttribute('href')).toBe('/portal/pages/finance/sales-invoices/i1');
  });

  it('links each payment to its page only for someone who may open payments', async () => {
    const setUpWithPayment = async () => {
      await setup();
      invoices.getInvoices.and.returnValue(page([invoice({ uuid: 'i1' })]));
      invoices.getInvoice.and.returnValue(ok(invoiceDetail({ uuid: 'i1' }, [payment({ paymentUuid: 'pay-9' })])));
      fixture.detectChanges();
      component.onTabChange({ index: 3 } as any);
      fixture.detectChanges();
    };

    permissions = [...ALL_PERMISSIONS, 'CUSTOMER_PAYMENT_VIEW'];
    await setUpWithPayment();
    expect(query('payment-link')!.getAttribute('href')).toBe('/portal/pages/finance/customer-payments/pay-9');

    permissions = [...ALL_PERMISSIONS];
    await setUpWithPayment();
    expect(query('payment-link')).toBeNull();
    expect(query('payments-table')!.textContent).toContain('RCPT-0001');
  });

  // ── Timeline ───────────────────────────────────────────────────────────────

  it('does not fetch the timeline until its tab is opened, then shows it in place', async () => {
    await setup();
    fixture.detectChanges();
    expect(timeline.getByTraceId).not.toHaveBeenCalled();

    component.activeTab = 4;
    fixture.detectChanges();

    expect(timeline.getByTraceId).toHaveBeenCalledOnceWith('t-1');
    fixture.detectChanges();
    const inline = query('timeline-inline');
    expect(inline).not.toBeNull();
    expect(inline!.textContent).toContain('SO-2026-00042');
  });

  // ── Tabs present ───────────────────────────────────────────────────────────

  it('has the five tabs in order', async () => {
    await setup();
    fixture.detectChanges();

    const headers = Array.from(fixture.nativeElement.querySelectorAll('.p-tablist [role="tab"]')).map((e: any) => e.textContent.trim());
    expect(headers).toEqual(['Lines', 'Deliveries', 'Invoices', 'Payments', 'Timeline']);
  });
});
