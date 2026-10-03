import { WritableSignal, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute, Router, convertToParamMap } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { Observable, of, throwError } from 'rxjs';

import { SaleOrderFormComponent } from '../sales/sale-orders/sale-order-form/sale-order-form.component';
import { InvoiceCreateComponent } from './invoices/invoice-create/invoice-create.component';
import { InvoiceDetailComponent } from './invoices/invoice-detail/invoice-detail.component';
import { SalesInvoiceDetailComponent } from './sales-invoices/sales-invoice-detail/sales-invoice-detail.component';

import { SaleOrderService, SaleOrderModel } from '../../services/sale-order.service';
import { BusinessPartnerService, BusinessPartnerModel } from '../../services/business-partner.service';
import { InventoryService } from '../../services/inventory.service';
import { PricingRuleService } from '../../services/pricing-rule.service';
import { AddressService } from '../../services/address.service';
import { CurrenciesService } from '../../services/currencies.service';
import { FinanceSetupService, TaxCodeModel } from '../../services/finance-setup.service';
import { FinanceService, InvoiceDetailModel } from '../../services/finance.service';
import { DemandService } from '../../services/demand.service';
import { SupplierService } from '../../services/supplier.service';
import { WarehouseService } from '../../services/warehouse.service';
import { SalesInvoiceLineModel } from '../../services/sales-invoice.service';
import { AuthService } from '../service/auth.service';
import { CurrentTenant, TenantService } from '../service/tenant.service';
import {
  MAX_TAX_CODE_LENGTH as SETUP_MAX_TAX_CODE_LENGTH, normalizeTaxCode as setupNormalizeTaxCode, usageLabel as setupUsageLabel
} from '../finance-setup/finance-setup.shared';
import {
  MAX_TAX_CODE_LENGTH as QBO_MAX_TAX_CODE_LENGTH, normalizeTaxCode as qboNormalizeTaxCode, taxCodeUsageLabel as qboUsageLabel
} from '../integrations/quickbooks/mappings-tab/mappings-tab.component';

/**
 * Cross-page consistency (SAP alignment, S-3): a tax code behaves the same on a sale-order line and on a
 * supplier-invoice header. Both pre-select the default, make the rate/amount read-only once a code is
 * chosen, fall back to manual entry when there are no codes (or they cannot be loaded), keep an old
 * record's retired code visible as "(no longer active)" and write a code as `CODE · 17%`.
 *
 * Each page has its own spec; this one pins the rules that hold *between* them, so one page cannot drift.
 */

function ok<T>(result: T) {
  return of({ success: true, message: '', result } as any);
}

function down(): Observable<never> {
  return throwError(() => ({ status: 500 }));
}

function taxCode(uuid: string, code: string, rate: number, opts: Partial<TaxCodeModel> = {}): TaxCodeModel {
  return { uuid, code, name: `${code.replace(/\d+/, ' ')}${rate}%`.trim(), description: null, ratePercent: rate, usage: 'BOTH', isDefault: false, isActive: true, ...opts };
}

/** The same two codes are offered on both sides (usage BOTH): the default 17% and a reduced 7.5%. */
const GST17 = taxCode('gst17', 'GST17', 17, { isDefault: true });
const RED7 = taxCode('red7', 'RED7', 7.5);
/** A code an old record carries that has since been deactivated, so the list no longer returns it. */
const RETIRED = { uuid: 'gst16', code: 'GST16', rate: 16 };

// ── Page harnesses ───────────────────────────────────────────────────────────

