import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { By } from '@angular/platform-browser';
import { signal } from '@angular/core';
import { MessageService } from 'primeng/api';

import { SaleQuotationDetailComponent } from './sale-quotation-detail.component';
import { SalesPreorderService, SaleQuotation } from '../../../../services/sales-preorder.service';
import { SaleOrderService } from '../../../../services/sale-order.service';
import { AddressService } from '../../../../services/address.service';
import { AttachmentService } from '../../../../services/attachment.service';
import { InventoryService } from '../../../../services/inventory.service';
import { FinanceSetupService } from '../../../../services/finance-setup.service';
import { PricingRuleService } from '../../../../services/pricing-rule.service';
import { TenantService } from '../../../service/tenant.service';
import { AuthService } from '../../../service/auth.service';
import { AttachmentListComponent } from '../../../../shared/attachment-list/attachment-list.component';
import { ok, fail, quotation, qLine } from '../sale-quotation.fixtures.spec';

describe('SaleQuotationDetailComponent', () => {
  let fixture: ComponentFixture<SaleQuotationDetailComponent>;
  let component: SaleQuotationDetailComponent;
  let service: jasmine.SpyObj<SalesPreorderService>;
  let saleOrders: jasmine.SpyObj<SaleOrderService>;
  let addresses: jasmine.SpyObj<AddressService>;
  let attachments: jasmine.SpyObj<AttachmentService>;
  let router: Router;
  let permissions: string[];

  const auth = {
    hasPermission: (code: string) => permissions.includes(code),
    hasAnyPermission: (...codes: string[]) => codes.some(c => permissions.includes(c))
  } as unknown as AuthService;

  async function setup(q: SaleQuotation | null = quotation(), opts: { loadFails?: number } = {}) {
    service = jasmine.createSpyObj<SalesPreorderService>('SalesPreorderService', [
      'getQuotation', 'deleteQuotationLine', 'sendQuotation', 'convertQuotationToOrder', 'acceptQuotation',
      'rejectQuotation', 'recordCustomerResponse', 'addQuotationLine', 'updateQuotationLine', 'getRejectionReasons',
      'copyQuotation'
    ]);
    service.getQuotation.and.returnValue(opts.loadFails ? fail(opts.loadFails) : ok(q));
    service.getRejectionReasons.and.returnValue(ok([]));
    saleOrders = jasmine.createSpyObj<SaleOrderService>('SaleOrderService', ['getDefaults']);
    saleOrders.getDefaults.and.returnValue(ok({ deliveryMode: 'SHIP', selfPickupEnabled: true }));
    addresses = jasmine.createSpyObj<AddressService>('AddressService', ['getAddresses']);
    addresses.getAddresses.and.returnValue(ok([{ uuid: 'addr-1', line1: '12 Mall Rd', cityName: 'Lahore', countryName: 'Pakistan' }]));
    attachments = jasmine.createSpyObj<AttachmentService>('AttachmentService', ['getAttachments', 'resolveUrl', 'isApiUrl', 'download']);
    attachments.getAttachments.and.returnValue(ok([]));
    const inventory = jasmine.createSpyObj<InventoryService>('InventoryService', ['getProducts']);
    inventory.getProducts.and.returnValue(ok({ data: [], totalRecords: 0 }));
    const setupSvc = jasmine.createSpyObj<FinanceSetupService>('FinanceSetupService', ['getTaxCodes']);
    setupSvc.getTaxCodes.and.returnValue(ok([]));
    const pricing = jasmine.createSpyObj<PricingRuleService>('PricingRuleService', ['resolvePrice']);
    pricing.resolvePrice.and.returnValue(ok({ found: false }));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [SaleQuotationDetailComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), MessageService,
        { provide: SalesPreorderService, useValue: service },
        { provide: SaleOrderService, useValue: saleOrders },
        { provide: AddressService, useValue: addresses },
        { provide: AttachmentService, useValue: attachments },
        { provide: InventoryService, useValue: inventory },
        { provide: FinanceSetupService, useValue: setupSvc },
        { provide: PricingRuleService, useValue: pricing },
        { provide: TenantService, useValue: { tenant: signal({ baseCurrency: 'cur-usd' }), hasFeature: () => true } },
        { provide: AuthService, useValue: auth },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['uuid', 'sq-1']]) } } }
      ]
    }).compileComponents();

    router = TestBed.inject(Router);
    fixture = TestBed.createComponent(SaleQuotationDetailComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  const el = (testId: string): HTMLElement | null => fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  const all = (testId: string): string[] =>
    Array.from(fixture.nativeElement.querySelectorAll(`[data-testid="${testId}"]`)).map((e: any) => e.textContent.replace(/\s+/g, ' ').trim());

  beforeEach(() => { permissions = ['SALE_QUOTATION_VIEW']; });

  // ── A32-PC-11 — header and tabs ─────────────────────────────────────────────

  describe('header and tabs (A32-PC-11)', () => {
    it('shows number, status, customer, validity, currency and the inquiry it came from', async () => {
      await setup();

      expect(service.getQuotation).toHaveBeenCalledWith('sq-1');
      expect(el('number')!.textContent).toContain('SQ-2026-00015');
      expect(el('status')!.textContent).toContain('Draft');
      expect(el('customer')!.textContent).toContain('GlobalTech Co');
      expect(el('validity')!.textContent!.replace(/\s+/g, ' ')).toContain('03 Oct 2026 — 31 Oct 2026');
      expect(el('currency')!.textContent).toContain('USD');
      const link = el('source-inquiry')!.querySelector('a')!;
      expect(link.textContent!.trim()).toBe('INQ-2026-00042');
      expect(link.getAttribute('href')).toBe('/portal/pages/sales/inquiries/inq-42');
    });

    it('says "—" for a quotation made on its own', async () => {
      await setup(quotation({ sourceInquiry: null }));
      expect(el('source-inquiry')!.querySelector('a')).toBeNull();
      expect(el('source-inquiry')!.textContent!.trim()).toBe('—');
    });

    it('has Details, Lines, Terms and Attachments tabs, with the terms on their own tab', async () => {
      await setup();
      const headers = Array.from(fixture.nativeElement.querySelectorAll('.p-tabview-nav li, [role="tab"]'))
        .map((e: any) => e.textContent.trim()).filter((t: string) => !!t);
      for (const tab of ['Details', 'Lines', 'Terms', 'Attachments']) expect(headers.join('|')).withContext(tab).toContain(tab);
      expect(component.tabs).toEqual(['Details', 'Lines', 'Terms', 'Attachments']);
      expect(fixture.nativeElement.textContent).toContain('Net 30');
      expect(fixture.nativeElement.textContent).toContain('FOB Karachi');
    });

    it('shows a not-found state for another organization\'s quotation or a bad link', async () => {
      await setup(null, { loadFails: 404 });
      expect(component.notFound).toBeTrue();
      expect(el('not-found')).not.toBeNull();
    });
  });

  // ── A32-PC-11 — lines table ────────────────────────────────────────────────

  describe('lines table (A32-PC-11)', () => {
    it('lists alternatives indented under the rejected line, lettered as in §10.3', async () => {
      await setup();
      expect(all('line-label')).toEqual(['1', '2', '3', '3a', '3b', '5']);
      const rows = fixture.nativeElement.querySelectorAll('[data-testid="line-row"]');
      expect(rows[3].classList).toContain('line-alt');
      expect(rows[4].classList).toContain('line-alt');
      expect(rows[0].classList).not.toContain('line-alt');
    });

    it('badges each line by type: NORMAL, ✕ REJ greyed out, ◇ ALT', async () => {
      await setup();
      expect(all('line-type')).toEqual(['NORMAL', 'NORMAL', '✕ REJ', '◇ ALT', '◇ ALT', 'NORMAL']);
      const rows = fixture.nativeElement.querySelectorAll('[data-testid="line-row"]');
      expect(rows[2].classList).toContain('line-rejected');
    });

    it('gives a rejected line its reason and no figures, and an alternative the line it replaces', async () => {
      await setup();
      const rows = fixture.nativeElement.querySelectorAll('[data-testid="line-row"]');
      const rejected = rows[2].textContent.replace(/\s+/g, ' ');
      expect(rejected).toContain('Reason: Discontinued');
      expect(rejected).toContain('Mill stopped the grade');
      expect(rows[2].querySelector('[data-testid="line-total"]').textContent.trim()).toBe('—');
      expect(rows[2].querySelector('[data-testid="line-qty"]').textContent.trim()).toBe('—');
      expect(rows[4].textContent.replace(/\s+/g, ' ')).toContain('Alternative for line 3 (lower grade option)');
      expect(rows[3].querySelector('[data-testid="line-total"]').textContent).toContain('1,700.00');
    });

    it('shows the tax as CODE · rate% when the line has a code', async () => {
      const lines = [qLine({ taxCodeUuid: 'gst17', taxCode: 'GST17', taxPercent: 17 })];
      await setup(quotation({ lines }));
      expect(all('line-tax')[0]).toBe('GST17 · 17%');
    });

    it('shows the customer\'s response per line: pending, accepted, a counter with its price, none on a rejected line', async () => {
      const lines = [
        qLine({ customerResponse: 'ACCEPTED' }),
        qLine({ uuid: 'l-2', lineNumber: 2, customerResponse: 'COUNTER', customerCounterPrice: 0.1 }),
        qLine({ uuid: 'l-3', lineNumber: 3, lineType: 'REJECTED', rejectionReasonDescription: 'Out of stock' }),
        qLine({ uuid: 'l-4', lineNumber: 4 })
      ];
      await setup(quotation({ status: 'SENT', isEditable: false, allowedActions: ['COPY'], lines }));
      expect(all('line-response')).toEqual(['Accepted', 'Counter 0.10', '—', 'Pending']);
    });

    it('totals the quotation in the footer from the server\'s figures', async () => {
      await setup(quotation({ subtotal: 6470, taxAmount: 1099.9, discountAmount: 10, grandTotal: 7559.9 }));
      expect(el('subtotal')!.textContent).toContain('6,470.00');
      expect(el('tax-total')!.textContent).toContain('1,099.90');
      expect(el('grand-total')!.textContent).toContain('7,559.90');
      expect(el('grand-total')!.textContent).toContain('USD');
    });
  });

  // ── Line changes in DRAFT (A32-PC-12 host) ─────────────────────────────────

  describe('changing lines (A32-PC-12)', () => {
    it('offers add, edit and delete only on a draft, to someone who may edit', async () => {
      permissions = ['SALE_QUOTATION_VIEW', 'SALE_QUOTATION_EDIT'];
      await setup();
      expect(el('add-line')).not.toBeNull();
      expect(all('edit-line').length).toBe(6);

      await setup(quotation({ status: 'SENT', isEditable: false, allowedActions: ['RECORD_RESPONSE', 'ACCEPT', 'REJECT', 'COPY'] }));
      expect(el('add-line')).toBeNull();
      expect(el('edit-line')).toBeNull();

      permissions = ['SALE_QUOTATION_VIEW'];
      await setup();
      expect(el('add-line')).withContext('viewing is not editing').toBeNull();
      expect(el('delete-line')).toBeNull();
    });

    it('opens the line editor for a new line and for an existing one', async () => {
      permissions = ['SALE_QUOTATION_VIEW', 'SALE_QUOTATION_EDIT'];
      await setup();
      component.openAddLine();
      expect(component.lineDialogVisible).toBeTrue();
      expect(component.editingLine).toBeNull();

      component.openEditLine(component.quotation!.lines[1]);
      expect(component.editingLine?.uuid).toBe('l-2');
    });

    it('reloads after the editor saves, and closes it', async () => {
      permissions = ['SALE_QUOTATION_VIEW', 'SALE_QUOTATION_EDIT'];
      await setup();
      component.openAddLine();
      component.onLineSaved();
      expect(component.lineDialogVisible).toBeFalse();
      expect(service.getQuotation).toHaveBeenCalledTimes(2);
    });

    it('deletes a line and reloads, but will not delete a rejected line that still has alternatives', async () => {
      permissions = ['SALE_QUOTATION_VIEW', 'SALE_QUOTATION_EDIT'];
      await setup();
      service.deleteQuotationLine.and.returnValue(ok(null));

      const rejected = component.quotation!.lines.find(l => l.uuid === 'l-3')!;
      expect(component.deleteBlockedReason(rejected)).toContain('alternatives');
      component.deleteLine(rejected);
      expect(component.lineToDelete).toBeNull();

      component.deleteLine(component.quotation!.lines[0]);
      expect(service.deleteQuotationLine).withContext('asks first').not.toHaveBeenCalled();
      component.confirmDeleteLine();
      expect(service.deleteQuotationLine).toHaveBeenCalledWith('sq-1', 'l-1');
      expect(component.lineToDelete).toBeNull();
      expect(service.getQuotation).toHaveBeenCalledTimes(2);
    });
  });

  // ── A32-PC-13 host ──────────────────────────────────────────────────────────

  describe('customer response panel (A32-PC-13 host)', () => {
    const sent = () => quotation({ status: 'SENT', isEditable: false, sentAt: '2026-10-04T09:15:00Z', allowedActions: ['RECORD_RESPONSE', 'ACCEPT', 'REJECT', 'COPY'] });

    it('appears on a sent quotation for someone who may edit, and not otherwise', async () => {
      permissions = ['SALE_QUOTATION_VIEW', 'SALE_QUOTATION_EDIT'];
      await setup(sent());
      expect(el('response-panel')).not.toBeNull();

      permissions = ['SALE_QUOTATION_VIEW'];
      await setup(sent());
      expect(el('response-panel')).toBeNull();

      permissions = ['SALE_QUOTATION_VIEW', 'SALE_QUOTATION_EDIT'];
      await setup(quotation());
      expect(el('response-panel')).withContext('draft').toBeNull();
    });
  });

  // ── A32-PC-14 — attachments, send, convert ──────────────────────────────────

  describe('attachments (A32-PC-14)', () => {
    it('lists the quotation\'s files under the SALE_QUOTATION interface code', async () => {
      await setup();
      const list = fixture.debugElement.query(By.directive(AttachmentListComponent));
      expect(list).not.toBeNull();
      expect(list.componentInstance.interfaceCode).toBe('SALE_QUOTATION');
      expect(list.componentInstance.documentId).toBe('sq-1');
    });
  });

  describe('send to customer (A32-PC-14)', () => {
    it('is offered on a draft the server says can be sent, only to someone with SALE_QUOTATION_SEND', async () => {
      permissions = ['SALE_QUOTATION_VIEW', 'SALE_QUOTATION_SEND'];
      await setup();
      expect(el('action-send')).not.toBeNull();

      await setup(quotation({ allowedActions: ['COPY'] }));
      expect(el('action-send')).withContext('no NORMAL/ALTERNATIVE line yet').toBeNull();

      permissions = ['SALE_QUOTATION_VIEW', 'SALE_QUOTATION_EDIT'];
      await setup();
      expect(el('action-send')).withContext('editing is not sending').toBeNull();
    });

    it('asks first, then sends and reloads', async () => {
      permissions = ['SALE_QUOTATION_VIEW', 'SALE_QUOTATION_SEND'];
      await setup();
      service.sendQuotation.and.returnValue(ok(null));

      component.openSendDialog();
      expect(component.sendDialogVisible).toBeTrue();
      expect(service.sendQuotation).not.toHaveBeenCalled();

      component.confirmSend();
      expect(service.sendQuotation).toHaveBeenCalledOnceWith('sq-1');
      expect(component.sendDialogVisible).toBeFalse();
      expect(service.getQuotation).toHaveBeenCalledTimes(2);
    });

    it('says why the server refused to send, and stays on the draft', async () => {
      permissions = ['SALE_QUOTATION_VIEW', 'SALE_QUOTATION_SEND'];
      await setup();
      const toast = spyOn(fixture.debugElement.injector.get(MessageService), 'add').and.callThrough();
      service.sendQuotation.and.returnValue(fail(400, 'Add at least one NORMAL or ALTERNATIVE line before sending.'));

      component.openSendDialog();
      component.confirmSend();
      expect(toast).toHaveBeenCalledWith(jasmine.objectContaining({ severity: 'error', detail: 'Add at least one NORMAL or ALTERNATIVE line before sending.' }));
      expect(service.getQuotation).toHaveBeenCalledTimes(1);
    });

    it('shows a Sent badge with when it was sent', async () => {
      await setup(quotation({ status: 'SENT', isEditable: false, sentAt: '2026-10-04T09:15:00Z', sentByUserName: 'Ayesha', allowedActions: ['COPY'] }));
      const badge = el('sent-badge')!;
      expect(badge).not.toBeNull();
      expect(badge.textContent).toContain('Sent');
      expect(badge.textContent).toContain('04 Oct 2026');

      await setup();
      expect(el('sent-badge')).toBeNull();
    });
  });

  describe('convert to sale order (A32-PC-14)', () => {
    const accepted = () => quotation({ status: 'ACCEPTED', isEditable: false, sentAt: '2026-10-04T09:15:00Z', allowedActions: ['CONVERT', 'COPY'] });

    it('is offered on an accepted quotation only to someone with SALE_ORDER_CREATE', async () => {
      permissions = ['SALE_QUOTATION_VIEW', 'SALE_ORDER_CREATE'];
      await setup(accepted());
      expect(el('action-convert')).not.toBeNull();

      permissions = ['SALE_QUOTATION_VIEW', 'SALE_QUOTATION_EDIT'];
      await setup(accepted());
      expect(el('action-convert')).toBeNull();

      permissions = ['SALE_QUOTATION_VIEW', 'SALE_ORDER_CREATE'];
      await setup(quotation({ status: 'SENT', isEditable: false, allowedActions: ['RECORD_RESPONSE', 'ACCEPT', 'REJECT'] }));
      expect(el('action-convert')).toBeNull();
    });

    it('asks for the customer PO, converts, and opens the new sale order', async () => {
      permissions = ['SALE_QUOTATION_VIEW', 'SALE_ORDER_CREATE'];
      await setup(accepted());
      const navigate = spyOn(router, 'navigate').and.resolveTo(true);
      service.convertQuotationToOrder.and.returnValue(ok('so-99'));

      component.openConvertDialog();
      expect(component.convertDialogVisible).toBeTrue();
      expect(addresses.getAddresses).toHaveBeenCalledWith('p-1');
      expect(component.convert.deliveryMode).toBe('SHIP');

      component.convert.customerPoReference = '  PO-55120 ';
      component.convert.customerPoDate = new Date(2026, 9, 5);
      component.convert.shippingAddressId = 'addr-1';
      component.confirmConvert();

      expect(service.convertQuotationToOrder).toHaveBeenCalledOnceWith('sq-1', jasmine.objectContaining({
        customerPoReference: 'PO-55120', customerPoDate: '2026-10-05', deliveryMode: 'SHIP', shippingAddressId: 'addr-1'
      }));
      expect(navigate).toHaveBeenCalledWith(['/portal/pages/sales/orders', 'so-99']);
    });

    it('needs an address to ship to, and none to have the customer collect', async () => {
      permissions = ['SALE_QUOTATION_VIEW', 'SALE_ORDER_CREATE'];
      await setup(accepted());
      component.openConvertDialog();

      component.convert.shippingAddressId = null;
      expect(component.canConfirmConvert).toBeFalse();
      component.confirmConvert();
      expect(service.convertQuotationToOrder).not.toHaveBeenCalled();

      component.convert.deliveryMode = 'SELF_PICKUP';
      expect(component.canConfirmConvert).toBeTrue();
    });

    it('sends no PO fields when none were given, and keeps the dialog open on a refusal', async () => {
      permissions = ['SALE_QUOTATION_VIEW', 'SALE_ORDER_CREATE'];
      await setup(accepted());
      service.convertQuotationToOrder.and.returnValue(fail(409, 'This quotation has already been converted.'));
      component.openConvertDialog();
      component.convert.deliveryMode = 'SELF_PICKUP';
      component.confirmConvert();

      const req = service.convertQuotationToOrder.calls.mostRecent().args[1];
      expect(req.customerPoReference).toBeNull();
      expect(req.customerPoDate).toBeNull();
      expect(req.shippingAddressId).toBeNull();
      expect(component.convertDialogVisible).toBeTrue();
      expect(component.convertError).toBe('This quotation has already been converted.');
    });

    it('copies a quotation into a new draft for someone who may create one, and opens it', async () => {
      permissions = ['SALE_QUOTATION_VIEW'];
      await setup(accepted());
      expect(el('action-copy')).toBeNull();

      permissions = ['SALE_QUOTATION_VIEW', 'SALE_QUOTATION_CREATE'];
      await setup(accepted());
      expect(el('action-copy')).not.toBeNull();
      const navigate = spyOn(router, 'navigate').and.resolveTo(true);
      service.copyQuotation.and.returnValue(ok('sq-copy'));
      component.copy();
      expect(service.copyQuotation).toHaveBeenCalledOnceWith('sq-1');
      expect(navigate).toHaveBeenCalledWith(['/portal/pages/sales/quotations', 'sq-copy']);
    });

    it('links the sale order a converted quotation became', async () => {
      await setup(quotation({ status: 'CONVERTED', isEditable: false, allowedActions: ['COPY'], saleOrder: { uuid: 'so-99', number: 'SO-2026-00120', status: 'DRAFT' } }));
      const link = el('sale-order')!.querySelector('a')!;
      expect(link.textContent!.trim()).toBe('SO-2026-00120');
      expect(link.getAttribute('href')).toBe('/portal/pages/sales/orders/so-99');
    });
  });
});
