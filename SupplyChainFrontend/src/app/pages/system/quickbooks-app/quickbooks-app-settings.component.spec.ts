import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { ConfirmationService, MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { QuickBooksAppSettingsComponent } from './quickbooks-app-settings.component';
import { PlatformAppKeysModel, QuickBooksIntegrationService } from '../../../services/quickbooks-integration.service';

function platform(overrides: Partial<PlatformAppKeysModel> = {}): PlatformAppKeysModel {
  return {
    isConfigured: true, source: 'Configuration', clientId: 'env-id', hasSecret: true, environment: 'Sandbox',
    redirectUri: 'https://api.example.com/api/integrations/quickbooks/callback', frontendReturnUrl: null,
    savedInDatabase: false, configurationAvailable: true, configurationClientId: 'env-id',
    connectionsUsingPlatformKeys: 2, organizationsWithOwnKeys: 1, ...overrides
  };
}

// docs/quickbooks/APP-CREDENTIALS.md D-4, D-7, D-8 — System Administration › QuickBooks App.
describe('QuickBooksAppSettingsComponent', () => {
  let fixture: ComponentFixture<QuickBooksAppSettingsComponent>;
  let component: QuickBooksAppSettingsComponent;
  let service: jasmine.SpyObj<QuickBooksIntegrationService>;

  const query = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  async function setup(model: PlatformAppKeysModel) {
    service = jasmine.createSpyObj<QuickBooksIntegrationService>('QuickBooksIntegrationService',
      ['getPlatformAppKeys', 'savePlatformAppKeys', 'deletePlatformAppKeys']);
    service.getPlatformAppKeys.and.returnValue(of(model));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [QuickBooksAppSettingsComponent],
      providers: [provideNoopAnimations(), provideRouter([]), { provide: QuickBooksIntegrationService, useValue: service }]
    }).compileComponents();

    fixture = TestBed.createComponent(QuickBooksAppSettingsComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  const confirmation = () => fixture.debugElement.injector.get(ConfirmationService);

  it('shows where the keys come from and how many organizations use them', async () => {
    await setup(platform());
    expect(query('qbo-app-status')!.textContent).toContain('Using the server configuration');
    expect(query('qbo-app-status')!.textContent).toContain('2 connected organization(s)');
    expect(component.clientId).toBe('env-id');
    expect(query('qbo-app-remove')).toBeNull();
  });

  it('needs a secret before the first save to the database, never pre-fills it, and keeps it when left blank afterwards', async () => {
    await setup(platform());
    expect((query('qbo-app-secret') as HTMLInputElement).value).toBe('');
    expect(component.canSave).toBeFalse();
    component.clientSecret = 'new-secret';
    expect(component.canSave).toBeTrue();

    await setup(platform({ source: 'Platform', savedInDatabase: true }));
    expect(component.canSave).toBeTrue();
    service.savePlatformAppKeys.and.returnValue(of(platform({ source: 'Platform', savedInDatabase: true })));
    component.save(false);
    expect(service.savePlatformAppKeys.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({
      clientId: 'env-id', clientSecret: null, environment: 'Sandbox', confirmBreakingChange: false
    }));
  });

  it('asks before stranding connected organizations, and resends with the confirmation', async () => {
    await setup(platform({ source: 'Platform', savedInDatabase: true }));
    const confirm = spyOn(confirmation(), 'confirm').and.callFake((c: any) => { c.accept(); return confirmation(); });
    service.savePlatformAppKeys.and.returnValues(
      throwError(() => ({ status: 409, error: { message: '2 organization(s) are connected … must reconnect.' } })),
      of(platform({ source: 'Platform', savedInDatabase: true, clientId: 'new-id' })));

    component.clientId = 'new-id';
    component.save(false);

    expect(confirm).toHaveBeenCalledWith(jasmine.objectContaining({ message: jasmine.stringContaining('must reconnect') }));
    expect(service.savePlatformAppKeys.calls.count()).toBe(2);
    expect(service.savePlatformAppKeys.calls.mostRecent().args[0].confirmBreakingChange).toBeTrue();
    expect(component.model!.clientId).toBe('new-id');
  });

  it('removes saved keys after a confirmation', async () => {
    await setup(platform({ source: 'Platform', savedInDatabase: true }));
    spyOn(confirmation(), 'confirm').and.callFake((c: any) => { c.accept(); return confirmation(); });
    service.deletePlatformAppKeys.and.returnValue(of(platform()));
    component.confirmRemove();
    expect(service.deletePlatformAppKeys).toHaveBeenCalledWith(false);
    expect(component.model!.savedInDatabase).toBeFalse();
  });

  it('says when the keys cannot be read', async () => {
    service = jasmine.createSpyObj<QuickBooksIntegrationService>('QuickBooksIntegrationService', ['getPlatformAppKeys']);
    service.getPlatformAppKeys.and.returnValue(throwError(() => ({ status: 403, error: { message: 'Super admin only.' } })));
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [QuickBooksAppSettingsComponent],
      providers: [provideNoopAnimations(), provideRouter([]), { provide: QuickBooksIntegrationService, useValue: service }]
    }).compileComponents();
    fixture = TestBed.createComponent(QuickBooksAppSettingsComponent);
    fixture.detectChanges();
    expect(query('qbo-app-error')!.textContent).toContain('Super admin only.');
  });
});