async function saleOrderForm(opts: { codes?: TaxCodeModel[]; codesFail?: boolean; edit?: SaleOrderModel } = {}) {
  const sales = jasmine.createSpyObj<SaleOrderService>('SaleOrderService', ['getSaleOrderById', 'createSaleOrder', 'updateSaleOrder', 'getDefaults']);
  sales.getDefaults.and.returnValue(ok({ deliveryMode: 'SHIP', selfPickupEnabled: true }));
  sales.getSaleOrderById.and.returnValue(ok(opts.edit ?? null));
  const partners = jasmine.createSpyObj<BusinessPartnerService>('BusinessPartnerService', ['getPartners', 'getPartnerById']);
  partners.getPartners.and.returnValue(ok({ data: [], totalRecords: 0 }));
  partners.getPartnerById.and.returnValue(ok({ uuid: 'cust-1', companyName: 'Acme Ltd' } as BusinessPartnerModel));
  const inventory = jasmine.createSpyObj<InventoryService>('InventoryService', ['getProducts', 'getProductById']);
  inventory.getProducts.and.returnValue(ok({ data: [], totalRecords: 0 }));
  const pricing = jasmine.createSpyObj<PricingRuleService>('PricingRuleService', ['resolvePrice']);
  pricing.resolvePrice.and.returnValue(ok({ found: true, unitPrice: 40 }));
  const addresses = jasmine.createSpyObj<AddressService>('AddressService', ['getAddresses', 'getAddress', 'createAddress']);
  addresses.getAddresses.and.returnValue(ok([]));
  addresses.getAddress.and.returnValue(ok(null));
  const currencies = jasmine.createSpyObj<CurrenciesService>('CurrenciesService', ['getAll']);
  currencies.getAll.and.returnValue(ok([{ id: 'cur-pkr', name: 'Pakistani Rupee', code: 'PKR', symbol: 'Rs' }]));
  const setup = jasmine.createSpyObj<FinanceSetupService>('FinanceSetupService', ['getTaxCodes', 'quoteExchangeRate']);
  setup.getTaxCodes.and.returnValue(opts.codesFail ? down() : ok(opts.codes ?? [GST17, RED7]));
  setup.quoteExchangeRate.and.returnValue(ok(null));
  const tenant: WritableSignal<CurrentTenant | null> = signal({
    id: 'org-1', orgCode: 'SCM', orgName: 'SCM', plan: 'BASIC', baseCurrency: 'cur-pkr',
    enabledFeatureCodes: [], isSuperAdmin: false, roleName: 'Sales', permissions: []
  } as CurrentTenant);
  const auth = { hasPermission: () => true, hasAnyPermission: () => true } as unknown as AuthService;

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
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map(opts.edit ? [['uuid', opts.edit.uuid]] : []) } } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(SaleOrderFormComponent);
  spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
  return { fixture, component: fixture.componentInstance };
}

async function invoiceCreate(opts: { codes?: TaxCodeModel[]; codesFail?: boolean } = {}) {
  const finance = jasmine.createSpyObj<FinanceService>('FinanceService', ['createInvoice']);
  finance.createInvoice.and.returnValue(ok('inv-new'));
  const setup = jasmine.createSpyObj<FinanceSetupService>('FinanceSetupService', ['getTaxCodes']);
  setup.getTaxCodes.and.returnValue(opts.codesFail ? down() : ok(opts.codes ?? [GST17, RED7]));
  const currencies = jasmine.createSpyObj<CurrenciesService>('CurrenciesService', ['getAll']);
  currencies.getAll.and.returnValue(ok([{ id: 'cur-pkr', name: 'Pakistani Rupee', code: 'PKR', symbol: 'Rs' }]));
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
  return { fixture, component: fixture.componentInstance };
}

function supplierInvoice(overrides: Partial<InvoiceDetailModel> = {}): InvoiceDetailModel {
  return {
    uuid: 'inv-1', invoiceNumber: 'INV-2026-00042', supplierInvoiceNo: 'KSW/881', supplierId: 'sup-1', supplierName: 'Karachi Steel',
    poUuid: 'po-1', poNumber: 'PO-2026-00007', invoiceDate: '2026-09-15T00:00:00', receivedDate: '2026-09-16T00:00:00',
    dueDate: '2026-10-15T00:00:00', currency: 'PKR', subtotal: 1000, taxAmount: 160, totalAmount: 1160,
    taxCodeUuid: RETIRED.uuid, taxCode: RETIRED.code, taxPercent: RETIRED.rate,
    matchedPoValue: 1000, matchedGrnValue: 1000, varianceAmount: 0, matchStatus: 'Pending', paymentStatus: 'Unpaid',
    paidAmount: 0, createdDate: '2026-09-16T10:00:00',
    lines: [], payments: [], debitNotes: [], creditNotes: [],
    ...overrides
  };
}

async function invoiceDetail(inv: InvoiceDetailModel, opts: { codes?: TaxCodeModel[]; codesFail?: boolean } = {}) {
  const finance = jasmine.createSpyObj<FinanceService>('FinanceService',
    ['getInvoiceById', 'getSupplierPayments', 'reverseInvoice', 'patchInvoice', 'approveInvoice', 'rejectInvoice', 'resolveFileUrl', 'downloadInvoicePdf']);
  finance.getInvoiceById.and.returnValue(ok(inv));
  finance.getSupplierPayments.and.returnValue(ok({ data: [], totalRecords: 0 }));
  const setup = jasmine.createSpyObj<FinanceSetupService>('FinanceSetupService', ['getTaxCodes']);
  setup.getTaxCodes.and.returnValue(opts.codesFail ? down() : ok(opts.codes ?? [GST17, RED7]));
  const auth = { hasPermission: () => true, hasAnyPermission: () => true } as unknown as AuthService;

  await TestBed.resetTestingModule().configureTestingModule({
    imports: [InvoiceDetailComponent],
    providers: [
      provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(),
      { provide: FinanceService, useValue: finance },
      { provide: FinanceSetupService, useValue: setup },
      { provide: AuthService, useValue: auth },
      { provide: ActivatedRoute, useValue: { snapshot: { paramMap: convertToParamMap({ uuid: inv.uuid }) } } }
    ]
  }).compileComponents();

  const fixture = TestBed.createComponent(InvoiceDetailComponent);
  fixture.detectChanges();
  fixture.componentInstance.openEditDialog();
  fixture.detectChanges();
  return { fixture, component: fixture.componentInstance };
}

