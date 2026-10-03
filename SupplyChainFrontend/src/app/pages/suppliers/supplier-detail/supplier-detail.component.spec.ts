import { SupplierDetailComponent } from './supplier-detail.component';
import { AuthService } from '../../service/auth.service';

/**
 * The Outstanding Invoices tab: what it lists is the server's (approved invoices with something still owed);
 * its Record Payment / Pay buttons lead to the payment page, which needs PAYMENT_PROCESS.
 */
describe('SupplierDetailComponent — payables', () => {
  function page(held: string[]) {
    const auth = { hasPermission: (c: string) => held.includes(c) } as unknown as AuthService;
    return Object.assign(Object.create(SupplierDetailComponent.prototype), { authService: auth }) as SupplierDetailComponent & Record<string, any>;
  }

  it('offers Record Payment / Pay only to a PAYMENT_PROCESS holder', () => {
    expect(page(['PAYMENT_PROCESS'])['canRecordPayment']).withContext('payer').toBeTrue();
    expect(page(['INVOICE_VIEW', 'PAYMENT_VIEW', 'SUPPLIER_VIEW'])['canRecordPayment']).withContext('viewer').toBeFalse();
  });

  it('explains an empty list: only approved invoices are payable', () => {
    expect(page([])['noPayablesHint']).toContain('Only approved invoices can be paid');
  });
});
