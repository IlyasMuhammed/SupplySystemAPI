import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { QuickBooksIntegrationService } from './quickbooks-integration.service';
import { environment } from '../../environments/environment';

// docs/quickbooks/APP-CREDENTIALS.md — the two app-key endpoints.
describe('QuickBooksIntegrationService — app keys', () => {
  let service: QuickBooksIntegrationService;
  let http: HttpTestingController;
  const ok = (result: unknown = {}) => ({ success: true, message: '', result });

  beforeEach(() => {
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    service = TestBed.inject(QuickBooksIntegrationService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('reads, saves and removes the organization\'s own keys', () => {
    const base = `${environment.apiUrl}/integrations/quickbooks/app-keys`;
    service.getOwnAppKeys().subscribe();
    http.expectOne(r => r.method === 'GET' && r.url === base).flush(ok());

    service.saveOwnAppKeys({ clientId: 'id', clientSecret: null, environment: 'Sandbox' }).subscribe();
    const put = http.expectOne(r => r.method === 'PUT' && r.url === base);
    expect(put.request.body).toEqual({ clientId: 'id', clientSecret: null, environment: 'Sandbox' });
    put.flush(ok());

    service.deleteOwnAppKeys().subscribe();
    http.expectOne(r => r.method === 'DELETE' && r.url === base).flush(ok());
  });

  it('talks to the super admin endpoint for the platform keys, with the breaking-change confirmation on delete', () => {
    const base = `${environment.apiUrl}/system/integrations/quickbooks/app`;
    service.getPlatformAppKeys().subscribe();
    http.expectOne(r => r.method === 'GET' && r.url === base).flush(ok());

    service.savePlatformAppKeys({ clientId: 'id', environment: 'Production', redirectUri: 'https://x/cb', confirmBreakingChange: true }).subscribe();
    const put = http.expectOne(r => r.method === 'PUT' && r.url === base);
    expect(put.request.body.confirmBreakingChange).toBeTrue();
    put.flush(ok());

    service.deletePlatformAppKeys(true).subscribe();
    const del = http.expectOne(r => r.method === 'DELETE' && r.url === base);
    expect(del.request.params.get('confirmBreakingChange')).toBe('true');
    del.flush(ok());
  });

  it('surfaces { success: false } as an error', () => {
    let failed = '';
    service.getOwnAppKeys().subscribe({ error: e => failed = e.message });
    http.expectOne(`${environment.apiUrl}/integrations/quickbooks/app-keys`).flush({ success: false, message: 'Nope', result: null });
    expect(failed).toBe('Nope');
  });
});
