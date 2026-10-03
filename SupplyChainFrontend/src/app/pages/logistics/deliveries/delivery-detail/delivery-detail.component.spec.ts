import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute, Router } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { DeliveryDetailComponent, packagesAmendableWhileStaged } from './delivery-detail.component';
import {
  LogisticsService, DeliveryDetailModel, DeliveryNextAction, RouteStepProgressModel
} from '../../../../services/logistics.service';
import { SalesInvoiceService } from '../../../../services/sales-invoice.service';
import { AuthService } from '../../../service/auth.service';

const UUID = '11111111-1111-1111-1111-111111111111';

function detail(overrides: Partial<DeliveryDetailModel> = {}): DeliveryDetailModel {
  return {
    uuid: UUID,
    deliveryNumber: 'DLV-2026-00001',
    traceId: '99999999-9999-9999-9999-999999999999',
    direction: 'OUTBOUND',
    sourceType: 'MANUAL',
    postsGoodsIssue: true,
    priority: 'NORMAL',
    status: 'DRAFT',
    linesUnknown: false,
    allowedNextStatuses: ['RELEASED', 'CANCELLED'],
    consignments: [],
    createdDate: '2026-09-01T00:00:00Z',
    lines: [
      {
        uuid: 'l1', lineNo: 1, itemDescription: '4mm cable', unitOfMeasure: 'M',
        qtyOrdered: 100, qtyPicked: 0, qtyPacked: 0, qtyShipped: 0, qtyDelivered: 0, qtyShort: 0,
        isHazardous: false, isFragile: false, isTemperatureControlled: false
      }
    ],
    ...overrides
  };
}

function ok(model: DeliveryDetailModel) {
  return of({ success: true, message: '', result: model } as any);
}

