import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute, Router, convertToParamMap, ParamMap } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { BehaviorSubject, Subject, of, throwError } from 'rxjs';

import { CustomerLedgerComponent } from './customer-ledger.component';
import { CustomerLedgerService, CustomerLedgerEntryModel } from '../../../services/customer-ledger.service';
import { BusinessPartnerService, BusinessPartnerModel } from '../../../services/business-partner.service';
import { AuthService } from '../../service/auth.service';
import { LEDGER_ENTRY_SEVERITY } from '../receivables/receivables.shared';

const ACME: BusinessPartnerModel = { uuid: 'cust-1', companyName: 'Acme Ltd' } as BusinessPartnerModel;
const GLOBEX: BusinessPartnerModel = { uuid: 'cust-2', companyName: 'Globex Corp' } as BusinessPartnerModel;

function entry(overrides: Partial<CustomerLedgerEntryModel> = {}): CustomerLedgerEntryModel {
  return {
    uuid: 'e-1', partnerId: 'cust-1', sequenceNo: 2, entryDate: '2026-09-25T12:00:00Z', entryType: 'PAYMENT',
    referenceType: 'CustomerPayment', referenceId: 'pay-1', referenceNumber: 'CPAY-20260925-0001',
    debitAmount: 0, creditAmount: 400, runningBalance: 770, currencyCode: 'PKR', narration: 'Received by bank transfer',
    createdBy: 1, createdDate: '2026-09-25T08:00:00Z',
    ...overrides
  };
}

const INVOICE_ENTRY = entry({
  uuid: 'e-0', sequenceNo: 1, entryDate: '2026-09-20T12:00:00Z', entryType: 'INVOICE', referenceType: 'SalesInvoice',
  referenceId: 'inv-1', referenceNumber: 'SINV-20260920-0001', debitAmount: 1170, creditAmount: 0, runningBalance: 1170, narration: 'Invoice issued'
});

function ok<T>(result: T) {
  return of({ success: true, message: '', result } as any);
}

function page(data: CustomerLedgerEntryModel[], totalRecords = data.length) {
  return ok({ data, totalRecords, page: 1, pageSize: 20, totalPages: 1 });
}

