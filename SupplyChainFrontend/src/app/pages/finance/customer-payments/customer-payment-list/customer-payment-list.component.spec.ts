import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { CustomerPaymentListComponent } from './customer-payment-list.component';
import { CustomerPaymentService, CustomerPaymentListItemModel } from '../../../../services/customer-payment.service';
import { AuthService } from '../../../service/auth.service';
import { PAYMENT_STATUS_SEVERITY } from '../../receivables/receivables.shared';

function payment(overrides: Partial<CustomerPaymentListItemModel> = {}): CustomerPaymentListItemModel {
  return {
    uuid: 'pay-1', paymentNumber: 'CPAY-20260920-0001', partnerId: 'p-1', partnerName: 'Acme Ltd',
    paymentDate: '2026-09-20T00:00:00Z', amount: 500, allocatedAmount: 400, unallocatedAmount: 100,
    paymentMethod: 'CHEQUE', chequeNumber: '004512', bankReference: null, currencyCode: 'PKR', status: 'RECEIVED',
    ...overrides
  };
}

function ok<T>(result: T) {
  return of({ success: true, message: '', result } as any);
}

describe('CustomerPaymentListComponent', () => {
  let fixture: ComponentFixture<CustomerPaymentListComponent>;
  let component: CustomerPaymentListComponent;
  let payments: jasmine.SpyObj<CustomerPaymentService>;
  let toasts: jasmine.Spy;
  let permissions: string[];

  const auth = { hasPermission: (code: string) => permissions.includes(code) } as unknown as AuthService;

  async function setup(list: CustomerPaymentListItemModel[] = [payment(), payment({ uuid: 'pay-2', paymentNumber: 'CPAY-20260920-0002', partnerName: 'Globex Corp', paymentMethod: 'CASH', chequeNumber: null, bankReference: 'TRX-9', unallocatedAmount: 0, allocatedAmount: 500 })]) {
    payments = jasmine.createSpyObj<CustomerPaymentService>('CustomerPaymentService', ['getPayments']);
    payments.getPayments.and.returnValue(ok({ data: list, totalRecords: list.length, page: 1, pageSize: 20, totalPages: 1 }));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [CustomerPaymentListComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), MessageService,
        { provide: CustomerPaymentService, useValue: payments },
        { provide: AuthService, useValue: auth }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(CustomerPaymentListComponent);
    component = fixture.componentInstance;
    toasts = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
  }

  beforeEach(() => { permissions = ['CUSTOMER_PAYMENT_VIEW']; });

  it('loads the first page through the table and renders the payments', async () => {
    await setup();
    fixture.detectChanges();

    expect(payments.getPayments).toHaveBeenCalledOnceWith(jasmine.objectContaining({ page: 1, pageSize: 20 }));
    fixture.detectChanges();
    const text = fixture.nativeElement.textContent;
    expect(text).toContain('CPAY-20260920-0001');
    expect(text).toContain('Globex Corp');
    expect(text).toContain('004512');
    expect(text).toContain('TRX-9');
    expect(component.isLoading).toBeFalse();
  });

  it('shows the cheque number, else the bank reference, else a dash', async () => {
    await setup();

    expect(component.reference(payment())).toBe('004512');
    expect(component.reference(payment({ chequeNumber: null, bankReference: 'TRX-9' }))).toBe('TRX-9');
    expect(component.reference(payment({ chequeNumber: null, bankReference: null }))).toBe('—');
  });

  it('shows how much of each payment is still on account', async () => {
    await setup();
    fixture.detectChanges();
    fixture.detectChanges();

    const onAccount = Array.from(fixture.nativeElement.querySelectorAll('[data-testid="on-account"]')).map((e: any) => e.textContent.trim());
    expect(onAccount).toEqual(['100.00', '0.00']);
  });

  it('sends only the filters that are set', async () => {
    await setup();
    component.selectedMethod = 'CASH';
    component.load();

    const filter = payments.getPayments.calls.mostRecent().args[0]!;
    expect(filter.method).toBe('CASH');
    expect(filter.status).toBeUndefined();
    expect(filter.search).toBeUndefined();
    expect(filter.unallocated).toBeUndefined();
    expect(filter.dateFrom).toBeUndefined();
  });

  it('asks only for money on account when told, and has no opinion when not', async () => {
    await setup();

    component.onAccountOnly = true;
    component.load();
    expect(payments.getPayments.calls.mostRecent().args[0]!.unallocated).toBeTrue();

    component.onAccountOnly = false;
    component.load();
    expect(payments.getPayments.calls.mostRecent().args[0]!.unallocated).withContext('false would mean fully applied').toBeUndefined();
  });

  it('sends the date range as calendar days', async () => {
    await setup();
    component.dateFrom = new Date(2026, 8, 1);
    component.dateTo = new Date(2026, 8, 30, 23, 59);
    component.load();

    const filter = payments.getPayments.calls.mostRecent().args[0]!;
    expect([filter.dateFrom, filter.dateTo]).toEqual(['2026-09-01', '2026-09-30']);
  });

  it('reloads from the first page when a filter changes', async () => {
    await setup();
    component.currentPage = 5;
    component.selectedStatus = 'BOUNCED';

    component.onFilterChange();

    expect(payments.getPayments).toHaveBeenCalledWith(jasmine.objectContaining({ page: 1, status: 'BOUNCED' }));
  });

  it('offers every status and method the server knows', async () => {
    await setup();

    expect(component.statusOptions.map(o => o.value)).toEqual(['', 'RECEIVED', 'BOUNCED', 'REVERSED']);
    expect(component.methodOptions.map(o => o.value)).toEqual(['', 'CASH', 'CHEQUE', 'BANK_TRANSFER', 'CARD', 'ONLINE']);
  });

  it('clears every filter and starts again', async () => {
    await setup();
    component.searchText = 'acme';
    component.selectedStatus = 'RECEIVED';
    component.selectedMethod = 'CASH';
    component.onAccountOnly = true;
    component.dateFrom = new Date(2026, 8, 1);
    component.currentPage = 3;

    component.resetFilters();

    expect([component.searchText, component.selectedStatus, component.selectedMethod, component.onAccountOnly, component.currentPage])
      .toEqual(['', '', '', false, 1]);
    expect(component.dateFrom).toBeNull();
  });

  it('follows the table to another page', async () => {
    await setup();

    component.onPageChange({ first: 20, rows: 20 });

    expect(payments.getPayments).toHaveBeenCalledWith(jasmine.objectContaining({ page: 2, pageSize: 20 }));
  });

  it('empties the list and shows the servers reason when the request fails', async () => {
    await setup();
    payments.getPayments.and.returnValue(throwError(() => ({ error: { message: "'X' is not a payment status." } })));

    component.load();

    expect(component.payments).toEqual([]);
    expect(component.isLoading).toBeFalse();
    expect((toasts.calls.mostRecent().args[0] as any).detail).toBe("'X' is not a payment status.");
  });

  it('maps every status the server can return to a severity', () => {
    expect(PAYMENT_STATUS_SEVERITY['RECEIVED']).toBe('success');
    expect(PAYMENT_STATUS_SEVERITY['BOUNCED']).toBe('danger');
    expect(PAYMENT_STATUS_SEVERITY['REVERSED']).toBe('danger');
  });

  it('offers to record a payment only to someone who may', async () => {
    await setup();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="record-payment"]')).toBeNull();

    permissions = ['CUSTOMER_PAYMENT_VIEW', 'CUSTOMER_PAYMENT_RECORD'];
    await setup();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="record-payment"]')).not.toBeNull();
  });
});