describe('DeliveryDetailComponent', () => {
  let fixture: ComponentFixture<DeliveryDetailComponent>;
  let component: DeliveryDetailComponent;
  let service: jasmine.SpyObj<LogisticsService>;
  let invoices: jasmine.SpyObj<SalesInvoiceService>;
  let permissions: string[] = [];

  const auth = { hasPermission: (code: string) => permissions.includes(code) } as unknown as AuthService;

  beforeEach(() => { permissions = []; });

  async function setup(model: DeliveryDetailModel | null = detail()) {
    invoices = jasmine.createSpyObj<SalesInvoiceService>('SalesInvoiceService', ['createFromDelivery']);
    invoices.createFromDelivery.and.returnValue(of({
      success: true, message: 'Record created successfully.',
      result: { invoiceUuid: 'new-invoice', invoiceNumber: 'SINV-20260921-0001', grandTotal: 1000, currencyCode: 'PKR', alreadyExisted: false }
    } as any));

    service = jasmine.createSpyObj<LogisticsService>('LogisticsService', [
      'getDeliveryById', 'holdDelivery', 'resumeDelivery', 'cancelDelivery', 'shortCloseDelivery',
      'recordPickup', 'downloadGatePass', 'getActiveCarriers', 'createConsignment',
      // A33 — the route's step actions
      'approveDelivery', 'advanceDelivery', 'generatePickList', 'getPickListForDelivery',
      'getDeliveryAvailability', 'releaseDelivery'
    ]);
    service.approveDelivery.and.returnValue(of({ success: true, message: 'Dispatch approved.' } as any));
    service.advanceDelivery.and.returnValue(of({
      success: true, message: '', result: { previousStatus: 'PACKED', status: 'STAGED', action: 'STAGE' }
    } as any));
    service.generatePickList.and.returnValue(of({ success: true, message: '', result: 'pick-list-1' } as any));
    service.getPickListForDelivery.and.returnValue(of({ success: true, message: '', result: { uuid: 'pick-list-7' } } as any));
    service.getDeliveryAvailability.and.returnValue(of({
      success: true, message: '', result: { requiresStock: true, canReleasePartially: false, lines: [] }
    } as any));
    service.getActiveCarriers.and.returnValue(of({
      success: true, message: '',
      result: [{ uuid: 'carrier-1', name: 'Beta Road', code: 'BETA', status: 'ACTIVE', isActive: true }]
    } as any));
    service.createConsignment.and.returnValue(of({ success: true, message: '', result: 'new-consignment' } as any));
    service.getDeliveryById.and.returnValue(
      model ? ok(model) : of({ success: false, message: 'not found', result: null } as any));
    service.holdDelivery.and.returnValue(of({ success: true, message: '' } as any));
    service.resumeDelivery.and.returnValue(of({ success: true, message: '' } as any));
    service.cancelDelivery.and.returnValue(of({ success: true, message: '' } as any));
    service.shortCloseDelivery.and.returnValue(of({ success: true, message: '' } as any));
    service.recordPickup.and.returnValue(of({
      success: true, message: '',
      result: { deliveryUuid: UUID, deliveryNumber: 'DLV-2026-00001', status: 'DELIVERED',
                pickedUpAt: '2026-09-20T10:00:00Z', pickupPersonName: 'Ahmed Raza', saleOrderStatus: 'FULFILLED' }
    } as any));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [DeliveryDetailComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        provideNoopAnimations(), MessageService,
        { provide: LogisticsService, useValue: service },
        { provide: SalesInvoiceService, useValue: invoices },
        { provide: AuthService, useValue: auth },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['uuid', UUID]]) } } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(DeliveryDetailComponent);
    component = fixture.componentInstance;
  }

  // ── TC-20.1 ────────────────────────────────────────────────────────────────

  it('loads the delivery named in the route and renders it', async () => {
    await setup();
    fixture.detectChanges();

    expect(service.getDeliveryById).toHaveBeenCalledOnceWith(UUID);

    const text = fixture.nativeElement.textContent;
    expect(text).toContain('DLV-2026-00001');
    expect(text).toContain('4mm cable');
    expect(component.isLoading).toBeFalse();
  });

  it('shows whether the delivery posts the stock movement itself', async () => {
    // Derived server-side from the source type. It decides whether goods issue will move stock
    // or only record it, so it belongs on the screen rather than buried in the API.
    await setup(detail({ sourceType: 'MIV', postsGoodsIssue: false }));
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('the source document does');
  });

  // ── TC-20.2 ────────────────────────────────────────────────────────────────

  it('shows a not-found state rather than an empty shell', async () => {
    await setup(null);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="not-found"]')).not.toBeNull();
    expect(component.isLoading).toBeFalse();
  });

  it('treats a 404 as not found and any other failure as an error', async () => {
    await setup();
    service.getDeliveryById.and.returnValue(throwError(() => ({ status: 404 })));
    fixture.detectChanges();
    expect(component.notFound).toBeTrue();

    await setup();
    const messages = fixture.debugElement.injector.get(MessageService);
    spyOn(messages, 'add');
    service.getDeliveryById.and.returnValue(throwError(() => ({ status: 500 })));
    fixture.detectChanges();

    // Showing "not found" for a network failure sends someone hunting for a deleted record.
    expect(component.notFound).toBeFalse();
    expect(messages.add).toHaveBeenCalled();
  });

  // ── TC-20.3 — actions come from the server's state machine ────────────────

  it('offers only the actions the server says are legal', async () => {
    await setup(detail({ status: 'DRAFT', allowedNextStatuses: ['RELEASED', 'CANCELLED'] }));
    fixture.detectChanges();

    expect(component.canCancel).toBeTrue();
    expect(component.canHold).withContext('a draft cannot be held').toBeFalse();
    expect(component.canShortClose).toBeFalse();
    expect(component.canResume).toBeFalse();

    const el = fixture.nativeElement;
    expect(el.querySelector('[data-testid="action-cancel"]')).not.toBeNull();
    expect(el.querySelector('[data-testid="action-hold"]')).toBeNull();
    expect(el.querySelector('[data-testid="action-short-close"]')).toBeNull();
  });

  it('offers hold and short close once a delivery is under way', async () => {
    await setup(detail({
      status: 'PICKED',
      allowedNextStatuses: ['PACKED', 'SHORT_CLOSED', 'ON_HOLD', 'CANCELLED']
    }));
    fixture.detectChanges();

    expect(component.canHold).toBeTrue();
    expect(component.canShortClose).toBeTrue();
    expect(component.canCancel).toBeTrue();
  });

  it('offers nothing but a read-only view once the stock has left', async () => {
    await setup(detail({ status: 'GOODS_ISSUED', allowedNextStatuses: ['IN_TRANSIT'] }));
    fixture.detectChanges();

    expect(component.canCancel).withContext('the ledger already records the movement').toBeFalse();
    expect(component.canHold).toBeFalse();
    expect(component.canShortClose).toBeFalse();
  });

  it('offers resume only for a held delivery that recorded where it paused', async () => {
    await setup(detail({
      status: 'ON_HOLD', statusBeforeHold: 'PACKED', holdReason: 'Site access blocked',
      allowedNextStatuses: ['PACKED', 'CANCELLED']
    }));
    fixture.detectChanges();

    expect(component.canResume).toBeTrue();
    expect(fixture.nativeElement.querySelector('[data-testid="action-resume"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="hold-banner"]').textContent)
      .toContain('Packed');
  });

  it('does not offer resume when the prior status was never recorded', async () => {
    // Backfilled or hand-edited rows can reach ON_HOLD without it; the server refuses to guess,
    // so the button must not pretend otherwise.
    await setup(detail({ status: 'ON_HOLD', allowedNextStatuses: ['PACKED', 'CANCELLED'] }));
    fixture.detectChanges();

    expect(component.canResume).toBeFalse();
    expect(fixture.nativeElement.querySelector('[data-testid="action-resume"]')).toBeNull();
  });

  // ── TC-20.4 — confirm, then reload from the server ────────────────────────

  it('requires a reason before an action can be confirmed', async () => {
    await setup(detail({ status: 'PICKED', allowedNextStatuses: ['ON_HOLD', 'CANCELLED'] }));
    fixture.detectChanges();

    component.openReasonDialog('hold');
    component.reasonText = '   ';
    component.confirmReason();

    expect(service.holdDelivery).not.toHaveBeenCalled();
  });

  it('sends the reason and reloads from the server afterwards', async () => {
    await setup(detail({ status: 'PICKED', allowedNextStatuses: ['ON_HOLD', 'CANCELLED'] }));
    fixture.detectChanges();
    service.getDeliveryById.calls.reset();

    component.openReasonDialog('hold');
    component.reasonText = 'Site access blocked';
    component.confirmReason();

    expect(service.holdDelivery).toHaveBeenCalledOnceWith(UUID, { reason: 'Site access blocked' });
    // Reloading rather than patching local state: the server decides the resulting status and
    // which actions are legal next.
    expect(service.getDeliveryById).toHaveBeenCalledTimes(1);
    expect(component.reasonDialogVisible).toBeFalse();
  });

  it('routes each action to its own endpoint', async () => {
    await setup(detail({ status: 'PICKED', allowedNextStatuses: ['ON_HOLD', 'SHORT_CLOSED', 'CANCELLED'] }));
    fixture.detectChanges();

    component.openReasonDialog('cancel');
    component.reasonText = 'no longer needed';
    component.confirmReason();
    expect(service.cancelDelivery).toHaveBeenCalled();

    component.openReasonDialog('short-close');
    component.reasonText = 'supplier short';
    component.confirmReason();
    expect(service.shortCloseDelivery).toHaveBeenCalled();
  });

  it('shows the server explanation when an action is refused', async () => {
    await setup(detail({ status: 'PICKED', allowedNextStatuses: ['ON_HOLD', 'CANCELLED'] }));
    fixture.detectChanges();

    const messages = fixture.debugElement.injector.get(MessageService);
    const add = spyOn(messages, 'add');
    service.cancelDelivery.and.returnValue(throwError(() => ({
      error: { message: 'A delivery in GOODS_ISSUED cannot move to CANCELLED.' }
    })));

    component.openReasonDialog('cancel');
    component.reasonText = 'changed my mind';
    component.confirmReason();

    expect(add).toHaveBeenCalled();
    const detailText = add.calls.mostRecent().args[0].detail as string;
    expect(detailText).toContain('GOODS_ISSUED');
    expect(component.isSubmitting).toBeFalse();
  });

  it('resumes without asking for a reason', async () => {
    await setup(detail({
      status: 'ON_HOLD', statusBeforeHold: 'PACKED', allowedNextStatuses: ['PACKED', 'CANCELLED']
    }));
    fixture.detectChanges();
    service.getDeliveryById.calls.reset();

    component.resume();

    expect(service.resumeDelivery).toHaveBeenCalledOnceWith(UUID);
    expect(service.getDeliveryById).toHaveBeenCalledTimes(1);
  });

  // ── Banners ────────────────────────────────────────────────────────────────

  it('warns that a migrated delivery cannot be picked', async () => {
    await setup(detail({ linesUnknown: true, lines: [] }));
    fixture.detectChanges();

    const banner = fixture.nativeElement.querySelector('[data-testid="lines-unknown-banner"]');
    expect(banner).not.toBeNull();
    expect(banner.textContent).toContain('cannot be picked');
  });

  it('warns when the delivery address has not been confirmed', async () => {
    // Booking a courier needs a validated address; saying so here beats failing at the point of
    // booking with no explanation.
    await setup(detail({
      shipToAddress: {
        uuid: 'a1', line1: 'Plot 12', cityName: 'UNKNOWN', countryName: 'UNKNOWN',
        addressType: 'OTHER', validationStatus: 'UNVALIDATED',
        validationNotes: 'City and country need confirming.'
      }
    }));
    fixture.detectChanges();

    const warning = fixture.nativeElement.querySelector('[data-testid="address-warning"]');
    expect(warning.textContent).toContain('City and country need confirming.');
  });

  it('shows no address warning for a confirmed address', async () => {
    await setup(detail({
      shipToAddress: {
        uuid: 'a1', line1: 'Plot 12', cityName: 'Karachi', countryName: 'Pakistan',
        addressType: 'WAREHOUSE', validationStatus: 'VALID'
      }
    }));
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="address-warning"]')).toBeNull();
  });

  // ── Self-pickup collection (A29-P6-08 §8.2) ────────────────────────────────

  const selfPickup = (overrides: Partial<DeliveryDetailModel> = {}) => detail({
    sourceType: 'SALE_ORDER', sourceNumber: 'SO-2026-00042', saleOrderUuid: 'so-1',
    deliveryMode: 'SELF_PICKUP', status: 'STAGED', allowedNextStatuses: ['GOODS_ISSUED', 'ON_HOLD', 'CANCELLED'],
    ...overrides
  });

  it('links a sale-order delivery back to its order', async () => {
    await setup(selfPickup());
    fixture.detectChanges();

    const link = fixture.nativeElement.querySelector('[data-testid="sale-order-link"]');
    expect(link).not.toBeNull();
    expect(link.textContent).toContain('SO-2026-00042');
    expect(fixture.nativeElement.querySelector('[data-testid="delivery-mode"]').textContent).toContain('Customer collects');
  });

  it('offers a collection only for a self-pickup that is ready to hand over', async () => {
    for (const status of ['PACKED', 'STAGED', 'PENDING_APPROVAL', 'GOODS_ISSUED']) {
      await setup(selfPickup({ status }));
      fixture.detectChanges();
      expect(component.canRecordPickup).withContext(status).toBeTrue();
    }

    for (const status of ['DRAFT', 'RELEASED', 'PICKING', 'PICKED', 'DELIVERED', 'CANCELLED']) {
      await setup(selfPickup({ status }));
      fixture.detectChanges();
      expect(component.canRecordPickup).withContext(status).toBeFalse();
    }

    // A shipped delivery is proved delivered by its consignment, never collected at the counter.
    await setup(selfPickup({ deliveryMode: 'SHIP' }));
    fixture.detectChanges();
    expect(component.canRecordPickup).toBeFalse();
    expect(fixture.nativeElement.querySelector('[data-testid="action-record-pickup"]')).toBeNull();
  });

  it('requires the collector to be named and identified before handing over', async () => {
    await setup(selfPickup());
    fixture.detectChanges();

    component.openPickupDialog();
    expect(component.canSubmitPickup).toBeFalse();

    component.pickup.pickupPersonName = 'Ahmed Raza';
    expect(component.canSubmitPickup).withContext('no ID number yet').toBeFalse();

    component.pickup.pickupPersonIdNumber = '35202-1234567-1';
    expect(component.canSubmitPickup).toBeTrue();

    component.pickup.pickupPersonName = '   ';
    component.submitPickup();
    expect(service.recordPickup).not.toHaveBeenCalled();
  });

  it('records the collection, tells the counter what the order became, and reloads', async () => {
    await setup(selfPickup());
    fixture.detectChanges();
    service.getDeliveryById.calls.reset();
    const messages = fixture.debugElement.injector.get(MessageService);
    const add = spyOn(messages, 'add');

    component.openPickupDialog();
    component.pickup = {
      pickupPersonName: ' Ahmed Raza ', pickupPersonIdType: 'PASSPORT',
      pickupPersonIdNumber: 'AB1234567 ', pickupAuthorization: '  '
    };
    component.submitPickup();

    expect(service.recordPickup).toHaveBeenCalledOnceWith(UUID, {
      pickupPersonName: 'Ahmed Raza', pickupPersonIdType: 'PASSPORT',
      pickupPersonIdNumber: 'AB1234567', pickupAuthorization: undefined
    });
    expect((add.calls.mostRecent().args[0].detail as string)).toContain('fulfilled');
    expect(service.getDeliveryById).toHaveBeenCalledTimes(1);
    expect(component.pickupDialogVisible).toBeFalse();
  });

  it('shows the server explanation when a collection is refused', async () => {
    await setup(selfPickup());
    fixture.detectChanges();
    const messages = fixture.debugElement.injector.get(MessageService);
    const add = spyOn(messages, 'add');
    service.recordPickup.and.returnValue(throwError(() => ({
      error: { message: 'Delivery DLV-2026-00001 was already collected on 2026-09-20 by Ahmed Raza.' }
    })));

    component.openPickupDialog();
    component.pickup.pickupPersonName = 'Somebody';
    component.pickup.pickupPersonIdNumber = '1';
    component.submitPickup();

    expect((add.calls.mostRecent().args[0].detail as string)).toContain('already collected');
    expect(component.isSubmitting).toBeFalse();
  });

  it('shows who collected once the delivery has been handed over', async () => {
    await setup(selfPickup({
      status: 'DELIVERED', allowedNextStatuses: ['CLOSED'],
      pickupPersonName: 'Ahmed Raza', pickupPersonIdType: 'CNIC', pickupPersonIdNumber: '35202-1234567-1',
      pickupAuthorization: 'Letter AL-2026-114', pickedUpAt: '2026-09-20T10:00:00Z'
    }));
    fixture.detectChanges();

    const banner = fixture.nativeElement.querySelector('[data-testid="collected-banner"]');
    expect(banner).not.toBeNull();
    expect(banner.textContent).toContain('Ahmed Raza');
    expect(banner.textContent).toContain('35202-1234567-1');
    expect(banner.textContent).toContain('Letter AL-2026-114');
    expect(component.canRecordPickup).toBeFalse();
  });

  it('offers the gate pass once the goods are at the dock', async () => {
    await setup(selfPickup({ status: 'PICKED', allowedNextStatuses: ['PACKED'] }));
    fixture.detectChanges();
    expect(component.canDownloadGatePass).toBeFalse();

    await setup(selfPickup({ status: 'STAGED' }));
    fixture.detectChanges();
    expect(component.canDownloadGatePass).toBeTrue();
    expect(fixture.nativeElement.querySelector('[data-testid="action-gate-pass"]')).not.toBeNull();
  });

  // ── Invoicing (A29 §9) ─────────────────────────────────────────────────────

  describe('create invoice', () => {
    const saleOrderDelivery = (overrides: Partial<DeliveryDetailModel> = {}) =>
      detail({ sourceType: 'SALE_ORDER', saleOrderUuid: 'so-1', sourceNumber: 'SO-2026-00042', status: 'DELIVERED', allowedNextStatuses: [], ...overrides });

    const button = () => fixture.nativeElement.querySelector('[data-testid="action-create-invoice"]');

    beforeEach(() => { permissions = ['SALES_INVOICE_MANAGE']; });

    it('offers an invoice for a sale-order delivery that has reached the customer', async () => {
      await setup(saleOrderDelivery());
      fixture.detectChanges();

      expect(component.canCreateInvoice).toBeTrue();
      expect(button()).not.toBeNull();
    });

    it('offers it for a closed delivery too, which the server also bills', async () => {
      await setup(saleOrderDelivery({ status: 'CLOSED' }));
      fixture.detectChanges();

      expect(component.canCreateInvoice).toBeTrue();
    });

    it('does not offer it before the goods have reached the customer', async () => {
      for (const status of ['DRAFT', 'PICKED', 'GOODS_ISSUED', 'IN_TRANSIT', 'PARTIALLY_DELIVERED', 'CANCELLED']) {
        await setup(saleOrderDelivery({ status }));
        fixture.detectChanges();
        expect(component.canCreateInvoice).withContext(status).toBeFalse();
      }
      expect(button()).toBeNull();
    });

    it('does not offer it for a delivery that is not for a sale order', async () => {
      await setup(saleOrderDelivery({ saleOrderUuid: undefined, sourceType: 'PO' }));
      fixture.detectChanges();

      expect(component.canCreateInvoice).toBeFalse();
      expect(button()).toBeNull();
    });

    it('does not offer it to someone who may not raise invoices', async () => {
      permissions = ['DELIVERY_VIEW'];
      await setup(saleOrderDelivery());
      fixture.detectChanges();

      expect(component.canCreateInvoice).toBeFalse();
      expect(button()).toBeNull();
    });

    it('raises the invoice for this delivery and opens it', async () => {
      await setup(saleOrderDelivery());
      fixture.detectChanges();
      const navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);

      component.createInvoice();

      expect(invoices.createFromDelivery).toHaveBeenCalledOnceWith(UUID);
      expect(navigate).toHaveBeenCalledWith(['/portal/pages/finance/sales-invoices', 'new-invoice']);
      expect(component.isSubmitting).toBeFalse();
    });

    it('says so, without alarm, and opens the invoice it already had', async () => {
      await setup(saleOrderDelivery());
      fixture.detectChanges();
      const navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
      const add = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
      invoices.createFromDelivery.and.returnValue(of({
        success: true, message: 'An invoice already exists for this delivery: SINV-20260920-0001.',
        result: { invoiceUuid: 'existing', invoiceNumber: 'SINV-20260920-0001', grandTotal: 1, currencyCode: 'PKR', alreadyExisted: true }
      } as any));

      component.createInvoice();

      expect(add.calls.mostRecent().args[0].severity).toBe('info');
      expect(add.calls.mostRecent().args[0].detail).toContain('SINV-20260920-0001');
      expect(navigate).toHaveBeenCalledWith(['/portal/pages/finance/sales-invoices', 'existing']);
    });

    it('shows the servers reason when the invoice is refused, and stays', async () => {
      await setup(saleOrderDelivery());
      fixture.detectChanges();
      const navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
      const add = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
      invoices.createFromDelivery.and.returnValue(throwError(() => ({
        error: { message: 'Nothing was delivered on DLV-2026-00001, so there is nothing to invoice.' }
      })));

      component.createInvoice();

      expect(add.calls.mostRecent().args[0].severity).toBe('error');
      expect(add.calls.mostRecent().args[0].detail).toContain('nothing to invoice');
      expect(navigate).not.toHaveBeenCalled();
      expect(component.isSubmitting).toBeFalse();
    });

    it('will not raise an invoice it should not offer', async () => {
      await setup(saleOrderDelivery({ status: 'IN_TRANSIT' }));
      fixture.detectChanges();

      component.createInvoice();

      expect(invoices.createFromDelivery).not.toHaveBeenCalled();
    });
  });

  // ── Handing a shipped delivery to a carrier ────────────────────────────────
  //
  // Until this existed a "Ship to customer" delivery ended at Goods Issued: nothing in the UI could
  // create the consignment that carries it, so the carrier, the tracking and the proof of delivery
  // that move it on had nowhere to start.

  describe('consignment', () => {
    beforeEach(() => { permissions = ['DELIVERY_EDIT']; });

    const shipped = (overrides: Partial<DeliveryDetailModel> = {}) => detail({
      sourceType: 'SALE_ORDER', sourceNumber: 'SO-2026-00002', saleOrderUuid: 'so-2',
      deliveryMode: 'SHIP', status: 'GOODS_ISSUED', allowedNextStatuses: ['IN_TRANSIT'],
      ...overrides
    });

    const button = () => fixture.nativeElement.querySelector('[data-testid="action-create-consignment"]');

    it('offers to create one for an outbound delivery that is boxed or issued and has none', async () => {
      for (const status of ['PACKED', 'STAGED', 'PENDING_APPROVAL', 'GOODS_ISSUED']) {
        await setup(shipped({ status }));
        fixture.detectChanges();
        expect(component.canCreateConsignment).withContext(status).toBeTrue();
      }

      await setup(shipped());
      fixture.detectChanges();
      expect(button()).not.toBeNull();
      expect(fixture.nativeElement.querySelector('[data-testid="no-consignment"]')).not.toBeNull();
    });

    it('does not offer it before the goods are boxed, or once they have reached the customer', async () => {
      for (const status of ['DRAFT', 'RELEASED', 'PICKING', 'PICKED', 'IN_TRANSIT', 'DELIVERED', 'CANCELLED']) {
        await setup(shipped({ status }));
        fixture.detectChanges();
        expect(component.canCreateConsignment).withContext(status).toBeFalse();
      }
    });

    it('never offers one for a collection, which does not travel', async () => {
      await setup(shipped({ deliveryMode: 'SELF_PICKUP' }));
      fixture.detectChanges();

      expect(component.canCreateConsignment).toBeFalse();
      expect(button()).toBeNull();
    });

    it('does not offer one for inbound or transfer movements', async () => {
      for (const direction of ['INBOUND', 'TRANSFER']) {
        await setup(shipped({ direction }));
        fixture.detectChanges();
        expect(component.canCreateConsignment).withContext(direction).toBeFalse();
      }
    });

    it('does not offer it to someone who may not edit deliveries', async () => {
      permissions = ['DELIVERY_VIEW'];
      await setup(shipped());
      fixture.detectChanges();

      expect(component.canCreateConsignment).toBeFalse();
      expect(button()).toBeNull();
    });

    it('stops offering it once the delivery has a consignment, and shows that one instead', async () => {
      await setup(shipped({
        consignments: [{
          consignmentUuid: 'cn-1', consignmentNumber: 'CN-2026-00007', status: 'BOOKED',
          carrierName: 'Beta Road', masterAwb: 'AWB-778'
        }]
      }));
      fixture.detectChanges();

      expect(component.canCreateConsignment).toBeFalse();
      expect(button()).toBeNull();

      const row = fixture.nativeElement.querySelector('[data-testid="consignment-row"]');
      expect(row.textContent).toContain('CN-2026-00007');
      expect(row.textContent).toContain('Booked');
      expect(row.textContent).toContain('Beta Road');
      expect(row.textContent).toContain('AWB-778');

      const link = fixture.nativeElement.querySelector('[data-testid="consignment-link"]');
      expect(link.getAttribute('href')).toContain('/logistics/consignments/cn-1');
    });

    it('explains that an issued delivery follows its consignment', async () => {
      const carried = [{ consignmentUuid: 'cn-1', consignmentNumber: 'CN-2026-00007', status: 'IN_TRANSIT' }];

      await setup(shipped({ consignments: carried }));
      fixture.detectChanges();
      expect(fixture.nativeElement.querySelector('[data-testid="follows-consignment"]')).not.toBeNull();

      await setup(shipped({ status: 'DELIVERED', allowedNextStatuses: ['CLOSED'], consignments: carried }));
      fixture.detectChanges();
      expect(fixture.nativeElement.querySelector('[data-testid="follows-consignment"]')).toBeNull();
    });

    it('shows no shipment card for a delivery that never travels and has none', async () => {
      await setup(shipped({ deliveryMode: 'SELF_PICKUP' }));
      fixture.detectChanges();

      expect(fixture.nativeElement.querySelector('[data-testid="shipment-card"]')).toBeNull();
    });

    it('loads the carriers once, and offers the dialog even if they cannot be loaded', async () => {
      await setup(shipped());
      fixture.detectChanges();

      component.openConsignmentDialog();
      expect(component.consignmentDialogVisible).toBeTrue();
      expect(component.carriers.map(c => c.name)).toEqual(['Beta Road']);

      component.openConsignmentDialog();
      expect(service.getActiveCarriers).toHaveBeenCalledTimes(1);

      await setup(shipped());
      fixture.detectChanges();
      service.getActiveCarriers.and.returnValue(throwError(() => ({ status: 500 })));

      component.openConsignmentDialog();
      expect(component.consignmentDialogVisible).toBeTrue();
      expect(component.carriers).toEqual([]);
    });

    it('creates the consignment for this delivery and opens it', async () => {
      await setup(shipped());
      fixture.detectChanges();
      const navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);

      component.openConsignmentDialog();
      component.consignment = { carrierUuid: 'carrier-1', notes: '  Call the gate first.  ' };
      component.submitConsignment();

      expect(service.createConsignment).toHaveBeenCalledOnceWith({
        deliveryUuids: [UUID], carrierUuid: 'carrier-1', notes: 'Call the gate first.'
      });
      expect(navigate).toHaveBeenCalledWith(['/portal/pages/logistics/consignments', 'new-consignment']);
      expect(component.consignmentDialogVisible).toBeFalse();
      expect(component.isSubmitting).toBeFalse();
    });

    it('lets the carrier be chosen later, sending neither a carrier nor an empty note', async () => {
      await setup(shipped());
      fixture.detectChanges();
      spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);

      component.openConsignmentDialog();
      component.submitConsignment();

      expect(service.createConsignment).toHaveBeenCalledOnceWith({
        deliveryUuids: [UUID], carrierUuid: undefined, notes: undefined
      });
    });

    it('shows the servers reason when the consignment is refused, and stays', async () => {
      await setup(shipped());
      fixture.detectChanges();
      const navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
      const add = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
      service.createConsignment.and.returnValue(throwError(() => ({
        error: { message: 'Carrier not found.' }
      })));

      component.openConsignmentDialog();
      component.submitConsignment();

      expect(add.calls.mostRecent().args[0].severity).toBe('error');
      expect(add.calls.mostRecent().args[0].detail).toContain('Carrier not found');
      expect(navigate).not.toHaveBeenCalled();
      expect(component.consignmentDialogVisible).toBeTrue();
      expect(component.isSubmitting).toBeFalse();
    });

    it('will not create one it should not offer', async () => {
      await setup(shipped({ status: 'DELIVERED', allowedNextStatuses: ['CLOSED'] }));
      fixture.detectChanges();

      component.submitConsignment();

      expect(service.createConsignment).not.toHaveBeenCalled();
    });
  });

  // ── A33 — fulfillment routes (API-CONTRACT.md §6–§8) ────────────────────────
  //
  // The server computes the tracker (routeSteps, with D-2/D-3 mapped onto the real statuses) and the next
  // actions (nextActions). These fixtures copy rows of the contract's §8 table, so the specs read as that table.

  const LABELS: Record<string, string> = {
    PICK: 'Pick', PACK: 'Pack', STAGE: 'Stage', APPROVAL: 'Approval',
    GOODS_ISSUE: 'Goods Issue', SHIP: 'Ship', COMPLETE: 'Complete'
  };

  /** 'PICK:DONE GOODS_ISSUE:CURRENT COMPLETE:PENDING' → the server's routeSteps. */
  function steps(spec: string): RouteStepProgressModel[] {
    return spec.split(' ').map(pair => {
      const [stepCode, state] = pair.split(':');
      return { stepCode, label: LABELS[stepCode], state: state as RouteStepProgressModel['state'] };
    });
  }

  /** PICK_PACK_SHIP at PICKED (§8: "Pack ●"), for sale order SO-2026-01085. */
  const routed = (overrides: Partial<DeliveryDetailModel> = {}) => detail({
    sourceType: 'SALE_ORDER', sourceNumber: 'SO-2026-01085', saleOrderUuid: 'so-1085', customerName: 'Punjab Group',
    deliveryMode: 'SHIP',
    fulfillmentRouteUuid: 'route-pps', fulfillmentRouteCode: 'PICK_PACK_SHIP', fulfillmentRouteName: 'Pick, Pack & Ship',
    status: 'PICKED', allowedNextStatuses: ['PACKED', 'SHORT_CLOSED', 'ON_HOLD', 'CANCELLED'],
    routeSteps: steps('PICK:DONE PACK:CURRENT GOODS_ISSUE:PENDING SHIP:PENDING COMPLETE:PENDING'),
    requiresApproval: false, nextStep: 'PACK', nextActions: ['PACK', 'HOLD', 'SHORT_CLOSE', 'CANCEL'],
    ...overrides
  });

  /** PICK_ONLY (Pick → Goods Issue → Complete), a counter collection. */
  const pickOnly = (overrides: Partial<DeliveryDetailModel> = {}) => routed({
    deliveryMode: 'SELF_PICKUP',
    fulfillmentRouteUuid: 'route-po', fulfillmentRouteCode: 'PICK_ONLY', fulfillmentRouteName: 'Pick Only',
    ...overrides
  });

  const q = (testId: string) => fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  const qAll = (testId: string): HTMLElement[] =>
    Array.from(fixture.nativeElement.querySelectorAll(`[data-testid="${testId}"]`));

  describe('A33-PE-04 step tracker', () => {
    it('shows only the route\'s own steps, then Complete, as ✓ done, ● current and ○ pending', async () => {
      await setup(routed());
      fixture.detectChanges();

      const nodes = qAll('route-step');
      expect(nodes.map(n => n.querySelector('.step-label')!.textContent!.trim()))
        .toEqual(['Pick', 'Pack', 'Goods Issue', 'Ship', 'Complete']);
      expect(nodes.map(n => n.getAttribute('data-state'))).toEqual(['DONE', 'CURRENT', 'PENDING', 'PENDING', 'PENDING']);
      expect(nodes.map(n => n.querySelector('.step-marker')!.textContent!.trim())).toEqual(['✓', '●', '○', '○', '○']);

      // Skipped steps never appear (BR-C5-04): this route has no STAGE and no APPROVAL.
      const tracker = q('route-tracker').textContent;
      expect(tracker).not.toContain('Stage');
      expect(tracker).not.toContain('Approval');
    });

    it('marks the current step for assistive technology and names the real status under it', async () => {
      // §8: SHIP is CURRENT while the consignment is IN_TRANSIT.
      await setup(routed({
        status: 'IN_TRANSIT', allowedNextStatuses: ['DELIVERED', 'PARTIALLY_DELIVERED'],
        routeSteps: steps('PICK:DONE PACK:DONE GOODS_ISSUE:DONE SHIP:CURRENT COMPLETE:PENDING'),
        nextStep: 'SHIP', nextActions: []
      }));
      fixture.detectChanges();

      const current = qAll('route-step').find(n => n.getAttribute('data-state') === 'CURRENT')!;
      expect(current.getAttribute('aria-current')).toBe('step');
      expect(current.textContent).toContain('Ship');
      expect(current.querySelector('.step-caption')!.textContent).toContain('In Transit');
      expect(qAll('route-step').filter(n => n.hasAttribute('aria-current')).length).toBe(1);
    });

    it('PICK_ONLY: once issued, Complete is current and the next action is Record Collection (D-2)', async () => {
      permissions = ['DISPATCH'];
      await setup(pickOnly({
        status: 'GOODS_ISSUED', allowedNextStatuses: ['DELIVERED'],
        routeSteps: steps('PICK:DONE GOODS_ISSUE:DONE COMPLETE:CURRENT'),
        nextStep: 'COMPLETE', nextActions: ['RECORD_COLLECTION']
      }));
      fixture.detectChanges();

      expect(qAll('route-step').map(n => n.getAttribute('data-state'))).toEqual(['DONE', 'DONE', 'CURRENT']);
      expect(q('route-action-primary').textContent).toContain('Record Collection');
    });

    it('shows every step done once the delivery has reached the customer', async () => {
      await setup(pickOnly({
        status: 'DELIVERED', allowedNextStatuses: ['CLOSED'],
        routeSteps: steps('PICK:DONE GOODS_ISSUE:DONE COMPLETE:DONE'), nextStep: null, nextActions: []
      }));
      fixture.detectChanges();

      expect(qAll('route-step').every(n => n.getAttribute('data-state') === 'DONE')).toBeTrue();
      expect(q('route-action-primary')).toBeNull();
    });

    it('always ends at Complete, even if the server sent the route steps alone (D-2)', async () => {
      await setup(routed({ routeSteps: steps('PICK:DONE PACK:CURRENT GOODS_ISSUE:PENDING SHIP:PENDING') }));
      fixture.detectChanges();

      const last = qAll('route-step').pop()!;
      expect(last.textContent).toContain('Complete');
      expect(last.getAttribute('data-state')).toBe('PENDING');

      await setup(pickOnly({
        status: 'CLOSED', allowedNextStatuses: [], routeSteps: steps('PICK:DONE GOODS_ISSUE:DONE'), nextActions: []
      }));
      fixture.detectChanges();
      expect(qAll('route-step').pop()!.getAttribute('data-state')).toBe('DONE');
    });

    it('stays where it stopped when cancelled, held or short closed, and says why', async () => {
      for (const status of ['CANCELLED', 'SHORT_CLOSED', 'ON_HOLD']) {
        await setup(routed({ status, allowedNextStatuses: [], nextActions: [] }));
        fixture.detectChanges();

        expect(qAll('route-step').map(n => n.getAttribute('data-state')))
          .withContext(status).toEqual(['DONE', 'CURRENT', 'PENDING', 'PENDING', 'PENDING']);
        expect(q('tracker-stopped')).withContext(status).not.toBeNull();
        expect(q('tracker-stopped').textContent).withContext(status).toContain(component.formatStatus(status));
      }

      await setup(routed());
      fixture.detectChanges();
      expect(q('tracker-stopped')).withContext('a moving delivery has no "stopped" note').toBeNull();
    });

    it('notes when an APPROVAL route has been approved', async () => {
      await setup(routed({
        status: 'STAGED', requiresApproval: true, approvedAt: '2026-10-03T09:30:00Z',
        routeSteps: steps('PICK:DONE PACK:DONE APPROVAL:DONE GOODS_ISSUE:CURRENT SHIP:PENDING COMPLETE:PENDING'),
        nextActions: ['GOODS_ISSUE']
      }));
      fixture.detectChanges();

      expect(q('approved-note')).not.toBeNull();
      expect(q('approved-note').textContent).toContain('03 Oct 2026');
    });

    it('leaves a delivery with no route exactly as it was: status tag, no tracker, no route badge', async () => {
      await setup(detail({ status: 'DRAFT', allowedNextStatuses: ['RELEASED', 'CANCELLED'] }));
      fixture.detectChanges();

      expect(component.hasRoute).toBeFalse();
      expect(q('route-tracker')).toBeNull();
      expect(q('route-badge')).toBeNull();
      expect(q('route-action-primary')).toBeNull();
      // Today's buttons, today's rules (no permission check was ever made on Release here).
      expect(q('action-release')).not.toBeNull();
      expect(q('action-cancel')).not.toBeNull();
    });

    it('treats an empty routeSteps as no route, as the contract says', async () => {
      await setup(detail({ routeSteps: [], nextActions: ['RELEASE'] }));
      fixture.detectChanges();

      expect(component.hasRoute).toBeFalse();
      expect(q('route-tracker')).toBeNull();
    });
  });

  describe('A33-PE-05 header and route actions', () => {
    it('shows the route, the sale order and the customer in the header', async () => {
      await setup(routed());
      fixture.detectChanges();

      expect(q('route-badge').textContent.trim()).toBe('PICK_PACK_SHIP');
      expect(q('route-name').textContent).toContain('Pick, Pack & Ship');
      const link: HTMLAnchorElement = q('sale-order-link');
      expect(link.textContent).toContain('SO-2026-01085');
      expect(link.getAttribute('href')).toBe('/portal/pages/sales/orders/so-1085');
      expect(q('customer-name').textContent).toContain('Punjab Group');
    });

    it('shows no customer row when the server sent none', async () => {
      await setup(routed({ customerName: null }));
      fixture.detectChanges();

      expect(q('customer-name')).toBeNull();
    });

    it('shows picked, packed and shipped quantities on every line', async () => {
      await setup(routed({
        lines: [{
          uuid: 'l1', lineNo: 1, itemDescription: 'Copper wire', unitOfMeasure: 'M',
          qtyOrdered: 600, qtyPicked: 600, qtyPacked: 400, qtyShipped: 250, qtyDelivered: 0, qtyShort: 0,
          isHazardous: false, isFragile: false, isTemperatureControlled: false
        }]
      }));
      fixture.detectChanges();

      const headers = Array.from(fixture.nativeElement.querySelectorAll('.table-card thead th'))
        .map((th: any) => th.textContent.trim());
      expect(headers).toEqual(jasmine.arrayContaining(['Picked', 'Packed', 'Shipped']));
      expect(q('qty-picked').textContent.trim()).toBe('600');
      expect(q('qty-packed').textContent.trim()).toBe('400');
      expect(q('qty-shipped').textContent.trim()).toBe('250');
    });

    // Each forward action, the permission the server checks for it, and the button it becomes.
    const ACTIONS: [DeliveryNextAction, string, string][] = [
      ['RELEASE',            'DELIVERY_EDIT',    'Release'],
      ['GENERATE_PICK_LIST', 'PICKING',          'Start Picking'],
      ['CONFIRM_PICK',       'PICKING',          'Confirm Pick'],
      ['PACK',               'DISPATCH',         'Start Packing'],
      ['STAGE',              'DISPATCH',         'Stage for Dispatch'],
      ['APPROVE',            'DELIVERY_APPROVE', 'Approve Dispatch'],
      ['GOODS_ISSUE',        'DISPATCH',         'Post Goods Issue'],
      ['CREATE_CONSIGNMENT', 'DELIVERY_EDIT',    'Dispatch for Shipment'],
      ['RECORD_COLLECTION',  'DISPATCH',         'Record Collection']
    ];

    it('turns the next action into the primary button, for whoever holds its permission', async () => {
      for (const [action, permission, label] of ACTIONS) {
        permissions = [permission];
        await setup(routed({ nextActions: [action, 'CANCEL'] }));
        fixture.detectChanges();

        expect(q('route-action-primary')).withContext(action).not.toBeNull();
        expect(q('route-action-primary').textContent).withContext(action).toContain(label);
      }
    });

    it('offers no step button to someone without its permission, and says who must act', async () => {
      for (const [action, , label] of ACTIONS) {
        // Every other logistics permission, but not this action's own.
        const own = ACTIONS.find(a => a[0] === action)![1];
        permissions = ['DELIVERY_EDIT', 'PICKING', 'DISPATCH', 'DELIVERY_APPROVE'].filter(p => p !== own);
        await setup(routed({ nextActions: [action] }));
        fixture.detectChanges();

        expect(q('route-action-primary')).withContext(action).toBeNull();
        expect(q('route-action-blocked')).withContext(action).not.toBeNull();
        expect(q('route-action-blocked').textContent).withContext(action).toContain(label);
      }
    });

    it('Approve Dispatch needs DELIVERY_APPROVE: DISPATCH alone does not approve (D-7)', async () => {
      permissions = ['DISPATCH', 'DELIVERY_EDIT'];
      await setup(routed({ status: 'STAGED', requiresApproval: true, nextActions: ['APPROVE'] }));
      fixture.detectChanges();

      expect(component.routeActions.length).toBe(0);
      expect(q('route-action-primary')).toBeNull();
    });

    it('approves through the approve endpoint, never advance, so an approval can never issue the goods', async () => {
      permissions = ['DELIVERY_APPROVE'];
      await setup(routed({ status: 'STAGED', requiresApproval: true, nextActions: ['APPROVE'] }));
      fixture.detectChanges();

      q('route-action-primary').querySelector('button').click();

      expect(service.approveDelivery).toHaveBeenCalledOnceWith(UUID);
      expect(service.advanceDelivery).not.toHaveBeenCalled();
      expect(service.getDeliveryById).withContext('reloads from the server').toHaveBeenCalledTimes(2);
    });

    it('says so, without alarm, when someone else had already approved it', async () => {
      permissions = ['DELIVERY_APPROVE'];
      await setup(routed({ status: 'STAGED', requiresApproval: true, nextActions: ['APPROVE'] }));
      fixture.detectChanges();
      const add = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
      service.approveDelivery.and.returnValue(of({
        success: true, message: '',
        result: { deliveryUuid: UUID, approvedAt: '2026-10-03T09:00:00Z', approvedBy: 7, alreadyApproved: true }
      } as any));

      component.performRouteAction('APPROVE');

      expect(add.calls.mostRecent().args[0].severity).toBe('info');
      expect(add.calls.mostRecent().args[0].summary).toBe('Already approved');
      expect(service.getDeliveryById).toHaveBeenCalledTimes(2);
    });

    it('stages through advance, naming the status on screen so a stale page gets a 409', async () => {
      permissions = ['DISPATCH'];
      await setup(routed({ status: 'PACKED', nextActions: ['STAGE'] }));
      fixture.detectChanges();
      const add = spyOn(fixture.debugElement.injector.get(MessageService), 'add');

      component.performRouteAction('STAGE');

      expect(service.advanceDelivery).toHaveBeenCalledOnceWith(UUID, { expectedStatus: 'PACKED' });
      expect(add.calls.mostRecent().args[0].severity).toBe('success');
      expect(service.getDeliveryById).toHaveBeenCalledTimes(2);
      expect(component.isSubmitting).toBeFalse();
    });

    it('asks before the goods issue, the point of no return, then advances', async () => {
      permissions = ['DISPATCH'];
      await setup(routed({ status: 'STAGED', nextActions: ['GOODS_ISSUE', 'CANCEL'] }));
      fixture.detectChanges();
      service.advanceDelivery.and.returnValue(of({
        success: true, message: '', result: { previousStatus: 'STAGED', status: 'GOODS_ISSUED', action: 'GOODS_ISSUE' }
      } as any));

      component.performRouteAction('GOODS_ISSUE');
      expect(component.issueDialogVisible).toBeTrue();
      expect(service.advanceDelivery).not.toHaveBeenCalled();

      component.confirmGoodsIssue();

      expect(service.advanceDelivery).toHaveBeenCalledOnceWith(UUID, { expectedStatus: 'STAGED' });
      expect(component.issueDialogVisible).toBeFalse();
      expect(service.getDeliveryById).toHaveBeenCalledTimes(2);
    });

    it('shows the server\'s reason on a 409 (moved on, or the step needs input) and reloads', async () => {
      permissions = ['DISPATCH'];
      await setup(routed({ status: 'PACKED', nextActions: ['STAGE'] }));
      fixture.detectChanges();
      const add = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
      service.advanceDelivery.and.returnValue(throwError(() => ({
        status: 409, error: { message: 'The delivery is no longer PACKED.' }
      })));

      component.performRouteAction('STAGE');

      expect(add.calls.mostRecent().args[0].severity).toBe('warn');
      expect(add.calls.mostRecent().args[0].detail).toContain('no longer PACKED');
      expect(service.getDeliveryById).withContext('shows what it is now').toHaveBeenCalledTimes(2);
      expect(component.isSubmitting).toBeFalse();
    });

    it('shows the server\'s reason on any other refusal, and stays', async () => {
      permissions = ['DELIVERY_APPROVE'];
      await setup(routed({ status: 'STAGED', requiresApproval: true, nextActions: ['APPROVE'] }));
      fixture.detectChanges();
      const add = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
      service.approveDelivery.and.returnValue(throwError(() => ({
        status: 400, error: { message: 'Only a STAGED delivery can be approved.' }
      })));

      component.performRouteAction('APPROVE');

      expect(add.calls.mostRecent().args[0].severity).toBe('error');
      expect(add.calls.mostRecent().args[0].detail).toContain('Only a STAGED delivery');
      expect(component.isSubmitting).toBeFalse();
    });

    it('opens the existing screens and dialogs for steps that need input, never advance', async () => {
      permissions = ['DELIVERY_EDIT', 'PICKING', 'DISPATCH'];

      await setup(routed({ status: 'DRAFT', nextActions: ['RELEASE'] }));
      fixture.detectChanges();
      component.performRouteAction('RELEASE');
      expect(component.releaseDialogVisible).withContext('release shows the stock check first').toBeTrue();
      expect(service.getDeliveryAvailability).toHaveBeenCalled();

      await setup(routed({ status: 'RELEASED', nextActions: ['GENERATE_PICK_LIST'] }));
      fixture.detectChanges();
      let navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
      component.performRouteAction('GENERATE_PICK_LIST');
      expect(service.generatePickList).toHaveBeenCalledWith(UUID);
      expect(navigate).toHaveBeenCalledWith(['/portal/pages/logistics/picking', 'pick-list-1']);

      await setup(routed({ status: 'PICKING', nextActions: ['CONFIRM_PICK'] }));
      fixture.detectChanges();
      navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
      component.performRouteAction('CONFIRM_PICK');
      expect(service.getPickListForDelivery).toHaveBeenCalledWith(UUID);
      expect(navigate).toHaveBeenCalledWith(['/portal/pages/logistics/picking', 'pick-list-7']);

      await setup(routed({ status: 'PICKED', nextActions: ['PACK'] }));
      fixture.detectChanges();
      navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
      component.performRouteAction('PACK');
      expect(navigate).toHaveBeenCalledWith(['/portal/pages/logistics/deliveries', UUID, 'pack']);

      await setup(routed({ status: 'GOODS_ISSUED', nextActions: ['CREATE_CONSIGNMENT'] }));
      fixture.detectChanges();
      component.performRouteAction('CREATE_CONSIGNMENT');
      expect(component.consignmentDialogVisible).toBeTrue();

      await setup(pickOnly({ status: 'GOODS_ISSUED', nextActions: ['RECORD_COLLECTION'] }));
      fixture.detectChanges();
      component.performRouteAction('RECORD_COLLECTION');
      expect(component.pickupDialogVisible).toBeTrue();

      expect(service.advanceDelivery).not.toHaveBeenCalled();
    });

    it('will not perform an action the route does not offer or the user may not do', async () => {
      permissions = ['DISPATCH'];
      await setup(routed({ status: 'PACKED', nextActions: ['STAGE'] }));
      fixture.detectChanges();

      component.performRouteAction('GOODS_ISSUE');
      component.confirmGoodsIssue();
      component.performRouteAction('APPROVE');

      expect(service.advanceDelivery).not.toHaveBeenCalled();
      expect(service.approveDelivery).not.toHaveBeenCalled();
      expect(component.issueDialogVisible).toBeFalse();
    });

    it('puts the first action in flow order first and offers the rest beside it', async () => {
      permissions = ['DISPATCH', 'DELIVERY_EDIT'];
      await setup(routed({ status: 'PACKED', nextActions: ['CREATE_CONSIGNMENT', 'STAGE', 'CANCEL'] }));
      fixture.detectChanges();

      expect(component.routeActions.map(a => a.action)).toEqual(['STAGE', 'CREATE_CONSIGNMENT']);
      expect(q('route-action-primary').textContent).toContain('Stage for Dispatch');
      expect(qAll('route-action-secondary').map(b => b.textContent!.trim())).toEqual(['Dispatch for Shipment']);
    });

    it('replaces today\'s step buttons with the route\'s, so nothing is offered twice', async () => {
      // Today a STAGED self-pickup offers "Record collection"; on PICK_ONLY the route says goods issue comes first.
      permissions = ['DISPATCH', 'DELIVERY_EDIT', 'PICKING'];
      await setup(pickOnly({
        status: 'STAGED', allowedNextStatuses: ['GOODS_ISSUED', 'ON_HOLD', 'CANCELLED'],
        routeSteps: steps('PICK:DONE GOODS_ISSUE:CURRENT COMPLETE:PENDING'), nextActions: ['GOODS_ISSUE', 'HOLD', 'CANCEL']
      }));
      fixture.detectChanges();

      expect(component.canRecordPickup).toBeFalse();
      expect(q('action-record-pickup')).toBeNull();
      expect(q('action-release')).toBeNull();
      expect(q('action-generate-pick-list')).toBeNull();
      expect(q('route-action-primary').textContent).toContain('Post Goods Issue');

      await setup(routed({ status: 'DRAFT', allowedNextStatuses: ['RELEASED', 'CANCELLED'], nextActions: ['RELEASE', 'CANCEL'] }));
      fixture.detectChanges();
      expect(q('action-release')).withContext('the route button replaces it').toBeNull();
      expect(q('route-action-primary').textContent).toContain('Release');
    });

    it('keeps hold, cancel and short close route-independent (BR-C5-05)', async () => {
      await setup(routed());
      fixture.detectChanges();

      expect(q('action-hold')).not.toBeNull();
      expect(q('action-short-close')).not.toBeNull();
      expect(q('action-cancel')).not.toBeNull();
    });

    it('creates the consignment only when the route offers it', async () => {
      permissions = ['DELIVERY_EDIT'];
      await setup(routed({ status: 'GOODS_ISSUED', allowedNextStatuses: ['IN_TRANSIT'], nextActions: ['CREATE_CONSIGNMENT'] }));
      fixture.detectChanges();
      spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);

      expect(component.canCreateConsignment).toBeTrue();
      component.openConsignmentDialog();
      component.submitConsignment();
      expect(service.createConsignment).toHaveBeenCalled();

      // PACKED would allow a consignment today; the route says stage first.
      await setup(routed({ status: 'PACKED', allowedNextStatuses: ['STAGED'], nextActions: ['STAGE'] }));
      fixture.detectChanges();
      expect(component.canCreateConsignment).toBeFalse();
    });

    it('offers the pack station only on a route that packs, and not twice', async () => {
      permissions = ['DISPATCH'];

      // PICK_AND_SHIP has no PACK: packing is automatic (D-3), so once its cartons are frozen (here: a live
      // consignment carries it) the dock screen has nothing to do. Before that, see REV-04b below.
      await setup(routed({
        status: 'STAGED', fulfillmentRouteCode: 'PICK_AND_SHIP',
        routeSteps: steps('PICK:DONE GOODS_ISSUE:CURRENT SHIP:PENDING COMPLETE:PENDING'), nextActions: ['GOODS_ISSUE'],
        consignments: [{ consignmentUuid: 'cn-1', consignmentNumber: 'CN-1', status: 'BOOKED' }]
      }));
      fixture.detectChanges();
      expect(q('action-pack-station')).toBeNull();

      // PICK_PACK_SHIP, packed: the cartons are worth seeing.
      await setup(routed({
        status: 'PACKED', routeSteps: steps('PICK:DONE PACK:DONE GOODS_ISSUE:CURRENT SHIP:PENDING COMPLETE:PENDING'),
        nextActions: ['STAGE']
      }));
      fixture.detectChanges();
      expect(q('action-pack-station')).not.toBeNull();

      // When packing is the next step, "Start Packing" already goes there.
      await setup(routed());
      fixture.detectChanges();
      expect(q('route-action-primary').textContent).toContain('Start Packing');
      expect(q('action-pack-station')).toBeNull();
    });

    // REV-04b — the server (REV-04) keeps an auto-staged delivery's cartons amendable until it is approved, consigned or
    // issued: that is the only time PICK_AND_SHIP's auto LOOSE unit can be weighed, which a courier needs.
    describe('REV-04b: cartons of an auto-staged delivery', () => {
      const autoStaged = (overrides: Partial<DeliveryDetailModel> = {}) => routed({
        status: 'STAGED', allowedNextStatuses: ['GOODS_ISSUED', 'ON_HOLD', 'CANCELLED'],
        fulfillmentRouteCode: 'PICK_AND_SHIP', fulfillmentRouteName: 'Pick & Ship',
        routeSteps: steps('PICK:DONE GOODS_ISSUE:CURRENT SHIP:PENDING COMPLETE:PENDING'),
        nextActions: ['GOODS_ISSUE', 'CANCEL'], approvedAt: null, consignments: [],
        ...overrides
      });

      it('mirrors the server rule: STAGED, route without STAGE, not approved, on no live consignment', () => {
        expect(packagesAmendableWhileStaged(autoStaged())).toBeTrue();

        expect(packagesAmendableWhileStaged(autoStaged({ approvedAt: '2026-10-03T09:00:00Z' })))
          .withContext('approved').toBeFalse();
        expect(packagesAmendableWhileStaged(autoStaged({
          consignments: [{ consignmentUuid: 'cn-1', consignmentNumber: 'CN-1', status: 'BOOKED' }]
        }))).withContext('on a consignment').toBeFalse();
        expect(packagesAmendableWhileStaged(autoStaged({
          consignments: [{ consignmentUuid: 'cn-1', consignmentNumber: 'CN-1', status: 'CANCELLED' }]
        }))).withContext('a cancelled consignment no longer carries it').toBeTrue();
        expect(packagesAmendableWhileStaged(autoStaged({
          routeSteps: steps('PICK:DONE PACK:DONE STAGE:DONE GOODS_ISSUE:CURRENT SHIP:PENDING COMPLETE:PENDING')
        }))).withContext('a route that stages by hand').toBeFalse();
        expect(packagesAmendableWhileStaged(autoStaged({ status: 'GOODS_ISSUED' }))).withContext('issued').toBeFalse();
        expect(packagesAmendableWhileStaged(detail({ status: 'STAGED' }))).withContext('no route: today\'s rule').toBeFalse();
      });

      it('offers the pack station on PICK_AND_SHIP while staged, not approved and not consigned', async () => {
        await setup(autoStaged());
        fixture.detectChanges();
        expect(q('action-pack-station')).not.toBeNull();
      });

      it('hides it once approved or on a consignment', async () => {
        await setup(autoStaged({ approvedAt: '2026-10-03T09:00:00Z' }));
        fixture.detectChanges();
        expect(q('action-pack-station')).withContext('approved').toBeNull();

        await setup(autoStaged({ consignments: [{ consignmentUuid: 'cn-1', consignmentNumber: 'CN-1', status: 'BOOKED' }] }));
        fixture.detectChanges();
        expect(q('action-pack-station')).withContext('consigned').toBeNull();
      });
    });
  });
});
