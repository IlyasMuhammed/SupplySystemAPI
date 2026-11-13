import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ApiResponse } from './demand.service';
import { environment } from '../../environments/environment';

/**
 * A50 D-25 / D-26 — the org's document (PDF) and email templates, and the "Email to …" / "Download PDF" document
 * actions (docs/onboarding/API-CONTRACT.md §4, TPL).
 */

export const DOCUMENT_TEMPLATE_TYPES = [
  'SalesInvoice', 'Quotation', 'PurchaseOrder', 'DeliveryNote', 'PaymentReceipt', 'CustomerStatement'
] as const;
export type DocumentTemplateType = typeof DOCUMENT_TEMPLATE_TYPES[number];

export const DOCUMENT_TEMPLATE_TYPE_LABEL: Record<string, string> = {
  SalesInvoice: 'Sales invoice',
  Quotation: 'Quotation',
  PurchaseOrder: 'Purchase order',
  DeliveryNote: 'Delivery note',
  PaymentReceipt: 'Payment receipt',
  CustomerStatement: 'Customer statement'
};

export const EMAIL_TEMPLATE_TYPES = [
  'InvoiceSent', 'QuotationSent', 'POToSupplier', 'PaymentReminder', 'PaymentReceived', 'WelcomeEmail', 'PayslipSent'
] as const;
export type EmailTemplateType = typeof EMAIL_TEMPLATE_TYPES[number];

export const EMAIL_TEMPLATE_TYPE_LABEL: Record<string, string> = {
  InvoiceSent: 'Invoice sent',
  QuotationSent: 'Quotation sent',
  POToSupplier: 'Purchase order to supplier',
  PaymentReminder: 'Payment reminder',
  PaymentReceived: 'Payment received',
  WelcomeEmail: 'Welcome email',
  PayslipSent: 'Payslip sent'
};

/** A type's label, or the code itself for a type this page does not know yet. */
export function templateTypeLabel(type: string | null | undefined): string {
  if (!type) return '';
  return DOCUMENT_TEMPLATE_TYPE_LABEL[type] ?? EMAIL_TEMPLATE_TYPE_LABEL[type] ?? type;
}

export interface DocumentTemplateModel {
  uuid: string;
  templateType: string;
  name: string;
  headerHtml?: string | null;
  bodyHtml?: string | null;
  footerHtml?: string | null;
  logoAttachmentUuid?: string | null;
  /** Where the logo can be fetched from (relative /uploads/… or /api/…), when the server sends it. */
  logoUrl?: string | null;
  primaryColor?: string | null;
  showBankDetails: boolean;
  bankDetailsText?: string | null;
  termsAndConditions?: string | null;
  isDefault: boolean;
  isSeeded: boolean;
  isActive?: boolean;
  modifiedAt?: string | null;
}

/** POST (create) body; PUT takes the same fields minus templateType / cloneFromUuid / isDefault. */
export interface DocumentTemplateRequest {
  templateType?: string;
  name: string;
  /** POST only: copy this template first, then override with any non-null field. */
  cloneFromUuid?: string | null;
  isDefault?: boolean;
  headerHtml?: string | null;
  bodyHtml?: string | null;
  footerHtml?: string | null;
  logoAttachmentUuid?: string | null;
  primaryColor?: string | null;
  showBankDetails: boolean;
  bankDetailsText?: string | null;
  termsAndConditions?: string | null;
  isActive?: boolean;
}

export interface EmailTemplateModel {
  uuid: string;
  templateType: string;
  name: string;
  subjectTemplate: string;
  bodyHtml: string;
  isDefault: boolean;
  isSeeded: boolean;
  isActive?: boolean;
  modifiedAt?: string | null;
}

export interface EmailTemplateRequest {
  templateType?: string;
  name: string;
  subjectTemplate?: string | null;
  bodyHtml?: string | null;
  cloneFromUuid?: string | null;
  isDefault?: boolean;
  isActive?: boolean;
}

export interface EmailPreviewResult { subject: string; bodyHtml: string; }

export interface SendTestResult { sent: boolean; toEmail?: string | null; subject?: string | null; error?: string | null; }

