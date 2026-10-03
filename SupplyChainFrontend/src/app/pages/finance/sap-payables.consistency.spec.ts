import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import {
  ActivatedRoute, ActivatedRouteSnapshot, CanActivateFn, Route, Router, RouterStateSnapshot, Routes, convertToParamMap, provideRouter
} from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import routes from '../pages.routes';
import { SupplierPaymentCreateComponent } from './supplier-payments/supplier-payment-create/supplier-payment-create.component';
import { SupplierPaymentListComponent } from './supplier-payments/supplier-payment-list/supplier-payment-list.component';
import { SupplierPaymentDetailComponent } from './supplier-payments/supplier-payment-detail/supplier-payment-detail.component';
import { InvoiceDetailComponent } from './invoices/invoice-detail/invoice-detail.component';
import { SupplierDetailComponent } from '../suppliers/supplier-detail/supplier-detail.component';
import { FinanceReportsComponent } from '../reports/finance-reports/finance-reports.component';

import {
  FinanceService, INVOICE_MATCH_STATUSES, InvoiceDetailModel, ONLY_APPROVED_INVOICES_PAYABLE, OutstandingInvoiceModel,
  SupplierPaymentDetailModel
} from '../../services/finance.service';
import { SupplierService } from '../../services/supplier.service';
import { ReportsService, InvoiceAgingItem, InvoiceAgingBucketSummary } from '../../services/reports.service';
import { PdfService } from '../../services/pdf.service';
import { AuthService } from '../service/auth.service';

/**
 * Cross-page consistency, round 2 — item E (USER decision): only an Approved supplier invoice is a payable.
 * Every page that lists, totals or pays supplier invoices says so in the same words (the shared
 * ONLY_APPROVED_INVOICES_PAYABLE), offers "Record Payment" to exactly the users the payment route and the server
 * accept, and only on an Approved invoice. Plus the pages' own permission gates against their routes, and the
 * payables report's dates.
 */

function ok<T>(result: T) {
  return of({ success: true, message: '', result } as any);
}

const auth = (held: string[]) => ({
  hasPermission: (c: string) => held.includes(c),
  hasAnyPermission: (...c: string[]) => c.some(x => held.includes(x)),
  isAuthenticated: () => true
}) as unknown as AuthService;

const text = (el: Element | null) => (el?.textContent ?? '').replace(/\s+/g, ' ');

/** Permissions a payables user might hold; each gate is checked against every one of them on its own. */
const UNIVERSE = [
  'PAYMENT_PROCESS', 'PAYMENT_VIEW', 'PAYMENT_APPROVE', 'INVOICE_VIEW', 'INVOICE_PROCESS', 'SUPPLIER_VIEW', 'SUPPLIER_MANAGE', 'REPORT_VIEW'
];

function route(path: string): Route {
  const r = (routes as Routes).find(x => x.path === path);
  if (!r) throw new Error(`no route ${path}`);
  return r;
}

