import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { Tooltip } from 'primeng/tooltip';
import { Subject, of, throwError } from 'rxjs';

import { LeadTimePopoverComponent, LeadTimeCalculation } from './lead-time-popover.component';
import { deliveryDateState, longDate } from './lead-time-display';
import { LeadTimeResultModel } from '../../../services/lead-time.service';

// A34 PC-07/08/09 — the ⏱ button, the breakdown popover and the delivery-date indicator shared by the inquiry, quotation
// and sale order lines (spec §6.6/§6.7, API-CONTRACT.md §4.6, §5.1). Calculation is on demand only (BR-C4-01).

const RESULT: LeadTimeResultModel = {
  totalLeadTimeDays: 13, earliestDeliveryDate: '2026-10-17T00:00:00', routeUuid: 'r-mfg', routeCode: 'MFG_PICK_PACK_SHIP',
  routeCategory: 'MANUFACTURE', calculatedAt: '2026-10-04T08:00:00Z',
  components: [
    { code: 'MANUFACTURING', name: 'Manufacturing', days: 5, source: 'BOM' },
    { code: 'MFG_BUFFER', name: 'Manufacturing buffer', days: 2, source: 'VARIANT' },
    { code: 'QC', name: 'Quality inspection', days: 1, source: 'ORG_DEFAULT' },
    { code: 'PICK_PACK', name: 'Pick & pack', days: 1, source: 'ORG_DEFAULT' },
    { code: 'SHIPPING', name: 'Shipping', days: 3, source: 'ORG_DEFAULT' },
    { code: 'SALES_BUFFER', name: 'Sales safety buffer', days: 1, source: 'ORG_DEFAULT', detail: 'per org' }
  ]
};

describe('A34 lead-time display helpers', () => {
  it('reads "— Not calculated" with no dates at all', () => {
    const s = deliveryDateState({});
    expect(s.source).toBe('NONE');
    expect(s.label).toBe('— Not calculated');
    expect(s.tooltip).toBe('Click ⏱ to calculate lead time');
    expect(s.effective).toBeNull();
  });

  it('reads "⏱ Calculated" with the days and the day it was calculated', () => {
    const s = deliveryDateState({
      calculatedDate: '2026-10-15T00:00:00', calculatedDays: 12, calculatedAt: new Date(2026, 9, 3, 12, 0).toISOString()
    });
    expect(s.source).toBe('CALCULATED');
    expect(s.label).toBe('⏱ Calculated');
    expect(s.tooltip).toBe('Lead time: 12 days — calculated on Oct 3, 2026');
    expect(s.effective).toBe('2026-10-15');
  });

  it('reads "✎ Manual" over a calculated date, naming the calculated one', () => {
    const s = deliveryDateState({ manualDate: '2026-10-20T00:00:00', calculatedDate: '2026-10-15T00:00:00', calculatedDays: 12 });
    expect(s.source).toBe('MANUAL');
    expect(s.label).toBe('✎ Manual');
    expect(s.tooltip).toBe('Manual date — calculated was Oct 15, 2026');
    expect(s.effective).toBe('2026-10-20');
    expect(deliveryDateState({ manualDate: '2026-10-20' }).tooltip).toBe('Manual date — not calculated');
  });

  it('formats a date-only value as the day it names, wherever the reader is (no UTC shift)', () => {
    expect(longDate('2026-10-01T00:00:00')).toBe('Oct 1, 2026');
    expect(longDate('2026-10-01T00:00:00Z')).toBe('Oct 1, 2026');
    expect(longDate(null)).toBe('—');
  });
});

