import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { CustomerListItem, CustomerService, customersCsv } from './customer.service';

/** A37 API-CONTRACT §5 — the customer facade's routes and query strings. */
describe('CustomerService (A37)', () => {
  let service: CustomerService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(CustomerService);
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  it('lists with search, type, status, paging and sort', () => {
    service.getCustomers({ search: 'acme', type: 'COMPANY', status: 'ACTIVE', page: 2, pageSize: 50, sortField: 'balance', sortOrder: 'desc' }).subscribe();
    const req = http.expectOne(r => r.url.endsWith('/customers'));
    expect(req.request.params.get('search')).toBe('acme');
    expect(req.request.params.get('type')).toBe('COMPANY');
    expect(req.request.params.get('status')).toBe('ACTIVE');
    expect(req.request.params.get('page')).toBe('2');
    expect(req.request.params.get('pageSize')).toBe('50');
    expect(req.request.params.get('sortField')).toBe('balance');
    expect(req.request.params.get('sortOrder')).toBe('desc');
    req.flush({ success: true, message: '', result: { data: [], totalRecords: 0, page: 2, pageSize: 50, totalPages: 0 } });
  });

  it('leaves empty filters out', () => {
    service.getCustomers({ type: '', status: '' }).subscribe();
    const req = http.expectOne(r => r.url.endsWith('/customers'));
    expect(req.request.params.keys().sort()).toEqual(['page', 'pageSize']);
    req.flush({ success: true, message: '', result: { data: [] } });
  });

  it('create, update, status, search and balance hit the contract routes', () => {
    service.createCustomer({ name: 'A', customerType: 'COMPANY' }).subscribe();
    expect(http.expectOne(r => r.method === 'POST' && r.url.endsWith('/customers')).request.body.name).toBe('A');
    service.updateCustomer('c-1', { name: 'B', customerType: 'INDIVIDUAL' }).subscribe();
    http.expectOne(r => r.method === 'PUT' && r.url.endsWith('/customers/c-1'));
    service.setStatus('c-1', false).subscribe();
    expect(http.expectOne(r => r.method === 'PATCH' && r.url.endsWith('/customers/c-1/status')).request.body).toEqual({ isActive: false });
    service.search('0300', 5).subscribe();
    const s = http.expectOne(r => r.url.endsWith('/customers/search'));
    expect(s.request.params.get('q')).toBe('0300');
    expect(s.request.params.get('limit')).toBe('5');
    service.getBalance('c-1').subscribe();
    http.expectOne(r => r.url.endsWith('/customers/c-1/balance'));
  });

  it('getAllCustomers walks every page of the filter', () => {
    let rows: CustomerListItem[] = [];
    service.getAllCustomers({ status: 'ACTIVE' }, 2).subscribe(r => rows = r);
    const row = (code: string) => ({ uuid: code, code, name: code, customerType: 'COMPANY', creditLimit: 0, balance: 0, isActive: true, isSystem: false });
    const p1 = http.expectOne(r => r.params.get('page') === '1');
    p1.flush({ success: true, message: '', result: { data: [row('C1'), row('C2')], totalRecords: 3, page: 1, pageSize: 2, totalPages: 2 } });
    const p2 = http.expectOne(r => r.params.get('page') === '2');
    expect(p2.request.params.get('status')).toBe('ACTIVE');
    p2.flush({ success: true, message: '', result: { data: [row('C3')], totalRecords: 3, page: 2, pageSize: 2, totalPages: 2 } });
    expect(rows.map(r => r.code)).toEqual(['C1', 'C2', 'C3']);
  });

  it('customersCsv quotes what needs quoting', () => {
    const csv = customersCsv([{ uuid: 'u', code: 'C-00001', name: 'Acme, "Ltd"', customerType: 'WALK_IN', phone: '0300', email: null,
      creditLimit: 0, balance: 12.5, currencyCode: 'PKR', isActive: true, isSystem: true }]);
    const [header, line] = csv.split('\r\n');
    expect(header).toBe('Code,Name,Type,Phone,Mobile,Email,Credit limit,Balance,Currency,Status');
    expect(line).toBe('C-00001,"Acme, ""Ltd""",Walk-in,0300,,,0,12.5,PKR,Active');
  });
});