function draftOrderOnRetiredCode(): SaleOrderModel {
  return {
    uuid: 'so-1', traceId: 't-1', soNumber: 'SO-2026-00042', partnerId: 'cust-1',
    orderDate: '2026-09-01T00:00:00', currencyId: 'cur-pkr',
    subtotal: 4000, taxAmount: 640, discountAmount: 0, grandTotal: 4640,
    status: 'DRAFT', requiresShipment: false, deliveryMode: 'SELF_PICKUP', createdDate: '2026-09-01T00:00:00',
    lines: [{
      uuid: 'l1', variantUuid: 'v1', itemDescription: '4mm cable', quantity: 100, unitPrice: 40, discountPercent: 0,
      taxPercent: RETIRED.rate, taxCodeUuid: RETIRED.uuid, taxCode: RETIRED.code, lineTotal: 4640,
      fulfilledQty: 0, invoicedQty: 0, status: 'OPEN'
    }]
  };
}

const labelOf = (options: { label: string; value: string | null }[], value: string) => options.find(o => o.value === value)?.label;

describe('SAP alignment consistency: Settings → Tax Codes and the QuickBooks tax mapping describe a code alike', () => {
  it('use the same words for who may use a code, and the same longest code', () => {
    for (const usage of ['SALES', 'PURCHASE', 'BOTH'] as const) {
      expect(qboUsageLabel(usage)).withContext(usage).toBe(setupUsageLabel(usage));
    }
    expect(QBO_MAX_TAX_CODE_LENGTH).toBe(SETUP_MAX_TAX_CODE_LENGTH);
    expect(qboNormalizeTaxCode(' gst17 ')).toBe(setupNormalizeTaxCode(' gst17 '));
  });
});

// ── The rules ────────────────────────────────────────────────────────────────

