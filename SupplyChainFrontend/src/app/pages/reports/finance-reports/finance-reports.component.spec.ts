import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { FinanceReportsComponent } from './finance-reports.component';
import { ReportsService } from '../../../services/reports.service';
import { PdfService } from '../../../services/pdf.service';
import { ONLY_APPROVED_INVOICES_PAYABLE } from '../../../services/finance.service';

/** Invoice ageing (GET /api/reports/invoice-aging) lists approved supplier invoices only: nothing else is payable. */
describe('FinanceReportsComponent — invoice ageing', () => {
  let fixture: ComponentFixture<FinanceReportsComponent>;
  let reports: jasmine.SpyObj<ReportsService>;
  let pdf: jasmine.SpyObj<PdfService>;

  /** Part-paid: 1,170 billed, 500 paid, 670 still owed. */
  const PART_PAID = {
    invoiceNumber: 'INV-2026-00042', supplierInvoiceNo: 'KSW/881', supplierName: 'Karachi Steel', dueDate: '2026-10-15T00:00:00',
    totalAmount: 1170, outstandingAmount: 670, paymentStatus: 'PARTIALLY_PAID', daysOverdue: 3, agingBucket: '0-30'
  };

  async function setup(items: any[] = [], buckets: any[] = []) {
    reports = jasmine.createSpyObj<ReportsService>('ReportsService', ['getInvoiceAging', 'getPaymentSummary', 'getBudgetUtilization']);
    pdf = jasmine.createSpyObj<PdfService>('PdfService', ['downloadTableReport']);
    reports.getInvoiceAging.and.returnValue(of({ success: true, message: '', result: { items, buckets } } as any));
    reports.getPaymentSummary.and.returnValue(of({ success: true, message: '', result: null } as any));
    reports.getBudgetUtilization.and.returnValue(of({ success: true, message: '', result: [] } as any));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [FinanceReportsComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations(),
        { provide: ReportsService, useValue: reports },
        { provide: PdfService, useValue: pdf }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(FinanceReportsComponent);
    fixture.detectChanges();
  }

  function text(): string {
    return ((fixture.nativeElement as HTMLElement).textContent ?? '').replace(/\s+/g, ' ');
  }

  it('says why the ageing list is empty: only approved invoices are payable', async () => {
    await setup();

    expect(text()).toContain(`No ageing invoices. ${ONLY_APPROVED_INVOICES_PAYABLE}`);
  });

  it('says above the list that only approved invoices are aged', async () => {
    await setup([{ invoiceNumber: 'INV-2026-00042', supplierName: 'Karachi Steel', dueDate: '2026-10-15T00:00:00', totalAmount: 1170, paymentStatus: 'Unpaid', daysOverdue: 0, agingBucket: '0-30' }]);

    const note = fixture.nativeElement.querySelector('[data-testid="aging-approved-only"]') as HTMLElement | null;
    expect(note?.textContent).toContain('approved');
    expect(text()).toContain('INV-2026-00042');
  });

  it('sends the date filter as the days picked, not a day early east of UTC', async () => {
    await setup();
    fixture.componentInstance.dateFrom = new Date(2026, 9, 1);
    fixture.componentInstance.dateTo   = new Date(2026, 9, 31);

    fixture.componentInstance.load();

    expect(reports.getPaymentSummary.calls.mostRecent().args[0]).toEqual({ dateFrom: '2026-10-01', dateTo: '2026-10-31' });
    expect(reports.getBudgetUtilization.calls.mostRecent().args[0]).toEqual({ dateFrom: '2026-10-01', dateTo: '2026-10-31' });
  });

  it('does not print the From/To filter on the ageing PDF, which ignores it, and says only approved invoices are aged', async () => {
    await setup([{ invoiceNumber: 'INV-2026-00042', supplierName: 'Karachi Steel', dueDate: '2026-10-15T00:00:00', totalAmount: 1170, paymentStatus: 'Unpaid', daysOverdue: 0, agingBucket: '0-30' }]);
    fixture.componentInstance.dateFrom = new Date(2026, 9, 1);

    fixture.componentInstance.downloadAgingPdf();

    const cfg = pdf.downloadTableReport.calls.mostRecent().args[0];
    expect(cfg.dateFilter).toBeUndefined();
    expect(cfg.subtitle).toBe(ONLY_APPROVED_INVOICES_PAYABLE);
  });

  // ── What is still owed (the server ages TotalAmount − PaidAmount; nothing-owed invoices are left out) ──

  it('shows the invoice total and, beside it, what is still owed', async () => {
    await setup([PART_PAID]);

    const headers = Array.from(fixture.nativeElement.querySelectorAll('th') as NodeListOf<HTMLElement>).map(h => h.textContent?.trim());
    expect(headers).toContain('Invoice total');
    expect(headers).toContain('Outstanding');
    expect(headers).not.toContain('Amount');
    expect(fixture.nativeElement.querySelector('[data-testid="aging-total"]')?.textContent?.trim()).toBe('1,170.00');
    expect(fixture.nativeElement.querySelector('[data-testid="aging-outstanding"]')?.textContent?.trim()).toBe('670.00');
  });

  it('says the bucket cards total what is still owed', async () => {
    await setup([PART_PAID], [{ bucket: '0-30', count: 1, totalAmount: 670 }]);

    const card = fixture.nativeElement.querySelector('.summary-card') as HTMLElement | null;
    expect(card?.textContent).toContain('670.00');
    expect(card?.textContent).toContain('still owed');
  });

  it('puts what is still owed on the ageing PDF, totalled', async () => {
    await setup([PART_PAID, { ...PART_PAID, invoiceNumber: 'INV-2026-00043', totalAmount: 500, outstandingAmount: 500, paymentStatus: 'UNPAID' }]);

    fixture.componentInstance.downloadAgingPdf();

    const cfg = pdf.downloadTableReport.calls.mostRecent().args[0];
    const outstandingCol = cfg.columns.indexOf('Outstanding');
    expect(cfg.columns).toContain('Invoice total');
    expect(outstandingCol).toBeGreaterThan(-1);
    expect(cfg.rows.map(r => r[outstandingCol])).toEqual(['670.00', '500.00']);
    expect(cfg.totalsRow?.[outstandingCol]).toBe('1170.00');
  });
});
