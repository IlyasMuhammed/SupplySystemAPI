import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of } from 'rxjs';

import { SalesInvoiceDetailComponent } from './sales-invoice-detail/sales-invoice-detail.component';
import { SalesInvoiceListComponent } from './sales-invoice-list/sales-invoice-list.component';
import {
  SalesInvoiceDetailModel, SalesInvoiceListItemModel, SalesInvoiceService, isCreditNote, isPosInvoice
} from '../../../services/sales-invoice.service';
import { LogisticsService } from '../../../services/logistics.service';
import { AttachmentService } from '../../../services/attachment.service';
import { PosAdminService } from '../../../services/pos-admin.service';
import { AuthService } from '../../service/auth.service';

const ok = (result: unknown) => of({ success: true, message: '', result } as any);

function detail(over: Partial<SalesInvoiceDetailModel> = {}): SalesInvoiceDetailModel {
  return {
    uuid: 'inv-1', invoiceNumber: 'SINV-2026-00001', saleOrderUuid: null, saleOrderNumber: null, sourceType: 'POS',
    sourceNumber: 'POS-S1-20261010-0001', deliveryUuid: null, deliveryNumber: null, partnerId: 'p-1', partnerName: 'Walk-in customer',
    invoiceDate: '2026-10-10T00:00:00Z', dueDate: '2026-10-10T00:00:00Z', grandTotal: 234, amountPaid: 0, balanceDue: 234,
    status: 'ISSUED', currencyCode: 'PKR', traceId: 't', subtotal: 200, discountAmount: 0, taxAmount: 34, notes: null,
    createdDate: '2026-10-10T10:00:00Z', payments: [],
    lines: [{ lineNo: 1, soLineUuid: null, variantUuid: 'v-1', description: 'Tea', quantity: 2, unitPrice: 117, discountPercent: 0,
              taxPercent: 17, lineTotal: 234, discountAmount: 10, taxAmount: 34 }],
    ...over
  } as SalesInvoiceDetailModel;
}

/** A38 — sales invoices from POS (no sale order) and customer credit notes (SCN-…). */
describe('SalesInvoiceDetailComponent — A38 POS invoices and credit notes', () => {
  let fixture: ComponentFixture<SalesInvoiceDetailComponent>;
  let component: SalesInvoiceDetailComponent;
  let pos: jasmine.SpyObj<PosAdminService>;
  let permissions: string[];

  const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  async function setup(model: SalesInvoiceDetailModel, perms = ['SALES_INVOICE_VIEW', 'SALES_INVOICE_MANAGE', 'SALE_ORDER_VIEW', 'POS_ORDER_VIEW'],
                       posOrders = [{ uuid: 'o-1', orderNumber: 'POS-S1-20261010-0001' }]) {
    permissions = perms;
    const invoices = jasmine.createSpyObj<SalesInvoiceService>('SalesInvoiceService', ['getInvoice', 'cancelInvoice']);
    invoices.getInvoice.and.returnValue(ok(model));
    const attachments = jasmine.createSpyObj<AttachmentService>('AttachmentService', ['getAttachments', 'resolveUrl', 'isApiUrl', 'download']);
    attachments.getAttachments.and.returnValue(ok([]));
    pos = jasmine.createSpyObj<PosAdminService>('PosAdminService', ['getOrders']);
    pos.getOrders.and.returnValue(ok({ data: posOrders, totalRecords: posOrders.length }));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [SalesInvoiceDetailComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), MessageService,
        { provide: SalesInvoiceService, useValue: invoices },
        { provide: AttachmentService, useValue: attachments },
        { provide: PosAdminService, useValue: pos },
        { provide: AuthService, useValue: { hasPermission: (c: string) => permissions.includes(c) } },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['uuid', model.uuid]]) } } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(SalesInvoiceDetailComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  it('POS invoice: POS pill, the order resolved by number and linked; no sale-order link; no Cancel', async () => {
    await setup(detail());
    expect(pos.getOrders).toHaveBeenCalledWith(jasmine.objectContaining({ search: 'POS-S1-20261010-0001' }));
    expect(q('pos-pill')).not.toBeNull();
    expect(q('pos-order-link')!.getAttribute('href')).toContain('/portal/pages/pos/orders/o-1');
    expect(q('order-link')).toBeNull();
    expect(component.canCancel).toBeFalse();
    expect(q('action-cancel')).toBeNull();
  });

  it('POS invoice whose order is not found: links to the orders list searched by number; without POS_ORDER_VIEW the number only', async () => {
    await setup(detail(), undefined, []);
    expect(q('pos-order-search-link')!.getAttribute('href')).toContain('search=POS-S1-20261010-0001');
    await setup(detail(), ['SALES_INVOICE_VIEW']);
    expect(pos.getOrders).not.toHaveBeenCalled();
    expect(q('pos-order-number')!.textContent).toContain('POS-S1-20261010-0001');
  });

  it('a sale-order invoice is unchanged; a null saleOrderUuid shows no broken link', async () => {
    await setup(detail({ sourceType: 'SALE_ORDER', sourceNumber: null, saleOrderUuid: 'so-1', saleOrderNumber: 'SO-1' }));
    expect(q('order-link')!.getAttribute('href')).toContain('/portal/pages/sales/orders/so-1');
    expect(q('action-cancel')).not.toBeNull();
    await setup(detail({ sourceType: null, sourceNumber: null, saleOrderUuid: null, saleOrderNumber: null }));
    expect(q('order-link')).toBeNull();
    expect(q('invoice-kicker')!.textContent).not.toContain('undefined');
  });

  it('credit note: shown as a credit note with a link to the original invoice; no Cancel', async () => {
    await setup(detail({ uuid: 'scn-1', invoiceNumber: 'SCN-2026-00001', status: 'CREDIT_NOTE', originalInvoiceUuid: 'inv-1',
                         originalInvoiceNumber: 'SINV-2026-00001', balanceDue: 0 }));
    expect(q('invoice-kicker')!.textContent).toContain('Credit note');
    expect(q('original-invoice-link')!.getAttribute('href')).toContain('/portal/pages/finance/sales-invoices/inv-1');
    expect(q('original-invoice-link')!.textContent).toContain('SINV-2026-00001');
    expect(component.stages.map(s => s.label)).toEqual(['Issued', 'Credited']);
    expect(q('action-cancel')).toBeNull();
  });

  it('returnedAmount above 0 is shown on the original invoice; line discount and tax amounts when present', async () => {
    await setup(detail({ returnedAmount: 117 }));
    expect(q('returned-amount')!.textContent).toContain('117.00');
    expect(q('line-discount-amount')!.textContent).toContain('10.00');
    expect(q('line-tax-amount')!.textContent).toContain('34.00');
    await setup(detail({ returnedAmount: 0, lines: [{ ...detail().lines[0], discountAmount: null, taxAmount: null }] }));
    expect(q('returned-amount')).toBeNull();
    expect(q('line-discount-amount')).toBeNull();
    expect(q('line-tax-amount')).toBeNull();
  });

  it('helpers', () => {
    expect(isPosInvoice({ sourceType: 'POS' })).toBeTrue();
    expect(isPosInvoice({ sourceType: null })).toBeFalse();
    expect(isCreditNote({ status: 'CREDIT_NOTE' })).toBeTrue();
  });
});

