import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { PickWalkComponent } from './pick-walk.component';
import {
  LogisticsService,
  PickListModel,
  PickListLineModel
} from '../../../../services/logistics.service';

const UUID = '11111111-1111-1111-1111-111111111111';

function line(overrides: Partial<PickListLineModel> = {}): PickListLineModel {
  return {
    uuid: 'i1',
    seqNo: 1,
    deliveryLineUuid: 'l1',
    deliveryLineNo: 1,
    itemDescription: '4mm cable',
    unitOfMeasure: 'M',
    zoneName: 'Zone A',
    binCode: 'A-01-02',
    qtyToPick: 30,
    qtyPicked: 0,
    qtyShort: 0,
    isConfirmed: false,
    ...overrides
  };
}

function pickList(overrides: Partial<PickListModel> = {}): PickListModel {
  return {
    uuid: UUID,
    pickListNumber: 'PCK-2026-00001',
    status: 'OPEN',
    deliveryUuid: 'd1',
    deliveryNumber: 'DLV-2026-00001',
    warehouseUuid: 'w1',
    warehouseName: 'Central',
    generatedAt: '2026-09-01T00:00:00Z',
    lines: [line(), line({ uuid: 'i2', seqNo: 2, binCode: 'A-02-01', qtyToPick: 20 })],
    ...overrides
  };
}

function ok<T>(result: T) {
  return of({ success: true, message: '', result } as any);
}

