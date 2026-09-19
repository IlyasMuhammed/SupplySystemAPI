import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { CodReconciliationComponent } from './cod-reconciliation.component';
import {
  LogisticsService, CodCollectionModel, CodSummaryModel, CarrierListItemModel
} from '../../../../services/logistics.service';

function record(overrides: Partial<CodCollectionModel> = {}): CodCollectionModel {
  return {
    uuid: 'cod-1', consignmentUuid: 'con-1', consignmentNumber: 'SHP-2026-00042',
    consignmentStatus: 'DELIVERED', masterAwb: 'AWB-1',
    carrierUuid: 'carrier-1', carrierName: 'Beta Road',
    expectedAmount: 5000, currency: 'PKR',
    remittedAmount: 0, outstandingAmount: 5000,
    status: 'EXPECTED', remittances: [], warnings: [],
    ...overrides
  };
}

function summary(overrides: Partial<CodSummaryModel> = {}): CodSummaryModel {
  return {
    asOf: '2026-09-18T00:00:00Z',
    openCount: 1, outstandingTotal: 5000, currency: 'PKR',
    byCarrier: [{ carrierUuid: 'carrier-1', carrierName: 'Beta Road', currency: 'PKR', count: 1, outstanding: 5000 }],
    neverCollected: [], warnings: [],
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

describe('CodReconciliationComponent', () => {
  let fixture: ComponentFixture<CodReconciliationComponent>;
  let component: CodReconciliationComponent;
  let api: jasmine.SpyObj<LogisticsService>;

  async function setup(
    records: CodCollectionModel[] = [record()],
    totals: CodSummaryModel = summary()) {

    api = jasmine.createSpyObj<LogisticsService>('LogisticsService', [
      'getCodSummary', 'getCodList', 'recordCodCollection', 'recordCodRemittance',
      'writeOffCod', 'getActiveCarriers'
    ]);

    api.getCodSummary.and.returnValue(ok(totals));
    api.getCodList.and.returnValue(page(records));
    api.getActiveCarriers.and.returnValue(
      ok([{ uuid: 'carrier-1', name: 'Beta Road' } as CarrierListItemModel]));
    api.recordCodCollection.and.returnValue(ok(record({ status: 'COLLECTED' })));
    api.recordCodRemittance.and.returnValue(ok(record({ status: 'SETTLED' })));
    api.writeOffCod.and.returnValue(of({ success: true, message: '' } as any));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [CodReconciliationComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        provideNoopAnimations(), MessageService,
        { provide: LogisticsService, useValue: api }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(CodReconciliationComponent);
    component = fixture.componentInstance;
  }

  // ── Reading ───────────────────────────────────────────────────────────────

  it('shows what carriers are holding, by carrier', async () => {
    await setup();
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="outstanding-total"]')?.textContent)
      .toContain('5,000');
    expect(fixture.nativeElement.querySelector('[data-testid="by-carrier"]')?.textContent)
      .toContain('Beta Road');
  });

  it('does not add two currencies together', async () => {
    // There is no exchange rate here, so a single figure across currencies would be a number
    // nobody could defend.
    await setup([record()], summary({
      currency: undefined, outstandingTotal: 0,
      warnings: ['Outstanding cash is in PKR, USD.'],
      byCarrier: [
        { carrierName: 'Beta Road', currency: 'PKR', count: 1, outstanding: 5000 },
        { carrierName: 'Beta Road', currency: 'USD', count: 1, outstanding: 100 }
      ]
    }));
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="mixed-currency"]')).toBeTruthy();
    expect(fixture.nativeElement.querySelector('[data-testid="summary-warnings"]')?.textContent)
      .toContain('PKR, USD');
  });

  it('calls out cash delivered and never accounted for', async () => {
    await setup([record()], summary({
      neverCollected: [{
        consignmentUuid: 'con-9', consignmentNumber: 'SHP-2026-00099',
        status: 'DELIVERED', carrierName: 'Beta Road', expectedAmount: 3000, currency: 'PKR',
        reason: 'Delivered with cash to collect, and the carrier has never said it took it.'
      }],
      warnings: ['1 consignment(s) were delivered carrying cash and nothing says it was collected.']
    }));
    fixture.detectChanges();

    const banner = fixture.nativeElement.querySelector('[data-testid="never-collected"]');
    expect(banner?.textContent).toContain('SHP-2026-00099');
    expect(banner?.textContent).toContain('before the trail goes cold');
  });

  it('says where each record stands in words rather than a code', async () => {
    await setup();

    expect(component.statusLabel('EXPECTED')).toBe('Not yet collected');
    expect(component.statusLabel('COLLECTED')).toBe('Carrier holds it');
    expect(component.statusLabel('SETTLED')).toBe('Settled');
  });

  it('shows a rows own warnings on the row', async () => {
    await setup([record({
      warnings: ["The carrier says it took PKR 4,500.00 of the PKR 5,000.00 expected."]
    })]);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="row-warning"]')?.textContent)
      .toContain('4,500');
  });

  it('offers nothing to do on a settled or written-off record', async () => {
    await setup();

    expect(component.canAct(record({ status: 'EXPECTED' }))).toBeTrue();
    expect(component.canAct(record({ status: 'SETTLED' }))).toBeFalse();
    expect(component.canAct(record({ status: 'WRITTEN_OFF' }))).toBeFalse();
  });

  // ── Recording ─────────────────────────────────────────────────────────────

  it('pre-fills what is still outstanding, because a figure somebody works out is one they get wrong', async () => {
    await setup();
    fixture.detectChanges();

    component.open(record({ outstandingAmount: 2000 }), 'remitted');

    expect(component.form.amount).toBe(2000);
  });

  it('refuses a collection larger than the consignment asked for', async () => {
    await setup();
    fixture.detectChanges();

    component.open(record({ expectedAmount: 5000 }), 'collected');
    component.form.amount = 6000;

    expect(component.validationError).toContain('This consignment collects 5000.00');
  });

  it('refuses a remittance larger than what is outstanding', async () => {
    // Money attributed to the wrong consignment is money nobody can trace.
    await setup();
    fixture.detectChanges();

    component.open(record({ outstandingAmount: 2000 }), 'remitted');
    component.form.amount = 2500;

    expect(component.validationError).toContain('Split the remittance');
  });

  it('records a collection with the carriers receipt', async () => {
    await setup();
    fixture.detectChanges();

    component.open(record(), 'collected');
    component.form.amount = 5000;
    component.form.reference = ' RCPT-8821 ';
    component.confirm();

    expect(api.recordCodCollection).toHaveBeenCalledWith('con-1', jasmine.objectContaining({
      amount: 5000, reference: 'RCPT-8821'
    }));
  });

  it('records a remittance against the transfer it arrived in', async () => {
    await setup();
    fixture.detectChanges();

    component.open(record(), 'remitted');
    component.form.amount = 5000;
    component.form.reference = 'TT-20260915';
    component.confirm();

    expect(api.recordCodRemittance).toHaveBeenCalledWith('con-1', jasmine.objectContaining({
      amount: 5000, reference: 'TT-20260915'
    }));
  });

  it('will not write cash off without a reason', async () => {
    await setup();
    fixture.detectChanges();

    component.open(record(), 'write-off');
    expect(component.canSave).toBeFalse();
    expect(component.validationError).toContain('why this cash is being written off');

    component.form.note = 'Carrier deducted its handling fee at source.';
    expect(component.canSave).toBeTrue();

    component.confirm();
    expect(api.writeOffCod)
      .toHaveBeenCalledWith('con-1', 'Carrier deducted its handling fee at source.');
  });

  it('titles the dialog by what is being recorded', async () => {
    await setup();

    component.action = 'collected'; expect(component.dialogHeader).toContain('carrier collected');
    component.action = 'remitted';  expect(component.dialogHeader).toContain('reaching us');
    component.action = 'write-off'; expect(component.dialogHeader).toContain('Write off');
  });

  it('surfaces the servers reason when a remittance is refused', async () => {
    await setup();
    fixture.detectChanges();
    const messages = fixture.debugElement.injector.get(MessageService);
    spyOn(messages, 'add');

    api.recordCodRemittance.and.returnValue(throwError(() => ({
      error: { message: 'Only PKR 2,000.00 is outstanding on this consignment.' }
    })));

    component.open(record(), 'remitted');
    component.form.amount = 100;
    component.confirm();

    expect(messages.add).toHaveBeenCalledWith(jasmine.objectContaining({
      severity: 'error', detail: jasmine.stringContaining('outstanding')
    }));
  });

  it('says so when no carrier is holding anything', async () => {
    await setup([], summary({ openCount: 0, outstandingTotal: 0, byCarrier: [] }));
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('[data-testid="cod-empty"]')?.textContent)
      .toContain('reached us or been written off');
  });

  it('narrows by carrier and status', async () => {
    await setup();
    fixture.detectChanges();

    component.filter = { carrierUuid: 'carrier-1', status: 'COLLECTED' };
    component.search();

    expect(api.getCodList).toHaveBeenCalledWith(jasmine.objectContaining({
      carrierUuid: 'carrier-1', status: 'COLLECTED', page: 1
    }));
  });
});
