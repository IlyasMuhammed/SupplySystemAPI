import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ConfirmationService, MessageService } from 'primeng/api';
import { Observable, of, Subject, throwError } from 'rxjs';

import { AttachmentListComponent } from './attachment-list.component';
import { AttachmentService, AttachmentModel } from '../../services/attachment.service';
import { AttachmentAccessRule, AttachmentPolicyService } from '../../services/attachment-policy.service';
import { AuthService } from '../../pages/service/auth.service';

function attachment(overrides: Partial<AttachmentModel> = {}): AttachmentModel {
  return {
    uuid: 'a-1', interfaceCode: 'SALES_INVOICE', documentId: 'inv-1', fileName: 'SINV-20260920-0001.pdf',
    fileUrl: '/api/attachments/a-1/content', fileSize: 48_213, contentType: 'application/pdf',
    uploadedBy: 42, uploadedByName: 'Finance Officer', uploadedDate: '2026-09-20T10:30:00Z',
    ...overrides
  };
}

describe('AttachmentListComponent — opening a file', () => {
  let fixture: ComponentFixture<AttachmentListComponent>;
  let component: AttachmentListComponent;
  let service: jasmine.SpyObj<AttachmentService>;
  let messages: MessageService;

  const generated = attachment();
  const uploaded = attachment({
    uuid: 'a-2', fileName: 'scan.pdf', fileUrl: '/uploads/attachments/invoice/x.pdf', interfaceCode: 'INVOICE'
  });

  beforeEach(async () => {
    service = jasmine.createSpyObj<AttachmentService>(
      'AttachmentService', ['getAttachments', 'download', 'resolveUrl', 'isApiUrl', 'upload', 'deleteAttachment']);
    service.getAttachments.and.returnValue(of({ success: true, message: '', result: [generated, uploaded] } as any));
    service.resolveUrl.and.callFake((u: string) => `http://api.test${u}`);
    service.isApiUrl.and.callFake((u: string | undefined | null) => !!u && u.startsWith('/api/'));

    await TestBed.configureTestingModule({
      imports: [AttachmentListComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations(),
                  { provide: AttachmentService, useValue: service }]
    }).compileComponents();

    fixture = TestBed.createComponent(AttachmentListComponent);
    component = fixture.componentInstance;
    component.interfaceCode = 'SALES_INVOICE';
    component.documentId = 'inv-1';
    component.readOnly = true;
    component.ngOnChanges({ documentId: { currentValue: 'inv-1', previousValue: undefined, firstChange: true, isFirstChange: () => true } });
    fixture.detectChanges();

    // The component provides its own MessageService (standalone providers), so spy on that one.
    messages = fixture.debugElement.injector.get(MessageService);
    spyOn(messages, 'add');
  });

  it('renders a generated document with no href, so a middle-click cannot open an unauthorised page', () => {
    const links = Array.from<HTMLAnchorElement>(fixture.nativeElement.querySelectorAll('a.attachment-file-link'));
    const generatedLink = links.find(a => a.textContent!.includes('SINV-20260920-0001.pdf'))!;
    const uploadedLink = links.find(a => a.textContent!.includes('scan.pdf'))!;

    expect(generatedLink.hasAttribute('href')).toBeFalse();
    expect(generatedLink.getAttribute('tabindex')).toBe('0');
    expect(uploadedLink.getAttribute('href')).toBe('http://api.test/uploads/attachments/invoice/x.pdf');
  });

  it('fetches a generated document with the caller\'s token and opens it in a new tab', () => {
    const bytes = new Blob(['%PDF-1.7'], { type: 'application/pdf' });
    service.download.and.returnValue(of(bytes));
    const open = spyOn(window, 'open');
    spyOn(URL, 'createObjectURL').and.returnValue('blob:invoice');
    const revoke = spyOn(URL, 'revokeObjectURL');
    jasmine.clock().install();

    try {
      const event = new Event('click', { cancelable: true });
      component.open(generated, event);

      expect(service.download).toHaveBeenCalledWith('/api/attachments/a-1/content');
      expect(event.defaultPrevented).toBeTrue();
      expect(open).toHaveBeenCalledWith('blob:invoice', '_blank', 'noopener');

      expect(revoke).not.toHaveBeenCalled();
      jasmine.clock().tick(60_000);
      expect(revoke).toHaveBeenCalledWith('blob:invoice');
    } finally {
      jasmine.clock().uninstall();
    }
  });

  it('types the blob with what the attachment says it is, falling back to what the server sent', () => {
    service.download.and.returnValue(of(new Blob(['x'], { type: 'application/octet-stream' })));
    spyOn(window, 'open');
    const create = spyOn(URL, 'createObjectURL').and.returnValue('blob:x');

    component.open(generated, new Event('click', { cancelable: true }));
    expect((create.calls.mostRecent().args[0] as Blob).type).toBe('application/pdf');

    component.open(attachment({ contentType: undefined }), new Event('click', { cancelable: true }));
    expect((create.calls.mostRecent().args[0] as Blob).type).toBe('application/octet-stream');
  });

  it('leaves an uploaded file to the browser: no fetch, no prevented click', () => {
    const event = new Event('click', { cancelable: true });

    component.open(uploaded, event);

    expect(service.download).not.toHaveBeenCalled();
    expect(event.defaultPrevented).toBeFalse();
  });

  it('says so plainly when the caller lacks the permission the file was filed with', () => {
    service.download.and.returnValue(throwError(() => ({ status: 403 })));
    const open = spyOn(window, 'open');

    component.open(generated, new Event('click', { cancelable: true }));

    expect(open).not.toHaveBeenCalled();
    expect(messages.add).toHaveBeenCalledWith(jasmine.objectContaining({
      severity: 'error', detail: 'You do not have permission to open this document.'
    }));
  });

  it('says when the document has gone, and otherwise asks the user to try again', () => {
    service.download.and.returnValue(throwError(() => ({ status: 404 })));
    component.open(generated, new Event('click', { cancelable: true }));
    expect(messages.add).toHaveBeenCalledWith(jasmine.objectContaining({ detail: 'This document is no longer available.' }));

    service.download.and.returnValue(throwError(() => ({ status: 500 })));
    component.open(generated, new Event('click', { cancelable: true }));
    expect(messages.add).toHaveBeenCalledWith(jasmine.objectContaining({ detail: 'The document could not be opened. Try again.' }));
  });

  it('clicking the rendered link opens the document', () => {
    service.download.and.returnValue(of(new Blob(['%PDF-'], { type: 'application/pdf' })));
    spyOn(window, 'open');
    spyOn(URL, 'createObjectURL').and.returnValue('blob:clicked');

    const link = Array.from<HTMLAnchorElement>(fixture.nativeElement.querySelectorAll('a.attachment-file-link'))
      .find(a => a.textContent!.includes('SINV-20260920-0001.pdf'))!;
    link.click();

    expect(service.download).toHaveBeenCalledWith('/api/attachments/a-1/content');
    expect(window.open).toHaveBeenCalledWith('blob:clicked', '_blank', 'noopener');
  });

  it('pressing Enter on the focused link does the same', () => {
    service.download.and.returnValue(of(new Blob(['%PDF-'], { type: 'application/pdf' })));
    spyOn(window, 'open');
    spyOn(URL, 'createObjectURL').and.returnValue('blob:keyboard');

    const link = Array.from<HTMLAnchorElement>(fixture.nativeElement.querySelectorAll('a.attachment-file-link'))
      .find(a => a.textContent!.includes('SINV-20260920-0001.pdf'))!;
    link.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));

    expect(service.download).toHaveBeenCalled();
  });

  it('never opens a file as a page of this site: anything but a document or a picture is handed over as a download', () => {
    service.download.and.returnValue(of(new Blob(['<script>'], { type: 'text/html' })));
    spyOn(window, 'open');
    const create = spyOn(URL, 'createObjectURL').and.returnValue('blob:x');

    component.open(attachment({ contentType: 'text/html' }), new Event('click', { cancelable: true }));
    expect((create.calls.mostRecent().args[0] as Blob).type).toBe('application/octet-stream');

    component.open(attachment({ contentType: 'image/svg+xml' }), new Event('click', { cancelable: true }));
    expect((create.calls.mostRecent().args[0] as Blob).type).toBe('application/octet-stream');

    component.open(attachment({ contentType: undefined }), new Event('click', { cancelable: true }));
    expect((create.calls.mostRecent().args[0] as Blob).type).toBe('application/octet-stream', 'the server said text/html');

    component.open(attachment({ contentType: 'image/png' }), new Event('click', { cancelable: true }));
    expect((create.calls.mostRecent().args[0] as Blob).type).toBe('image/png');
  });
});

