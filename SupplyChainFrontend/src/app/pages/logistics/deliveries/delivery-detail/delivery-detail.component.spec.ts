import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { DeliveryDetailComponent } from './delivery-detail.component';
import { LogisticsService, DeliveryDetailModel } from '../../../../services/logistics.service';

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

  async function setup(model: DeliveryDetailModel | null = detail()) {
    service = jasmine.createSpyObj<LogisticsService>('LogisticsService', [
      'getDeliveryById', 'holdDelivery', 'resumeDelivery', 'cancelDelivery', 'shortCloseDelivery'
    ]);
    service.getDeliveryById.and.returnValue(
      model ? ok(model) : of({ success: false, message: 'not found', result: null } as any));
    service.holdDelivery.and.returnValue(of({ success: true, message: '' } as any));
    service.resumeDelivery.and.returnValue(of({ success: true, message: '' } as any));
    service.cancelDelivery.and.returnValue(of({ success: true, message: '' } as any));
    service.shortCloseDelivery.and.returnValue(of({ success: true, message: '' } as any));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [DeliveryDetailComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        provideNoopAnimations(), MessageService,
        { provide: LogisticsService, useValue: service },
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
});
