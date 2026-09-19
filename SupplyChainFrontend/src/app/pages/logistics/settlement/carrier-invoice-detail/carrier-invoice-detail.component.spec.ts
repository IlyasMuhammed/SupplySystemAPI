import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { CarrierInvoiceDetailComponent } from './carrier-invoice-detail.component';
import {
  LogisticsService, CarrierInvoiceModel, InvoiceMatchResultModel,
  InvoiceLineMatchModel, ThreeWayMatchModel
} from '../../../../services/logistics.service';

const UUID = 'inv-1';

function invoice(overrides: Partial<CarrierInvoiceModel> = {}): CarrierInvoiceModel {
  return {
    uuid: UUID, carrierUuid: 'carrier-1', carrierName: 'Beta Road',
    invoiceNumber: 'INV-9001', invoiceDate: '2026-09-01T00:00:00Z',
    currency: 'PKR', totalAmount: 1400, lineTotal: 1400,
    status: 'RECEIVED', lineCount: 1, createdDate: '2026-09-01T00:00:00Z',
    lines: [],
    ...overrides
  };
}

function line(overrides: Partial<InvoiceLineMatchModel> = {}): InvoiceLineMatchModel {
  return {
    lineUuid: 'line-1', lineNo: 1, description: 'Carriage',
    invoiceUuid: UUID, invoiceNumber: 'INV-9001', carrierName: 'Beta Road',
    awbNumber: 'AWB-1', amount: 1400, currency: 'PKR',
    matchStatus: 'MATCHED', matchMethod: 'AWB',
    matchedConsignmentUuid: 'con-1', matchedConsignmentNumber: 'SHP-2026-00042',
    ...overrides
  };
}

function matches(overrides: Partial<InvoiceMatchResultModel> = {}): InvoiceMatchResultModel {
  return {
    invoiceUuid: UUID, invoiceNumber: 'INV-9001',
    lineCount: 1, matched: 1, ambiguous: 0, unmatched: 0, excluded: 0,
    isComplete: true, lines: [line()], warnings: [],
    ...overrides
  };
}

function threeWay(overrides: Partial<ThreeWayMatchModel> = {}): ThreeWayMatchModel {
  return {
    invoiceUuid: UUID, invoiceNumber: 'INV-9001', carrierName: 'Beta Road', currency: 'PKR',
    status: 'RECEIVED', invoiceTotal: 1400, expectedTotal: 1260, varianceTotal: 140,
    consignmentCount: 1, withinTolerance: 0, overcharged: 1, undercharged: 0, notAccrued: 0,
    unmatchedAmount: 0, unmatchedLines: 0,
    tolerancePercent: 2, toleranceAmount: 0, isClean: false,
    consignments: [{
      consignmentUuid: 'con-1', consignmentNumber: 'SHP-2026-00042', masterAwb: 'AWB-1',
      quotedAmount: 1260, bookedAmount: 1260, invoicedAmount: 1400,
      expectedAmount: 1260, expectedBasis: 'BOOKED', currency: 'PKR',
      varianceAmount: 140, variancePercent: 11.11, outcome: 'OVERCHARGED',
      varianceReason: 'WEIGHT',
      varianceNote: 'The carrier billed on 14 kg where we made it 12.5 kg.',
      lines: []
    }],
    warnings: [],
    ...overrides
  };
}

function ok<T>(result: T) {
  return of({ success: true, message: '', result } as any);
}

