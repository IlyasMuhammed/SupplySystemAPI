import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { AttachmentListComponent } from './attachment-list.component';
import { AttachmentService, AttachmentModel } from '../../services/attachment.service';

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
});
