import { ComponentFixture, TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, Router } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { InvoiceCreateComponent } from './invoice-create.component';
import { FinanceService, CreateInvoiceRequest, purchaseTaxFor } from '../../../../services/finance.service';
import { FinanceSetupService, TaxCodeModel } from '../../../../services/finance-setup.service';
import { CurrenciesService } from '../../../../services/currencies.service';
import { DemandService } from '../../../../services/demand.service';
import { SupplierService } from '../../../../services/supplier.service';
import { WarehouseService } from '../../../../services/warehouse.service';
import { TenantService } from '../../../service/tenant.service';

function ok<T>(result: T) {
  return of({ success: true, message: '', result } as any);
}

function code(uuid: string, c: string, rate: number, isDefault = false): TaxCodeModel {
  return { uuid, code: c, name: `${c} tax`, description: null, ratePercent: rate, usage: 'PURCHASE', isDefault, isActive: true };
}

const GST17 = code('gst17', 'GST17', 17, true);
const GST5  = code('gst5', 'GST5', 5);

describe('purchaseTaxFor', () => {
  it('works the tax out as the server does: 2dp, half away from zero', () => {
    expect(purchaseTaxFor(1099.99, 17)).toBe(187);
    expect(purchaseTaxFor(150.5, 5)).toBe(7.53);   // 7.525 — not 7.52, which is what 7.525 in binary rounds to
    expect(purchaseTaxFor(1000, 0)).toBe(0);
    expect(purchaseTaxFor(0, 17)).toBe(0);
  });
});