describe('CustomerLedgerComponent', () => {
  let fixture: ComponentFixture<CustomerLedgerComponent>;
  let component: CustomerLedgerComponent;
  let ledger: jasmine.SpyObj<CustomerLedgerService>;
  let partners: jasmine.SpyObj<BusinessPartnerService>;
  let params: BehaviorSubject<ParamMap>;
  let navigate: jasmine.Spy;
  let toasts: jasmine.Spy;
  let permissions: string[];

  const auth = { hasPermission: (code: string) => permissions.includes(code) } as unknown as AuthService;

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  async function setup(partnerId: string | null = 'cust-1', entries: CustomerLedgerEntryModel[] = [entry(), INVOICE_ENTRY]) {
    ledger = jasmine.createSpyObj<CustomerLedgerService>('CustomerLedgerService', ['getLedger']);
    ledger.getLedger.and.returnValue(page(entries));

    partners = jasmine.createSpyObj<BusinessPartnerService>('BusinessPartnerService', ['getPartners', 'getPartnerById']);
    partners.getPartners.and.returnValue(ok({ data: [ACME, GLOBEX], totalRecords: 2, page: 1, pageSize: 20, totalPages: 1 }));
    partners.getPartnerById.and.callFake((id: string) => ok(id === 'cust-1' ? ACME : GLOBEX));

    params = new BehaviorSubject<ParamMap>(convertToParamMap(partnerId ? { partnerId } : {}));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [CustomerLedgerComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), MessageService,
        { provide: CustomerLedgerService, useValue: ledger },
        { provide: BusinessPartnerService, useValue: partners },
        { provide: AuthService, useValue: auth },
        { provide: ActivatedRoute, useValue: { paramMap: params } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(CustomerLedgerComponent);
    component = fixture.componentInstance;
    navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    toasts = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
  }

  beforeEach(() => { permissions = ['CUSTOMER_LEDGER_VIEW', 'SALES_INVOICE_VIEW', 'CUSTOMER_PAYMENT_VIEW', 'CUSTOMER_PAYMENT_RECORD']; });

  // ── No customer yet ────────────────────────────────────────────────────────

  it('asks for a customer and loads nothing when the address has none', async () => {
    await setup(null);
    fixture.detectChanges();

    expect(query('choose-customer')).not.toBeNull();
    expect(query('balance-card')).toBeNull();
    expect(ledger.getLedger).not.toHaveBeenCalled();
    expect(partners.getPartnerById).not.toHaveBeenCalled();
  });

  it('opens the ledger of the customer picked, by going to their address', async () => {
    await setup(null);
    fixture.detectChanges();

    component.onCustomerSelected(GLOBEX);

    expect(navigate).toHaveBeenCalledWith(['/portal/pages/finance/customer-ledger', 'cust-2']);
    expect(ledger.getLedger).not.toHaveBeenCalled();
  });

  it('does nothing for a pick that is not a customer', async () => {
    await setup(null);
    fixture.detectChanges();

    component.onCustomerSelected({} as BusinessPartnerModel);

    expect(navigate).not.toHaveBeenCalled();
  });

  it('searches active customers only, by what was typed', async () => {
    await setup(null);
    fixture.detectChanges();

    component.searchCustomers({ query: 'glo' } as any);

    expect(partners.getPartners).toHaveBeenCalledOnceWith({ isCustomer: true, active: true, search: 'glo', pageSize: 20 });
    expect(component.customerSuggestions.length).toBe(2);
    partners.getPartners.and.returnValue(throwError(() => ({ status: 500 })));
    component.searchCustomers({ query: 'x' } as any);
    expect(component.customerSuggestions).toEqual([]);
  });

  // ── The ledger of one customer ─────────────────────────────────────────────

  it('loads the customer named in the address, with their name in the box, and shows the entries', async () => {
    await setup();
    fixture.detectChanges();

    expect(partners.getPartnerById).toHaveBeenCalledOnceWith('cust-1');
    expect((component.customer as BusinessPartnerModel).companyName).toBe('Acme Ltd');
    expect(ledger.getLedger).toHaveBeenCalledOnceWith('cust-1', { dateFrom: undefined, dateTo: undefined, page: 1, pageSize: 20 });
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelectorAll('[data-testid="entry"]').length).toBe(2);
    expect(fixture.nativeElement.textContent).toContain('CPAY-20260925-0001');
    expect(fixture.nativeElement.textContent).toContain('Received by bank transfer');
  });

  it('loads once, not once by the address and again by the table', async () => {
    await setup();
    fixture.detectChanges();
    fixture.detectChanges();

    expect(ledger.getLedger).toHaveBeenCalledTimes(1);
  });

  it('shows the debit of an invoice and the credit of a payment, each in its own column', async () => {
    await setup();
    fixture.detectChanges();
    fixture.detectChanges();

    const debits = Array.from(fixture.nativeElement.querySelectorAll('[data-testid="debit"]')).map((e: any) => e.textContent.trim());
    const credits = Array.from(fixture.nativeElement.querySelectorAll('[data-testid="credit"]')).map((e: any) => e.textContent.trim());
    expect(debits).toEqual(['', '1,170.00']);
    expect(credits).toEqual(['400.00', '']);
  });

  it('shows the balance after the newest entry, as owed by the customer', async () => {
    await setup();
    fixture.detectChanges();
    fixture.detectChanges();

    expect(component.balance).toBe(770);
    expect(query('balance')!.textContent).toContain('770.00');
    expect(query('balance')!.textContent).toContain('PKR');
    expect(query('balance-note')!.textContent!.trim()).toBe('owed by the customer');
    expect(query('balance-card')!.textContent).toContain('25 Sep 2026');
  });

  it('shows a customer in credit as a positive figure that is in credit, not as a negative one', async () => {
    await setup('cust-1', [entry({ runningBalance: -250 })]);
    fixture.detectChanges();
    fixture.detectChanges();

    expect(component.balance).toBe(-250);
    expect(component.absBalance).toBe(250);
    expect(query('balance')!.textContent).toContain('250.00');
    expect(query('balance')!.textContent).not.toContain('-');
    expect(query('balance-note')!.textContent!.trim()).toBe('in credit');
    expect(fixture.nativeElement.querySelector('[data-testid="balance"].credit')).not.toBeNull();
  });

  it('warns that the balance adds currencies together when the entries are in more than one', async () => {
    await setup('cust-1', [entry({ currencyCode: 'USD' }), INVOICE_ENTRY]);
    fixture.detectChanges();
    fixture.detectChanges();

    expect(component.mixedCurrencies).toBeTrue();
    expect(query('mixed-currency-note')!.textContent).toContain('without converting');
  });

  it('says nothing about currencies when they are all the same', async () => {
    await setup();
    fixture.detectChanges();
    fixture.detectChanges();

    expect(component.mixedCurrencies).toBeFalse();
    expect(query('mixed-currency-note')).toBeNull();
  });

  it('says a customer who owes nothing is settled', async () => {
    await setup('cust-1', [entry({ runningBalance: 0 })]);
    fixture.detectChanges();
    fixture.detectChanges();

    expect(component.balanceLabel).toBe('settled');
  });

  it('shows no balance when there is no entry to read it from', async () => {
    await setup('cust-1', []);
    fixture.detectChanges();
    fixture.detectChanges();

    expect(component.balance).toBeNull();
    expect(component.balanceLabel).toBe('');
    expect(query('balance-card')).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('No ledger entries yet');
  });

  it('says no entries fall in the range when there is a range', async () => {
    await setup('cust-1', []);
    component.dateFrom = new Date(2026, 0, 1);
    fixture.detectChanges();
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('No ledger entries in this date range');
  });

  it('keeps the balance from the first page while a later page is showing', async () => {
    await setup();
    fixture.detectChanges();
    ledger.getLedger.and.returnValue(page([entry({ runningBalance: 5, sequenceNo: 1 })], 40));

    component.onPageChange({ first: 20, rows: 20 });

    expect(ledger.getLedger.calls.mostRecent().args[1]).toEqual(jasmine.objectContaining({ page: 2 }));
    expect(component.balance).toBe(770);
  });

  it('sends the date range as calendar days, and starts again from the first page', async () => {
    await setup();
    fixture.detectChanges();
    component.currentPage = 3;
    component.dateFrom = new Date(2026, 8, 1);
    component.dateTo = new Date(2026, 8, 30, 23, 59);

    component.onFilterChange();

    expect(ledger.getLedger.calls.mostRecent().args).toEqual(['cust-1', { dateFrom: '2026-09-01', dateTo: '2026-09-30', page: 1, pageSize: 20 }]);
  });

  it('clears the dates', async () => {
    await setup();
    fixture.detectChanges();
    component.dateFrom = new Date(2026, 8, 1);
    component.dateTo = new Date(2026, 8, 30);

    component.resetFilters();

    expect(component.dateFrom).toBeNull();
    expect(component.dateTo).toBeNull();
    expect(ledger.getLedger.calls.mostRecent().args[1]).toEqual(jasmine.objectContaining({ dateFrom: undefined, dateTo: undefined }));
  });

  it('follows the address from one customer to another, starting again', async () => {
    await setup();
    fixture.detectChanges();
    component.currentPage = 3;
    ledger.getLedger.and.returnValue(page([entry({ partnerId: 'cust-2', runningBalance: 42 })]));

    params.next(convertToParamMap({ partnerId: 'cust-2' }));

    expect(partners.getPartnerById).toHaveBeenCalledWith('cust-2');
    expect(ledger.getLedger.calls.mostRecent().args[0]).toBe('cust-2');
    expect(ledger.getLedger.calls.mostRecent().args[1]).toEqual(jasmine.objectContaining({ page: 1 }));
    expect(component.balance).toBe(42);
    expect((component.customer as BusinessPartnerModel).companyName).toBe('Globex Corp');
  });

  it('shows the customer picked without fetching them again', async () => {
    await setup(null);
    fixture.detectChanges();
    component.customer = GLOBEX;

    params.next(convertToParamMap({ partnerId: 'cust-2' }));

    expect(partners.getPartnerById).not.toHaveBeenCalled();
    expect(ledger.getLedger).toHaveBeenCalledTimes(1);
  });

  it('goes back to asking for a customer when the address loses theirs', async () => {
    await setup();
    fixture.detectChanges();

    params.next(convertToParamMap({}));
    fixture.detectChanges();

    expect(component.partnerId).toBeNull();
    expect(component.customer).toBeNull();
    expect(component.entries).toEqual([]);
    expect(query('choose-customer')).not.toBeNull();
  });

  it('still shows the ledger when the customers name cannot be fetched', async () => {
    await setup();
    partners.getPartnerById.and.returnValue(throwError(() => ({ status: 500 })));
    fixture.detectChanges();

    expect(component.customer).toBeNull();
    expect(component.entries.length).toBe(2);
  });

  it('empties the ledger and shows the servers reason when it cannot be loaded', async () => {
    await setup();
    ledger.getLedger.and.returnValue(throwError(() => ({ error: { message: 'The range is backwards.' } })));
    fixture.detectChanges();

    expect(component.entries).toEqual([]);
    expect(component.balance).toBeNull();
    expect(component.isLoading).toBeFalse();
    expect((toasts.calls.mostRecent().args[0] as any).detail).toBe('The range is backwards.');
  });

  it('ignores an answer for a customer who is no longer the one showing', async () => {
    await setup();
    const late = new Subject<any>();
    ledger.getLedger.and.returnValue(late);
    fixture.detectChanges();
    ledger.getLedger.and.returnValue(page([entry({ runningBalance: 42 })]));

    params.next(convertToParamMap({ partnerId: 'cust-2' }));
    late.next({ success: true, message: '', result: { data: [entry({ runningBalance: 999 })], totalRecords: 1, page: 1, pageSize: 20, totalPages: 1 } });

    expect(component.balance).toBe(42);
  });

  // ── References ─────────────────────────────────────────────────────────────

  it('links an entry to its invoice or payment for someone who may open them', async () => {
    await setup();
    fixture.detectChanges();

    expect(component.referenceLink(INVOICE_ENTRY)).toEqual(['/portal/pages/finance/sales-invoices', 'inv-1']);
    expect(component.referenceLink(entry())).toEqual(['/portal/pages/finance/customer-payments', 'pay-1']);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelectorAll('[data-testid="reference-link"]').length).toBe(2);
  });

  it('shows a reference as plain text when the user may not open it', async () => {
    permissions = ['CUSTOMER_LEDGER_VIEW'];
    await setup();
    fixture.detectChanges();
    fixture.detectChanges();

    expect(component.referenceLink(INVOICE_ENTRY)).toBeNull();
    expect(component.referenceLink(entry())).toBeNull();
    expect(fixture.nativeElement.querySelectorAll('[data-testid="reference-link"]').length).toBe(0);
    expect(fixture.nativeElement.textContent).toContain('SINV-20260920-0001');
  });

  it('has no link for an entry with no document, or a kind of document that has no page', async () => {
    await setup();

    expect(component.referenceLink(entry({ referenceId: '' }))).toBeNull();
    expect(component.referenceLink(entry({ referenceType: 'CreditNote' }))).toBeNull();
  });

  it('marks money owed and money in differently, and knows every kind of entry', () => {
    for (const type of ['INVOICE', 'PAYMENT', 'CREDIT_NOTE', 'DEBIT_NOTE', 'ADVANCE', 'REFUND', 'OPENING_BAL'])
      expect(LEDGER_ENTRY_SEVERITY[type]).withContext(type).toBeDefined();
    expect(LEDGER_ENTRY_SEVERITY['INVOICE']).not.toBe(LEDGER_ENTRY_SEVERITY['PAYMENT']);
  });

  // ── Actions ────────────────────────────────────────────────────────────────

  it('offers to record a payment for this customer only to someone who may', async () => {
    await setup();
    fixture.detectChanges();
    expect(query('record-payment')).not.toBeNull();

    permissions = ['CUSTOMER_LEDGER_VIEW'];
    await setup();
    fixture.detectChanges();
    expect(query('record-payment')).toBeNull();

    permissions = ['CUSTOMER_LEDGER_VIEW', 'CUSTOMER_PAYMENT_RECORD'];
    await setup(null);
    fixture.detectChanges();
    expect(query('record-payment')).withContext('no customer to record it for').toBeNull();
  });
});
