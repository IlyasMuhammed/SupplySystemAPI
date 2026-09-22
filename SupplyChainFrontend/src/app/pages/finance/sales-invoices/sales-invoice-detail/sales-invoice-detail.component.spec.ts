import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute, Router, RouterLink, UrlTree } from '@angular/router';
import { By } from '@angular/platform-browser';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError, Subject } from 'rxjs';

import { SalesInvoiceDetailComponent } from './sales-invoice-detail.component';
import { SalesInvoiceService, SalesInvoiceDetailModel } from '../../../../services/sales-invoice.service';
import { AttachmentService } from '../../../../services/attachment.service';
import { AuthService } from '../../../service/auth.service';

const UUID = '22222222-2222-2222-2222-222222222222';

const ALL_PERMISSIONS = [
  'SALES_INVOICE_VIEW', 'SALES_INVOICE_MANAGE', 'CUSTOMER_PAYMENT_VIEW', 'CUSTOMER_PAYMENT_RECORD',
  'CUSTOMER_LEDGER_VIEW', 'DELIVERY_VIEW', 'SALE_ORDER_VIEW'
];

function invoice(overrides: Partial<SalesInvoiceDetailModel> = {}): SalesInvoiceDetailModel {
  return {
    uuid: UUID, invoiceNumber: 'SINV-20260920-0001', saleOrderUuid: 'so-1', saleOrderNumber: 'SO-2026-00042',
    deliveryUuid: 'd-1', deliveryNumber: 'DLV-2026-00001', partnerId: 'p-1', partnerName: 'Acme Ltd',
    invoiceDate: '2026-09-20T00:00:00Z', dueDate: '2026-10-20T00:00:00Z',
    grandTotal: 1170, amountPaid: 0, balanceDue: 1170, status: 'ISSUED', currencyCode: 'PKR',
    traceId: 't-1', subtotal: 1000, discountAmount: 0, taxAmount: 170, notes: null, createdDate: '2026-09-20T09:30:00Z',
    lines: [
      { lineNo: 1, soLineUuid: 'sl-1', variantUuid: 'v-1', description: '4mm cable (CAB-4MM)', quantity: 25, unitPrice: 40, discountPercent: 0, taxPercent: 17, lineTotal: 1170 }
    ],
    payments: [],
    ...overrides
  } as SalesInvoiceDetailModel;
}

function ok<T>(result: T, message = '') {
  return of({ success: true, message, result } as any);
}

