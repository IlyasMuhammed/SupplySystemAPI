import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of } from 'rxjs';

import { InvoiceListComponent } from './invoice-list.component';
import { FinanceService, InvoiceListItemModel } from '../../../../services/finance.service';
import { AuthService } from '../../../service/auth.service';

function row(overrides: Partial<InvoiceListItemModel> = {}): InvoiceListItemModel {
  return {
    uuid: 'inv-1', invoiceNumber: 'INV-2026-00042', supplierInvoiceNo: 'KSW/881', supplierName: 'Karachi Steel',
    poNumber: 'PO-2026-00007', invoiceDate: '2026-09-15T00:00:00', dueDate: '2026-10-15T00:00:00',
    totalAmount: 1170, currency: 'PKR', matchStatus: 'Approved', paymentStatus: 'Unpaid',
    ...overrides
  };
}

describe('InvoiceListComponent', () => {
  let fixture: ComponentFixture<InvoiceListComponent>;
  let component: InvoiceListComponent;
  let finance: jasmine.SpyObj<FinanceService>;
  let permissions: string[];

  const auth = { hasPermission: (c: string) => permissions.includes(c) } as unknown as AuthService;

  async function setup(rows: InvoiceListItemModel[] = [row()]) {
    finance = jasmine.createSpyObj<FinanceService>('FinanceService', ['getInvoices']);
    finance.getInvoices.and.returnValue(of({ success: true, message: '', result: { data: rows, totalRecords: rows.length, page: 1, pageSize: 20, totalPages: 1 } }));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [InvoiceListComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]), provideNoopAnimations(),
        { provide: FinanceService, useValue: finance },
        { provide: AuthService, useValue: auth }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(InvoiceListComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  function newInvoiceButton(): HTMLElement | null {
    return fixture.nativeElement.querySelector('p-button[label="New Invoice"]');
  }

  beforeEach(() => { permissions = ['INVOICE_VIEW', 'INVOICE_PROCESS']; });

  it('offers New Invoice to an INVOICE_PROCESS holder', async () => {
    await setup();

    expect(newInvoiceButton()).not.toBeNull();
  });

  it('does not offer New Invoice to an INVOICE_VIEW-only reader (the server would refuse the create)', async () => {
    permissions = ['INVOICE_VIEW'];
    await setup();

    expect(newInvoiceButton()).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('INV-2026-00042');
  });

  it('filters by Reversed and tags a reversed invoice apart from the rest', async () => {
    await setup([row({ matchStatus: 'Reversed' })]);

    expect(component.matchOptions.map(o => o.value)).toContain('Reversed');
    expect(component.getMatchSeverity('Reversed')).toBe('contrast');

    component.selectedMatch = 'Reversed';
    component.onFilterChange();
    expect(finance.getInvoices.calls.mostRecent().args[0]?.matchStatus).toBe('Reversed');
  });

  it('shows a dash in the PO column for a payable with no purchase order', async () => {
    await setup([row({ poNumber: null as unknown as string })]);

    const poCell = fixture.nativeElement.querySelector('[data-testid="invoice-row-po"]') as HTMLElement | null;
    expect(poCell?.textContent?.trim()).toBe('—');
  });
});
