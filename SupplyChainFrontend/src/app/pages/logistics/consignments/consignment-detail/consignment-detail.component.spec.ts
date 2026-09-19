import { ComponentFixture, TestBed, fakeAsync, tick, flush } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { ConsignmentDetailComponent } from './consignment-detail.component';
import {
  LogisticsService,
  ConsignmentDetailModel,
  ConsignmentBookingStatusModel,
  ConsignmentTrackingEventModel,
  ConsignmentRateModel,
  ConsignmentWeightModel,
  RateShopResultModel,
  RateShopOptionModel,
  ShippingRuleDecisionModel
} from '../../../../services/logistics.service';

const UUID = '11111111-1111-1111-1111-111111111111';

function consignment(overrides: Partial<ConsignmentDetailModel> = {}): ConsignmentDetailModel {
  return {
    uuid: UUID,
    consignmentNumber: 'SHP-2026-00001',
    carrierUuid: 'carrier-1',
    carrierName: 'Simcourier',
    integrationMode: 'API',
    mode: 'COURIER',
    freightTerms: 'PREPAID',
    status: 'DRAFT',
    allowedNextStatuses: ['BOOKING', 'CANCELLED'],
    createdDate: '2026-09-01T00:00:00Z',
    deliveries: [],
    ...overrides
  } as ConsignmentDetailModel;
}

function booking(overrides: Partial<ConsignmentBookingStatusModel> = {}): ConsignmentBookingStatusModel {
  return {
    consignmentUuid: UUID,
    consignmentNumber: 'SHP-2026-00001',
    status: 'DRAFT',
    isSandbox: false,
    attemptCount: 0,
    hasStoredLabel: false,
    needsResolution: false,
    willRetryAutomatically: false,
    ...overrides
  };
}

function trackingEvent(
  overrides: Partial<ConsignmentTrackingEventModel> = {}): ConsignmentTrackingEventModel {
  return {
    milestone: 'IN_TRANSIT',
    occurredAt: '2026-09-02T08:00:00Z',
    receivedAt: '2026-09-02T08:05:00Z',
    source: 'POLL',
    ...overrides
  };
}

function rate(overrides: Partial<ConsignmentRateModel> = {}): ConsignmentRateModel {
  return {
    consignmentUuid: UUID,
    consignmentNumber: 'SHP-2026-00001',
    status: 'RATED',
    isRated: true,
    freightCost: 1260,
    freightCurrency: 'PKR',
    ratedAt: '2026-09-18T09:00:00Z',
    source: 'CARRIER',
    note: 'Quoted by Simcourier.',
    serviceCode: 'ECON',
    chargeableWeightKg: 12.5,
    charges: [
      { code: 'BASE', description: 'Base carriage', amount: 1125 },
      { code: 'FUEL', description: 'Fuel surcharge', amount: 135 }
    ],
    attempts: [{ source: 'CARRIER', succeeded: true, message: '1 rate(s) returned.' }],
    warnings: [],
    ...overrides
  };
}

function weight(overrides: Partial<ConsignmentWeightModel> = {}): ConsignmentWeightModel {
  return {
    consignmentUuid: UUID,
    consignmentNumber: 'SHP-2026-00001',
    chargesVolumetricWeight: true,
    dimDivisor: 5000,
    packageCount: 1,
    totalActualKg: 12.5,
    totalVolumetricKg: 4.8,
    totalChargeableKg: 12.5,
    isComplete: true,
    warnings: [],
    packages: [{
      packageUuid: 'p1', packageBarcode: 'PKG-001', packageType: 'BOX',
      actualKg: 12.5, volumetricKg: 4.8, chargeableKg: 12.5, basis: 'ACTUAL',
      divisorUsed: 5000, warnings: []
    }],
    ...overrides
  };
}

function option(overrides: Partial<RateShopOptionModel> = {}): RateShopOptionModel {
  return {
    carrierUuid: 'carrier-1', carrierName: 'Beta Road',
    serviceCode: 'ROAD', serviceName: 'Road freight',
    source: 'RATE_CARD', totalAmount: 750, currency: 'PKR', baseAmount: 750,
    surcharges: [], transitDays: 4, isGuaranteed: false,
    rank: 1, moreThanBest: 0, note: 'Cheapest of 2.',
    ...overrides
  };
}

