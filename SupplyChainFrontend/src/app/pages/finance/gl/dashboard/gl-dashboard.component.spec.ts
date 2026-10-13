import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { HttpErrorResponse } from '@angular/common/http';
import { throwError } from 'rxjs';

import { GlDashboardComponent } from './gl-dashboard.component';
import { AccountingService } from '../../../../services/accounting.service';
import { fakeApi, okRes } from '../gl.testing';

describe('GlDashboardComponent (A42 0641–0643)', () => {
  let fixture: ComponentFixture<GlDashboardComponent>;
  let api: jasmine.SpyObj<AccountingService>;
  const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  async function setup() {
    api = fakeApi();
    api.getDashboard.and.returnValue(okRes({ totalAssets: 1450000, totalLiabilities: 800000, totalEquity: 650000, totalRevenue: 0, totalExpenses: 1234.5,
      currencyCode: 'PKR', accountCount: 72, fiscalYear: { name: 'FY 2026-27', startDate: '2026-07-01', endDate: '2027-06-30' } }));
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [GlDashboardComponent], providers: [provideRouter([]), { provide: AccountingService, useValue: api }]
    }).compileComponents();
    fixture = TestBed.createComponent(GlDashboardComponent);
    fixture.detectChanges();
  }

  afterEach(() => fixture?.destroy());

  it('tiles per root type with thousand separators and 2 dp, plus links to every GL page', async () => {
    await setup();
    expect(q('tile-assets')!.textContent).toContain('1,450,000.00');
    expect(q('tile-liabilities')!.textContent).toContain('800,000.00');
    expect(q('tile-equity')!.textContent).toContain('650,000.00');
    expect(q('tile-expenses')!.textContent).toContain('1,234.50');
    const hrefs = Array.from(q('links')!.querySelectorAll('a')).map(a => a.getAttribute('href'));
    expect(hrefs).toContain('/portal/pages/finance/gl/accounts');
    expect(hrefs).toContain('/portal/pages/finance/gl/setup-status');
    expect(fixture.nativeElement.textContent).toContain('FY 2026-27');
  });

  it('an error (e.g. the 403 before setup, redirected by the interceptor) shows the message', async () => {
    await setup();
    api.getDashboard.and.returnValue(throwError(() => new HttpErrorResponse({ status: 403, error: { error: 'FINANCE_SETUP_REQUIRED', message: 'Finish the general ledger setup first.' } })));
    fixture.componentInstance.load();
    fixture.detectChanges();
    expect(q('load-error')!.textContent).toContain('Finish the general ledger setup first.');
  });
});