describe('AttachmentListComponent — who may add and remove files', () => {
  const PO_RULE: AttachmentAccessRule = {
    interfaceCode: 'PO',
    view: ['PO_VIEW', 'PO_CREATE', 'PO_EDIT', 'PO_APPROVE'],
    upload: ['PO_CREATE', 'PO_EDIT'],
    delete: ['PO_EDIT'],
    deleteOwn: ['PO_CREATE']
  };

  let fixture: ComponentFixture<AttachmentListComponent>;
  let component: AttachmentListComponent;
  let attachments: jasmine.SpyObj<AttachmentService>;
  let policy: { ruleFor: jasmine.Spy<(code: string) => Observable<AttachmentAccessRule | null>> };
  let permissions: string[];
  let uploaded: AttachmentModel;
  let filed: AttachmentModel;

  // The AuthService a page spec provides has hasPermission and nothing else; the component must make do with it.
  const auth = { hasPermission: (code: string) => permissions.includes(code) } as unknown as AuthService;

  /**
   * `canRemove` is what the server says about the uploaded file for this caller — it alone knows who
   * uploaded what — and it is ignored for the filed copy, which nobody may remove.
   */
  async function render(options: {
    permissions: string[];
    canRemove?: boolean;
    rule?: Observable<AttachmentAccessRule | null>;
    readOnly?: boolean;
    inline?: boolean;
  }) {
    permissions = options.permissions;
    uploaded = attachment({
      uuid: 'u-1', interfaceCode: 'PO', documentId: 'po-1', fileName: 'quote.pdf',
      fileUrl: '/uploads/attachments/po/u-1.pdf', isGenerated: false, canRemove: options.canRemove ?? true
    });
    filed = attachment({
      uuid: 'g-1', interfaceCode: 'PO', documentId: 'po-1', fileName: 'filed.pdf',
      fileUrl: '/api/attachments/g-1/content', isGenerated: true, canRemove: true
    });

    attachments = jasmine.createSpyObj<AttachmentService>(
      'AttachmentService', ['getAttachments', 'download', 'resolveUrl', 'isApiUrl', 'upload', 'deleteAttachment']);
    attachments.getAttachments.and.returnValue(of({ success: true, message: '', result: [uploaded, filed] } as any));
    attachments.resolveUrl.and.callFake((u: string) => `http://api.test${u}`);
    attachments.isApiUrl.and.callFake((u: string | undefined | null) => !!u && u.startsWith('/api/'));
    attachments.upload.and.returnValue(of({ success: true, message: '', result: 'new' } as any));
    attachments.deleteAttachment.and.returnValue(of({ success: true, message: '', result: null } as any));

    policy = { ruleFor: jasmine.createSpy('ruleFor').and.returnValue(options.rule ?? of(PO_RULE)) };

    await TestBed.configureTestingModule({
      imports: [AttachmentListComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations(),
                  { provide: AttachmentService, useValue: attachments },
                  { provide: AttachmentPolicyService, useValue: policy },
                  { provide: AuthService, useValue: auth }]
    }).compileComponents();

    fixture = TestBed.createComponent(AttachmentListComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('interfaceCode', 'PO');
    fixture.componentRef.setInput('documentId', 'po-1');
    if (options.readOnly !== undefined) fixture.componentRef.setInput('readOnly', options.readOnly);
    if (options.inline !== undefined) fixture.componentRef.setInput('inline', options.inline);
    fixture.detectChanges();
  }

  const el = () => fixture.nativeElement as HTMLElement;
  const uploadControl = () => el().querySelector('.attachment-upload, .attachment-chip--add');
  const fileInput = () => el().querySelector('input[type=file]');
  const removeControls = () => el().querySelectorAll('.attachment-remove, .attachment-chip-remove');
  const fileNames = () => Array.from(el().querySelectorAll('.attachment-file-name, .attachment-chip-name')).map(n => n.textContent!.trim());

  function removeControlFor(fileName: string): Element | null {
    const row = Array.from(el().querySelectorAll('.attachment-item, .attachment-chip:not(.attachment-chip--add)'))
      .find(r => r.textContent!.includes(fileName))!;
    return row.querySelector('.attachment-remove, .attachment-chip-remove');
  }

  it('asks the policy for the rule of its own kind of document', async () => {
    await render({ permissions: ['PO_VIEW'] });

    expect(policy.ruleFor).toHaveBeenCalledWith('PO');
  });

  it('offers a holder of the upload permission the upload control, and removal where the server says they may', async () => {
    await render({ permissions: ['PO_VIEW', 'PO_EDIT'] });

    expect(uploadControl()).not.toBeNull();
    expect(fileInput()).not.toBeNull();
    expect(removeControlFor('quote.pdf')).not.toBeNull();
  });

  it('leaves out "remove" on a file the server says this caller may not remove — somebody else\'s', async () => {
    await render({ permissions: ['PO_VIEW', 'PO_CREATE'], canRemove: false });

    expect(uploadControl()).not.toBeNull();
    expect(removeControls().length).toBe(0);
  });

  it('never offers to remove a document the system filed, whoever is looking', async () => {
    await render({ permissions: ['PO_VIEW', 'PO_CREATE', 'PO_EDIT'] });

    expect(removeControlFor('filed.pdf')).toBeNull();
    expect(removeControls().length).toBe(1);
  });

  it('shows a view-only caller every file, with no upload control and nothing to remove', async () => {
    await render({ permissions: ['PO_VIEW'], canRemove: false });

    expect(fileNames()).toEqual(['quote.pdf', 'filed.pdf']);
    expect(uploadControl()).toBeNull();
    expect(fileInput()).toBeNull();
    expect(removeControls().length).toBe(0);
  });

  it('a view-only caller gets no upload control even if a stale list claimed otherwise', async () => {
    await render({ permissions: ['PO_VIEW'], canRemove: true });

    expect(uploadControl()).toBeNull();
  });

  it('a page\'s [readOnly]="true" still wins over the caller\'s permissions', async () => {
    await render({ permissions: ['PO_VIEW', 'PO_CREATE', 'PO_EDIT'], readOnly: true });

    expect(uploadControl()).toBeNull();
    expect(removeControls().length).toBe(0);
  });

  it('stays read-only when the policy cannot be loaded', async () => {
    await render({ permissions: ['PO_VIEW', 'PO_CREATE', 'PO_EDIT'], rule: of(null) });

    expect(fileNames()).toEqual(['quote.pdf', 'filed.pdf']);
    expect(uploadControl()).toBeNull();
    expect(removeControls().length).toBe(0);
  });

  it('stays read-only until the policy arrives, then offers what the rule allows', async () => {
    const pending = new Subject<AttachmentAccessRule | null>();
    await render({ permissions: ['PO_VIEW', 'PO_EDIT'], rule: pending });

    expect(uploadControl()).toBeNull();
    expect(removeControls().length).toBe(0);

    pending.next(PO_RULE);
    pending.complete();
    fixture.detectChanges();

    expect(uploadControl()).not.toBeNull();
    expect(removeControls().length).toBe(1);
  });

  it('gates the single-line layout the same way', async () => {
    await render({ permissions: ['PO_VIEW'], canRemove: false, inline: true });
    expect(el().querySelectorAll('.attachment-chip-name').length).toBe(2);
    expect(uploadControl()).toBeNull();
    expect(removeControls().length).toBe(0);

    TestBed.resetTestingModule();
    await render({ permissions: ['PO_EDIT'], inline: true });
    expect(uploadControl()).not.toBeNull();
    expect(removeControlFor('quote.pdf')).not.toBeNull();
    expect(removeControlFor('filed.pdf')).toBeNull();
  });

  it('a rule that lets nobody upload — a sales invoice\'s filed copies — offers no upload control', async () => {
    await render({
      permissions: ['SALES_INVOICE_VIEW', 'SALES_INVOICE_MANAGE', 'PO_EDIT'],
      canRemove: false,
      rule: of({ interfaceCode: 'SALES_INVOICE', view: ['SALES_INVOICE_VIEW', 'SALES_INVOICE_MANAGE'], upload: [], delete: [], deleteOwn: [] })
    });

    expect(uploadControl()).toBeNull();
    expect(removeControls().length).toBe(0);
  });

  it('does nothing when asked to upload or remove without the right, whatever calls it', async () => {
    await render({ permissions: ['PO_VIEW'], canRemove: false });
    const confirm = spyOn(fixture.debugElement.injector.get(ConfirmationService), 'confirm');
    const file = new File(['%PDF-'], 'sneaky.pdf', { type: 'application/pdf' });

    component.onFileSelected({ target: { files: [file], value: '' } } as unknown as Event);
    component.remove(uploaded);

    expect(attachments.upload).not.toHaveBeenCalled();
    expect(confirm).not.toHaveBeenCalled();
    expect(attachments.deleteAttachment).not.toHaveBeenCalled();
  });

  it('never asks to remove a filed document, even for someone allowed to remove files', async () => {
    await render({ permissions: ['PO_EDIT'] });
    const confirm = spyOn(fixture.debugElement.injector.get(ConfirmationService), 'confirm');

    component.remove(filed);

    expect(confirm).not.toHaveBeenCalled();
  });

  it('uploads and removes for a caller who may', async () => {
    await render({ permissions: ['PO_EDIT'] });
    const confirm = spyOn(fixture.debugElement.injector.get(ConfirmationService), 'confirm')
      .and.callFake((c: any) => { c.accept(); return undefined as any; });
    const file = new File(['%PDF-'], 'quote-2.pdf', { type: 'application/pdf' });

    component.onFileSelected({ target: { files: [file], value: '' } } as unknown as Event);
    expect(attachments.upload).toHaveBeenCalledWith(file, 'PO', 'po-1', undefined);

    component.remove(uploaded);
    expect(confirm).toHaveBeenCalled();
    expect(attachments.deleteAttachment).toHaveBeenCalledWith('u-1');
  });

  it('puts the file name into the confirmation as text, not as markup', async () => {
    await render({ permissions: ['PO_EDIT'] });
    const confirm = spyOn(fixture.debugElement.injector.get(ConfirmationService), 'confirm');

    component.remove(attachment({ uuid: 'x', fileName: '<img src=x onerror=alert(1)>.pdf', isGenerated: false, canRemove: true }));

    const message = (confirm.calls.mostRecent().args[0] as { message: string }).message;
    expect(message).not.toContain('<img');
    expect(message).toContain('&lt;img src=x onerror=alert(1)&gt;.pdf');
  });
});
