import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { CarrierScorecardComponent } from './carrier-scorecard.component';
import {
  LogisticsService, CarrierScorecardModel, CarrierScoreModel, CarrierListItemModel
} from '../../../../services/logistics.service';

function carrier(overrides: Partial<CarrierScoreModel> = {}): CarrierScoreModel {
  return {
    carrierUuid: 'carrier-1', carrierName: 'Beta Road',
    consignments: 40, delivered: 36, returnedToOrigin: 2, lost: 0, stillMoving: 2,
    judgeable: 30, onTime: 27, late: 3, onTimePercent: 90, averageDaysLate: 2.5, notJudgeable: 6,
    exceptions: 5, criticalExceptions: 1, stillOpen: 2, exceptionsPer100: 12.5,
    averageHoursToResolve: 18, stuckNow: 1,
    defensible: 30, proofCoveragePercent: 83.3,
    billing: {
      invoiced: 30, overcharged: 3, undercharged: 1, accuracyPercent: 86.7,
      variance: [{ currency: 'PKR', variance: 4200, consignments: 4 }],
      notYetInvoiced: 10
    },
    warnings: [],
    ...overrides
  };
}

function card(overrides: Partial<CarrierScorecardModel> = {}): CarrierScorecardModel {
  return {
    from: '2026-06-20T00:00:00Z', to: '2026-09-18T00:00:00Z',
    sortedBy: 'VOLUME', carriers: [carrier()], warnings: [],
    ...overrides
  };
}

function ok<T>(result: T) {
  return of({ success: true, message: '', result } as any);
}

