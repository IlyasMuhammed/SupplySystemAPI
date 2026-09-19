import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { CarrierInvoicesComponent } from './carrier-invoices.component';
import {
  LogisticsService, CarrierInvoiceModel, CarrierListItemModel
} from '../../../../services/logistics.service';

function invoice(overrides: Partial<CarrierInvoiceModel> = {}): CarrierInvoiceModel {
  return {
    uuid: 'inv-1', carrierUuid: 'carrier-1', carrierName: 'Beta Road',
    invoiceNumber: 'INV-9001', invoiceDate: '2026-09-01T00:00:00Z',
    currency: 'PKR', totalAmount: 1260, lineTotal: 1260,
    status: 'RECEIVED', lineCount: 2, createdDate: '2026-09-01T00:00:00Z',
    lines: [],
    ...overrides
  };
}

function page<T>(data: T[]) {
  return of({
    success: true, message: '',
    result: { data, totalRecords: data.length, page: 1, pageSize: 20, totalPages: 1 }
  } as any);
}

function ok<T>(result: T) {
  return of({ success: true, message: '', result } as any);
}

describe('CarrierInvoicesComponent', () => {
  let fixture: ComponentFixture<CarrierInvoicesComponent>;
  let component: CarrierInvoicesComponent;
  let api: jasmine.SpyObj<LogisticsService>;

  async function setup(invoices: CarrierInvoiceModel[] = [invoice()]) {
    api = jasmine.createSpyObj<LogisticsService>('LogisticsService', [
      'getCarrierInvoices', 'createCarrierInvoice', 'cancelCarrierInvoice', 'getActiveCarriers'
    ]);

    api.getCarrierInvoices.and.returnValue(page(invoices));
    api.getActiveCarriers.and.returnValue(
      ok([{ uuid: 'carrier-1', name: 'Beta Road' } as CarrierListItemModel]));
    api.createCarrierInvoice.and.returnValue(ok('new-invoice'));
    api.cancelCarrierInvoice.and.returnValue(of({ success: true, message: '' } as any));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [CarrierInvoicesComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        provideNoopAnimations(), MessageService,
        { provide: LogisticsService, useValue: api }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(CarrierInvoicesComponent);
    component = fixture.componentInstance;
  }

  // ── Reading ───────────────────────────────────────────────────────────────

  it('lists bills with their status', async () => {
    await setup();
    fixture.detectChanges();

    expect(api.getCarrierInvoices).toHaveBeenCalled();
    expect(fixture.nativeElement.querySelectorAll('[data-testid="invoice-row"]').length).toBe(1);
  });

  it('says so when no bill has been recorded', async () => {
    await setup([]);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="no-invoices"]')?.textContent)
      .toContain('nothing checks what carriers are charging');
  });

  it('calls a withdrawn bill withdrawn rather than cancelled', async () => {
    await setup();

    expect(component.formatStatus('CANCELLED')).toBe('Withdrawn');
    expect(component.formatStatus('DISPUTED')).toBe('Disputed');
  });

  it('narrows by carrier, status and a free search', async () => {
    await setup();
    fixture.detectChanges();

    component.filter = { carrierUuid: 'carrier-1', status: 'DISPUTED', search: ' AWB-1 ' };
    component.search();

    expect(api.getCarrierInvoices).toHaveBeenCalledWith(jasmine.objectContaining({
      carrierUuid: 'carrier-1', status: 'DISPUTED', search: 'AWB-1', page: 1
    }));
  });

  // ── Keying a bill ─────────────────────────────────────────────────────────

  it('shows the running line total against the header while keying', async () => {
    // The two having to agree is the rule most often broken, so it is shown live rather than
    // discovered on save.
    await setup();
    fixture.detectChanges();

    component.openCreate();
    component.form.totalAmount = 1260;
    component.lines[0].amount = 1125;
    component.addLine();
    component.lines[1].amount = 100;

    expect(component.lineTotal).toBe(1225);
    expect(component.difference).toBe(-35);
  });

  it('refuses a bill whose lines do not add up to its own total', async () => {
    await setup();
    fixture.detectChanges();

    component.openCreate();
    component.form.carrierUuid = 'carrier-1';
    component.form.invoiceNumber = 'INV-1';
    component.form.totalAmount = 1260;
    component.lines[0] = {
      description: 'Carriage', awbNumber: '', consignmentReference: '',
      chargeCode: '', serviceCode: '', chargeableWeightKg: null, amount: 1000
    };

    expect(component.validationError).toContain('They have to agree');
    expect(component.canSave).toBeFalse();
  });

  it('tolerates a cent of rounding', async () => {
    await setup();
    fixture.detectChanges();

    component.openCreate();
    component.form.carrierUuid = 'carrier-1';
    component.form.invoiceNumber = 'INV-1';
    component.form.totalAmount = 1260.01;
    component.lines[0] = {
      description: 'Carriage', awbNumber: '', consignmentReference: '',
      chargeCode: '', serviceCode: '', chargeableWeightKg: null, amount: 1260
    };

    expect(component.validationError).toBeNull();
  });

  it('names the missing field rather than surfacing a refusal after a round trip', async () => {
    await setup();
    fixture.detectChanges();
    component.openCreate();

    expect(component.validationError).toContain('which carrier');

    component.form.carrierUuid = 'carrier-1';
    expect(component.validationError).toContain('invoice number');

    component.form.invoiceNumber = 'INV-1';
    component.form.currency = 'RUPEES';
    expect(component.validationError).toContain('three ISO letters');

    component.form.currency = 'PKR';
    expect(component.validationError).toContain('not a bill');
  });

  it('refuses a due date before the issue date', async () => {
    await setup();
    fixture.detectChanges();
    component.openCreate();

    component.form.carrierUuid = 'carrier-1';
    component.form.invoiceNumber = 'INV-1';
    component.form.invoiceDate = new Date('2026-09-10');
    component.form.dueDate = new Date('2026-09-01');

    expect(component.validationError).toContain('before it was issued');
  });

  it('refuses a line that charges nothing', async () => {
    await setup();
    fixture.detectChanges();
    component.openCreate();

    component.form.carrierUuid = 'carrier-1';
    component.form.invoiceNumber = 'INV-1';
    component.form.totalAmount = 100;
    component.lines[0] = {
      description: 'Carriage', awbNumber: '', consignmentReference: '',
      chargeCode: '', serviceCode: '', chargeableWeightKg: null, amount: null
    };

    expect(component.validationError).toContain('Line 1 charges nothing');
  });

  it('records a bill with its lines, trimming what was typed', async () => {
    await setup();
    fixture.detectChanges();
    component.openCreate();

    component.form.carrierUuid = 'carrier-1';
    component.form.invoiceNumber = ' INV-9001 ';
    component.form.currency = 'pkr';
    component.form.invoiceDate = new Date('2026-09-01T00:00:00Z');
    component.form.totalAmount = 1260;
    component.lines[0] = {
      description: ' Carriage ', awbNumber: ' AWB-1 ', consignmentReference: '',
      chargeCode: 'BASE', serviceCode: '', chargeableWeightKg: 12.5, amount: 1260
    };

    component.save();

    const sent = api.createCarrierInvoice.calls.mostRecent().args[0];
    expect(sent.invoiceNumber).toBe('INV-9001');
    expect(sent.currency).toBe('PKR');
    expect(sent.lines[0].description).toBe('Carriage');
    expect(sent.lines[0].awbNumber).toBe('AWB-1');
    expect(sent.lines[0].chargeableWeightKg).toBe(12.5);
  });

  it('surfaces the servers reason when a bill is a duplicate', async () => {
    await setup();
    fixture.detectChanges();
    const messages = fixture.debugElement.injector.get(MessageService);
    spyOn(messages, 'add');

    api.createCarrierInvoice.and.returnValue(throwError(() => ({
      error: { message: "Beta Road invoice 'INV-9001' is already recorded." }
    })));

    component.openCreate();
    component.form.carrierUuid = 'carrier-1';
    component.form.invoiceNumber = 'INV-9001';
    component.form.totalAmount = 100;
    component.lines[0].description = 'Carriage';
    component.lines[0].amount = 100;
    component.save();

    expect(messages.add).toHaveBeenCalledWith(jasmine.objectContaining({
      severity: 'error', detail: jasmine.stringContaining('already recorded')
    }));
  });

  // ── Withdrawing ───────────────────────────────────────────────────────────

  it('will not withdraw a bill without a reason', async () => {
    await setup();
    fixture.detectChanges();

    component.openCancel(invoice());
    expect(component.canCancel).toBeFalse();

    component.cancelReason = 'Duplicate of INV-9000.';
    expect(component.canCancel).toBeTrue();

    component.confirmCancel();
    expect(api.cancelCarrierInvoice).toHaveBeenCalledWith('inv-1', 'Duplicate of INV-9000.');
  });
});
