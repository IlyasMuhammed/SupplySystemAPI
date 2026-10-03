import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { ConfirmationService, MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { ConnectionTabComponent } from './connection-tab.component';
import { ConnectionStatusModel, QuickBooksIntegrationService } from '../../../../services/quickbooks-integration.service';

function connection(overrides: Partial<ConnectionStatusModel> = {}): ConnectionStatusModel {
  return {
    appConfigured: true, status: 'NeedsSetup', isConnected: true, companyName: 'Acme Traders', realmId: '9130357',
    environment: 'Sandbox', homeCurrencyCode: 'PKR', multiCurrencyEnabled: false, country: 'PK',
    connectedAt: '2026-09-29T10:00:00Z', refreshTokenExpiresAt: '2026-10-20T12:00:00Z', reconnectSoon: false, mode: 'DryRun',
    ...overrides
  };
}

describe('ConnectionTabComponent', () => {
  let fixture: ComponentFixture<ConnectionTabComponent>;
  let component: ConnectionTabComponent;
  let service: jasmine.SpyObj<QuickBooksIntegrationService>;
  let toasts: jasmine.Spy;
  let navigate: jasmine.Spy;
  let redirect: jasmine.Spy;

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  async function setup(opts: { connection?: ConnectionStatusModel | null; canManage?: boolean; query?: Record<string, string> } = {}) {
    service = jasmine.createSpyObj<QuickBooksIntegrationService>('QuickBooksIntegrationService', ['connect', 'testConnection', 'disconnect']);
    service.connect.and.returnValue(of({ consentUrl: 'https://appcenter.intuit.com/connect/oauth2?x=1' }));
    service.testConnection.and.returnValue(of({ ok: true, companyName: 'Acme Traders' }));
    service.disconnect.and.returnValue(of(connection({ status: 'NotConnected', isConnected: false, companyName: null })));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [ConnectionTabComponent],
      providers: [
        provideNoopAnimations(), provideRouter([]), MessageService,
        { provide: QuickBooksIntegrationService, useValue: service },
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(opts.query ?? {}) } } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(ConnectionTabComponent);
    component = fixture.componentInstance;
    component.connection = opts.connection === undefined ? connection() : opts.connection;
    component.canManage = opts.canManage ?? true;
    toasts = spyOn(TestBed.inject(MessageService), 'add');
    navigate = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    redirect = spyOn(component, 'redirectTo');
    fixture.detectChanges();
  }

  function confirmation(): ConfirmationService {
    return fixture.debugElement.injector.get(ConfirmationService);
  }

  // ── Back from Intuit ───────────────────────────────────────────────────────

  it('says the connection worked when Intuit returns ?result=connected, then clears the address', async () => {
    await setup({ query: { result: 'connected' } });

    expect(toasts).toHaveBeenCalledOnceWith(jasmine.objectContaining({ severity: 'success', summary: 'Connected to QuickBooks' }));
    expect(navigate).toHaveBeenCalledOnceWith([], jasmine.objectContaining({
      queryParams: { result: null, reason: null }, queryParamsHandling: 'merge', replaceUrl: true
    }));
  });

  const reasons: [string, string][] = [
    ['state_invalid', 'could not be matched'],
    ['state_expired', 'expired'],
    ['state_used', 'already been used'],
    ['access_denied', 'not granted'],
    ['realm_mismatch', 'different QuickBooks company'],
    ['exchange_failed', 'did not complete the sign-in'],
    ['not_configured', 'no Intuit app keys']
  ];
  for (const [reason, words] of reasons) {
    it(`explains ?result=error&reason=${reason} in words, then clears the address`, async () => {
      await setup({ query: { result: 'error', reason } });
      const toast = toasts.calls.mostRecent().args[0];
      expect(toast.severity).toBe('error');
      expect(toast.detail).toContain(words);
      expect(navigate).toHaveBeenCalledTimes(1);
    });
  }

  it('has a fallback for an unknown reason', async () => {
    await setup({ query: { result: 'error', reason: 'mystery' } });
    expect(toasts.calls.mostRecent().args[0].detail).toContain('did not complete');
  });

  it('does nothing without ?result', async () => {
    await setup();
    expect(toasts).not.toHaveBeenCalled();
    expect(navigate).not.toHaveBeenCalled();
  });

  // ── Not connected ──────────────────────────────────────────────────────────

  it('offers Connect when not connected, and sends the browser to the consent address', async () => {
    await setup({ connection: connection({ status: 'NotConnected', isConnected: false, companyName: null }) });
    expect(query('not-connected')).not.toBeNull();
    expect(query('connected-card')).toBeNull();

    const button = query('connect') as HTMLButtonElement;
    expect(button.disabled).toBeFalse();
    button.click();

    expect(service.connect).toHaveBeenCalledTimes(1);
    expect(redirect).toHaveBeenCalledOnceWith('https://appcenter.intuit.com/connect/oauth2?x=1');
  });

  it('disables Connect, and says why, when the server has no Intuit app keys', async () => {
    await setup({ connection: connection({ status: 'NotConnected', isConnected: false, appConfigured: false }) });
    expect((query('connect') as HTMLButtonElement).disabled).toBeTrue();
    expect(query('not-configured')!.textContent).toContain('no Intuit app keys');
    component.connect();
    expect(service.connect).not.toHaveBeenCalled();
  });

  it('disables Connect without the manage permission', async () => {
    await setup({ connection: connection({ status: 'NotConnected', isConnected: false }), canManage: false });
    expect((query('connect') as HTMLButtonElement).disabled).toBeTrue();
    expect(query('no-manage')).not.toBeNull();
  });

  it('reports a refused connect and does not navigate', async () => {
    await setup({ connection: connection({ status: 'NotConnected', isConnected: false }) });
    service.connect.and.returnValue(throwError(() => ({ status: 409, error: { success: false, message: 'Already connected.' } })));
    component.connect();
    expect(redirect).not.toHaveBeenCalled();
    expect(toasts.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({ severity: 'error', detail: 'Already connected.' }));
    expect(component.isConnecting).toBeFalse();
  });

  // ── Connected ──────────────────────────────────────────────────────────────

  it('shows the company, realm, environment, currency and mode', async () => {
    await setup({ connection: connection({ environment: 'Production', mode: 'Live' }) });
    expect(query('company-name')!.textContent).toContain('Acme Traders');
    expect(query('realm-id')!.textContent).toContain('9130357');
    expect(query('environment-tag')!.textContent).toContain('Production');
    expect(query('home-currency')!.textContent).toContain('PKR');
    expect(query('mode-tag')!.textContent).toContain('Live');
  });

  it('warns to reconnect before the sign-in runs out', async () => {
    await setup({ connection: connection({ reconnectSoon: true }) });
    const banner = query('reconnect-soon-banner')!;
    expect(banner.textContent).toContain('Reconnect before 20 Oct 2026');
    expect(query('reconnect-banner')).toBeNull();
  });

  it('shows a red banner with Reconnect for a revoked or expired connection', async () => {
    await setup({ connection: connection({ status: 'Revoked', isConnected: false, lastError: 'invalid_grant' }) });
    expect(query('reconnect-banner')!.textContent).toContain('revoked');
    expect(query('reconnect-banner')!.textContent).toContain('invalid_grant');
    expect(query('not-connected')).toBeNull();

    (query('reconnect')!.querySelector('button') as HTMLButtonElement).click();
    expect(service.connect).toHaveBeenCalledTimes(1);
    expect(redirect).toHaveBeenCalled();

    await setup({ connection: connection({ status: 'Expired', isConnected: false }) });
    expect(query('reconnect-banner')!.textContent).toContain('expired');
  });

  it('tests the connection and shows the answer', async () => {
    await setup();
    component.test();
    fixture.detectChanges();
    expect(service.testConnection).toHaveBeenCalledTimes(1);
    expect(query('test-result')!.textContent).toContain('Acme Traders');
    expect(toasts.calls.mostRecent().args[0].severity).toBe('success');

    service.testConnection.and.returnValue(of({ ok: false, message: 'Token refused.' }));
    component.test();
    fixture.detectChanges();
    expect(query('test-result')!.textContent).toContain('Token refused.');
    expect(toasts.calls.mostRecent().args[0].severity).toBe('error');
  });

  // ── Disconnect ─────────────────────────────────────────────────────────────

  it('asks before disconnecting, saying QuickBooks data is unchanged and links are kept', async () => {
    await setup();
    const confirm = spyOn(confirmation(), 'confirm');
    (query('disconnect')!.querySelector('button') as HTMLButtonElement).click();

    expect(confirm).toHaveBeenCalledTimes(1);
    const options = confirm.calls.mostRecent().args[0];
    expect(options.message).toContain('Your QuickBooks data is not changed; links are kept so reconnecting the same company re-links');
    expect(options.key).toBe('qbo-disconnect');
    expect(service.disconnect).not.toHaveBeenCalled();
  });

  it('disconnects only when confirmed, and reports the new status', async () => {
    await setup();
    const emitted: ConnectionStatusModel[] = [];
    component.connectionChange.subscribe(c => emitted.push(c));
    const confirm = spyOn(confirmation(), 'confirm');

    component.confirmDisconnect();
    confirm.calls.mostRecent().args[0].reject?.();
    expect(service.disconnect).not.toHaveBeenCalled();

    component.confirmDisconnect();
    confirm.calls.mostRecent().args[0].accept!();
    expect(service.disconnect).toHaveBeenCalledTimes(1);
    expect(emitted.length).toBe(1);
    expect(emitted[0].status).toBe('NotConnected');
  });

  it('offers no Disconnect without the manage permission', async () => {
    await setup({ canManage: false });
    expect(query('disconnect')).toBeNull();
    const confirm = spyOn(confirmation(), 'confirm');
    component.confirmDisconnect();
    expect(confirm).not.toHaveBeenCalled();
  });
});