describe('SalesInvoiceListComponent — A38 POS invoices and credit notes', () => {
  let fixture: ComponentFixture<SalesInvoiceListComponent>;

  const row = (over: Partial<SalesInvoiceListItemModel>): SalesInvoiceListItemModel => ({
    uuid: 'inv-1', invoiceNumber: 'SINV-1', saleOrderUuid: 'so-1', saleOrderNumber: 'SO-1', partnerId: 'p', partnerName: 'Acme',
    invoiceDate: '2026-10-10', dueDate: '2026-10-10', grandTotal: 1, amountPaid: 0, balanceDue: 1, status: 'ISSUED', currencyCode: 'PKR', ...over
  });

  async function setup(list: SalesInvoiceListItemModel[]) {
    const invoices = jasmine.createSpyObj<SalesInvoiceService>('SalesInvoiceService', ['getInvoices']);
    invoices.getInvoices.and.returnValue(ok({ data: list, totalRecords: list.length, page: 1, pageSize: 20, totalPages: 1 }));
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [SalesInvoiceListComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), MessageService,
        { provide: SalesInvoiceService, useValue: invoices },
        { provide: LogisticsService, useValue: { getDeliveries: () => ok({ data: [] }) } },
        { provide: AuthService, useValue: { hasPermission: () => true } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(SalesInvoiceListComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  const cells = () => Array.from(fixture.nativeElement.querySelectorAll('[data-testid="source"]')) as HTMLElement[];

  it('shows the sale order, a POS pill + order link, and a credit note with its original invoice', async () => {
    await setup([
      row({}),
      row({ uuid: 'inv-2', invoiceNumber: 'SINV-2', saleOrderUuid: null, saleOrderNumber: null, sourceType: 'POS', sourceNumber: 'POS-S1-1' }),
      row({ uuid: 'scn-1', invoiceNumber: 'SCN-1', status: 'CREDIT_NOTE', saleOrderUuid: null, saleOrderNumber: null, sourceType: 'POS',
            sourceNumber: 'RET-S1-1', originalInvoiceUuid: 'inv-2', originalInvoiceNumber: 'SINV-2' })
    ]);
    const c = cells();
    expect(c[0].textContent).toContain('SO-1');
    expect(c[1].querySelector('[data-testid="pos-pill"]')).not.toBeNull();
    expect(c[1].querySelector('[data-testid="pos-order-link"]')!.getAttribute('href')).toContain('search=POS-S1-1');
    expect(c[2].querySelector('[data-testid="original-invoice-link"]')!.getAttribute('href')).toContain('/portal/pages/finance/sales-invoices/inv-2');
    expect(fixture.nativeElement.querySelector('[data-testid="credit-note-pill"]')).not.toBeNull();
  });

  it('no sale order and no source shows a dash', async () => {
    await setup([row({ saleOrderUuid: null, saleOrderNumber: null })]);
    expect(cells()[0].textContent!.trim()).toBe('—');
  });
});
