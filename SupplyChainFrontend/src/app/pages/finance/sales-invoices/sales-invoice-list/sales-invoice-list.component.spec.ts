import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, Router } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError, Subject } from 'rxjs';

import { SalesInvoiceListComponent } from './sales-invoice-list.component';
import { SalesInvoiceService, SalesInvoiceListItemModel } from '../../../../services/sales-invoice.service';
import { LogisticsService } from '../../../../services/logistics.service';
import { AuthService } from '../../../service/auth.service';
import { INVOICE_STATUS_SEVERITY } from '../../receivables/receivables.shared';

function invoice(overrides: Partial<SalesInvoiceListItemModel> = {}): SalesInvoiceListItemModel {
  return {
    uuid: 'inv-1', invoiceNumber: 'SINV-20260920-0001', saleOrderUuid: 'so-1', saleOrderNumber: 'SO-2026-00042',
    deliveryUuid: 'd-1', deliveryNumber: 'DLV-2026-00001', partnerId: 'p-1', partnerName: 'Acme Ltd',
    invoiceDate: '2026-09-20T00:00:00Z', dueDate: '2026-10-20T00:00:00Z',
    grandTotal: 1000, amountPaid: 0, balanceDue: 1000, status: 'ISSUED', currencyCode: 'PKR',
    ...overrides
  };
}

function ok<T>(result: T) {
  return of({ success: true, message: '', result } as any);
}

