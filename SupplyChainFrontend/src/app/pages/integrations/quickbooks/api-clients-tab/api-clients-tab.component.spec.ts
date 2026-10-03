import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { signal } from '@angular/core';
import { ConfirmationService, MessageService } from 'primeng/api';
import { of } from 'rxjs';

import { ApiClientsTabComponent, keyStatus } from './api-clients-tab.component';
import { ApiClientModel, QuickBooksIntegrationService } from '../../../../services/quickbooks-integration.service';
import { TenantService } from '../../../service/tenant.service';

const SECRET = 'sqb_4f9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a';

function client(overrides: Partial<ApiClientModel> = {}): ApiClientModel {
  return {
    id: 'c1', name: 'Shop POS', scopes: ['customers:write', 'status:read'], isActive: true, createdAt: '2026-09-01T00:00:00Z',
    keys: [
      { id: 'k1', keyPrefix: 'sqb_4f9a8b7c', createdAt: '2026-09-01T00:00:00Z', lastUsedAt: '2026-09-29T08:00:00Z', isActive: true },
      { id: 'k0', keyPrefix: 'sqb_0000aaaa', createdAt: '2026-08-01T00:00:00Z', revokedAt: '2026-08-15T00:00:00Z', isActive: false }
    ],
    ...overrides
  };
}

