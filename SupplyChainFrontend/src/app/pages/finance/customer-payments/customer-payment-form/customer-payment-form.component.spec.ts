import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError, Subject } from 'rxjs';

import { CustomerPaymentFormComponent } from './customer-payment-form.component';
import { BusinessPartnerService, BusinessPartnerModel } from '../../../../services/business-partner.service';
import { SalesInvoiceService, SalesInvoiceListItemModel } from '../../../../services/sales-invoice.service';
import { CustomerPaymentService } from '../../../../services/customer-payment.service';
import { CurrenciesService } from '../../../../services/currencies.service';
import { AuthService } from '../../../service/auth.service';

const CUSTOMER: BusinessPartnerModel = { uuid: 'cust-1', companyName: 'Acme Ltd' } as BusinessPartnerModel;
const OTHER: BusinessPartnerModel = { uuid: 'cust-2', companyName: 'Globex Corp' } as BusinessPartnerModel;

function open(uuid: string, balanceDue: number, invoiceDate: string, currencyCode = 'PKR'): SalesInvoiceListItemModel {
  return {
    uuid, invoiceNumber: `INV-${uuid}`, saleOrderUuid: 's', saleOrderNumber: 'SO-1', partnerId: 'cust-1', partnerName: 'Acme Ltd',
    invoiceDate, dueDate: '2026-10-01T00:00:00Z', grandTotal: balanceDue, amountPaid: 0, balanceDue, status: 'ISSUED', currencyCode
  };
}

const OLD = open('old', 100, '2026-08-01T00:00:00Z');
const MID = open('mid', 50, '2026-08-15T00:00:00Z');
const USD = open('usd', 30, '2026-08-20T00:00:00Z', 'USD');

function ok<T>(result: T) {
  return of({ success: true, message: '', result } as any);
}

