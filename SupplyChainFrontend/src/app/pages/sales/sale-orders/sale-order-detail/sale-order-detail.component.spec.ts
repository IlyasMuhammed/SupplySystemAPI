import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute, Router } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { SaleOrderDetailComponent } from './sale-order-detail.component';
import { SaleOrderService, SaleOrderModel, SaleOrderLineModel } from '../../../../services/sale-order.service';
import { BusinessPartnerService } from '../../../../services/business-partner.service';
import { DeliveryListItemModel } from '../../../../services/logistics.service';

const UUID = '11111111-1111-1111-1111-111111111111';

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

function ok<T>(result: T) {
  return of({ success: true, message: '', result } as any);
}

describe('SaleOrderDetailComponent', () => {
  let fixture: ComponentFixture<SaleOrderDetailComponent>;
  let component: SaleOrderDetailComponent;
  let service: jasmine.SpyObj<SaleOrderService>;
  let partners: jasmine.SpyObj<BusinessPartnerService>;
  let router: Router;

  async function setup(model: SaleOrderModel | null = order(), deliveries: DeliveryListItemModel[] = []) {
    service = jasmine.createSpyObj<SaleOrderService>('SaleOrderService', ['getSaleOrderById', 'getDeliveries', 'createDelivery']);
    service.getSaleOrderById.and.returnValue(
      model ? ok(model) : of({ success: false, message: 'not found', result: null } as any));
    service.getDeliveries.and.returnValue(ok(deliveries));
    service.createDelivery.and.returnValue(ok('new-delivery-uuid'));
    partners = jasmine.createSpyObj<BusinessPartnerService>('BusinessPartnerService', ['getPartnerById']);
    partners.getPartnerById.and.returnValue(ok({ uuid: 'p-1', companyName: 'Acme Ltd' }));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [SaleOrderDetailComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), MessageService,
        { provide: SaleOrderService, useValue: service },
        { provide: BusinessPartnerService, useValue: partners },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['uuid', UUID]]) } } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(SaleOrderDetailComponent);
    component = fixture.componentInstance;
    router = TestBed.inject(Router);
  }

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

    expect(fixture.nativeElement.querySelector('[data-testid="not-found"]')).not.toBeNull();
    expect(service.getDeliveries).not.toHaveBeenCalled();
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

  // ── Create delivery ────────────────────────────────────────────────────────

  it('offers a delivery only while the order still has something to send', async () => {
    await setup(order({ status: 'CONFIRMED' }));
    fixture.detectChanges();
    expect(component.canCreateDelivery).toBeTrue();
    expect(fixture.nativeElement.querySelector('[data-testid="action-create-delivery"]')).not.toBeNull();

    await setup(order({ status: 'DRAFT' }));
    fixture.detectChanges();
    expect(component.canCreateDelivery).withContext('a draft has reserved nothing').toBeFalse();

    await setup(order({ status: 'FULFILLED', lines: [line({ fulfilledQty: 100 })] }));
    fixture.detectChanges();
    expect(component.canCreateDelivery).toBeFalse();
    expect(fixture.nativeElement.querySelector('[data-testid="action-create-delivery"]')).toBeNull();
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
});
