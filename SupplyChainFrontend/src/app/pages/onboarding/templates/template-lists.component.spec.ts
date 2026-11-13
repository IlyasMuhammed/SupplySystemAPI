import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ConfirmationService } from 'primeng/api';
import { of, throwError } from 'rxjs';
import { DocumentTemplateListComponent } from './document-template-list.component';
import { EmailTemplateListComponent } from './email-template-list.component';
import { DocumentTemplateModel, EmailTemplateModel, OnboardingTemplatesService } from '../../../services/onboarding-templates.service';

const ok = <T>(result: T) => of({ success: true, message: '', result });

function doc(over: Partial<DocumentTemplateModel> = {}): DocumentTemplateModel {
  return { uuid: 'd1', templateType: 'SalesInvoice', name: 'Standard Sales Invoice', showBankDetails: true, isDefault: true, isSeeded: true, isActive: true, ...over };
}
function mail(over: Partial<EmailTemplateModel> = {}): EmailTemplateModel {
  return { uuid: 'e1', templateType: 'InvoiceSent', name: 'Invoice to customer', subjectTemplate: 'Invoice {InvoiceNumber}', bodyHtml: '<p>Hi</p>', isDefault: true, isSeeded: true, isActive: true, ...over };
}

describe('DocumentTemplateListComponent (A50 task 297)', () => {
  let fixture: ComponentFixture<DocumentTemplateListComponent>;
  let api: jasmine.SpyObj<OnboardingTemplatesService>;
  let router: Router;
  const q = (id: string): HTMLElement | null => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);

  async function setup(list: DocumentTemplateModel[] | 'error') {
    api = jasmine.createSpyObj<OnboardingTemplatesService>('OnboardingTemplatesService',
      ['getDocumentTemplates', 'createDocumentTemplate', 'setDefaultDocumentTemplate', 'resetDocumentTemplate']);
    api.getDocumentTemplates.and.returnValue(list === 'error' ? throwError(() => ({ error: { message: 'Down' } })) : ok(list));
    await TestBed.configureTestingModule({
      imports: [DocumentTemplateListComponent],
      providers: [provideRouter([]), provideNoopAnimations(), { provide: OnboardingTemplatesService, useValue: api }]
    }).compileComponents();
    router = TestBed.inject(Router);
    spyOn(router, 'navigate').and.returnValue(Promise.resolve(true));
    fixture = TestBed.createComponent(DocumentTemplateListComponent);
    fixture.detectChanges();
  }

  it('lists type, name, default and seeded, with set-default only on non-defaults', async () => {
    await setup([doc(), doc({ uuid: 'd2', name: 'Blue invoice', isDefault: false, isSeeded: false })]);
    expect(q('row-d1')?.textContent).toContain('Sales invoice');
    expect(q('row-d1')?.textContent).toContain('Default');
    expect(q('row-d2')?.textContent).toContain('Custom');
    expect(q('set-default-d1')).toBeNull();
    expect(q('set-default-d2')).not.toBeNull();
    expect(q('reset-d2')).toBeNull();     // only the seeded row resets
  });

  it('filters by type chip, with an empty state', async () => {
    await setup([doc()]);
    (q('chip-Quotation') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(q('list-empty')).not.toBeNull();
  });

  it('shows the error state', async () => {
    await setup('error');
    expect(q('list-error')?.textContent).toContain('Down');
  });

  it('clone posts cloneFromUuid and opens the copy', async () => {
    await setup([doc()]);
    api.createDocumentTemplate.and.returnValue(ok(doc({ uuid: 'd9', name: 'Copy of Standard Sales Invoice', isDefault: false })));
    fixture.componentInstance.clone(doc());
    expect(api.createDocumentTemplate).toHaveBeenCalledWith(jasmine.objectContaining({ cloneFromUuid: 'd1', name: 'Copy of Standard Sales Invoice', templateType: 'SalesInvoice' }));
    expect(router.navigate).toHaveBeenCalledWith(['/portal/pages/onboarding/document-templates', 'd9']);
  });

  it('reset asks first, then resets the type and reloads', async () => {
    await setup([doc()]);
    const confirm = fixture.debugElement.injector.get(ConfirmationService);
    spyOn(confirm, 'confirm').and.callFake((c: any) => { c.accept(); return confirm; });
    api.resetDocumentTemplate.and.returnValue(ok(doc()));
    fixture.componentInstance.confirmReset('SalesInvoice');
    expect(api.resetDocumentTemplate).toHaveBeenCalledWith('SalesInvoice');
    expect(api.getDocumentTemplates).toHaveBeenCalledTimes(2);
  });

  it('set as default calls the API and reloads', async () => {
    await setup([doc({ isDefault: false })]);
    api.setDefaultDocumentTemplate.and.returnValue(ok(doc()));
    fixture.componentInstance.setDefault(doc({ isDefault: false }));
    expect(api.setDefaultDocumentTemplate).toHaveBeenCalledWith('d1');
    expect(api.getDocumentTemplates).toHaveBeenCalledTimes(2);
  });
});

describe('EmailTemplateListComponent (A50 task 305)', () => {
  let fixture: ComponentFixture<EmailTemplateListComponent>;
  let api: jasmine.SpyObj<OnboardingTemplatesService>;
  const q = (id: string): HTMLElement | null => fixture.nativeElement.querySelector(`[data-testid="${id}"]`);

  beforeEach(async () => {
    api = jasmine.createSpyObj<OnboardingTemplatesService>('OnboardingTemplatesService',
      ['getEmailTemplates', 'createEmailTemplate', 'setDefaultEmailTemplate', 'resetEmailTemplate']);
    api.getEmailTemplates.and.returnValue(ok([mail(), mail({ uuid: 'e2', templateType: 'PayslipSent', name: 'Payslip', isDefault: true })]));
    await TestBed.configureTestingModule({
      imports: [EmailTemplateListComponent],
      providers: [provideRouter([]), provideNoopAnimations(), { provide: OnboardingTemplatesService, useValue: api }]
    }).compileComponents();
    spyOn(TestBed.inject(Router), 'navigate').and.returnValue(Promise.resolve(true));
    fixture = TestBed.createComponent(EmailTemplateListComponent);
    fixture.detectChanges();
  });

  it('lists templates with the subject, and marks PayslipSent as for later (D-30)', () => {
    expect(q('row-e1')?.textContent).toContain('Invoice {InvoiceNumber}');
    expect(q('row-e2')?.textContent).toContain('Later');
  });

  it('a new template starts as a clone of the type\'s default', () => {
    const c = fixture.componentInstance;
    api.createEmailTemplate.and.returnValue(ok(mail({ uuid: 'e5' })));
    c.newType = 'InvoiceSent'; c.newName = 'Friendly invoice';
    c.create();
    expect(api.createEmailTemplate).toHaveBeenCalledWith({ templateType: 'InvoiceSent', name: 'Friendly invoice', cloneFromUuid: 'e1', isDefault: false });
  });

  it('a new template of a type with none sends a subject and a body', () => {
    const c = fixture.componentInstance;
    api.createEmailTemplate.and.returnValue(ok(mail({ uuid: 'e6' })));
    c.newType = 'WelcomeEmail'; c.newName = 'Welcome';
    c.create();
    expect(api.createEmailTemplate).toHaveBeenCalledWith(jasmine.objectContaining({ templateType: 'WelcomeEmail', subjectTemplate: 'Welcome' }));
  });
});
