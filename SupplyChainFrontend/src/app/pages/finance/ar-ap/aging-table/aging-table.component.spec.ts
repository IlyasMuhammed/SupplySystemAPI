import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';

import { AgingTableComponent } from './aging-table.component';
import { StatementViewComponent } from '../statement-view/statement-view.component';
import { aging, statement } from '../ar-ap.testing';

describe('AgingTableComponent (A48 tasks 541 / 543 / 544)', () => {
  async function make(report = aging()) {
    await TestBed.configureTestingModule({ imports: [AgingTableComponent], providers: [provideRouter([])] }).compileComponents();
    const f = TestBed.createComponent(AgingTableComponent);
    f.componentInstance.report = report;
    f.detectChanges();
    return f;
  }

  it('a column per bucket, a hold pill, the total row; no link without partnerLink', async () => {
    const f = await make();
    const el: HTMLElement = f.nativeElement;
    expect([...el.querySelectorAll('thead th')].map(t => t.textContent!.trim())).toEqual(['', 'Customer', 'Current', '1-30', '31-60', '61-90', '91-120', '120+', 'Total']);
    expect(el.querySelector('[data-testid="aging-row-c2"]')!.textContent).toContain('Hold');
    expect(el.querySelector('[data-testid="aging-total"]')!.textContent).toContain('78,100.00');
    expect(el.querySelector('[data-testid="aging-link-c1"]')).toBeNull();
  });

  it('a row click asks the parent for the detail once; given details render under the row', async () => {
    const f = await make();
    const c = f.componentInstance;
    const asked: string[] = [];
    c.expand.subscribe((id: string) => asked.push(id));
    c.toggle(c.report!.lines[0]);
    expect(asked).toEqual(['c1']);
    c.details = { c1: [{ documentType: 'SALES_INVOICE', documentUuid: 'd9', documentNumber: 'SI-9', documentDate: '2026-08-01', dueDate: '2026-08-31',
      originalAmount: 10, openAmount: 10, daysOverdue: 5, bucketLabel: '1-30', currencyCode: 'USD', openAmountBase: 2800 }] };
    c.baseCurrency = 'PKR';
    f.detectChanges();
    const row = f.nativeElement.querySelector('[data-testid="aging-detail-SI-9"]') as HTMLElement;
    expect(row.textContent).toContain('2,800.00');
    expect(row.textContent).toContain('10.00 USD');
    c.toggle(c.report!.lines[0]);
    f.detectChanges();
    expect(f.nativeElement.querySelector('[data-testid="aging-detail-SI-9"]')).toBeNull();
  });

  it('unapplied credits and net show beside the buckets when present (D-RPT-2)', async () => {
    const r = aging();
    r.lines[0].unappliedCredits = 600;
    r.totals.unappliedCredits = 600;
    const f = await make(r);
    expect(f.nativeElement.querySelector('[data-testid="aging-row-c1"]').textContent).toContain('−600.00');
    expect(f.nativeElement.querySelector('[data-testid="aging-net"]').textContent).toContain('77,500.00');
  });

  it('empty report says so', async () => {
    const f = await make(aging({ lines: [] }));
    expect(f.nativeElement.querySelector('[data-testid="aging-empty"]')).not.toBeNull();
  });
});

describe('StatementViewComponent (A48 C4 / C13)', () => {
  it('Open items show due date and status; Activity shows the opening row', async () => {
    await TestBed.configureTestingModule({ imports: [StatementViewComponent] }).compileComponents();
    const f = TestBed.createComponent(StatementViewComponent);
    f.componentInstance.statement = statement({ type: 'OpenItems', agingLabels: ['Current', '1-30'],
      sections: [{ currencyCode: 'PKR', openingBalance: 0, closingBalance: 50, totalDebits: 50, totalCredits: 0, aging: [50, 0],
        lines: [{ date: '2026-01-05', entryNumber: 'SI-1', documentType: 'SALES_INVOICE', description: 'Invoice', dueDate: '2026-02-04', debit: 50, runningBalance: 50, status: 'Open' }] }] });
    f.detectChanges();
    const el: HTMLElement = f.nativeElement;
    expect(el.textContent).toContain('SI-1');
    expect(el.textContent).toContain('Sales Invoice');
    expect(el.textContent).not.toContain('Opening balance');
    expect(el.querySelector('[data-testid="statement-aging-PKR"]')!.textContent).toContain('Current');
    expect(el.querySelector('[data-testid="statement-aging-PKR"]')!.textContent).toContain('50.00');
    f.componentInstance.statement = { ...f.componentInstance.statement!, type: 'Activity' };
    f.detectChanges();
    expect(el.textContent).toContain('Opening balance');
  });
});