function shop(overrides: Partial<RateShopResultModel> = {}): RateShopResultModel {
  return {
    consignmentUuid: UUID, consignmentNumber: 'SHP-2026-00001',
    chargeableWeightKg: 12.5, shipDate: '2026-09-18T00:00:00Z', strategy: 'CHEAPEST',
    options: [option()], excluded: [],
    recommended: option(), recommendation: 'Cheapest of 2: Beta Road ROAD at PKR 750.00.',
    warnings: [],
    ...overrides
  };
}

function decision(overrides: Partial<ShippingRuleDecisionModel> = {}): ShippingRuleDecisionModel {
  return {
    consignmentUuid: UUID, consignmentNumber: 'SHP-2026-00001',
    chargeableWeightKg: 12.5, isHazardous: false, hasCod: false,
    matchedRule: { ruleUuid: 'r1', name: 'Domestic parcels', priority: 20, matched: true,
                   reason: 'Every condition holds: weight 12.5 kg is at most 30 kg.' },
    considered: [
      { ruleUuid: 'r0', name: 'Featherweight', priority: 10, matched: false,
        reason: 'Does not apply: weight 12.5 kg is at most 2 kg — not true of this consignment.' },
      { ruleUuid: 'r1', name: 'Domestic parcels', priority: 20, matched: true,
        reason: 'Every condition holds: weight 12.5 kg is at most 30 kg.' }
    ],
    selection: 'Beta Road, cheapest of its services.',
    recommended: option(), options: [option()], excluded: [], warnings: [],
    ...overrides
  };
}

function ok<T>(result: T) {
  return of({ success: true, message: '', result } as any);
}

