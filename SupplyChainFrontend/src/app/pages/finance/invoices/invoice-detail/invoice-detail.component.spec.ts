import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute, convertToParamMap } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ConfirmationService, MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { InvoiceDetailComponent } from './invoice-detail.component';
import { FinanceService, InvoiceDetailModel, SupplierPaymentListItemModel, NO_TAX_CODE } from '../../../../services/finance.service';
import { FinanceSetupService, TaxCodeModel } from '../../../../services/finance-setup.service';
import { AuthService } from '../../../service/auth.service';

function ok<T>(result: T, message = '') {
  return of({ success: true, message, result } as any);
}

const GST17: TaxCodeModel = { uuid: 'gst17', code: 'GST17', name: 'GST 17', description: null, ratePercent: 17, usage: 'PURCHASE', isDefault: true, isActive: true };
const GST5: TaxCodeModel  = { uuid: 'gst5', code: 'GST5', name: 'GST 5', description: null, ratePercent: 5, usage: 'BOTH', isDefault: false, isActive: true };

function invoice(overrides: Partial<InvoiceDetailModel> = {}): InvoiceDetailModel {
  return {
    uuid: 'inv-1', invoiceNumber: 'INV-2026-00042', supplierInvoiceNo: 'KSW/881', supplierId: 'sup-1', supplierName: 'Karachi Steel',
    poUuid: 'po-1', poNumber: 'PO-2026-00007', invoiceDate: '2026-09-15T00:00:00', receivedDate: '2026-09-16T00:00:00',
    dueDate: '2026-10-15T00:00:00', currency: 'PKR', subtotal: 1000, taxAmount: 170, totalAmount: 1170,
    taxCodeUuid: 'gst17', taxCode: 'GST17', taxPercent: 17,
    matchedPoValue: 1000, matchedGrnValue: 1000, varianceAmount: 0, matchStatus: 'Approved', paymentStatus: 'Unpaid',
    paidAmount: 0, approvedAt: '2026-09-20T10:00:00', createdDate: '2026-09-16T10:00:00',
    lines: [], payments: [], debitNotes: [], creditNotes: [],
    ...overrides
  };
}

function payment(status: string): SupplierPaymentListItemModel {
  return {
    uuid: `pay-${status}`, paymentNumber: `SPAY-2026-0000${status.length}`, supplierId: 'sup-1', supplierName: 'Karachi Steel',
    paymentDate: '2026-09-25', paymentMethod: 'CASH', totalAmount: 100, status, paymentType: 'STANDARD', lineCount: 1, attachmentCount: 0
  };
}