describe('SalesInvoiceDetailComponent', () => {
  let fixture: ComponentFixture<SalesInvoiceDetailComponent>;
  let component: SalesInvoiceDetailComponent;
  let invoices: jasmine.SpyObj<SalesInvoiceService>;
  let attachments: jasmine.SpyObj<AttachmentService>;
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

  async function setup(model: SalesInvoiceDetailModel | null = invoice()) {
    invoices = jasmine.createSpyObj<SalesInvoiceService>('SalesInvoiceService',
      ['getInvoice', 'issueInvoice', 'updateInvoice', 'deleteInvoice', 'attachPdf', 'downloadPdf']);
    invoices.getInvoice.and.returnValue(model ? ok(model) : of({ success: false, message: 'no', result: null } as any));
    invoices.issueInvoice.and.returnValue(ok({ invoiceUuid: UUID, invoiceNumber: 'x', status: 'ISSUED', grandTotal: 1170, partnerBalance: 1170 }, 'Sales invoice issued and its PDF filed.'));
    invoices.updateInvoice.and.returnValue(ok(null));
    invoices.deleteInvoice.and.returnValue(ok(null));
    invoices.attachPdf.and.returnValue(ok({ alreadyStored: false }, 'Invoice PDF filed as an attachment.'));
    invoices.downloadPdf.and.returnValue(of(new Blob(['%PDF'])));

    attachments = jasmine.createSpyObj<AttachmentService>('AttachmentService', ['getAttachments', 'resolveUrl', 'isApiUrl', 'download']);
    attachments.getAttachments.and.returnValue(ok([]));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [SalesInvoiceDetailComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), MessageService,
        { provide: SalesInvoiceService, useValue: invoices },
        { provide: AttachmentService, useValue: attachments },
        { provide: AuthService, useValue: auth },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['uuid', UUID]]) } } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(SalesInvoiceDetailComponent);
    component = fixture.componentInstance;
    navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    toasts = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
  }

  beforeEach(() => { permissions = [...ALL_PERMISSIONS]; });

  // ── Showing the invoice ────────────────────────────────────────────────────

  it('loads the invoice and renders its numbers, lines and customer', async () => {
    await setup();
    fixture.detectChanges();

    expect(invoices.getInvoice).toHaveBeenCalledOnceWith(UUID);
    const text = fixture.nativeElement.textContent;
    expect(text).toContain('SINV-20260920-0001');
    expect(text).toContain('Acme Ltd');
    expect(text).toContain('4mm cable (CAB-4MM)');
    expect(query('grand-total')!.textContent!.trim()).toBe('1,170.00');
    expect(query('amount-paid')!.textContent!.trim()).toBe('0.00');
    expect(query('balance-due')!.textContent!.trim()).toBe('1,170.00');
  });

  it('shows a not-found state for a missing invoice, and an error toast for a failed request', async () => {
    await setup(null);
    fixture.detectChanges();
    expect(query('not-found')).not.toBeNull();

    await setup();
    invoices.getInvoice.and.returnValue(throwError(() => ({ status: 500 })));
    fixture.detectChanges();
    expect(component.notFound).toBeFalse();
    expect(lastToast().severity).toBe('error');
  });

  it('treats a 404 as not found', async () => {
    await setup();
    invoices.getInvoice.and.returnValue(throwError(() => ({ status: 404 })));
    fixture.detectChanges();

    expect(component.notFound).toBeTrue();
  });

  it('links to the customers ledger, the order and the delivery for someone who may open them', async () => {
    await setup();
    fixture.detectChanges();

    expect(query('customer-link')).not.toBeNull();
    expect(query('order-link')).not.toBeNull();
    expect(query('delivery-link')).not.toBeNull();
  });

  it('shows them as plain text for someone who may not', async () => {
    permissions = ['SALES_INVOICE_VIEW'];
    await setup();
    fixture.detectChanges();

    expect(query('customer-link')).toBeNull();
    expect(query('order-link')).toBeNull();
    expect(query('delivery-link')).toBeNull();
    expect(query('customer-name')!.textContent).toContain('Acme Ltd');
    expect(fixture.nativeElement.textContent).toContain('SO-2026-00042');
    expect(fixture.nativeElement.textContent).toContain('DLV-2026-00001');
  });

  it('shows a dash for an invoice with no delivery', async () => {
    await setup(invoice({ deliveryUuid: null, deliveryNumber: null }));
    fixture.detectChanges();

    expect(query('delivery-link')).toBeNull();
    expect(fixture.nativeElement.textContent).not.toContain('DLV-');
  });

  it('shows the notes when there are some', async () => {
    await setup(invoice({ notes: 'Net 30' }));
    fixture.detectChanges();

    expect(query('notes')!.textContent).toContain('Net 30');
  });

  it('lists the payments applied, linking each to its page only for someone who may open it', async () => {
    const payments = [{
      allocationUuid: 'a1', paymentUuid: 'pay-1', paymentNumber: 'CPAY-20260925-0001', paymentDate: '2026-09-25T00:00:00Z',
      paymentMethod: 'BANK_TRANSFER', paymentStatus: 'RECEIVED', allocatedAmount: 400, allocatedAt: '2026-09-25T08:00:00Z', allocatedBy: 1
    }];
    await setup(invoice({ status: 'PARTIALLY_PAID', amountPaid: 400, balanceDue: 770, payments }));
    fixture.detectChanges();

    expect(query('payments-table')!.textContent).toContain('CPAY-20260925-0001');
    expect(query('payments-table')!.textContent).toContain('Bank Transfer');
    expect(query('payment-link')).not.toBeNull();

    permissions = ['SALES_INVOICE_VIEW'];
    await setup(invoice({ status: 'PARTIALLY_PAID', payments }));
    fixture.detectChanges();
    expect(query('payment-link')).toBeNull();
    expect(query('payments-table')!.textContent).toContain('CPAY-20260925-0001');
  });

  it('says no payment has been applied when there is none', async () => {
    await setup();
    fixture.detectChanges();

    expect(query('payments-table')!.textContent).toContain('No payment has been applied');
  });

  it('marks a payment that bounced', async () => {
    await setup();

    expect(component.getPaymentSeverity({ paymentStatus: 'RECEIVED' } as any)).toBe('success');
    expect(component.getPaymentSeverity({ paymentStatus: 'BOUNCED' } as any)).toBe('danger');
    expect(component.getPaymentSeverity({ paymentStatus: 'SOMETHING_NEW' } as any)).toBe('secondary');
  });

  // ── What shows, and to whom ────────────────────────────────────────────────

  it('offers edit, issue and delete on a draft, and no payment or filing', async () => {
    await setup(invoice({ status: 'DRAFT' }));
    fixture.detectChanges();

    expect(query('action-edit')).not.toBeNull();
    expect(query('action-issue')).not.toBeNull();
    expect(query('action-delete')).not.toBeNull();
    expect(query('action-record-payment')).toBeNull();
    expect(query('action-file-pdf')).toBeNull();
    expect(query('action-pdf')).not.toBeNull();
  });

  it('offers a payment and a filing on an issued invoice, and no edit, issue or delete', async () => {
    await setup(invoice({ status: 'ISSUED' }));
    fixture.detectChanges();

    expect(query('action-edit')).toBeNull();
    expect(query('action-issue')).toBeNull();
    expect(query('action-delete')).toBeNull();
    expect(query('action-record-payment')).not.toBeNull();
    expect(query('action-file-pdf')).not.toBeNull();
  });

  it('offers a payment while something is owing on an issued invoice, and not otherwise', async () => {
    for (const status of ['ISSUED', 'PARTIALLY_PAID', 'OVERDUE']) {
      await setup(invoice({ status, balanceDue: 10 }));
      fixture.detectChanges();
      expect(component.canRecordPayment).withContext(status).toBeTrue();
    }
    for (const status of ['DRAFT', 'PAID', 'CANCELLED', 'CREDIT_NOTE']) {
      await setup(invoice({ status, balanceDue: 10 }));
      fixture.detectChanges();
      expect(component.canRecordPayment).withContext(status).toBeFalse();
    }

    await setup(invoice({ status: 'ISSUED', balanceDue: 0 }));
    fixture.detectChanges();
    expect(component.canRecordPayment).withContext('nothing owing').toBeFalse();
  });

  it('carries the customer and the invoice to the payment form', async () => {
    await setup();
    fixture.detectChanges();

    const router = TestBed.inject(Router);
    const urls = fixture.debugElement.queryAll(By.directive(RouterLink))
      .map(d => d.injector.get(RouterLink).urlTree)
      .filter((t): t is UrlTree => !!t)
      .map(t => router.serializeUrl(t));

    expect(urls).toContain(`/portal/pages/finance/customer-payments/new?partnerId=p-1&invoiceUuid=${UUID}`);
  });

  it('hides each action from someone without its permission', async () => {
    permissions = ['SALES_INVOICE_VIEW'];
    await setup(invoice({ status: 'DRAFT' }));
    fixture.detectChanges();

    expect(query('action-edit')).toBeNull();
    expect(query('action-issue')).toBeNull();
    expect(query('action-delete')).toBeNull();

    await setup(invoice({ status: 'ISSUED' }));
    fixture.detectChanges();
    expect(query('action-record-payment')).toBeNull();
    expect(query('action-file-pdf')).toBeNull();
    expect(query('action-pdf')).withContext('viewing the PDF is viewing').not.toBeNull();
  });

  it('shows the filed copies for an invoice that has been issued, and not for a draft', async () => {
    await setup(invoice({ status: 'ISSUED' }));
    fixture.detectChanges();
    expect(query('filed-copies')).not.toBeNull();
    expect(attachments.getAttachments).toHaveBeenCalledOnceWith('SALES_INVOICE', UUID);

    await setup(invoice({ status: 'DRAFT' }));
    fixture.detectChanges();
    expect(query('filed-copies')).toBeNull();
  });

  // ── PDF ────────────────────────────────────────────────────────────────────

  it('opens the pdf viewer for this invoice', async () => {
    await setup();
    fixture.detectChanges();
    spyOn(URL, 'createObjectURL').and.returnValue('blob:x');
    spyOn(URL, 'revokeObjectURL');

    query('action-pdf')!.querySelector('button')!.click();
    fixture.detectChanges();

    expect(component.pdfVisible).toBeTrue();
    expect(invoices.downloadPdf).toHaveBeenCalledOnceWith(UUID);
  });

  // ── Edit a draft ───────────────────────────────────────────────────────────

  it('opens the edit dialog with the invoices own due date and notes', async () => {
    await setup(invoice({ status: 'DRAFT', notes: 'Net 30' }));
    fixture.detectChanges();

    component.openEditDialog();

    expect(component.editVisible).toBeTrue();
    expect(component.editNotes).toBe('Net 30');
    const due = component.editDueDate!;
    expect([due.getFullYear(), due.getMonth(), due.getDate()]).toEqual([2026, 9, 20]);
    expect(component.editMinDate!.getDate()).toBe(20);
  });

  it('saves the due date as a calendar day and the notes trimmed, then reloads', async () => {
    await setup(invoice({ status: 'DRAFT' }));
    fixture.detectChanges();
    component.openEditDialog();
    component.editDueDate = new Date(2026, 10, 5);
    component.editNotes = '  Net 45  ';

    component.saveEdit();

    expect(invoices.updateInvoice).toHaveBeenCalledOnceWith(UUID, { dueDate: '2026-11-05', notes: 'Net 45' });
    expect(component.editVisible).toBeFalse();
    expect(invoices.getInvoice).withContext('reloaded').toHaveBeenCalledTimes(2);
    expect(lastToast().severity).toBe('success');
  });

  it('clears the notes when they are emptied', async () => {
    await setup(invoice({ status: 'DRAFT', notes: 'Net 30' }));
    fixture.detectChanges();
    component.openEditDialog();
    component.editNotes = '   ';

    component.saveEdit();

    expect(invoices.updateInvoice.calls.mostRecent().args[1].notes).toBeUndefined();
  });

  it('will not save without a due date, or with notes past the limit', async () => {
    await setup(invoice({ status: 'DRAFT' }));
    fixture.detectChanges();
    component.openEditDialog();

    component.editDueDate = null;
    expect(component.canSaveEdit).toBeFalse();
    component.saveEdit();

    component.editDueDate = new Date(2026, 10, 5);
    component.editNotes = 'x'.repeat(501);
    expect(component.canSaveEdit).toBeFalse();
    component.saveEdit();

    expect(invoices.updateInvoice).not.toHaveBeenCalled();

    component.editNotes = 'x'.repeat(500);
    expect(component.canSaveEdit).toBeTrue();
  });

  it('shows the servers reason when the edit is refused, and keeps the dialog open', async () => {
    await setup(invoice({ status: 'DRAFT' }));
    fixture.detectChanges();
    invoices.updateInvoice.and.returnValue(throwError(() => ({ error: { message: 'The due date is before the invoice date.' } })));
    component.openEditDialog();

    component.saveEdit();

    expect(lastToast().detail).toBe('The due date is before the invoice date.');
    expect(component.editVisible).toBeTrue();
    expect(component.isSaving).toBeFalse();
  });

  it('will not open the edit dialog on an issued invoice, or for someone who may not manage', async () => {
    await setup(invoice({ status: 'ISSUED' }));
    fixture.detectChanges();
    component.openEditDialog();
    component.saveEdit();
    expect(component.editVisible).toBeFalse();
    expect(invoices.updateInvoice).not.toHaveBeenCalled();

    permissions = ['SALES_INVOICE_VIEW'];
    await setup(invoice({ status: 'DRAFT' }));
    fixture.detectChanges();
    component.openEditDialog();
    expect(component.editVisible).toBeFalse();
  });

  // ── Issue ──────────────────────────────────────────────────────────────────

  it('asks before issuing, and books nothing by asking', async () => {
    await setup(invoice({ status: 'DRAFT' }));
    fixture.detectChanges();

    component.openIssueDialog();

    expect(component.issueVisible).toBeTrue();
    expect(invoices.issueInvoice).not.toHaveBeenCalled();
  });

  it('issues the invoice, reports what the server said, and reloads it', async () => {
    await setup(invoice({ status: 'DRAFT' }));
    fixture.detectChanges();
    component.openIssueDialog();

    component.issue();

    expect(invoices.issueInvoice).toHaveBeenCalledOnceWith(UUID);
    expect(component.issueVisible).toBeFalse();
    expect(component.isIssuing).toBeFalse();
    expect(lastToast().severity).toBe('success');
    expect(lastToast().detail).toBe('Sales invoice issued and its PDF filed.');
    expect(invoices.getInvoice).toHaveBeenCalledTimes(2);
  });

  it('shows the filed copies once the reloaded invoice is issued', async () => {
    await setup(invoice({ status: 'DRAFT' }));
    fixture.detectChanges();
    expect(query('filed-copies')).toBeNull();
    invoices.getInvoice.and.returnValue(ok(invoice({ status: 'ISSUED' })));
    component.openIssueDialog();

    component.issue();
    fixture.detectChanges();

    expect(query('filed-copies')).not.toBeNull();
    expect(attachments.getAttachments).toHaveBeenCalledOnceWith('SALES_INVOICE', UUID);
  });

  it('shows the servers reason when issuing is refused, and leaves the dialog open', async () => {
    await setup(invoice({ status: 'DRAFT' }));
    fixture.detectChanges();
    invoices.issueInvoice.and.returnValue(throwError(() => ({ error: { message: 'Sales invoice SINV-1 is ISSUED. Only a DRAFT can be issued.' } })));
    component.openIssueDialog();

    component.issue();

    expect(lastToast().severity).toBe('error');
    expect(lastToast().detail).toContain('Only a DRAFT can be issued');
    expect(component.issueVisible).toBeTrue();
    expect(component.isIssuing).toBeFalse();
    expect(invoices.getInvoice).toHaveBeenCalledTimes(1);
  });

  it('issues once however often the button is pressed while it is working', async () => {
    await setup(invoice({ status: 'DRAFT' }));
    fixture.detectChanges();
    invoices.issueInvoice.and.returnValue(new Subject<any>());
    component.openIssueDialog();

    component.issue();
    component.issue();

    expect(invoices.issueInvoice).toHaveBeenCalledTimes(1);
  });

  it('will not issue an invoice that is not a draft, or for someone who may not manage', async () => {
    await setup(invoice({ status: 'PAID' }));
    fixture.detectChanges();
    component.openIssueDialog();
    component.issue();
    expect(component.issueVisible).toBeFalse();

    permissions = ['SALES_INVOICE_VIEW'];
    await setup(invoice({ status: 'DRAFT' }));
    fixture.detectChanges();
    component.issue();

    expect(invoices.issueInvoice).not.toHaveBeenCalled();
  });

  // ── Delete ─────────────────────────────────────────────────────────────────

  it('deletes a draft and goes back to the list', async () => {
    await setup(invoice({ status: 'DRAFT' }));
    fixture.detectChanges();
    component.openDeleteDialog();

    component.deleteDraft();

    expect(invoices.deleteInvoice).toHaveBeenCalledOnceWith(UUID);
    expect(navigate).toHaveBeenCalledWith(['/portal/pages/finance/sales-invoices']);
    expect(component.deleteVisible).toBeFalse();
  });

  it('shows the servers reason when the delete is refused, and stays', async () => {
    await setup(invoice({ status: 'DRAFT' }));
    fixture.detectChanges();
    invoices.deleteInvoice.and.returnValue(throwError(() => ({ error: { message: 'Only a DRAFT can be deleted.' } })));
    component.openDeleteDialog();

    component.deleteDraft();

    expect(lastToast().detail).toBe('Only a DRAFT can be deleted.');
    expect(navigate).not.toHaveBeenCalled();
    expect(component.isDeleting).toBeFalse();
  });

  it('will not delete an issued invoice', async () => {
    await setup(invoice({ status: 'ISSUED' }));
    fixture.detectChanges();

    component.openDeleteDialog();
    component.deleteDraft();

    expect(component.deleteVisible).toBeFalse();
    expect(invoices.deleteInvoice).not.toHaveBeenCalled();
  });

  // ── File the PDF ───────────────────────────────────────────────────────────

  it('files a copy of the PDF and refreshes the filed copies', async () => {
    await setup(invoice({ status: 'ISSUED' }));
    fixture.detectChanges();
    const reload = spyOn(component.attachments!, 'load');

    component.filePdf();

    expect(invoices.attachPdf).toHaveBeenCalledOnceWith(UUID);
    expect(lastToast().detail).toBe('Invoice PDF filed as an attachment.');
    expect(reload).toHaveBeenCalledTimes(1);
    expect(component.isFiling).toBeFalse();
  });

  it('shows the servers reason when filing fails', async () => {
    await setup(invoice({ status: 'ISSUED' }));
    fixture.detectChanges();
    invoices.attachPdf.and.returnValue(throwError(() => ({ status: 500 })));

    component.filePdf();

    expect(lastToast().severity).toBe('error');
    expect(lastToast().detail).toBe('The invoice PDF could not be filed.');
    expect(component.isFiling).toBeFalse();
  });

  it('will not file the PDF of a draft', async () => {
    await setup(invoice({ status: 'DRAFT' }));
    fixture.detectChanges();

    component.filePdf();

    expect(invoices.attachPdf).not.toHaveBeenCalled();
  });
});
