import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRouteSnapshot, CanActivateFn, Route, Router, RouterStateSnapshot, Routes, provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import routes from '../pages.routes';
import { AppMenu } from '../../layout/component/app.menu';
import { InvoiceListComponent } from './invoices/invoice-list/invoice-list.component';
import { InvoiceDetailComponent } from './invoices/invoice-detail/invoice-detail.component';
import { SalesInvoiceDetailComponent } from './sales-invoices/sales-invoice-detail/sales-invoice-detail.component';
import { MasterLedgerComponent } from './master-ledger/master-ledger.component';
import { SupplierDetailComponent } from '../suppliers/supplier-detail/supplier-detail.component';
import { PartnerDetailComponent } from '../suppliers/partner-detail/partner-detail.component';
import { TaxCodesComponent } from '../finance-setup/tax-codes/tax-codes.component';
import { ExchangeRatesComponent } from '../finance-setup/exchange-rates/exchange-rates.component';
import { INVOICE_STATUS_OPTIONS, INVOICE_STATUS_SEVERITY } from './receivables/receivables.shared';

import { FinanceService, INVOICE_MATCH_STATUSES, InvoiceDetailModel } from '../../services/finance.service';
import { CANCELLABLE_INVOICE_STATUSES, PAYABLE_INVOICE_STATUSES, SalesInvoiceDetailModel } from '../../services/sales-invoice.service';
import { QboSyncStatusStore } from '../../shared/components/qbo-sync-badge/qbo-sync-status.store';
import { AuthService } from '../service/auth.service';
import { TenantService } from '../service/tenant.service';

/**
 * Cross-page consistency (SAP alignment, S-7 "reverse, don't edit"): the new statuses (sales CANCELLED,
 * supplier Reversed) and ledger entry (INVOICE_REVERSED) are spelt, coloured and filtered the same everywhere;
 * nothing offers an action on a cancelled or reversed document; buttons appear exactly for the roles the
 * server accepts; and every menu entry for these pages leads somewhere its user may go.
 */

const auth = (held: string[]) => ({
  hasPermission: (c: string) => held.includes(c),
  hasAnyPermission: (...c: string[]) => c.some(x => held.includes(x)),
  isAuthenticated: () => true
}) as unknown as AuthService;