describe('CustomerPaymentFormComponent', () => {
  let fixture: ComponentFixture<CustomerPaymentFormComponent>;
  let component: CustomerPaymentFormComponent;
  let partners: jasmine.SpyObj<BusinessPartnerService>;
  let invoices: jasmine.SpyObj<SalesInvoiceService>;
  let payments: jasmine.SpyObj<CustomerPaymentService>;
  let currencies: jasmine.SpyObj<CurrenciesService>;
  let navigate: jasmine.Spy;
  let toasts: jasmine.Spy;
  let permissions: string[];

  const auth = { hasPermission: (code: string) => permissions.includes(code) } as unknown as AuthService;

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  function lastToast() {
    return toasts.calls.mostRecent().args[0] as { severity: string; summary: string; detail: string };
  }

  async function setup(queryParams: Record<string, string> = {}, openInvoices: SalesInvoiceListItemModel[] = [OLD, MID]) {
    partners = jasmine.createSpyObj<BusinessPartnerService>('BusinessPartnerService', ['getPartners', 'getPartnerById']);
    partners.getPartners.and.returnValue(ok({ data: [CUSTOMER, OTHER], totalRecords: 2, page: 1, pageSize: 20, totalPages: 1 }));
    partners.getPartnerById.and.returnValue(ok(CUSTOMER));

    invoices = jasmine.createSpyObj<SalesInvoiceService>('SalesInvoiceService', ['getOpenInvoices']);
    invoices.getOpenInvoices.and.returnValue(of({ invoices: openInvoices, truncated: false }));

    payments = jasmine.createSpyObj<CustomerPaymentService>('CustomerPaymentService', ['recordPayment']);
    payments.recordPayment.and.returnValue(ok({
      paymentUuid: 'pay-new', paymentNumber: 'CPAY-20260921-0001', amount: 120, allocatedAmount: 120, unallocatedAmount: 0,
      currencyCode: 'PKR', allocations: [{ invoiceUuid: 'old', invoiceNumber: 'INV-old', amount: 100, balanceDue: 0, invoiceStatus: 'PAID' },
                                          { invoiceUuid: 'mid', invoiceNumber: 'INV-mid', amount: 20, balanceDue: 30, invoiceStatus: 'PARTIALLY_PAID' }],
      partnerBalance: 50
    }));

    currencies = jasmine.createSpyObj<CurrenciesService>('CurrenciesService', ['getAll']);
    currencies.getAll.and.returnValue(ok([
      { id: '1', name: 'Pakistani Rupee', code: 'PKR', symbol: 'Rs' },
      { id: '2', name: 'US Dollar', code: 'USD', symbol: '$' },
      { id: '3', name: 'Legacy money', code: null, symbol: null }
    ]));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [CustomerPaymentFormComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), MessageService,
        { provide: BusinessPartnerService, useValue: partners },
        { provide: SalesInvoiceService, useValue: invoices },
        { provide: CustomerPaymentService, useValue: payments },
        { provide: CurrenciesService, useValue: currencies },
        { provide: AuthService, useValue: auth },
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(queryParams) } } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(CustomerPaymentFormComponent);
    component = fixture.componentInstance;
    navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    toasts = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
  }

  function pickCustomer(partner: BusinessPartnerModel = CUSTOMER) {
    component.form.get('customer')!.setValue(partner);
    component.onCustomerSelected(partner);
  }

  /** A payment that would go through: customer, amount and currency set. */
  function fill(amount = 120) {
    pickCustomer();
    component.form.patchValue({ amount, currencyCode: 'PKR' });
  }

  beforeEach(() => { permissions = ['CUSTOMER_PAYMENT_RECORD', 'CUSTOMER_PAYMENT_VIEW']; });

  // ── Starting state ─────────────────────────────────────────────────────────

  it('starts as a bank transfer received today, oldest invoices first', async () => {
    await setup();
    fixture.detectChanges();

    expect(component.method).toBe('BANK_TRANSFER');
    expect(component.mode).toBe('FIFO');
    const today = new Date();
    const date = component.form.get('paymentDate')!.value as Date;
    expect([date.getFullYear(), date.getMonth(), date.getDate()]).toEqual([today.getFullYear(), today.getMonth(), today.getDate()]);
    expect(query('choose-customer')).not.toBeNull();
    expect(partners.getPartnerById).not.toHaveBeenCalled();
  });

  it('offers the five methods the server accepts', async () => {
    await setup();

    expect(component.methodOptions.map(o => o.value)).toEqual(['CASH', 'CHEQUE', 'BANK_TRANSFER', 'CARD', 'ONLINE']);
  });

  it('offers only currencies that have a code', async () => {
    await setup();
    fixture.detectChanges();

    expect(component.currencyOptions).toEqual([
      { label: 'PKR — Pakistani Rupee', value: 'PKR' },
      { label: 'USD — US Dollar', value: 'USD' }
    ]);
  });

  it('says so when the currencies cannot be loaded', async () => {
    await setup();
    currencies.getAll.and.returnValue(throwError(() => ({ status: 500 })));
    fixture.detectChanges();

    expect(lastToast().severity).toBe('error');
    expect(lastToast().detail).toContain('currency list');
  });

  it('searches active customers only, by what was typed', async () => {
    await setup();
    fixture.detectChanges();

    component.searchCustomers({ query: 'acm' } as any);

    expect(partners.getPartners).toHaveBeenCalledOnceWith({ isCustomer: true, active: true, search: 'acm', pageSize: 20 });
    expect(component.customerSuggestions.length).toBe(2);
    partners.getPartners.and.returnValue(throwError(() => ({ status: 500 })));
    component.searchCustomers({ query: 'x' } as any);
    expect(component.customerSuggestions).toEqual([]);
  });

  // ── Customer and invoices ──────────────────────────────────────────────────

  it('loads the customers unpaid invoices when one is picked', async () => {
    await setup();
    fixture.detectChanges();

    pickCustomer();

    expect(invoices.getOpenInvoices).toHaveBeenCalledOnceWith('cust-1');
    expect(component.openInvoices.map(i => i.uuid)).toEqual(['old', 'mid']);
    expect(component.isLoadingInvoices).toBeFalse();
  });

  it('takes the currency the customer owes in when it is the only one, and leaves it to the user when there are several', async () => {
    await setup();
    fixture.detectChanges();
    pickCustomer();
    expect(component.currencyCode).toBe('PKR');

    await setup({}, [OLD, USD]);
    fixture.detectChanges();
    pickCustomer();
    expect(component.currencyCode).toBe('');
  });

  it('does not overwrite a currency the user already chose', async () => {
    await setup();
    fixture.detectChanges();
    component.form.patchValue({ currencyCode: 'USD' });

    pickCustomer();

    expect(component.currencyCode).toBe('USD');
  });

  it('sets nothing when the customer owes nothing', async () => {
    await setup({}, []);
    fixture.detectChanges();

    pickCustomer();

    expect(component.currencyCode).toBe('');
    expect(component.openInvoices).toEqual([]);
  });

  it('pays only invoices in the currency the money arrived in, and counts the rest', async () => {
    await setup({}, [OLD, USD, MID]);
    fixture.detectChanges();
    pickCustomer();
    component.form.patchValue({ currencyCode: 'PKR' });

    expect(component.matching.map(i => i.uuid)).toEqual(['old', 'mid']);
    expect(component.otherCurrencyCount).toBe(1);
    fixture.detectChanges();
    expect(query('other-currency')!.textContent).toContain('1 of their unpaid invoice is in another currency');
  });

  it('says nothing about other currencies until a currency is chosen', async () => {
    await setup({}, [OLD, USD]);
    fixture.detectChanges();
    pickCustomer();

    expect(component.otherCurrencyCount).toBe(0);
  });

  it('warns when there are more invoices than could be listed', async () => {
    await setup();
    invoices.getOpenInvoices.and.returnValue(of({ invoices: [OLD], truncated: true }));
    fixture.detectChanges();

    pickCustomer();
    fixture.detectChanges();

    expect(component.truncated).toBeTrue();
    expect(query('truncated')).not.toBeNull();
  });

  it('forgets the invoices and what was chosen when the customer is cleared', async () => {
    await setup();
    fixture.detectChanges();
    pickCustomer();
    component.amounts = { old: 10 };

    component.onCustomerCleared();

    expect(component.openInvoices).toEqual([]);
    expect(component.amounts).toEqual({});
  });

  it('forgets what was chosen when another customer is picked', async () => {
    await setup();
    fixture.detectChanges();
    pickCustomer();
    component.amounts = { old: 10 };

    pickCustomer(OTHER);

    expect(component.amounts).toEqual({});
    expect(invoices.getOpenInvoices).toHaveBeenCalledWith('cust-2');
  });

  it('says so when the invoices cannot be loaded, and carries on with none', async () => {
    await setup();
    invoices.getOpenInvoices.and.returnValue(throwError(() => ({ error: { message: 'No access.' } })));
    fixture.detectChanges();

    pickCustomer();

    expect(lastToast().detail).toBe('No access.');
    expect(component.openInvoices).toEqual([]);
    expect(component.isLoadingInvoices).toBeFalse();
  });

  it('forgets what was chosen when the currency changes, since it is another set of invoices', async () => {
    await setup();
    fixture.detectChanges();
    component.amounts = { old: 10 };

    component.onCurrencyChange();

    expect(component.amounts).toEqual({});
  });

  // ── Method ─────────────────────────────────────────────────────────────────

  it('asks for a cheque number only for a cheque, and clears it when the method changes', async () => {
    await setup();
    fixture.detectChanges();
    expect(query('cheque-field')).toBeNull();
    expect(component.form.get('chequeNumber')!.valid).toBeTrue();

    component.form.get('method')!.setValue('CHEQUE');
    component.onMethodChange();
    fixture.detectChanges();
    expect(query('cheque-field')).not.toBeNull();
    expect(component.form.get('chequeNumber')!.valid).toBeFalse();

    component.form.get('chequeNumber')!.setValue('004512');
    component.form.get('method')!.setValue('CASH');
    component.onMethodChange();
    expect(component.form.get('chequeNumber')!.value).toBe('');
    expect(component.form.get('chequeNumber')!.valid).toBeTrue();
  });

  // ── Validation ─────────────────────────────────────────────────────────────

  it('refuses without a customer, and does not call the server', async () => {
    await setup();
    fixture.detectChanges();
    component.form.patchValue({ amount: 10, currencyCode: 'PKR' });

    component.save();

    expect(lastToast().severity).toBe('warn');
    expect(lastToast().detail).toBe('Choose the customer from the list.');
    expect(payments.recordPayment).not.toHaveBeenCalled();
  });

  it('does not take customer text that was typed rather than picked', async () => {
    await setup();
    fixture.detectChanges();
    component.form.patchValue({ customer: 'Acme', amount: 10, currencyCode: 'PKR' });

    expect(component.customer).toBeNull();
    component.save();
    expect(lastToast().detail).toBe('Choose the customer from the list.');
  });

  it('refuses without an amount, or with one that is not above zero', async () => {
    await setup();
    fixture.detectChanges();
    pickCustomer();
    component.form.patchValue({ currencyCode: 'PKR' });

    component.save();
    expect(lastToast().detail).toBe('Enter the amount received, above zero.');

    component.form.patchValue({ amount: 0 });
    component.save();
    expect(lastToast().detail).toBe('Enter the amount received, above zero.');
    expect(payments.recordPayment).not.toHaveBeenCalled();
  });

  it('refuses without a currency', async () => {
    await setup({}, [OLD, USD]);
    fixture.detectChanges();
    pickCustomer();
    component.form.patchValue({ amount: 10 });

    component.save();

    expect(lastToast().detail).toBe('Choose the currency the money arrived in.');
    expect(payments.recordPayment).not.toHaveBeenCalled();
  });

  it('refuses a cheque with no number', async () => {
    await setup();
    fixture.detectChanges();
    fill();
    component.form.get('method')!.setValue('CHEQUE');
    component.onMethodChange();

    component.save();

    expect(lastToast().detail).toBe('A cheque needs its cheque number.');
    expect(payments.recordPayment).not.toHaveBeenCalled();
  });

  it('refuses a date that is missing', async () => {
    await setup();
    fixture.detectChanges();
    fill();
    component.form.patchValue({ paymentDate: null });

    component.save();

    expect(lastToast().detail).toBe('Enter the date the money was received.');
  });

  // ── The request ────────────────────────────────────────────────────────────

  it('leaves the allocation to the server when the oldest invoices are to be paid first', async () => {
    await setup();
    fixture.detectChanges();
    fill(120);
    component.form.patchValue({ paymentDate: new Date(2026, 8, 20), bankReference: '  TRX-99  ', notes: '  Part payment  ' });

    const request = component.buildRequest();

    expect(request).toEqual({
      partnerId: 'cust-1', amount: 120, method: 'BANK_TRANSFER', currencyCode: 'PKR', paymentDate: '2026-09-20',
      chequeNumber: undefined, bankReference: 'TRX-99', notes: 'Part payment', allocations: undefined
    });
  });

  it('sends the cheque number for a cheque, trimmed, and for no other method', async () => {
    await setup();
    fixture.detectChanges();
    fill();
    component.form.get('method')!.setValue('CHEQUE');
    component.onMethodChange();
    component.form.patchValue({ chequeNumber: '  004512 ' });

    expect(component.buildRequest().chequeNumber).toBe('004512');
    expect(component.buildRequest().method).toBe('CHEQUE');

    component.form.get('method')!.setValue('CARD');
    component.onMethodChange();
    expect(component.buildRequest().chequeNumber).toBeUndefined();
  });

  it('rounds the amount to the cent', async () => {
    await setup();
    fixture.detectChanges();
    fill();
    component.form.patchValue({ amount: 0.1 + 0.2 });

    expect(component.buildRequest().amount).toBe(0.3);
  });

  it('sends the invoices chosen, and only those given something, when the user chooses', async () => {
    await setup();
    fixture.detectChanges();
    fill(120);
    component.setMode('MANUAL');
    component.amounts = { old: 100, mid: 0 };

    expect(component.buildRequest().allocations).toEqual([{ invoiceUuid: 'old', amount: 100 }]);
  });

  it('sends an empty list, so the whole amount stays on account, when the user chooses none', async () => {
    await setup();
    fixture.detectChanges();
    fill(120);
    component.setMode('MANUAL');

    expect(component.buildRequest().allocations).toEqual([]);
  });

  it('sends nothing for invoices in another currency, whatever was chosen', async () => {
    await setup({}, [OLD, USD]);
    fixture.detectChanges();
    fill(120);
    component.setMode('MANUAL');
    component.amounts = { old: 50, usd: 30 };

    expect(component.buildRequest().allocations).toEqual([{ invoiceUuid: 'old', amount: 50 }]);
  });

  it('refuses allocations that come to more than the payment, or more than an invoice owes', async () => {
    await setup();
    fixture.detectChanges();
    fill(120);
    component.setMode('MANUAL');

    component.amounts = { old: 100, mid: 50 };
    component.save();
    expect(lastToast().detail).toBe('The allocations come to 150.00, more than the 120.00 available.');

    component.amounts = { mid: 60 };
    component.save();
    expect(lastToast().detail).toBe('INV-mid: 60.00 is more than the 50.00 still owing.');
    expect(payments.recordPayment).not.toHaveBeenCalled();
  });

  it('has no allocation to check when the server decides', async () => {
    await setup();
    fixture.detectChanges();
    fill(1000);

    expect(component.firstProblem()).toBeNull();
  });

  // ── The oldest-first preview ───────────────────────────────────────────────

  it('shows what paying the oldest invoices first would do', async () => {
    await setup();
    fixture.detectChanges();
    fill(120);
    fixture.detectChanges();

    expect(component.fifoRows.map(r => [r.invoice.uuid, r.amount])).toEqual([['old', 100], ['mid', 20]]);
    expect(component.fifoApplied).toBe(120);
    expect(fixture.nativeElement.querySelectorAll('[data-testid="fifo-row"]').length).toBe(2);
    expect(query('fifo-remainder')).toBeNull();
  });

  it('says what will be held on account when the payment is more than is owed', async () => {
    await setup();
    fixture.detectChanges();
    fill(200);
    fixture.detectChanges();

    expect(component.fifoApplied).toBe(150);
    expect(query('fifo-remainder')!.textContent).toContain('50.00 PKR is more than they owe');
  });

  it('says it will all be held on account when there is no invoice to pay', async () => {
    await setup({}, []);
    fixture.detectChanges();
    pickCustomer();
    component.form.patchValue({ amount: 100, currencyCode: 'PKR' });
    fixture.detectChanges();

    expect(query('fifo-none')).not.toBeNull();
    expect(component.fifoRows).toEqual([]);
  });

  it('asks for the amount before it can show anything', async () => {
    await setup();
    fixture.detectChanges();
    pickCustomer();
    fixture.detectChanges();

    expect(query('fifo-preview')!.textContent).toContain('Enter the amount');
  });

  it('shows the allocation editor, and no preview, when the user chooses the invoices', async () => {
    await setup();
    fixture.detectChanges();
    fill(120);
    component.setMode('MANUAL');
    fixture.detectChanges();

    expect(query('allocation-editor')).not.toBeNull();
    expect(query('fifo-preview')).toBeNull();
  });

  it('asks for a currency before showing which invoices it can pay', async () => {
    await setup({}, [OLD, USD]);
    fixture.detectChanges();
    pickCustomer();
    fixture.detectChanges();

    expect(query('choose-currency')).not.toBeNull();
    expect(query('fifo-preview')).toBeNull();
  });

  // ── Saving ─────────────────────────────────────────────────────────────────

  it('records the payment and opens it, saying where the money went', async () => {
    await setup();
    fixture.detectChanges();
    fill(120);

    component.save();

    expect(payments.recordPayment).toHaveBeenCalledTimes(1);
    expect(navigate).toHaveBeenCalledWith(['/portal/pages/finance/customer-payments', 'pay-new']);
    expect(component.isSaving).toBeFalse();
    expect(lastToast().severity).toBe('success');
    expect(lastToast().detail).toBe('CPAY-20260921-0001: 120.00 PKR applied to 2 invoices');
  });

  it('says how much is held on account when some of it is', async () => {
    await setup();
    fixture.detectChanges();
    fill(200);
    payments.recordPayment.and.returnValue(ok({
      paymentUuid: 'pay-new', paymentNumber: 'CPAY-1', amount: 200, allocatedAmount: 150, unallocatedAmount: 50, currencyCode: 'PKR',
      allocations: [{ invoiceUuid: 'old', invoiceNumber: 'INV-old', amount: 150, balanceDue: 0, invoiceStatus: 'PAID' }], partnerBalance: -50
    }));

    component.save();

    expect(lastToast().detail).toBe("CPAY-1: 150.00 PKR applied to 1 invoice, 50.00 PKR held on the customer's account");
  });

  it('says none was applied when the payment went entirely on account', async () => {
    await setup();
    fixture.detectChanges();
    fill(100);
    payments.recordPayment.and.returnValue(ok({
      paymentUuid: 'pay-new', paymentNumber: 'CPAY-2', amount: 100, allocatedAmount: 0, unallocatedAmount: 100, currencyCode: 'PKR',
      allocations: [], partnerBalance: -100
    }));

    component.save();

    expect(lastToast().detail).toBe("CPAY-2: not applied to any invoice, 100.00 PKR held on the customer's account");
  });

  it('stays on the form, ready for the next payment, for someone who may record but not view', async () => {
    permissions = ['CUSTOMER_PAYMENT_RECORD'];
    await setup();
    fixture.detectChanges();
    fill(120);
    component.setMode('MANUAL');

    component.save();

    expect(navigate).not.toHaveBeenCalled();
    expect(component.customer).toBeNull();
    expect(component.openInvoices).toEqual([]);
    expect(component.mode).toBe('FIFO');
    expect(component.method).toBe('BANK_TRANSFER');
    expect(component.form.get('amount')!.value).toBeNull();
  });

  it('shows the servers reason when the payment is refused, and can be tried again', async () => {
    await setup();
    fixture.detectChanges();
    fill(120);
    payments.recordPayment.and.returnValue(throwError(() => ({
      error: { message: 'A cheque payment needs the cheque number, so a returned cheque can be matched to its receipt.' }
    })));

    component.save();

    expect(lastToast().severity).toBe('error');
    expect(lastToast().detail).toContain('cheque number');
    expect(component.isSaving).toBeFalse();
    expect(navigate).not.toHaveBeenCalled();

    payments.recordPayment.and.returnValue(ok({
      paymentUuid: 'p', paymentNumber: 'CPAY-3', amount: 120, allocatedAmount: 0, unallocatedAmount: 120, currencyCode: 'PKR', allocations: [], partnerBalance: 0
    }));
    component.save();
    expect(navigate).toHaveBeenCalledTimes(1);
  });

  it('records once however often the button is pressed while it is working', async () => {
    await setup();
    fixture.detectChanges();
    fill(120);
    payments.recordPayment.and.returnValue(new Subject<any>());

    component.save();
    component.save();

    expect(payments.recordPayment).toHaveBeenCalledTimes(1);
  });

  // ── Arriving from an invoice ───────────────────────────────────────────────

  it('starts from an invoice: the customer, its currency, its balance as the amount, and that invoice chosen', async () => {
    await setup({ partnerId: 'cust-1', invoiceUuid: 'mid' });
    fixture.detectChanges();

    expect(partners.getPartnerById).toHaveBeenCalledOnceWith('cust-1');
    expect(component.customer!.companyName).toBe('Acme Ltd');
    expect(invoices.getOpenInvoices).toHaveBeenCalledOnceWith('cust-1');
    expect(component.currencyCode).toBe('PKR');
    expect(component.amount).toBe(50);
    expect(component.mode).toBe('MANUAL');
    expect(component.amounts).toEqual({ mid: 50 });
    expect(component.firstProblem()).toBeNull();
  });

  it('takes the invoices own currency even when the customer owes in several', async () => {
    await setup({ partnerId: 'cust-1', invoiceUuid: 'usd' }, [OLD, USD]);
    fixture.detectChanges();

    expect(component.currencyCode).toBe('USD');
    expect(component.amount).toBe(30);
    expect(component.amounts).toEqual({ usd: 30 });
  });

  it('starts from a customer alone: their invoices load, and the rest is left to the user', async () => {
    await setup({ partnerId: 'cust-1' });
    fixture.detectChanges();

    expect(component.customer!.uuid).toBe('cust-1');
    expect(component.mode).toBe('FIFO');
    expect(component.amount).toBe(0);
    expect(component.amounts).toEqual({});
  });

  it('says so when the invoice it was sent for has nothing left owing', async () => {
    await setup({ partnerId: 'cust-1', invoiceUuid: 'paid-long-ago' });
    fixture.detectChanges();

    expect(lastToast().severity).toBe('info');
    expect(lastToast().detail).toContain('nothing left owing');
    expect(component.mode).toBe('FIFO');
  });

  it('says so when the customer it was sent for cannot be loaded', async () => {
    await setup({ partnerId: 'cust-1' });
    partners.getPartnerById.and.returnValue(throwError(() => ({ status: 404 })));
    fixture.detectChanges();

    expect(lastToast().severity).toBe('error');
    expect(component.customer).toBeNull();
  });
});
