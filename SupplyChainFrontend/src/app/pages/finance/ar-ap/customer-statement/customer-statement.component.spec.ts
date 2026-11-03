import { HttpResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';

import { CustomerStatementComponent, statementPeriodProblem } from './customer-statement.component';
import { mount, statement as make } from '../ar-ap.testing';

const statement = make({
  sections: [{ currencyCode: 'PKR', openingBalance: 1000, closingBalance: 1500, totalDebits: 800, totalCredits: 300,
    lines: [{ date: '2026-07-05', documentType: 'INVOICE', entryNumber: 'SI-0001', description: 'Invoice', debit: 800, runningBalance: 1800 },
      { date: '2026-08-01', documentType: 'PAYMENT', entryNumber: 'CP-0001', description: 'Payment', credit: 300, runningBalance: 1500 }] },
    { currencyCode: 'USD', openingBalance: 0, closingBalance: 50, totalDebits: 50, totalCredits: 0, lines: [] }]
});

describe('CustomerStatementComponent (A48 tasks 552–553)', () => {
  it('?customer= generates the statement: one section per currency with opening and closing (552, D-13)', async () => {
    const m = await mount(CustomerStatementComponent, { perms: ['AR_VIEW'], query: { customer: 'c1' }, prepare: api => api.getCustomerStatement.and.returnValue(of(statement)) });
    expect(m.api.getCustomerStatement).toHaveBeenCalledWith('c1', jasmine.objectContaining({ type: 'Activity', currencyCode: null }));
    expect(m.text('statement-title')).toContain('Acme Corp');
    expect(m.text('closing-PKR')).toBe('1,500.00');
    expect(m.q('statement-USD')).not.toBeNull();
    expect(m.text('statement-base')).toContain('PKR');
    expect(m.api.getStatementLog).toHaveBeenCalledWith('Customer', 'c1');
  });

  it('PDF download and email with subject / message (553); email and batch need AR_STATEMENT_SEND', async () => {
    const viewer = await mount(CustomerStatementComponent, { perms: ['AR_VIEW'], query: { customer: 'c1' }, prepare: api => api.getCustomerStatement.and.returnValue(of(statement)) });
    expect(viewer.q('email')).toBeNull();
    expect(viewer.q('batch')).toBeNull();

    const m = await mount(CustomerStatementComponent, { perms: ['AR_VIEW', 'AR_STATEMENT_SEND'], query: { customer: 'c1' }, prepare: api => {
      api.getCustomerStatement.and.returnValue(of(statement));
      api.downloadCustomerStatementPdf.and.returnValue(of(new HttpResponse({ body: new Blob(['%PDF']) })));
      api.emailCustomerStatement.and.returnValue(of({ emailedTo: 'ar@acme.test', emailedAt: '2026-10-25T10:00:00Z', closingBalanceBase: 1500 }));
    } });
    spyOn(URL, 'createObjectURL').and.returnValue('blob:x');
    spyOn(URL, 'revokeObjectURL');
    m.c.downloadPdf();
    expect(m.api.downloadCustomerStatementPdf).toHaveBeenCalledWith('c1', jasmine.objectContaining({ type: 'Activity' }));
    m.c.openEmail();
    m.c.emailTo = 'not-an-email';
    m.c.sendEmail();
    expect(m.c.emailError).toContain('valid email');
    m.c.emailTo = '';
    m.c.emailMessage = ' Please settle by month end. ';
    m.c.sendEmail();
    expect(m.api.emailCustomerStatement).toHaveBeenCalledWith('c1', jasmine.objectContaining({ fromDate: m.c.from }),
      { emailTo: null, subject: null, message: 'Please settle by month end.' });
    expect(m.toast).toHaveBeenCalledWith(jasmine.objectContaining({ detail: 'The statement was sent to ar@acme.test.' }));
  });

  it('REV: one address only; a failed send (409) stays in the dialog with the sender\'s error', async () => {
    const m = await mount(CustomerStatementComponent, { perms: ['AR_VIEW', 'AR_STATEMENT_SEND'], query: { customer: 'c1' }, prepare: api => {
      api.getCustomerStatement.and.returnValue(of(statement));
      api.emailCustomerStatement.and.returnValue(throwError(() => ({ status: 409, error: { message: 'The statement could not be emailed: SMTP 550 mailbox unavailable' } })));
    } });
    m.c.openEmail();
    m.c.emailTo = 'a@x.test, b@x.test';
    m.c.sendEmail();
    expect(m.c.emailError).toBe('Give one email address.');
    expect(m.api.emailCustomerStatement).not.toHaveBeenCalled();
    m.c.emailTo = 'a@x.test';
    m.c.sendEmail();
    expect(m.c.emailError).toBe('The statement could not be emailed: SMTP 550 mailbox unavailable');
    expect(m.c.emailOpen).toBeTrue();
  });

  it('REV: batch shows the failed customers with their error', async () => {
    const m = await mount(CustomerStatementComponent, { perms: ['AR_STATEMENT_SEND'], prepare: api =>
      api.batchStatements.and.returnValue(of({ considered: 2, generated: 2, emailed: 1, skipped: 0, noEmail: 0, failed: 1,
        items: [{ partnerId: 'c2', partnerName: 'Beta Ltd', closingBalanceBase: 900, emailed: false, outcome: 'FAILED', error: 'SMTP timeout' }] })) });
    m.c.runBatch();
    m.fixture.detectChanges();
    expect(m.text('batch-result')).toContain('1 failed');
    expect(m.text('batch')).toContain('SMTP timeout');
  });

  it('batch run with a minimum balance (BR-ST-005): counts and per-customer outcomes', async () => {
    const m = await mount(CustomerStatementComponent, { perms: ['AR_STATEMENT_SEND'], prepare: api =>
      api.batchStatements.and.returnValue(of({ considered: 7, generated: 5, emailed: 4, skipped: 1, noEmail: 1, failed: 0,
        items: [{ partnerId: 'c1', partnerName: 'Acme Corp', closingBalanceBase: 1500, emailed: false, outcome: 'NO_EMAIL' }] })) });
    m.c.minimumBalance = 1000;
    m.c.batchEmail = true;
    m.c.runBatch();
    m.fixture.detectChanges();
    expect(m.api.batchStatements).toHaveBeenCalledWith({ from: m.c.from, to: m.c.to, type: 'Activity', minimumBalance: 1000, sendEmail: true });
    expect(m.text('batch-result')).toContain('7 considered · 5 generated · 4 emailed · 1 skipped · 1 without an email');
    expect(m.text('batch')).toContain('No Email');
    expect(m.q('generate')).toBeNull();
  });

  it('asks for the customer and a valid period first', async () => {
    const m = await mount(CustomerStatementComponent, { perms: ['AR_VIEW'] });
    m.c.generate();
    expect(m.c.formError).toBe('Choose the customer.');
    expect(statementPeriodProblem('2026-10-01', '2026-09-01')).toBe('The period must start on or before its end.');
  });
});