/** One `{Token}` a template of a type may use. */
export interface TemplatePlaceholder {
  token: string;
  label?: string | null;
  description?: string | null;
  group?: string | null;
  /** A block token placed on a line of its own ({LineItems}, {Totals}, …). */
  structural?: boolean;
}

export type EmailableDocument = 'SalesInvoice' | 'Quotation' | 'PurchaseOrder';

export interface DocumentEmailRequest {
  to?: string | null;
  cc?: string | null;
  message?: string | null;
}

export interface DocumentEmailResult {
  /** Every recipient succeeded. */
  sent: boolean;
  sentTo?: string[] | null;
  cc?: string[] | null;
  subject?: string | null;
  failed?: { email: string; error?: string | null }[] | null;
}

const ONB = `${environment.apiUrl}/onboarding`;
const EMAIL_ROUTE: Record<EmailableDocument, string> = {
  SalesInvoice: 'sales-invoices',
  Quotation: 'sale-quotations',
  PurchaseOrder: 'purchase-orders'
};

/** "{Token}" whatever the server sends ("Token", "{Token}", "{{Token}}"). */
export function normalizeToken(token: string): string {
  const bare = (token ?? '').trim().replace(/^\{+/, '').replace(/\}+$/, '');
  return bare ? `{${bare}}` : '';
}

@Injectable({ providedIn: 'root' })
export class OnboardingTemplatesService {
  constructor(private http: HttpClient) {}

  // ── Document templates (DOC_TEMPLATE_MANAGE) ──────────────────────────────────

  getDocumentTemplates(type?: string | null): Observable<ApiResponse<DocumentTemplateModel[]>> {
    let params = new HttpParams();
    if (type) params = params.set('type', type);
    return this.http.get<ApiResponse<DocumentTemplateModel[]>>(`${ONB}/document-templates`, { params });
  }

  getDocumentTemplate(uuid: string): Observable<ApiResponse<DocumentTemplateModel>> {
    return this.http.get<ApiResponse<DocumentTemplateModel>>(`${ONB}/document-templates/${uuid}`);
  }

  createDocumentTemplate(req: DocumentTemplateRequest): Observable<ApiResponse<DocumentTemplateModel>> {
    return this.http.post<ApiResponse<DocumentTemplateModel>>(`${ONB}/document-templates`, req);
  }

  updateDocumentTemplate(uuid: string, req: DocumentTemplateRequest): Observable<ApiResponse<DocumentTemplateModel>> {
    return this.http.put<ApiResponse<DocumentTemplateModel>>(`${ONB}/document-templates/${uuid}`, req);
  }

  setDefaultDocumentTemplate(uuid: string): Observable<ApiResponse<DocumentTemplateModel>> {
    return this.http.post<ApiResponse<DocumentTemplateModel>>(`${ONB}/document-templates/${uuid}/set-default`, {});
  }

  /** Puts the seeded template of the type back as it was shipped. */
  resetDocumentTemplate(type: string): Observable<ApiResponse<DocumentTemplateModel>> {
    return this.http.post<ApiResponse<DocumentTemplateModel>>(`${ONB}/document-templates/reset/${encodeURIComponent(type)}`, {});
  }

  /** The template rendered with sample data. The editor's unsaved fields go along, so the preview shows the edit. */
  previewDocumentTemplate(uuid: string, draft?: DocumentTemplateRequest | null): Observable<Blob> {
    return this.http.post(`${ONB}/document-templates/${uuid}/preview`, draft ?? {}, { responseType: 'blob' });
  }

  getDocumentPlaceholders(type: string): Observable<ApiResponse<TemplatePlaceholder[]>> {
    return this.http.get<ApiResponse<TemplatePlaceholder[]>>(`${ONB}/document-templates/placeholders/${encodeURIComponent(type)}`);
  }

  // ── Email templates (EMAIL_TEMPLATE_MANAGE) ───────────────────────────────────

