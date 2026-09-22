import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { PaymentAllocationEditorComponent } from './payment-allocation-editor.component';
import { AllocationAmounts } from './payment-allocation';
import { SalesInvoiceListItemModel } from '../../../../services/sales-invoice.service';

function invoice(uuid: string, balanceDue: number, invoiceDate: string, status = 'ISSUED'): SalesInvoiceListItemModel {
  return {
    uuid, invoiceNumber: `INV-${uuid}`, saleOrderUuid: 's', saleOrderNumber: 'SO-1', partnerId: 'p', partnerName: 'Acme',
    invoiceDate, dueDate: '2026-10-01T00:00:00Z', grandTotal: balanceDue, amountPaid: 0, balanceDue, status, currencyCode: 'PKR'
  };
}

describe('PaymentAllocationEditorComponent', () => {
  let fixture: ComponentFixture<PaymentAllocationEditorComponent>;
  let component: PaymentAllocationEditorComponent;
  let emitted: AllocationAmounts[];

  const invoices = [
    invoice('new', 200, '2026-09-01T00:00:00Z'),
    invoice('old', 100, '2026-08-01T00:00:00Z', 'OVERDUE'),
    invoice('mid', 50, '2026-08-15T00:00:00Z', 'PARTIALLY_PAID')
  ];

  async function setup(available = 120, amounts: AllocationAmounts = {}, list = invoices) {
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [PaymentAllocationEditorComponent],
      providers: [provideNoopAnimations()]
    }).compileComponents();

    fixture = TestBed.createComponent(PaymentAllocationEditorComponent);
    component = fixture.componentInstance;
    emitted = [];
    component.amountsChange.subscribe(a => emitted.push(a));

    fixture.componentRef.setInput('invoices', list);
    fixture.componentRef.setInput('available', available);
    fixture.componentRef.setInput('currencyCode', 'PKR');
    fixture.componentRef.setInput('amounts', amounts);
    fixture.detectChanges();
  }

  function text(testId: string): string {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`).textContent.trim();
  }

  it('lists the invoices oldest first, whatever order it is given them in', async () => {
    await setup();

    expect(component.rows.map(r => r.uuid)).toEqual(['old', 'mid', 'new']);
    const numbers = Array.from(fixture.nativeElement.querySelectorAll('[data-testid="allocation-row"] td:first-child')).map((e: any) => e.textContent.trim());
    expect(numbers).toEqual(['INV-old', 'INV-mid', 'INV-new']);
  });

  it('shows how much there is, how much is applied, and how much stays on account', async () => {
    await setup(120, { old: 100, mid: 5 });

    expect(text('available')).toBe('120.00');
    expect(text('applied')).toBe('105.00');
    expect(text('remaining')).toBe('15.00');
    expect(component.problem).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="allocation-problem"]')).toBeNull();
  });

  it('asks for the oldest invoices to be paid first, and does not change what it was given', async () => {
    const given: AllocationAmounts = { new: 5 };
    await setup(120, given);

    component.fillOldestFirst();

    expect(emitted).toEqual([{ old: 100, mid: 20 }]);
    expect(given).toEqual({ new: 5 });
  });

  it('clears', async () => {
    await setup(120, { old: 100 });

    component.clear();

    expect(emitted).toEqual([{}]);
  });

  it('reports an amount typed for one invoice, keeping the others', async () => {
    await setup(120, { old: 100 });

    component.setAmount(invoices[2], 15);

    expect(emitted).toEqual([{ old: 100, mid: 15 }]);
  });

  it('pays an invoice in full, or as much as the money left allows', async () => {
    await setup(120, { old: 100 });

    component.payInFull(invoices[2]);
    expect(emitted[0]).withContext('50 owing, 20 left').toEqual({ old: 100, mid: 20 });

    component.payInFull(invoices[1]);
    expect(emitted[1]).withContext('what it holds already counts as room').toEqual({ old: 100 });
  });

  it('pays in full up to the balance when there is money to spare', async () => {
    await setup(500, {});

    component.payInFull(invoices[2]);

    expect(emitted).toEqual([{ mid: 50 }]);
  });

  it('never pays a negative amount in full when the money has run out', async () => {
    await setup(100, { old: 100 });

    component.payInFull(invoices[2]);

    expect(emitted).toEqual([{ old: 100, mid: 0 }]);
  });

  it('says what is wrong, marks the row, and shows the remainder as negative when too much is allocated', async () => {
    await setup(120, { old: 100, mid: 50 });

    expect(component.remaining).toBe(-30);
    expect(text('allocation-problem')).toContain('The allocations come to 150.00, more than the 120.00 available.');
    expect(fixture.nativeElement.querySelector('[data-testid="remaining"].negative')).not.toBeNull();
  });

  it('marks the row given more than it owes', async () => {
    await setup(500, { mid: 60 });

    expect(component.rowProblem(invoices[2])).toBeTrue();
    expect(component.rowProblem(invoices[1])).toBeFalse();
    expect(fixture.nativeElement.querySelectorAll('tr.row-problem').length).toBe(1);
    expect(text('allocation-problem')).toContain('INV-mid: 60.00 is more than the 50.00 still owing.');
  });

  it('says there is nothing to apply it to when there is no open invoice', async () => {
    await setup(120, {}, []);

    expect(fixture.nativeElement.querySelector('[data-testid="no-open-invoices"]').textContent).toContain('no unpaid PKR invoice');
    expect(fixture.nativeElement.querySelector('[data-testid="allocation-table"]')).toBeNull();
  });

  it('follows the list when it changes', async () => {
    await setup();

    fixture.componentRef.setInput('invoices', [invoice('only', 10, '2026-09-01T00:00:00Z')]);
    fixture.detectChanges();

    expect(component.rows.map(r => r.uuid)).toEqual(['only']);
  });
});
