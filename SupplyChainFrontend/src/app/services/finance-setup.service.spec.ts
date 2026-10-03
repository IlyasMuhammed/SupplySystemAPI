import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { FinanceSetupService, SaveExchangeRateRequest, SaveTaxCodeRequest } from './finance-setup.service';
import { environment } from '../../environments/environment';

const BASE = `${environment.apiUrl}/finance`;

describe('FinanceSetupService', () => {
  let service: FinanceSetupService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(FinanceSetupService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  // ── Tax codes ──────────────────────────────────────────────────────────────

  it('lists every active tax code with no parameters', () => {
    service.getTaxCodes().subscribe(res => expect(res.result.length).toBe(1));

    const req = http.expectOne(r => r.url === `${BASE}/tax-codes`);
    expect(req.request.method).toBe('GET');
    expect(req.request.params.keys()).toEqual([]);
    req.flush({ success: true, message: '', result: [{ uuid: 'u', code: 'GST17' }] });
  });

  it('narrows to a side and asks for inactive codes only when told to', () => {
    service.getTaxCodes('SALES', true).subscribe();

    const req = http.expectOne(r => r.url === `${BASE}/tax-codes`);
    expect(req.request.params.get('side')).toBe('SALES');
    expect(req.request.params.get('includeInactive')).toBe('true');
    req.flush({ success: true, message: '', result: [] });
  });

  it('creates with POST and changes with PUT to the code’s address, sending the whole code', () => {
    const body: SaveTaxCodeRequest = {
      code: 'GST17', name: 'GST 17%', description: null, ratePercent: 17, usage: 'SALES', isDefault: true, isActive: true
    };

    service.createTaxCode(body).subscribe();
    const create = http.expectOne(`${BASE}/tax-codes`);
    expect(create.request.method).toBe('POST');
    expect(create.request.body).toEqual(body);
    create.flush({ success: true, message: 'created', result: { uuid: 'u1' } });

    service.updateTaxCode('u1', { ...body, isActive: false }).subscribe();
    const update = http.expectOne(`${BASE}/tax-codes/u1`);
    expect(update.request.method).toBe('PUT');
    expect(update.request.body.isActive).toBeFalse();
    update.flush({ success: true, message: 'updated', result: { uuid: 'u1' } });
  });

  it('asks for codes from the rates in use with an empty POST', () => {
    service.createTaxCodesFromRatesInUse().subscribe(res => {
      expect(res.result.created.length).toBe(1);
      expect(res.result.skippedRates).toEqual([0]);
    });

    const req = http.expectOne(`${BASE}/tax-codes/from-rates-in-use`);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({});
    req.flush({ success: true, message: 'Created 1', result: { created: [{ uuid: 'x', code: 'TAX17' }], skippedRates: [0] } });
  });

  // ── Exchange rates ─────────────────────────────────────────────────────────

  it('lists rates, with from and to only when given', () => {
    service.getExchangeRates().subscribe();
    const all = http.expectOne(r => r.url === `${BASE}/exchange-rates`);
    expect(all.request.params.keys()).toEqual([]);
    all.flush({ success: true, message: '', result: [] });

    service.getExchangeRates('USD', undefined).subscribe();
    const from = http.expectOne(r => r.url === `${BASE}/exchange-rates`);
    expect(from.request.params.get('from')).toBe('USD');
    expect(from.request.params.has('to')).toBeFalse();
    from.flush({ success: true, message: '', result: [] });

    service.getExchangeRates('USD', 'PKR').subscribe();
    const pair = http.expectOne(r => r.url === `${BASE}/exchange-rates`);
    expect(pair.request.params.get('to')).toBe('PKR');
    pair.flush({ success: true, message: '', result: [] });
  });

  it('creates, changes and deletes a rate at the documented addresses', () => {
    const body: SaveExchangeRateRequest = { fromCurrencyCode: 'USD', toCurrencyCode: 'PKR', rate: 278.5, effectiveDate: '2026-10-01', notes: null };

    service.createExchangeRate(body).subscribe();
    const create = http.expectOne(`${BASE}/exchange-rates`);
    expect(create.request.method).toBe('POST');
    expect(create.request.body).toEqual(body);
    create.flush({ success: true, message: '', result: {} });

    service.updateExchangeRate('r1', body).subscribe();
    const update = http.expectOne(`${BASE}/exchange-rates/r1`);
    expect(update.request.method).toBe('PUT');
    update.flush({ success: true, message: '', result: {} });

    service.deleteExchangeRate('r1').subscribe();
    const del = http.expectOne(`${BASE}/exchange-rates/r1`);
    expect(del.request.method).toBe('DELETE');
    del.flush({ success: true, message: '', result: null });
  });

  it('quotes a pair on a date and passes a null result through', () => {
    service.quoteExchangeRate('PKR', 'USD', '2026-10-15').subscribe(res => expect(res.result).toBeNull());

    const req = http.expectOne(r => r.url === `${BASE}/exchange-rates/quote`);
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('from')).toBe('PKR');
    expect(req.request.params.get('to')).toBe('USD');
    expect(req.request.params.get('date')).toBe('2026-10-15');
    req.flush({ success: true, message: 'No PKR → USD rate', result: null });
  });
});
