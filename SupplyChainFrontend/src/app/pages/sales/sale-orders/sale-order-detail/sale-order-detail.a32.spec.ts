import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute, convertToParamMap } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { Tooltip } from 'primeng/tooltip';
import { BehaviorSubject, of, throwError } from 'rxjs';

import {
  SaleOrderDetailComponent, RESERVE_PERMISSION_TOOLTIP, RELEASE_PERMISSION_TOOLTIP
} from './sale-order-detail.component';
import {
  SaleOrderService, SaleOrderModel, SaleOrderLineModel, SaleOrderLineReservationModel, CustomerPoDuplicateModel
} from '../../../../services/sale-order.service';
import { BusinessPartnerService } from '../../../../services/business-partner.service';
import { SalesInvoiceService } from '../../../../services/sales-invoice.service';
import { AddressService } from '../../../../services/address.service';
import { TimelineService } from '../../../../services/timeline.service';
import { ProductionOrderService } from '../../../../services/production-order.service';
import { AttachmentService, AttachmentModel } from '../../../../services/attachment.service';
import { AttachmentPolicyService } from '../../../../services/attachment-policy.service';
import { AttachmentListComponent } from '../../../../shared/attachment-list/attachment-list.component';
import { AuthService } from '../../../service/auth.service';

// A32 — sale order detail: source + customer PO header (PD-06), delivery indicator (PE-07), reserved qty and
// Reserve / Release / Reserve all (PE-08), partial reservation dialog (PE-09). The pre-A32 behaviour is pinned in
// sale-order-detail.component.spec.ts.

const UUID = '11111111-1111-1111-1111-111111111111';

const BASE_PERMISSIONS = ['SALE_ORDER_VIEW', 'SALE_ORDER_EDIT', 'SALE_ORDER_RESERVE', 'SALE_ORDER_RELEASE_RESERVATION'];

function line(overrides: Partial<SaleOrderLineModel> = {}): SaleOrderLineModel {
  return {
    uuid: 'l1', variantUuid: 'v1', itemDescription: '4mm cable (CAB-4MM)', variantSku: 'CAB-4MM', unitOfMeasure: 'M',
    quantity: 100, unitPrice: 40, discountPercent: 0, taxPercent: 0, lineTotal: 4000,
    fulfilledQty: 0, invoicedQty: 0, fulfillmentMode: 'IN_STOCK', status: 'OPEN',
    reservedQty: 0, reservableQty: 100, deliveryIndicator: 'RED',
    ...overrides
  };
}

function order(overrides: Partial<SaleOrderModel> = {}): SaleOrderModel {
  return {
    uuid: UUID, traceId: 't-1', soNumber: 'SO-2026-00042', partnerId: 'p-1',
    orderDate: '2026-09-01T00:00:00Z', currencyId: 'c-1',
    subtotal: 4000, taxAmount: 0, discountAmount: 0, grandTotal: 4000,
    status: 'CONFIRMED', requiresShipment: true, deliveryMode: 'SELF_PICKUP',
    createdDate: '2026-09-01T00:00:00Z', sourceType: 'MANUAL', lines: [line()],
    ...overrides
  };
}

function reservation(overrides: Partial<SaleOrderLineReservationModel> = {}): SaleOrderLineReservationModel {
  return {
    lineUuid: 'l1', variantUuid: 'v1', outcome: 'RESERVED', requestedQty: 100, changedQty: 100, availableQty: 250,
    warehouseUuid: 'wh-1', warehouseName: 'Main warehouse', reservedQty: 100, reservableQty: 0,
    deliveryIndicator: 'BLUE', lineStatus: 'RESERVED',
    ...overrides
  };
}

function file(overrides: Partial<AttachmentModel> = {}): AttachmentModel {
  return {
    uuid: 'att-1', interfaceCode: 'CUSTOMER_PO', documentId: UUID, fileName: 'GlobalTech_PO_4521.pdf',
    fileUrl: '/uploads/po.pdf', uploadedBy: 1, uploadedByName: 'Sana', uploadedDate: '2026-10-05T09:00:00Z',
    ...overrides
  };
}

function ok<T>(result: T, message = '') {
  return of({ success: true, message, result } as any);
}

