import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { CarrierRateCardsComponent } from './rate-cards.component';
import {
  LogisticsService, RateCardModel, CarrierServiceModel
} from '../../../../services/logistics.service';

const CARRIER = '22222222-2222-2222-2222-222222222222';

function card(overrides: Partial<RateCardModel> = {}): RateCardModel {
  return {
    uuid: 'card-1', carrierUuid: CARRIER, carrierName: 'Beta Road',
    name: 'Domestic 2026', currency: 'PKR',
    effectiveFrom: '2026-01-01T00:00:00Z',
    isActive: true, isInEffect: true, createdDate: '2026-01-01T00:00:00Z',
    fuelSurchargePercent: 12,
    lanes: [{
      uuid: 'lane-1', name: 'Anywhere',
      breaks: [
        { uuid: 'b1', fromWeightKg: 0, basis: 'PER_KG', amount: 200 },
        { uuid: 'b2', fromWeightKg: 5, basis: 'PER_KG', amount: 150 }
      ]
    }],
    ...overrides
  };
}

function service(overrides: Partial<CarrierServiceModel> = {}): CarrierServiceModel {
  return {
    uuid: 's1', carrierUuid: CARRIER, carrierName: 'Beta Road',
    serviceCode: 'ROAD', serviceName: 'Road freight',
    supportsCod: false, supportsHazardous: false,
    isDefault: true, isActive: true, chargesVolumetricWeight: false,
    createdDate: '2026-01-01T00:00:00Z',
    ...overrides
  };
}

function ok<T>(result: T) {
  return of({ success: true, message: '', result } as any);
}

