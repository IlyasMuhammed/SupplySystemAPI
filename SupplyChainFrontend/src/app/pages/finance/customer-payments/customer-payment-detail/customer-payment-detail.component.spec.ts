import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError, Subject } from 'rxjs';

import { CustomerPaymentDetailComponent } from './customer-payment-detail.component';
import { CustomerPaymentService, CustomerPaymentDetailModel } from '../../../../services/customer-payment.service';
import { SalesInvoiceService, SalesInvoiceListItemModel } from '../../../../services/sales-invoice.service';
import { AuthService } from '../../../service/auth.service';

const UUID = '33333333-3333-3333-3333-333333333333';

const ALL_PERMISSIONS = ['CUSTOMER_PAYMENT_VIEW', 'CUSTOMER_PAYMENT_RECORD', 'SALES_INVOICE_VIEW', 'CUSTOMER_LEDGER_VIEW'];

function payment(overrides: Partial<CustomerPaymentDetailModel> = {}): CustomerPaymentDetailModel {
  return {
    uuid: UUID, paymentNumber: 'CPAY-20260920-0001', partnerId: 'p-1', partnerName: 'Acme Ltd',
    paymentDate: '2026-09-20T00:00:00Z', amount: 500, allocatedAmount: 400, unallocatedAmount: 100,
    paymentMethod: 'CHEQUE', chequeNumber: '004512', bankReference: null, currencyCode: 'PKR', status: 'RECEIVED',
    notes: null, createdBy: 1, createdDate: '2026-09-20T09:30:00Z', modifiedBy: null, modifiedDate: null,
    allocations: [{
      allocationUuid: 'a1', invoiceUuid: 'inv-1', invoiceNumber: 'SINV-20260901-0001', allocatedAmount: 400,
      allocatedAt: '2026-09-20T09:30:00Z', allocatedBy: 1, invoiceBalanceDue: 0, invoiceStatus: 'PAID'
    }],
    ...overrides
  };
}

function open(uuid: string, balanceDue: number, invoiceDate: string): SalesInvoiceListItemModel {
  return {
    uuid, invoiceNumber: `INV-${uuid}`, saleOrderUuid: 's', saleOrderNumber: 'SO-1', partnerId: 'p-1', partnerName: 'Acme Ltd',
    invoiceDate, dueDate: '2026-10-01T00:00:00Z', grandTotal: balanceDue, amountPaid: 0, balanceDue, status: 'ISSUED', currencyCode: 'PKR'
  };
}

function ok<T>(result: T) {
  return of({ success: true, message: '', result } as any);
}

