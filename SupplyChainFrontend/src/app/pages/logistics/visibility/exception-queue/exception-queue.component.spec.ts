import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { ExceptionQueueComponent } from './exception-queue.component';
import {
  LogisticsService, DeliveryExceptionModel, ExceptionSummaryModel, CarrierListItemModel
} from '../../../../services/logistics.service';

function exception(overrides: Partial<DeliveryExceptionModel> = {}): DeliveryExceptionModel {
  return {
    uuid: 'exc-1', consignmentUuid: 'con-1', consignmentNumber: 'SHP-2026-00042',
    consignmentStatus: 'EXCEPTION', masterAwb: 'AWB-1',
    carrierUuid: 'carrier-1', carrierName: 'Beta Road',
    exceptionType: 'CUSTOMS_HOLD', severity: 'CRITICAL', status: 'OPEN', source: 'CARRIER',
    description: 'Held pending commercial invoice',
    occurredAt: '2026-09-16T09:00:00Z', openForHours: 6,
    warnings: [],
    ...overrides
  };
}

function summary(overrides: Partial<ExceptionSummaryModel> = {}): ExceptionSummaryModel {
  return {
    open: 3, waiting: 1, critical: 2, unassigned: 2,
    byType: [{ exceptionType: 'CUSTOMS_HOLD', open: 2, critical: 2, unassigned: 1, oldestHours: 72 }],
    warnings: [],
    ...overrides
  };
}

function page<T>(data: T[]) {
  return of({
    success: true, message: '',
    result: { data, totalRecords: data.length, page: 1, pageSize: 20, totalPages: 1 }
  } as any);
}

function ok<T>(result: T) {
  return of({ success: true, message: '', result } as any);
}

