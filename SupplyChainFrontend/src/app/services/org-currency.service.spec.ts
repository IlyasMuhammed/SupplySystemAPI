import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { OrgCurrencyService, OPEN_END, isOpenEnd } from './org-currency.service';
import { environment } from '../../environments/environment';

const API = environment.apiUrl;
const CUR = '11111111-1111-1111-1111-111111111111';
const RATE = '22222222-2222-2222-2222-222222222222';

/** A35 — docs/multi-currency/API-CONTRACT.md §1, §2, §4, §5. */
describe('OrgCurrencyService', () => {
  let service: OrgCurrencyService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(OrgCurrencyService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('§1 lists, adds and updates org currencies on api/currencies', () => {
    service.getCurrencies(true).subscribe();
    const list = http.expectOne(r => r.url === `${API}/currencies`);
    expect(list.request.method).toBe('GET');
    expect(list.request.params.get('includeInactive')).toBe('true');
    list.flush({ success: true, message: '', result: [] });

    service.getCurrencies().subscribe();
    expect(http.expectOne(r => r.url === `${API}/currencies`).request.params.get('includeInactive')).toBe('false');

    const body = { code: 'MXN', name: 'Mexican Peso', symbol: '$', decimalPlaces: 2 };
    service.createCurrency(body).subscribe();
    const post = http.expectOne(`${API}/currencies`);
    expect(post.request.method).toBe('POST');
    expect(post.request.body).toEqual(body);
    post.flush({ success: true, message: '', result: {} });

    service.updateCurrency(CUR, { ...body, isActive: false }).subscribe();
    const put = http.expectOne(`${API}/currencies/${CUR}`);
    expect(put.request.method).toBe('PUT');
    expect(put.request.body.isActive).toBeFalse();
    put.flush({ success: true, message: '', result: {} });
  });

  it('§2 reads rates (filtered, active, on a date, history) on api/currency-rates', () => {
    service.getRates({ currencyId: CUR, from: '2026-10-01', to: '2026-10-31' }).subscribe();
    const list = http.expectOne(r => r.url === `${API}/currency-rates`);
    expect(list.request.params.get('currencyId')).toBe(CUR);
    expect(list.request.params.get('from')).toBe('2026-10-01');
    expect(list.request.params.get('to')).toBe('2026-10-31');
    list.flush({ success: true, message: '', result: [] });

    service.getRates({}).subscribe();
    const all = http.expectOne(r => r.url === `${API}/currency-rates`);
    expect(all.request.params.keys()).toEqual([]);
    all.flush({ success: true, message: '', result: [] });

    service.getActiveRates().subscribe();
    http.expectOne(`${API}/currency-rates/active`).flush({ success: true, message: '', result: [] });

    service.getRateOn(CUR, '2026-10-03').subscribe();
    const on = http.expectOne(r => r.url === `${API}/currency-rates/${CUR}`);
    expect(on.request.params.get('date')).toBe('2026-10-03');
    on.flush({ success: true, message: '', result: {} });

    service.getRateHistory(CUR).subscribe();
    http.expectOne(`${API}/currency-rates/history/${CUR}`).flush({ success: true, message: '', result: [] });
  });

  it('§2 inserts and corrects rates', () => {
    const insert = { currencyId: CUR, rate: 278.12, effectiveFrom: '2026-10-08', effectiveTo: null, notes: null };
    service.createRate(insert).subscribe();
    const post = http.expectOne(`${API}/currency-rates`);
    expect(post.request.method).toBe('POST');
    expect(post.request.body).toEqual(insert);
    post.flush({ success: true, message: '', result: { rate: {}, closedPrevious: null } });

    const fix = { rate: 277.9, effectiveFrom: '2026-10-05', effectiveTo: '2026-10-05', notes: 'fix' };
    service.updateRate(RATE, fix).subscribe();
    const put = http.expectOne(`${API}/currency-rates/${RATE}`);
    expect(put.request.method).toBe('PUT');
    expect(put.request.body).toEqual(fix);
    put.flush({ success: true, message: '', result: {} });
  });

  it('§4 reads and saves the org currency settings', () => {
    service.getSettings().subscribe();
    http.expectOne(`${API}/organization/currency-settings`).flush({ success: true, message: '', result: {} });

    const body = {
      saleBaseCurrencyId: CUR, purchaseBaseCurrencyId: CUR, serviceBaseCurrencyId: CUR, rateCurrencyId: null,
      exchangeGainAccountCode: '7110', exchangeLossAccountCode: null, unrealizedGainAccountCode: null, unrealizedLossAccountCode: null
    };
    service.saveSettings(body).subscribe();
    const put = http.expectOne(`${API}/organization/currency-settings`);
    expect(put.request.method).toBe('PUT');
    expect(put.request.body).toEqual(body);
    put.flush({ success: true, message: '', result: {} });
  });

  it('knows the open end of a rate', () => {
    expect(OPEN_END).toBe('9999-12-31');
    expect(isOpenEnd('9999-12-31')).toBeTrue();
    expect(isOpenEnd('9999-12-31T00:00:00')).toBeTrue();
    expect(isOpenEnd('2026-10-07')).toBeFalse();
    expect(isOpenEnd(null)).toBeTrue();
  });
});
