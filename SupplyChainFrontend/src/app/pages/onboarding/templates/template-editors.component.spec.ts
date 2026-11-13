import { ComponentFixture, TestBed } from '@angular/core/testing';
import { ActivatedRoute, provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';
import { DocumentTemplateEditorComponent, DOC_TEMPLATE_LOGO } from './document-template-editor.component';
import { EmailTemplateEditorComponent } from './email-template-editor.component';
import { DocumentTemplateModel, EmailTemplateModel, OnboardingTemplatesService } from '../../../services/onboarding-templates.service';
import { AttachmentService } from '../../../services/attachment.service';

const ok = <T>(result: T) => of({ success: true, message: '', result });
const route = (uuid: string) => ({ provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['uuid', uuid]]) } } });

function doc(over: Partial<DocumentTemplateModel> = {}): DocumentTemplateModel {
  return {
    uuid: 'd1', templateType: 'SalesInvoice', name: 'Standard Sales Invoice', headerHtml: '<p><strong>{CompanyName}</strong></p>',
    bodyHtml: null, footerHtml: '<p>Thanks</p>', logoAttachmentUuid: null, logoUrl: null, primaryColor: '#4A42CC',
    showBankDetails: true, bankDetailsText: 'HBL', termsAndConditions: 'T&C', isDefault: false, isSeeded: true, isActive: true, ...over
  };
}

describe('DocumentTemplateEditorComponent (A50 tasks 298–304)', () => {
  let fixture: ComponentFixture<DocumentTemplateEditorComponent>;
  let c: DocumentTemplateEditorComponent;
  let api: jasmine.SpyObj<OnboardingTemplatesService>;
  let attachments: jasmine.SpyObj<AttachmentService>;
  let toasts: jasmine.Spy;
  const q = (id: string): HTMLElement | null => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);

  async function setup(get: any = ok(doc())) {
    api = jasmine.createSpyObj<OnboardingTemplatesService>('OnboardingTemplatesService', [
      'getDocumentTemplate', 'getDocumentPlaceholders', 'updateDocumentTemplate', 'previewDocumentTemplate',
      'setDefaultDocumentTemplate', 'resetDocumentTemplate', 'createDocumentTemplate']);
    api.getDocumentTemplate.and.returnValue(get);
    api.getDocumentPlaceholders.and.returnValue(ok([{ token: '{InvoiceNumber}', label: 'Invoice number', group: 'Document' }]));
    attachments = jasmine.createSpyObj<AttachmentService>('AttachmentService', ['upload', 'getAttachments', 'resolveUrl', 'isApiUrl', 'download']);
    attachments.resolveUrl.and.callFake((u: string) => 'https://api' + u);
    attachments.isApiUrl.and.returnValue(false);
    await TestBed.configureTestingModule({
      imports: [DocumentTemplateEditorComponent],
      providers: [provideRouter([]), provideNoopAnimations(), route('d1'),
        { provide: OnboardingTemplatesService, useValue: api }, { provide: AttachmentService, useValue: attachments }]
    }).compileComponents();
    fixture = TestBed.createComponent(DocumentTemplateEditorComponent);
    c = fixture.componentInstance;
    toasts = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  it('loads the template into the form and the HTML fields, and its type\'s placeholders', async () => {
    await setup();
    expect(api.getDocumentPlaceholders).toHaveBeenCalledWith('SalesInvoice');
    expect((q('name') as HTMLInputElement).value).toBe('Standard Sales Invoice');
    expect(q('html-headerHtml')?.innerHTML).toContain('{CompanyName}');
    expect(q('ph-InvoiceNumber')).not.toBeNull();
  });

  it('not found and error states', async () => {
    await setup(throwError(() => ({ status: 404 })));
    expect(q('editor-not-found')).not.toBeNull();
    TestBed.resetTestingModule();
    await setup(throwError(() => ({ status: 500, error: { message: 'Boom' } })));
    expect(q('editor-error')?.textContent).toContain('Boom');
  });

  it('save sends every field (empty HTML as null, colour upper-cased)', async () => {
    await setup();
    api.updateDocumentTemplate.and.returnValue(ok(doc({ primaryColor: '#1F6FEB' })));
    c.onColorText('1f6feb');
    c.bodyHtml = '<br>';
    c.save();
    expect(api.updateDocumentTemplate).toHaveBeenCalledWith('d1', jasmine.objectContaining({
      name: 'Standard Sales Invoice', primaryColor: '#1F6FEB', bodyHtml: null, showBankDetails: true, bankDetailsText: 'HBL',
      termsAndConditions: 'T&C', isActive: true
    }));
  });

  it('refuses a bad colour', async () => {
    await setup();
    c.onColorText('#12');
    expect(c.colorValid).toBeFalse();
    expect(c.canSave).toBeFalse();
  });

  it('placeholder clicks insert into the field last focused', async () => {
    await setup();
    c.target = 'footerHtml';
    c.insertPlaceholder('{InvoiceNumber}');
    expect(c.footerHtml).toContain('{InvoiceNumber}');
    expect(c.dirty).toBeTrue();
  });

  it('logo upload uses DOC_TEMPLATE_LOGO with the template uuid, and saves the attachment uuid', async () => {
    await setup();
    attachments.upload.and.returnValue(ok('att-1') as any);
    attachments.getAttachments.and.returnValue(ok([{ uuid: 'att-1', fileUrl: '/uploads/logo.png' }]) as any);
    c.uploadLogo(new File(['x'], 'logo.png', { type: 'image/png' }));
    expect(attachments.upload).toHaveBeenCalledWith(jasmine.any(File), DOC_TEMPLATE_LOGO, 'd1');
    expect(c.logoAttachmentUuid).toBe('att-1');
    expect(c.logoSrc).toBe('https://api/uploads/logo.png');
    expect(c.request().logoAttachmentUuid).toBe('att-1');
  });

  it('refuses a non-image or a too-large logo', async () => {
    await setup();
    c.uploadLogo(new File(['x'], 'a.pdf', { type: 'application/pdf' }));
    expect(attachments.upload).not.toHaveBeenCalled();
    expect(toasts).toHaveBeenCalledWith(jasmine.objectContaining({ summary: 'Not a picture' }));
  });

  it('preview posts the unsaved edit and shows the PDF', async () => {
    await setup();
    api.previewDocumentTemplate.and.returnValue(of(new Blob(['%PDF'], { type: 'application/pdf' })));
    c.name = 'Edited';
    c.preview();
    expect(api.previewDocumentTemplate).toHaveBeenCalledWith('d1', jasmine.objectContaining({ name: 'Edited' }));
    expect(c.previewUrl).not.toBeNull();
    c.closePreview();
    expect(c.previewUrl).toBeNull();
  });

  it('set as default refuses while there are unsaved edits, then works', async () => {
    await setup();
    c.touch();
    c.setDefault();
    expect(api.setDefaultDocumentTemplate).not.toHaveBeenCalled();
    c.dirty = false;
    api.setDefaultDocumentTemplate.and.returnValue(ok(doc({ isDefault: true })));
    c.setDefault();
    expect(c.template?.isDefault).toBeTrue();
  });
});