describe('InvoiceCreateComponent', () => {
  let fixture: ComponentFixture<InvoiceCreateComponent>;
  let component: InvoiceCreateComponent;
  let finance: jasmine.SpyObj<FinanceService>;
  let setupService: jasmine.SpyObj<FinanceSetupService>;
  let currencies: jasmine.SpyObj<CurrenciesService>;

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  async function setup(opts: { baseCurrency?: string | null; codes?: TaxCodeModel[]; codesFail?: boolean; pos?: any[] } = {}) {
    finance = jasmine.createSpyObj<FinanceService>('FinanceService', ['createInvoice']);
    finance.createInvoice.and.returnValue(ok('inv-new'));

    setupService = jasmine.createSpyObj<FinanceSetupService>('FinanceSetupService', ['getTaxCodes']);
    setupService.getTaxCodes.and.returnValue(opts.codesFail ? throwError(() => new Error('down')) : ok(opts.codes ?? [GST17, GST5]));

    currencies = jasmine.createSpyObj<CurrenciesService>('CurrenciesService', ['getAll']);
    currencies.getAll.and.returnValue(ok([
      { id: 'cur-pkr', name: 'Pakistani Rupee', code: 'PKR', symbol: 'Rs' },
      { id: 'cur-usd', name: 'US Dollar', code: 'USD', symbol: '$' },
      { id: 'cur-aed', name: 'UAE Dirham', code: 'aed', symbol: null },
      { id: 'cur-old', name: 'Legacy money', code: null, symbol: null }
    ]));

    const demand = jasmine.createSpyObj<DemandService>('DemandService', ['getPos', 'getPoById']);
    demand.getPos.and.returnValue(ok({ data: opts.pos ?? [], totalRecords: (opts.pos ?? []).length }));
    const suppliers = jasmine.createSpyObj<SupplierService>('SupplierService', ['getSuppliers']);
    suppliers.getSuppliers.and.returnValue(ok({ data: [], totalRecords: 0 }));
    const warehouse = jasmine.createSpyObj<WarehouseService>('WarehouseService', ['getGrns', 'getGrnById']);
    warehouse.getGrns.and.returnValue(ok({ data: [], totalRecords: 0 }));

    const tenant = { tenant: signal(opts.baseCurrency === undefined ? null : { baseCurrency: opts.baseCurrency }) } as unknown as TenantService;

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [InvoiceCreateComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(),
        { provide: FinanceService, useValue: finance },
        { provide: FinanceSetupService, useValue: setupService },
        { provide: CurrenciesService, useValue: currencies },
        { provide: DemandService, useValue: demand },
        { provide: SupplierService, useValue: suppliers },
        { provide: WarehouseService, useValue: warehouse },
        { provide: TenantService, useValue: tenant }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(InvoiceCreateComponent);
    component = fixture.componentInstance;
    spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    fixture.detectChanges();
  }

  function fillValid() {
    component.form.supplierId = 'sup-1';
    component.form.poUuid = 'po-1';
    component.invoiceDateVal  = new Date(2026, 8, 15);
    component.receivedDateVal = new Date(2026, 8, 16);
    component.dueDateVal      = new Date(2026, 9, 15);
    component.form.subtotal   = 1000;
  }

  // ── Currencies ─────────────────────────────────────────────────────────────

  it('offers every Lookups currency that has a code, not a fixed four', async () => {
    await setup();

    expect(component.currencyOptions.map(o => o.value)).toEqual(['PKR', 'USD', 'AED']);
    expect(component.currencyOptions[1].label).toBe('USD — US Dollar');
  });

  it("pre-selects the organization's base currency", async () => {
    await setup({ baseCurrency: 'cur-usd' });

    expect(component.form.currency).toBe('USD');
  });

  it('keeps PKR when there is no base currency and PKR is on offer', async () => {
    await setup({ baseCurrency: null });

    expect(component.form.currency).toBe('PKR');
  });

  // ── Tax code ───────────────────────────────────────────────────────────────

  it('asks for purchase codes and pre-selects the default one', async () => {
    await setup();

    expect(setupService.getTaxCodes).toHaveBeenCalledWith('PURCHASE');
    expect(component.form.taxCodeUuid).toBe('gst17');
    expect(component.taxCodeOptions[0].value).toBeNull();
    expect(component.taxCodeOptions.map(o => o.value)).toContain('gst5');
  });

  it('works the tax out from the code and shows it read-only', async () => {
    await setup();
    component.form.subtotal = 1099.99;
    fixture.detectChanges();

    expect(component.taxAmount).toBe(187);
    expect(component.totalAmount).toBeCloseTo(1286.99, 2);
    expect(query('invoice-tax-computed')?.textContent).toContain('187.00');
    expect(query('invoice-tax-amount')).toBeNull();
  });

  it('with no code the tax is entered by hand', async () => {
    await setup();
    component.form.taxCodeUuid = null;
    component.form.subtotal = 1000;
    component.form.taxAmount = 55;
    fixture.detectChanges();

    expect(component.taxAmount).toBe(55);
    expect(query('invoice-tax-amount')).not.toBeNull();
    expect(query('invoice-tax-computed')).toBeNull();
  });

  it('still works with no codes at all when the codes cannot be loaded', async () => {
    await setup({ codesFail: true });

    expect(component.form.taxCodeUuid).toBeNull();
    expect(component.taxCodeOptions.length).toBe(1);
  });

  // ── Save ───────────────────────────────────────────────────────────────────

  it('sends the code, the tax it implies, and the dates as the days picked', async () => {
    await setup();
    fillValid();

    component.save();

    const sent = finance.createInvoice.calls.mostRecent().args[0] as CreateInvoiceRequest;
    expect(sent.taxCodeUuid).toBe('gst17');
    expect(sent.taxAmount).toBe(170);
    expect(sent.invoiceDate).toBe('2026-09-15');
    expect(sent.receivedDate).toBe('2026-09-16');
    expect(sent.dueDate).toBe('2026-10-15');
    expect(sent.currency).toBe('PKR');
  });

  it('sends no code and the typed amount for a hand-entered tax', async () => {
    await setup();
    fillValid();
    component.form.taxCodeUuid = null;
    component.form.taxAmount = 42.5;

    component.save();

    const sent = finance.createInvoice.calls.mostRecent().args[0] as CreateInvoiceRequest;
    expect(sent.taxCodeUuid).toBeNull();
    expect(sent.taxAmount).toBe(42.5);
  });

  it('a tax worked out from a code does not overwrite the amount typed for "no code", should the save fail', async () => {
    await setup();
    fillValid();
    component.form.taxAmount = 42.5;                 // typed earlier, then GST17 chosen
    finance.createInvoice.and.returnValue(throwError(() => ({ status: 500, error: { message: 'down' } })));

    component.save();
    component.form.taxCodeUuid = null;               // they switch back to entering it by hand

    expect(component.taxAmount).toBe(42.5);
  });

  // ── No purchase order (G10: a payable nobody ordered, e.g. a freight bill) ─────

  it('creates an invoice with no purchase order, sending no PO rather than an empty id', async () => {
    await setup();
    fillValid();
    component.form.poUuid = '';

    component.save();

    expect(finance.createInvoice).toHaveBeenCalledTimes(1);
    const sent = finance.createInvoice.calls.mostRecent().args[0] as CreateInvoiceRequest;
    expect(sent.poUuid ?? null).toBeNull();
    expect((sent as any).grnUuid).toBeUndefined();
    expect(sent.subtotal).toBe(1000);
    expect(sent.lines).toBeUndefined();
  });

  it('takes the supplier from the purchase order picked, and refuses an invoice naming one supplier on another\'s PO', async () => {
    await setup({ pos: [{ uuid: 'po-9', poNumber: 'PO-2026-00009', supplierId: 'sup-2', supplierName: 'Lahore Bolts', status: 'RECEIVED' }] });
    fillValid();

    component.form.poUuid = 'po-9';
    component.onPoChange();
    expect(component.form.supplierId).toBe('sup-2');

    component.form.supplierId = 'sup-1';            // changed back afterwards
    component.save();
    expect(finance.createInvoice).not.toHaveBeenCalled();

    component.form.supplierId = 'sup-2';
    component.save();
    expect(finance.createInvoice).toHaveBeenCalledTimes(1);
    expect((finance.createInvoice.calls.mostRecent().args[0] as CreateInvoiceRequest).poUuid).toBe('po-9');
  });

  // ── Notes: the server's column holds 300 characters ─────────────────────────

  it('caps the notes at the 300 characters the server keeps', async () => {
    await setup();
    const notes = query('invoice-notes') as HTMLInputElement | null;
    expect(notes?.maxLength).toBe(300);

    fillValid();
    component.form.notes = 'n'.repeat(301);
    component.save();
    expect(finance.createInvoice).not.toHaveBeenCalled();
  });
});