describe('SaleOrderDetailComponent — A32 source, customer PO and reservations', () => {
  let fixture: ComponentFixture<SaleOrderDetailComponent>;
  let component: SaleOrderDetailComponent;
  let service: jasmine.SpyObj<SaleOrderService>;
  let attachments: jasmine.SpyObj<AttachmentService>;
  let toasts: jasmine.Spy;
  let permissions: string[];
  /** The order's CUSTOMER_PO files, as GET api/attachments answers. */
  let poFiles: AttachmentModel[];
  /** A test's own ActivatedRoute; the default has the order's uuid in the snapshot only. */
  let route: unknown;

  const auth = { hasPermission: (code: string) => permissions.includes(code) } as unknown as AuthService;

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  function queryAll(testId: string): HTMLElement[] {
    return Array.from(fixture.nativeElement.querySelectorAll(`[data-testid="${testId}"]`));
  }

  /** The text the element's pTooltip will show on hover. */
  function tooltipOf(testId: string, index = 0): string {
    const el = fixture.debugElement.queryAll(By.css(`[data-testid="${testId}"]`))[index];
    return String(el.injector.get(Tooltip).content ?? '');
  }

  function button(testId: string, index = 0): HTMLButtonElement {
    return queryAll(testId)[index].querySelector('button') as HTMLButtonElement;
  }

  function lastToast() {
    return toasts.calls.mostRecent().args[0] as { severity: string; summary: string; detail: string };
  }

  async function setup(model: SaleOrderModel = order()) {
    service = jasmine.createSpyObj<SaleOrderService>('SaleOrderService', [
      'getSaleOrderById', 'getDeliveries', 'createDelivery', 'confirmSaleOrder', 'cancelSaleOrder', 'getAvailability',
      'updateCustomerPo', 'checkCustomerPo', 'reserveLine', 'releaseLine', 'reserveAll'
    ]);
    service.getSaleOrderById.and.returnValue(ok(model));
    service.getDeliveries.and.returnValue(ok([]));
    service.getAvailability.and.returnValue(ok([]));
    service.updateCustomerPo.and.returnValue(ok(null));
    service.checkCustomerPo.and.returnValue(ok([]));
    service.reserveLine.and.returnValue(ok(reservation()));
    service.releaseLine.and.returnValue(ok(reservation({
      outcome: 'RELEASED', requestedQty: 100, changedQty: 100, reservedQty: 0, reservableQty: 100,
      deliveryIndicator: 'RED', lineStatus: 'OPEN'
    })));
    service.reserveAll.and.returnValue(ok({ lines: [reservation()], reservedLineCount: 1, partialLineCount: 0, unchangedLineCount: 0 }));

    const partners = jasmine.createSpyObj<BusinessPartnerService>('BusinessPartnerService', ['getPartnerById']);
    partners.getPartnerById.and.returnValue(ok({ uuid: 'p-1', companyName: 'GlobalTech Co' }));
    const invoices = jasmine.createSpyObj<SalesInvoiceService>('SalesInvoiceService', ['getInvoices', 'getInvoice', 'downloadPdf']);
    invoices.getInvoices.and.returnValue(ok({ data: [], totalRecords: 0, page: 1, pageSize: 100, totalPages: 0 }));
    const addresses = jasmine.createSpyObj<AddressService>('AddressService', ['getAddress']);
    addresses.getAddress.and.returnValue(ok(null));
    const timeline = jasmine.createSpyObj<TimelineService>('TimelineService', ['getByTraceId', 'getByDocument']);
    timeline.getByTraceId.and.returnValue(ok(null));
    const production = jasmine.createSpyObj<ProductionOrderService>('ProductionOrderService', ['getList']);
    production.getList.and.returnValue(ok({ data: [], totalRecords: 0, page: 1, pageSize: 100, totalPages: 0 }));

    attachments = jasmine.createSpyObj<AttachmentService>('AttachmentService',
      ['getAttachments', 'upload', 'deleteAttachment', 'resolveUrl', 'isApiUrl', 'download']);
    attachments.getAttachments.and.callFake(() => ok(poFiles));
    attachments.isApiUrl.and.returnValue(false);
    attachments.resolveUrl.and.callFake((url: string) => `http://files${url}`);
    const policy = jasmine.createSpyObj<AttachmentPolicyService>('AttachmentPolicyService', ['ruleFor']);
    policy.ruleFor.and.returnValue(of(null));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [SaleOrderDetailComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), MessageService,
        { provide: SaleOrderService, useValue: service },
        { provide: BusinessPartnerService, useValue: partners },
        { provide: SalesInvoiceService, useValue: invoices },
        { provide: AddressService, useValue: addresses },
        { provide: TimelineService, useValue: timeline },
        { provide: ProductionOrderService, useValue: production },
        { provide: AttachmentService, useValue: attachments },
        { provide: AttachmentPolicyService, useValue: policy },
        { provide: AuthService, useValue: auth },
        { provide: ActivatedRoute, useValue: route ?? { snapshot: { paramMap: new Map([['uuid', UUID]]) } } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(SaleOrderDetailComponent);
    component = fixture.componentInstance;
    toasts = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
    fixture.detectChanges();
  }

  beforeEach(() => { permissions = [...BASE_PERMISSIONS]; poFiles = []; route = undefined; });

  // The duplicate-PO warning links to another order on this same route; Angular keeps the component, so the
  // page has to follow the route's uuid rather than read it once.
  it('loads the other order when a link on the page leads to one', async () => {
    const params = new BehaviorSubject(convertToParamMap({ uuid: UUID }));
    route = { snapshot: { paramMap: new Map([['uuid', UUID]]) }, paramMap: params };
    await setup(order({ customerPoReference: 'GT-1' }));
    expect(service.getSaleOrderById).toHaveBeenCalledOnceWith(UUID);

    service.getSaleOrderById.and.returnValue(ok(order({ uuid: 'so-9', soNumber: 'SO-2026-00009' })));
    params.next(convertToParamMap({ uuid: 'so-9' }));
    fixture.detectChanges();

    expect(service.getSaleOrderById.calls.mostRecent().args[0]).toBe('so-9');
    expect(component.uuid).toBe('so-9');
    expect(fixture.nativeElement.textContent).toContain('SO-2026-00009');
  });

  // ── PD-06: source ──────────────────────────────────────────────────────────

  it('badges where the order came from, for every source type', async () => {
    const expected: Record<string, string> = {
      MANUAL: 'Manual', FROM_QUOTATION: 'From quotation', PORTAL: 'Customer portal', INTER_TENANT: 'Inter-tenant'
    };
    for (const [sourceType, label] of Object.entries(expected)) {
      await setup(order({ sourceType: sourceType as any }));
      expect(query('source-badge')!.textContent).withContext(sourceType).toContain(label);
    }
  });

  it('reads an order with no source type as manual', async () => {
    await setup(order({ sourceType: undefined }));
    expect(query('source-badge')!.textContent).toContain('Manual');
  });

  it('links the quotation and the inquiry the order came from', async () => {
    permissions = [...BASE_PERMISSIONS, 'SALE_QUOTATION_VIEW', 'SALE_INQUIRY_VIEW'];
    await setup(order({
      sourceType: 'FROM_QUOTATION',
      sourceQuotation: { uuid: 'q-15', number: 'SQ-2026-00015', status: 'CONVERTED' },
      sourceInquiry: { uuid: 'i-42', number: 'INQ-2026-00042', status: 'QUOTED' }
    }));

    const quotation = query('source-quotation-link') as HTMLAnchorElement;
    const inquiry = query('source-inquiry-link') as HTMLAnchorElement;
    expect(quotation.textContent).toContain('SQ-2026-00015');
    expect(quotation.getAttribute('href')).toBe('/portal/pages/sales/quotations/q-15');
    expect(inquiry.textContent).toContain('INQ-2026-00042');
    expect(inquiry.getAttribute('href')).toBe('/portal/pages/sales/inquiries/i-42');
  });

  it('names the source documents without a link for someone who may not open them', async () => {
    await setup(order({
      sourceType: 'FROM_QUOTATION',
      sourceQuotation: { uuid: 'q-15', number: 'SQ-2026-00015', status: 'CONVERTED' },
      sourceInquiry: { uuid: 'i-42', number: 'INQ-2026-00042', status: 'QUOTED' }
    }));

    expect(query('source-quotation-link')).toBeNull();
    expect(query('source-inquiry-link')).toBeNull();
    expect(query('source-quotation')!.textContent).toContain('SQ-2026-00015');
    expect(query('source-inquiry')!.textContent).toContain('INQ-2026-00042');
  });

  it('shows no quotation or inquiry row on a manual order', async () => {
    await setup();
    expect(query('source-quotation')).toBeNull();
    expect(query('source-inquiry')).toBeNull();
  });

  // ── PD-06: customer PO ─────────────────────────────────────────────────────

  it('shows the customer PO reference, its date as that day, and the linked PO file', async () => {
    poFiles = [file()];
    await setup(order({
      customerPoReference: 'GT-PO-2026-4521', customerPoDate: '2026-10-05T00:00:00Z', customerPoAttachmentUuid: 'att-1'
    }));

    expect(query('po-reference')!.textContent).toContain('GT-PO-2026-4521');
    expect(query('po-date')!.textContent).toContain('05 Oct 2026');
    expect(attachments.getAttachments).toHaveBeenCalledWith('CUSTOMER_PO', UUID);
    expect(query('po-document')!.textContent).toContain('GlobalTech_PO_4521.pdf');
    expect((query('po-document-link') as HTMLAnchorElement).getAttribute('href')).toBe('http://files/uploads/po.pdf');
  });

  it('says when no customer PO has been recorded', async () => {
    await setup();
    expect(query('po-reference')!.textContent).toContain('—');
    expect(query('po-document')!.textContent).toContain('—');
    expect(service.checkCustomerPo).not.toHaveBeenCalled();
  });

  it("lists the order's CUSTOMER_PO files in the attachment panel", async () => {
    await setup();
    const panel = fixture.debugElement.query(By.directive(AttachmentListComponent))?.componentInstance as AttachmentListComponent;

    expect(panel).toBeTruthy();
    expect(panel.interfaceCode).toBe('CUSTOMER_PO');
    expect(panel.documentId).toBe(UUID);
  });

  it('warns, without blocking anything, when another order carries the same customer PO', async () => {
    const dup: CustomerPoDuplicateModel = {
      uuid: 'so-9', soNumber: 'SO-2026-00009', partnerId: 'p-1', status: 'CONFIRMED', orderDate: '2026-09-20T00:00:00Z'
    };
    await setup(order({ customerPoReference: 'GT-PO-2026-4521' }));
    service.checkCustomerPo.and.returnValue(ok([dup]));
    component.load();
    fixture.detectChanges();

    expect(service.checkCustomerPo).toHaveBeenCalledWith('GT-PO-2026-4521', UUID);
    const banner = query('po-duplicate-warning')!;
    expect(banner.textContent).toContain('GT-PO-2026-4521');
    expect(banner.textContent).toContain('SO-2026-00009');
    expect(banner.querySelector('a')!.getAttribute('href')).toBe('/portal/pages/sales/orders/so-9');
    // Nothing is held back: the page's actions are all still there.
    expect(query('reserve-line')).not.toBeNull();
  });

  it('shows no duplicate warning when the reference is unique, or when the check fails', async () => {
    await setup(order({ customerPoReference: 'GT-PO-2026-4521' }));
    expect(query('po-duplicate-warning')).toBeNull();

    service.checkCustomerPo.and.returnValue(throwError(() => ({ status: 500 })));
    component.load();
    fixture.detectChanges();
    expect(query('po-duplicate-warning')).toBeNull();
  });

  it('edits the customer PO through PUT customer-po, any status but cancelled or closed, with SALE_ORDER_EDIT', async () => {
    await setup(order({ status: 'PARTIALLY_FULFILLED' }));
    expect(query('edit-customer-po')).not.toBeNull();

    for (const status of ['CANCELLED', 'CLOSED']) {
      await setup(order({ status }));
      expect(query('edit-customer-po')).withContext(status).toBeNull();
    }

    permissions = ['SALE_ORDER_VIEW'];
    await setup();
    expect(query('edit-customer-po')).toBeNull();
    component.openCustomerPoDialog();
    expect(component.poDialogVisible).toBeFalse();
  });

  it('starts the dialog from what the order has, offers its CUSTOMER_PO files, and saves all three', async () => {
    await setup(order({ customerPoReference: 'OLD-1', customerPoDate: '2026-10-01T00:00:00Z', customerPoAttachmentUuid: null }));
    attachments.getAttachments.and.returnValue(ok([file(), file({ uuid: 'att-2', fileName: 'revised.pdf' })]));

    component.openCustomerPoDialog();
    fixture.detectChanges();

    expect(component.poDialogVisible).toBeTrue();
    expect(component.poReference).toBe('OLD-1');
    expect(component.poDate!.getDate()).toBe(1);
    expect(component.poFileOptions.map(o => o.value)).toEqual(['att-1', 'att-2']);

    component.poReference = '  GT-PO-2026-4521 ';
    component.poDate = new Date(2026, 9, 5);
    component.poAttachmentUuid = 'att-2';
    component.saveCustomerPo();

    expect(service.updateCustomerPo).toHaveBeenCalledOnceWith(UUID, {
      customerPoReference: 'GT-PO-2026-4521', customerPoDate: '2026-10-05', customerPoAttachmentUuid: 'att-2'
    });
    expect(component.poDialogVisible).toBeFalse();
    expect(service.getSaleOrderById).toHaveBeenCalledTimes(2);
  });

  it('links the only PO file there is when none is linked yet, and clears all three when emptied', async () => {
    await setup();
    attachments.getAttachments.and.returnValue(ok([file()]));
    component.openCustomerPoDialog();
    expect(component.poAttachmentUuid).toBe('att-1');

    component.poReference = '   ';
    component.poDate = null;
    component.poAttachmentUuid = null;
    component.saveCustomerPo();

    expect(service.updateCustomerPo).toHaveBeenCalledOnceWith(UUID, {
      customerPoReference: null, customerPoDate: null, customerPoAttachmentUuid: null
    });
  });

  it('refuses a reference over 50 characters without calling the server', async () => {
    await setup();
    component.openCustomerPoDialog();
    component.poReference = 'X'.repeat(51);
    component.saveCustomerPo();

    expect(service.updateCustomerPo).not.toHaveBeenCalled();
    expect(lastToast().severity).toBe('warn');
  });

  it('warns in the dialog when the typed reference is on another order, and still saves', async () => {
    await setup();
    component.openCustomerPoDialog();
    service.checkCustomerPo.and.returnValue(ok([
      { uuid: 'so-9', soNumber: 'SO-2026-00009', partnerId: 'p-1', status: 'DRAFT', orderDate: '2026-09-20T00:00:00Z' }
    ]));

    component.poReference = 'GT-PO-1';
    component.checkDialogPoReference();
    fixture.detectChanges();

    expect(service.checkCustomerPo).toHaveBeenCalledWith('GT-PO-1', UUID);
    expect(query('po-dialog-duplicate')!.textContent).toContain('SO-2026-00009');

    component.saveCustomerPo();
    expect(service.updateCustomerPo).toHaveBeenCalledTimes(1);
  });

  it("shows the server's refusal and keeps the dialog open when the customer PO is not saved", async () => {
    await setup();
    component.openCustomerPoDialog();
    service.updateCustomerPo.and.returnValue(throwError(() => ({ status: 400, error: { message: 'That file is not a CUSTOMER_PO file of this order.' } })));

    component.poReference = 'GT-1';
    component.saveCustomerPo();

    expect(lastToast().detail).toBe('That file is not a CUSTOMER_PO file of this order.');
    expect(component.poDialogVisible).toBeTrue();
    expect(component.isSavingPo).toBeFalse();
  });

  // ── PE-07: delivery indicator ──────────────────────────────────────────────

  it('shows every line as a coloured dot with what the colour means', async () => {
    await setup(order({
      lines: [
        line({ uuid: 'a', deliveryIndicator: 'GREEN', status: 'FULFILLED', fulfilledQty: 100, reservableQty: 0 }),
        line({ uuid: 'b', deliveryIndicator: 'BLUE', status: 'RESERVED', reservedQty: 100, reservableQty: 0 }),
        line({ uuid: 'c', deliveryIndicator: 'YELLOW', status: 'RESERVED', reservedQty: 40, reservableQty: 60 }),
        line({ uuid: 'd', deliveryIndicator: 'RED' }),
        line({ uuid: 'e', deliveryIndicator: 'GREY', status: 'CANCELLED', reservableQty: 0 })
      ]
    }));

    const dots = queryAll('line-indicator');
    expect(dots.map(d => d.getAttribute('data-indicator'))).toEqual(['GREEN', 'BLUE', 'YELLOW', 'RED', 'GREY']);
    expect(dots[0].classList).toContain('ind-green');
    expect(dots[3].classList).toContain('ind-red');

    expect(tooltipOf('line-indicator', 0)).toContain('Delivered in full');
    expect(tooltipOf('line-indicator', 1)).toContain('Reserved in full');
    expect(tooltipOf('line-indicator', 2)).toContain('Partly delivered or partly reserved');
    expect(tooltipOf('line-indicator', 2)).toContain('Reserved 40');
    expect(tooltipOf('line-indicator', 3)).toContain('Nothing reserved or delivered');
    expect(tooltipOf('line-indicator', 4)).toContain('Cancelled');
  });

  // ── PE-08: reserved column and buttons ─────────────────────────────────────

  it('shows how much the order holds for each line', async () => {
    await setup(order({ lines: [line({ reservedQty: 40, reservableQty: 60, status: 'RESERVED' })] }));
    expect(query('line-reserved')!.textContent!.trim()).toBe('40');
  });

  it('enables Reserve and Release for someone who holds both codes', async () => {
    await setup(order({ lines: [line({ reservedQty: 40, reservableQty: 60, status: 'RESERVED', deliveryIndicator: 'YELLOW' })] }));

    expect(button('reserve-line').disabled).toBeFalse();
    expect(button('release-line').disabled).toBeFalse();
    expect(query('reserve-permission-note')).toBeNull();
  });

  it('disables Reserve, without hiding it, for someone without SALE_ORDER_RESERVE — and says why', async () => {
    permissions = ['SALE_ORDER_VIEW', 'SALE_ORDER_RELEASE_RESERVATION'];
    await setup(order({ lines: [line({ reservedQty: 40, reservableQty: 60, status: 'RESERVED' })] }));

    expect(button('reserve-line').disabled).toBeTrue();
    expect(tooltipOf('reserve-wrap')).toBe(RESERVE_PERMISSION_TOOLTIP);
    expect(RESERVE_PERMISSION_TOOLTIP).toBe('You do not have permission to reserve inventory. Contact your administrator.');
    // The two codes are independent (§6.5).
    expect(button('release-line').disabled).toBeFalse();
    expect(query('reserve-permission-note')).not.toBeNull();

    component.reserveLine(component.order!.lines[0]);
    expect(service.reserveLine).not.toHaveBeenCalled();
  });

  it('disables Release, without hiding it, for someone without SALE_ORDER_RELEASE_RESERVATION — and says why', async () => {
    permissions = ['SALE_ORDER_VIEW', 'SALE_ORDER_RESERVE'];
    await setup(order({ lines: [line({ reservedQty: 40, reservableQty: 60, status: 'RESERVED' })] }));

    expect(button('release-line').disabled).toBeTrue();
    expect(tooltipOf('release-wrap')).toBe(RELEASE_PERMISSION_TOOLTIP);
    expect(RELEASE_PERMISSION_TOOLTIP).toBe('You do not have permission to release reserved inventory. Contact your administrator.');
    expect(button('reserve-line').disabled).toBeFalse();

    component.releaseLine(component.order!.lines[0]);
    expect(service.releaseLine).not.toHaveBeenCalled();
  });

  it('disables both for someone with neither code, still showing them', async () => {
    permissions = ['SALE_ORDER_VIEW'];
    await setup(order({ lines: [line({ reservedQty: 40, reservableQty: 60, status: 'RESERVED' })] }));

    expect(button('reserve-line').disabled).toBeTrue();
    expect(button('release-line').disabled).toBeTrue();
    expect(button('reserve-all').disabled).toBeTrue();
    expect(tooltipOf('reserve-all-wrap')).toBe(RESERVE_PERMISSION_TOOLTIP);
  });

  it('offers Release only while something is held, and Reserve only while something is left to hold', async () => {
    await setup(order({
      lines: [
        line({ uuid: 'nothing-held', reservedQty: 0, reservableQty: 100 }),
        line({ uuid: 'all-held', reservedQty: 100, reservableQty: 0, status: 'RESERVED', deliveryIndicator: 'BLUE' }),
        line({ uuid: 'delivered', fulfilledQty: 100, reservableQty: 0, status: 'FULFILLED', deliveryIndicator: 'GREEN' }),
        line({ uuid: 'drop-ship', fulfillmentMode: 'DROP_SHIP', reservableQty: 100 })
      ]
    }));
    const rows = Array.from(fixture.nativeElement.querySelectorAll('[data-testid="lines-table"] tbody tr')) as HTMLElement[];
    const has = (row: HTMLElement, testId: string) => !!row.querySelector(`[data-testid="${testId}"]`);

    expect([has(rows[0], 'reserve-line'), has(rows[0], 'release-line')]).toEqual([true, false]);
    expect([has(rows[1], 'reserve-line'), has(rows[1], 'release-line')]).toEqual([false, true]);
    expect([has(rows[2], 'reserve-line'), has(rows[2], 'release-line')]).toEqual([false, false]);
    expect([has(rows[3], 'reserve-line'), has(rows[3], 'release-line')]).toEqual([false, false]);
  });

  it('offers no Reserve on a draft (confirming reserves) or a cancelled order', async () => {
    for (const status of ['DRAFT', 'CANCELLED', 'FULFILLED']) {
      await setup(order({ status }));
      expect(query('reserve-line')).withContext(status).toBeNull();
      expect(query('reserve-all')).withContext(status).toBeNull();
    }
  });

  it('reserves a line, shows the new dot and figure at once, and re-reads the order', async () => {
    await setup();
    service.getSaleOrderById.and.returnValue(ok(order({
      lines: [line({ reservedQty: 100, reservableQty: 0, status: 'RESERVED', deliveryIndicator: 'BLUE' })]
    })));

    button('reserve-line').click();
    fixture.detectChanges();

    expect(service.reserveLine).toHaveBeenCalledOnceWith(UUID, 'l1', {});
    expect(lastToast().severity).toBe('success');
    expect(lastToast().detail).toContain('Main warehouse');
    expect(service.getSaleOrderById).toHaveBeenCalledTimes(2);
    expect(query('line-indicator')!.getAttribute('data-indicator')).toBe('BLUE');
    expect(query('line-reserved')!.textContent!.trim()).toBe('100');
  });

  it('shows the new dot even when re-reading the order fails', async () => {
    await setup();
    service.getSaleOrderById.and.returnValue(throwError(() => ({ status: 500 })));

    component.reserveLine(component.order!.lines[0]);
    fixture.detectChanges();

    expect(component.order).not.toBeNull();
    expect(query('line-indicator')!.getAttribute('data-indicator')).toBe('BLUE');
    expect(query('line-reserved')!.textContent!.trim()).toBe('100');
  });

  it('releases what a line holds and refreshes it', async () => {
    await setup(order({ lines: [line({ reservedQty: 100, reservableQty: 0, status: 'RESERVED', deliveryIndicator: 'BLUE' })] }));
    service.getSaleOrderById.and.returnValue(ok(order()));

    button('release-line').click();
    fixture.detectChanges();

    expect(service.releaseLine).toHaveBeenCalledOnceWith(UUID, 'l1', {});
    expect(lastToast().severity).toBe('success');
    expect(query('line-indicator')!.getAttribute('data-indicator')).toBe('RED');
    expect(service.getSaleOrderById).toHaveBeenCalledTimes(2);
  });

  it("shows the server's reason when a reserve or release is refused", async () => {
    await setup(order({ lines: [line({ reservedQty: 40, reservableQty: 60, status: 'RESERVED' })] }));
    service.reserveLine.and.returnValue(throwError(() => ({ status: 400, error: { message: 'The line is not reservable.' } })));
    service.releaseLine.and.returnValue(throwError(() => ({ status: 400, error: { message: 'Nothing is held for this line.' } })));

    component.reserveLine(component.order!.lines[0]);
    expect(lastToast().detail).toBe('The line is not reservable.');
    component.releaseLine(component.order!.lines[0]);
    expect(lastToast().detail).toBe('Nothing is held for this line.');
    expect(component.busyLineUuid).toBeNull();
  });

  it('says so when no stock at all is free, without a dialog', async () => {
    await setup();
    service.reserveLine.and.returnValue(ok(reservation({
      outcome: 'NONE_AVAILABLE', changedQty: 0, availableQty: 0, reservedQty: 0, reservableQty: 100, deliveryIndicator: 'RED',
      lineStatus: 'OPEN', message: 'No warehouse has 4mm cable free.'
    })));

    component.reserveLine(component.order!.lines[0]);

    expect(component.partialDialogVisible).toBeFalse();
    expect(lastToast().severity).toBe('warn');
    expect(lastToast().detail).toBe('No warehouse has 4mm cable free.');
  });

  it('reserves every open line at once, partial holds allowed, and reports what happened', async () => {
    await setup(order({
      lines: [line({ uuid: 'a' }), line({ uuid: 'b' }), line({ uuid: 'c' })]
    }));
    service.reserveAll.and.returnValue(ok({
      lines: [
        reservation({ lineUuid: 'a' }),
        reservation({ lineUuid: 'b', outcome: 'PARTIAL', changedQty: 30, reservedQty: 30, reservableQty: 70, deliveryIndicator: 'YELLOW' }),
        reservation({ lineUuid: 'c', outcome: 'NONE_AVAILABLE', changedQty: 0, reservedQty: 0, reservableQty: 100, deliveryIndicator: 'RED' })
      ],
      reservedLineCount: 1, partialLineCount: 1, unchangedLineCount: 1
    }));
    service.getSaleOrderById.and.returnValue(ok(order({
      lines: [
        line({ uuid: 'a', reservedQty: 100, reservableQty: 0, status: 'RESERVED', deliveryIndicator: 'BLUE' }),
        line({ uuid: 'b', reservedQty: 30, reservableQty: 70, status: 'RESERVED', deliveryIndicator: 'YELLOW' }),
        line({ uuid: 'c' })
      ]
    })));

    button('reserve-all').click();
    fixture.detectChanges();

    expect(service.reserveAll).toHaveBeenCalledOnceWith(UUID, true);
    expect(lastToast().detail).toContain('1 line reserved in full');
    expect(lastToast().detail).toContain('1 in part');
    expect(lastToast().detail).toContain('1 unchanged');
    expect(queryAll('line-indicator').map(d => d.getAttribute('data-indicator'))).toEqual(['BLUE', 'YELLOW', 'RED']);
    expect(service.getSaleOrderById).toHaveBeenCalledTimes(2);
  });

  it('disables Reserve all when no line has anything left to hold', async () => {
    await setup(order({ lines: [line({ reservedQty: 100, reservableQty: 0, status: 'RESERVED', deliveryIndicator: 'BLUE' })] }));
    expect(button('reserve-all').disabled).toBeTrue();
  });

  // ── PE-09: partial reservation ─────────────────────────────────────────────

  function needsConfirmation() {
    return ok(reservation({
      outcome: 'NEEDS_CONFIRMATION', requestedQty: 100, changedQty: 0, availableQty: 30, reservedQty: 0,
      reservableQty: 100, deliveryIndicator: 'RED', lineStatus: 'OPEN'
    }));
  }

  it('asks before holding only part, naming the item, the warehouse and both figures', async () => {
    await setup();
    service.reserveLine.and.returnValue(needsConfirmation());

    component.reserveLine(component.order!.lines[0]);
    fixture.detectChanges();

    expect(component.partialDialogVisible).toBeTrue();
    expect(document.body.querySelector('[data-testid="partial-message"]')!.textContent)
      .toContain('Available: 30, Required: 100. Reserve 30 units?');
    expect(document.body.querySelector('[data-testid="partial-variant"]')!.textContent).toContain('4mm cable (CAB-4MM)');
    expect(document.body.querySelector('[data-testid="partial-variant"]')!.textContent).toContain('CAB-4MM');
    expect(document.body.querySelector('[data-testid="partial-warehouse"]')!.textContent).toContain('Main warehouse');
    // Nothing was held, so nothing to refresh yet.
    expect(service.getSaleOrderById).toHaveBeenCalledTimes(1);
  });

  it('reserves the available part from the same warehouse on confirm', async () => {
    await setup();
    service.reserveLine.and.returnValues(needsConfirmation(), ok(reservation({
      outcome: 'RESERVED', requestedQty: 30, changedQty: 30, reservedQty: 30, reservableQty: 70, deliveryIndicator: 'YELLOW'
    })));
    service.getSaleOrderById.and.returnValue(ok(order({
      lines: [line({ reservedQty: 30, reservableQty: 70, status: 'RESERVED', deliveryIndicator: 'YELLOW' })]
    })));

    component.reserveLine(component.order!.lines[0]);
    component.confirmPartial();
    fixture.detectChanges();

    expect(service.reserveLine.calls.count()).toBe(2);
    expect(service.reserveLine.calls.mostRecent().args).toEqual([UUID, 'l1', { quantity: 30, allowPartial: true, warehouseUuid: 'wh-1' }]);
    expect(component.partialDialogVisible).toBeFalse();
    expect(query('line-indicator')!.getAttribute('data-indicator')).toBe('YELLOW');
    expect(service.getSaleOrderById).toHaveBeenCalledTimes(2);
  });

  it('does nothing on cancel', async () => {
    await setup();
    service.reserveLine.and.returnValue(needsConfirmation());

    component.reserveLine(component.order!.lines[0]);
    component.cancelPartial();

    expect(component.partialDialogVisible).toBeFalse();
    expect(service.reserveLine).toHaveBeenCalledTimes(1);
    expect(service.getSaleOrderById).toHaveBeenCalledTimes(1);
    expect(component.busyLineUuid).toBeNull();
  });
});