describe('ExceptionQueueComponent', () => {
  let fixture: ComponentFixture<ExceptionQueueComponent>;
  let component: ExceptionQueueComponent;
  let api: jasmine.SpyObj<LogisticsService>;

  async function setup(
    exceptions: DeliveryExceptionModel[] = [exception()],
    totals: ExceptionSummaryModel = summary()) {

    api = jasmine.createSpyObj<LogisticsService>('LogisticsService', [
      'getExceptionSummary', 'getExceptions', 'patchException',
      'resolveException', 'withdrawException', 'getActiveCarriers'
    ]);

    api.getExceptionSummary.and.returnValue(ok(totals));
    api.getExceptions.and.returnValue(page(exceptions));
    api.getActiveCarriers.and.returnValue(
      ok([{ uuid: 'carrier-1', name: 'Beta Road' } as CarrierListItemModel]));
    api.patchException.and.returnValue(of({ success: true, message: '' } as any));
    api.resolveException.and.returnValue(of({ success: true, message: '' } as any));
    api.withdrawException.and.returnValue(of({ success: true, message: '' } as any));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [ExceptionQueueComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        provideNoopAnimations(), MessageService,
        { provide: LogisticsService, useValue: api }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(ExceptionQueueComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  function text(testId: string): string {
    const el = fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
    return el ? el.textContent.trim() : '';
  }

  // ── Loading ─────────────────────────────────────────────────────────────────

  it('loads the queue and its summary on open', async () => {
    await setup();

    expect(api.getExceptions).toHaveBeenCalled();
    expect(api.getExceptionSummary).toHaveBeenCalled();
    expect(component.exceptions.length).toBe(1);
  });

  it('shows what is critical and what nobody owns, because those are what get worked', async () => {
    await setup();

    expect(text('tile-critical')).toBe('2');
    expect(text('tile-unassigned')).toBe('2');
  });

  it('asks the server for everything still needing work by default', async () => {
    await setup();

    const filter = api.getExceptions.calls.mostRecent().args[0];

    expect(filter?.status).toBeUndefined('the server defaults to OPEN and WAITING');
    expect(filter?.severity).toBeUndefined();
  });

  it('survives a queue that will not load', async () => {
    api = jasmine.createSpyObj<LogisticsService>('LogisticsService', [
      'getExceptionSummary', 'getExceptions', 'patchException',
      'resolveException', 'withdrawException', 'getActiveCarriers'
    ]);
    api.getExceptionSummary.and.returnValue(ok(summary()));
    api.getActiveCarriers.and.returnValue(ok([]));
    api.getExceptions.and.returnValue(throwError(() => ({ error: { message: 'No.' } })));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [ExceptionQueueComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        provideNoopAnimations(), MessageService,
        { provide: LogisticsService, useValue: api }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(ExceptionQueueComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();

    expect(component.exceptions).toEqual([]);
    expect(component.isLoading).withContext('the spinner must not spin forever').toBeFalse();
  });

  // ── Reading a row ───────────────────────────────────────────────────────────

  it('says what each kind of exception means rather than showing its code', async () => {
    await setup();

    expect(component.typeLabel('CONSIGNEE_UNREACHABLE')).toBe('Nobody reachable');
    expect(component.typeLabel('COD_MISMATCH')).toBe('Cash does not agree');
  });

  it('distinguishes what the carrier reported from what went quiet', async () => {
    await setup();

    expect(component.sourceLabel('CARRIER')).toContain('carrier reported');
    expect(component.sourceLabel('SYSTEM')).toContain('went quiet');
    expect(component.sourceLabel('MANUAL')).toContain('Raised here');
  });

  it('says an age in days once hours stop meaning anything', async () => {
    await setup();

    expect(component.age(exception({ openForHours: 0.5 }))).toBe('under an hour');
    expect(component.age(exception({ openForHours: 6 }))).toBe('6 hours');
    expect(component.age(exception({ openForHours: 73 }))).toBe('3 days');
  });

  it('marks anything open for two days as being ignored rather than worked', async () => {
    await setup();

    expect(component.isStale(exception({ openForHours: 6 }))).toBeFalse();
    expect(component.isStale(exception({ openForHours: 60 }))).toBeTrue();
    expect(component.isStale(exception({ openForHours: 600, status: 'RESOLVED' })))
      .withContext('a closed exception is not being ignored').toBeFalse();
  });

  it('offers no actions on an exception that is already closed', async () => {
    await setup([exception({ status: 'RESOLVED', resolution: 'Cleared customs.' })]);

    expect(component.isLive(component.exceptions[0])).toBeFalse();
    expect(fixture.nativeElement.querySelector('[data-testid="resolve-exc-1"]')).toBeNull();
  });

  // ── Acting on one ───────────────────────────────────────────────────────────

  it('refuses to resolve one without saying how it was settled', async () => {
    await setup();

    component.open(component.exceptions[0], 'resolve');
    fixture.detectChanges();

    expect(component.canSave).toBeFalse();
    expect(component.validationError).toContain('teaches nobody anything');

    component.confirm();

    expect(api.resolveException).not.toHaveBeenCalled();
  });

  it('refuses to withdraw one without saying why', async () => {
    await setup();

    component.open(component.exceptions[0], 'withdraw');
    component.form.note = '   ';

    expect(component.canSave).toBeFalse();
    expect(api.withdrawException).not.toHaveBeenCalled();
  });

  it('resolves with the reason and reloads both the queue and the summary', async () => {
    await setup();

    // Counted from here: the table's lazy load fires on first render as well as ngOnInit, so the
    // absolute count says nothing useful.
    const queueCalls   = api.getExceptions.calls.count();
    const summaryCalls = api.getExceptionSummary.calls.count();

    component.open(component.exceptions[0], 'resolve');
    component.form.note = '  Broker cleared it.  ';
    component.confirm();

    expect(api.resolveException).toHaveBeenCalledWith('exc-1', 'Broker cleared it.');
    expect(api.getExceptions.calls.count()).toBeGreaterThan(queueCalls);
    expect(api.getExceptionSummary.calls.count()).toBeGreaterThan(summaryCalls);
    expect(component.dialogVisible).toBeFalse();
  });

  it('withdraws through its own call, never through resolve', async () => {
    await setup();

    component.open(component.exceptions[0], 'withdraw');
    component.form.note = 'Duplicate of yesterday.';
    component.confirm();

    expect(api.withdrawException).toHaveBeenCalledWith('exc-1', 'Duplicate of yesterday.');
    expect(api.resolveException)
      .withContext('counting a mistake as a fix would flatter the carrier scorecard')
      .not.toHaveBeenCalled();
  });

  it('assigns an owner and re-grades in one call', async () => {
    await setup();

    component.open(component.exceptions[0], 'assign');
    component.form.assignToUserId = 77;
    component.form.severity = 'NORMAL';
    component.form.status = 'WAITING';
    component.confirm();

    expect(api.patchException).toHaveBeenCalledWith('exc-1', jasmine.objectContaining({
      assignToUserId: 77, severity: 'NORMAL', status: 'WAITING', clearAssignee: false
    }));
  });

  it('hands one back to nobody without sending an owner', async () => {
    await setup([exception({ assignedToUserId: 77 })]);

    component.open(component.exceptions[0], 'assign');
    component.form.clearAssignee = true;
    component.confirm();

    const req = api.patchException.calls.mostRecent().args[1];

    expect(req.clearAssignee).toBeTrue();
    expect(req.assignToUserId).toBeUndefined();
  });

  it('keeps the dialog open and says why when the server refuses', async () => {
    await setup();
    api.resolveException.and.returnValue(
      throwError(() => ({ error: { message: 'This exception is RESOLVED.' } })));

    component.open(component.exceptions[0], 'resolve');
    component.form.note = 'Done.';
    component.confirm();

    expect(component.dialogVisible).withContext('the typed reason must not be lost').toBeTrue();
    expect(component.isSubmitting).toBeFalse();
  });

  // ── Filtering ───────────────────────────────────────────────────────────────

  it('narrows to what nobody owns', async () => {
    await setup();

    component.filter.unassigned = true;
    component.search();

    expect(api.getExceptions.calls.mostRecent().args[0]?.unassigned).toBeTrue();
  });

  it('goes back to the first page when the filter changes', async () => {
    await setup();

    component.page = 4;
    component.filter.severity = 'CRITICAL';
    component.search();

    expect(component.page).toBe(1);
    expect(api.getExceptions.calls.mostRecent().args[0]?.page).toBe(1);
  });
});
