import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of } from 'rxjs';

import { ExchangeRatesComponent } from '../finance-setup/exchange-rates/exchange-rates.component';
import { SalesInvoiceDetailComponent } from './sales-invoices/sales-invoice-detail/sales-invoice-detail.component';
import { SaleOrderFormComponent } from '../sales/sale-orders/sale-order-form/sale-order-form.component';
import { InvoiceCreateComponent } from './invoices/invoice-create/invoice-create.component';
import { InvoiceDetailComponent } from './invoices/invoice-detail/invoice-detail.component';
import { MappingFormValue, toSettingsRequest } from '../integrations/quickbooks/mappings-tab/mappings-tab.component';

import { ExchangeRateModel, FinanceSetupService, SaveExchangeRateRequest } from '../../services/finance-setup.service';
import { CurrenciesService } from '../../services/currencies.service';
import { SalesInvoiceService, SalesInvoiceDetailModel, UpdateSalesInvoiceRequest } from '../../services/sales-invoice.service';
import { AttachmentService } from '../../services/attachment.service';
import { SaleOrderService, SaleOrderModel, CreateSaleOrderRequest } from '../../services/sale-order.service';
import { BusinessPartnerService, BusinessPartnerModel } from '../../services/business-partner.service';
import { InventoryService } from '../../services/inventory.service';
import { PricingRuleService } from '../../services/pricing-rule.service';
import { AddressService } from '../../services/address.service';
import { FinanceService, CreateInvoiceRequest, InvoiceDetailModel } from '../../services/finance.service';
import { DemandService } from '../../services/demand.service';
import { SupplierService } from '../../services/supplier.service';
import { WarehouseService } from '../../services/warehouse.service';
import { AuthService } from '../service/auth.service';
import { CurrentTenant, TenantService } from '../service/tenant.service';

/**
 * Cross-page consistency (SAP alignment, S-4/S-5): a date that is only a date (an exchange rate's effective
 * date, an invoice date, a due date, an expected delivery date) is sent as yyyy-MM-dd — the day the person
 * picked — and an ISO timestamp from the server is read back as the calendar day it names.
 *
 * A date picker hands over local midnight. East of UTC (this suite's reason to exist; Pakistan is UTC+5),
 * `toISOString()` turns 1 Oct 00:00 into "2026-09-30T19:00:00Z", and the rate or due date lands a day early.
 * These checks only bite in a non-UTC time zone, so they say so rather than pass vacuously in UTC.
 */

function ok<T>(result: T, message = '') {
  return of({ success: true, message, result } as any);
}

const PICKED = new Date(2026, 9, 1);           // 1 Oct 2026, local midnight — what a date picker gives
const PICKED_ISO = '2026-10-01';
const auth = { hasPermission: () => true, hasAnyPermission: () => true, isAuthenticated: () => true } as unknown as AuthService;