function mail(over: Partial<EmailTemplateModel> = {}): EmailTemplateModel {
  return { uuid: 'e1', templateType: 'InvoiceSent', name: 'Invoice to customer', subjectTemplate: 'Invoice {InvoiceNumber}',
    bodyHtml: '<p>Dear {CustomerName},</p>', isDefault: true, isSeeded: true, isActive: true, ...over };
}

describe('EmailTemplateEditorComponent (A50 tasks 306–308)', () => {
  let fixture: ComponentFixture<EmailTemplateEditorComponent>;
  let c: EmailTemplateEditorComponent;
  let api: jasmine.SpyObj<OnboardingTemplatesService>;
  const q = (id: string): HTMLElement | null => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);

  beforeEach(async () => {
    api = jasmine.createSpyObj<OnboardingTemplatesService>('OnboardingTemplatesService', [
      'getEmailTemplate', 'getEmailPlaceholders', 'updateEmailTemplate', 'previewEmailTemplate', 'sendTestEmail',
      'setDefaultEmailTemplate', 'resetEmailTemplate', 'createEmailTemplate']);
    api.getEmailTemplate.and.returnValue(ok(mail()));
    api.getEmailPlaceholders.and.returnValue(ok([{ token: '{CompanyName}', label: 'Company' }]));
    await TestBed.configureTestingModule({
      imports: [EmailTemplateEditorComponent],
      providers: [provideRouter([]), provideNoopAnimations(), route('e1'), { provide: OnboardingTemplatesService, useValue: api }]
    }).compileComponents();
    fixture = TestBed.createComponent(EmailTemplateEditorComponent);
    c = fixture.componentInstance;
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  });

  it('loads subject and body', () => {
    expect((q('subject') as HTMLInputElement).value).toBe('Invoice {InvoiceNumber}');
    expect(q('html-body')?.innerHTML).toContain('{CustomerName}');
  });

  it('inserts a placeholder at the subject cursor', () => {
    c.target = 'subject';
    (c as any).subjectCaret = 0;
    c.insertPlaceholder('{CompanyName}');
    expect(c.subjectTemplate).toBe('{CompanyName}Invoice {InvoiceNumber}');
  });

  it('validates the subject (one line, required) and the body', () => {
    c.subjectTemplate = 'a\nb';
    expect(c.subjectValid).toBeFalse();
    c.subjectTemplate = 'ok';
    c.bodyHtml = '<p></p>';
    expect(c.canSave).toBeFalse();
  });

  it('save sends name, subject, body, active', () => {
    api.updateEmailTemplate.and.returnValue(ok(mail()));
    c.save();
    expect(api.updateEmailTemplate).toHaveBeenCalledWith('e1', { name: 'Invoice to customer', subjectTemplate: 'Invoice {InvoiceNumber}', bodyHtml: '<p>Dear {CustomerName},</p>', isActive: true });
  });

  it('send test: sent, and a 200 with sent:false is shown as a failure', () => {
    c.testEmail = 'me@x.com';
    api.sendTestEmail.and.returnValue(ok({ sent: true, toEmail: 'me@x.com', subject: '[TEST] Invoice', error: null }));
    c.sendTest();
    expect(api.sendTestEmail).toHaveBeenCalledWith('e1', 'me@x.com');
    expect(c.testResult).toEqual({ ok: true, text: 'Test email sent to me@x.com.' });
    api.sendTestEmail.and.returnValue(ok({ sent: false, toEmail: 'me@x.com', error: 'SMTP refused' }));
    c.sendTest();
    expect(c.testResult).toEqual({ ok: false, text: 'SMTP refused' });
  });

  it('send test needs one valid address', () => {
    c.testEmail = 'a@b.com; c@d.com';
    expect(c.testValid).toBeFalse();
    c.testEmail = 'nope';
    expect(c.testValid).toBeFalse();
  });

  it('preview shows the rendered subject and body', () => {
    api.previewEmailTemplate.and.returnValue(ok({ subject: 'Invoice SINV-1 from Sunrise', bodyHtml: '<p>Dear Acme,</p>' }));
    c.preview();
    fixture.detectChanges();
    expect(document.querySelector('[data-testid="preview-subject"]')?.textContent).toContain('SINV-1');
  });
});