describe('PickWalkComponent', () => {
  let fixture: ComponentFixture<PickWalkComponent>;
  let component: PickWalkComponent;
  let service: jasmine.SpyObj<LogisticsService>;

  async function setup(model: PickListModel | null = pickList()) {
    service = jasmine.createSpyObj<LogisticsService>('LogisticsService', [
      'getPickListById', 'confirmPick', 'cancelPickList'
    ]);

    service.getPickListById.and.returnValue(
      model ? ok(model) : of({ success: false, message: 'not found', result: null } as any));
    service.confirmPick.and.returnValue(ok({
      pickListUuid: UUID, pickListStatus: 'IN_PROGRESS', deliveryStatus: 'PICKING',
      completed: false, linesConfirmed: 1, linesOutstanding: 1,
      qtyPicked: 30, qtyShort: 0, qtyReturnedToStock: 0
    }));
    service.cancelPickList.and.returnValue(of({ success: true, message: '' } as any));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [PickWalkComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        provideNoopAnimations(), MessageService,
        { provide: LogisticsService, useValue: service },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['uuid', UUID]]) } } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(PickWalkComponent);
    component = fixture.componentInstance;
  }

  // ── Loading and focus ─────────────────────────────────────────────────────

  it('loads once and focuses the first unanswered instruction', async () => {
    await setup();
    fixture.detectChanges();

    expect(service.getPickListById).toHaveBeenCalledOnceWith(UUID);
    expect(component.current?.seqNo).toBe(1);
    // Pre-filled with what the instruction asks for: taking exactly what was asked is the
    // overwhelmingly common case.
    expect(component.qtyPicked).toBe(30);
  });

  it('skips instructions already answered', async () => {
    await setup(pickList({
      status: 'IN_PROGRESS',
      lines: [
        line({ isConfirmed: true, qtyPicked: 30 }),
        line({ uuid: 'i2', seqNo: 2, qtyToPick: 20 })
      ]
    }));
    fixture.detectChanges();

    expect(component.current?.seqNo).toBe(2);
    expect(component.confirmedCount).toBe(1);
    expect(component.progressPercent).toBe(50);
  });

  it('focuses nothing once every instruction is answered', async () => {
    await setup(pickList({
      status: 'COMPLETED',
      lines: [line({ isConfirmed: true, qtyPicked: 30 })]
    }));
    fixture.detectChanges();

    expect(component.current).toBeNull();
    expect(component.isComplete).toBeTrue();
  });

  it('shows not found for a 404 but not for a failed request', async () => {
    await setup();
    service.getPickListById.and.returnValue(throwError(() => ({ status: 404 })));
    fixture.detectChanges();
    expect(component.notFound).toBeTrue();

    await setup();
    service.getPickListById.and.returnValue(throwError(() => ({ status: 500 })));
    fixture.detectChanges();
    expect(component.notFound).toBeFalse();
  });

  // ── Confirming ────────────────────────────────────────────────────────────

  it('confirms a full pick without asking for a reason', async () => {
    await setup();
    fixture.detectChanges();

    expect(component.isShort).toBeFalse();
    expect(component.canConfirm).toBeTrue();

    component.confirm();

    const [uuid, req] = service.confirmPick.calls.mostRecent().args;
    expect(uuid).toBe(UUID);
    expect(req.lines).toEqual([{
      lineUuid: 'i1', qtyPicked: 30, shortReasonCode: undefined, shortNote: undefined
    }]);
  });

  it('will not confirm a short pick without a reason', async () => {
    // The server requires one, and finding that out through a 400 after the picker has walked
    // away is a poor way to learn it.
    await setup();
    fixture.detectChanges();

    component.qtyPicked = 22;

    expect(component.isShort).toBeTrue();
    expect(component.shortfall).toBe(8);
    expect(component.canConfirm).toBeFalse();

    component.shortReasonCode = 'SHORT_ON_SHELF';
    expect(component.canConfirm).toBeTrue();
  });

  it('sends the short reason and trims the note', async () => {
    await setup();
    fixture.detectChanges();

    component.qtyPicked = 22;
    component.shortReasonCode = 'DAMAGED';
    component.shortNote = '  Two cartons crushed  ';
    component.confirm();

    expect(service.confirmPick.calls.mostRecent().args[1].lines[0]).toEqual({
      lineUuid: 'i1', qtyPicked: 22,
      shortReasonCode: 'DAMAGED', shortNote: 'Two cartons crushed'
    });
  });

  it('refuses more than the instruction reserves', async () => {
    // More than the instruction is not a short pick — it is somebody else's stock.
    await setup();
    fixture.detectChanges();

    component.qtyPicked = 40;

    expect(component.isOverPicked).toBeTrue();
    expect(component.canConfirm).toBeFalse();
  });

  it('treats a confirmed zero as an answer', async () => {
    // "I looked and there was none" has to be recordable, or the line hangs unanswered forever.
    await setup();
    fixture.detectChanges();

    component.qtyPicked = 0;
    component.shortReasonCode = 'NOT_FOUND';

    expect(component.canConfirm).toBeTrue();
  });

  it('reloads after confirming rather than patching its own state', async () => {
    await setup();
    fixture.detectChanges();
    component.confirm();

    expect(service.getPickListById).toHaveBeenCalledTimes(2);
  });

  it('reports what went back to stock when the walk closes', async () => {
    await setup();
    fixture.detectChanges();
    const messages = fixture.debugElement.injector.get(MessageService);
    spyOn(messages, 'add');

    service.confirmPick.and.returnValue(ok({
      pickListUuid: UUID, pickListStatus: 'COMPLETED', deliveryStatus: 'PICKED',
      completed: true, linesConfirmed: 2, linesOutstanding: 0,
      qtyPicked: 42, qtyShort: 8, qtyReturnedToStock: 8
    }));

    component.qtyPicked = 22;
    component.shortReasonCode = 'NOT_FOUND';
    component.confirm();

    expect(messages.add).toHaveBeenCalledWith(jasmine.objectContaining({
      summary: 'Picking complete',
      detail: '42 picked. 8 was not found and is back in stock.'
    }));
  });

  it('counts down what is left while the walk is still open', async () => {
    await setup();
    fixture.detectChanges();
    const messages = fixture.debugElement.injector.get(MessageService);
    spyOn(messages, 'add');

    component.confirm();

    expect(messages.add).toHaveBeenCalledWith(jasmine.objectContaining({
      summary: 'Confirmed', detail: '1 left to pick.'
    }));
  });

  it('surfaces the servers explanation when a pick is refused', async () => {
    await setup();
    fixture.detectChanges();
    const messages = fixture.debugElement.injector.get(MessageService);
    spyOn(messages, 'add');

    service.confirmPick.and.returnValue(throwError(() => ({
      error: { message: 'Delivery DLV-1 is ON_HOLD, not PICKING.' }
    })));

    component.confirm();

    expect(messages.add).toHaveBeenCalledWith(jasmine.objectContaining({
      severity: 'error', detail: 'Delivery DLV-1 is ON_HOLD, not PICKING.'
    }));
    expect(component.isSubmitting).toBeFalse();
  });

  // ── Correcting ────────────────────────────────────────────────────────────

  it('lets an answered instruction be corrected while the walk is open', async () => {
    await setup(pickList({
      status: 'IN_PROGRESS',
      lines: [
        line({ isConfirmed: true, qtyPicked: 10, qtyShort: 20, shortReasonCode: 'NOT_FOUND' }),
        line({ uuid: 'i2', seqNo: 2, qtyToPick: 20 })
      ]
    }));
    fixture.detectChanges();

    expect(component.current?.seqNo).toBe(2);

    component.reopen(component.lines[0]);

    expect(component.current?.seqNo).toBe(1);
    // Re-focusing resets the form to the instruction, not to yesterday's answer.
    expect(component.qtyPicked).toBe(30);
    expect(component.shortReasonCode).toBeNull();
  });

  it('will not reopen an instruction once the walk is closed', async () => {
    await setup(pickList({
      status: 'COMPLETED',
      lines: [line({ isConfirmed: true, qtyPicked: 30 })]
    }));
    fixture.detectChanges();

    component.reopen(component.lines[0]);

    expect(component.current).toBeNull();
    expect(component.canConfirm).toBeFalse();
  });

  // ── Cancelling ────────────────────────────────────────────────────────────

  it('requires a reason to cancel, and trims it', async () => {
    await setup();
    fixture.detectChanges();

    component.cancelReason = '   ';
    component.confirmCancel();
    expect(service.cancelPickList).not.toHaveBeenCalled();

    component.cancelReason = '  Wrong dock  ';
    component.confirmCancel();
    expect(service.cancelPickList).toHaveBeenCalledWith(UUID, { reason: 'Wrong dock' });
  });

  // ── Display ───────────────────────────────────────────────────────────────

  it('reads a location as zone and bin, and says so when there is none', async () => {
    await setup();
    fixture.detectChanges();

    expect(component.location(line())).toBe('Zone A · A-01-02');
    // Goods received but not yet put away have no bin — saying so is honest.
    expect(component.location(line({ zoneName: undefined, binCode: undefined })))
      .toBe('Not put away');
  });

  it('renders a short reason code as its label', async () => {
    await setup();
    expect(component.reasonLabel('SHORT_ON_SHELF')).toBe('Some there, not enough');
    expect(component.reasonLabel('UNKNOWN_CODE')).toBe('UNKNOWN_CODE');
  });
});
