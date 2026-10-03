import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { Observable, of, throwError } from 'rxjs';

import { SupplierPaymentCreateComponent } from './supplier-payment-create.component';
import { FinanceService, CreateSupplierPaymentRequest, OutstandingInvoiceModel } from '../../../../services/finance.service';
import { SupplierService } from '../../../../services/supplier.service';

function ok<T>(result: T) {
  return of({ success: true, message: '', result } as any);
}

function outstanding(invoiceUuid: string, invoiceNumber: string, outstandingAmount: number): OutstandingInvoiceModel {
  return { invoiceUuid, invoiceNumber, totalAmount: 1170, outstandingAmount, paymentStatus: 'UNPAID', dueDate: '2026-10-15T00:00:00' };
}

describe('SupplierPaymentCreateComponent', () => {
  let fixture: ComponentFixture<SupplierPaymentCreateComponent>;
  let component: SupplierPaymentCreateComponent;
  let finance: jasmine.SpyObj<FinanceService>;
  let toasts: jasmine.Spy;

  /** The server lists approved invoices with something still owed — nothing else can be paid. */
  async function setup(opts: { query?: Record<string, string>; invoices?: OutstandingInvoiceModel[]; invoicesLoad?: Observable<any> } = {}) {
    finance = jasmine.createSpyObj<FinanceService>('FinanceService', ['getOutstandingInvoices', 'createSupplierPayment']);
    finance.getOutstandingInvoices.and.returnValue(opts.invoicesLoad ?? ok(opts.invoices ?? []));
    finance.createSupplierPayment.and.returnValue(ok('pay-new'));

    const suppliers = jasmine.createSpyObj<SupplierService>('SupplierService', ['getSupplierById', 'getSuppliers']);
    suppliers.getSupplierById.and.returnValue(ok({ uuid: 'sup-1', supplierName: 'Karachi Steel' }));
    suppliers.getSuppliers.and.returnValue(ok({ data: [{ uuid: 'sup-1', supplierName: 'Karachi Steel' }], totalRecords: 1 }));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [SupplierPaymentCreateComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(),
        { provide: FinanceService, useValue: finance },
        { provide: SupplierService, useValue: suppliers },
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(opts.query ?? {}), paramMap: convertToParamMap({}) } } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(SupplierPaymentCreateComponent);
    component = fixture.componentInstance;
    toasts = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
    spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    fixture.detectChanges();
  }

  function text(): string {
    return (fixture.nativeElement as HTMLElement).textContent ?? '';
  }

  // ── What can be paid ───────────────────────────────────────────────────────

  it('says that only approved invoices can be paid when the supplier has none to pay', async () => {
    await setup({ query: { supplierId: 'sup-1' } });
    fixture.detectChanges();

    expect(component.outstandingInvoices).toEqual([]);
    expect(text()).toContain('Only approved invoices can be paid');
    expect(text()).toContain('free-standing Advance Payment');
  });

  it('says so beside the picker too, when there are invoices to pick from', async () => {
    await setup({ query: { supplierId: 'sup-1' }, invoices: [outstanding('inv-1', 'INV-2026-00042', 1170)] });
    fixture.detectChanges();

    expect(text()).toContain('Only approved invoices can be paid');
  });

  it('pre-selects the invoice Record Payment was pressed on, with what is still owed', async () => {
    await setup({ query: { supplierId: 'sup-1', invoiceUuid: 'inv-1' }, invoices: [outstanding('inv-1', 'INV-2026-00042', 670)] });

    expect(component.lineInputs.map(l => [l.invoiceUuid, l.outstandingAmount, l.allocatedAmount])).toEqual([['inv-1', 670, 670]]);
    expect(toasts).not.toHaveBeenCalled();
  });

  it('says why when the invoice asked for is not payable (not approved, or nothing owed), and carries on without it', async () => {
    await setup({ query: { supplierId: 'sup-1', invoiceUuid: 'inv-pending' }, invoices: [outstanding('inv-1', 'INV-2026-00042', 1170)] });

    expect(component.lineInputs).toEqual([]);
    expect(component.outstandingInvoices.length).toBe(1);
    const toast = toasts.calls.mostRecent()?.args[0];
    expect(toast?.severity).toBe('warn');
    expect(toast?.detail).toContain('approved');
  });

  it('says so when the invoices cannot be loaded, rather than showing an empty list as if nothing were owed', async () => {
    await setup({ query: { supplierId: 'sup-1' }, invoicesLoad: throwError(() => ({ status: 500 })) });

    expect(component.isLoadingInvoices).toBeFalse();
    expect(toasts.calls.mostRecent()?.args[0]?.severity).toBe('error');
  });

  it("shows the server's refusal of an invoice that is not approved", async () => {
    await setup({ query: { supplierId: 'sup-1', invoiceUuid: 'inv-1' }, invoices: [outstanding('inv-1', 'INV-2026-00042', 1170)] });
    finance.createSupplierPayment.and.returnValue(throwError(() => ({
      status: 400, error: { message: 'Invoice INV-2026-00042 is not approved yet; approve it before paying.' }
    })));
    component.bankAccount = 'HBL 0001';

    component.save();

    const toast = toasts.calls.mostRecent().args[0];
    expect(toast.severity).toBe('error');
    expect(toast.detail).toBe('Invoice INV-2026-00042 is not approved yet; approve it before paying.');
    expect(component.isSaving).toBeFalse();
  });

  // ── Dates: the day picked, not its UTC instant ─────────────────────────────

  it('sends the payment and cheque dates as the days picked', async () => {
    await setup({ query: { supplierId: 'sup-1', invoiceUuid: 'inv-1' }, invoices: [outstanding('inv-1', 'INV-2026-00042', 1170)] });
    component.paymentDateVal = new Date(2026, 9, 2);       // 2 Oct, local midnight
    component.paymentMethod = 'CHEQUE';
    component.chequeNo = '000123';
    component.chequeDateVal = new Date(2026, 9, 5);

    component.save();

    const sent = finance.createSupplierPayment.calls.mostRecent().args[0] as CreateSupplierPaymentRequest;
    expect(sent.paymentDate).toBe('2026-10-02');
    expect(sent.chequeDate).toBe('2026-10-05');
    expect(sent.lines).toEqual([{ invoiceUuid: 'inv-1', allocatedAmount: 1170, notes: undefined }]);
  });
});
