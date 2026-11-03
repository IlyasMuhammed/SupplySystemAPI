import { of } from 'rxjs';

import { PaymentSchedulesComponent } from './payment-schedules.component';
import { cashFlow, mount, schedule } from '../ar-ap.testing';

describe('PaymentSchedulesComponent (A48 C10 / C15)', () => {
  const rows = [schedule('2', { dueDate: '2026-11-05', paymentBlock: true, blockReason: 'Disputed' }), schedule('1', { daysUntilDue: -2, dueDate: '2026-10-23' })];

  it('upcoming: sorted by due date, overdue marked, blocked shown with reason', async () => {
    const m = await mount(PaymentSchedulesComponent, { perms: ['AP_VIEW'], prepare: api => api.getUpcomingPayments.and.returnValue(of(rows)) });
    expect(m.api.getUpcomingPayments).toHaveBeenCalledWith(m.c.from, m.c.to);
    expect(m.c.rows.map(r => r.uuid)).toEqual(['1', '2']);
    expect(m.text('sched-1')).toContain('2d late');
    expect(m.text('sched-2')).toContain('Disputed');
    expect(m.text('totals')).toContain('2,000.00');
    expect(m.q('block-1')).toBeNull();
  });

  it('block needs a reason (AP_EXECUTE); unblock', async () => {
    const m = await mount(PaymentSchedulesComponent, { perms: ['AP_VIEW', 'AP_EXECUTE'], prepare: api => {
      api.getUpcomingPayments.and.returnValue(of(rows));
      api.blockSchedule.and.returnValue(of(schedule('1', { paymentBlock: true })));
      api.unblockSchedule.and.returnValue(of(schedule('2')));
    } });
    m.c.openBlock(m.c.rows[0]);
    m.c.block();
    expect(m.c.dialogError).toBe('A reason is required.');
    m.c.reason = 'Wrong quantity';
    m.c.block();
    expect(m.api.blockSchedule).toHaveBeenCalledWith('1', 'Wrong quantity');
    m.c.unblock(m.c.rows[1]);
    expect(m.api.unblockSchedule).toHaveBeenCalledWith('2');
  });

  it('?view=discounts lists the discount opportunities; cash-flow bars scale to the largest week', async () => {
    const m = await mount(PaymentSchedulesComponent, { perms: ['AP_VIEW'], query: { view: 'discounts' }, prepare: api => {
      api.getDiscountOpportunities.and.returnValue(of([schedule('3', { discountAmount: 20, discountDate: '2026-10-27' })]));
      const week = (n: number, start: string, end: string, due: number) =>
        ({ week: n, weekStart: start, weekEnd: end, itemCount: 1, dueAmount: due, blockedAmount: n === 2 ? 25 : 0, discountAvailable: 0, netIfDiscountsTaken: due });
      api.getCashFlow.and.returnValue(of(cashFlow({ weekRows: [week(1, '2026-10-26', '2026-11-01', 100), week(2, '2026-11-02', '2026-11-08', 50)],
        totalDue: 150, overdueAmount: 30, overdueCount: 2, laterAmount: 400 })));
    } });
    expect(m.api.getDiscountOpportunities).toHaveBeenCalledWith(jasmine.any(String), 7);
    expect(m.text('totals')).toContain('20.00');
    m.c.setView('cashflow');
    m.fixture.detectChanges();
    expect(m.api.getCashFlow).toHaveBeenCalledWith(jasmine.any(String), 8);
    expect(m.c.barWidth(50)).toBe(50);
    expect(m.q('week-2026-10-26')).not.toBeNull();
    expect(m.text('week-2026-11-02')).toContain('25.00 blocked');
    expect(m.text('cashflow-total')).toContain('150.00');
    expect(m.text('cashflow-total')).toContain('30.00 already overdue (2)');
    expect(m.text('cashflow-total')).toContain('400.00 later');
  });

  it('a schedule held by a run names it', async () => {
    const m = await mount(PaymentSchedulesComponent, { perms: ['AP_VIEW'], prepare: api =>
      api.getUpcomingPayments.and.returnValue(of([schedule('9', { reservedByRunNumber: 'PR-0042', status: 'Scheduled' })])) });
    expect(m.text('in-run')).toBe('in run PR-0042');
  });
});