function salesInvoice(overrides: Partial<SalesInvoiceDetailModel> = {}): SalesInvoiceDetailModel {
  return {
    uuid: 'si-1', invoiceNumber: 'SINV-1', saleOrderUuid: 'so-1', saleOrderNumber: 'SO-1', partnerId: 'p-1', partnerName: 'Acme',
    invoiceDate: '2026-09-20T00:00:00', dueDate: '2026-10-20T00:00:00', grandTotal: 1170, amountPaid: 0, balanceDue: 1170,
    status: 'ISSUED', currencyCode: 'PKR', traceId: 't', subtotal: 1000, discountAmount: 0, taxAmount: 170,
    createdDate: '2026-09-20T09:00:00', lines: [], payments: [], ...overrides
  } as SalesInvoiceDetailModel;
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

/** The page's own getters and methods over a given record and user — no template, no HTTP. */
function salesDetail(inv: SalesInvoiceDetailModel, held: string[]): SalesInvoiceDetailComponent {
  const c = Object.create(SalesInvoiceDetailComponent.prototype) as SalesInvoiceDetailComponent;
  Object.assign(c, { invoice: inv, authService: auth(held) });
  return c;
}

function supplierDetail(inv: InvoiceDetailModel, held: string[]): InvoiceDetailComponent {
  const c = Object.create(InvoiceDetailComponent.prototype) as InvoiceDetailComponent;
  Object.assign(c, { invoice: inv, authService: auth(held), newPayments: [] });
  return c;
}

describe('SAP alignment consistency: statuses, permissions and navigation', () => {

  // ── Status vocabularies ────────────────────────────────────────────────────

  describe('status vocabularies match the server and each other', () => {
    it('sales invoices: every server status (CANCELLED included) has a colour and a list filter', () => {
      // SalesInvoiceFilter.Status on the server: DRAFT, ISSUED, PARTIALLY_PAID, PAID, OVERDUE, CANCELLED, CREDIT_NOTE.
      const server = ['DRAFT', 'ISSUED', 'PARTIALLY_PAID', 'PAID', 'OVERDUE', 'CANCELLED', 'CREDIT_NOTE'];
      expect(INVOICE_STATUS_OPTIONS.map(o => o.value).filter(v => v)).toEqual(server);
      for (const s of server) expect(INVOICE_STATUS_SEVERITY[s]).withContext(s).toBeDefined();
      expect(INVOICE_STATUS_SEVERITY['CANCELLED']).not.toBe('success');
    });

    it('sales invoices: only ISSUED/OVERDUE can be cancelled (SalesInvoiceService.EnsureCancellableAsync), and a cancelled one is never payable', () => {
      expect([...CANCELLABLE_INVOICE_STATUSES].sort()).toEqual(['ISSUED', 'OVERDUE']);
      expect(PAYABLE_INVOICE_STATUSES).not.toContain('CANCELLED');
    });

    it('supplier invoices: every match status (Reversed included) is in the list filter, and list and detail colour them alike', () => {
      // InvoiceListItemModel.MatchStatus on the server: Pending | Matched | Variance | Approved | Rejected | Reversed.
      const server = ['Pending', 'Matched', 'Variance', 'Approved', 'Rejected', 'Reversed'];
      expect([...INVOICE_MATCH_STATUSES]).toEqual(server as typeof INVOICE_MATCH_STATUSES);

      const list = Object.create(InvoiceListComponent.prototype) as InvoiceListComponent;
      const detail = Object.create(InvoiceDetailComponent.prototype) as InvoiceDetailComponent;
      for (const s of server) {
        expect(detail.getMatchSeverity(s)).withContext(`${s}: detail vs list`).toBe(list.getMatchSeverity(s) as any);
      }
      expect(list.getMatchSeverity('Reversed')).withContext('Reversed must not look like Approved').not.toBe(list.getMatchSeverity('Approved'));
    });

    it('INVOICE_REVERSED is filterable on the master ledger and coloured as a credit on all three ledgers', () => {
      const master = new MasterLedgerComponent({} as any, {} as any, {} as any, {} as any);
      expect(master.transactionTypeOptions.map(o => o.value)).toContain('INVOICE_REVERSED');
      expect(master.getTypeLabel('INVOICE_REVERSED')).not.toBe('INVOICE_REVERSED');

      const credit = master.getTypeSeverity('PAYMENT');
      expect(master.getTypeSeverity('INVOICE_REVERSED')).withContext('master ledger').toBe(credit);
      expect(SupplierDetailComponent.prototype.getLedgerTypeSeverity.call(null, 'INVOICE_REVERSED')).withContext('supplier detail').toBe(credit as any);
      expect(PartnerDetailComponent.prototype.getLedgerTypeSeverity.call(null, 'INVOICE_REVERSED')).withContext('partner detail').toBe(credit as any);
    });
  });

  // ── No actions on cancelled / reversed documents ───────────────────────────

  describe('a cancelled or reversed document offers no actions', () => {
    const everything = ['SALES_INVOICE_VIEW', 'SALES_INVOICE_MANAGE', 'CUSTOMER_PAYMENT_RECORD', 'INVOICE_VIEW', 'INVOICE_PROCESS', 'PAYMENT_PROCESS'];

    it('a CANCELLED sales invoice: no edit, issue, delete, payment or cancel', () => {
      const c = salesDetail(salesInvoice({ status: 'CANCELLED', balanceDue: 0, cancelledAt: '2026-09-22T10:00:00' }), everything);
      expect([c.canEdit, c.canIssue, c.canDelete, c.canRecordPayment, c.canCancel]).toEqual([false, false, false, false, false]);
    });

    it('a Reversed supplier invoice: no approve, reject, payment, tax change or reverse', () => {
      const c = supplierDetail(supplierInvoice({ matchStatus: 'Reversed', reversedAt: '2026-09-30T09:00:00' }), everything);
      expect([c.canApprove(), c.canReject(), c.canPay(), c.canEditTax(), c.canSeeReverse()]).toEqual([false, false, false, false, false]);
    });
  });

  // ── Buttons follow the server's permissions ────────────────────────────────

  describe('buttons appear exactly for the roles the server accepts', () => {
    it('Cancel invoice (POST /sales-invoices/{id}/cancel needs SALES_INVOICE_MANAGE)', () => {
      expect(salesDetail(salesInvoice(), ['SALES_INVOICE_VIEW']).canCancel).withContext('viewer').toBeFalse();
      expect(salesDetail(salesInvoice(), ['SALES_INVOICE_MANAGE']).canCancel).withContext('manager').toBeTrue();
    });

    it('Reverse (POST /finance/invoices/{id}/reverse needs INVOICE_PROCESS)', () => {
      expect(supplierDetail(supplierInvoice(), ['INVOICE_VIEW']).canSeeReverse()).withContext('viewer').toBeFalse();
      expect(supplierDetail(supplierInvoice(), ['INVOICE_PROCESS']).canSeeReverse()).withContext('processor').toBeTrue();
    });

    it('Tax code writes (FINANCE_SETUP_MANAGE) and exchange rate writes (A35: CURRENCY_RATE_MANAGE); anyone else the page admits gets it read-only', () => {
      for (const [Page, code] of [[TaxCodesComponent, 'FINANCE_SETUP_MANAGE'], [ExchangeRatesComponent, 'CURRENCY_RATE_MANAGE']] as const) {
        const viewer = Object.assign(Object.create(Page.prototype), { authService: auth(['INVOICE_VIEW']) });
        const manager = Object.assign(Object.create(Page.prototype), { authService: auth([code]) });
        expect(viewer.canManage).withContext(`${Page.name} viewer`).toBeFalse();
        expect(manager.canManage).withContext(`${Page.name} manager`).toBeTrue();
      }
    });

    it('"New Invoice" on the supplier invoice list only for INVOICE_PROCESS (the create route refuses anyone else)', async () => {
      async function newInvoiceButton(held: string[]): Promise<HTMLElement | null> {
        const finance = jasmine.createSpyObj<FinanceService>('FinanceService', ['getInvoices']);
        finance.getInvoices.and.returnValue(of({ success: true, message: '', result: { data: [], totalRecords: 0 } } as any));
        await TestBed.resetTestingModule().configureTestingModule({
          imports: [InvoiceListComponent],
          providers: [
            provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(),
            { provide: FinanceService, useValue: finance },
            { provide: AuthService, useValue: auth(held) },
            { provide: QboSyncStatusStore, useValue: { isAvailable: () => false } }
          ]
        }).compileComponents();
        const fixture = TestBed.createComponent(InvoiceListComponent);
        fixture.detectChanges();
        const buttons = Array.from((fixture.nativeElement as HTMLElement).querySelectorAll('button')) as HTMLElement[];
        return buttons.find(b => b.textContent?.includes('New Invoice')) ?? null;
      }

      expect(await newInvoiceButton(['INVOICE_VIEW'])).withContext('INVOICE_VIEW only').toBeNull();
      expect(await newInvoiceButton(['INVOICE_PROCESS'])).withContext('INVOICE_PROCESS').not.toBeNull();
    });
  });

  // ── Menu ↔ routes ──────────────────────────────────────────────────────────

  describe('menu entries for these pages lead somewhere their user may go', () => {
    type NavItem = { label?: string; routerLink?: string[]; permRequired?: string[]; featureCode?: string; items?: NavItem[] };

    const IN_SCOPE = /^\/portal\/pages\/(finance\/|finance-setup\/|integrations\/quickbooks|sales\/orders|organizations$)/;

    function leaves(items: NavItem[], feature?: string): (NavItem & { feature?: string })[] {
      return items.flatMap(i => i.items?.length
        ? leaves(i.items, i.featureCode ?? feature)
        : [{ ...i, feature: i.featureCode ?? feature }]);
    }

    function menuLeaves() {
      const menu = new AppMenu(auth([]), { tenant: () => null, hasFeature: () => true } as unknown as TenantService);
      return leaves((menu as unknown as { fullMenu: NavItem[] }).fullMenu)
        .filter(l => l.routerLink?.length && IN_SCOPE.test(l.routerLink[0]));
    }

    function routeFor(link: string): Route | undefined {
      const path = link.replace(/^\/portal\/pages\//, '');
      return (routes as Routes).find(r => r.path === path);
    }

    function admits(route: Route, held: string[]): boolean {
      TestBed.resetTestingModule().configureTestingModule({
        providers: [
          { provide: AuthService, useValue: auth(held) },
          { provide: Router, useValue: { navigate: () => Promise.resolve(true) } }
        ]
      });
      return (route.canActivate ?? []).every(g =>
        TestBed.runInInjectionContext(() => (g as CanActivateFn)({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot)) === true);
    }

    it('finds the in-scope entries (Tax Codes, Exchange Rates, invoices, sales invoices, master ledger, QuickBooks)', () => {
      const links = menuLeaves().map(l => l.routerLink![0]);
      for (const expected of [
        '/portal/pages/finance-setup/tax-codes', '/portal/pages/finance-setup/exchange-rates', '/portal/pages/finance/invoices',
        '/portal/pages/finance/invoices/create', '/portal/pages/finance/sales-invoices', '/portal/pages/finance/master-ledger',
        '/portal/pages/integrations/quickbooks'
      ]) expect(links).withContext(expected).toContain(expected);
    });

    it('every entry has a route (no dead links) and the route is guarded', () => {
      for (const l of menuLeaves()) {
        const route = routeFor(l.routerLink![0]);
        expect(route).withContext(`${l.label}: no route for ${l.routerLink![0]}`).toBeDefined();
        expect(route?.canActivate?.length ?? 0).withContext(`${l.label}: unguarded`).toBeGreaterThan(0);
      }
    });

    it('anyone the menu shows an entry to gets through its route guard, and the guard admits no one the menu hides it from', () => {
      const universe = [...new Set([
        ...menuLeaves().flatMap(l => l.permRequired ?? []),
        'INVOICE_VIEW', 'INVOICE_PROCESS', 'SALES_INVOICE_VIEW', 'SALES_INVOICE_MANAGE', 'FINANCE_SETUP_MANAGE',
        'PAYMENT_VIEW', 'PAYMENT_PROCESS', 'INTEGRATION_VIEW', 'INTEGRATION_MANAGE', 'SYSTEM_CONFIGURE', 'PLATFORM_SUPER_ADMIN'
      ])];

      for (const l of menuLeaves()) {
        const route = routeFor(l.routerLink![0]);
        // An entry with no permission list is gated otherwise (Super Admin only): nothing to compare.
        if (!route || !l.permRequired?.length) continue;
        const shownTo = [...(l.permRequired ?? [])].sort();
        const admitted = universe.filter(p => admits(route, [p])).sort();
        expect(admitted).withContext(`${l.label} (${l.routerLink![0]}): route admits vs menu shows`).toEqual(shownTo);
      }
    });

    it('the supplier-invoice list and detail need INVOICE_VIEW, as the API reads do — INVOICE_PROCESS alone would only get 403s', () => {
      for (const path of ['finance/invoices', 'finance/invoices/:uuid']) {
        const route = (routes as Routes).find(r => r.path === path)!;
        expect(admits(route, ['INVOICE_VIEW'])).withContext(`${path}: INVOICE_VIEW`).toBeTrue();
        expect(admits(route, ['INVOICE_PROCESS'])).withContext(`${path}: INVOICE_PROCESS without VIEW`).toBeFalse();
      }
      const allInvoices = menuLeaves().find(l => l.routerLink![0] === '/portal/pages/finance/invoices')!;
      expect(allInvoices.permRequired).toEqual(['INVOICE_VIEW']);
    });

    it('the Finance pages sit behind MODULE_FINANCE in the menu, as their controllers do', () => {
      for (const l of menuLeaves().filter(x => /\/(finance|finance-setup)\//.test(x.routerLink![0]) && !/master-ledger/.test(x.routerLink![0]))) {
        expect(l.feature).withContext(`${l.label}`).toBe('MODULE_FINANCE');
      }
    });
  });
});
