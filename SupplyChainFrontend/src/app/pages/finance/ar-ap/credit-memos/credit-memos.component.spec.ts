import { of, throwError } from 'rxjs';

import { CreditMemosComponent } from './credit-memos.component';
import { mount, openItem, page } from '../ar-ap.testing';
import { CreditMemo } from '../../../../services/ar-ap.service';

const memo = (over: Partial<CreditMemo> = {}): CreditMemo => ({
  uuid: 'n1', creditNoteNumber: 'SCN-20261025-0001', sourceType: 'AR_CREDIT_MEMO', customerUuid: 'c1', customerName: 'Acme Corp', date: '2026-10-25',
  currencyCode: 'PKR', subtotal: 500, taxAmount: 0, total: 500, applied: 0, refunded: 0, remaining: 500, lines: [], applications: [], ...over
});

describe('CreditMemosComponent (A48 C7 / contract §4)', () => {
  const setup = (perms: string[], prep: (api: any) => void = () => {}) => mount(CreditMemosComponent, { perms, query: { customer: 'c1' }, prepare: api => {
    api.getOpenCredits.and.returnValue(of([memo()]));
    api.getCustomerOpenItems.and.returnValue(page([openItem('i1', { dueDate: '2026-09-01', openAmount: 300 }), openItem('i2', { dueDate: '2026-10-01', openAmount: 400 }),
      openItem('u1', { documentType: 'CUSTOMER_PAYMENT', isCredit: true, openAmount: -50 }), openItem('x1', { currencyCode: 'USD' })]));
    prep(api);
  } });

  it('?customer= lists the open credits; a credit opens with the customer\'s open invoices in its currency', async () => {
    const m = await setup(['AR_VIEW']);
    expect(m.api.getOpenCredits).toHaveBeenCalledWith('c1');
    expect(m.api.getCustomerOpenItems).toHaveBeenCalledWith('c1', { includeCredits: false });
    expect(m.text('credit-n1')).toContain('SCN-20261025-0001');
    m.c.select(m.c.credits[0]);
    m.fixture.detectChanges();
    expect(m.c.rows.map(r => r.invoiceNumber)).toEqual(['SI-i1', 'SI-i2']);
    expect(m.q('apply')).withContext('applying needs AR_CREDIT_MEMO').toBeNull();
    expect(m.q('new')).toBeNull();
  });

  it('apply: oldest first fills up to the remaining credit; checks; POST the applications', async () => {
    const m = await setup(['AR_VIEW', 'AR_CREDIT_MEMO'], api => api.applyCreditMemo.and.returnValue(of(memo({ applied: 500, remaining: 0 }))));
    m.c.select(m.c.credits[0]);
    m.c.rows[0].amount = 600;
    m.c.apply();
    expect(m.c.applyError).toBe('Invoice SI-i1 has only 300.00 open.');
    m.c.fillOldest();
    expect(m.c.rows.map(r => r.amount)).toEqual([300, 200]);
    m.c.apply();
    expect(m.api.applyCreditMemo).toHaveBeenCalledWith('n1', [{ invoiceUuid: 'i1', amount: 300 }, { invoiceUuid: 'i2', amount: 200 }]);
    expect(m.toast).toHaveBeenCalledWith(jasmine.objectContaining({ detail: 'SCN-20261025-0001: 500.00 applied.' }));
  });

  it('create: against an invoice (applied at once by default) or standalone in a currency', async () => {
    const m = await setup(['AR_VIEW', 'AR_CREDIT_MEMO'], api => api.createCreditMemo.and.returnValue(of(memo())));
    m.c.openNew();
    m.c.create();
    expect(m.c.formError).toBe('A reason is required.');
    m.c.reason = 'Damaged goods';
    m.c.lines = [{ description: 'Two broken units', amount: 500 }];
    m.c.originalUuid = 'i1';
    m.c.create();
    expect(m.api.createCreditMemo).toHaveBeenCalledWith(jasmine.objectContaining({
      customerUuid: 'c1', originalInvoiceUuid: 'i1', currencyCode: null, reason: 'Damaged goods', applyToOriginal: true,
      lines: [{ description: 'Two broken units', amount: 500 }]
    }));
    m.c.openNew();
    m.c.reason = 'Goodwill';
    m.c.lines = [{ description: 'Goodwill', amount: 25 }];
    m.c.currencyCode = 'usd';
    m.c.create();
    expect(m.api.createCreditMemo).toHaveBeenCalledWith(jasmine.objectContaining({ originalInvoiceUuid: null, currencyCode: 'USD', applyToOriginal: null }));
  });

  it('a memo-only user (AR_CREDIT_MEMO, no AR_VIEW) gets the invoices and can apply (open-items admits AR_CREDIT_MEMO)', async () => {
    const m = await setup(['AR_CREDIT_MEMO']);
    m.c.select(m.c.credits[0]);
    m.fixture.detectChanges();
    expect(m.c.rows.length).toBe(2);
    expect(m.q('apply')).not.toBeNull();
    expect(m.q('new')).not.toBeNull();
  });

  it('a server refusal stays in the form', async () => {
    const m = await setup(['AR_CREDIT_MEMO'], api => api.createCreditMemo.and.returnValue(throwError(() => ({ status: 400, error: { message: 'USD is not a currency of this organization.' } }))));
    m.c.openNew();
    m.c.reason = 'x';
    m.c.lines = [{ description: 'x', amount: 1 }];
    m.c.create();
    expect(m.c.formError).toContain('not a currency');
  });
});