describe('CarrierInvoiceDetailComponent', () => {
  let fixture: ComponentFixture<CarrierInvoiceDetailComponent>;
  let component: CarrierInvoiceDetailComponent;
  let api: jasmine.SpyObj<LogisticsService>;

  async function setup(
    detail: CarrierInvoiceModel | null = invoice(),
    match: InvoiceMatchResultModel = matches(),
    comparison: ThreeWayMatchModel = threeWay()) {

    api = jasmine.createSpyObj<LogisticsService>('LogisticsService', [
      'getCarrierInvoiceById', 'getInvoiceMatches', 'matchCarrierInvoice',
      'previewThreeWayMatch', 'runThreeWayMatch',
      'getMatchCandidates', 'matchInvoiceLine', 'excludeInvoiceLine', 'unmatchInvoiceLine'
    ]);

    api.getCarrierInvoiceById.and.returnValue(
      detail ? ok(detail) : of({ success: false, message: 'not found', result: null } as any));
    api.getInvoiceMatches.and.returnValue(ok(match));
    api.matchCarrierInvoice.and.returnValue(ok(match));
    api.previewThreeWayMatch.and.returnValue(ok(comparison));
    api.runThreeWayMatch.and.returnValue(ok(comparison));
    api.getMatchCandidates.and.returnValue(ok([{
      consignmentUuid: 'con-1', consignmentNumber: 'SHP-2026-00042', masterAwb: 'AWB-1',
      status: 'DELIVERED', freightCost: 1260, freightCurrency: 'PKR',
      reason: 'The airway bill matches.'
    }]));
    api.matchInvoiceLine.and.returnValue(of({ success: true, message: '' } as any));
    api.excludeInvoiceLine.and.returnValue(of({ success: true, message: '' } as any));
    api.unmatchInvoiceLine.and.returnValue(of({ success: true, message: '' } as any));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [CarrierInvoiceDetailComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        provideNoopAnimations(), MessageService,
        { provide: LogisticsService, useValue: api },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['uuid', UUID]]) } } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(CarrierInvoiceDetailComponent);
    component = fixture.componentInstance;
  }

  // ── Loading ───────────────────────────────────────────────────────────────

  it('loads the bill, its matches and the comparison', async () => {
    await setup();
    fixture.detectChanges();

    expect(api.getCarrierInvoiceById).toHaveBeenCalledWith(UUID);
    expect(api.getInvoiceMatches).toHaveBeenCalledWith(UUID);
    expect(api.previewThreeWayMatch).toHaveBeenCalledWith(UUID);
  });

  it('uses the preview on load, which records nothing', async () => {
    await setup();
    fixture.detectChanges();

    expect(api.runThreeWayMatch).not.toHaveBeenCalled();
  });

  it('shows not found for a 404', async () => {
    await setup();
    api.getCarrierInvoiceById.and.returnValue(throwError(() => ({ status: 404 })));
    fixture.detectChanges();

    expect(component.notFound).toBeTrue();
  });

  // ── The comparison ────────────────────────────────────────────────────────

  it('shows the three figures side by side with the named reason', async () => {
    await setup();
    fixture.detectChanges();

    const row: HTMLElement = fixture.nativeElement.querySelector('[data-testid="comparison-row"]');

    expect(row.textContent).toContain('1,260');   // quoted and booked
    expect(row.textContent).toContain('1,400');   // billed
    expect(row.textContent).toContain('140');     // difference
    expect(row.textContent).toContain('Weight');
    expect(row.textContent).toContain('14 kg where we made it 12.5 kg');
  });

  it('says which figure the bill was measured against', async () => {
    await setup();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="comparison-row"]').textContent)
      .toContain('used: booked');
  });

  it('reports the tolerance so a verdict can be reproduced', async () => {
    await setup();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="tolerance"]')?.textContent)
      .toContain('2%');
  });

  it('says there is nothing to compare when no line is tied to a movement', async () => {
    await setup(invoice(), matches(), threeWay({
      consignments: [], consignmentCount: 0,
      warnings: ['No line on this bill is tied to a movement, so there is nothing to compare.']
    }));
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="nothing-compared"]')?.textContent)
      .toContain('Match the lines first');
  });

  it('names how strong each match is', async () => {
    await setup();

    expect(component.methodLabel('AWB')).toBe('on the airway bill');
    expect(component.methodLabel('MANUAL')).toBe('by hand');
  });

  // ── Matching ──────────────────────────────────────────────────────────────

  it('says how many lines still need a person rather than simply reporting success', async () => {
    // The lines nothing matched are where a wrong charge goes unnoticed.
    await setup();
    fixture.detectChanges();
    const messages = fixture.debugElement.injector.get(MessageService);
    spyOn(messages, 'add');

    api.matchCarrierInvoice.and.returnValue(ok(matches({
      matched: 1, unmatched: 2, ambiguous: 1, isComplete: false
    })));

    component.runMatching();

    expect(messages.add).toHaveBeenCalledWith(jasmine.objectContaining({
      severity: 'warn', detail: '3 line(s) could not be tied to a movement.'
    }));
  });

  it('reports a clean bill and a disputed one differently', async () => {
    await setup();
    fixture.detectChanges();
    const messages = fixture.debugElement.injector.get(MessageService);
    spyOn(messages, 'add');

    component.runThreeWay();

    expect(messages.add).toHaveBeenCalledWith(jasmine.objectContaining({
      severity: 'warn', summary: 'Bill disputed'
    }));

    api.runThreeWayMatch.and.returnValue(ok(threeWay({
      isClean: true, overcharged: 0, withinTolerance: 1, varianceTotal: 0
    })));

    component.runThreeWay();

    expect(messages.add).toHaveBeenCalledWith(jasmine.objectContaining({
      severity: 'success', summary: 'Bill agrees'
    }));
  });

  it('offers nothing to do on a withdrawn bill', async () => {
    await setup(invoice({ status: 'CANCELLED', cancelReason: 'Duplicate' }));
    fixture.detectChanges();

    expect(component.isWithdrawn).toBeTrue();
    expect(component.canWork).toBeFalse();
    expect(fixture.nativeElement.querySelector('[data-testid="banner-withdrawn"]')).toBeTruthy();
  });

  // ── Deciding a line ───────────────────────────────────────────────────────

  it('offers candidates with a reason each when matching by hand', async () => {
    await setup();
    fixture.detectChanges();

    component.openLine(line({ matchStatus: 'UNMATCHED' }), 'match');
    fixture.detectChanges();

    expect(api.getMatchCandidates).toHaveBeenCalledWith('line-1');
    expect(fixture.nativeElement.querySelector('[data-testid="candidate"]')?.textContent)
      .toContain('The airway bill matches.');
  });

  it('will not match by hand without both a movement and a reason', async () => {
    await setup();
    fixture.detectChanges();

    component.openLine(line({ matchStatus: 'UNMATCHED' }), 'match');
    expect(component.canDecide).toBeFalse();

    component.chosenConsignment = 'con-1';
    expect(component.canDecide).withContext('a note is still required').toBeFalse();

    component.lineNote = 'Carrier confirmed by email.';
    expect(component.canDecide).toBeTrue();

    component.confirmLine();
    expect(api.matchInvoiceLine)
      .toHaveBeenCalledWith('line-1', 'con-1', 'Carrier confirmed by email.');
  });

  it('needs only a reason to set a line aside', async () => {
    await setup();
    fixture.detectChanges();

    component.openLine(line({ matchStatus: 'UNMATCHED' }), 'exclude');
    expect(component.canDecide).toBeFalse();

    component.lineNote = 'Monthly account charge, not carriage.';
    expect(component.canDecide).toBeTrue();

    component.confirmLine();
    expect(api.excludeInvoiceLine)
      .toHaveBeenCalledWith('line-1', 'Monthly account charge, not carriage.');
  });

  it('undoes a match with a reason', async () => {
    await setup();
    fixture.detectChanges();

    component.openLine(line(), 'unmatch');
    component.lineNote = 'Wrong movement — the airway bill was reused.';
    component.confirmLine();

    expect(api.unmatchInvoiceLine).toHaveBeenCalledWith(
      'line-1', 'Wrong movement — the airway bill was reused.');
  });

  it('titles the dialog by what is being decided', async () => {
    await setup();

    component.lineAction = 'match';   expect(component.dialogHeader).toContain('by hand');
    component.lineAction = 'exclude'; expect(component.dialogHeader).toContain('Not a movement charge');
    component.lineAction = 'unmatch'; expect(component.dialogHeader).toContain('Undo');
  });

  it('shows when a movement is already charged on another bill', async () => {
    await setup(invoice(), matches({
      lines: [line({ duplicateWarning: 'Also charged on INV-9000.' })]
    }));
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="duplicate-warning"]')?.textContent)
      .toContain('Also charged on INV-9000.');
  });
});
