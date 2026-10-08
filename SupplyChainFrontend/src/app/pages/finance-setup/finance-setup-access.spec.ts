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
import { OrgCurrencyService } from '../../services/org-currency.service';
import { MoneyService } from '../../services/money.service';
import { TaxCodesComponent } from './tax-codes/tax-codes.component';
import { ExchangeRatesComponent } from './exchange-rates/exchange-rates.component';
import { OrgCurrenciesComponent } from './org-currencies/org-currencies.component';
import { CurrencyConfigurationComponent } from './currency-configuration/currency-configuration.component';

/**
 * Who can open Settings → Finance Setup: Tax Codes, Exchange Rates, Currencies, Currency Configuration. Each page's own
 * MANAGE code maintains it (FINANCE_SETUP_MANAGE for tax codes; A35: CURRENCY_RATE_MANAGE, CURRENCY_MANAGE,
 * ORG_CURRENCY_SETTINGS_MANAGE), FINANCE_SETUP_MANAGE opens every one, and INVOICE_VIEW opens them read-only for finance
 * viewers. The guard, the menu and the pages all go through the real AuthService here, fed a token carrying just the
 * permissions under test, so the any-of check is the one the app really runs.
 */

const PAGES: { path: string; label: string; component: unknown; manage: string }[] = [
  { path: 'finance-setup/tax-codes', label: 'Tax Codes', component: TaxCodesComponent, manage: 'FINANCE_SETUP_MANAGE' },
  { path: 'finance-setup/currencies', label: 'Currencies', component: OrgCurrenciesComponent, manage: 'CURRENCY_MANAGE' },
  { path: 'finance-setup/exchange-rates', label: 'Exchange Rates', component: ExchangeRatesComponent, manage: 'CURRENCY_RATE_MANAGE' },
  { path: 'finance-setup/currency-configuration', label: 'Currency Configuration', component: CurrencyConfigurationComponent, manage: 'ORG_CURRENCY_SETTINGS_MANAGE' }
];

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

    it('routes the four pages', () => {
      for (const p of PAGES) expect(find(p.path)?.component).withContext(p.path).toBe(p.component as any);
    });

    it('lets a finance viewer with only INVOICE_VIEW in', () => {
      for (const p of PAGES) {
        const { allowed, navigate } = runGuard(p.path, authWith(['INVOICE_VIEW']));
        expect(allowed).withContext(p.path).toBeTrue();
        expect(navigate).not.toHaveBeenCalled();
      }
    });

    it('lets whoever maintains finance setup in, and each page\'s own MANAGE code', () => {
      for (const p of PAGES) {
        expect(runGuard(p.path, authWith(['FINANCE_SETUP_MANAGE'])).allowed).withContext(p.path).toBeTrue();
        expect(runGuard(p.path, authWith([p.manage])).allowed).withContext(`${p.path} ${p.manage}`).toBeTrue();
      }
    });

    it('keeps out a user with none of them — the every-role *_VIEW codes and sales-invoice viewing are not enough', () => {
      for (const p of PAGES) {
        const { allowed, navigate } = runGuard(p.path, authWith(['SALES_INVOICE_VIEW', 'PAYMENT_VIEW', 'CURRENCY_VIEW', 'CURRENCY_RATE_VIEW']));
        expect(allowed).withContext(p.path).toBeFalse();
        expect(navigate).toHaveBeenCalledWith(['/portal/access-denied']);
      }
    });

    it('sends someone not signed in to the login page', () => {
      const { allowed, navigate } = runGuard(PAGES[0].path, authWith([], false));
      expect(allowed).toBeFalse();
      expect(navigate).toHaveBeenCalledWith(['/auth/login']);
    });
  });

  // ── Menu ──────────────────────────────────────────────────────────────────

  describe('menu', () => {
    function financeSetup(auth: AuthService, features: string[]): string[] {
      const tenant = signal<CurrentTenant | null>({
        id: 'org-1', orgCode: 'ORG1', orgName: 'Org One', plan: 'ENTERPRISE', enabledFeatureCodes: features,
        isSuperAdmin: false, roleName: 'Accountant', permissions: auth.getPermissions()
      });
      const tenantService = { tenant, hasFeature: (code: string) => features.includes(code), isSuperAdmin: () => false };
      const menu = new AppMenu(auth, tenantService as any);

      const out: string[] = [];
      const walk = (items: any[], inside: boolean) => items.forEach(i => {
        const here = inside || i.label === 'Finance Setup';
        if (here && i.routerLink) out.push(i.label);
        if (i.items) walk(i.items, here);
      });
      walk(menu.model(), false);
      return out;
    }

    it('shows all four to a finance viewer with only INVOICE_VIEW, and to whoever maintains finance setup', () => {
      const all = PAGES.map(p => p.label);
      expect(financeSetup(authWith(['INVOICE_VIEW']), ['MODULE_FINANCE'])).toEqual(all);
      expect(financeSetup(authWith(['FINANCE_SETUP_MANAGE']), ['MODULE_FINANCE'])).toEqual(all);
    });

    it('shows a page to its own MANAGE code', () => {
      for (const p of PAGES.slice(1)) {
        expect(financeSetup(authWith([p.manage]), ['MODULE_FINANCE'])).withContext(p.manage).toEqual([p.label]);
      }
    });

    it('still needs the Finance module switched on', () => {
      expect(financeSetup(authWith(['INVOICE_VIEW', 'FINANCE_SETUP_MANAGE']), ['MODULE_DEMAND'])).toEqual([]);
    });

    it('hides them from a user with none of the codes (the every-role view codes included)', () => {
      expect(financeSetup(authWith(['SALES_INVOICE_VIEW', 'CURRENCY_VIEW', 'CURRENCY_RATE_VIEW']), ['MODULE_FINANCE'])).toEqual([]);
    });
  });

  // ── The pages, for a viewer ───────────────────────────────────────────────

  describe('pages for a finance viewer (INVOICE_VIEW only)', () => {
    let service: jasmine.SpyObj<FinanceSetupService>;
    let currencyService: jasmine.SpyObj<OrgCurrencyService>;

    async function render<T>(component: new (...args: any[]) => T) {
      service = jasmine.createSpyObj<FinanceSetupService>('FinanceSetupService', [
        'getTaxCodes', 'createTaxCode', 'updateTaxCode', 'createTaxCodesFromRatesInUse'
      ]);
      service.getTaxCodes.and.returnValue(of({ success: true, message: '', result: [{
        uuid: 't1', code: 'GST17', name: 'GST 17%', description: null, ratePercent: 17, usage: 'SALES', isDefault: true, isActive: true
      }] }));
      currencyService = jasmine.createSpyObj<OrgCurrencyService>('OrgCurrencyService', [
        'getCurrencies', 'createCurrency', 'updateCurrency', 'getRates', 'getRateHistory', 'getRateOn', 'createRate', 'updateRate',
        'getSettings', 'saveSettings'
      ]);
      currencyService.getCurrencies.and.returnValue(of({ success: true, message: '', result: [{
        currencyId: 'usd', code: 'USD', name: 'US Dollar', symbol: '$', decimalPlaces: 2, rounding: 0.01, symbolPosition: 'before',
        isActive: true, displayOrder: 1
      }] }));
      currencyService.getRates.and.returnValue(of({ success: true, message: '', result: [{
        id: 'r1', currencyId: 'usd', currencyCode: 'USD', currencyName: 'US Dollar', rate: 278.5, inverseRate: 0.0035906643,
        effectiveFrom: '2026-10-01', effectiveTo: '9999-12-31', isCurrent: true, source: 'MANUAL', notes: null,
        rateCurrencyId: 'pkr', rateCurrencyCode: 'PKR'
      }] }));
      currencyService.getSettings.and.returnValue(of({ success: true, message: '', result: {
        saleBaseCurrencyId: 'usd', saleBaseCurrencyCode: 'USD', purchaseBaseCurrencyId: 'usd', purchaseBaseCurrencyCode: 'USD',
        serviceBaseCurrencyId: 'usd', serviceBaseCurrencyCode: 'USD', rateCurrencyId: 'usd', rateCurrencyCode: 'USD',
        exchangeGainAccountCode: null, exchangeLossAccountCode: null, unrealizedGainAccountCode: null, unrealizedLossAccountCode: null,
        isStored: true, locks: null
      } }));
      const catalog = jasmine.createSpyObj<CurrenciesService>('CurrenciesService', ['getAll']);
      catalog.getAll.and.returnValue(of({ success: true, message: '', result: [] }));

      await TestBed.resetTestingModule().configureTestingModule({
        imports: [component],
        providers: [
          provideNoopAnimations(),
          { provide: AuthService, useValue: authWith(['INVOICE_VIEW']) },
          { provide: FinanceSetupService, useValue: service },
          { provide: OrgCurrencyService, useValue: currencyService },
          { provide: CurrenciesService, useValue: catalog },
          { provide: MoneyService, useValue: jasmine.createSpyObj('MoneyService', ['reload']) }
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
      for (const id of ['add-rate', 'edit-r1']) {
        expect(el.querySelector(`[data-testid="${id}"]`)).withContext(id).toBeNull();
      }

      page.openCreate();
      page.openEdit(page.rates[0]);
      page.save();
      expect(page.dialogVisible).toBeFalse();
      expect(currencyService.createRate).not.toHaveBeenCalled();
      expect(currencyService.updateRate).not.toHaveBeenCalled();
      fixture.destroy();
    });

    it('Currencies: listed read-only', async () => {
      const fixture = await render(OrgCurrenciesComponent);
      const page = fixture.componentInstance;
      const el: HTMLElement = fixture.nativeElement;
      expect(page.canManage).toBeFalse();
      expect(el.querySelector('[data-testid="cur-USD"]')).not.toBeNull();
      expect(el.querySelector('[data-testid="add-currency"]')).toBeNull();
      page.toggleActive(page.currencies[0], false);
      expect(currencyService.updateCurrency).not.toHaveBeenCalled();
      fixture.destroy();
    });

    it('Currency Configuration: shown read-only, no Save', async () => {
      const fixture = await render(CurrencyConfigurationComponent);
      const page = fixture.componentInstance;
      const el: HTMLElement = fixture.nativeElement;
      expect(page.canManage).toBeFalse();
      expect(el.querySelector('[data-testid="read-only"]')).not.toBeNull();
      expect(el.querySelector('[data-testid="save"]')).toBeNull();
      page.save();
      expect(currencyService.saveSettings).not.toHaveBeenCalled();
      fixture.destroy();
    });
  });
});
