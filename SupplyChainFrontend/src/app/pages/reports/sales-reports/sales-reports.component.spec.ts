import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute, Router, convertToParamMap, ParamMap } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { BehaviorSubject, of } from 'rxjs';

import { SalesReportsComponent } from './sales-reports.component';
import { SalesReportPanelComponent } from './sales-report-panel/sales-report-panel.component';
import { SalesReportsService } from '../../../services/sales-reports.service';
import { BusinessPartnerService } from '../../../services/business-partner.service';
import { InventoryService } from '../../../services/inventory.service';
import { AuthService } from '../../service/auth.service';

const ALL_PERMISSIONS = [
  'REPORT_VIEW', 'REPORT_EXPORT', 'SALE_ORDER_VIEW', 'CUSTOMER_LEDGER_VIEW', 'SALES_INVOICE_VIEW',
  'DELIVERY_VIEW', 'PRODUCT_LEDGER_VIEW'
];

const emptyReport = { generatedAt: '2026-09-21T00:00:00', totalRecords: 0, page: 1, pageSize: 20, totalPages: 1, totals: [], items: [], invoices: [], customers: [] };

describe('SalesReportsComponent', () => {
  let fixture: ComponentFixture<SalesReportsComponent>;
  let component: SalesReportsComponent;
  let reports: jasmine.SpyObj<SalesReportsService>;
  let params: BehaviorSubject<ParamMap>;
  let navigate: jasmine.Spy;
  let permissions: string[];

  const auth = { hasPermission: (code: string) => permissions.includes(code) } as unknown as AuthService;

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  async function setup(reportKey: string | null = null) {
    reports = jasmine.createSpyObj<SalesReportsService>('SalesReportsService', ['getReport', 'download']);
    reports.getReport.and.returnValue(of({ success: true, message: '', result: emptyReport } as any));

    const partners = jasmine.createSpyObj<BusinessPartnerService>('BusinessPartnerService', ['getPartners']);
    const inventory = jasmine.createSpyObj<InventoryService>('InventoryService', ['getProducts', 'getProductById', 'getWarehouses']);
    inventory.getProducts.and.returnValue(of({ success: true, message: '', result: { data: [] } } as any));
    inventory.getWarehouses.and.returnValue(of({ success: true, message: '', result: [] } as any));

    params = new BehaviorSubject<ParamMap>(convertToParamMap(reportKey ? { report: reportKey } : {}));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [SalesReportsComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(),
        { provide: SalesReportsService, useValue: reports },
        { provide: BusinessPartnerService, useValue: partners },
        { provide: InventoryService, useValue: inventory },
        { provide: AuthService, useValue: auth },
        { provide: ActivatedRoute, useValue: { paramMap: params } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(SalesReportsComponent);
    component = fixture.componentInstance;
    navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    fixture.detectChanges();
  }

  beforeEach(() => { permissions = [...ALL_PERMISSIONS]; });

  it('shows all ten reports as cards, with their codes', async () => {
    await setup();

    const codes = Array.from(fixture.nativeElement.querySelectorAll('.card-code')).map((e: any) => e.textContent.trim());
    expect(codes).toEqual(['R1', 'R2', 'R3', 'R4', 'R5', 'R6', 'R7', 'R8', 'R9', 'R10']);
    expect(fixture.nativeElement.querySelectorAll('[data-testid^="card-"]').length).toBe(10);
  });

  it('asks for a report to be chosen, and runs nothing, until one is', async () => {
    await setup();

    expect(query('choose')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('app-sales-report-panel')).toBeNull();
    expect(reports.getReport).not.toHaveBeenCalled();
  });

  it('opens the report the address names, and runs it', async () => {
    await setup('aging-receivables');

    expect(component.selected!.code).toBe('R3');
    expect(fixture.nativeElement.querySelector('app-sales-report-panel')).not.toBeNull();
    expect(reports.getReport).toHaveBeenCalledOnceWith('aging-receivables', { page: 1, pageSize: 20 });
    expect(query('choose')).toBeNull();
    expect(query('card-aging-receivables')!.classList).toContain('selected');
  });

  it('goes to a reports address when its card is pressed', async () => {
    await setup();

    query('card-margin-analysis')!.click();

    expect(navigate).toHaveBeenCalledWith(['/portal/pages/reports/sales-reports', 'margin-analysis']);
  });

  it('changes report as the address changes, without leaving the page', async () => {
    await setup('order-register');
    expect(component.selected!.code).toBe('R1');

    params.next(convertToParamMap({ report: 'sales-by-customer' }));
    fixture.detectChanges();

    expect(component.selected!.code).toBe('R5');
    expect(fixture.nativeElement.querySelector('app-sales-report-panel')).not.toBeNull();
    expect(reports.getReport.calls.mostRecent().args[0]).toBe('sales-by-customer');
  });

  it('goes back to asking for a report when the address loses its report', async () => {
    await setup('order-register');

    params.next(convertToParamMap({}));
    fixture.detectChanges();

    expect(component.selected).toBeNull();
    expect(query('choose')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('app-sales-report-panel')).toBeNull();
  });

  it('says so when the address names a report there is not', async () => {
    await setup('nope');

    expect(component.unknownKey).toBe('nope');
    expect(query('unknown')!.textContent).toContain('no report called "nope"');
    expect(query('choose')).toBeNull();
    expect(fixture.nativeElement.querySelector('app-sales-report-panel')).toBeNull();
  });

  // ── Who may see what ───────────────────────────────────────────────────────

  it('locks the reports whose books this user may not open, saying which permission is missing', async () => {
    permissions = ['REPORT_VIEW', 'REPORT_EXPORT', 'SALES_INVOICE_VIEW'];
    await setup();

    const open = component.reports.filter(r => component.canView(r)).map(r => r.code);
    expect(open).toEqual(['R3', 'R4', 'R5']);
    expect(query('card-order-register')!.hasAttribute('disabled')).toBeTrue();
    expect(query('card-aging-receivables')!.hasAttribute('disabled')).toBeFalse();
    expect(fixture.nativeElement.querySelectorAll('[data-testid="lock"]').length).toBe(7);
    expect(component.missing(component.reports[0])).toEqual(['SALE_ORDER_VIEW']);
    expect(component.lockedHint(component.reports[7])).toBe('Needs PRODUCT_LEDGER_VIEW');
  });

  it('needs every permission a report lists, not just one of them', async () => {
    permissions = ['REPORT_VIEW', 'SALES_INVOICE_VIEW'];
    await setup();
    const salesVsPurchase = component.reports.find(r => r.code === 'R8')!;

    expect(component.canView(salesVsPurchase)).toBeFalse();
    expect(component.missing(salesVsPurchase)).toEqual(['PRODUCT_LEDGER_VIEW']);
  });

  it('opens nothing for a locked card', async () => {
    permissions = ['REPORT_VIEW'];
    await setup();

    component.open(component.reports[0]);

    expect(navigate).not.toHaveBeenCalled();
  });

  it('will not show a report the address names but the user may not see, and says what it needs', async () => {
    permissions = ['REPORT_VIEW', 'SALES_INVOICE_VIEW'];
    await setup('product-ledger');

    expect(component.selected).toBeNull();
    expect(component.denied!.code).toBe('R9');
    expect(query('denied')!.textContent).toContain('It needs PRODUCT_LEDGER_VIEW');
    expect(fixture.nativeElement.querySelector('app-sales-report-panel')).toBeNull();
    expect(reports.getReport).not.toHaveBeenCalled();
  });

  it('says so when no report is open to the user at all', async () => {
    permissions = ['REPORT_VIEW'];
    await setup();

    expect(component.availableCount).toBe(0);
    expect(query('none-available')).not.toBeNull();
    expect(query('choose')).toBeNull();
  });

  it('counts the reports open to the user', async () => {
    await setup();

    expect(component.availableCount).toBe(10);
    expect(query('none-available')).toBeNull();
  });

  it('shows the panel for the selected report, and gives it that reports definition', async () => {
    await setup('sales-vs-purchase');

    const panel = fixture.debugElement.query(sel => sel.componentInstance instanceof SalesReportPanelComponent)!.componentInstance as SalesReportPanelComponent;
    expect(panel.definition.code).toBe('R8');
  });

  it('scrolls to the report once a card has taken the user to it', async () => {
    await setup();
    const scroll = jasmine.createSpy('scrollIntoView');
    component.panelAnchor!.nativeElement.scrollIntoView = scroll;

    component.open(component.reports[2]);
    await new Promise(resolve => setTimeout(resolve));

    expect(scroll).toHaveBeenCalledTimes(1);
  });

  it('stops following the address when it is destroyed', async () => {
    await setup('order-register');

    fixture.destroy();

    expect(params.observed).toBeFalse();
  });
});