describe('SAP alignment consistency: dates go to the server as the day picked', () => {
  beforeAll(() => {
    // The bug this guards against (local midnight through UTC) cannot show in UTC itself.
    if (new Date(2026, 9, 1).getTimezoneOffset() === 0) {
      console.warn('sap-dates.consistency: running in UTC — the day-shift checks below cannot fail here.');
    }
  });

  it('runs in a time zone where local midnight is a different UTC day (else these checks prove nothing)', () => {
    expect(PICKED.toISOString().slice(0, 10)).withContext(`TZ offset ${PICKED.getTimezoneOffset()} min`).not.toBe(PICKED_ISO);
  });

  // ── Settings → Exchange Rates ─────────────────────────────────────────────

  describe('exchange rates', () => {
    let service: jasmine.SpyObj<FinanceSetupService>;

    async function page(rates: ExchangeRateModel[] = []) {
      service = jasmine.createSpyObj<FinanceSetupService>('FinanceSetupService',
        ['getExchangeRates', 'createExchangeRate', 'updateExchangeRate', 'deleteExchangeRate', 'quoteExchangeRate']);
      service.getExchangeRates.and.returnValue(ok(rates));
      service.createExchangeRate.and.callFake(req => ok({ ...req, uuid: 'new', source: 'MANUAL', notes: null, createdDate: '' }));
      service.updateExchangeRate.and.callFake((uuid, req) => ok({ ...req, uuid, source: 'MANUAL', notes: null, createdDate: '' }));
      service.quoteExchangeRate.and.returnValue(ok(null));
      const currencies = jasmine.createSpyObj<CurrenciesService>('CurrenciesService', ['getAll']);
      currencies.getAll.and.returnValue(ok([{ id: 'c1', name: 'US Dollar', code: 'USD' }, { id: 'c2', name: 'Pakistani Rupee', code: 'PKR' }]));

      await TestBed.resetTestingModule().configureTestingModule({
        imports: [ExchangeRatesComponent],
        providers: [
          provideNoopAnimations(),
          { provide: FinanceSetupService, useValue: service },
          { provide: CurrenciesService, useValue: currencies },
          { provide: AuthService, useValue: auth }
        ]
      }).compileComponents();
      const fixture = TestBed.createComponent(ExchangeRatesComponent);
      fixture.detectChanges();
      return fixture.componentInstance;
    }

    it('saves a new rate effective from the day picked', async () => {
      const c = await page();
      c.openCreate();
      c.draft = { fromCurrencyCode: 'USD', toCurrencyCode: 'PKR', rate: 278.5, effectiveDate: new Date(PICKED), notes: '' };

      c.save();

      expect((service.createExchangeRate.calls.mostRecent().args[0] as SaveExchangeRateRequest).effectiveDate).toBe(PICKED_ISO);
    });

    it('keeps an existing rate on its own day when it is edited and saved', async () => {
      const existing: ExchangeRateModel = {
        uuid: 'r1', fromCurrencyCode: 'USD', toCurrencyCode: 'PKR', rate: 278.5, effectiveDate: PICKED_ISO,
        source: 'MANUAL', notes: null, createdDate: '2026-10-01T08:00:00'
      };
      const c = await page([existing]);
      c.openEdit(existing);
      c.draft = { ...c.draft, rate: 279 };

      c.save();

      expect((service.updateExchangeRate.calls.mostRecent().args[1] as SaveExchangeRateRequest).effectiveDate).toBe(PICKED_ISO);
    });

    it('asks for the rate on the day picked', async () => {
      const c = await page();
      c.quoteFrom = 'USD';
      c.quoteTo = 'PKR';
      c.quoteDate = new Date(PICKED);

      c.checkQuote();

      expect(service.quoteExchangeRate).toHaveBeenCalledWith('USD', 'PKR', PICKED_ISO);
    });
  });

  // ── Sales invoice: the draft's due date ───────────────────────────────────

  describe('sales invoice due date', () => {
    let invoices: jasmine.SpyObj<SalesInvoiceService>;

    async function page(dueDate: string) {
      const draft = {
        uuid: 'si-1', invoiceNumber: 'SINV-1', saleOrderUuid: 'so-1', saleOrderNumber: 'SO-1', partnerId: 'p-1', partnerName: 'Acme',
        invoiceDate: '2026-09-20T00:00:00', dueDate, grandTotal: 100, amountPaid: 0, balanceDue: 100, status: 'DRAFT',
        currencyCode: 'PKR', traceId: 't', subtotal: 100, discountAmount: 0, taxAmount: 0, createdDate: '2026-09-20T09:00:00',
        lines: [], payments: []
      } as SalesInvoiceDetailModel;
      invoices = jasmine.createSpyObj<SalesInvoiceService>('SalesInvoiceService',
        ['getInvoice', 'issueInvoice', 'updateInvoice', 'deleteInvoice', 'attachPdf', 'downloadPdf', 'cancelInvoice']);
      invoices.getInvoice.and.returnValue(ok(draft));
      invoices.updateInvoice.and.returnValue(ok(null));
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
      return fixture.componentInstance;
    }

    for (const sent of ['2026-10-20T00:00:00', '2026-10-20T00:00:00Z']) {
      it(`reads "${sent}" as the 20th and sends it back unchanged`, async () => {
        const c = await page(sent);
        c.openEditDialog();

        expect(c.editDueDate?.getDate()).toBe(20);
        c.saveEdit();
        expect((invoices.updateInvoice.calls.mostRecent().args[1] as UpdateSalesInvoiceRequest).dueDate).toBe('2026-10-20');
      });
    }

    it('sends a newly picked due date as that day', async () => {
      const c = await page('2026-10-20T00:00:00');
      c.openEditDialog();
      c.editDueDate = new Date(PICKED);

      c.saveEdit();

      expect((invoices.updateInvoice.calls.mostRecent().args[1] as UpdateSalesInvoiceRequest).dueDate).toBe(PICKED_ISO);
    });
  });

  // ── Sale order: expected delivery date ───────────────────────────────────

  describe('sale order expected delivery date', () => {
    async function form(edit?: SaleOrderModel) {
      const sales = jasmine.createSpyObj<SaleOrderService>('SaleOrderService', ['getSaleOrderById', 'createSaleOrder', 'updateSaleOrder', 'getDefaults']);
      sales.getDefaults.and.returnValue(ok({ deliveryMode: 'SHIP', selfPickupEnabled: true }));
      sales.getSaleOrderById.and.returnValue(ok(edit ?? null));
      const partners = jasmine.createSpyObj<BusinessPartnerService>('BusinessPartnerService', ['getPartners', 'getPartnerById']);
      partners.getPartners.and.returnValue(ok({ data: [], totalRecords: 0 }));
      partners.getPartnerById.and.returnValue(ok({ uuid: 'cust-1', companyName: 'Acme' } as BusinessPartnerModel));
      const inventory = jasmine.createSpyObj<InventoryService>('InventoryService', ['getProducts', 'getProductById']);
      inventory.getProducts.and.returnValue(ok({ data: [], totalRecords: 0 }));
      const pricing = jasmine.createSpyObj<PricingRuleService>('PricingRuleService', ['resolvePrice']);
      pricing.resolvePrice.and.returnValue(ok({ found: true, unitPrice: 40 }));
      const addresses = jasmine.createSpyObj<AddressService>('AddressService', ['getAddresses', 'getAddress', 'createAddress']);
      addresses.getAddresses.and.returnValue(ok([]));
      addresses.getAddress.and.returnValue(ok(null));
      const currencies = jasmine.createSpyObj<CurrenciesService>('CurrenciesService', ['getAll']);
      currencies.getAll.and.returnValue(ok([{ id: 'cur-pkr', name: 'Pakistani Rupee', code: 'PKR' }]));
      const setup = jasmine.createSpyObj<FinanceSetupService>('FinanceSetupService', ['getTaxCodes', 'quoteExchangeRate']);
      setup.getTaxCodes.and.returnValue(ok([]));
      setup.quoteExchangeRate.and.returnValue(ok(null));
      const tenant = signal({ id: 'org-1', baseCurrency: 'cur-pkr', enabledFeatureCodes: [], isSuperAdmin: false, permissions: [] } as unknown as CurrentTenant);

      await TestBed.resetTestingModule().configureTestingModule({
        imports: [SaleOrderFormComponent],
        providers: [
          provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(), MessageService,
          { provide: SaleOrderService, useValue: sales },
          { provide: BusinessPartnerService, useValue: partners },
          { provide: InventoryService, useValue: inventory },
          { provide: PricingRuleService, useValue: pricing },
          { provide: AddressService, useValue: addresses },
          { provide: CurrenciesService, useValue: currencies },
          { provide: FinanceSetupService, useValue: setup },
          { provide: TenantService, useValue: { tenant, hasFeature: () => true } },
          { provide: AuthService, useValue: auth },
          { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map(edit ? [['uuid', edit.uuid]] : []) } } }
        ]
      }).compileComponents();
      const fixture = TestBed.createComponent(SaleOrderFormComponent);
      spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
      fixture.detectChanges();
      return fixture.componentInstance;
    }

    it('sends a picked expected delivery date as that day', async () => {
      const c = await form();
      c.form.get('customer')!.setValue({ uuid: 'cust-1', companyName: 'Acme' } as BusinessPartnerModel);
      c.form.get('expectedDeliveryDate')!.setValue(new Date(PICKED));

      expect((c.buildRequest() as CreateSaleOrderRequest).expectedDeliveryDate).toBe(PICKED_ISO);
    });

    it("reads a saved order's expected delivery date as its own day and sends it back unchanged", async () => {
      const c = await form({
        uuid: 'so-1', traceId: 't', soNumber: 'SO-1', partnerId: 'cust-1', orderDate: '2026-09-01T00:00:00',
        expectedDeliveryDate: '2026-09-30T00:00:00Z', currencyId: 'cur-pkr', subtotal: 0, taxAmount: 0, discountAmount: 0,
        grandTotal: 0, status: 'DRAFT', requiresShipment: false, deliveryMode: 'SELF_PICKUP', createdDate: '2026-09-01T00:00:00', lines: []
      });

      expect((c.form.get('expectedDeliveryDate')!.value as Date).getDate()).toBe(30);
      expect(c.buildRequest().expectedDeliveryDate).toBe('2026-09-30');
    });
  });

  // ── Supplier invoice: create and edit ────────────────────────────────────

  describe('supplier invoice dates', () => {
    it('are sent as the days picked on create', async () => {
      const finance = jasmine.createSpyObj<FinanceService>('FinanceService', ['createInvoice']);
      finance.createInvoice.and.returnValue(ok('inv-new'));
      const setup = jasmine.createSpyObj<FinanceSetupService>('FinanceSetupService', ['getTaxCodes']);
      setup.getTaxCodes.and.returnValue(ok([]));
      const currencies = jasmine.createSpyObj<CurrenciesService>('CurrenciesService', ['getAll']);
      currencies.getAll.and.returnValue(ok([{ id: 'cur-pkr', name: 'Pakistani Rupee', code: 'PKR' }]));
      const demand = jasmine.createSpyObj<DemandService>('DemandService', ['getPos', 'getPoById']);
      demand.getPos.and.returnValue(ok({ data: [], totalRecords: 0 }));
      const suppliers = jasmine.createSpyObj<SupplierService>('SupplierService', ['getSuppliers']);
      suppliers.getSuppliers.and.returnValue(ok({ data: [], totalRecords: 0 }));
      const warehouse = jasmine.createSpyObj<WarehouseService>('WarehouseService', ['getGrns', 'getGrnById']);
      warehouse.getGrns.and.returnValue(ok({ data: [], totalRecords: 0 }));

      await TestBed.resetTestingModule().configureTestingModule({
        imports: [InvoiceCreateComponent],
        providers: [
          provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(),
          { provide: FinanceService, useValue: finance },
          { provide: FinanceSetupService, useValue: setup },
          { provide: CurrenciesService, useValue: currencies },
          { provide: DemandService, useValue: demand },
          { provide: SupplierService, useValue: suppliers },
          { provide: WarehouseService, useValue: warehouse },
          { provide: TenantService, useValue: { tenant: signal({ baseCurrency: 'cur-pkr' }) } }
        ]
      }).compileComponents();
      const fixture = TestBed.createComponent(InvoiceCreateComponent);
      spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
      fixture.detectChanges();
      const c = fixture.componentInstance;
      c.form.supplierId = 'sup-1';
      c.form.poUuid = 'po-1';
      c.form.subtotal = 1000;
      c.invoiceDateVal = new Date(PICKED);
      c.receivedDateVal = new Date(2026, 9, 2);
      c.dueDateVal = new Date(2026, 9, 31);

      c.save();

      const sent = finance.createInvoice.calls.mostRecent().args[0] as CreateInvoiceRequest;
      expect([sent.invoiceDate, sent.receivedDate, sent.dueDate]).toEqual([PICKED_ISO, '2026-10-02', '2026-10-31']);
    });

    async function detail(inv: Partial<InvoiceDetailModel>) {
      const finance = jasmine.createSpyObj<FinanceService>('FinanceService',
        ['getInvoiceById', 'getSupplierPayments', 'reverseInvoice', 'patchInvoice', 'approveInvoice', 'rejectInvoice', 'resolveFileUrl', 'downloadInvoicePdf']);
      finance.getInvoiceById.and.returnValue(ok({
        uuid: 'inv-1', invoiceNumber: 'INV-1', supplierId: 'sup-1', supplierName: 'Karachi Steel', poUuid: 'po-1', poNumber: 'PO-1',
        invoiceDate: '2026-09-15T00:00:00', receivedDate: '2026-09-16T00:00:00', dueDate: '2026-10-15T00:00:00', currency: 'PKR',
        subtotal: 1000, taxAmount: 0, totalAmount: 1000, matchedPoValue: 1000, matchedGrnValue: 1000, varianceAmount: 0,
        matchStatus: 'Pending', paymentStatus: 'Unpaid', paidAmount: 0, createdDate: '2026-09-16T10:00:00',
        lines: [], payments: [], debitNotes: [], creditNotes: [], ...inv
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
      fixture.componentInstance.openEditDialog();
      return fixture.componentInstance;
    }

    it('leave an untouched due date out of an edit, and send a picked one as that day', async () => {
      const c = await detail({});
      expect(c.buildPatch().dueDate).withContext('untouched due date').toBeUndefined();

      c.editForm.dueDate = new Date(PICKED);
      expect(c.buildPatch().dueDate).toBe(PICKED_ISO);
    });
  });

  // ── QuickBooks: the document start date ──────────────────────────────────

  it('QuickBooks settings send the document start date as the day picked', () => {
    const value = {
      defaultIncomeAccountId: '1', defaultExpenseAccountId: '2', freightExpenseAccountId: null, discountAccountId: null,
      defaultPurchaseTaxCodeId: null, itemTypeDefault: 'NonInventory', partnerScope: 'OnlyWhenReferenced',
      documentStartDate: new Date(PICKED), autoPushCustomers: true, autoPushVendors: true, autoPushItems: true,
      autoPushSalesInvoices: true, autoPushBills: true
    } as MappingFormValue;

    expect(toSettingsRequest(value).documentStartDate).toBe(PICKED_ISO);
  });
});
