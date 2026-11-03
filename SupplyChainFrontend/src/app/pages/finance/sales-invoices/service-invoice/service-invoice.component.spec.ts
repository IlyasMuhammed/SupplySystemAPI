import { TestBed } from '@angular/core/testing';
import { Router } from '@angular/router';

import { API, mountHrm } from '../../../hrm/core/hrm.testing';
import { ServiceInvoiceComponent } from './service-invoice.component';

describe('ServiceInvoiceComponent (A49 E-13)', () => {
  it('reads customers only; checks lines; posts to the service route; opens the invoice', async () => {
    const m = await mountHrm(ServiceInvoiceComponent, { perms: ['SALES_INVOICE_MANAGE'] });
    const nav = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    const list = m.http.expectOne(r => r.url === `${API}partners`);
    expect(list.request.params.get('isCustomer')).toBe('true');
    list.flush({ success: true, result: { data: [{ uuid: 'c1', companyName: 'Acme' }] } });
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
});