describe('CarrierScorecardComponent', () => {
  let fixture: ComponentFixture<CarrierScorecardComponent>;
  let component: CarrierScorecardComponent;
  let api: jasmine.SpyObj<LogisticsService>;

  async function setup(scorecard: CarrierScorecardModel = card()) {
    api = jasmine.createSpyObj<LogisticsService>('LogisticsService', [
      'getCarrierScorecard', 'getActiveCarriers'
    ]);

    api.getCarrierScorecard.and.returnValue(ok(scorecard));
    api.getActiveCarriers.and.returnValue(
      ok([{ uuid: 'carrier-1', name: 'Beta Road' } as CarrierListItemModel]));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [CarrierScorecardComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        provideNoopAnimations(), MessageService,
        { provide: LogisticsService, useValue: api }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(CarrierScorecardComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  function el(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  function text(testId: string): string {
    return el(testId)?.textContent?.trim() ?? '';
  }

  // ── Loading ─────────────────────────────────────────────────────────────────

  it('loads the last ninety days on open', async () => {
    await setup();

    expect(api.getCarrierScorecard).toHaveBeenCalled();

    const filter = api.getCarrierScorecard.calls.mostRecent().args[0]!;
    const days = (new Date(filter.to!).getTime() - new Date(filter.from!).getTime()) / 86_400_000;

    expect(Math.round(days)).toBe(90);
  });

  it('survives a scorecard that will not load', async () => {
    api = jasmine.createSpyObj<LogisticsService>('LogisticsService', [
      'getCarrierScorecard', 'getActiveCarriers'
    ]);
    api.getActiveCarriers.and.returnValue(ok([]));
    api.getCarrierScorecard.and.returnValue(throwError(() => ({ error: { message: 'No.' } })));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [CarrierScorecardComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        provideNoopAnimations(), MessageService,
        { provide: LogisticsService, useValue: api }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(CarrierScorecardComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();

    expect(component.scorecard).toBeNull();
    expect(component.isLoading).withContext('the spinner must not spin forever').toBeFalse();
  });

  // ── Saying a figure honestly ────────────────────────────────────────────────

  it('shows a dash where nothing could be judged, never a zero', async () => {
    await setup();

    expect(component.percent(90)).toBe('90%');
    expect(component.percent(0)).toBe('0%');
    expect(component.percent(undefined))
      .withContext('a carrier that was late and one nothing could be judged about are different')
      .toBe('—');
  });

  it('shows every percentage with the count it came from', async () => {
    await setup();

    expect(component.outOf(27, 30)).toBe('27 of 30');
    expect(text('ontime-carrier-1')).toBe('90%');
    expect(fixture.nativeElement.textContent).toContain('27 of 30');
  });

  it('says when a sample is too small to compare on', async () => {
    await setup(card({ carriers: [carrier({ consignments: 4 })] }));

    expect(component.isThinSample(component.carriersOnCard[0])).toBeTrue();
    expect(text('thin-carrier-1')).toContain('too few to compare');
  });

  it('does not call a large sample thin', async () => {
    await setup();

    expect(component.isThinSample(component.carriersOnCard[0])).toBeFalse();
  });

  it('shows how many deliveries had no ETA, rather than folding them into the rate', async () => {
    await setup();

    expect(text('unjudged-carrier-1')).toContain('6 had no ETA');
  });

  it('shows what never arrived, so a good on-time figure cannot hide it', async () => {
    await setup(card({ carriers: [carrier({ lost: 3, returnedToOrigin: 2 })] }));

    expect(text('bad-outcomes-carrier-1')).toContain('3 lost');
    expect(text('bad-outcomes-carrier-1')).toContain('2 returned');
  });

  it('shows what is stuck right now', async () => {
    await setup();

    expect(text('stuck-carrier-1')).toContain('1 stuck right now');
  });

  // ── Grading ─────────────────────────────────────────────────────────────────

  it('grades on time, exceptions, evidence and billing on their own scales', async () => {
    await setup();

    expect(component.onTimeTag(carrier({ onTimePercent: 97 }))).toBe('success');
    expect(component.onTimeTag(carrier({ onTimePercent: 88 }))).toBe('warn');
    expect(component.onTimeTag(carrier({ onTimePercent: 60 }))).toBe('danger');
    expect(component.onTimeTag(carrier({ onTimePercent: undefined }))).toBe('secondary');

    expect(component.exceptionTag(carrier({ criticalExceptions: 1 }))).toBe('danger');
    expect(component.exceptionTag(carrier({ criticalExceptions: 0, exceptionsPer100: 20 }))).toBe('warn');
    expect(component.exceptionTag(carrier({ criticalExceptions: 0, exceptionsPer100: 1 }))).toBe('secondary');

    expect(component.proofTag(carrier({ proofCoveragePercent: 99 }))).toBe('success');
    expect(component.proofTag(carrier({ proofCoveragePercent: undefined }))).toBe('secondary');
  });

  // ── Money ───────────────────────────────────────────────────────────────────

  it('signs a variance, because the direction is the point', async () => {
    await setup();

    expect(component.varianceLabel(4200, 'PKR')).toBe('+4200.00 PKR');
    expect(component.varianceLabel(-310.5, 'AED')).toBe('-310.50 AED');
  });

  it('lists variance per currency and never adds them together', async () => {
    await setup(card({
      carriers: [carrier({
        billing: {
          invoiced: 6, overcharged: 2, undercharged: 0, accuracyPercent: 66.7,
          variance: [
            { currency: 'PKR', variance: 4200, consignments: 3 },
            { currency: 'AED', variance: 50, consignments: 1 }
          ],
          notYetInvoiced: 0
        }
      })]
    }));

    const variance = text('variance-carrier-1');

    expect(variance).toContain('+4200.00 PKR');
    expect(variance).toContain('+50.00 AED');
    expect(variance).withContext('rupees plus dirhams is not money').not.toContain('4250');
  });

  it('counts undercharging separately, because it is not good news either', async () => {
    await setup();

    expect(fixture.nativeElement.textContent).toContain('1 under');
  });

  it('hides the billing column entirely when the server withheld it', async () => {
    await setup(card({
      carriers: [carrier({ billing: undefined })],
      warnings: ['Billing accuracy is not shown. It needs FREIGHT_INVOICE_VIEW — what the company '
               + 'pays to move goods is a separate question from how well they moved.']
    }));

    expect(component.billingWithheld).toBeTrue();
    expect(el('billing-carrier-1'))
      .withContext('a withheld figure must not be drawn as zero').toBeNull();
    expect(text('card-warnings')).toContain('FREIGHT_INVOICE_VIEW');
  });

  // ── No overall score ────────────────────────────────────────────────────────

  it('never combines the measures into one number, and says why on the page', async () => {
    await setup();

    expect(text('no-score')).toContain('weights nobody has agreed');
    expect(Object.keys(component.carriersOnCard[0]))
      .not.toContain('score');
  });

  it('sorts by whichever question is being asked, server-side', async () => {
    await setup();

    component.filter.sortBy = 'ON_TIME';
    component.load();

    expect(api.getCarrierScorecard.calls.mostRecent().args[0]?.sortBy).toBe('ON_TIME');
  });

  it('narrows to one carrier', async () => {
    await setup();

    component.filter.carrierUuid = 'carrier-1';
    component.load();

    expect(api.getCarrierScorecard.calls.mostRecent().args[0]?.carrierUuid).toBe('carrier-1');
  });

  // ── The cohort ──────────────────────────────────────────────────────────────

  it('says the cohort is what was collected, not what was delivered', async () => {
    await setup();

    expect(text('cohort')).toContain('collected');
  });

  it('shows a carrier’s own caveats beside its figures', async () => {
    await setup(card({
      carriers: [carrier({ warnings: ['6 delivered consignment(s) had no ETA.'] })]
    }));

    expect(text('warning-carrier-1')).toContain('had no ETA');
  });

  it('says plainly when there is nothing to compare', async () => {
    await setup(card({
      carriers: [],
      warnings: ['Nothing was collected by a carrier in this window, so there is nothing to compare.']
    }));

    expect(text('empty')).toContain('nothing to compare');
  });
});