describe('LeadTimePopoverComponent', () => {
  let fixture: ComponentFixture<LeadTimePopoverComponent>;
  let component: LeadTimePopoverComponent;
  let calculator: jasmine.Spy;
  let emitted: (string | null)[];
  let calculations: LeadTimeCalculation[];

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  function queryAll(testId: string): HTMLElement[] {
    return Array.from(fixture.nativeElement.querySelectorAll(`[data-testid="${testId}"]`));
  }

  function tooltipOf(testId: string): string {
    const el = fixture.debugElement.query(By.css(`[data-testid="${testId}"]`));
    return String(el.injector.get(Tooltip).content ?? '');
  }

  function click(testId: string) {
    const el = query(testId);
    expect(el).withContext(testId).not.toBeNull();
    (el!.tagName === 'P-BUTTON' ? el!.querySelector('button')! : el!).click();
    fixture.detectChanges();
  }

  async function setup(inputs: Partial<Record<string, unknown>> = {}) {
    await TestBed.configureTestingModule({
      imports: [LeadTimePopoverComponent],
      providers: [provideNoopAnimations()]
    }).compileComponents();
    fixture = TestBed.createComponent(LeadTimePopoverComponent);
    component = fixture.componentInstance;
    calculator = jasmine.createSpy('calculator').and.returnValue(of({ leadTime: RESULT, line: { uuid: 'l2' } }));
    emitted = [];
    calculations = [];
    component.manualDateChange.subscribe(v => emitted.push(v));
    component.calculated.subscribe(c => calculations.push(c));
    const all = { variantUuid: 'v2', quantity: 50, canCalculate: true, canOverride: true, calculator, ...inputs };
    for (const [k, v] of Object.entries(all)) fixture.componentRef.setInput(k, v);
    fixture.detectChanges();
  }

  it('calculates nothing until ⏱ is pressed (BR-C4-01)', async () => {
    await setup();
    expect(calculator).not.toHaveBeenCalled();
    expect(query('lt-panel')).toBeNull();
    expect(query('lt-indicator')!.textContent).toContain('— Not calculated');
    expect(tooltipOf('lt-indicator')).toBe('Click ⏱ to calculate lead time');
  });

  it('shows ⏱ only for a chosen variant, a quantity above zero and someone who may calculate', async () => {
    await setup({ variantUuid: null });
    expect(query('lt-calculate')).toBeNull();

    fixture.componentRef.setInput('variantUuid', 'v2');
    fixture.componentRef.setInput('quantity', 0);
    fixture.detectChanges();
    expect(query('lt-calculate')).toBeNull();

    fixture.componentRef.setInput('quantity', 50);
    fixture.detectChanges();
    expect(query('lt-calculate')).not.toBeNull();

    fixture.componentRef.setInput('canCalculate', false);
    fixture.detectChanges();
    expect(query('lt-calculate')).toBeNull();
  });

  it('shows a loading state, then the breakdown with each source, the total and the earliest date', async () => {
    const pending = new Subject<LeadTimeCalculation>();
    await setup();
    calculator.and.returnValue(pending);

    click('lt-calculate');
    expect(query('lt-loading')).not.toBeNull();
    expect((query('lt-calculate') as HTMLButtonElement).disabled).toBeTrue();

    pending.next({ leadTime: RESULT });
    pending.complete();
    fixture.detectChanges();

    expect(query('lt-loading')).toBeNull();
    const rows = queryAll('lt-component').map(r => r.textContent!.replace(/\s+/g, ' ').trim());
    expect(rows.length).toBe(6);
    expect(rows[0]).toContain('Manufacturing');
    expect(rows[0]).toContain('5 days');
    expect(rows[0]).toContain('BOM');
    expect(rows[1]).toContain('Variant override');
    expect(rows[2]).toContain('Org default');
    expect(rows[2]).toContain('1 day');
    expect(rows[5]).toContain('per org');
    expect(query('lt-total')!.textContent!.replace(/\s+/g, ' ').trim()).toBe('Total: 13 days → Earliest delivery: Oct 17, 2026');
    expect(calculations.length).toBe(1);
    expect(calculator).toHaveBeenCalledTimes(1);
  });

  it('shows the server\'s total, never a sum of the rows: a component not counted (v1.3 SUPPLIER on MANUFACTURE) says so', async () => {
    const withSupplier = {
      ...RESULT,
      components: [{ code: 'SUPPLIER', name: 'Supplier lead time', days: 10, source: 'SUPPLIER_RATE', includedInTotal: false } as any,
                   ...RESULT.components]
    };
    await setup();
    calculator.and.returnValue(of({ leadTime: withSupplier }));
    click('lt-calculate');

    const rows = queryAll('lt-component').map(r => r.textContent!.replace(/\s+/g, ' ').trim());
    expect(rows[0]).toContain('10 days');
    expect(rows[0]).toContain('not counted');
    expect(rows[1]).not.toContain('not counted');
    expect(query('lt-total')!.textContent!.replace(/\s+/g, ' ').trim()).toBe('Total: 13 days → Earliest delivery: Oct 17, 2026');
  });

  it('says why a calculation failed, in the server\'s words', async () => {
    await setup();
    calculator.and.returnValue(throwError(() => ({ status: 400, error: { message: 'Lead-time calculation needs the Inventory module.' } })));
    click('lt-calculate');
    expect(query('lt-error')!.textContent).toContain('needs the Inventory module');
    expect(calculations.length).toBe(0);
  });

  it('Apply over a manual date clears it, so the calculated date is used', async () => {
    await setup({ manualDate: '2026-10-20', calculatedDate: '2026-10-15T00:00:00' });
    click('lt-calculate');
    click('lt-apply');
    expect(emitted).toEqual([null]);
    expect(query('lt-panel')).toBeNull();
  });

  it('Apply with no manual date changes nothing: the calculated date already is the line\'s date', async () => {
    await setup();
    click('lt-calculate');
    click('lt-apply');
    expect(emitted).toEqual([]);
    expect(query('lt-panel')).toBeNull();
  });

  it('Apply in set-manual mode writes the earliest date as the line\'s date (where nothing stores the calculation)', async () => {
    await setup({ applyMode: 'set-manual' });
    click('lt-calculate');
    click('lt-apply');
    expect(emitted).toEqual(['2026-10-17']);
  });

  it('Override date saves the picked day as yyyy-MM-dd, with no UTC shift', async () => {
    await setup();
    click('lt-calculate');
    click('lt-override');
    expect(query('lt-override-editor')).not.toBeNull();
    expect(component.overrideValue).toEqual(new Date(2026, 9, 17));

    component.overrideValue = new Date(2026, 9, 20);
    fixture.detectChanges();
    click('lt-override-save');
    expect(emitted).toEqual(['2026-10-20']);
  });

  it('✎ sets a date without calculating, where calculating is not allowed (a confirmed order)', async () => {
    await setup({ canCalculate: false, calculatedDate: '2026-10-15T00:00:00', calculatedDays: 12 });
    expect(query('lt-calculate')).toBeNull();
    click('lt-set-date');
    expect(query('lt-override-editor')).not.toBeNull();
    expect(component.overrideValue).toEqual(new Date(2026, 9, 15));
    expect(calculator).not.toHaveBeenCalled();
  });

  it('marks a manual date amber with the calculated one in its tooltip, and × clears it', async () => {
    await setup({ manualDate: '2026-10-20T00:00:00', calculatedDate: '2026-10-15T00:00:00', calculatedDays: 12 });
    const indicator = query('lt-indicator')!;
    expect(indicator.textContent).toContain('✎ Manual');
    expect(indicator.classList).toContain('lt-manual');
    expect(tooltipOf('lt-indicator')).toBe('Manual date — calculated was Oct 15, 2026');
    expect(query('lt-date')!.textContent).toContain('20 Oct 2026');

    click('lt-clear');
    expect(emitted).toEqual([null]);
  });

  it('offers no date actions without the right to change it, and says why when told', async () => {
    await setup({ canOverride: false, manualDate: '2026-10-20', overrideNote: 'Set the estimate by evaluating the line.' });
    expect(query('lt-clear')).toBeNull();
    expect(query('lt-set-date')).toBeNull();
    click('lt-calculate');
    expect(query('lt-apply')).toBeNull();
    expect(query('lt-override')).toBeNull();
    expect(query('lt-override-note')!.textContent).toContain('evaluating the line');
  });

  it('closes on Escape', async () => {
    await setup();
    click('lt-calculate');
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
    fixture.detectChanges();
    expect(query('lt-panel')).toBeNull();
  });
});