describe('ApiClientsTabComponent', () => {
  let fixture: ComponentFixture<ApiClientsTabComponent>;
  let component: ApiClientsTabComponent;
  let service: jasmine.SpyObj<QuickBooksIntegrationService>;
  let toasts: jasmine.Spy;

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  function inDocument(testId: string): HTMLElement | null {
    return document.querySelector(`[data-testid="${testId}"]`);
  }

  async function setup(canManage = true) {
    service = jasmine.createSpyObj<QuickBooksIntegrationService>('QuickBooksIntegrationService', [
      'getApiClients', 'createApiClient', 'issueApiKey', 'revokeApiKey', 'deactivateApiClient'
    ]);
    service.getApiClients.and.returnValue(of([client()]));
    service.createApiClient.and.callFake(body => of(client({ id: 'c2', name: body.name, scopes: body.scopes, keys: [] })));
    service.issueApiKey.and.returnValue(of({
      keyId: 'k2', apiKey: SECRET, keyPrefix: 'sqb_4f9a8b7c', tenantId: '6f1c2d3e-0000-4000-8000-000000000001', expiresAt: null
    }));
    service.revokeApiKey.and.returnValue(of(client({ keys: [] })));
    service.deactivateApiClient.and.returnValue(of(client({ isActive: false })));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [ApiClientsTabComponent],
      providers: [
        provideNoopAnimations(), MessageService,
        { provide: QuickBooksIntegrationService, useValue: service },
        { provide: TenantService, useValue: { tenant: signal({ id: 'org-guid-1' }) } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(ApiClientsTabComponent);
    component = fixture.componentInstance;
    component.canManage = canManage;
    toasts = spyOn(TestBed.inject(MessageService), 'add');
    fixture.detectChanges();
  }

  afterEach(() => fixture?.destroy());

  it('lists clients with their scopes and keys (prefix, last used, status)', async () => {
    await setup();
    expect(service.getApiClients).toHaveBeenCalledTimes(1);
    const card = query('client-c1')!;
    expect(card.textContent).toContain('Shop POS');
    expect(card.textContent).toContain('customers:write');
    const keys = card.querySelectorAll('[data-testid="key-row"]');
    expect(keys.length).toBe(2);
    expect(keys[0].textContent).toContain('sqb_4f9a8b7c…');
    expect(keys[0].textContent).toContain('Active');
    expect(keys[1].textContent).toContain('Revoked');
    expect(query('revoke-k1')).not.toBeNull();
    expect(query('revoke-k0')).toBeNull();
  });

  it('shows example headers with this organization as X-Tenant-Id', async () => {
    await setup();
    const text = query('example-headers')!.textContent!;
    expect(text).toContain('X-Tenant-Id: org-guid-1');
    expect(text).toContain('X-Api-Key: <the key>');
    expect(text).toContain('/api/gateway/quickbooks/v1/customers/{your-customer-id}');
  });

  it('creates a client with a name and at least one scope, in the fixed scope order', async () => {
    await setup();
    component.openCreate();
    expect(component.createProblem).toBe('Give the client a name.');
    component.newName = '  Web shop ';
    expect(component.createProblem).toBe('Choose at least one thing it may do.');
    component.newScopes = ['status:read', 'invoices:write'];
    expect(component.createProblem).toBeNull();

    component.create();
    expect(service.createApiClient).toHaveBeenCalledOnceWith({ name: 'Web shop', scopes: ['invoices:write', 'status:read'] });
    expect(component.createVisible).toBeFalse();
    expect(component.clients.map(c => c.name)).toEqual(['Shop POS', 'Web shop']);
  });

  it('refuses a duplicate client name', async () => {
    await setup();
    component.openCreate();
    component.newName = 'shop pos';
    component.newScopes = ['customers:write'];
    expect(component.createProblem).toBe('A client with this name already exists.');
    component.create();
    expect(service.createApiClient).not.toHaveBeenCalled();
  });

  it('shows an issued key once, with the tenant id and a warning, and forgets it when the dialog closes', async () => {
    await setup();
    component.openIssue(component.clients[0]);
    component.issue();
    fixture.detectChanges();

    expect(service.issueApiKey).toHaveBeenCalledOnceWith('c1', { expiresAt: null });
    expect(component.keyVisible).toBeTrue();
    expect(inDocument('issued-key')!.textContent).toContain(SECRET);
    expect(inDocument('issued-tenant')!.textContent).toContain('6f1c2d3e-0000-4000-8000-000000000001');
    expect(inDocument('issued-headers')!.textContent).toContain(`X-Api-Key: ${SECRET}`);
    expect(inDocument('key-dialog')!.textContent).toContain('You will not see this key again');

    component.closeKey();
    fixture.detectChanges();
    expect(component.issuedKey).toBeNull();
    expect(component.keyVisible).toBeFalse();
    expect(document.body.textContent).not.toContain(SECRET);
  });

  it('also forgets the key when the dialog is dismissed another way', async () => {
    await setup();
    component.openIssue(component.clients[0]);
    component.issue();
    component.onKeyDialogVisibleChange(false);
    expect(component.issuedKey).toBeNull();
  });

  it('sends the chosen expiry as an ISO date', async () => {
    await setup();
    component.openIssue(component.clients[0]);
    component.issueExpiresAt = new Date(Date.UTC(2027, 0, 31, 0, 0, 0));
    component.issue();
    expect(service.issueApiKey).toHaveBeenCalledOnceWith('c1', { expiresAt: '2027-01-31T00:00:00.000Z' });
  });

  it('copies the key to the clipboard', async () => {
    await setup();
    const write = spyOn(navigator.clipboard, 'writeText').and.resolveTo();
    component.copy('key', SECRET);
    await fixture.whenStable();
    expect(write).toHaveBeenCalledOnceWith(SECRET);
    expect(component.copied['key']).toBeTrue();
  });

  it('asks before revoking a key or deactivating a client', async () => {
    await setup();
    const confirm = spyOn(fixture.debugElement.injector.get(ConfirmationService), 'confirm');

    component.confirmRevoke(component.clients[0], component.clients[0].keys[0]);
    expect(service.revokeApiKey).not.toHaveBeenCalled();
    confirm.calls.mostRecent().args[0].accept!();
    expect(service.revokeApiKey).toHaveBeenCalledOnceWith('c1', 'k1');

    component.confirmDeactivate(component.clients[0]);
    expect(service.deactivateApiClient).not.toHaveBeenCalled();
    confirm.calls.mostRecent().args[0].accept!();
    expect(service.deactivateApiClient).toHaveBeenCalledOnceWith('c1');
    expect(component.clients[0].isActive).toBeFalse();
  });

  it('shows and loads nothing without the manage permission', async () => {
    await setup(false);
    expect(service.getApiClients).not.toHaveBeenCalled();
    expect(query('no-permission')).not.toBeNull();
    expect(query('new-client')).toBeNull();
  });
});

describe('keyStatus', () => {
  it('reads revoked, expired, active and inactive', () => {
    const now = Date.UTC(2026, 8, 30);
    expect(keyStatus({ id: '', keyPrefix: '', createdAt: '', isActive: true, revokedAt: '2026-01-01' }, now)).toBe('Revoked');
    expect(keyStatus({ id: '', keyPrefix: '', createdAt: '', isActive: true, expiresAt: '2026-09-01T00:00:00Z' }, now)).toBe('Expired');
    expect(keyStatus({ id: '', keyPrefix: '', createdAt: '', isActive: true, expiresAt: '2027-01-01T00:00:00Z' }, now)).toBe('Active');
    expect(keyStatus({ id: '', keyPrefix: '', createdAt: '', isActive: false }, now)).toBe('Inactive');
  });
});
