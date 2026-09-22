import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { AttachmentService } from './attachment.service';
import { environment } from '../../environments/environment';

describe('AttachmentService', () => {
  let service: AttachmentService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(AttachmentService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  describe('isApiUrl', () => {
    it('is true for a document the API serves behind the caller\'s token', () => {
      expect(service.isApiUrl('/api/attachments/3f2a/content')).toBeTrue();
    });

    it('is false for an uploaded file, which is a public static url', () => {
      expect(service.isApiUrl('/uploads/attachments/invoice/x.pdf')).toBeFalse();
    });

    it('is false for an absolute url, a blank one and a missing one', () => {
      expect(service.isApiUrl('https://files.example.com/x.pdf')).toBeFalse();
      expect(service.isApiUrl('')).toBeFalse();
      expect(service.isApiUrl(undefined)).toBeFalse();
      expect(service.isApiUrl(null)).toBeFalse();
    });

    it('does not mistake a path that merely contains api for the API', () => {
      expect(service.isApiUrl('/uploads/api/x.pdf')).toBeFalse();
      expect(service.isApiUrl('/apiary/x.pdf')).toBeFalse();
    });
  });

  describe('download', () => {
    it('fetches the file as a blob from the API origin so the auth interceptor can add the token', () => {
      let received: Blob | undefined;
      service.download('/api/attachments/3f2a/content').subscribe(b => received = b);

      const req = http.expectOne(`${environment.apiOrigin}/api/attachments/3f2a/content`);
      expect(req.request.method).toBe('GET');
      expect(req.request.responseType).toBe('blob');

      const pdf = new Blob(['%PDF-1.7'], { type: 'application/pdf' });
      req.flush(pdf);
      expect(received).toBe(pdf);
    });

    it('surfaces a refusal to the caller rather than swallowing it', () => {
      let status: number | undefined;
      service.download('/api/attachments/3f2a/content').subscribe({ error: e => status = e.status });

      http.expectOne(`${environment.apiOrigin}/api/attachments/3f2a/content`)
          .flush(new Blob(), { status: 403, statusText: 'Forbidden' });

      expect(status).toBe(403);
    });
  });

  describe('resolveUrl', () => {
    it('puts a relative url on the API origin and leaves an absolute one alone', () => {
      expect(service.resolveUrl('/api/attachments/x/content')).toBe(`${environment.apiOrigin}/api/attachments/x/content`);
      expect(service.resolveUrl('https://files.example.com/x.pdf')).toBe('https://files.example.com/x.pdf');
      expect(service.resolveUrl('')).toBe('');
    });
  });
});
