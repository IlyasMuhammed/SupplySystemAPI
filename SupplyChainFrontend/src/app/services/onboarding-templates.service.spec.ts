import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import {
  OnboardingTemplatesService, errorMessageOf, normalizeToken, templateTypeLabel
} from './onboarding-templates.service';
import { environment } from '../../environments/environment';

const API = environment.apiUrl;
const ONB = `${API}/onboarding`;
const ok = <T>(result: T) => ({ success: true, message: '', result });

describe('OnboardingTemplatesService (A50 D-TPL)', () => {
  let svc: OnboardingTemplatesService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    svc = TestBed.inject(OnboardingTemplatesService);
    http = TestBed.inject(HttpTestingController);
  });
  afterEach(() => http.verify());

  it('document templates: list (type filter), get, create with clone, update, set-default, reset, placeholders', () => {
    svc.getDocumentTemplates('SalesInvoice').subscribe();
    expect(http.expectOne(r => r.url === `${ONB}/document-templates` && r.params.get('type') === 'SalesInvoice').request.method).toBe('GET');
    svc.getDocumentTemplates().subscribe();
    expect(http.expectOne(r => r.url === `${ONB}/document-templates` && !r.params.has('type'))).toBeTruthy();

    svc.getDocumentTemplate('u1').subscribe();
    http.expectOne(`${ONB}/document-templates/u1`).flush(ok({}));

    svc.createDocumentTemplate({ templateType: 'Quotation', name: 'Copy', cloneFromUuid: 'u1', showBankDetails: true }).subscribe();
    const c = http.expectOne(`${ONB}/document-templates`);
    expect(c.request.method).toBe('POST');
    expect(c.request.body.cloneFromUuid).toBe('u1');

    svc.updateDocumentTemplate('u1', { name: 'X', showBankDetails: false }).subscribe();
    expect(http.expectOne(`${ONB}/document-templates/u1`).request.method).toBe('PUT');
    svc.setDefaultDocumentTemplate('u1').subscribe();
    expect(http.expectOne(`${ONB}/document-templates/u1/set-default`).request.method).toBe('POST');
    svc.resetDocumentTemplate('PurchaseOrder').subscribe();
    expect(http.expectOne(`${ONB}/document-templates/reset/PurchaseOrder`).request.method).toBe('POST');
    svc.getDocumentPlaceholders('SalesInvoice').subscribe();
    http.expectOne(`${ONB}/document-templates/placeholders/SalesInvoice`).flush(ok([]));
  });

  it('preview posts the unsaved draft and asks for a blob', () => {
    svc.previewDocumentTemplate('u1', { name: 'Draft', showBankDetails: false, primaryColor: '#112233' }).subscribe();
    const r = http.expectOne(`${ONB}/document-templates/u1/preview`);
    expect(r.request.method).toBe('POST');
    expect(r.request.responseType).toBe('blob');
    expect(r.request.body.primaryColor).toBe('#112233');
  });

  it('email templates: set-default, reset, preview, send-test, placeholders', () => {
    svc.setDefaultEmailTemplate('e1').subscribe();
    http.expectOne(`${ONB}/email-templates/e1/set-default`);
    svc.resetEmailTemplate('InvoiceSent').subscribe();
    http.expectOne(`${ONB}/email-templates/reset/InvoiceSent`);
    svc.previewEmailTemplate('e1').subscribe();
    expect(http.expectOne(`${ONB}/email-templates/e1/preview`).request.body).toEqual({});
    svc.sendTestEmail('e1', 'me@x.com').subscribe();
    expect(http.expectOne(`${ONB}/email-templates/e1/send-test`).request.body).toEqual({ toEmail: 'me@x.com' });
    svc.getEmailPlaceholders('POToSupplier').subscribe();
    http.expectOne(`${ONB}/email-templates/placeholders/POToSupplier`);
  });

  it('document email goes to each document\'s own route, sending only filled fields', () => {
    svc.emailDocument('SalesInvoice', 'i1', { to: ' a@b.com ', cc: '', message: '  ' }).subscribe();
    expect(http.expectOne(`${API}/sales-invoices/i1/email`).request.body).toEqual({ to: 'a@b.com' });
    svc.emailDocument('Quotation', 'q1', { message: 'Hi' }).subscribe();
    expect(http.expectOne(`${API}/sale-quotations/q1/email`).request.body).toEqual({ message: 'Hi' });
    svc.emailDocument('PurchaseOrder', 'p1', {}).subscribe();
    expect(http.expectOne(`${API}/purchase-orders/p1/email`).request.body).toEqual({});
  });

  it('PDF downloads', () => {
    svc.downloadQuotationPdf('q1').subscribe();
    expect(http.expectOne(`${API}/sale-quotations/q1/pdf`).request.responseType).toBe('blob');
    svc.downloadCustomerPaymentPdf('c1').subscribe();
    expect(http.expectOne(`${API}/customer-payments/c1/pdf`).request.responseType).toBe('blob');
  });

  it('helpers: token normalising, type labels, blob error messages', async () => {
    expect(normalizeToken('InvoiceNumber')).toBe('{InvoiceNumber}');
    expect(normalizeToken('{{InvoiceNumber}}')).toBe('{InvoiceNumber}');
    expect(normalizeToken('')).toBe('');
    expect(templateTypeLabel('POToSupplier')).toBe('Purchase order to supplier');
    expect(templateTypeLabel('Unknown')).toBe('Unknown');
    expect(await errorMessageOf({ error: new Blob([JSON.stringify({ message: 'Nope' })]) }, 'fb')).toBe('Nope');
    expect(await errorMessageOf({ error: new Blob(['<html>']) }, 'fb')).toBe('fb');
    expect(await errorMessageOf({ error: { message: 'Plain' } }, 'fb')).toBe('Plain');
  });
});
