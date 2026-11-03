import { HttpResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';

import { ArDashboardComponent } from './ar-dashboard.component';
import { aging, arSummary, mount, page } from '../ar-ap.testing';

describe('ArDashboardComponent (A48 tasks 539–544)', () => {
  const summary = arSummary({ totalOutstanding: 247500, totalOverdue: 82300, dso: 38, totalCustomersWithBalance: 12, totalCustomersOverdue: 4, collectionRate: 81.5 });

  it('tiles: Total AR, Overdue, DSO, On hold (from the exposures count); aging rows and total (540–541)', async () => {
    const m = await mount(ArDashboardComponent, { perms: ['AR_VIEW'], prepare: api => {
      api.getArSummary.and.returnValue(of(summary));
      api.getArAging.and.returnValue(of(aging()));
      api.getExposures.and.returnValue(page([], 3));
    } });
    expect(m.text('kpi-total')).toContain('247,500.00');
    expect(m.text('kpi-total')).toContain('12 customers');
    expect(m.text('kpi-dso')).toContain('38 days');
    expect(m.text('kpi-dso')).toContain('81.5%');
    expect(m.text('kpi-hold')).toContain('3');
    expect(m.api.getExposures).toHaveBeenCalledWith({ onHold: true, pageSize: 1 });
    expect(m.text('aging-row-c1')).toContain('Acme Corp');
    expect(m.text('aging-row-c1')).toContain('12,000.00');
    expect(m.text('aging-total')).toContain('78,100.00');
    expect(m.api.getArAging).toHaveBeenCalledWith(jasmine.objectContaining({ asOfDate: m.c.asOfDate, basis: 'DueDate', buckets: [30, 60, 90, 120] }));
  });

  it('as-of date and custom buckets reload; a bad bucket list is refused locally (542, BR-AG-006)', async () => {
    const m = await mount(ArDashboardComponent, { perms: ['AR_VIEW'] });
    m.api.getArAging.calls.reset();
    m.c.bucketText = '30';
    m.c.applyBuckets();
    m.fixture.detectChanges();
    expect(m.text('bucket-error')).toContain('At least 3 buckets');
    expect(m.api.getArAging).not.toHaveBeenCalled();
    m.c.bucketText = '15, 45, 90';
    m.c.applyBuckets();
    expect(m.api.getArAging).toHaveBeenCalledWith(jasmine.objectContaining({ buckets: [15, 45, 90] }));
    m.c.asOfDate = '2026-09-30';
    m.c.load();
    expect(m.api.getArSummary).toHaveBeenCalledWith('2026-09-30');
  });

  it('drill-down links to credit control; a row expands with the documents (543–544)', async () => {
    const m = await mount(ArDashboardComponent, { perms: ['AR_VIEW'], prepare: api => api.getArAging.and.callFake((q: any) => of(q.includeDetails
      ? aging({ lines: [{ ...aging().lines[0], details: [{ documentType: 'SALES_INVOICE', documentUuid: 'd1', documentNumber: 'SI-0001', documentDate: '2026-08-01',
          dueDate: '2026-08-31', currencyCode: 'PKR', originalAmount: 500, openAmount: 500, openAmountBase: 500, daysOverdue: 40, bucketLabel: '31-60' }] }] })
      : aging())) });
    expect(m.q('aging-link-c1')?.getAttribute('href')).toContain('/portal/pages/finance/ar/credit/c1');
    m.q('aging-row-c1')!.click();
    m.fixture.detectChanges();
    expect(m.api.getArAging).toHaveBeenCalledWith(jasmine.objectContaining({ partnerUuid: 'c1', includeDetails: true }));
    expect(m.text('aging-detail-SI-0001')).toContain('40d overdue');
  });

  it('export PDF / Excel need AR_VIEW + REPORT_EXPORT (contract §5)', async () => {
    const m = await mount(ArDashboardComponent, { perms: ['AR_VIEW'], prepare: api => api.getArAging.and.returnValue(of(aging())) });
    expect(m.q('export-pdf')).toBeNull();
    expect(m.q('export')).not.toBeNull();
    const n = await mount(ArDashboardComponent, { perms: ['AR_VIEW', 'REPORT_EXPORT'], prepare: api => {
      api.getArAging.and.returnValue(of(aging()));
      api.exportArAging.and.returnValue(of(new HttpResponse({ body: new Blob(['x']) })));
    } });
    spyOn(URL, 'createObjectURL').and.returnValue('blob:x');
    spyOn(URL, 'revokeObjectURL');
    n.c.export('xlsx');
    expect(n.api.exportArAging).toHaveBeenCalledWith(jasmine.objectContaining({ asOfDate: n.c.asOfDate }), 'xlsx');
  });

  it('shows the server refusal of a failed aging read', async () => {
    const m = await mount(ArDashboardComponent, { perms: ['AR_VIEW'], prepare: api =>
      api.getArAging.and.returnValue(throwError(() => ({ status: 400, error: { message: 'Bucket boundaries must be positive and ascending.' } }))) });
    expect(m.text('aging-error')).toContain('positive and ascending');
  });
});
