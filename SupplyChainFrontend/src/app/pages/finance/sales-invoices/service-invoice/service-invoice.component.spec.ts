import { TestBed, fakeAsync, tick } from '@angular/core/testing';
import { Router } from '@angular/router';

import { API, mountHrm } from '../../../hrm/core/hrm.testing';
import { ServiceInvoiceComponent } from './service-invoice.component';

describe('ServiceInvoiceComponent (A49 E-13)', () => {
  it('reads the customer master; checks lines; posts to the service route; opens the invoice', async () => {
    const m = await mountHrm(ServiceInvoiceComponent, { perms: ['SALES_INVOICE_MANAGE', 'CUSTOMER_VIEW'] });
    const nav = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    // REV49B-10: api/customers (MODULE_CUSTOMERS), not partners (MODULE_SUPPLIERS); the server caps a page at 100.
    m.http.expectNone(r => r.url === `${API}partners`);
    const list = m.http.expectOne(r => r.url === `${API}customers`);
    expect(list.request.params.get('pageSize')).toBe('100');
    list.flush({ success: true, result: { data: [{ uuid: 'c1', code: 'C-1', name: 'Acme', isActive: true }, { uuid: 'c9', code: 'C-9', name: 'Old', isActive: false }] } });
    expect(m.c.customers).toEqual([{ label: 'Acme (C-1)', value: 'c1' }]);
    m.c.save();
    expect(m.c.error).toBe('Customer is required.');
    m.c.customerUuid = 'c1';
    m.c.save();
    expect(m.c.error).toBe('Lines row 1: Description is required.');
    Object.assign(m.c.values['lines'][0], { description: 'Consulting', quantity: 2, unitPrice: 500 });
    expect(m.c.total).toBe('1,000.00');
    m.c.save();
    const req = m.expectReq('sales-invoices/service', 'POST');
    expect(req.request.body).toEqual(jasmine.objectContaining({ customerUuid: 'c1' }));
    expect(req.request.body.lines[0]).toEqual(jasmine.objectContaining({ description: 'Consulting', quantity: 2, unitPrice: 500 }));
    req.flush({ success: true, result: { uuid: 'i1' } });
    expect(nav).toHaveBeenCalledWith(['/portal/pages/finance/sales-invoices', 'i1']);
  });

  it('beyond the first page, typing searches the server and keeps the chosen customer', fakeAsync(() => {
    let m: any;
    mountHrm(ServiceInvoiceComponent, { perms: ['SALES_INVOICE_MANAGE', 'CUSTOMER_VIEW'] }).then(x => m = x);
    tick();
    m.http.expectOne((r: any) => r.url === `${API}customers`).flush({ success: true, result: { data: [{ uuid: 'c1', code: 'C-1', name: 'Acme', isActive: true }] } });
    m.c.customerUuid = 'c1';
    m.c.onCustomerFilter('z');
    tick(400);
    m.http.expectNone(`${API}customers/search`);
    m.c.onCustomerFilter('zeta');
    tick(400);
    const s = m.http.expectOne((r: any) => r.url === `${API}customers/search`);
    expect(s.request.params.get('q')).toBe('zeta');
    s.flush({ success: true, result: [{ uuid: 'c7', code: 'C-7', name: 'Zeta', isActive: true }] });
    expect(m.c.customers.map((o: any) => o.value)).toEqual(['c1', 'c7']);
  }));
});
