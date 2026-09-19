import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { FreightAccrualsComponent } from './freight-accruals.component';
import {
  LogisticsService, FreightAccrualSummaryModel, UnaccruableConsignmentModel
} from '../../../../services/logistics.service';

function unaccruable(overrides: Partial<UnaccruableConsignmentModel> = {}): UnaccruableConsignmentModel {
  return {
    consignmentUuid: 'con-9', consignmentNumber: 'SHP-2026-00099',
    status: 'IN_TRANSIT', carrierName: 'Beta Road', masterAwb: 'AWB-9',
    dispatchedAt: '2026-09-01T00:00:00Z',
    reason: 'Dispatched but never priced, so there is no figure to accrue.',
    ...overrides
  };
}

function summary(overrides: Partial<FreightAccrualSummaryModel> = {}): FreightAccrualSummaryModel {
  return {
    asOf: '2026-09-18T00:00:00Z',
    openCount: 2, openTotal: 2520, currency: 'PKR',
    byCarrier: [{ carrierUuid: 'carrier-1', carrierName: 'Beta Road', currency: 'PKR', count: 2, total: 2520 }],
    couldNotAccrue: [], warnings: [],
    ...overrides
  };
}

function ok<T>(result: T) {
  return of({ success: true, message: '', result } as any);
}

describe('FreightAccrualsComponent', () => {
  let fixture: ComponentFixture<FreightAccrualsComponent>;
  let component: FreightAccrualsComponent;
  let api: jasmine.SpyObj<LogisticsService>;

  async function setup(totals: FreightAccrualSummaryModel = summary()) {
    api = jasmine.createSpyObj<LogisticsService>('LogisticsService', [
      'getAccrualSummary', 'accrueConsignment', 'reverseAccrual'
    ]);

    api.getAccrualSummary.and.returnValue(ok(totals));
    api.accrueConsignment.and.returnValue(ok({} as any));
    api.reverseAccrual.and.returnValue(of({ success: true, message: '' } as any));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [FreightAccrualsComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        provideNoopAnimations(), MessageService,
        { provide: LogisticsService, useValue: api }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(FreightAccrualsComponent);
    component = fixture.componentInstance;
  }

  // ── Reading ───────────────────────────────────────────────────────────────

  it('shows what is owed, by carrier', async () => {
    await setup();
    fixture.detectChanges();

    expect(api.getAccrualSummary).toHaveBeenCalledWith(undefined);
    expect(fixture.nativeElement.querySelector('[data-testid="open-total"]')?.textContent)
      .toContain('2,520');
    expect(fixture.nativeElement.querySelector('[data-testid="by-carrier"]')?.textContent)
      .toContain('Beta Road');
  });

  it('strikes the balance as at a date, and says it is historic', async () => {
    // A month-end figure has to be asked for as at month end; a balance struck today cannot be
    // reconciled to a closed period.
    await setup();
    fixture.detectChanges();

    component.asOf = new Date('2026-08-31T23:59:00Z');
    component.load();
    fixture.detectChanges();

    expect(api.getAccrualSummary).toHaveBeenCalledWith('2026-08-31T23:59:00.000Z');
    expect(component.isHistoric).toBeTrue();
    expect(fixture.nativeElement.querySelector('[data-testid="historic-note"]')?.textContent)
      .toContain('not now');
  });

  it('goes back to today when the date is cleared', async () => {
    await setup();
    fixture.detectChanges();

    component.asOf = new Date('2026-08-31');
    component.clearDate();

    expect(component.asOf).toBeNull();
    expect(api.getAccrualSummary).toHaveBeenCalledWith(undefined);
  });

  it('does not add two currencies together', async () => {
    await setup(summary({
      currency: undefined, openTotal: 0,
      warnings: ['Open accruals are in PKR, USD.'],
      byCarrier: [
        { carrierName: 'Beta Road', currency: 'PKR', count: 1, total: 1260 },
        { carrierName: 'Alpha Air', currency: 'USD', count: 1, total: 20 }
      ]
    }));
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="mixed-currency"]')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('[data-testid="summary-warnings"]')?.textContent)
      .toContain('PKR, USD');
  });

  it('says so when nothing is outstanding', async () => {
    await setup(summary({ openCount: 0, openTotal: 0, byCarrier: [] }));
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="nothing-accrued"]')?.textContent)
      .toContain('billed or written back');
  });

  // ── What is missing from the balance ──────────────────────────────────────

  it('lists what moved without ever being priced, and says the total is understated', async () => {
    // Accruing them at zero would look correct and be wrong.
    await setup(summary({
      couldNotAccrue: [unaccruable()],
      warnings: ['1 consignment(s) have moved without ever being priced.']
    }));
    fixture.detectChanges();

    const panel = fixture.nativeElement.querySelector('[data-testid="could-not-accrue"]');

    expect(panel?.textContent).toContain('SHP-2026-00099');
    expect(panel?.textContent).toContain('understated by whatever they come to');
    expect(fixture.nativeElement.querySelectorAll('[data-testid="unaccruable-row"]').length).toBe(1);
  });

  it('hides the panel entirely when nothing is missing', async () => {
    await setup();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="could-not-accrue"]')).toBeNull();
  });

  it('accrues one that was missed', async () => {
    await setup(summary({ couldNotAccrue: [unaccruable()] }));
    fixture.detectChanges();

    component.openAccrue(unaccruable());
    component.confirmAccrue();

    expect(api.accrueConsignment).toHaveBeenCalledWith('con-9');
  });

  it('surfaces the servers refusal when it still has no price', async () => {
    await setup(summary({ couldNotAccrue: [unaccruable()] }));
    fixture.detectChanges();
    const messages = fixture.debugElement.injector.get(MessageService);
    spyOn(messages, 'add');

    api.accrueConsignment.and.returnValue(throwError(() => ({
      error: { message: 'It has moved but was never priced, so there is no figure to accrue.' }
    })));

    component.openAccrue(unaccruable());
    component.confirmAccrue();

    expect(messages.add).toHaveBeenCalledWith(jasmine.objectContaining({
      severity: 'error', detail: jasmine.stringContaining('never priced')
    }));
  });

  // ── Writing one back ──────────────────────────────────────────────────────

  it('will not write an accrual back without a reason', async () => {
    await setup();
    fixture.detectChanges();

    component.openReverse('con-1');
    expect(component.canReverse).toBeFalse();

    component.reverseReason = 'Accrued in error — this moved on the customer’s own account.';
    expect(component.canReverse).toBeTrue();

    component.confirmReverse();

    expect(api.reverseAccrual).toHaveBeenCalledWith(
      'con-1', 'Accrued in error — this moved on the customer’s own account.');
  });
});