  getEmailTemplates(type?: string | null): Observable<ApiResponse<EmailTemplateModel[]>> {
    let params = new HttpParams();
    if (type) params = params.set('type', type);
    return this.http.get<ApiResponse<EmailTemplateModel[]>>(`${ONB}/email-templates`, { params });
  }

  getEmailTemplate(uuid: string): Observable<ApiResponse<EmailTemplateModel>> {
    return this.http.get<ApiResponse<EmailTemplateModel>>(`${ONB}/email-templates/${uuid}`);
  }

  createEmailTemplate(req: EmailTemplateRequest): Observable<ApiResponse<EmailTemplateModel>> {
    return this.http.post<ApiResponse<EmailTemplateModel>>(`${ONB}/email-templates`, req);
  }

  updateEmailTemplate(uuid: string, req: EmailTemplateRequest): Observable<ApiResponse<EmailTemplateModel>> {
    return this.http.put<ApiResponse<EmailTemplateModel>>(`${ONB}/email-templates/${uuid}`, req);
  }

  setDefaultEmailTemplate(uuid: string): Observable<ApiResponse<EmailTemplateModel>> {
    return this.http.post<ApiResponse<EmailTemplateModel>>(`${ONB}/email-templates/${uuid}/set-default`, {});
  }

  resetEmailTemplate(type: string): Observable<ApiResponse<EmailTemplateModel>> {
    return this.http.post<ApiResponse<EmailTemplateModel>>(`${ONB}/email-templates/reset/${encodeURIComponent(type)}`, {});
  }

  /** Subject + body with sample values. */
  previewEmailTemplate(uuid: string, placeholders?: Record<string, string> | null): Observable<ApiResponse<EmailPreviewResult>> {
    return this.http.post<ApiResponse<EmailPreviewResult>>(`${ONB}/email-templates/${uuid}/preview`, placeholders ? { placeholders } : {});
  }

  /** A failed send is still 200 with sent:false. */
  sendTestEmail(uuid: string, toEmail: string): Observable<ApiResponse<SendTestResult>> {
    return this.http.post<ApiResponse<SendTestResult>>(`${ONB}/email-templates/${uuid}/send-test`, { toEmail });
  }

  getEmailPlaceholders(type: string): Observable<ApiResponse<TemplatePlaceholder[]>> {
    return this.http.get<ApiResponse<TemplatePlaceholder[]>>(`${ONB}/email-templates/placeholders/${encodeURIComponent(type)}`);
  }

  // ── Document actions (each document's own permissions) ────────────────────────

  /** Emails the document's PDF (attached by the server) to its customer / supplier. */
  emailDocument(kind: EmailableDocument, uuid: string, req: DocumentEmailRequest): Observable<ApiResponse<DocumentEmailResult | null>> {
    const body: DocumentEmailRequest = {};
    if (req.to?.trim()) body.to = req.to.trim();
    if (req.cc?.trim()) body.cc = req.cc.trim();
    if (req.message?.trim()) body.message = req.message.trim();
    return this.http.post<ApiResponse<DocumentEmailResult | null>>(`${environment.apiUrl}/${EMAIL_ROUTE[kind]}/${uuid}/email`, body);
  }

  downloadQuotationPdf(uuid: string): Observable<Blob> {
    return this.http.get(`${environment.apiUrl}/sale-quotations/${uuid}/pdf`, { responseType: 'blob' });
  }

  downloadCustomerPaymentPdf(uuid: string): Observable<Blob> {
    return this.http.get(`${environment.apiUrl}/customer-payments/${uuid}/pdf`, { responseType: 'blob' });
  }
}

/** Saves a blob as a file (a link cannot carry the bearer token, so PDFs come back as blobs). */
export function saveBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName;
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

/**
 * The server's message from an error, also when the body is a Blob (a failed PDF call answers JSON in a blob).
 * Resolves to the fallback when there is none.
 */
export async function errorMessageOf(err: any, fallback: string): Promise<string> {
  const body = err?.error;
  if (body instanceof Blob) {
    try {
      const text = await body.text();
      const parsed = JSON.parse(text);
      return parsed?.message || fallback;
    } catch {
      return fallback;
    }
  }
  return body?.message || fallback;
}
