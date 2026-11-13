import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of, throwError } from 'rxjs';
import {
  DocumentEmailDialogComponent, describeFailures, emailSentToast, validEmailList
} from './document-email-dialog.component';
import { OnboardingTemplatesService } from '../../../services/onboarding-templates.service';

describe('DocumentEmailDialogComponent (A50 D-26)', () => {
  let fixture: ComponentFixture<DocumentEmailDialogComponent>;
  let c: DocumentEmailDialogComponent;
  let api: jasmine.SpyObj<OnboardingTemplatesService>;

  beforeEach(async () => {
    api = jasmine.createSpyObj<OnboardingTemplatesService>('OnboardingTemplatesService', ['emailDocument']);
    await TestBed.configureTestingModule({
      imports: [DocumentEmailDialogComponent],
      providers: [provideNoopAnimations(), { provide: OnboardingTemplatesService, useValue: api }]
    }).compileComponents();
    fixture = TestBed.createComponent(DocumentEmailDialogComponent);
    c = fixture.componentInstance;
    c.kind = 'SalesInvoice';
    c.uuid = 'inv-1';
    c.documentLabel = 'invoice SINV-1';
    fixture.detectChanges();
  });

  function open() {
    fixture.componentRef.setInput('visible', true);
    fixture.detectChanges();
  }

  it('opens empty with the "defaults to the customer\'s email" hint, and resets each time it opens', () => {
    open();
    expect(document.querySelector('[data-testid="email-to-hint"]')?.textContent).toContain("customer's email on file");
    c.to = 'x@y.com'; c.message = 'hi';
    fixture.componentRef.setInput('visible', false); fixture.detectChanges();
    open();
    expect(c.to).toBe('');
    expect(c.message).toBe('');
  });

  it('validates To and Cc as comma / semicolon lists', () => {
    expect(validEmailList('', false)).toBeTrue();
    expect(validEmailList('', true)).toBeFalse();
    expect(validEmailList('a@b.com; c@d.org, e@f.io', false)).toBeTrue();
    expect(validEmailList('a@b', false)).toBeFalse();
    c.to = 'not-an-email';
    expect(c.canSend).toBeFalse();
    c.to = 'a@b.com';
    expect(c.canSend).toBeTrue();
  });

  it('sends, emits the result and closes', () => {
    const result = { sent: true, sentTo: ['a@b.com'], cc: [], failed: [] };
    api.emailDocument.and.returnValue(of({ success: true, message: '', result }));
    const sent = jasmine.createSpy('sent'); const closed = jasmine.createSpy('closed');
    c.sent.subscribe(sent); c.visibleChange.subscribe(closed);
    open();
    c.to = 'a@b.com'; c.cc = 'c@d.com'; c.message = 'Thanks';
    c.send();
    expect(api.emailDocument).toHaveBeenCalledWith('SalesInvoice', 'inv-1', { to: 'a@b.com', cc: 'c@d.com', message: 'Thanks' });
    expect(sent).toHaveBeenCalledWith(result);
    expect(closed).toHaveBeenCalledWith(false);
  });

  it('a 400 ("No recipient…") or a send that reached nobody stays open and emits the reason', () => {
    const failed = jasmine.createSpy('failed');
    c.failed.subscribe(failed);
    open();
    api.emailDocument.and.returnValue(throwError(() => ({ status: 400, error: { message: 'No recipient: enter an email address' } })));
    c.send();
    expect(failed).toHaveBeenCalledWith('No recipient: enter an email address');
    api.emailDocument.and.returnValue(of({ success: true, message: '', result: { sent: false, sentTo: [], failed: [{ email: 'a@b.com', error: 'down' }] } }));
    c.send();
    expect(failed).toHaveBeenCalledWith('a@b.com: down');
    expect(c.visible).toBeTrue();
    expect(c.sending).toBeFalse();
  });

  it('toast helpers', () => {
    expect(emailSentToast({ sent: true, sentTo: ['a@b.com'], cc: ['c@d.com'] }).detail).toBe('Sent to a@b.com, c@d.com.');
    const partial = emailSentToast({ sent: false, sentTo: ['a@b.com'], failed: [{ email: 'x@y.com', error: 'full' }] });
    expect(partial.severity).toBe('warn');
    expect(partial.detail).toContain('Not sent to x@y.com: full');
    expect(describeFailures(null)).toBe('');
  });
});