describe('ConsignmentDetailComponent', () => {
  let fixture: ComponentFixture<ConsignmentDetailComponent>;
  let component: ConsignmentDetailComponent;
  let service: jasmine.SpyObj<LogisticsService>;

  async function setup(
    detail: ConsignmentDetailModel | null = consignment(),
    bookingStatus: ConsignmentBookingStatusModel = booking()) {
    service = jasmine.createSpyObj<LogisticsService>('LogisticsService', [
      'getConsignmentById', 'getConsignmentBooking', 'getConsignmentTracking',
      'getCarrierAccounts', 'bookConsignment', 'bookConsignmentManually',
      'resolveConsignmentBooking', 'downloadConsignmentLabel', 'refreshConsignmentTracking',
      'getConsignmentRate', 'getChargeableWeight', 'rateConsignment', 'setManualRate',
      'clearConsignmentRate', 'shopRates', 'acceptRate',
      'evaluateShippingRules', 'applyShippingRule'
    ]);

    service.getConsignmentById.and.returnValue(
      detail ? ok(detail) : of({ success: false, message: 'not found', result: null } as any));
    service.getConsignmentBooking.and.returnValue(ok(bookingStatus));
    service.getConsignmentTracking.and.returnValue(ok([]));
    service.getCarrierAccounts.and.returnValue(ok([]));
    service.bookConsignment.and.returnValue(ok(booking({ status: 'BOOKING', commandStatus: 'IN_FLIGHT' })));
    service.bookConsignmentManually.and.returnValue(of({ success: true, message: '' } as any));
    service.resolveConsignmentBooking.and.returnValue(ok(booking({ status: 'BOOKED', masterAwb: 'AWB-1' })));
    service.downloadConsignmentLabel.and.returnValue(of(new Blob(['%PDF-'])));
    service.refreshConsignmentTracking.and.returnValue(
      ok({ polled: true, status: 'IN_TRANSIT', newEvents: 2 }));

    // Unrated is the normal starting state, so the default is "nothing priced yet".
    service.getConsignmentRate.and.returnValue(
      ok(rate({ isRated: false, status: 'DRAFT', freightCost: undefined, source: undefined, charges: [] })));
    service.getChargeableWeight.and.returnValue(ok(weight()));
    service.rateConsignment.and.returnValue(ok(rate()));
    service.setManualRate.and.returnValue(ok(rate({ source: 'MANUAL', freightCost: 3250 })));
    service.clearConsignmentRate.and.returnValue(of({ success: true, message: '' } as any));
    service.shopRates.and.returnValue(ok(shop()));
    service.acceptRate.and.returnValue(ok(rate({ source: 'RATE_CARD', freightCost: 750 })));
    service.evaluateShippingRules.and.returnValue(ok(decision()));
    service.applyShippingRule.and.returnValue(ok(rate({ source: 'RATE_CARD', freightCost: 750 })));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [ConsignmentDetailComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        provideNoopAnimations(), MessageService,
        { provide: LogisticsService, useValue: service },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['uuid', UUID]]) } } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(ConsignmentDetailComponent);
    component = fixture.componentInstance;
  }

  // ── Loading ───────────────────────────────────────────────────────────────

  it('loads the consignment, its booking and its tracking', async () => {
    await setup();
    fixture.detectChanges();

    expect(service.getConsignmentById).toHaveBeenCalledWith(UUID);
    expect(service.getConsignmentBooking).toHaveBeenCalledWith(UUID);
    expect(service.getConsignmentTracking).toHaveBeenCalledWith(UUID);
    expect(component.notFound).toBeFalse();
  });

  it('shows not found for a 404 but not for a failed request', async () => {
    await setup();
    service.getConsignmentById.and.returnValue(throwError(() => ({ status: 404 })));
    fixture.detectChanges();
    expect(component.notFound).toBeTrue();

    await setup();
    service.getConsignmentById.and.returnValue(throwError(() => ({ status: 500 })));
    fixture.detectChanges();
    expect(component.notFound).toBeFalse();
  });

  it('still renders when the booking panel cannot be loaded', async () => {
    // A failed side panel must not take the whole screen down.
    await setup();
    service.getConsignmentBooking.and.returnValue(throwError(() => ({ status: 500 })));
    fixture.detectChanges();

    expect(component.consignment).not.toBeNull();
    expect(component.booking).toBeNull();
  });

  // ── What may be done ──────────────────────────────────────────────────────

  it('offers API booking only to an API carrier in a bookable status', async () => {
    for (const [status, expected] of [
      ['DRAFT', true], ['RATED', true], ['BOOKING_FAILED', true],
      ['BOOKING', false], ['BOOKED', false], ['DELIVERED', false]
    ] as [string, boolean][]) {
      await setup(consignment({ status }));
      fixture.detectChanges();
      expect(component.canBook).toBe(expected, `canBook for ${status}`);
    }
  });

  it('offers manual entry only to a manual carrier, and never both', async () => {
    await setup(consignment({ integrationMode: 'MANUAL' }));
    fixture.detectChanges();

    expect(component.canBookManually).toBeTrue();
    expect(component.canBook).toBeFalse();
  });

  it('will not offer booking while one is in flight or unresolved', async () => {
    await setup(consignment(), booking({ status: 'BOOKING', commandStatus: 'IN_FLIGHT' }));
    fixture.detectChanges();
    expect(component.canBook).toBeFalse();

    await setup(consignment({ status: 'BOOKING_FAILED' }), booking({ needsResolution: true }));
    fixture.detectChanges();
    expect(component.canBook).toBeFalse();
  });

  it('offers the label once there is an airway bill, stored or not', async () => {
    // hasStoredLabel only says whether it is cached; it is fetched on first print.
    await setup(consignment({ status: 'BOOKED' }),
                booking({ masterAwb: 'AWB-1', hasStoredLabel: false }));
    fixture.detectChanges();

    expect(component.canPrintLabel).toBeTrue();
  });

  // ── Booking is asynchronous ───────────────────────────────────────────────

  it('reports a booking as requested, not as done', fakeAsync(async () => {
    // POST answers 202 — the carrier has not answered yet, and saying "booked" here would be a
    // lie the next poll contradicts.
    await setup();
    fixture.detectChanges();
    const messages = fixture.debugElement.injector.get(MessageService);
    spyOn(messages, 'add');

    component.openBookDialog();
    component.confirmBook();

    expect(messages.add).toHaveBeenCalledWith(jasmine.objectContaining({
      severity: 'info', summary: 'Booking requested'
    }));

    component.ngOnDestroy();
    flush();
  }));

  it('polls until the booking settles, then says what happened', fakeAsync(async () => {
    await setup();
    fixture.detectChanges();
    const messages = fixture.debugElement.injector.get(MessageService);
    spyOn(messages, 'add');

    component.confirmBook();
    expect(component.isBookingInFlight).toBeTrue();

    // Still in flight on the first poll…
    service.getConsignmentBooking.and.returnValue(
      ok(booking({ status: 'BOOKING', commandStatus: 'IN_FLIGHT' })));
    tick(2500);
    expect(component.isBookingInFlight).toBeTrue();

    // …then the carrier answers.
    service.getConsignmentBooking.and.returnValue(
      ok(booking({ status: 'BOOKED', commandStatus: 'SUCCEEDED', masterAwb: 'AWB-77' })));
    tick(2500);

    expect(component.isBookingInFlight).toBeFalse();
    expect(messages.add).toHaveBeenCalledWith(jasmine.objectContaining({
      summary: 'Booked', detail: 'Airway bill AWB-77.'
    }));

    component.ngOnDestroy();
    flush();
  }));

  it('stops polling when the screen is destroyed', fakeAsync(async () => {
    // A timer that outlives the screen keeps hitting the carrier endpoint forever.
    await setup();
    fixture.detectChanges();

    component.confirmBook();
    service.getConsignmentBooking.and.returnValue(
      ok(booking({ status: 'BOOKING', commandStatus: 'IN_FLIGHT' })));
    tick(2500);

    const callsBefore = service.getConsignmentBooking.calls.count();

    component.ngOnDestroy();
    tick(30_000);

    expect(service.getConsignmentBooking.calls.count()).toBe(callsBefore);
    flush();
  }));

  it('gives up politely if the carrier never answers', fakeAsync(async () => {
    await setup();
    fixture.detectChanges();
    const messages = fixture.debugElement.injector.get(MessageService);
    spyOn(messages, 'add');

    service.getConsignmentBooking.and.returnValue(
      ok(booking({ status: 'BOOKING', commandStatus: 'IN_FLIGHT' })));

    component.confirmBook();
    tick(130_000);

    expect(messages.add).toHaveBeenCalledWith(jasmine.objectContaining({
      severity: 'warn', summary: 'Still booking'
    }));

    component.ngOnDestroy();
    flush();
  }));

  it('picks up a booking somebody else started', fakeAsync(async () => {
    // A refreshed page, or a colleague's booking — the screen polls without anyone pressing.
    await setup(consignment({ status: 'BOOKING' }),
                booking({ status: 'BOOKING', commandStatus: 'IN_FLIGHT' }));
    fixture.detectChanges();

    expect(component.isBookingInFlight).toBeTrue();

    service.getConsignmentBooking.and.returnValue(
      ok(booking({ status: 'BOOKED', masterAwb: 'AWB-9' })));
    tick(2500);

    expect(component.isBookingInFlight).toBeFalse();

    component.ngOnDestroy();
    flush();
  }));

  // ── The state a person must act on ────────────────────────────────────────

  it('flags an unknown outcome as needing a person', async () => {
    await setup(consignment({ status: 'BOOKING_FAILED' }),
                booking({ needsResolution: true, failureReason: 'The carrier timed out.' }));
    fixture.detectChanges();

    expect(component.needsResolution).toBeTrue();
    expect(component.canBook).toBeFalse();
  });

  it('will not resolve without a note, nor claim a booking without an airway bill', async () => {
    await setup(consignment(), booking({ needsResolution: true }));
    fixture.detectChanges();

    component.openResolveDialog();
    expect(component.canResolve).toBeFalse();

    component.resolveForm.note = 'Spoke to Ayesha at the depot';
    // "The carrier has it" with no AWB leaves nothing to track or prove it.
    expect(component.canResolve).toBeFalse();

    component.resolveForm.awb = 'AWB-55';
    expect(component.canResolve).toBeTrue();

    // "No record of it" needs no airway bill.
    component.resolveForm.carrierBooked = false;
    component.resolveForm.awb = '';
    expect(component.canResolve).toBeTrue();
  });

  it('sends no airway bill when the carrier has no record', async () => {
    await setup(consignment(), booking({ needsResolution: true }));
    fixture.detectChanges();

    component.openResolveDialog();
    component.resolveForm = { carrierBooked: false, awb: 'AWB-TYPED-THEN-CHANGED', note: 'No record' };
    component.confirmResolve();

    const req = service.resolveConsignmentBooking.calls.mostRecent().args[1];
    expect(req.carrierBooked).toBeFalse();
    expect(req.awb).toBeUndefined();
  });

  // ── Manual booking ────────────────────────────────────────────────────────

  it('needs an airway bill to record a manual booking', async () => {
    await setup(consignment({ integrationMode: 'MANUAL' }));
    fixture.detectChanges();

    component.openManualDialog();
    expect(component.canSaveManual).toBeFalse();

    component.manualForm.awb = '  AWB-123  ';
    expect(component.canSaveManual).toBeTrue();

    component.confirmManual();
    expect(service.bookConsignmentManually).toHaveBeenCalledWith(UUID, jasmine.objectContaining({
      awb: 'AWB-123'
    }));
  });

  // ── Label ─────────────────────────────────────────────────────────────────

  it('opens the label rather than downloading it', async () => {
    // The server serves it inline precisely so a browser goes straight to print.
    await setup(consignment({ status: 'BOOKED' }), booking({ masterAwb: 'AWB-1' }));
    fixture.detectChanges();

    spyOn(URL, 'createObjectURL').and.returnValue('blob:test');
    const open = spyOn(window, 'open').and.returnValue({} as Window);

    component.printLabel();

    expect(service.downloadConsignmentLabel).toHaveBeenCalledWith(UUID);
    expect(open).toHaveBeenCalledWith('blob:test', '_blank');
  });

  it('says so when a pop-up blocker swallows the label', async () => {
    await setup(consignment({ status: 'BOOKED' }), booking({ masterAwb: 'AWB-1' }));
    fixture.detectChanges();
    const messages = fixture.debugElement.injector.get(MessageService);
    spyOn(messages, 'add');

    spyOn(URL, 'createObjectURL').and.returnValue('blob:test');
    spyOn(window, 'open').and.returnValue(null);

    component.printLabel();

    expect(messages.add).toHaveBeenCalledWith(jasmine.objectContaining({
      summary: 'Pop-up blocked'
    }));
  });

  it('surfaces the servers reason when there is no label to give', async () => {
    await setup(consignment({ status: 'BOOKED' }), booking({ masterAwb: 'AWB-1' }));
    fixture.detectChanges();
    const messages = fixture.debugElement.injector.get(MessageService);
    spyOn(messages, 'add');

    service.downloadConsignmentLabel.and.returnValue(throwError(() => ({
      error: { message: 'This carrier issues its own labels.' }
    })));

    component.printLabel();

    expect(messages.add).toHaveBeenCalledWith(jasmine.objectContaining({
      severity: 'error', detail: 'This carrier issues its own labels.'
    }));
  });

  // ── Rating (T-50) ─────────────────────────────────────────────────────────

  it('loads the price and the weights it was worked out from', async () => {
    await setup();
    fixture.detectChanges();

    expect(service.getConsignmentRate).toHaveBeenCalledWith(UUID);
    expect(service.getChargeableWeight).toHaveBeenCalledWith(UUID);
    expect(component.weight?.totalChargeableKg).toBe(12.5);
  });

  it('shows an unrated consignment as unpriced rather than free', async () => {
    await setup();
    fixture.detectChanges();

    expect(component.isRated).toBeFalse();
    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('[data-testid="not-rated"]')).toBeTruthy();
    expect(el.querySelector('[data-testid="freight-cost"]')).toBeNull();
  });

  it('prices a consignment and shows the charge lines', async () => {
    await setup();
    fixture.detectChanges();

    service.getConsignmentRate.and.returnValue(ok(rate()));
    component.rateNow();
    fixture.detectChanges();

    expect(service.rateConsignment).toHaveBeenCalledWith(UUID, { rateCardOnly: false });
    expect(component.rate?.freightCost).toBe(1260);

    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('[data-testid="freight-cost"]')?.textContent).toContain('1,260');
    expect(el.querySelectorAll('[data-testid="charge-lines"] tr').length).toBe(3);
  });

  it('says when the carrier would not quote and a card answered instead', async () => {
    // "The carrier quoted this" and "the carrier would not answer, so the card was used" are
    // different facts about the same number, and only one is worth chasing.
    await setup();
    fixture.detectChanges();
    const messages = fixture.debugElement.injector.get(MessageService);
    spyOn(messages, 'add');

    service.rateConsignment.and.returnValue(ok(rate({
      source: 'RATE_CARD',
      attempts: [{ source: 'CARRIER', succeeded: false, message: 'Postcode not serviceable.' },
                 { source: 'RATE_CARD', succeeded: true }]
    })));

    component.rateNow();

    expect(messages.add).toHaveBeenCalledWith(jasmine.objectContaining({
      summary: 'Priced from the rate card', detail: 'Postcode not serviceable.'
    }));
  });

  it('can price from the rate card without asking the carrier', async () => {
    await setup();
    fixture.detectChanges();

    component.rateNow(true);

    expect(service.rateConsignment).toHaveBeenCalledWith(UUID, { rateCardOnly: true });
  });

  it('names the three sources differently, because they are not equal in a dispute', async () => {
    await setup();

    expect(component.sourceLabelFor('CARRIER')).toBe('Quoted by the carrier');
    expect(component.sourceLabelFor('RATE_CARD')).toBe('Priced from a rate card');
    expect(component.sourceLabelFor('MANUAL')).toBe('Entered by hand');
    expect(component.sourceLabelFor(undefined)).toBe('Not priced');
  });

  it('offers pricing only before the carrier has the goods', async () => {
    await setup();
    fixture.detectChanges();
    expect(component.canRate).toBeTrue();

    await setup(consignment({ status: 'BOOKED' }), booking({ masterAwb: 'AWB-1' }));
    fixture.detectChanges();
    expect(component.canRate).toBeFalse();
  });

  it('shows how each package was charged, and flags an incomplete weight', async () => {
    await setup();
    service.getChargeableWeight.and.returnValue(ok(weight({
      isComplete: false,
      packages: [
        { packageUuid: 'p1', packageBarcode: 'PKG-001', packageType: 'BOX',
          actualKg: 1, volumetricKg: 4.8, chargeableKg: 4.8, basis: 'VOLUMETRIC', warnings: [] },
        { packageUuid: 'p2', packageBarcode: 'PKG-002', packageType: 'BOX',
          basis: 'UNKNOWN', warnings: ['This package has neither a weight nor dimensions.'] }
      ]
    })));
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('[data-testid="weight-table"]')).toBeTruthy();
    expect(el.querySelector('[data-testid="weight-incomplete"]')).toBeTruthy();
    expect(component.basisLabel('VOLUMETRIC')).toBe('By volume');
    expect(component.basisLabel('UNKNOWN')).toBe('Cannot be rated');
  });

  // ── A price obtained outside the system ───────────────────────────────────

  it('will not record a keyed-in price without an amount, a currency and a source', async () => {
    await setup();
    fixture.detectChanges();
    component.openManualRateDialog();

    expect(component.canSaveManualRate).toBeFalse();

    component.manualRateForm = { amount: 3250, currency: 'PKR', note: '' };
    expect(component.canSaveManualRate).withContext('provenance is required').toBeFalse();

    component.manualRateForm = { amount: 0, currency: 'PKR', note: 'Q-1' };
    expect(component.canSaveManualRate).withContext('zero is not a price').toBeFalse();

    component.manualRateForm = { amount: 3250, currency: 'RUPEES', note: 'Q-1' };
    expect(component.canSaveManualRate).withContext('three-letter ISO only').toBeFalse();

    component.manualRateForm = { amount: 3250, currency: 'pkr', note: 'Quotation Q-2026-881' };
    expect(component.canSaveManualRate).toBeTrue();
  });

  it('records a price given over the telephone, normalising the currency', async () => {
    await setup();
    fixture.detectChanges();

    component.manualRateForm = { amount: 3250, currency: ' pkr ', note: ' Quotation Q-881 ' };
    component.confirmManualRate();

    expect(service.setManualRate).toHaveBeenCalledWith(UUID, {
      amount: 3250, currency: 'PKR', note: 'Quotation Q-881'
    });
  });

  // ── Rate shopping ─────────────────────────────────────────────────────────

  it('compares carriers and shows why one won', async () => {
    await setup();
    fixture.detectChanges();

    component.openShopDialog();
    component.runShop();
    fixture.detectChanges();

    expect(service.shopRates).toHaveBeenCalled();

    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('[data-testid="recommendation"]')?.textContent)
      .toContain('Cheapest of 2');
    expect(el.querySelectorAll('[data-testid="shop-option"]').length).toBe(1);
  });

  it('shows what a deadline ruled out, with its price', async () => {
    // The cheapest option that misses by a day is exactly what somebody needs to see before
    // accepting the one that does not.
    await setup();
    fixture.detectChanges();

    service.shopRates.and.returnValue(ok(shop({
      excluded: [{
        carrierUuid: 'carrier-2', carrierName: 'Gamma Road', serviceCode: 'ROAD',
        totalAmount: 500, currency: 'PKR',
        reason: 'Arrives 2026-09-22, after the 2026-09-20 the goods are needed by.'
      }]
    })));

    component.runShop();
    fixture.detectChanges();

    const excluded: HTMLElement | null =
      fixture.nativeElement.querySelector('[data-testid="shop-excluded"]');

    expect(excluded?.textContent).toContain('Gamma Road');
    expect(excluded?.textContent).toContain('500');
    expect(excluded?.textContent).toContain('after the 2026-09-20');
  });

  it('says so when nothing could be compared', async () => {
    await setup();
    fixture.detectChanges();
    const messages = fixture.debugElement.injector.get(MessageService);
    spyOn(messages, 'add');

    service.shopRates.and.returnValue(ok(shop({
      options: [], recommended: undefined, recommendation: undefined,
      warnings: ['There are no active carriers to compare.']
    })));

    component.runShop();

    expect(messages.add).toHaveBeenCalledWith(jasmine.objectContaining({
      summary: 'Nothing to compare', detail: 'There are no active carriers to compare.'
    }));
  });

  it('accepts an option, passing the carrier, account and source back', async () => {
    await setup();
    fixture.detectChanges();
    component.runShop();

    component.acceptOption(option({ carrierAccountUuid: 'acct-1', source: 'CARRIER' }));

    expect(service.acceptRate).toHaveBeenCalledWith(UUID, jasmine.objectContaining({
      carrierUuid: 'carrier-1', carrierAccountUuid: 'acct-1',
      serviceCode: 'ROAD', source: 'CARRIER'
    }));
  });

  it('explains a stale option rather than storing it', async () => {
    // The price is re-quoted on accept, so a figure that went stale in the browser is refused.
    await setup();
    fixture.detectChanges();
    const messages = fixture.debugElement.injector.get(MessageService);
    spyOn(messages, 'add');

    service.acceptRate.and.returnValue(throwError(() => ({
      error: { message: "'ROAD' could not be quoted for that carrier now." }
    })));

    component.acceptOption(option());

    expect(messages.add).toHaveBeenCalledWith(jasmine.objectContaining({
      severity: 'error', detail: "'ROAD' could not be quoted for that carrier now."
    }));
  });

  // ── Shipping rules ────────────────────────────────────────────────────────

  it('shows which rule fired and why the earlier ones did not', async () => {
    await setup();
    fixture.detectChanges();

    component.evaluateRules();
    fixture.detectChanges();

    const verdicts = fixture.nativeElement.querySelectorAll('[data-testid="rule-verdict"]');
    expect(verdicts.length).toBe(2);
    expect(verdicts[0].textContent).toContain('Featherweight');
    expect(verdicts[0].textContent).toContain('not true of this consignment');
    expect(verdicts[1].textContent).toContain('Fired');
  });

  it('says what to do when no rule matches', async () => {
    await setup();
    fixture.detectChanges();
    const messages = fixture.debugElement.injector.get(MessageService);
    spyOn(messages, 'add');

    service.evaluateShippingRules.and.returnValue(ok(decision({
      matchedRule: undefined, considered: [], recommended: undefined,
      warnings: ['None of the 1 active rule(s) matches this consignment.']
    })));

    component.evaluateRules();

    expect(messages.add).toHaveBeenCalledWith(jasmine.objectContaining({
      summary: 'No rule matches'
    }));
    expect(component.canApplyRule).toBeFalse();
  });

  it('applies a rule only when one has been checked and priced', async () => {
    await setup();
    fixture.detectChanges();
    expect(component.canApplyRule).withContext('nothing checked yet').toBeFalse();

    component.evaluateRules();
    expect(component.canApplyRule).toBeTrue();

    component.applyRule();
    expect(service.applyShippingRule).toHaveBeenCalledWith(UUID);
  });

  // ── Tracking ──────────────────────────────────────────────────────────────

  it('refreshes tracking only once booked', async () => {
    await setup();
    fixture.detectChanges();
    expect(component.canRefreshTracking).toBeFalse();

    await setup(consignment({ status: 'BOOKED' }), booking({ masterAwb: 'AWB-1' }));
    fixture.detectChanges();
    expect(component.canRefreshTracking).toBeTrue();
  });

  it('is honest when the carrier was asked moments ago', async () => {
    // A rate limit doing its job is not a failure, and pretending it refreshed would be a lie.
    await setup(consignment({ status: 'BOOKED' }), booking({ masterAwb: 'AWB-1' }));
    fixture.detectChanges();
    const messages = fixture.debugElement.injector.get(MessageService);
    spyOn(messages, 'add');

    service.refreshConsignmentTracking.and.returnValue(
      ok({ polled: false, status: 'IN_TRANSIT', newEvents: 0 }));

    component.refreshTracking();

    expect(messages.add).toHaveBeenCalledWith(jasmine.objectContaining({
      severity: 'info', summary: 'Just asked'
    }));
  });

  it('reports a carrier that did not answer without claiming failure of the page', async () => {
    await setup(consignment({ status: 'BOOKED' }), booking({ masterAwb: 'AWB-1' }));
    fixture.detectChanges();
    const messages = fixture.debugElement.injector.get(MessageService);
    spyOn(messages, 'add');

    service.refreshConsignmentTracking.and.returnValue(
      ok({ polled: true, status: 'IN_TRANSIT', newEvents: 0, error: 'Carrier returned 503.' }));

    component.refreshTracking();

    expect(messages.add).toHaveBeenCalledWith(jasmine.objectContaining({
      severity: 'warn', detail: 'Carrier returned 503.'
    }));
    expect(component.isRefreshingTracking).toBeFalse();
  });

  it('counts what the refresh actually brought back', async () => {
    await setup(consignment({ status: 'BOOKED' }), booking({ masterAwb: 'AWB-1' }));
    fixture.detectChanges();
    const messages = fixture.debugElement.injector.get(MessageService);
    spyOn(messages, 'add');

    component.refreshTracking();

    expect(messages.add).toHaveBeenCalledWith(jasmine.objectContaining({
      detail: '2 new tracking event(s).'
    }));
  });

  it('renders where each event came from', async () => {
    await setup();
    service.getConsignmentTracking.and.returnValue(ok([
      trackingEvent({ source: 'WEBHOOK' }), trackingEvent({ source: 'POLL' })
    ]));
    fixture.detectChanges();

    expect(component.tracking.length).toBe(2);
    expect(component.sourceLabel('WEBHOOK')).toBe('Carrier push');
    expect(component.sourceLabel('POLL')).toBe('Polled');
  });

  // ── Display ───────────────────────────────────────────────────────────────

  it('renders statuses as readable text with a severity', async () => {
    await setup();
    expect(component.formatStatus('OUT_FOR_DELIVERY')).toBe('Out For Delivery');
    expect(component.formatStatus(undefined)).toBe('');
    expect(component.getStatusSeverity('BOOKING_FAILED')).toBe('danger');
    expect(component.getStatusSeverity('WHATEVER')).toBe('secondary');
  });

  it('offers the default account plus the active ones', async () => {
    await setup();
    service.getCarrierAccounts.and.returnValue(ok([
      { uuid: 'a1', accountName: 'Domestic', isActive: true, isSandbox: false } as any,
      { uuid: 'a2', accountName: 'Test', isActive: true, isSandbox: true } as any,
      { uuid: 'a3', accountName: 'Old', isActive: false, isSandbox: false } as any
    ]));
    fixture.detectChanges();

    const options = component.accountOptions;
    expect(options[0].value).toBeNull();
    expect(options.map(o => o.value)).not.toContain('a3');
    expect(options.find(o => o.value === 'a2')!.label).toContain('sandbox');
  });
});
