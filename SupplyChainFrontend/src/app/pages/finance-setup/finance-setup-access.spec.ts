import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ActivatedRouteSnapshot, CanActivateFn, Router, RouterStateSnapshot, Routes } from '@angular/router';
import { of } from 'rxjs';

import routes from '../pages.routes';
import { AppMenu } from '../../layout/component/app.menu';
import { AuthService } from '../service/auth.service';
import { CurrentTenant } from '../service/tenant.service';
import { FinanceSetupService } from '../../services/finance-setup.service';
import { CurrenciesService } from '../../services/currencies.service';
import { TaxCodesComponent } from './tax-codes/tax-codes.component';
import { ExchangeRatesComponent } from './exchange-rates/exchange-rates.component';

/**
 * Who can open Settings → Tax Codes / Exchange Rates. FINANCE_SETUP_MANAGE maintains them; INVOICE_VIEW opens them
 * read-only for finance viewers. The guard, the menu and the pages all go through the real AuthService here, fed a
 * token carrying just the permissions under test, so the any-of check is the one the app really runs.
 */

const PATHS = ['finance-setup/tax-codes', 'finance-setup/exchange-rates'];

/** A real AuthService whose token carries these permissions (and has not expired). */
function authWith(permissions: string[], signedIn = true): AuthService {
  const auth = new AuthService({} as any, {} as any, {} as any);
  const payload = btoa(JSON.stringify({ exp: Math.floor(Date.now() / 1000) + 3600, permission: permissions }));
  spyOn(auth, 'getToken').and.returnValue(signedIn ? `header.${payload}.signature` : null);
  return auth;
}

