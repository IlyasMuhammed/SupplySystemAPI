import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { SupplierPricelistService, PricelistItem, PricelistUpsert, sortPricelist, validatePricelist, PricelistFormValue } from './supplier-pricelist.service';

const item = (over: Partial<PricelistItem> = {}): PricelistItem => ({
  uuid: 'u-1', productUuid: 'p-1', productName: 'Widget', supplierUuid: 's-1', supplierName: 'Acme', minQuantity: 1, unitPrice: 10,
  currencyId: 'c-1', currencyCode: 'USD', pricingDateBasis: 0, validFrom: '2026-01-01', validTo: '2026-12-31', priority: 0, isActive: true,
  rowVersion: 'AAAA', ...over
});

describe('SupplierPricelistService (A45 C4)', () => {
  let svc: SupplierPricelistService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    svc = TestBed.inject(SupplierPricelistService);
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  it('lists with the contract query params and unwraps a paged result', () => {
    let page: any;
    svc.list({ productUuid: 'p-1', supplierUuid: 's-1', activeOnly: true, search: ' acme ', page: 2, pageSize: 50 }).subscribe(p => page = p);
    const req = http.expectOne(r => r.url.endsWith('/purchase/supplier-pricelists'));
    expect(req.request.method).toBe('GET');
    expect(req.request.params.get('productUuid')).toBe('p-1');
    expect(req.request.params.get('supplierUuid')).toBe('s-1');
    expect(req.request.params.get('activeOnly')).toBe('true');
    expect(req.request.params.get('search')).toBe('acme');
    expect(req.request.params.get('page')).toBe('2');
    expect(req.request.params.get('pageSize')).toBe('50');
    expect(req.request.params.has('variantUuid')).toBeFalse();
    req.flush({ success: true, message: '', result: { data: [item()], totalRecords: 31, page: 2, pageSize: 50 } });
    expect(page.items.length).toBe(1);
    expect(page.total).toBe(31);
  });

  it('reads a bare array and the "data" envelope key too', () => {
    let page: any;
    svc.list().subscribe(p => page = p);
    http.expectOne(r => r.url.endsWith('/purchase/supplier-pricelists')).flush({ success: true, data: [item(), item({ uuid: 'u-2' })] });
    expect(page.items.map((i: PricelistItem) => i.uuid)).toEqual(['u-1', 'u-2']);
    expect(page.total).toBe(2);
  });

  it('creates and returns item + overlapWarning', () => {
    let res: any;
    const body: PricelistUpsert = { productUuid: 'p-1', supplierUuid: 's-1', minQuantity: 1, unitPrice: 5, currencyId: 'c-1', pricingDateBasis: 0,
      validFrom: '2026-01-01', validTo: '2026-02-01', priority: 0, isActive: true };
    svc.create(body).subscribe(r => res = r);
    const req = http.expectOne(r => r.url.endsWith('/purchase/supplier-pricelists'));
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual(body);
    req.flush({ success: true, result: { item: item(), overlapWarning: 'Overlaps priority 0, 1 Jan - 31 Dec 2026' } });
    expect(res.overlapWarning).toContain('Overlaps');
    expect(res.item.uuid).toBe('u-1');
  });

  it('updates by uuid with the rowVersion in the body', () => {
    svc.update('u-1', { ...({} as PricelistUpsert), rowVersion: 'AAAA' }).subscribe();
    const req = http.expectOne(r => r.url.endsWith('/purchase/supplier-pricelists/u-1'));
    expect(req.request.method).toBe('PUT');
    expect(req.request.body.rowVersion).toBe('AAAA');
    req.flush({ success: true, result: { item: item() } });
  });

  it('deactivates with DELETE', () => {
    let done = false;
    svc.deactivate('u-1').subscribe(() => done = true);
    const req = http.expectOne(r => r.url.endsWith('/purchase/supplier-pricelists/u-1'));
    expect(req.request.method).toBe('DELETE');
    req.flush({ success: true });
    expect(done).toBeTrue();
  });

  it('propagates a 409', () => {
    let status = 0;
    svc.update('u-1', {} as PricelistUpsert).subscribe({ error: e => status = e.status });
    http.expectOne(r => r.url.endsWith('/u-1')).flush({ success: false, message: 'stale' }, { status: 409, statusText: 'Conflict' });
    expect(status).toBe(409);
  });
});

describe('sortPricelist (A45-0160)', () => {
  it('orders by priority ascending, then valid-from descending', () => {
    const rows = [
      item({ uuid: 'a', priority: 1, validFrom: '2026-01-01' }),
      item({ uuid: 'b', priority: 0, validFrom: '2026-01-01' }),
      item({ uuid: 'c', priority: 0, validFrom: '2026-06-01' })
    ];
    expect(sortPricelist(rows).map(r => r.uuid)).toEqual(['c', 'b', 'a']);
  });
});

describe('validatePricelist (A45-0171..0173)', () => {
  const ok = (): PricelistFormValue => ({
    productUuid: 'p', variantUuid: null, supplierUuid: 's', vendorProductCode: null, vendorProductName: null, minQuantity: 1, unitPrice: 5,
    currencyId: 'c', pricingDateBasis: 0, validFrom: new Date(2026, 0, 1), validTo: new Date(2026, 11, 31), leadTimeDays: null, priority: 0, notes: null, isActive: true
  });

  it('accepts a complete row', () => expect(validatePricelist(ok())).toEqual({}));
  it('rejects a zero or negative price (TS-23)', () => {
    expect(validatePricelist({ ...ok(), unitPrice: 0 }).unitPrice).toBeTruthy();
    expect(validatePricelist({ ...ok(), unitPrice: -1 }).unitPrice).toBeTruthy();
    expect(validatePricelist({ ...ok(), unitPrice: null }).unitPrice).toBeTruthy();
  });
  it('rejects valid-to before valid-from (TS-22) but allows the same day', () => {
    expect(validatePricelist({ ...ok(), validTo: new Date(2025, 11, 31) }).validTo).toBeTruthy();
    expect(validatePricelist({ ...ok(), validTo: new Date(2026, 0, 1) }).validTo).toBeUndefined();
  });
  it('rejects a negative minimum quantity and requires product, vendor, currency, dates', () => {
    expect(validatePricelist({ ...ok(), minQuantity: -1 }).minQuantity).toBeTruthy();
    const e = validatePricelist({ ...ok(), productUuid: null, supplierUuid: null, currencyId: null, validFrom: null, validTo: null });
    expect(Object.keys(e).sort()).toEqual(['currencyId', 'productUuid', 'supplierUuid', 'validFrom', 'validTo']);
  });
});
