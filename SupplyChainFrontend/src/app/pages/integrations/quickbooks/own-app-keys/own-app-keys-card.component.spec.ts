import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { OwnAppKeysCardComponent } from './own-app-keys-card.component';
import { QuickBooksIntegrationService, TenantAppKeysModel } from '../../../../services/quickbooks-integration.service';

function keys(overrides: Partial<TenantAppKeysModel> = {}): TenantAppKeysModel {
  return {
    featureEnabled: true, hasOwnKeys: false, hasSecret: false, environment: 'Sandbox', connectedWithOwnKeys: false,
    redirectUriToRegister: 'https://api.example.com/api/integrations/quickbooks/callback', newConnectionsUse: 'Platform',
    ...overrides
  };
}

// docs/quickbooks/APP-CREDENTIALS.md D-4, D-7, D-8.
describe('OwnAppKeysCardComponent', () => {
  let fixture: ComponentFixture<OwnAppKeysCardComponent>;
  let component: OwnAppKeysCardComponent;
  let service: jasmine.SpyObj<QuickBooksIntegrationService>;
  let toasts: jasmine.Spy;

  const query = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  async function setup(model: TenantAppKeysModel | 'fail', canManage = true) {
    service = jasmine.createSpyObj<QuickBooksIntegrationService>('QuickBooksIntegrationService',
      ['getOwnAppKeys', 'saveOwnAppKeys', 'deleteOwnAppKeys']);
    service.getOwnAppKeys.and.returnValue(model === 'fail' ? throwError(() => ({ status: 500 })) : of(model));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [OwnAppKeysCardComponent],
      providers: [provideNoopAnimations(), MessageService, { provide: QuickBooksIntegrationService, useValue: service }]
    }).compileComponents();

    fixture = TestBed.createComponent(OwnAppKeysCardComponent);
    component = fixture.componentInstance;
    component.canManage = canManage;
    toasts = spyOn(TestBed.inject(MessageService), 'add');
    fixture.detectChanges();
  }

  it('stays hidden when the feature is off and nothing is saved, or the read fails', async () => {
    await setup(keys({ featureEnabled: false }));
    expect(query('own-app-card')).toBeNull();
    await setup('fail');
    expect(query('own-app-card')).toBeNull();
  });

  it('shows the redirect URI to register and needs a secret the first time', async () => {
    await setup(keys());
    expect(query('redirect-uri')!.textContent).toContain('/api/integrations/quickbooks/callback');
    component.clientId = 'own-id';
    expect(component.canSave).toBeFalse();
    component.clientSecret = 's3cret';
    expect(component.canSave).toBeTrue();
    component.clientId = 'has space';
    expect(component.canSave).toBeFalse();
  });

  it('saves trimmed keys, sends no secret when left blank, and never shows the stored one', async () => {
    await setup(keys({ hasOwnKeys: true, hasSecret: true, clientId: 'own-id' }));
    expect((query('own-client-secret') as HTMLInputElement).value).toBe('');
    expect((query('own-client-secret') as HTMLInputElement).type).toBe('password');

    const saved = keys({ hasOwnKeys: true, hasSecret: true, clientId: 'own-id', environment: 'Production', newConnectionsUse: 'Tenant' });
    service.saveOwnAppKeys.and.returnValue(of(saved));
    const changed: TenantAppKeysModel[] = [];
    component.keysChanged.subscribe(m => changed.push(m));

    component.clientId = '  own-id  ';
    component.environment = 'Production';
    component.save();

    expect(service.saveOwnAppKeys).toHaveBeenCalledWith({ clientId: 'own-id', clientSecret: null, environment: 'Production' });
    expect(changed.length).toBe(1);
    expect(component.clientSecret).toBe('');
    fixture.detectChanges();
    expect(query('own-app-in-use')).not.toBeNull();
  });

  it('says why a save was refused', async () => {
    await setup(keys({ hasOwnKeys: true, hasSecret: true, clientId: 'own-id' }));
    service.saveOwnAppKeys.and.returnValue(throwError(() => ({ status: 409, error: { message: 'Disconnect QuickBooks first.' } })));
    component.save();
    expect(toasts).toHaveBeenCalledWith(jasmine.objectContaining({ severity: 'error', detail: 'Disconnect QuickBooks first.' }));
  });

  it('locks the Client ID and removal while connected through these keys', async () => {
    await setup(keys({ hasOwnKeys: true, hasSecret: true, clientId: 'own-id', connectedWithOwnKeys: true }));
    expect(query('own-app-locked')).not.toBeNull();
    expect((query('own-client-id') as HTMLInputElement).disabled).toBeTrue();
    expect((query('own-client-secret') as HTMLInputElement).disabled).toBeFalse();
    component.remove();
    expect(service.deleteOwnAppKeys).not.toHaveBeenCalled();
  });

  it('is read-only without the manage permission', async () => {
    await setup(keys({ hasOwnKeys: true, hasSecret: true, clientId: 'own-id' }), false);
    expect(query('own-app-save')).toBeNull();
    expect(query('own-app-remove')).toBeNull();
    expect(component.canSave).toBeFalse();
  });

  it('removes the keys', async () => {
    await setup(keys({ hasOwnKeys: true, hasSecret: true, clientId: 'own-id' }));
    service.deleteOwnAppKeys.and.returnValue(of(keys()));
    component.remove();
    expect(service.deleteOwnAppKeys).toHaveBeenCalled();
    expect(component.model!.hasOwnKeys).toBeFalse();
  });
});
