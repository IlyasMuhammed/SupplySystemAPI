import { of } from 'rxjs';

import { ApDashboardComponent } from './ap-dashboard.component';
import { aging, apSummary, mount, page } from '../ar-ap.testing';

describe('ApDashboardComponent (A48 tasks 556–559)', () => {
  const vendors = () => aging({ lines: [{ partner: { uuid: 'v1', name: 'Supplier A' }, bucketAmounts: [30000, 12000, 0, 0, 0, 0], totalOutstanding: 42000 }],
    totals: { bucketTotals: [30000, 12000, 0, 0, 0, 0], grandTotal: 42000, totalPartners: 1, totalOpenItems: 4 }, totalOutstanding: 42000 });

  it('tiles: Total AP, Overdue, DPO, Due this week; aged payables; pending-match count (557–558)', async () => {
    const m = await mount(ApDashboardComponent, { perms: ['AP_VIEW'], prepare: api => {
      api.getApSummary.and.returnValue(of(apSummary({ totalOutstanding: 183200, totalOverdue: 28500, dpo: 42, totalDueThisWeek: 34700, totalVendorsWithBalance: 3,
        totalVendorsOverdue: 1, discountOpportunityAmount: 1230 })));
      api.getApAging.and.returnValue(of(vendors()));
      api.getPendingMatches.and.returnValue(page([], 4));
    } });
    expect(m.text('kpi-total')).toContain('183,200.00');
    expect(m.text('kpi-total')).toContain('3 vendors');
    expect(m.text('kpi-overdue')).toContain('1 vendors');
    expect(m.text('kpi-dpo')).toContain('42 days');
    expect(m.text('kpi-week')).toContain('34,700.00');
    expect(m.text('go-matching')).toContain('(4)');
    expect(m.api.getPendingMatches).toHaveBeenCalledWith({ pageSize: 1 });
    expect(m.text('kpi-week')).toContain('1,230.00 discount available');
    expect(m.text('aging-row-v1')).toContain('Supplier A');
  });

  it('vendor drill-down to the statement (559); commands follow the codes', async () => {
    const m = await mount(ApDashboardComponent, { perms: ['AP_VIEW'], prepare: api => api.getApAging.and.returnValue(of(vendors())) });
    expect(m.q('aging-link-v1')?.getAttribute('href')).toContain('/portal/pages/finance/ap/vendor-statement/v1');
    expect(m.q('go-run')).toBeNull();
    expect(m.q('go-matching')).not.toBeNull();
    expect(m.q('go-discounts')).not.toBeNull();
    const p = await mount(ApDashboardComponent, { perms: ['AP_VIEW', 'PAYMENT_RUN_PROPOSE'] });
    expect(p.q('go-run')?.getAttribute('href')).toContain('/portal/pages/finance/ap/payment-runs/new');
  });

  it('expanding a vendor asks for its documents with vendorUuid', async () => {
    const m = await mount(ApDashboardComponent, { perms: ['AP_VIEW'], prepare: api => api.getApAging.and.returnValue(of(vendors())) });
    m.c.loadDetail('v1');
    expect(m.api.getApAging).toHaveBeenCalledWith(jasmine.objectContaining({ partnerUuid: 'v1', includeDetails: true }));
  });
});
