import { of } from 'rxjs';

import { PaymentRunListComponent } from './payment-run-list.component';
import { mount } from '../ar-ap.testing';

describe('PaymentRunListComponent (A48 task 568)', () => {
  const r = (n: string, status: string, date: string) => ({ uuid: 'u' + n, runNumber: 'PR-' + n, runDate: date, paymentDate: date, status, totalVendors: 1,
    totalItems: 2, totalAmount: 100, totalDiscount: 0, netPaymentAmount: 100 });

  it('newest first; status chips group Draft / Reviewing under Proposed', async () => {
    const m = await mount(PaymentRunListComponent, { perms: ['AP_VIEW'], prepare: api => api.getPaymentRuns.and.returnValue(of({
      data: [r('0001', 'Completed', '2026-10-01'), r('0002', 'Reviewing', '2026-10-20'), r('0003', 'Cancelled', '2026-10-10')], totalRecords: 3, page: 1, pageSize: 200 })) });
    expect(m.api.getPaymentRuns).toHaveBeenCalledWith(null, 1, 200);
    expect(m.c.runs.map(x => x.runNumber)).toEqual(['PR-0002', 'PR-0003', 'PR-0001']);
    m.c.status = 'Proposed';
    expect(m.c.shown.map(x => x.runNumber)).toEqual(['PR-0002']);
    expect(m.q('new')).toBeNull();
    const p = await mount(PaymentRunListComponent, { perms: ['PAYMENT_RUN_PROPOSE'] });
    expect(p.q('new')).not.toBeNull();
  });
});