describe('SAP alignment consistency: tax codes on the sale-order line and the supplier invoice', () => {

  describe('a code is written CODE · 17%', () => {
    it('on the sale-order line dropdown', async () => {
      const { component } = await saleOrderForm();

      expect(labelOf(component.taxCodeOptions, 'gst17')).toMatch(/^GST17 · 17%/);
      expect(labelOf(component.taxCodeOptions, 'red7')).toMatch(/^RED7 · 7\.5%/);
    });

    it('on the supplier-invoice create dropdown', async () => {
      const { component } = await invoiceCreate();

      expect(labelOf(component.taxCodeOptions, 'gst17')).withContext('invoice-create label').toMatch(/^GST17 · 17%/);
      expect(labelOf(component.taxCodeOptions, 'red7')).withContext('invoice-create label').toMatch(/^RED7 · 7\.5%/);
    });

    it('in the supplier-invoice edit dialog dropdown', async () => {
      const { component } = await invoiceDetail(supplierInvoice({ taxCodeUuid: null, taxCode: null, taxPercent: null }));

      expect(labelOf(component.taxCodeOptions, 'gst17')).withContext('invoice-detail edit label').toMatch(/^GST17 · 17%/);
      expect(labelOf(component.taxCodeOptions, 'red7')).withContext('invoice-detail edit label').toMatch(/^RED7 · 7\.5%/);
    });

    it('in the same words on the sale-order line, the supplier invoice and the sales invoice it becomes', async () => {
      const { component: so } = await saleOrderForm();
      const { component: create } = await invoiceCreate();
      const { component: edit } = await invoiceDetail(supplierInvoice({ taxCodeUuid: null, taxCode: null, taxPercent: null }));

      // Whatever follows the code and rate (a name, say), the three dropdowns offer the same text for it.
      expect(labelOf(create.taxCodeOptions, 'gst17')).toBe(labelOf(so.taxCodeOptions, 'gst17'));
      expect(labelOf(edit.taxCodeOptions, 'gst17')).toBe(labelOf(so.taxCodeOptions, 'gst17'));
    });

    it('on the read-only displays: sales-invoice line and supplier-invoice tax label', () => {
      const salesDetail = Object.create(SalesInvoiceDetailComponent.prototype) as SalesInvoiceDetailComponent;
      const line = { taxCode: 'RED7', taxPercent: 7.5 } as SalesInvoiceLineModel;
      expect(salesDetail.taxLabel(line)).toBe('RED7 · 7.5%');

      const supplierDetail = Object.create(InvoiceDetailComponent.prototype) as InvoiceDetailComponent;
      supplierDetail.invoice = supplierInvoice({ taxCodeUuid: 'red7', taxCode: 'RED7', taxPercent: 7.5 });
      expect(supplierDetail.taxLabel).toBe('Tax (RED7 · 7.5%)');
    });
  });

  describe('the default code is pre-selected', () => {
    it('on a new sale-order line and on a new supplier invoice alike', async () => {
      const { component: so } = await saleOrderForm();
      const { component: create } = await invoiceCreate();

      expect(so.lineControl(0, 'taxCodeUuid').value).toBe('gst17');
      expect(so.lineControl(0, 'taxPercent').value).toBe(17);
      expect(create.form.taxCodeUuid).toBe('gst17');
    });
  });

  describe('with a code chosen the rate / amount is read-only', () => {
    it('on the sale-order line and on the supplier invoice', async () => {
      const so = await saleOrderForm();
      const soInput = so.fixture.nativeElement.querySelector('[data-testid="tax"] input') as HTMLInputElement | null;
      expect(so.component.lineHasTaxCode(0)).toBeTrue();
      expect(soInput?.readOnly).withContext('sale-order tax % input').toBeTrue();

      const create = await invoiceCreate();
      create.fixture.detectChanges();
      const el = create.fixture.nativeElement as HTMLElement;
      expect(el.querySelector('[data-testid="invoice-tax-amount"]')).withContext('typed amount hidden').toBeNull();
      expect(el.querySelector('[data-testid="invoice-tax-computed"]')).withContext('computed amount shown').not.toBeNull();
    });
  });

  describe('with no codes, or none loadable, the tax is entered by hand', () => {
    for (const scenario of [{ name: 'no codes exist', codes: [] as TaxCodeModel[], codesFail: false }, { name: 'the load fails', codes: undefined, codesFail: true }]) {
      it(`on both pages when ${scenario.name}`, async () => {
        const so = await saleOrderForm({ codes: scenario.codes, codesFail: scenario.codesFail });
        expect(so.component.showTaxCodes).withContext('sale order shows the code dropdown').toBeFalse();
        expect(so.component.lineControl(0, 'taxCodeUuid').value).toBeNull();
        expect(so.fixture.nativeElement.querySelector('[data-testid="tax"] input')?.readOnly).withContext('sale order % typed').toBeFalse();

        const create = await invoiceCreate({ codes: scenario.codes, codesFail: scenario.codesFail });
        create.fixture.detectChanges();
        expect(create.component.form.taxCodeUuid).toBeNull();
        expect(create.fixture.nativeElement.querySelector('[data-testid="invoice-tax-amount"]')).withContext('supplier invoice amount typed').not.toBeNull();
      });
    }
  });

  describe("an old record's retired code still shows, marked (no longer active)", () => {
    it('on a sale-order line being edited', async () => {
      const { component } = await saleOrderForm({ edit: draftOrderOnRetiredCode() });

      expect(component.lineControl(0, 'taxCodeUuid').value).toBe(RETIRED.uuid);
      expect(labelOf(component.taxCodeOptions, RETIRED.uuid)).toBe('GST16 · 16% (no longer active)');
    });

    it('on a supplier invoice being edited', async () => {
      const { component } = await invoiceDetail(supplierInvoice());

      expect(component.editForm.taxCodeUuid).toBe(RETIRED.uuid);
      expect(labelOf(component.taxCodeOptions, RETIRED.uuid)).withContext('invoice-detail retired option').toBe('GST16 · 16% (no longer active)');
    });
  });

  describe("an old record's code still shows when the codes cannot be loaded (never blank)", () => {
    it('on a sale-order line being edited', async () => {
      const { component } = await saleOrderForm({ edit: draftOrderOnRetiredCode(), codesFail: true });

      expect(component.showTaxCodes).toBeTrue();
      expect(labelOf(component.taxCodeOptions, RETIRED.uuid)).toBe('GST16 · 16%');
    });

    it('on a supplier invoice being edited', async () => {
      const { component } = await invoiceDetail(supplierInvoice(), { codesFail: true });

      expect(component.editForm.taxCodeUuid).toBe(RETIRED.uuid);
      // Without an option for its own code the dropdown falls back to its placeholder, "No tax code — enter
      // the amount", on an invoice whose tax does come from a code.
      expect(labelOf(component.taxCodeOptions, RETIRED.uuid)).withContext('invoice-detail own code option after a failed load').toContain('GST16 · 16%');
    });
  });
});
