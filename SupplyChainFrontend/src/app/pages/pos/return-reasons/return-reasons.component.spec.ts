import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';

import { PosReturnReasonsComponent, reasonRows, swapOrders } from './return-reasons.component';
import { PosReturnsAdminService } from '../../../services/pos-returns-admin.service';
import { fail400, ok, reasonFixture } from '../pos-fixtures.testing';

/** A39 — `/pos/return-reasons`: grid, create / edit dialog, reorder by sort order, activate / deactivate. */
describe('PosReturnReasonsComponent (A39)', () => {
  let fixture: ComponentFixture<PosReturnReasonsComponent>;
  let component: PosReturnReasonsComponent;
  let el: HTMLElement;
  let api: jasmine.SpyObj<PosReturnsAdminService>;

  const reasons = [
    reasonFixture({ uuid: 'r-2', code: 'WRONG_SIZE', name: 'Wrong size', sortOrder: 20 }),
    reasonFixture(),
    reasonFixture({ uuid: 'r-3', code: 'PRICE_MATCH', name: 'Price match', sortOrder: 30, requiresNote: true, requiresManager: true,
                    restockingFeeOverride: 0, isActive: false })
  ];

  async function setup() {
    api = jasmine.createSpyObj<PosReturnsAdminService>('PosReturnsAdminService', ['getReasons', 'createReason', 'updateReason', 'setReasonStatus']);
    api.getReasons.and.returnValue(ok(reasons));
    api.createReason.and.returnValue(ok(null));
    api.updateReason.and.returnValue(ok(null));
    api.setReasonStatus.and.returnValue(ok(null));
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [PosReturnReasonsComponent],
      providers: [provideNoopAnimations(), provideRouter([]), { provide: PosReturnsAdminService, useValue: api }]
    }).compileComponents();
    fixture = TestBed.createComponent(PosReturnReasonsComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    el = fixture.nativeElement;
  }

  const q = (id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  it('lists every reason (inactive too) in sort order with its rules', async () => {
    await setup();
    expect(api.getReasons).toHaveBeenCalledWith({ includeInactive: true });
    expect(component.rows.map(r => r.code)).toEqual(['DEFECTIVE', 'WRONG_SIZE', 'PRICE_MATCH']);
    const pm = q('reason-PRICE_MATCH')!.textContent!;
    expect(pm).toContain('Price match');
    expect(pm).toContain('Waived');
    expect(pm).toContain('Inactive');
    expect(q('reason-DEFECTIVE')!.textContent).toContain('Policy');
    expect(q('reason-DEFECTIVE')!.textContent).toContain('Returns and exchanges');
  });

  it('create: code rules, then POST; a duplicate-code refusal is shown verbatim', async () => {
    await setup();
    component.open(null);
    expect(component.model.sortOrder).toBe(40);
    component.model.code = component.upper('bad code!');
    component.model.name = 'Bad';
    expect(component.problem).toContain('A–Z');
    component.model.code = component.upper('store damage');
    expect(component.model.code).toBe('STORE_DAMAGE');
    expect(component.problem).toBeNull();
    component.model.restockingFeeOverride = 150;
    expect(component.problem).toContain('between 0 and 100');
    component.model.restockingFeeOverride = 5;
    api.createReason.and.returnValue(fail400('A reason with code STORE_DAMAGE already exists.'));
    component.save();
    expect(api.createReason.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({ code: 'STORE_DAMAGE', name: 'Bad', restockingFeeOverride: 5 }));
    expect(component.error).toBe('A reason with code STORE_DAMAGE already exists.');
    expect(component.dialog).toBeTrue();
  });

  it('edit PUTs the reason; up / down swap sort orders with the neighbour; activate / deactivate PATCH the status', async () => {
    await setup();
    component.open(component.rows[1]);
    component.model.name = 'Wrong size or fit';
    component.save();
    expect(api.updateReason).toHaveBeenCalledWith('r-2', jasmine.objectContaining({ name: 'Wrong size or fit', code: 'WRONG_SIZE' }));
    expect(component.dialog).toBeFalse();

    api.updateReason.calls.reset();
    component.move(component.rows[1], 'up');
    expect(api.updateReason).toHaveBeenCalledWith('r-2', jasmine.objectContaining({ sortOrder: 10 }));
    expect(api.updateReason).toHaveBeenCalledWith('rr-1', jasmine.objectContaining({ sortOrder: 20 }));

    expect(component.rows[0].first).toBeTrue();
    expect(component.rows[2].last).toBeTrue();
    component.toggle(component.rows[2]);
    expect(api.setReasonStatus).toHaveBeenCalledWith('r-3', true);
    component.toggle(component.rows[0]);
    expect(api.setReasonStatus).toHaveBeenCalledWith('rr-1', false);
  });

  it('helpers: equal sort orders are separated; rows know first / last', () => {
    const a = reasonFixture({ sortOrder: 5 }), b = reasonFixture({ uuid: 'b', sortOrder: 5 });
    expect(swapOrders(a, b, 'up')).toEqual({ row: 4, neighbour: 5 });
    expect(swapOrders(a, b, 'down')).toEqual({ row: 6, neighbour: 5 });
    expect(swapOrders(reasonFixture({ sortOrder: 1 }), b, 'down')).toEqual({ row: 5, neighbour: 1 });
    const rows = reasonRows([a]);
    expect(rows[0].first && rows[0].last).toBeTrue();
  });
});