describe('SalesInvoiceListComponent', () => {
  let fixture: ComponentFixture<SalesInvoiceListComponent>;
  let component: SalesInvoiceListComponent;
  let invoices: jasmine.SpyObj<SalesInvoiceService>;
  let logistics: jasmine.SpyObj<LogisticsService>;
  let router: Router;
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

  async function setup(list: SalesInvoiceListItemModel[] = [invoice(), invoice({ uuid: 'inv-2', invoiceNumber: 'SINV-20260920-0002', status: 'PAID', partnerName: 'Globex Corp' })]) {
    invoices = jasmine.createSpyObj<SalesInvoiceService>('SalesInvoiceService', ['getInvoices', 'createFromDelivery', 'downloadPdf']);
    invoices.getInvoices.and.returnValue(ok({ data: list, totalRecords: list.length, page: 1, pageSize: 20, totalPages: 1 }));
    invoices.createFromDelivery.and.returnValue(ok({ invoiceUuid: 'new-inv', invoiceNumber: 'SINV-20260921-0001', grandTotal: 500, currencyCode: 'PKR', alreadyExisted: false }));
    invoices.downloadPdf.and.returnValue(of(new Blob(['%PDF'])));

    logistics = jasmine.createSpyObj<LogisticsService>('LogisticsService', ['getDeliveries']);
    logistics.getDeliveries.and.returnValue(ok({
      data: [
        { uuid: 'd-1', deliveryNumber: 'DLV-2026-00001', sourceNumber: 'SO-2026-00042' },
        { uuid: 'd-2', deliveryNumber: 'DLV-2026-00002' }
      ],
      totalRecords: 2, page: 1, pageSize: 20, totalPages: 1
    }));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [SalesInvoiceListComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), MessageService,
        { provide: SalesInvoiceService, useValue: invoices },
        { provide: LogisticsService, useValue: logistics },
        { provide: AuthService, useValue: auth }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(SalesInvoiceListComponent);
    component = fixture.componentInstance;
    router = TestBed.inject(Router);
    navigate = spyOn(router, 'navigate').and.resolveTo(true);
    toasts = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
  }

  beforeEach(() => { permissions = ['SALES_INVOICE_VIEW']; });

  // ── The list ───────────────────────────────────────────────────────────────

  it('loads the first page through the table and renders the invoices', async () => {
    await setup();
    fixture.detectChanges();

    expect(invoices.getInvoices).toHaveBeenCalledOnceWith(jasmine.objectContaining({ page: 1, pageSize: 20 }));
    fixture.detectChanges();
    const text = fixture.nativeElement.textContent;
    expect(text).toContain('SINV-20260920-0001');
    expect(text).toContain('SINV-20260920-0002');
    expect(text).toContain('Globex Corp');
    expect(text).toContain('SO-2026-00042');
    expect(text).toContain('DLV-2026-00001');
    expect(component.isLoading).toBeFalse();
  });

  it('shows a dash for an invoice with no delivery', async () => {
    await setup([invoice({ deliveryUuid: null, deliveryNumber: null })]);
    fixture.detectChanges();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('tbody').textContent).not.toContain('DLV-');
  });

  it('sends only the filters that are set', async () => {
    await setup();
    component.selectedStatus = 'OVERDUE';
    component.load();

    const filter = invoices.getInvoices.calls.mostRecent().args[0]!;
    expect(filter.status).toBe('OVERDUE');
    expect(filter.search).toBeUndefined();
    expect(filter.dateFrom).toBeUndefined();
    expect(filter.dateTo).toBeUndefined();
  });

  it('sends the date range as calendar days', async () => {
    await setup();
    component.dateFrom = new Date(2026, 8, 1);
    component.dateTo = new Date(2026, 8, 30, 23, 59);
    component.load();

    const filter = invoices.getInvoices.calls.mostRecent().args[0]!;
    expect([filter.dateFrom, filter.dateTo]).toEqual(['2026-09-01', '2026-09-30']);
  });

  it('reloads from the first page when a filter changes', async () => {
    await setup();
    component.currentPage = 4;
    component.selectedStatus = 'PAID';

    component.onFilterChange();

    expect(invoices.getInvoices).toHaveBeenCalledWith(jasmine.objectContaining({ page: 1, status: 'PAID' }));
  });

  it('offers every status the server knows as a filter', async () => {
    await setup();

    expect(component.statusOptions.map(o => o.value))
      .toEqual(['', 'DRAFT', 'ISSUED', 'PARTIALLY_PAID', 'PAID', 'OVERDUE', 'CANCELLED', 'CREDIT_NOTE']);
  });

  it('clears every filter and starts again', async () => {
    await setup();
    component.searchText = 'acme';
    component.selectedStatus = 'PAID';
    component.dateFrom = new Date(2026, 8, 1);
    component.dateTo = new Date(2026, 8, 30);
    component.currentPage = 3;

    component.resetFilters();

    expect([component.searchText, component.selectedStatus, component.currentPage]).toEqual(['', '', 1]);
    expect(component.dateFrom).toBeNull();
    expect(component.dateTo).toBeNull();
    expect(invoices.getInvoices.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({ page: 1, status: undefined }));
  });

  it('follows the table to another page and page size', async () => {
    await setup();

    component.onPageChange({ first: 40, rows: 20 });

    expect(invoices.getInvoices).toHaveBeenCalledWith(jasmine.objectContaining({ page: 3, pageSize: 20 }));
  });

  it('empties the list and shows the servers reason when the request fails', async () => {
    await setup();
    invoices.getInvoices.and.returnValue(throwError(() => ({ error: { message: 'The date range is backwards.' } })));

    component.load();

    expect(component.isLoading).toBeFalse();
    expect(component.invoices).toEqual([]);
    expect(lastToast().detail).toBe('The date range is backwards.');
  });

  it('falls back to a general message when the failure has none', async () => {
    await setup();
    invoices.getInvoices.and.returnValue(throwError(() => ({ status: 0 })));

    component.load();

    expect(lastToast().detail).toBe('Failed to load sales invoices.');
  });

  it('maps every status the server can return to a severity', () => {
    for (const status of ['DRAFT', 'ISSUED', 'PARTIALLY_PAID', 'PAID', 'OVERDUE', 'CANCELLED', 'CREDIT_NOTE'])
      expect(INVOICE_STATUS_SEVERITY[status]).withContext(status).toBeDefined();
  });

  it('marks an overdue invoice as danger and a paid one as success', async () => {
    await setup();

    expect(component.getStatusSeverity('OVERDUE')).toBe('danger');
    expect(component.getStatusSeverity('PAID')).toBe('success');
    expect(component.getStatusSeverity('SOMETHING_NEW')).toBe('secondary');
  });

  // ── PDF ────────────────────────────────────────────────────────────────────

  it('opens the pdf of the invoice whose button was pressed', async () => {
    await setup();
    fixture.detectChanges();
    fixture.detectChanges();

    fixture.nativeElement.querySelector('[data-testid="open-pdf"] button').click();
    fixture.detectChanges();

    expect(component.pdfVisible).toBeTrue();
    expect(component.pdfInvoice!.uuid).toBe('inv-1');
    expect(invoices.downloadPdf).toHaveBeenCalledOnceWith('inv-1');
  });

  // ── New invoice ────────────────────────────────────────────────────────────

  it('offers a new invoice only to someone who may raise one and may find a delivery', async () => {
    await setup();
    fixture.detectChanges();
    expect(query('new-invoice')).toBeNull();

    permissions = ['SALES_INVOICE_VIEW', 'SALES_INVOICE_MANAGE'];
    await setup();
    fixture.detectChanges();
    expect(query('new-invoice')).withContext('cannot pick a delivery without DELIVERY_VIEW').toBeNull();

    permissions = ['SALES_INVOICE_VIEW', 'DELIVERY_VIEW'];
    await setup();
    fixture.detectChanges();
    expect(query('new-invoice')).withContext('cannot raise without SALES_INVOICE_MANAGE').toBeNull();

    permissions = ['SALES_INVOICE_MANAGE', 'DELIVERY_VIEW'];
    await setup();
    fixture.detectChanges();
    expect(query('new-invoice')).not.toBeNull();
  });

  it('will not open the dialog for someone who may not raise invoices', async () => {
    await setup();

    component.openNewDialog();

    expect(component.newDialogVisible).toBeFalse();
  });

  it('searches only deliveries that reached the customer for a sale order, by what was typed', async () => {
    permissions = ['SALES_INVOICE_MANAGE', 'DELIVERY_VIEW'];
    await setup();

    component.searchDeliveries({ query: 'DLV-2026' } as any);

    expect(logistics.getDeliveries).toHaveBeenCalledOnceWith({
      status: 'DELIVERED', direction: 'OUTBOUND', sourceType: 'SALE_ORDER', search: 'DLV-2026', pageSize: 20
    });
    expect(component.deliverySuggestions).toEqual([
      { uuid: 'd-1', label: 'DLV-2026-00001 · SO-2026-00042' },
      { uuid: 'd-2', label: 'DLV-2026-00002' }
    ]);
  });

  it('offers nothing when the delivery search fails', async () => {
    permissions = ['SALES_INVOICE_MANAGE', 'DELIVERY_VIEW'];
    await setup();
    logistics.getDeliveries.and.returnValue(throwError(() => ({ status: 500 })));

    component.searchDeliveries({ query: 'x' } as any);

    expect(component.deliverySuggestions).toEqual([]);
  });

  it('does not take text typed into the box for a delivery', async () => {
    permissions = ['SALES_INVOICE_MANAGE', 'DELIVERY_VIEW'];
    await setup();
    component.openNewDialog();
    component.deliverySearch = 'DLV-2026-0000';

    expect(component.chosenDelivery).toBeNull();
    component.createInvoice();
    expect(invoices.createFromDelivery).not.toHaveBeenCalled();
  });

  it('raises the invoice for the chosen delivery, and opens it', async () => {
    permissions = ['SALES_INVOICE_MANAGE', 'DELIVERY_VIEW'];
    await setup();
    component.openNewDialog();
    component.deliverySearch = { uuid: 'd-1', label: 'DLV-2026-00001 · SO-2026-00042' };

    component.createInvoice();

    expect(invoices.createFromDelivery).toHaveBeenCalledOnceWith('d-1');
    expect(navigate).toHaveBeenCalledWith(['/portal/pages/finance/sales-invoices', 'new-inv']);
    expect(component.newDialogVisible).toBeFalse();
    expect(component.isCreating).toBeFalse();
    expect(lastToast().severity).toBe('success');
  });

  it('says so, without alarm, when the delivery already had an invoice, and opens that one', async () => {
    permissions = ['SALES_INVOICE_MANAGE', 'DELIVERY_VIEW'];
    await setup();
    invoices.createFromDelivery.and.returnValue(of({
      success: true, message: 'An invoice already exists for this delivery: SINV-20260920-0001.',
      result: { invoiceUuid: 'inv-1', invoiceNumber: 'SINV-20260920-0001', grandTotal: 1000, currencyCode: 'PKR', alreadyExisted: true }
    } as any));
    component.openNewDialog();
    component.deliverySearch = { uuid: 'd-1', label: 'x' };

    component.createInvoice();

    expect(lastToast().severity).toBe('info');
    expect(lastToast().summary).toBe('Already invoiced');
    expect(lastToast().detail).toContain('SINV-20260920-0001');
    expect(navigate).toHaveBeenCalledWith(['/portal/pages/finance/sales-invoices', 'inv-1']);
  });

  it('shows the servers reason when the invoice is refused, and keeps the dialog open', async () => {
    permissions = ['SALES_INVOICE_MANAGE', 'DELIVERY_VIEW'];
    await setup();
    invoices.createFromDelivery.and.returnValue(throwError(() => ({
      error: { message: 'Delivery DLV-2026-00001 is PACKED. An invoice can only be raised for goods that have reached the customer.' }
    })));
    component.openNewDialog();
    component.deliverySearch = { uuid: 'd-1', label: 'x' };

    component.createInvoice();

    expect(lastToast().severity).toBe('error');
    expect(lastToast().detail).toContain('is PACKED');
    expect(component.newDialogVisible).toBeTrue();
    expect(component.isCreating).toBeFalse();
    expect(navigate).not.toHaveBeenCalled();
  });

  it('raises one invoice however often the button is pressed while it is working', async () => {
    permissions = ['SALES_INVOICE_MANAGE', 'DELIVERY_VIEW'];
    await setup();
    invoices.createFromDelivery.and.returnValue(new Subject<any>());
    component.openNewDialog();
    component.deliverySearch = { uuid: 'd-1', label: 'x' };

    component.createInvoice();
    component.createInvoice();

    expect(invoices.createFromDelivery).toHaveBeenCalledTimes(1);
  });

  it('starts the dialog empty each time it opens', async () => {
    permissions = ['SALES_INVOICE_MANAGE', 'DELIVERY_VIEW'];
    await setup();
    component.openNewDialog();
    component.deliverySearch = { uuid: 'd-1', label: 'x' };
    component.deliverySuggestions = [{ uuid: 'd-1', label: 'x' }];

    component.openNewDialog();

    expect(component.deliverySearch).toBeNull();
    expect(component.deliverySuggestions).toEqual([]);
  });
});
