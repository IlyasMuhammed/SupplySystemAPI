import { of } from 'rxjs';

import { VendorStatementComponent, reconciliationProblem } from './vendor-statement.component';
import { mount, statement } from '../ar-ap.testing';

describe('VendorStatementComponent (A48 C13)', () => {
  const setup = (perms: string[]) => mount(VendorStatementComponent, { perms, params: { uuid: 'v1' }, prepare: api => {
    api.getVendorBalance.and.returnValue(of({ partnerId: 'v1', partnerName: 'Supplier A', partnerRole: 'Vendor', asOfDate: '2026-10-25', totalOpen: 4200,
      totalCredits: 200, netBalance: 4000, totalOverdue: 0, openItemCount: 3, baseCurrencyCode: 'PKR', byCurrency: [] }));
    api.getVendorStatement.and.returnValue(of(statement({ partnerType: 'Vendor', partnerId: 'v1', partnerName: 'Supplier A', type: 'OpenItems',
      sections: [{ currencyCode: 'PKR', openingBalance: 0, closingBalance: 4000, totalDebits: 0, totalCredits: 0, lines: [] }] })));
    api.reconcileVendor.and.returnValue(of({ vendorId: 'v1', vendorName: 'Supplier A', asOfDate: '2026-10-25', currencyCode: 'PKR', smsBalance: 4000,
      vendorClaimedBalance: 4500, difference: -500, matchedCount: 2, isReconciled: false,
      differences: [{ type: 'InVendorNotSMS', reference: 'V-77', vendorAmount: 500 }] }));
  } });

  it('balance and open-items statement for the vendor', async () => {
    const m = await setup(['AP_VIEW']);
    expect(m.text('net')).toBe('4,000.00');
    expect(m.api.getVendorStatement).toHaveBeenCalledWith('v1', jasmine.objectContaining({ type: 'OpenItems' }));
    expect(m.text('closing-PKR')).toBe('4,000.00');
    expect(m.q('reconcile')).toBeNull();
  });

  it('reconcile with the vendor\'s claimed balance and documents (AP_EXECUTE)', async () => {
    const m = await setup(['AP_VIEW', 'AP_EXECUTE']);
    m.c.reconcile();
    expect(m.c.recError).toBe('Enter the balance the vendor claims.');
    m.c.claimed = 4500;
    m.c.addItem();
    m.c.reconcile();
    expect(m.c.recError).toBe('Line 1: enter the vendor\'s reference.');
    m.c.items[0].reference = ' V-77 ';
    m.c.items[0].amount = 500;
    m.c.reconcile();
    m.fixture.detectChanges();
    expect(m.api.reconcileVendor).toHaveBeenCalledWith('v1', jasmine.objectContaining({ vendorClaimedBalance: 4500, vendorItems: [jasmine.objectContaining({ reference: 'V-77' })] }));
    expect(m.text('rec-result')).toContain('difference -500.00');
    expect(m.text('differences')).toContain('On their statement, not in SMS');
    expect(reconciliationProblem('', 1, [])).toContain('date');
  });
});