describe('CarrierRateCardsComponent', () => {
  let fixture: ComponentFixture<CarrierRateCardsComponent>;
  let component: CarrierRateCardsComponent;
  let api: jasmine.SpyObj<LogisticsService>;

  async function setup(cards: RateCardModel[] = [card()]) {
    api = jasmine.createSpyObj<LogisticsService>('LogisticsService', [
      'getRateCards', 'getRateCardById', 'createRateCard', 'patchRateCard', 'deleteRateCard',
      'quoteRateCard', 'getCarrierServices'
    ]);

    api.getRateCards.and.returnValue(ok(cards));
    api.getCarrierServices.and.returnValue(ok([service()]));
    api.createRateCard.and.returnValue(ok('new-card'));
    api.patchRateCard.and.returnValue(of({ success: true, message: '' } as any));
    api.deleteRateCard.and.returnValue(of({ success: true, message: '' } as any));
    api.quoteRateCard.and.returnValue(ok({ status: 'Quoted', quoted: true,
      cardName: 'Domestic 2026', laneName: 'Anywhere',
      option: { carrierUuid: CARRIER, carrierName: 'Beta Road', serviceCode: 'ROAD',
                source: 'RATE_CARD', totalAmount: 1400, currency: 'PKR',
                surcharges: [], isGuaranteed: false } }));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [CarrierRateCardsComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        provideNoopAnimations(), MessageService,
        { provide: LogisticsService, useValue: api },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['uuid', CARRIER]]) } } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(CarrierRateCardsComponent);
    component = fixture.componentInstance;
  }

  // ── Reading ───────────────────────────────────────────────────────────────

  it('lists a carrier\'s cards with their lanes and breaks', async () => {
    await setup();
    fixture.detectChanges();

    expect(api.getRateCards).toHaveBeenCalledWith(CARRIER);

    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelectorAll('[data-testid="rate-card"]').length).toBe(1);
    expect(el.querySelector('[data-testid="rate-card-lane"]')?.textContent).toContain('from 5 kg');
  });

  it('says so when a carrier has no tariff at all', async () => {
    await setup([]);
    fixture.detectChanges();

    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('[data-testid="no-cards"]')?.textContent)
      .toContain('most carriers have none');
  });

  it('distinguishes in effect, starting later, expired and switched off', async () => {
    // Four states, not two: a card configured for next quarter and one that lapsed last year read
    // very differently to somebody deciding whether their tariff is live.
    await setup();

    expect(component.statusOf(card()).label).toBe('In effect');
    expect(component.statusOf(card({ isActive: false })).label).toBe('Switched off');
    expect(component.statusOf(card({
      isInEffect: false, effectiveFrom: '2099-01-01T00:00:00Z'
    })).label).toBe('Starts later');
    expect(component.statusOf(card({
      isInEffect: false, effectiveFrom: '2020-01-01T00:00:00Z'
    })).label).toBe('Expired');
  });

  it('names an unnamed lane by what it matches', async () => {
    await setup();

    expect(component.laneLabel({ name: 'Lahore' })).toBe('Lahore');
    expect(component.laneLabel({})).toBe('Anywhere');
    expect(component.laneLabel({ destinationCountryIso: 'PK', destinationPostcodePrefix: '54' }))
      .toBe('anywhere → PK 54');
  });

  // ── Editing ───────────────────────────────────────────────────────────────

  it('starts a new card with a catch-all lane whose breaks begin at zero', async () => {
    // A card with no lanes prices nothing, and a lane whose lowest break is above zero has a hole
    // under it. The editor seeds both rather than letting somebody find out from a server error.
    await setup();
    fixture.detectChanges();

    component.openCreate();

    expect(component.lanes.length).toBe(1);
    expect(component.lanes[0].breaks[0].fromWeightKg).toBe(0);
  });

  it('refuses a tariff with a hole in it, and says where', async () => {
    await setup();
    fixture.detectChanges();
    component.openCreate();
    component.form.name = 'Domestic';

    component.lanes[0].breaks = [{ fromWeightKg: 5, basis: 'PER_KG', amount: 100 }];
    expect(component.validationError).toContain('must have a break starting at 0 kg');

    component.lanes[0].breaks = [
      { fromWeightKg: 0, basis: 'PER_KG', amount: 100 },
      { fromWeightKg: 0, basis: 'PER_KG', amount: 90 }
    ];
    expect(component.validationError).toContain('One weight, one rate');

    component.lanes[0].breaks = [{ fromWeightKg: 0, basis: 'PER_KG', amount: 0 }];
    expect(component.validationError).toContain('positive rate');

    component.lanes[0].breaks = [{ fromWeightKg: 0, basis: 'PER_KG', amount: 100 }];
    expect(component.validationError).toBeNull();
  });

  it('refuses a card that stops applying before it starts', async () => {
    await setup();
    fixture.detectChanges();
    component.openCreate();
    component.form.name = 'Domestic';
    component.lanes[0].breaks = [{ fromWeightKg: 0, basis: 'PER_KG', amount: 100 }];

    component.form.effectiveFrom = new Date('2026-06-01');
    component.form.effectiveTo   = new Date('2026-01-01');

    expect(component.validationError).toContain('before it starts');
  });

  it('refuses a currency that is not three letters', async () => {
    await setup();
    fixture.detectChanges();
    component.openCreate();
    component.form.name = 'Domestic';
    component.form.currency = 'RUPEES';

    expect(component.validationError).toContain('three ISO letters');
  });

  it('creates a card with its lanes and breaks, normalising the currency', async () => {
    await setup();
    fixture.detectChanges();
    component.openCreate();

    component.form.name = ' Domestic 2026 ';
    component.form.currency = 'pkr';
    component.form.effectiveFrom = new Date('2026-01-01T00:00:00Z');
    component.form.fuelSurchargePercent = 12;
    component.lanes[0].breaks = [{ fromWeightKg: 0, basis: 'PER_KG', amount: 150 }];

    component.save();

    expect(api.createRateCard).toHaveBeenCalledWith(jasmine.objectContaining({
      carrierUuid: CARRIER, name: 'Domestic 2026', currency: 'PKR', fuelSurchargePercent: 12
    }));

    const sent = api.createRateCard.calls.mostRecent().args[0];
    expect(sent.lanes[0].breaks).toEqual([{ fromWeightKg: 0, basis: 'PER_KG', amount: 150 }]);
  });

  it('loads a card into the editor whole, and saves it whole', async () => {
    // A card is edited as a whole so it never spends a moment with a hole in it — the same reason
    // the server replaces every lane when lanes are sent.
    await setup();
    fixture.detectChanges();

    component.openEdit(card());
    expect(component.lanes[0].breaks.length).toBe(2);

    component.save();

    const sent = api.patchRateCard.calls.mostRecent().args[1];
    expect(sent.lanes?.length).toBe(1);
    expect(sent.lanes![0].breaks.length).toBe(2);
  });

  it('clears a term that was emptied rather than leaving it alone', async () => {
    // A null on a patch means "leave alone", so emptying a field on the form would otherwise be
    // silently ignored.
    await setup();
    fixture.detectChanges();

    component.openEdit(card({ fuelSurchargePercent: 12, minimumCharge: 500 }));
    component.form.fuelSurchargePercent = null;
    component.form.minimumCharge = null;

    component.save();

    const sent = api.patchRateCard.calls.mostRecent().args[1];
    expect(sent.clearTerms).toContain('FUEL_SURCHARGE');
    expect(sent.clearTerms).toContain('MINIMUM_CHARGE');
  });

  it('surfaces the servers reason when a card overlaps another', async () => {
    await setup();
    fixture.detectChanges();
    const messages = fixture.debugElement.injector.get(MessageService);
    spyOn(messages, 'add');

    api.createRateCard.and.returnValue(throwError(() => ({
      error: { message: "'2026' already prices every service on Beta Road between 2026-01-01 and further notice." }
    })));

    component.openCreate();
    component.form.name = 'Also 2026';
    component.lanes[0].breaks = [{ fromWeightKg: 0, basis: 'PER_KG', amount: 100 }];
    component.save();

    expect(messages.add).toHaveBeenCalledWith(jasmine.objectContaining({
      severity: 'error', detail: jasmine.stringContaining('already prices')
    }));
  });

  // ── The dry run ───────────────────────────────────────────────────────────

  it('tries a weight against the cards and shows the price', async () => {
    await setup();
    fixture.detectChanges();

    component.openQuoteDialog();
    component.quoteForm.chargeableWeightKg = 10;
    component.quoteForm.destinationPostcode = '54000';
    component.runQuote();
    fixture.detectChanges();

    expect(api.quoteRateCard).toHaveBeenCalledWith(jasmine.objectContaining({
      carrierUuid: CARRIER, chargeableWeightKg: 10, destinationPostcode: '54000'
    }));

    expect(fixture.nativeElement.querySelector('[data-testid="quote-result"]')?.textContent)
      .toContain('1,400');
  });

  it('says no price rather than showing nothing when no lane matches', async () => {
    // "No price" and "priced at nothing" look identical on a screen and mean opposite things.
    await setup();
    fixture.detectChanges();

    api.quoteRateCard.and.returnValue(ok({
      status: 'NoLane', quoted: false,
      explanation: "'Domestic 2026' has no lane covering PK 74000 to AE 00000."
    }));

    component.openQuoteDialog();
    component.runQuote();
    fixture.detectChanges();

    const none = fixture.nativeElement.querySelector('[data-testid="quote-none"]');
    expect(none?.textContent).toContain('No price');
    expect(none?.textContent).toContain('no lane covering');
  });
});
