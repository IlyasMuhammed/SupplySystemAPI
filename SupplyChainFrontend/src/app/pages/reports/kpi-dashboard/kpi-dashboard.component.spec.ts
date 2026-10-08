import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of, throwError } from 'rxjs';
import { KpiDashboardComponent } from './kpi-dashboard.component';
import { KpiDashboardModel, OperationsKpiModel, ReportsService } from '../../../services/reports.service';

describe('KpiDashboardComponent', () => {
  let fixture: ComponentFixture<KpiDashboardComponent>;
  let component: KpiDashboardComponent;
  let reports: jasmine.SpyObj<ReportsService>;

  const core = (over: Partial<KpiDashboardModel> = {}): KpiDashboardModel => ({
    poCycleTimeDays: 4, supplierOnTimeDeliveryRate: 60, poFillRate: 96, stockTurnoverRatio: null,
    inventoryAccuracy: 99, invoiceProcessingTimeDays: 2, threeWayMatchRate: 92, budgetVariancePercent: 3,
    grnRejectionRate: 1, reorderTriggerCount: 0, ...over
  });

  const m = (value: number | null, basis = 10) => ({ value, basis });

  const ops = (over: Partial<OperationsKpiModel> = {}): OperationsKpiModel => ({
    from: '2026-07-09', to: '2026-10-07', windowDays: 90,
    sales: { quoteWinRate: m(45), inquiryConversionRate: m(80), orderCancellationRate: m(2), lateOpenOrderRate: m(30) },
    fulfilment: { onTimeShipmentRate: m(97), inFullRate: m(null, 0), orderToShipDays: m(2.5) },
    manufacturing: null,
    receivables: { daysSalesOutstanding: m(40), overdueRate: m(12), unconvertedInvoices: 2 },
    ...over
  });

  async function setup(coreResult: KpiDashboardModel | 'error', opsResult: OperationsKpiModel | 'error') {
    sessionStorage.removeItem('kpi_dashboard_v2');
    reports = jasmine.createSpyObj<ReportsService>('ReportsService', ['getKpis', 'getOperationsKpis']);
    reports.getKpis.and.returnValue(coreResult === 'error' ? throwError(() => ({ status: 500 })) : of({ success: true, result: coreResult } as any));
    reports.getOperationsKpis.and.returnValue(opsResult === 'error' ? throwError(() => ({ status: 500 })) : of({ success: true, result: opsResult } as any));

    await TestBed.configureTestingModule({
      imports: [KpiDashboardComponent],
      providers: [provideNoopAnimations(), { provide: ReportsService, useValue: reports }]
    }).compileComponents();

    fixture   = TestBed.createComponent(KpiDashboardComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  afterEach(() => sessionStorage.removeItem('kpi_dashboard_v2'));

  const q = (testId: string) => fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  const card = (id: string) => component.allCards.find(c => c.id === id)!;

  it('loads the original KPIs and the operations KPIs for the default 90-day window', async () => {
    await setup(core(), ops());
    expect(reports.getKpis).toHaveBeenCalledTimes(1);
    expect(reports.getOperationsKpis).toHaveBeenCalledOnceWith(90);
  });

  it('shows a section only for areas the server returned', async () => {
    await setup(core(), ops());

    expect(q('section-sales')).not.toBeNull();
    expect(q('section-fulfilment')).not.toBeNull();
    expect(q('section-receivables')).not.toBeNull();
    expect(q('section-procurement')).not.toBeNull();
    expect(q('section-manufacturing')).toBeNull('manufacturing came back null: the user cannot see production');
  });

  it('shows a KPI with nothing to measure as "no data" and leaves it out of the health score', async () => {
    await setup(core(), ops());

    const inFull = card('inFull');
    expect(component.status(inFull)).toBe('none');
    expect(component.formatValue(inFull)).toBe('—');
    expect(q('kpi-inFull').textContent).toContain('No data');
    expect(q('kpi-inFull').textContent).toContain('Nothing in this period yet');
    expect(component.measuredCards).not.toContain(inFull);
    expect(component.healthScore).toBe(Math.round(component.goodCount / component.measuredCards.length * 100));
    expect(q('no-data-note')).not.toBeNull();
  });

  it('grades lower-is-better and higher-is-better KPIs against their own targets', async () => {
    await setup(core(), ops());

    expect(component.status(card('quoteWin'))).toBe('good');      // 45 ≥ 40
    expect(component.status(card('lateOpen'))).toBe('bad');       // 30 > 15
    expect(component.status(card('overdueAr'))).toBe('warn');     // 12: over 10, within 25
    expect(component.status(card('supplierOtd'))).toBe('bad');    // 60 < 75
    expect(component.formatTarget(card('dso'))).toBe('≤ 45 days');
    expect(component.formatTarget(card('quoteWin'))).toBe('≥ 40%');
  });

  it('says how many records a KPI is based on', async () => {
    await setup(core(), ops());
    expect(q('kpi-quoteWin').textContent).toContain('Based on 10 records');
  });

  it('reloads the operations KPIs for another period', async () => {
    await setup(core(), ops());

    component.setWindow(30);

    expect(reports.getOperationsKpis).toHaveBeenCalledWith(30);
    expect(component.windowDays).toBe(30);
  });

  it('still shows the original KPIs when the operations KPIs fail', async () => {
    await setup(core(), 'error');

    expect(q('section-procurement')).not.toBeNull();
    expect(q('section-sales')).toBeNull();
  });

  it('warns about open invoices with no exchange rate', async () => {
    await setup(core(), ops());
    expect(fixture.nativeElement.textContent).toContain('2 open invoice(s) have no exchange rate on file');
  });
});
