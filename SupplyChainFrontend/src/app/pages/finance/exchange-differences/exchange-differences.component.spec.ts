import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of, throwError } from 'rxjs';

import { ExchangeDifferencesComponent } from './exchange-differences.component';
import { ExchangeDifferenceModel, ExchangeDifferenceService } from '../../../services/exchange-difference.service';
import { AuthService } from '../../service/auth.service';
import { TenantService } from '../../service/tenant.service';
import { provideTestOrgCurrencies } from '../../../shared/doc-currency/doc-currency.testing';

// A35-E-06 — the exchange-difference register (GET api/finance/exchange-differences) and "Run revaluation"
// (POST api/finance/exchange-revaluation/run, EXCHANGE_REVALUATION_RUN). Contract §7.1–§7.2.

const REALIZED: ExchangeDifferenceModel = {
  id: 'x1', kind: 'REALIZED', side: 'RECEIVABLE', documentType: 'SALES_INVOICE', documentUuid: 'si-1', documentNo: 'SINV-2026-00041',
  paymentType: 'CUSTOMER_PAYMENT', paymentUuid: 'cp-7', paymentNo: 'RCPT-2026-00007', currencyId: 'cur-aed', currencyCode: 'AED',
  amountCurrency: 10550, bookedRate: 76.3, settlementRate: 76.45, baseCurrencyId: 'cur-pkr', baseCurrencyCode: 'PKR',
  bookedAmountBase: 804965, settledAmountBase: 806547.5, differenceBase: 1582.5, accountCode: '7110',
  postedAt: '2026-10-14T09:12:00Z', revaluationDate: null
};
const UNREALIZED: ExchangeDifferenceModel = {
  ...REALIZED, id: 'x2', kind: 'UNREALIZED', side: 'PAYABLE', documentType: 'SUPPLIER_INVOICE', documentUuid: 'inv-3',
  documentNo: 'INV-2026-00003', paymentType: null, paymentUuid: null, paymentNo: null, settlementRate: 75.8,
  differenceBase: -5500, accountCode: '7140', postedAt: null, revaluationDate: '2026-10-31'
};

function page(rows: ExchangeDifferenceModel[]) {
  return of({ success: true, message: '', result: { data: rows, totalRecords: rows.length, page: 1, pageSize: 50, totalPages: 1 } } as any);
}

describe('ExchangeDifferencesComponent (A35-E-06)', () => {
  let fixture: ComponentFixture<ExchangeDifferencesComponent>;
  let component: ExchangeDifferencesComponent;
  let service: jasmine.SpyObj<ExchangeDifferenceService>;
  let permissions: string[];

  const q = (id: string): HTMLElement | null => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);
  const qa = (id: string): HTMLElement[] => Array.from(fixture.nativeElement.querySelectorAll(`[data-testid="${id}"]`));
  const text = (el: Element | null) => (el?.textContent ?? '').replace(/\s+/g, ' ').trim();

  async function setup() {
    service = jasmine.createSpyObj<ExchangeDifferenceService>('ExchangeDifferenceService', ['getDifferences', 'runRevaluation']);
    service.getDifferences.and.returnValue(page([REALIZED, UNREALIZED]));
    await TestBed.configureTestingModule({
      imports: [ExchangeDifferencesComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), provideTestOrgCurrencies(),
        { provide: ExchangeDifferenceService, useValue: service },
        { provide: AuthService, useValue: { hasPermission: (c: string) => permissions.includes(c) } },
        { provide: TenantService, useValue: { tenant: signal({ id: 'org-1' }) } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(ExchangeDifferencesComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  beforeEach(() => { permissions = ['INVOICE_VIEW', 'EXCHANGE_REVALUATION_RUN']; });

  it('lists the register: document, payment, amounts, both rates and the gain or loss in the base', async () => {
    await setup();
    expect(service.getDifferences).toHaveBeenCalledWith(jasmine.objectContaining({ page: 1, pageSize: 50 }));
    expect(qa('xd-document').map(text)).toEqual(['SINV-2026-00041', 'INV-2026-00003']);
    expect(qa('xd-payment').map(text)).toEqual(['RCPT-2026-00007', '—']);
    expect(qa('xd-amount').map(text)).toEqual(['AED 10,550.00', 'AED 10,550.00']);
    expect(qa('xd-rates').map(text)).toEqual(['76.3000 → 76.4500', '76.3000 → 75.8000']);
    expect(qa('xd-difference').map(text)).toEqual(['+PKR 1,582.50', '-PKR 5,500.00']);
    expect(qa('xd-difference')[0].classList).toContain('gain');
    expect(qa('xd-difference')[1].classList).toContain('loss');
    expect(qa('xd-date').map(text)).toEqual(['14 Oct 2026', '31 Oct 2026']);
  });

  it('filters by kind, side and dates (date-only, no UTC shift)', async () => {
    await setup();
    component.filterKind = 'UNREALIZED';
    component.filterSide = 'PAYABLE';
    component.filterFrom = new Date(2026, 9, 1);
    component.filterTo = new Date(2026, 9, 31);
    component.applyFilters();
    expect(service.getDifferences.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({
      kind: 'UNREALIZED', side: 'PAYABLE', from: '2026-10-01', to: '2026-10-31', page: 1
    }));
  });

  it('"Run revaluation" is there only with EXCHANGE_REVALUATION_RUN', async () => {
    permissions = ['INVOICE_VIEW'];
    await setup();
    expect(q('xd-run')).toBeNull();
  });

  it('runs the revaluation for the date picked, shows what it did and what it skipped, and reloads', async () => {
    await setup();
    service.runRevaluation.and.returnValue(of({ success: true, message: '', result: {
      revaluationDate: '2026-10-31', receivablesRevalued: 3, payablesRevalued: 2, rowsWritten: 5, rowsReplaced: 0,
      totals: [{ baseCurrencyCode: 'PKR', gain: 2600, loss: -5500, net: -2900 }],
      skipped: [{ documentType: 'SALES_INVOICE', documentNo: 'SINV-2026-00050', reason: 'No exchange rate for MXN on 2026-10-31.' }]
    } } as any));
    component.openRun();
    component.runDate = new Date(2026, 9, 31);
    component.run();
    fixture.detectChanges();

    expect(service.runRevaluation).toHaveBeenCalledOnceWith('2026-10-31');
    expect(text(q('xd-run-summary'))).toContain('3 receivables and 2 payables revalued');
    expect(text(q('xd-run-totals'))).toContain('PKR');
    expect(qa('xd-run-skipped').map(text)).toEqual(['SINV-2026-00050 — No exchange rate for MXN on 2026-10-31.']);
    expect(service.getDifferences).toHaveBeenCalledTimes(2);
  });

  it('a refused run says why', async () => {
    await setup();
    service.runRevaluation.and.returnValue(throwError(() => ({ status: 403, error: { message: 'Forbidden' } })));
    component.openRun();
    component.run();
    fixture.detectChanges();
    expect(text(q('xd-run-error'))).toBe('Forbidden');
  });
});