describe('Finance setup access', () => {
  const find = (path: string) => (routes as Routes).find(r => r.path === path);

  // ── Route guard ───────────────────────────────────────────────────────────

  describe('routes', () => {
    function runGuard(path: string, auth: AuthService) {
      const navigate = jasmine.createSpy('navigate');
      TestBed.resetTestingModule().configureTestingModule({
        providers: [{ provide: AuthService, useValue: auth }, { provide: Router, useValue: { navigate } }]
      });
      const guards = (find(path)?.canActivate ?? []) as CanActivateFn[];
      expect(guards.length).withContext(`${path} must be guarded`).toBe(1);
      const allowed = TestBed.runInInjectionContext(() => guards[0]({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot));
      return { allowed, navigate };
    }

    it('routes the two pages', () => {
      expect(find('finance-setup/tax-codes')?.component).toBe(TaxCodesComponent);
      expect(find('finance-setup/exchange-rates')?.component).toBe(ExchangeRatesComponent);
    });

    it('lets a finance viewer with only INVOICE_VIEW in', () => {
      for (const path of PATHS) {
        const { allowed, navigate } = runGuard(path, authWith(['INVOICE_VIEW']));
        expect(allowed).withContext(path).toBeTrue();
        expect(navigate).not.toHaveBeenCalled();
      }
    });

    it('lets whoever maintains them in', () => {
      for (const path of PATHS) {
        expect(runGuard(path, authWith(['FINANCE_SETUP_MANAGE'])).allowed).withContext(path).toBeTrue();
      }
    });

    it('keeps out a user with neither permission — sales-invoice viewing is not enough', () => {
      for (const path of PATHS) {
        const { allowed, navigate } = runGuard(path, authWith(['SALES_INVOICE_VIEW', 'PAYMENT_VIEW']));
        expect(allowed).withContext(path).toBeFalse();
        expect(navigate).toHaveBeenCalledWith(['/portal/access-denied']);
      }
    });

    it('sends someone not signed in to the login page', () => {
      const { allowed, navigate } = runGuard(PATHS[0], authWith([], false));
      expect(allowed).toBeFalse();
      expect(navigate).toHaveBeenCalledWith(['/auth/login']);
    });
  });

  // ── Menu ──────────────────────────────────────────────────────────────────

  describe('menu', () => {
    function labels(auth: AuthService, features: string[]): string[] {
      const tenant = signal<CurrentTenant | null>({
        id: 'org-1', orgCode: 'ORG1', orgName: 'Org One', plan: 'ENTERPRISE', enabledFeatureCodes: features,
        isSuperAdmin: false, roleName: 'Accountant', permissions: auth.getPermissions()
      });
      const tenantService = { tenant, hasFeature: (code: string) => features.includes(code), isSuperAdmin: () => false };
      const menu = new AppMenu(auth, tenantService as any);

      const out: string[] = [];
      const walk = (items: any[]) => items.forEach(i => { if (i.label) out.push(i.label); if (i.items) walk(i.items); });
      walk(menu.model());
      return out;
    }

    it('shows both entries to a finance viewer with only INVOICE_VIEW', () => {
      const shown = labels(authWith(['INVOICE_VIEW']), ['MODULE_FINANCE']);
      expect(shown).toContain('Tax Codes');
      expect(shown).toContain('Exchange Rates');
    });

    it('shows both entries to whoever maintains them', () => {
      const shown = labels(authWith(['FINANCE_SETUP_MANAGE']), ['MODULE_FINANCE']);
      expect(shown).toContain('Tax Codes');
      expect(shown).toContain('Exchange Rates');
    });

    it('still needs the Finance module switched on', () => {
      const shown = labels(authWith(['INVOICE_VIEW', 'FINANCE_SETUP_MANAGE']), ['MODULE_DEMAND']);
      expect(shown).not.toContain('Tax Codes');
      expect(shown).not.toContain('Exchange Rates');
    });

    it('hides them from a user with neither permission', () => {
      const shown = labels(authWith(['SALES_INVOICE_VIEW']), ['MODULE_FINANCE']);
      expect(shown).not.toContain('Tax Codes');
      expect(shown).not.toContain('Exchange Rates');
    });
  });

  // ── The pages, for a viewer ───────────────────────────────────────────────

  describe('pages for a finance viewer (INVOICE_VIEW only)', () => {
    let service: jasmine.SpyObj<FinanceSetupService>;

    async function render<T>(component: new (...args: any[]) => T) {
      service = jasmine.createSpyObj<FinanceSetupService>('FinanceSetupService', [
        'getTaxCodes', 'createTaxCode', 'updateTaxCode', 'createTaxCodesFromRatesInUse',
        'getExchangeRates', 'createExchangeRate', 'updateExchangeRate', 'deleteExchangeRate', 'quoteExchangeRate'
      ]);
      service.getTaxCodes.and.returnValue(of({ success: true, message: '', result: [{
        uuid: 't1', code: 'GST17', name: 'GST 17%', description: null, ratePercent: 17, usage: 'SALES', isDefault: true, isActive: true
      }] }));
      service.getExchangeRates.and.returnValue(of({ success: true, message: '', result: [{
        uuid: 'r1', fromCurrencyCode: 'USD', toCurrencyCode: 'PKR', rate: 278.5, effectiveDate: '2026-10-01', source: 'MANUAL',
        notes: null, createdDate: '2026-10-01T08:00:00Z'
      }] }));
      const currencies = jasmine.createSpyObj<CurrenciesService>('CurrenciesService', ['getAll']);
      currencies.getAll.and.returnValue(of({ success: true, message: '', result: [] }));

      await TestBed.resetTestingModule().configureTestingModule({
        imports: [component],
        providers: [
          provideNoopAnimations(),
          { provide: AuthService, useValue: authWith(['INVOICE_VIEW']) },
          { provide: FinanceSetupService, useValue: service },
          { provide: CurrenciesService, useValue: currencies }
        ]
      }).compileComponents();

      const fixture = TestBed.createComponent(component);
      fixture.detectChanges();
      return fixture;
    }

    it('Tax Codes: the codes load, every write is hidden and does nothing', async () => {
      const fixture = await render(TaxCodesComponent);
      const page = fixture.componentInstance;
      const el: HTMLElement = fixture.nativeElement;

      expect(page.canManage).toBeFalse();
      expect(el.querySelector('[data-testid="read-only"]')).not.toBeNull();
      expect(el.querySelector('[data-testid="code-GST17"]')).not.toBeNull();
      for (const id of ['new-code', 'from-rates', 'edit-GST17', 'deactivate-GST17']) {
        expect(el.querySelector(`[data-testid="${id}"]`)).withContext(id).toBeNull();
      }

      page.openCreate();
      page.save();
      page.createFromRates();
      page.reactivate({ ...page.codes[0], isActive: false });
      expect(page.dialogVisible).toBeFalse();
      expect(service.createTaxCode).not.toHaveBeenCalled();
      expect(service.updateTaxCode).not.toHaveBeenCalled();
      expect(service.createTaxCodesFromRatesInUse).not.toHaveBeenCalled();
      fixture.destroy();
    });

    it('Exchange Rates: the rates load and can be checked, every write is hidden and does nothing', async () => {
      const fixture = await render(ExchangeRatesComponent);
      const page = fixture.componentInstance;
      const el: HTMLElement = fixture.nativeElement;

      expect(page.canManage).toBeFalse();
      expect(el.querySelector('[data-testid="read-only"]')).not.toBeNull();
      expect(el.querySelector('[data-testid="rate-r1"]')).not.toBeNull();
      expect(el.querySelector('[data-testid="quote-check"]')).not.toBeNull();
      for (const id of ['new-rate', 'edit-r1', 'delete-r1']) {
        expect(el.querySelector(`[data-testid="${id}"]`)).withContext(id).toBeNull();
      }

      page.openCreate();
      page.delete(page.rates[0]);
      page.save();
      expect(page.dialogVisible).toBeFalse();
      expect(service.deleteExchangeRate).not.toHaveBeenCalled();
      expect(service.createExchangeRate).not.toHaveBeenCalled();
      expect(service.updateExchangeRate).not.toHaveBeenCalled();
      fixture.destroy();
    });
  });
});
