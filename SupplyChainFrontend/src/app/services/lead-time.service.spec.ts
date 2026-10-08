import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import {
  LEAD_TIME_COMPONENT_CODES, LEAD_TIME_DEFAULT_FIELDS, LeadTimeService, SYSTEM_LEAD_TIME_DEFAULTS, leadTimeSourceLabel,
  leadTimeSourceSeverity
} from './lead-time.service';
import { environment } from '../../environments/environment';

const API = environment.apiUrl;
const VARIANT = '22222222-2222-2222-2222-222222222222';

/** A34-PB-07/08 — api/lead-time/* and api/variants/{uuid}/lead-times (API-CONTRACT.md §4.4–§4.6). */
describe('LeadTimeService', () => {
  let service: LeadTimeService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(LeadTimeService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('reads and saves the organization defaults on api/lead-time/defaults', () => {
    service.getDefaults().subscribe(res => expect(res.result.isSaved).toBeFalse());
    const get = http.expectOne(`${API}/lead-time/defaults`);
    expect(get.request.method).toBe('GET');
    get.flush({ success: true, message: '', result: { ...SYSTEM_LEAD_TIME_DEFAULTS, isSaved: false } });

    const body = { pickPackDays: 2, shippingLeadTimeDays: 4, salesBufferDays: 1, manufacturingBufferDays: 1, qualityInspectionDays: 0, internalTransferDays: 0 };
    service.updateDefaults(body).subscribe();
    const put = http.expectOne(`${API}/lead-time/defaults`);
    expect(put.request.method).toBe('PUT');
    expect(put.request.body).toEqual(body);
    put.flush({ success: true, message: '', result: { ...body, isSaved: true } });
  });

  it('reads and replaces a variant\'s lead times on api/variants/{uuid}/lead-times', () => {
    service.getVariantLeadTimes(VARIANT).subscribe();
    const get = http.expectOne(`${API}/variants/${VARIANT}/lead-times`);
    expect(get.request.method).toBe('GET');
    get.flush({ success: true, message: '', result: {} });

    service.updateVariantLeadTimes(VARIANT, { pickPackDays: 2, shippingLeadTimeDays: null }).subscribe();
    const put = http.expectOne(`${API}/variants/${VARIANT}/lead-times`);
    expect(put.request.method).toBe('PUT');
    expect(put.request.body).toEqual({ pickPackDays: 2, shippingLeadTimeDays: null });
    put.flush({ success: true, message: '', result: {} });
  });

  it('calculates with POST api/lead-time/calculate and calculate-manufacturing', () => {
    service.calculate({ variantUuid: VARIANT, quantity: 5, requestedDate: '2026-11-02' }).subscribe();
    const calc = http.expectOne(`${API}/lead-time/calculate`);
    expect(calc.request.method).toBe('POST');
    expect(calc.request.body).toEqual({ variantUuid: VARIANT, quantity: 5, requestedDate: '2026-11-02' });
    calc.flush({ success: true, message: '', result: {} });

    service.calculateManufacturing({ variantUuid: VARIANT, quantity: 3 }).subscribe();
    const mfg = http.expectOne(`${API}/lead-time/calculate-manufacturing`);
    expect(mfg.request.method).toBe('POST');
    expect(mfg.request.body).toEqual({ variantUuid: VARIANT, quantity: 3 });
    mfg.flush({ success: true, message: '', result: {} });
  });
});

describe('lead-time display helpers', () => {
  it('lists the eight components in the contract\'s order', () =>
    expect([...LEAD_TIME_COMPONENT_CODES]).toEqual(
      ['SUPPLIER', 'MANUFACTURING', 'MFG_BUFFER', 'QC', 'TRANSFER', 'PICK_PACK', 'SHIPPING', 'SALES_BUFFER']));

  it('words the source badges as the contract says (§4.5)', () => {
    expect(leadTimeSourceLabel('VARIANT')).toBe('Variant override');
    expect(leadTimeSourceLabel('ORG_DEFAULT')).toBe('Org default');
    expect(leadTimeSourceLabel('SUPPLIER_RATE')).toBe('Supplier');
    expect(leadTimeSourceLabel('SUPPLIER_RECORD')).toBe('Supplier');
    expect(leadTimeSourceLabel('PRODUCT')).toBe('Product');
    expect(leadTimeSourceLabel('BOM')).toBe('BOM');
    expect(leadTimeSourceLabel('IN_STOCK')).toBe('In stock');
    expect(leadTimeSourceLabel('SYSTEM_DEFAULT')).toBe('System default');
    expect(leadTimeSourceLabel('SOMETHING_NEW')).toBe('SOMETHING_NEW');
  });

  it('colours a variant override apart from the inherited values', () => {
    expect(leadTimeSourceSeverity('VARIANT')).toBe('info');
    expect(leadTimeSourceSeverity('IN_STOCK')).toBe('success');
    expect(leadTimeSourceSeverity('ORG_DEFAULT')).toBe('secondary');
  });

  it('has the six org defaults in the §11.5 order with the system values 1/3/1/0/0/0', () => {
    expect(LEAD_TIME_DEFAULT_FIELDS.map(f => f.field)).toEqual(
      ['pickPackDays', 'shippingLeadTimeDays', 'salesBufferDays', 'manufacturingBufferDays', 'qualityInspectionDays', 'internalTransferDays']);
    expect(LEAD_TIME_DEFAULT_FIELDS.map(f => f.label)).toEqual(
      ['Pick & Pack Time', 'Shipping Lead Time', 'Sales Safety Buffer', 'Manufacturing Buffer', 'Quality Inspection Time', 'Internal Transfer Time']);
    expect(LEAD_TIME_DEFAULT_FIELDS.map(f => SYSTEM_LEAD_TIME_DEFAULTS[f.field])).toEqual([1, 3, 1, 0, 0, 0]);
  });
});
