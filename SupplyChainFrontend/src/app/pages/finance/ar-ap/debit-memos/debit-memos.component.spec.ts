import { of, throwError } from 'rxjs';

import { DebitMemosComponent, debitMemoProblem } from './debit-memos.component';
import { mount, openItem, page } from '../ar-ap.testing';
import { DebitMemo } from '../../../../services/ar-ap.service';

const memo = (over: Partial<DebitMemo> = {}): DebitMemo => ({
  uuid: 'dn1', debitNoteNumber: 'DN-0001', origin: 'MEMO', vendorUuid: 'v1', vendorName: 'Supplier A', amount: 200, debitReason: 'PRICE_DIFFERENCE',
  status: 'ISSUED', applicationStatus: 'CARRIED_FORWARD', remainingAmount: 150, issuedAt: '2026-09-01T10:00:00Z', applications: [], ...over
});

describe('DebitMemosComponent (A48 C14 / contract §9, FE-8)', () => {
  const setup = (perms: string[], prep: (api: any) => void = () => {}) => mount(DebitMemosComponent, { perms, query: { vendor: 'v1' }, prepare: api => {
    api.getVendorDebitMemos.and.returnValue(page([memo()]));
    api.getDebitMemo.and.returnValue(of(memo({ applications: [{ uuid: 'a1', noteUuid: 'dn1', noteNumber: 'DN-0001', invoiceUuid: 'inv0', invoiceNumber: 'INV-0',
      amount: 50, appliedAt: '2026-10-01T10:00:00Z' }] })));
    api.getVendorOpenItems.and.returnValue(page([
      openItem('inv1', { documentType: 'SUPPLIER_INVOICE', documentNumber: 'INV-1', openAmount: 500, dueDate: '2026-10-01' }),
      openItem('inv2', { documentType: 'SUPPLIER_INVOICE', documentNumber: 'INV-2', openAmount: 80, dueDate: '2026-09-01' })
    ]));
    prep(api);
  } });

  it('lists the vendor\'s open debit notes (§9 list, openOnly) and the open invoices; a note opens with its detail and applications', async () => {
    const m = await setup(['AP_VIEW']);
    expect(m.api.getVendorDebitMemos).toHaveBeenCalledWith('v1', true);
    expect(m.api.getVendorOpenItems).toHaveBeenCalledWith('v1', { includeCredits: false });
    expect(m.text('debit-dn1')).toContain('DN-0001');
    expect(m.text('debit-dn1')).toContain('150.00');
    m.c.select(m.c.debits[0]);
    m.fixture.detectChanges();
    expect(m.api.getDebitMemo).toHaveBeenCalledWith('dn1');
    expect(m.text('applications')).toContain('INV-0');
    expect(m.c.rows.map(r => r.invoiceNumber)).toEqual(['INV-2', 'INV-1']);
    expect(m.q('apply')).withContext('applying needs AP_DEBIT_MEMO').toBeNull();
    m.c.setOpenOnly(false);
    expect(m.api.getVendorDebitMemos).toHaveBeenCalledWith('v1', false);
  });

  it('a memo-only user (AP_DEBIT_MEMO, no AP_VIEW) applies within the remaining amount', async () => {
    const m = await setup(['AP_DEBIT_MEMO'], api => api.applyDebitMemo.and.returnValue(of(memo({ remainingAmount: 70 }))));
    m.c.select(m.c.debits[0]);
    m.fixture.detectChanges();
    expect(m.q('apply')).not.toBeNull();
    m.c.rows[0].amount = 80;
    m.c.rows[1].amount = 100;
    m.c.apply();
    expect(m.c.applyError).toBe('DN-0001 has only 150.00 left to apply.');
    m.c.rows[1].amount = null;
    m.c.apply();
    expect(m.api.applyDebitMemo).toHaveBeenCalledWith('dn1', [{ invoiceUuid: 'inv2', amount: 80 }]);
    expect(m.c.selected?.remainingAmount).toBe(70);
  });

  it('REV: a note in a currency is applied only to invoices in that currency; the currency refusal is shown', async () => {
    const m = await setup(['AP_DEBIT_MEMO'], api => {
      api.getDebitMemo.and.returnValue(of(memo({ currencyCode: 'USD' })));
      api.getVendorOpenItems.and.returnValue(page([
        openItem('inv1', { documentType: 'SUPPLIER_INVOICE', documentNumber: 'INV-1', openAmount: 500, currencyCode: 'PKR' }),
        openItem('inv3', { documentType: 'SUPPLIER_INVOICE', documentNumber: 'INV-3', openAmount: 90, currencyCode: 'USD' })]));
      api.applyDebitMemo.and.returnValue(throwError(() => ({ status: 400, error: { message: 'DN-0001 is in USD; invoice INV-3 is in PKR.' } })));
    });
    m.c.select(m.c.debits[0]);
    m.fixture.detectChanges();
    expect(m.c.rows.map(r => r.invoiceNumber)).toEqual(['INV-3']);
    expect(m.text('apply-currency')).toBe('USD invoices only');
    m.c.rows[0].amount = 50;
    m.c.apply();
    expect(m.c.applyError).toBe('DN-0001 is in USD; invoice INV-3 is in PKR.');
  });

  it('create a memo against an invoice', async () => {
    const m = await setup(['AP_DEBIT_MEMO'], api => api.createDebitMemo.and.returnValue(of(memo({ uuid: 'd9', debitNoteNumber: 'DN-0009', remainingAmount: 0 }))));
    m.c.openNew();
    m.c.memoAmount = 0;
    m.c.create();
    expect(m.c.formError).toBe('The amount must be greater than zero.');
    m.c.memoAmount = 120;
    m.c.reason = 'REBATE';
    m.c.reasonDetail = ' Q3 volume rebate ';
    m.c.originalUuid = 'inv1';
    m.c.create();
    m.fixture.detectChanges();
    expect(m.api.createDebitMemo).toHaveBeenCalledWith(jasmine.objectContaining({ vendorUuid: 'v1', originalInvoiceUuid: 'inv1', reason: 'REBATE', reasonDetail: 'Q3 volume rebate', amount: 120 }));
    expect(m.text('created')).toContain('DN-0009');
    expect(debitMemoProblem('', 1, '2026-10-01', '2026-10-25')).toBe('Choose the reason.');
    expect(debitMemoProblem('REBATE', 1, '2026-11-01', '2026-10-25')).toBe('The date cannot be in the future.');
  });
});