describe('CustomerPaymentDetailComponent', () => {
  let fixture: ComponentFixture<CustomerPaymentDetailComponent>;
  let component: CustomerPaymentDetailComponent;
  let payments: jasmine.SpyObj<CustomerPaymentService>;
  let invoices: jasmine.SpyObj<SalesInvoiceService>;
  let toasts: jasmine.Spy;
  let permissions: string[];

  const auth = { hasPermission: (code: string) => permissions.includes(code) } as unknown as AuthService;

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  function lastToast() {
    return toasts.calls.mostRecent().args[0] as { severity: string; summary: string; detail: string };
  }

  async function setup(model: CustomerPaymentDetailModel | null = payment()) {
    payments = jasmine.createSpyObj<CustomerPaymentService>('CustomerPaymentService', ['getPayment', 'allocatePayment']);
    payments.getPayment.and.returnValue(model ? ok(model) : of({ success: false, message: 'no', result: null } as any));
    payments.allocatePayment.and.returnValue(ok({
      paymentUuid: UUID, paymentNumber: 'CPAY-20260920-0001', amount: 500, allocatedAmount: 500, unallocatedAmount: 0,
      allocations: [{ invoiceUuid: 'old', invoiceNumber: 'INV-old', amount: 60, balanceDue: 0, invoiceStatus: 'PAID' },
                    { invoiceUuid: 'mid', invoiceNumber: 'INV-mid', amount: 40, balanceDue: 10, invoiceStatus: 'PARTIALLY_PAID' }]
    }));

    invoices = jasmine.createSpyObj<SalesInvoiceService>('SalesInvoiceService', ['getOpenInvoices']);
    invoices.getOpenInvoices.and.returnValue(of({
      invoices: [open('old', 60, '2026-08-01T00:00:00Z'), open('mid', 50, '2026-08-15T00:00:00Z')], truncated: false
    }));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [CustomerPaymentDetailComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), MessageService,
        { provide: CustomerPaymentService, useValue: payments },
        { provide: SalesInvoiceService, useValue: invoices },
        { provide: AuthService, useValue: auth },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['uuid', UUID]]) } } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(CustomerPaymentDetailComponent);
    component = fixture.componentInstance;
    toasts = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
  }

  beforeEach(() => { permissions = [...ALL_PERMISSIONS]; });

  // ── Showing the payment ────────────────────────────────────────────────────

  it('loads the payment and renders what it is, how much, and what it was applied to', async () => {
    await setup();
    fixture.detectChanges();

    expect(payments.getPayment).toHaveBeenCalledOnceWith(UUID);
    const text = fixture.nativeElement.textContent;
    expect(text).toContain('CPAY-20260920-0001');
    expect(text).toContain('Acme Ltd');
    expect(query('method')!.textContent).toContain('Cheque');
    expect(query('cheque-number')!.textContent).toContain('004512');
    expect(query('amount')!.textContent!.trim()).toBe('500.00');
    expect(query('allocated')!.textContent!.trim()).toBe('400.00');
    expect(query('unallocated')!.textContent!.trim()).toBe('100.00');
    expect(query('allocations-table')!.textContent).toContain('SINV-20260901-0001');
  });

  it('shows a not-found state for a missing payment, and an error toast for a failed request', async () => {
    await setup(null);
    fixture.detectChanges();
    expect(query('not-found')).not.toBeNull();

    await setup();
    payments.getPayment.and.returnValue(throwError(() => ({ status: 500 })));
    fixture.detectChanges();
    expect(component.notFound).toBeFalse();
    expect(lastToast().severity).toBe('error');
  });

  it('treats a 404 as not found', async () => {
    await setup();
    payments.getPayment.and.returnValue(throwError(() => ({ status: 404 })));
    fixture.detectChanges();

    expect(component.notFound).toBeTrue();
  });

  it('shows a bank reference and notes when there are some', async () => {
    await setup(payment({ paymentMethod: 'BANK_TRANSFER', chequeNumber: null, bankReference: 'TRX-99', notes: 'Part payment' }));
    fixture.detectChanges();

    expect(query('bank-reference')!.textContent).toContain('TRX-99');
    expect(query('notes')!.textContent).toContain('Part payment');
    expect(query('cheque-number')).toBeNull();
  });

  it('links to the customers ledger and each invoice only for someone who may open them', async () => {
    await setup();
    fixture.detectChanges();
    expect(query('customer-link')).not.toBeNull();
    expect(query('invoice-link')).not.toBeNull();

    permissions = ['CUSTOMER_PAYMENT_VIEW'];
    await setup();
    fixture.detectChanges();
    expect(query('customer-link')).toBeNull();
    expect(query('invoice-link')).toBeNull();
    expect(query('customer-name')!.textContent).toContain('Acme Ltd');
    expect(query('allocations-table')!.textContent).toContain('SINV-20260901-0001');
  });

  it('says it has not been applied to any invoice when it has not', async () => {
    await setup(payment({ allocations: [], allocatedAmount: 0, unallocatedAmount: 500 }));
    fixture.detectChanges();

    expect(query('allocations-table')!.textContent).toContain('has not been applied to any invoice');
  });

  it('says a bounced payment is history only, and offers nothing to apply', async () => {
    await setup(payment({ status: 'BOUNCED', allocatedAmount: 0, unallocatedAmount: 0 }));
    fixture.detectChanges();

    expect(component.isHistoryOnly).toBeTrue();
    expect(query('history-note')!.textContent).toContain('Bounced');
    expect(query('apply-panel')).toBeNull();
    expect(query('allocations-table')!.textContent).toContain('SINV-20260901-0001');
  });

  // ── Apply what is left ─────────────────────────────────────────────────────

  it('offers to apply what is left when some is held on account, and loads the invoices it could pay in its currency', async () => {
    await setup();
    fixture.detectChanges();

    expect(component.canApply).toBeTrue();
    expect(query('apply-panel')).not.toBeNull();
    expect(invoices.getOpenInvoices).toHaveBeenCalledOnceWith('p-1', 'PKR');
    expect(component.openInvoices.map(i => i.uuid)).toEqual(['old', 'mid']);
  });

  it('offers nothing to apply when the payment is fully applied, or not received, or for someone who may not record', async () => {
    await setup(payment({ unallocatedAmount: 0, allocatedAmount: 500 }));
    fixture.detectChanges();
    expect(component.canApply).withContext('fully applied').toBeFalse();
    expect(query('apply-panel')).toBeNull();
    expect(invoices.getOpenInvoices).not.toHaveBeenCalled();

    await setup(payment({ status: 'REVERSED', unallocatedAmount: 0 }));
    fixture.detectChanges();
    expect(component.canApply).withContext('reversed').toBeFalse();

    permissions = ['CUSTOMER_PAYMENT_VIEW'];
    await setup();
    fixture.detectChanges();
    expect(component.canApply).withContext('may not record').toBeFalse();
    expect(query('apply-panel')).toBeNull();
  });

  it('still offers oldest first, but no list, to someone who may not see invoices', async () => {
    permissions = ['CUSTOMER_PAYMENT_VIEW', 'CUSTOMER_PAYMENT_RECORD'];
    await setup();
    fixture.detectChanges();

    expect(query('apply-panel')).not.toBeNull();
    expect(query('apply-oldest')).not.toBeNull();
    expect(query('allocation-editor')).toBeNull();
    expect(invoices.getOpenInvoices).not.toHaveBeenCalled();
  });

  it('applies what is left oldest first, with no allocations, and reloads', async () => {
    await setup();
    fixture.detectChanges();

    component.applyOldestFirst();

    expect(payments.allocatePayment).toHaveBeenCalledOnceWith(UUID, undefined);
    expect(lastToast().severity).toBe('success');
    expect(lastToast().detail).toBe('100.00 applied to 2 invoices; 0.00 still on account.');
    expect(component.isApplying).toBeFalse();
    expect(payments.getPayment).withContext('reloaded').toHaveBeenCalledTimes(2);
  });

  it('applies the invoices chosen, exactly as chosen', async () => {
    await setup();
    fixture.detectChanges();
    component.amounts = { old: 60, mid: 0, elsewhere: 5 };

    component.applyChosen();

    expect(payments.allocatePayment).toHaveBeenCalledOnceWith(UUID, [{ invoiceUuid: 'old', amount: 60 }]);
  });

  it('will not apply nothing, or more than is left, or more than an invoice owes', async () => {
    await setup();
    fixture.detectChanges();

    component.amounts = {};
    expect(component.canApplyChosen).withContext('nothing chosen').toBeFalse();

    component.amounts = { old: 60, mid: 50 };
    expect(component.problem).toBe('The allocations come to 110.00, more than the 100.00 available.');
    expect(component.canApplyChosen).toBeFalse();

    component.amounts = { mid: 60 };
    expect(component.problem).toBe('INV-mid: 60.00 is more than the 50.00 still owing.');
    expect(component.canApplyChosen).toBeFalse();

    component.applyChosen();
    expect(payments.allocatePayment).not.toHaveBeenCalled();

    component.amounts = { old: 60, mid: 40 };
    expect(component.canApplyChosen).toBeTrue();
  });

  it('shows the servers reason when applying is refused, and leaves everything as it was', async () => {
    await setup();
    fixture.detectChanges();
    payments.allocatePayment.and.returnValue(throwError(() => ({
      error: { message: 'There is no unpaid PKR invoice to apply payment CPAY-20260920-0001 to.' }
    })));

    component.applyOldestFirst();

    expect(lastToast().severity).toBe('error');
    expect(lastToast().detail).toContain('no unpaid PKR invoice');
    expect(component.isApplying).toBeFalse();
    expect(payments.getPayment).toHaveBeenCalledTimes(1);
  });

  it('applies once however often the button is pressed while it is working', async () => {
    await setup();
    fixture.detectChanges();
    payments.allocatePayment.and.returnValue(new Subject<any>());

    component.applyOldestFirst();
    component.applyOldestFirst();

    expect(payments.allocatePayment).toHaveBeenCalledTimes(1);
  });

  it('will not apply for someone who may not record, or a payment with nothing left', async () => {
    permissions = ['CUSTOMER_PAYMENT_VIEW'];
    await setup();
    fixture.detectChanges();
    component.applyOldestFirst();
    expect(payments.allocatePayment).not.toHaveBeenCalled();

    permissions = [...ALL_PERMISSIONS];
    await setup(payment({ unallocatedAmount: 0 }));
    fixture.detectChanges();
    component.applyOldestFirst();
    expect(payments.allocatePayment).not.toHaveBeenCalled();
  });

  it('forgets what was chosen once the payment has been applied and reloaded', async () => {
    await setup();
    fixture.detectChanges();
    component.amounts = { old: 60 };

    component.applyChosen();

    expect(component.amounts).toEqual({});
  });

  it('says so, and still offers oldest first, when the invoices cannot be loaded', async () => {
    await setup();
    invoices.getOpenInvoices.and.returnValue(throwError(() => ({ status: 500 })));
    fixture.detectChanges();

    expect(component.invoicesFailed).toBeTrue();
    expect(component.isLoadingInvoices).toBeFalse();
    expect(query('invoices-failed')).not.toBeNull();
    expect(query('apply-oldest')).not.toBeNull();
    expect(query('allocation-editor')).toBeNull();
  });
});
