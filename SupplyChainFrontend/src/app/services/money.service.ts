import { Injectable, computed, effect, signal, untracked } from '@angular/core';
import { Observable, of } from 'rxjs';
import { catchError, finalize, map, shareReplay } from 'rxjs/operators';

import { OrgCurrencyModel, OrgCurrencyService } from './org-currency.service';
import { TenantService } from '../pages/service/tenant.service';
import { MoneyCurrency, MoneyFormatOptions, formatMoney, roundMoney } from '../shared/money/money-format';

/** An ISO code as typed (3 letters, any case); anything else given to find/format is taken as a currency id. */
const CODE = /^[A-Za-z]{3}$/;

interface CacheState {
  /** The organization (TenantService.tenant().id, null before it is known) the list was loaded for. */
  org: string | null;
  list: OrgCurrencyModel[];
}

/**
 * A35 D-21 — amounts in the organization's currencies, written with each one's symbol, decimals and symbol position
 * (T-C1-08..10). The org's currency list (api/currencies, inactive ones included — old documents still use them) is
 * loaded once per organization and shared; it is keyed by the current organization (TenantService), so after a logout
 * and a login into another organization the old list is never used (REV-03) and the next ensureLoaded() fetches the new
 * one. `format` never waits: before the list arrives, or for a currency it does not know, a code is shown as
 * "PKR 5.00" and an unknown id as the bare number with two decimals.
 *
 *   money.ensureLoaded();                         // e.g. in ngOnInit (the `money` pipe does it itself)
 *   money.format(1234, currencyIdOrCode)          // '¥1,234'
 *   money.format(6000, id, { display: 'code' })   // 'AED 6,000.00'
 *   {{ amount | money:currencyIdOrCode }}         // same, in a template; {{ amount | money:id:{ display: 'code' } }}
 */
@Injectable({ providedIn: 'root' })
export class MoneyService {
  private readonly state = signal<CacheState | null>(null);
  private readonly currentOrg = computed(() => this.tenants.tenant()?.id ?? null);

  /** The current organization's currencies (empty until loaded, and while another organization's list is cached). */
  readonly currencies = computed<OrgCurrencyModel[]>(() => {
    const s = this.state();
    return s && s.org === this.currentOrg() ? s.list : [];
  });

  private inFlight: Observable<OrgCurrencyModel[]> | null = null;
  private inFlightOrg: string | null = null;

  /** Someone asked for the list (ensureLoaded/load) — from then on, follow the organization. */
  private wanted = false;
  /** The organization whose last load failed (REV-05); null when none did. */
  private failed: { org: string | null } | null = null;

  constructor(private orgCurrencies: OrgCurrencyService, private tenants: TenantService) {
    // REV-04: on a hard refresh the pipes are built before the shell has loaded the tenant. When the organization becomes
    // known (or changes), fetch its list without waiting for a pipe or a page to ask again — so amounts formatted through
    // format() in component code update too once the list arrives (the computed `currencies` signal changes).
    effect(() => {
      const org = this.currentOrg();
      untracked(() => {
        if (this.wanted && org !== null) this.ensureLoaded();
      });
    });
  }

  private get isLoadedForCurrentOrg(): boolean {
    const s = this.state();
    return !!s && s.org === this.currentOrg();
  }

  /** Starts the one load for the current organization if it has not happened (or failed last time). */
  ensureLoaded(): void {
    this.wanted = true;
    if (this.isLoadedForCurrentOrg) return;
    if (this.inFlight && this.inFlightOrg === this.currentOrg()) return;
    // REV-05: a failed load (403, 500, offline) is not retried automatically for the same organization — the impure pipe
    // would otherwise fire a GET on every change detection. reload() or another organization tries again.
    if (this.failed && this.failed.org === this.currentOrg()) return;
    this.load().subscribe();
  }

  /** The current organization's currencies, from the server the first time, then from memory. */
  load(): Observable<OrgCurrencyModel[]> {
    this.wanted = true;
    if (this.isLoadedForCurrentOrg) return of(this.currencies());
    const org = this.currentOrg();
    if (this.inFlight && this.inFlightOrg === org) return this.inFlight;

    const request = this.orgCurrencies.getCurrencies(true).pipe(
      map(res => res?.result ?? []),
      map(list => { this.state.set({ org, list }); this.failed = null; return list; }),
      catchError(() => { this.failed = { org }; return of([] as OrgCurrencyModel[]); }),
      finalize(() => { if (this.inFlight === request) { this.inFlight = null; this.inFlightOrg = null; } }),
      shareReplay(1)
    );
    this.inFlight = request;
    this.inFlightOrg = org;
    return request;
  }

  /** Forget and fetch again (after a currency was added or changed on Settings → Currencies). */
  reload(): void {
    this.failed = null;
    this.state.set(null);
    this.inFlight = null;
    this.inFlightOrg = null;
    this.ensureLoaded();
  }

  /** The org currency by id or ISO code (any case); null when unknown or not loaded yet. */
  find(idOrCode: string | null | undefined): OrgCurrencyModel | null {
    const key = (idOrCode ?? '').trim();
    if (!key) return null;
    const list = this.currencies();
    if (CODE.test(key)) {
      const code = key.toUpperCase();
      return list.find(c => (c.code ?? '').toUpperCase() === code) ?? null;
    }
    const id = key.toLowerCase();
    return list.find(c => (c.currencyId ?? '').toLowerCase() === id) ?? null;
  }

  /** The currency's decimal places (2 when unknown). */
  decimalsOf(idOrCode: string | null | undefined): number {
    return this.find(idOrCode)?.decimalPlaces ?? 2;
  }

  /** The amount rounded at the currency's decimals, half away from zero (D-13). */
  round(amount: number, idOrCode: string | null | undefined): number {
    return roundMoney(amount, this.decimalsOf(idOrCode));
  }

  /** The amount as text in that currency (see the class comment). */
  format(
    amount: number | null | undefined,
    currency: string | MoneyCurrency | null | undefined,
    options: MoneyFormatOptions = {}
  ): string {
    if (currency && typeof currency === 'object') return formatMoney(amount, currency, options);
    const known = this.find(currency);
    if (known) return formatMoney(amount, known, options);
    const key = (currency ?? '').trim();
    return formatMoney(amount, CODE.test(key) ? key.toUpperCase() : null, options);
  }
}