function routeAdmits(r: Route, held: string[]): boolean {
  TestBed.resetTestingModule().configureTestingModule({
    providers: [
      { provide: AuthService, useValue: auth(held) },
      { provide: Router, useValue: { navigate: () => Promise.resolve(true) } }
    ]
  });
  return (r.canActivate ?? []).every(g =>
    TestBed.runInInjectionContext(() => (g as CanActivateFn)({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot)) === true);
}

function supplierInvoice(overrides: Partial<InvoiceDetailModel> = {}): InvoiceDetailModel {
  return {
    uuid: 'inv-1', invoiceNumber: 'INV-1', supplierId: 'sup-1', supplierName: 'Karachi Steel', poUuid: 'po-1', poNumber: 'PO-1',
    invoiceDate: '2026-09-15T00:00:00', receivedDate: '2026-09-16T00:00:00', dueDate: '2026-10-15T00:00:00', currency: 'PKR',
    subtotal: 1000, taxAmount: 170, totalAmount: 1170, matchedPoValue: 1000, matchedGrnValue: 1000, varianceAmount: 0,
    matchStatus: 'Approved', paymentStatus: 'Unpaid', paidAmount: 0, approvedAt: '2026-09-20T10:00:00',
    createdDate: '2026-09-16T10:00:00', lines: [], payments: [], debitNotes: [], creditNotes: [], ...overrides
  };
}

describe('SAP alignment consistency (round 2, E): only approved supplier invoices are payables', () => {

  // ── One wording ────────────────────────────────────────────────────────────

  describe('every payables list explains an empty or partial list in the same words', () => {
    it('the shared wording itself', () => {
      expect(ONLY_APPROVED_INVOICES_PAYABLE).toBe('Only approved invoices can be paid.');
    });

    async function paymentCreate(invoices: OutstandingInvoiceModel[]): Promise<HTMLElement> {
      const finance = jasmine.createSpyObj<FinanceService>('FinanceService', ['getOutstandingInvoices', 'createSupplierPayment']);
      finance.getOutstandingInvoices.and.returnValue(ok(invoices));
      const suppliers = jasmine.createSpyObj<SupplierService>('SupplierService', ['getSupplierById', 'getSuppliers']);
      suppliers.getSupplierById.and.returnValue(ok({ uuid: 'sup-1', supplierName: 'Karachi Steel' }));
      suppliers.getSuppliers.and.returnValue(ok({ data: [], totalRecords: 0 }));
      await TestBed.resetTestingModule().configureTestingModule({
        imports: [SupplierPaymentCreateComponent],
        providers: [
          provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(),
          { provide: FinanceService, useValue: finance },
          { provide: SupplierService, useValue: suppliers },
          { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap({ supplierId: 'sup-1' }), paramMap: convertToParamMap({}) } } }
        ]
      }).compileComponents();
      const fixture = TestBed.createComponent(SupplierPaymentCreateComponent);
      fixture.detectChanges();
      fixture.detectChanges();
      return fixture.nativeElement as HTMLElement;
    }

    it('supplier payment: with nothing to pay, and beside the picker when there is', async () => {
      expect(text(await paymentCreate([]))).withContext('empty').toContain(ONLY_APPROVED_INVOICES_PAYABLE);
      const one: OutstandingInvoiceModel = {
        invoiceUuid: 'inv-1', invoiceNumber: 'INV-1', totalAmount: 1170, outstandingAmount: 1170, paymentStatus: 'Unpaid', dueDate: '2026-10-15T00:00:00'
      };
      expect(text(await paymentCreate([one]))).withContext('picker').toContain(ONLY_APPROVED_INVOICES_PAYABLE);
    });

    it("supplier detail's Outstanding Invoices tab", () => {
      const page = Object.create(SupplierDetailComponent.prototype) as Record<string, any>;
      expect(page['noPayablesHint']).toContain(ONLY_APPROVED_INVOICES_PAYABLE);
    });

    async function financeReports(items: InvoiceAgingItem[], buckets: InvoiceAgingBucketSummary[] = []) {
      const reports = jasmine.createSpyObj<ReportsService>('ReportsService', ['getInvoiceAging', 'getPaymentSummary', 'getBudgetUtilization']);
      reports.getInvoiceAging.and.returnValue(ok({ items, buckets }));
      reports.getPaymentSummary.and.returnValue(ok({ totalProcessed: 0, processedCount: 0, totalPending: 0, pendingCount: 0, totalReversed: 0, reversedCount: 0, byMethod: [] }));
      reports.getBudgetUtilization.and.returnValue(ok([]));
      await TestBed.resetTestingModule().configureTestingModule({
        imports: [FinanceReportsComponent],
        providers: [
          provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations(),
          { provide: ReportsService, useValue: reports },
          { provide: PdfService, useValue: jasmine.createSpyObj('PdfService', ['downloadTableReport']) }
        ]
      }).compileComponents();
      const fixture = TestBed.createComponent(FinanceReportsComponent);
      fixture.detectChanges();
      return { fixture, reports };
    }

    it('Reports → Finance Reports → Invoice Ageing (now approved invoices only)', async () => {
      const { fixture } = await financeReports([]);
      expect(text(fixture.nativeElement)).withContext('ageing empty state').toContain(ONLY_APPROVED_INVOICES_PAYABLE);
    });

    it('Invoice Ageing shows what is still owed (outstandingAmount), as the supplier aging and payables reports do', async () => {
      // ReportsModels.InvoiceAgingItem.OutstandingAmount = TotalAmount − PaidAmount; the buckets sum it.
      const partPaid = {
        invoiceNumber: 'INV-1', supplierInvoiceNo: null, supplierName: 'Karachi Steel', dueDate: '2026-09-01T00:00:00',
        totalAmount: 1170, outstandingAmount: 670, paymentStatus: 'PARTIALLY_PAID', daysOverdue: 31, agingBucket: '31–60 days'
      } as InvoiceAgingItem;
      const { fixture } = await financeReports([partPaid], [{ bucket: '31–60 days', count: 1, totalAmount: 670 }]);
      fixture.detectChanges();
      const table = (fixture.nativeElement as HTMLElement).querySelector('p-table') as HTMLElement;

      expect(text(table)).withContext('ageing table header').toContain('Outstanding');
      expect(text(table)).withContext('ageing row').toContain('670.00');
    });

    it('Finance Reports sends its date filter as the days picked (no shift east of UTC)', async () => {
      const { fixture, reports } = await financeReports([]);
      const c = fixture.componentInstance;
      c.dateFrom = new Date(2026, 9, 1);
      c.dateTo = new Date(2026, 9, 31);
      c.load();

      const filter = reports.getPaymentSummary.calls.mostRecent().args[0] as { dateFrom?: string; dateTo?: string };
      expect([filter.dateFrom, filter.dateTo]).toEqual(['2026-10-01', '2026-10-31']);
    });
  });

  // ── Record Payment: who, and on what ───────────────────────────────────────

  describe('"Record Payment" appears for exactly the users the payment route admits, and only on an approved invoice', () => {
    const paymentRoute = () => route('finance/payments/create');

    function invoiceDetail(inv: InvoiceDetailModel, held: string[]) {
      return Object.assign(Object.create(InvoiceDetailComponent.prototype), { invoice: inv, authService: auth(held), newPayments: [] }) as InvoiceDetailComponent;
    }

    const gates: { page: string; shows: (held: string[]) => boolean }[] = [
      { page: 'invoice detail (Approved, unpaid)', shows: held => invoiceDetail(supplierInvoice(), held).canPay() },
      { page: 'supplier detail', shows: held => (Object.assign(Object.create(SupplierDetailComponent.prototype), { authService: auth(held) }) as any).canRecordPayment },
      { page: 'supplier payment list', shows: held => (Object.assign(Object.create(SupplierPaymentListComponent.prototype), { authService: auth(held) }) as any).canRecordPayment }
    ];

    it('each page shows it to the same users as finance/payments/create admits', () => {
      const admitted = UNIVERSE.filter(p => routeAdmits(paymentRoute(), [p]));
      expect(admitted).toEqual(['PAYMENT_PROCESS']);
      for (const g of gates) {
        expect(UNIVERSE.filter(p => g.shows([p]) === true)).withContext(g.page).toEqual(admitted);
      }
    });

    it('never on an invoice that is not Approved, whatever the user holds', () => {
      const everything = [...UNIVERSE];
      for (const status of INVOICE_MATCH_STATUSES.filter(s => s !== 'Approved')) {
        expect(invoiceDetail(supplierInvoice({ matchStatus: status }), everything).canPay()).withContext(status).toBeFalse();
      }
      expect(invoiceDetail(supplierInvoice({ paymentStatus: 'Paid' }), everything).canPay()).withContext('Approved but paid').toBeFalse();
    });
  });

  // ── Supplier payment detail: actions vs permissions ────────────────────────

  describe('a supplier payment offers each action only to the users the server accepts for it', () => {
    function payment(overrides: Partial<SupplierPaymentDetailModel>): SupplierPaymentDetailModel {
      return {
        uuid: 'pay-1', paymentNumber: 'SPAY-1', supplierId: 'sup-1', supplierName: 'Karachi Steel', paymentDate: '2026-10-02T00:00:00',
        paymentMethod: 'CHEQUE', totalAmount: 1170, chequeNo: '000123', status: 'DRAFT', createdBy: 1, createdDate: '2026-10-02T09:00:00',
        paymentType: 'STANDARD', lines: [], ...overrides
      };
    }

    async function buttons(p: SupplierPaymentDetailModel, held: string[]): Promise<string[]> {
      const finance = jasmine.createSpyObj<FinanceService>('FinanceService', ['getSupplierPaymentById']);
      finance.getSupplierPaymentById.and.returnValue(ok(p));
      await TestBed.resetTestingModule().configureTestingModule({
        imports: [SupplierPaymentDetailComponent],
        providers: [
          provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(),
          { provide: FinanceService, useValue: finance },
          { provide: AuthService, useValue: auth(held) },
          { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ uuid: p.uuid }) } } }
        ]
      }).compileComponents();
      const fixture = TestBed.createComponent(SupplierPaymentDetailComponent);
      fixture.detectChanges();
      return Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('button'))
        .map(b => (b.textContent ?? '').trim())
        .filter(t => ['Approve', 'Post', 'Bounce', 'Cancel'].includes(t));
    }

    it('Post and Cancel (PAYMENT_PROCESS) on an approved payment', async () => {
      expect(await buttons(payment({ status: 'APPROVED' }), ['PAYMENT_VIEW'])).withContext('viewer').toEqual([]);
      expect((await buttons(payment({ status: 'APPROVED' }), ['PAYMENT_PROCESS'])).sort()).withContext('processor').toEqual(['Cancel', 'Post']);
    });

    it('Bounce (PAYMENT_PROCESS) on a posted cheque', async () => {
      expect(await buttons(payment({ status: 'POSTED' }), ['PAYMENT_VIEW', 'PAYMENT_APPROVE'])).withContext('viewer/approver').toEqual([]);
      expect(await buttons(payment({ status: 'POSTED' }), ['PAYMENT_PROCESS'])).withContext('processor').toEqual(['Bounce']);
    });

    it('Approve (PAYMENT_APPROVE) on a draft — and Cancel only for PAYMENT_PROCESS', async () => {
      expect(await buttons(payment({ status: 'DRAFT' }), ['PAYMENT_APPROVE'])).withContext('approver').toEqual(['Approve']);
      expect(await buttons(payment({ status: 'DRAFT' }), ['PAYMENT_PROCESS'])).withContext('processor').toEqual(['Cancel']);
    });
  });

  // ── Models against the C# shapes ───────────────────────────────────────────

  it('the payables models carry the fields the server sends (FinanceModels.cs / ReportsModels.cs)', () => {
    const fields = <T>() => <K extends keyof T>(...keys: K[]) => keys;
    expect(fields<OutstandingInvoiceModel>()('invoiceUuid', 'invoiceNumber', 'totalAmount', 'outstandingAmount', 'paymentStatus', 'dueDate').length).toBe(6);
    expect(fields<InvoiceAgingItem>()('invoiceNumber', 'supplierInvoiceNo', 'supplierName', 'dueDate', 'totalAmount', 'outstandingAmount', 'paymentStatus', 'daysOverdue', 'agingBucket').length).toBe(9);
    expect(fields<InvoiceAgingBucketSummary>()('bucket', 'count', 'totalAmount').length).toBe(3);
    // Item C kept the names: MatchedPoValue is now "expected at PO prices" for what this invoice bills.
    expect(fields<InvoiceDetailModel>()('matchedPoValue', 'matchedGrnValue', 'varianceAmount', 'matchStatus', 'paymentStatus', 'paidAmount').length).toBe(6);
  });
});
