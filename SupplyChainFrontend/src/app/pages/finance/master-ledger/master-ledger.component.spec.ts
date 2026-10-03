import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, Router } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { MasterLedgerComponent } from './master-ledger.component';
import { MasterLedgerService, MasterLedgerEntryModel } from '../../../services/master-ledger.service';
import { SupplierService } from '../../../services/supplier.service';

function entry(transactionType: string, overrides: Partial<MasterLedgerEntryModel> = {}): MasterLedgerEntryModel {
  return {
    uuid: `e-${transactionType}`, sequenceNo: 1, supplierId: 'sup-1', supplierName: 'Karachi Steel',
    transactionType, referenceType: 'Invoice', referenceId: 'inv-1', referenceNo: 'INV-2026-00042',
    entryDate: '2026-09-30T09:00:00', debitAmount: 0, creditAmount: 0, balanceAfter: 0,
    createdBy: 1, createdDate: '2026-09-30T09:00:00',
    ...overrides
  };
}

describe('MasterLedgerComponent', () => {
  let fixture: ComponentFixture<MasterLedgerComponent>;
  let component: MasterLedgerComponent;
  let navigate: jasmine.Spy;

  async function setup(entries: MasterLedgerEntryModel[]) {
    const ledger = jasmine.createSpyObj<MasterLedgerService>('MasterLedgerService', ['getLedger', 'getSummary', 'exportPdf', 'exportExcel']);
    ledger.getLedger.and.returnValue(of({ success: true, message: '', result: { data: entries, totalRecords: entries.length, page: 1, pageSize: 20, totalPages: 1 } } as any));
    ledger.getSummary.and.returnValue(of({ success: true, message: '', result: { totalPayables: 0, totalDebits: 1170, totalCredits: 1170, netMovement: 0 } } as any));
    const suppliers = jasmine.createSpyObj<SupplierService>('SupplierService', ['getSuppliers']);

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [MasterLedgerComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(),
        { provide: MasterLedgerService, useValue: ledger },
        { provide: SupplierService, useValue: suppliers }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(MasterLedgerComponent);
    component = fixture.componentInstance;
    navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    fixture.detectChanges();
  }

  it('labels an invoice reversal as such, as a credit, and offers it as a filter', async () => {
    await setup([]);

    expect(component.getTypeLabel('INVOICE_REVERSED')).toBe('Invoice Reversed');
    expect(component.getTypeSeverity('INVOICE_REVERSED')).toBe('success');
    expect(component.getTypeSeverity('INVOICE_APPROVED')).toBe('danger');
    expect(component.transactionTypeOptions.map(o => o.value)).toContain('INVOICE_REVERSED');
  });

  it('links a reversal to its invoice exactly as the approval is linked', async () => {
    const approved = entry('INVOICE_APPROVED', { debitAmount: 1170, balanceAfter: 1170 });
    const reversed = entry('INVOICE_REVERSED', { uuid: 'e-rev', sequenceNo: 2, creditAmount: 1170, balanceAfter: 0 });
    await setup([approved, reversed]);

    const links = Array.from(fixture.nativeElement.querySelectorAll('a.ref-link') as NodeListOf<HTMLAnchorElement>);
    expect(links.map(a => a.textContent?.trim())).toEqual(['INV-2026-00042', 'INV-2026-00042']);
    expect(fixture.nativeElement.textContent).toContain('Invoice Reversed');

    links[1].click();
    expect(navigate).toHaveBeenCalledWith(['/portal/pages/finance/invoices', 'inv-1']);
    expect(component.canDrillDown(approved)).toBe(component.canDrillDown(reversed));
  });
});