describe('InvoiceDetailComponent', () => {
  let fixture: ComponentFixture<InvoiceDetailComponent>;
  let component: InvoiceDetailComponent;
  let finance: jasmine.SpyObj<FinanceService>;
  let setupService: jasmine.SpyObj<FinanceSetupService>;
  let permissions: string[];
  let toasts: jasmine.Spy;
  let confirm: jasmine.Spy;

  const auth = { hasPermission: (c: string) => permissions.includes(c) } as unknown as AuthService;

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  /** The labels of the buttons in the page's command bar, as the user sees them offered. */
  function actions(): string[] {
    return Array.from(fixture.nativeElement.querySelectorAll('.sf-cmd p-button') as NodeListOf<HTMLElement>)
      .map(b => b.getAttribute('label') ?? '')
      .filter(l => !!l);
  }

  async function setup(inv: InvoiceDetailModel, newPayments: SupplierPaymentListItemModel[] = []) {
    finance = jasmine.createSpyObj<FinanceService>('FinanceService',
      ['getInvoiceById', 'getSupplierPayments', 'reverseInvoice', 'patchInvoice', 'approveInvoice', 'rejectInvoice', 'resolveFileUrl', 'downloadInvoicePdf']);
    finance.getInvoiceById.and.returnValue(ok(inv));
    finance.getSupplierPayments.and.returnValue(ok({ data: newPayments, totalRecords: newPayments.length }));
    finance.reverseInvoice.and.returnValue(ok(invoice({ matchStatus: 'Reversed', reversalReason: 'Entered twice', reversedAt: '2026-09-30T09:00:00' }), 'Invoice reversed.'));
    finance.patchInvoice.and.returnValue(ok(null));

    setupService = jasmine.createSpyObj<FinanceSetupService>('FinanceSetupService', ['getTaxCodes']);
    setupService.getTaxCodes.and.returnValue(ok([GST17, GST5]));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [InvoiceDetailComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(),
        { provide: FinanceService, useValue: finance },
        { provide: FinanceSetupService, useValue: setupService },
        { provide: AuthService, useValue: auth },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ uuid: inv.uuid }) } } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(InvoiceDetailComponent);
    component = fixture.componentInstance;
    toasts  = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
    confirm = spyOn(fixture.debugElement.injector.get(ConfirmationService), 'confirm').and.callFake((c: any) => { c.accept?.(); return undefined as any; });
    fixture.detectChanges();
  }

  beforeEach(() => { permissions = ['INVOICE_VIEW', 'INVOICE_PROCESS']; });

  // ── What it shows ──────────────────────────────────────────────────────────

  it('labels the tax with its code and rate', async () => {
    await setup(invoice());

    expect(query('invoice-tax-label')?.textContent?.trim()).toBe('Tax (GST17 · 17%)');
  });

  it('labels a hand-entered tax plainly', async () => {
    await setup(invoice({ taxCodeUuid: null, taxCode: null, taxPercent: null }));

    expect(query('invoice-tax-label')?.textContent?.trim()).toBe('Tax');
  });

  it('shows the base-currency total snapshotted at approval for a foreign invoice', async () => {
    await setup(invoice({ currency: 'USD', exchangeRate: 278.5, baseCurrencyCode: 'PKR', baseTotalAmount: 325845 }));

    const row = query('invoice-base-total')?.textContent ?? '';
    expect(row).toContain('In PKR');
    expect(row).toContain('278.5');
    expect(row).toContain('325,845.00 PKR');
  });

  it('shows no base-currency row for an invoice in the base currency', async () => {
    await setup(invoice({ exchangeRate: 1, baseCurrencyCode: 'PKR', baseTotalAmount: 1170 }));

    expect(query('invoice-base-total')).toBeNull();
  });

  it('says so when a foreign invoice was approved with no rate on file', async () => {
    await setup(invoice({ currency: 'EUR', exchangeRate: null, baseCurrencyCode: 'PKR', baseTotalAmount: null }));

    expect(component.missingRate).toBeTrue();
    expect(fixture.nativeElement.textContent).toContain('No EUR → PKR rate was on file');
  });

  it('shows a reversed invoice as Reversed with its reason, and offers nothing that would change its money', async () => {
    permissions = ['INVOICE_VIEW', 'INVOICE_PROCESS', 'PAYMENT_PROCESS'];
    await setup(invoice({ matchStatus: 'Reversed', reversedAt: '2026-09-30T09:00:00', reversalReason: 'Entered twice' }));

    expect(component.getMatchSeverity('Reversed')).toBe('contrast');
    const banner = query('invoice-reversal')?.textContent ?? '';
    expect(banner).toContain('Reversed');
    expect(banner).toContain('Entered twice');
    expect(banner).toContain('void it there by hand');
    expect(component.canApprove()).toBeFalse();
    expect(component.canReject()).toBeFalse();
    expect(component.canPay()).toBeFalse();
    expect(component.canSeeReverse()).toBeFalse();
    expect(component.canEditTax()).toBeFalse();
    expect(component.canEditSupplierNo()).toBeFalse();
    expect(query('invoice-reverse')).toBeNull();
    expect(actions()).not.toContain('Record Payment');
  });

  // ── What each status offers, to whom (server: INVOICE_VIEW reads, INVOICE_PROCESS changes) ──────────────

  const INVOICE_ACTIONS = ['Edit', 'Approve', 'Reject', 'Reverse', 'Upload Attachment', 'Record Payment'];
  const OFFERED_TO_PROCESSOR: Record<string, string[]> = {
    Pending:  ['Edit', 'Approve', 'Reject', 'Upload Attachment'],
    Matched:  ['Edit', 'Approve', 'Reject', 'Upload Attachment'],
    Variance: ['Edit', 'Approve', 'Reject', 'Upload Attachment'],
    Approved: ['Edit', 'Reverse', 'Upload Attachment'],
    Rejected: ['Upload Attachment'],
    Reversed: ['Edit', 'Upload Attachment']
  };

  for (const status of Object.keys(OFFERED_TO_PROCESSOR)) {
    const inv = () => invoice({
      matchStatus: status,
      approvedAt: status === 'Approved' || status === 'Reversed' ? '2026-09-20T10:00:00' : undefined,
      reversedAt: status === 'Reversed' ? '2026-09-30T09:00:00' : null
    });

    it(`${status}: an INVOICE_PROCESS holder is offered ${OFFERED_TO_PROCESSOR[status].join(', ')}`, async () => {
      await setup(inv());

      expect(actions().filter(a => INVOICE_ACTIONS.includes(a))).toEqual(jasmine.arrayWithExactContents(OFFERED_TO_PROCESSOR[status]));
      expect(actions()).toContain('Download PDF');
    });

    it(`${status}: an INVOICE_VIEW-only reader (Auditor) sees the invoice and its PDF, and no action the server would refuse`, async () => {
      permissions = ['INVOICE_VIEW'];
      await setup(inv());

      expect(fixture.nativeElement.textContent).toContain('INV-2026-00042');
      expect(actions().filter(a => INVOICE_ACTIONS.includes(a))).toEqual([]);
      expect(actions()).toContain('Download PDF');
      expect(component.canEdit()).withContext('canEdit').toBeFalse();
      expect(component.canApprove()).withContext('canApprove').toBeFalse();
      expect(component.canReject()).withContext('canReject').toBeFalse();
      expect(component.canUpload()).withContext('canUpload').toBeFalse();
      expect(component.canSeeReverse()).withContext('canSeeReverse').toBeFalse();
    });
  }

  it('offers Record Payment on an approved invoice to a PAYMENT_PROCESS holder only', async () => {
    permissions = ['INVOICE_VIEW', 'PAYMENT_PROCESS'];
    await setup(invoice());
    expect(actions()).toContain('Record Payment');

    permissions = ['INVOICE_VIEW', 'INVOICE_PROCESS'];
    await setup(invoice());
    expect(actions()).not.toContain('Record Payment');
  });

  // ── Payable only once Approved (SAP: the server refuses a payment on anything else with 400) ──────────

  describe('Record Payment, for a PAYMENT_PROCESS holder', () => {
    beforeEach(() => { permissions = ['INVOICE_VIEW', 'PAYMENT_PROCESS']; });

    for (const status of ['Pending', 'Matched', 'Variance', 'Rejected', 'Reversed']) {
      it(`is not offered on a ${status} invoice — only an approved one can be paid`, async () => {
        await setup(invoice({ matchStatus: status, approvedAt: status === 'Reversed' ? '2026-09-20T10:00:00' : undefined }));

        expect(component.canPay()).toBeFalse();
        expect(actions()).not.toContain('Record Payment');
      });
    }

    for (const paymentStatus of ['Unpaid', 'UNPAID', 'Scheduled', 'Partial', 'PARTIALLY_PAID']) {
      it(`is offered on an approved invoice that is ${paymentStatus}`, async () => {
        await setup(invoice({ paymentStatus, paidAmount: paymentStatus.toUpperCase().startsWith('PARTIAL') ? 500 : 0 }));

        expect(component.canPay()).toBeTrue();
        expect(actions()).toContain('Record Payment');
      });
    }

    for (const paymentStatus of ['Paid', 'FULLY_PAID', 'OVERPAID']) {
      it(`is not offered on an approved invoice that is ${paymentStatus}`, async () => {
        await setup(invoice({ paymentStatus, paidAmount: 1170 }));

        expect(component.canPay()).toBeFalse();
        expect(actions()).not.toContain('Record Payment');
      });
    }
  });

  // ── Three-way match: what this invoice bills, at PO prices (not the whole PO) ─────────────────────

  it('shows the match against what the invoice bills at PO prices, so a partial invoice matches its own lines', async () => {
    await setup(invoice({ matchStatus: 'Variance', approvedAt: undefined, subtotal: 400, matchedPoValue: 300, matchedGrnValue: 300, varianceAmount: 100 }));

    const card = query('invoice-match')?.textContent ?? '';
    expect(card).toContain('Expected at PO prices');
    expect(card).not.toContain('PO Value');
    expect(card).toContain('Variance vs expected');
    expect(card).toContain('only what this invoice bills');
    expect(card).toContain('more than was received and not yet invoiced');
    expect(query('invoice-match-expected')?.textContent?.trim()).toBe('300.00');
    expect(query('invoice-match-variance')?.textContent?.trim()).toBe('100.00');
  });

  it('shows no expected value or variance for an invoice with no purchase order', async () => {
    await setup(invoice({ poUuid: null, poNumber: null, matchStatus: 'Pending', approvedAt: undefined, matchedPoValue: 0, varianceAmount: 0 }));

    expect(query('invoice-match-expected')).toBeNull();
    expect(query('invoice-match-variance')).toBeNull();
  });

  // ── Payments unreadable (a custom role with INVOICE_VIEW but not PAYMENT_VIEW: GET supplier-payments is 403) ──

  describe('when the payments against it cannot be read', () => {
    async function setupUnreadable(inv: InvoiceDetailModel) {
      await setup(inv);
      finance.getSupplierPayments.and.returnValue(throwError(() => ({ status: 403 })));
      component.loadNewPayments(inv.uuid);
      fixture.detectChanges();
    }

    it('does not offer Reverse on a guess: disabled, saying the payments could not be checked', async () => {
      await setupUnreadable(invoice());

      expect(component.canSeeReverse()).toBeTrue();
      expect(component.reverseBlockedReason).toContain('could not be checked');
      expect((query('invoice-reverse')?.querySelector('button') as HTMLButtonElement | null)?.disabled).toBeTrue();
    });

    it('does not offer a tax or supplier-number change on a guess', async () => {
      await setupUnreadable(invoice({ matchStatus: 'Matched', approvedAt: undefined }));

      expect(component.canEdit()).toBeTrue();
      expect(component.canEditTax()).toBeFalse();
      expect(component.canEditSupplierNo()).toBeFalse();
      component.openEditDialog();
      component.editForm.supplierInvoiceNo = 'CHANGED';
      component.editForm.notes = 'Still editable';
      expect(component.buildPatch()).toEqual({ notes: 'Still editable' });
    });

    it('shows no error toast for it — the invoice itself is fine', async () => {
      await setupUnreadable(invoice());

      expect(toasts.calls.allArgs().filter(a => a[0]?.severity === 'error')).toEqual([]);
    });

    it('trusts a successful reload again', async () => {
      await setupUnreadable(invoice());
      finance.getSupplierPayments.and.returnValue(ok({ data: [], totalRecords: 0 }));
      component.loadNewPayments('inv-1');

      expect(component.reverseBlockedReason).toBeNull();
    });
  });

  it('does not offer Record Payment on an overpaid invoice', async () => {
    permissions = ['INVOICE_VIEW', 'INVOICE_PROCESS', 'PAYMENT_PROCESS'];
    await setup(invoice({ paymentStatus: 'OVERPAID', paidAmount: 1200 }));

    expect(component.canPay()).toBeFalse();
  });

  // ── Mirroring the server's rules ───────────────────────────────────────────

  it('lets an approved invoice be edited, but not its tax', async () => {
    await setup(invoice());

    expect(component.canEdit()).toBeTrue();
    expect(component.canEditTax()).toBeFalse();
    expect(component.canApprove()).toBeFalse();
    expect(component.canReject()).toBeFalse();
  });

  it('offers approval for Pending (no PO), Matched and Variance invoices', async () => {
    for (const status of ['Pending', 'Matched', 'Variance']) {
      await setup(invoice({ matchStatus: status, approvedAt: undefined }));
      expect(component.canApprove()).withContext(status).toBeTrue();
      expect(component.canEditTax()).withContext(status).toBeTrue();
    }
  });

  it('an approved invoice edit sends only what changed and never the tax', async () => {
    await setup(invoice());
    component.openEditDialog();
    component.editForm.notes = 'Paper copy received';
    component.editForm.dueDate = new Date(2026, 10, 1);

    expect(component.buildPatch()).toEqual({ notes: 'Paper copy received', dueDate: '2026-11-01' });
    expect(setupService.getTaxCodes).not.toHaveBeenCalled();
  });

  it('switching the code sends the code; removing it sends NO_TAX_CODE with the amount typed', async () => {
    await setup(invoice({ matchStatus: 'Matched', approvedAt: undefined }));
    component.openEditDialog();
    expect(setupService.getTaxCodes).toHaveBeenCalledWith('PURCHASE');

    component.editForm.taxCodeUuid = 'gst5';
    expect(component.editTaxPreview).toBe(50);
    expect(component.buildPatch()).toEqual({ taxCodeUuid: 'gst5' });

    component.editForm.taxCodeUuid = null;
    component.editForm.taxAmount = 80;
    expect(component.buildPatch()).toEqual({ taxCodeUuid: NO_TAX_CODE, taxAmount: 80 });
  });

  it('a paid-against invoice keeps its supplier number', async () => {
    await setup(invoice(), [payment('POSTED')]);
    component.openEditDialog();
    component.editForm.supplierInvoiceNo = 'CHANGED';

    expect(component.canEditSupplierNo()).toBeFalse();
    expect(component.buildPatch()).toEqual({});
  });

  it('a reversed invoice can still have its notes edited, and nothing else is offered or sent', async () => {
    await setup(invoice({ matchStatus: 'Reversed', reversedAt: '2026-09-30T09:00:00', reversalReason: 'Entered twice', notes: 'Old note' }));
    expect(component.canEdit()).toBeTrue();

    component.openEditDialog();
    fixture.detectChanges();

    expect(document.querySelector('[data-testid="invoice-edit-notes"]')).not.toBeNull();
    expect(document.querySelector('[data-testid="invoice-edit-reversed-note"]')?.textContent).toContain('only its notes');
    expect(document.querySelector('[data-testid="invoice-edit-supplier-no"]')).toBeNull();
    expect(document.querySelector('[data-testid="invoice-edit-due-date"]')).toBeNull();
    expect(document.querySelector('[data-testid="invoice-edit-payment-method"]')).toBeNull();
    expect(document.querySelector('[data-testid="invoice-edit-tax-code"]')).toBeNull();
    expect(setupService.getTaxCodes).not.toHaveBeenCalled();

    // Even were the other fields to change, only the notes go: the server refuses anything else with 409.
    component.editForm.notes = 'Duplicate of INV-2026-00041';
    component.editForm.supplierInvoiceNo = 'CHANGED';
    component.editForm.dueDate = new Date(2026, 11, 1);
    component.editForm.paymentMethod = 'Cash';
    component.editForm.taxCodeUuid = 'gst5';
    component.saveEdit();

    expect(finance.patchInvoice).toHaveBeenCalledOnceWith('inv-1', { notes: 'Duplicate of INV-2026-00041' });
  });

  it('caps the edit notes at the 300 characters the server keeps', async () => {
    await setup(invoice());
    component.openEditDialog();
    fixture.detectChanges();

    const notes = document.querySelector('[data-testid="invoice-edit-notes"]') as HTMLTextAreaElement | null;
    expect(notes?.maxLength).toBe(300);
    expect(document.querySelector('[data-testid="invoice-edit-notes-count"]')?.textContent?.trim()).toBe('0 / 300');

    component.editForm.notes = 'n'.repeat(301);
    component.saveEdit();
    expect(finance.patchInvoice).not.toHaveBeenCalled();
    expect(toasts.calls.mostRecent().args[0].severity).toBe('warn');

    component.editForm.notes = 'n'.repeat(300);
    component.saveEdit();
    expect(finance.patchInvoice).toHaveBeenCalledOnceWith('inv-1', { notes: 'n'.repeat(300) });
  });

  it('clearing the payment method sends it cleared, rather than silently keeping it', async () => {
    await setup(invoice({ paymentMethod: 'Cheque' }));
    component.openEditDialog();
    component.editForm.paymentMethod = null as unknown as string;   // what p-dropdown's clear (x) writes

    expect(component.buildPatch()).toEqual({ paymentMethod: '' });
  });

  // ── Approve / Reject: the server keeps at most 300 characters ─────────────────

  it('caps the approval notes at 300 characters, and does not wipe the notes with blanks', async () => {
    await setup(invoice({ matchStatus: 'Matched', approvedAt: undefined }));
    finance.approveInvoice.and.returnValue(ok(null));
    component.showApproveDialog = true;
    fixture.detectChanges();

    const input = document.querySelector('[data-testid="invoice-approve-notes"]') as HTMLInputElement | null;
    expect(input?.maxLength).toBe(300);

    component.approveNotes = 'a'.repeat(301);
    component.approve();
    expect(finance.approveInvoice).not.toHaveBeenCalled();
    expect(toasts.calls.mostRecent().args[0].severity).toBe('warn');

    component.approveNotes = '   ';
    component.approve();
    expect(finance.approveInvoice).toHaveBeenCalledOnceWith('inv-1', undefined);
  });

  it('caps the rejection reason at 300 characters', async () => {
    await setup(invoice({ matchStatus: 'Variance', approvedAt: undefined }));
    finance.rejectInvoice.and.returnValue(ok(null));
    component.showRejectDialog = true;
    fixture.detectChanges();

    const input = document.querySelector('[data-testid="invoice-reject-reason"]') as HTMLInputElement | null;
    expect(input?.maxLength).toBe(300);

    component.rejectReason = 'r'.repeat(301);
    component.reject();
    expect(finance.rejectInvoice).not.toHaveBeenCalled();

    component.rejectReason = '  Price is not the agreed one  ';
    component.reject();
    expect(finance.rejectInvoice).toHaveBeenCalledOnceWith('inv-1', 'Price is not the agreed one');
  });

  // ── Due date: the day the server holds, wherever the reader is ─────────────────

  describe('read by someone west of UTC', () => {
    const RealDate = Date;

    /**
     * Chrome on Windows ignores TZ, so a reader at UTC−05:00 is simulated: a string that names a UTC
     * instant (a Z or an offset, or a bare yyyy-MM-dd, which JS reads as UTC) is seen five hours behind
     * that instant, as such a reader's browser would; a local date-time string, and numeric parts, are
     * read as they are.
     */
    function readWestOfUtc() {
      class WestOfUtcDate extends RealDate {
        constructor(...args: unknown[]) {
          const a = args[0];
          if (args.length === 1 && typeof a === 'string' && /(Z|[+-]\d\d:?\d\d)$|^\d{4}-\d\d-\d\d$/.test(a)) {
            const instant = new RealDate(a).getTime();
            const hereEastOfUtcMin = -new RealDate(instant).getTimezoneOffset();
            super(instant - (hereEastOfUtcMin + 300) * 60_000);
          } else {
            super(...(args as []));
          }
        }
      }
      (window as any).Date = WestOfUtcDate;
    }

    afterEach(() => { (window as any).Date = RealDate; });

    for (const dueDate of ['2026-10-15T00:00:00', '2026-10-15T00:00:00Z', '2026-10-15']) {
      it(`shows a due date of ${dueDate} as the 15th, and saving it unchanged sends no due date`, async () => {
        await setup(invoice({ dueDate, notes: 'n' }));
        readWestOfUtc();

        component.openEditDialog();
        const shown = component.editForm.dueDate!;
        const patch = component.buildPatch();
        (window as any).Date = RealDate;

        expect([shown.getFullYear(), shown.getMonth() + 1, shown.getDate()]).toEqual([2026, 10, 15]);
        expect(patch).toEqual({});
      });
    }
  });

  // ── Reverse ────────────────────────────────────────────────────────────────

  it('offers Reverse on an approved, unpaid invoice to an INVOICE_PROCESS holder', async () => {
    await setup(invoice());

    expect(component.canSeeReverse()).toBeTrue();
    expect(component.reverseBlockedReason).toBeNull();
    expect(query('invoice-reverse')).not.toBeNull();
  });

  it('does not offer Reverse without INVOICE_PROCESS', async () => {
    permissions = ['INVOICE_VIEW'];
    await setup(invoice());

    expect(query('invoice-reverse')).toBeNull();
  });

  it('disables Reverse, saying why, once anything is paid or a note deducted', async () => {
    await setup(invoice(), [payment('DRAFT')]);
    expect(component.reverseBlockedReason).toContain('Only an unpaid invoice');

    await setup(invoice({ paidAmount: 100 }));
    expect(component.reverseBlockedReason).toContain('Only an unpaid invoice');

    await setup(invoice({ creditNotes: [{ uuid: 'cn', creditNoteNumber: 'CN-1' } as any] }));
    expect(component.reverseBlockedReason).toContain('credit or debit note');

    await setup(invoice(), [payment('CANCELLED'), payment('BOUNCED')]);
    expect(component.reverseBlockedReason).toBeNull();
  });

  it('asks for a reason, confirms, reverses and shows the result', async () => {
    await setup(invoice());
    component.openReverseDialog();

    component.reverseReason = '   ';
    component.confirmReverse();
    expect(finance.reverseInvoice).not.toHaveBeenCalled();
    expect(toasts.calls.mostRecent().args[0].severity).toBe('warn');

    component.reverseReason = '  Entered twice  ';
    component.confirmReverse();

    expect(confirm).toHaveBeenCalled();
    expect(confirm.calls.mostRecent().args[0].message).toContain('1170.00 PKR');
    expect(finance.reverseInvoice).toHaveBeenCalledWith('inv-1', 'Entered twice');
    expect(component.showReverseDialog).toBeFalse();
    expect(toasts.calls.mostRecent().args[0].severity).toBe('success');
    expect(finance.getInvoiceById).toHaveBeenCalledTimes(2);
  });

  it("shows the server's refusal when the reversal is refused", async () => {
    await setup(invoice());
    finance.reverseInvoice.and.returnValue(throwError(() => ({ status: 409, error: { message: 'Invoice INV-2026-00042 is on payment SPAY-2026-00001.' } })));
    component.openReverseDialog();
    component.reverseReason = 'Entered twice';

    component.confirmReverse();

    const toast = toasts.calls.mostRecent().args[0];
    expect(toast.severity).toBe('error');
    expect(toast.detail).toContain('is on payment SPAY-2026-00001');
    expect(component.showReverseDialog).toBeTrue();
  });

  it('refuses a reason longer than the server keeps', async () => {
    await setup(invoice());
    component.reverseReason = 'r'.repeat(501);

    component.confirmReverse();

    expect(confirm).not.toHaveBeenCalled();
    expect(finance.reverseInvoice).not.toHaveBeenCalled();
  });
});
