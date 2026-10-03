import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of } from 'rxjs';

import { SalesInvoiceDetailComponent } from './sales-invoices/sales-invoice-detail/sales-invoice-detail.component';
import { InvoiceDetailComponent } from './invoices/invoice-detail/invoice-detail.component';
import { SalesInvoiceService, SalesInvoiceDetailModel } from '../../services/sales-invoice.service';
import { AttachmentService } from '../../services/attachment.service';
import { FinanceService, InvoiceDetailModel } from '../../services/finance.service';
import { FinanceSetupService } from '../../services/finance-setup.service';
import { AuthService } from '../service/auth.service';

/**
 * Cross-page consistency (SAP alignment, S-5): a final document in a foreign currency shows its value in the
 * organization's base currency at the rate fixed when it became final — the sales invoice at issue, the
 * supplier invoice at approval — and both say so in the same way when no rate was on file.
 */

function ok<T>(result: T) {
  return of({ success: true, message: '', result } as any);
}

const auth = { hasPermission: () => true, hasAnyPermission: () => true } as unknown as AuthService;

async function salesPage(overrides: Partial<SalesInvoiceDetailModel>): Promise<HTMLElement> {
  const inv = {
    uuid: 'si-1', invoiceNumber: 'SINV-1', saleOrderUuid: 'so-1', saleOrderNumber: 'SO-1', partnerId: 'p-1', partnerName: 'Acme',
    invoiceDate: '2026-09-20T00:00:00', dueDate: '2026-10-20T00:00:00', grandTotal: 1170, amountPaid: 0, balanceDue: 1170,
    status: 'ISSUED', currencyCode: 'USD', traceId: 't', subtotal: 1000, discountAmount: 0, taxAmount: 170,
    createdDate: '2026-09-20T09:00:00', lines: [], payments: [], ...overrides
  } as SalesInvoiceDetailModel;
  const invoices = jasmine.createSpyObj<SalesInvoiceService>('SalesInvoiceService',
    ['getInvoice', 'issueInvoice', 'updateInvoice', 'deleteInvoice', 'attachPdf', 'downloadPdf', 'cancelInvoice']);
  invoices.getInvoice.and.returnValue(ok(inv));
  const attachments = jasmine.createSpyObj<AttachmentService>('AttachmentService', ['getAttachments', 'resolveUrl', 'isApiUrl', 'download']);
  attachments.getAttachments.and.returnValue(ok([]));

  await TestBed.resetTestingModule().configureTestingModule({
    imports: [SalesInvoiceDetailComponent],
    providers: [
      provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), MessageService,
      { provide: SalesInvoiceService, useValue: invoices },
      { provide: AttachmentService, useValue: attachments },
      { provide: AuthService, useValue: auth },
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['uuid', 'si-1']]) } } }
    ]
  }).compileComponents();
  const fixture = TestBed.createComponent(SalesInvoiceDetailComponent);
  spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

async function supplierPage(overrides: Partial<InvoiceDetailModel>): Promise<HTMLElement> {
  const finance = jasmine.createSpyObj<FinanceService>('FinanceService',
    ['getInvoiceById', 'getSupplierPayments', 'reverseInvoice', 'patchInvoice', 'approveInvoice', 'rejectInvoice', 'resolveFileUrl', 'downloadInvoicePdf']);
  finance.getInvoiceById.and.returnValue(ok({
    uuid: 'inv-1', invoiceNumber: 'INV-1', supplierId: 'sup-1', supplierName: 'Karachi Steel', poUuid: 'po-1', poNumber: 'PO-1',
    invoiceDate: '2026-09-15T00:00:00', receivedDate: '2026-09-16T00:00:00', dueDate: '2026-10-15T00:00:00', currency: 'USD',
    subtotal: 1000, taxAmount: 170, totalAmount: 1170, matchedPoValue: 1000, matchedGrnValue: 1000, varianceAmount: 0,
    matchStatus: 'Approved', paymentStatus: 'Unpaid', paidAmount: 0, approvedAt: '2026-09-20T10:00:00',
    createdDate: '2026-09-16T10:00:00', lines: [], payments: [], debitNotes: [], creditNotes: [], ...overrides
  }));
  finance.getSupplierPayments.and.returnValue(ok({ data: [], totalRecords: 0 }));
  const setup = jasmine.createSpyObj<FinanceSetupService>('FinanceSetupService', ['getTaxCodes']);
  setup.getTaxCodes.and.returnValue(ok([]));

  await TestBed.resetTestingModule().configureTestingModule({
    imports: [InvoiceDetailComponent],
    providers: [
      provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(),
      { provide: FinanceService, useValue: finance },
      { provide: FinanceSetupService, useValue: setup },
      { provide: AuthService, useValue: auth },
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ uuid: 'inv-1' }) } } }
    ]
  }).compileComponents();
  const fixture = TestBed.createComponent(InvoiceDetailComponent);
  fixture.detectChanges();
  return fixture.nativeElement as HTMLElement;
}

const text = (el: HTMLElement) => (el.textContent ?? '').replace(/\s+/g, ' ');

describe('SAP alignment consistency: base-currency snapshot on the sales and supplier invoice', () => {
  it('both show the base-currency value and the rate fixed when the document became final', async () => {
    const sales = await salesPage({ exchangeRate: 278.5, baseCurrencyCode: 'PKR', baseGrandTotal: 325845 });
    const salesRow = text(sales.querySelector('[data-testid="base-total"]') as HTMLElement);
    expect(salesRow).toContain('325,845.00');
    expect(salesRow).toContain('278.5');

    const supplier = await supplierPage({ exchangeRate: 278.5, baseCurrencyCode: 'PKR', baseTotalAmount: 325845 });
    const supplierRow = text(supplier.querySelector('[data-testid="invoice-base-total"]') as HTMLElement);
    expect(supplierRow).toContain('325,845.00');
    expect(supplierRow).toContain('278.5');
  });

  it('neither shows a base-currency row for a document already in the base currency', async () => {
    const sales = await salesPage({ currencyCode: 'PKR', exchangeRate: 1, baseCurrencyCode: 'PKR', baseGrandTotal: 1170 });
    expect(sales.querySelector('[data-testid="base-total"]')).toBeNull();

    const supplier = await supplierPage({ currency: 'PKR', exchangeRate: 1, baseCurrencyCode: 'PKR', baseTotalAmount: 1170 });
    expect(supplier.querySelector('[data-testid="invoice-base-total"]')).toBeNull();
  });

  it('both say so when the document became final in a foreign currency with no rate on file', async () => {
    // The supplier invoice keeps BaseCurrencyCode when no rate is on file (FinanceRepository.SnapshotExchangeRateAsync)
    // and its page says "No USD → PKR rate was on file when it was approved". The sales invoice should read the same.
    const supplier = await supplierPage({ exchangeRate: null, baseCurrencyCode: 'PKR', baseTotalAmount: null });
    expect(text(supplier)).withContext('supplier invoice').toContain('No USD → PKR rate was on file');

    const sales = await salesPage({ exchangeRate: null, baseCurrencyCode: 'PKR', baseGrandTotal: null });
    expect(text(sales)).withContext('sales invoice').toContain('No USD → PKR rate was on file');
  });
});
