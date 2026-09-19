import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter, ActivatedRoute } from '@angular/router';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { CarrierAccountsComponent } from './carrier-accounts.component';
import {
  LogisticsService,
  CarrierAccountModel,
  CarrierCredentialModel,
  CarrierDetailModel
} from '../../../../services/logistics.service';

const CARRIER = '11111111-1111-1111-1111-111111111111';

function carrier(overrides: Partial<CarrierDetailModel> = {}): CarrierDetailModel {
  return {
    uuid: CARRIER, name: 'Simcourier', code: 'SIM',
    status: 'Active', isActive: true,
    createdDate: '2026-09-01T00:00:00Z',
    ...overrides
  };
}

function capability(name: string, provider: boolean, account: boolean | null) {
  return {
    name,
    supportedByProvider: provider,
    enabledOnAccount: account,
    effective: provider && (account ?? true)
  };
}

function account(overrides: Partial<CarrierAccountModel> = {}): CarrierAccountModel {
  return {
    uuid: 'acc-1',
    carrierUuid: CARRIER,
    carrierName: 'Simcourier',
    accountName: 'Domestic',
    isDefault: true,
    isSandbox: false,
    isActive: true,
    providerKey: 'SIMULATOR',
    providerDisplayName: 'Simulator',
    capabilities: [
      capability('COD', true, null),
      capability('LABELS', true, false),
      capability('TRACKING', true, null),
      capability('CANCELLATION', true, null),
      capability('PICKUP_BOOKING', false, null)
    ],
    createdDate: '2026-09-01T00:00:00Z',
    ...overrides
  };
}

function credential(overrides: Partial<CarrierCredentialModel> = {}): CarrierCredentialModel {
  return {
    uuid: 'cred-1', key: 'ApiKey', isExpired: false,
    setAt: '2026-09-01T00:00:00Z', setBy: 1,
    ...overrides
  };
}

function ok<T>(result: T) {
  return of({ success: true, message: '', result } as any);
}

describe('CarrierAccountsComponent', () => {
  let fixture: ComponentFixture<CarrierAccountsComponent>;
  let component: CarrierAccountsComponent;
  let service: jasmine.SpyObj<LogisticsService>;

  async function setup(
    accounts: CarrierAccountModel[] = [account()],
    detail: CarrierDetailModel | null = carrier()) {
    service = jasmine.createSpyObj<LogisticsService>('LogisticsService', [
      'getCarrierById', 'getCarrierAccounts', 'createCarrierAccount', 'patchCarrierAccount',
      'deleteCarrierAccount', 'getCarrierCredentials', 'setCarrierCredential',
      'removeCarrierCredential'
    ]);

    service.getCarrierById.and.returnValue(
      detail ? ok(detail) : of({ success: false, message: 'not found', result: null } as any));
    service.getCarrierAccounts.and.returnValue(ok(accounts));
    service.createCarrierAccount.and.returnValue(ok('new-account'));
    service.patchCarrierAccount.and.returnValue(of({ success: true, message: '' } as any));
    service.deleteCarrierAccount.and.returnValue(of({ success: true, message: '' } as any));
    service.getCarrierCredentials.and.returnValue(ok([credential()]));
    service.setCarrierCredential.and.returnValue(ok('cred-1'));
    service.removeCarrierCredential.and.returnValue(of({ success: true, message: '' } as any));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [CarrierAccountsComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideRouter([]),
        provideNoopAnimations(), MessageService,
        { provide: LogisticsService, useValue: service },
        { provide: ActivatedRoute, useValue: { snapshot: { paramMap: new Map([['uuid', CARRIER]]) } } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(CarrierAccountsComponent);
    component = fixture.componentInstance;
  }

  // ── Loading ───────────────────────────────────────────────────────────────

  it('loads the carrier and its accounts once', async () => {
    await setup();
    fixture.detectChanges();

    expect(service.getCarrierById).toHaveBeenCalledOnceWith(CARRIER);
    expect(service.getCarrierAccounts).toHaveBeenCalledOnceWith(CARRIER);
    expect(component.accounts.length).toBe(1);
    expect(component.isLoading).toBeFalse();
  });

  it('shows not found for a 404 but not for a failed request', async () => {
    await setup();
    service.getCarrierById.and.returnValue(throwError(() => ({ status: 404 })));
    fixture.detectChanges();
    expect(component.notFound).toBeTrue();

    await setup();
    service.getCarrierById.and.returnValue(throwError(() => ({ status: 500 })));
    fixture.detectChanges();
    expect(component.notFound).toBeFalse();
  });

  it('names the adapter from the accounts, not from the carrier row', async () => {
    // CarrierDetailModel deliberately does not expose integrationMode or providerKey — a backend
    // contract test guards that, so Phase 2 columns never leak into the response the legacy
    // carrier screens read. The account-level answer is better anyway: the server resolved it
    // through the same logic the booking flow uses.
    await setup();
    fixture.detectChanges();

    expect(component.providerSummary).toBe('Simulator');
  });

  it('names no adapter when there are no accounts, or none resolved one', async () => {
    await setup([]);
    fixture.detectChanges();
    expect(component.providerSummary).toBeNull();

    await setup([account({ providerDisplayName: undefined, providerWarning: 'No adapter for DHL.' })]);
    fixture.detectChanges();
    expect(component.providerSummary).toBeNull();
  });

  // ── Capabilities ──────────────────────────────────────────────────────────

  it('distinguishes what the adapter cannot do from what somebody switched off', async () => {
    // The distinction the screen exists to make: one is a fact about the carrier, the other is
    // a decision — and they need different fixes.
    await setup();
    fixture.detectChanges();

    const caps = component.accounts[0].capabilities;

    expect(component.capabilityReason(caps.find(c => c.name === 'PICKUP_BOOKING')!))
      .toBe('The adapter cannot do this');
    expect(component.capabilityReason(caps.find(c => c.name === 'LABELS')!))
      .toBe('Switched off for this account');
    expect(component.capabilityReason(caps.find(c => c.name === 'COD')!))
      .toBe('Available');
  });

  it('labels capabilities for humans', async () => {
    await setup();
    expect(component.capabilityLabel('PICKUP_BOOKING')).toBe('Pickup booking');
    expect(component.capabilityLabel('UNKNOWN')).toBe('UNKNOWN');
  });

  it('surfaces a missing adapter across the whole carrier', async () => {
    await setup([account({ providerKey: undefined, providerWarning: 'No courier provider is registered for DHL.' })]);
    fixture.detectChanges();

    expect(component.hasBrokenProvider).toBeTrue();
  });

  // ── Editing an account ────────────────────────────────────────────────────

  it('seeds the edit form from the account, including its overrides', async () => {
    await setup();
    fixture.detectChanges();

    component.openEdit(component.accounts[0]);

    expect(component.editingUuid).toBe('acc-1');
    expect(component.form.accountName).toBe('Domestic');
    expect(component.form.overrides['LABELS']).toBeFalse();
    expect(component.form.overrides['COD']).toBeNull();
  });

  it('will not save an account without a name', async () => {
    await setup();
    fixture.detectChanges();

    component.openCreate();
    component.form.accountName = '   ';

    expect(component.canSaveAccount).toBeFalse();
  });

  it('creates an account with only the switched-off capabilities sent', async () => {
    await setup();
    fixture.detectChanges();

    component.openCreate();
    component.form.accountName = '  International  ';
    component.form.overrides['COD'] = false;

    component.saveAccount();

    const req = service.createCarrierAccount.calls.mostRecent().args[0];
    expect(req.carrierUuid).toBe(CARRIER);
    expect(req.accountName).toBe('International');
    expect(req.codEnabled).toBeFalse();
    // Everything not switched off is left to the adapter rather than asserted as "on".
    expect(req.labelsEnabled).toBeUndefined();
  });

  it('clears overrides it is no longer setting, because null means leave alone', async () => {
    // The whole reason clearOverrides exists on the patch contract.
    await setup();
    fixture.detectChanges();

    component.openEdit(component.accounts[0]);
    component.form.overrides['LABELS'] = null;   // hand it back to the adapter

    component.saveAccount();

    const req = service.patchCarrierAccount.calls.mostRecent().args[1];
    expect(req.clearOverrides).toContain('LABELS');
    expect(req.labelsEnabled).toBeUndefined();
  });

  it('reloads after saving rather than patching its own state', async () => {
    await setup();
    fixture.detectChanges();

    component.openCreate();
    component.form.accountName = 'Spare';
    component.saveAccount();

    expect(service.getCarrierAccounts).toHaveBeenCalledTimes(2);
    expect(component.accountDialogVisible).toBeFalse();
  });

  it('surfaces the servers explanation when a change is refused', async () => {
    await setup();
    fixture.detectChanges();
    const messages = fixture.debugElement.injector.get(MessageService);
    spyOn(messages, 'add');

    service.patchCarrierAccount.and.returnValue(throwError(() => ({
      error: { message: "'Domestic' is this carrier's default account." }
    })));

    component.setActive(component.accounts[0], false);

    expect(messages.add).toHaveBeenCalledWith(jasmine.objectContaining({
      severity: 'error', detail: "'Domestic' is this carrier's default account."
    }));
    expect(component.isSubmitting).toBeFalse();
  });

  it('does not re-send a default that is already the default', async () => {
    await setup();
    fixture.detectChanges();

    component.makeDefault(component.accounts[0]);

    expect(service.patchCarrierAccount).not.toHaveBeenCalled();
  });

  // ── Credentials ───────────────────────────────────────────────────────────

  it('loads credentials only when the panel is opened', async () => {
    await setup();
    fixture.detectChanges();

    expect(service.getCarrierCredentials).not.toHaveBeenCalled();

    component.toggleCredentials(component.accounts[0]);

    expect(service.getCarrierCredentials).toHaveBeenCalledWith('acc-1');
    expect(component.credentialsFor(component.accounts[0]).length).toBe(1);
  });

  it('will not save a credential without both a key and a value', async () => {
    await setup();
    fixture.detectChanges();

    component.openCredential(component.accounts[0]);
    expect(component.canSaveCredential).toBeFalse();

    component.credentialForm.key = 'ApiKey';
    expect(component.canSaveCredential).toBeFalse();

    component.credentialForm.value = 'secret';
    expect(component.canSaveCredential).toBeTrue();
  });

  it('opens blank when replacing, because the value cannot be read back', async () => {
    await setup();
    fixture.detectChanges();

    component.openCredential(component.accounts[0], 'ApiKey');

    expect(component.credentialForm.key).toBe('ApiKey');
    expect(component.credentialForm.value).toBe('');
  });

  it('sends the value untrimmed but trims the key and note', async () => {
    // A secret may legitimately begin or end with whitespace; a key or a note may not.
    await setup();
    fixture.detectChanges();

    component.openCredential(component.accounts[0]);
    component.credentialForm.key = '  ApiKey  ';
    component.credentialForm.value = ' secret-with-spaces ';
    component.credentialForm.description = '  from the account manager  ';

    component.saveCredential();

    const [uuid, req] = service.setCarrierCredential.calls.mostRecent().args;
    expect(uuid).toBe('acc-1');
    expect(req.key).toBe('ApiKey');
    expect(req.value).toBe(' secret-with-spaces ');
    expect(req.description).toBe('from the account manager');
  });

  it('clears the secret from memory as soon as it is saved', async () => {
    // It cannot be read back, so the form has no reason to keep holding it.
    await setup();
    fixture.detectChanges();

    component.openCredential(component.accounts[0]);
    component.credentialForm.key = 'ApiKey';
    component.credentialForm.value = 'secret';
    component.saveCredential();

    expect(component.credentialForm.value).toBe('');
    expect(component.credentialDialogVisible).toBeFalse();
  });

  it('refetches credentials after saving one', async () => {
    await setup();
    fixture.detectChanges();
    component.toggleCredentials(component.accounts[0]);

    component.openCredential(component.accounts[0]);
    component.credentialForm.key = 'ApiKey';
    component.credentialForm.value = 'secret';
    component.saveCredential();

    expect(service.getCarrierCredentials).toHaveBeenCalledTimes(2);
  });

  it('removes a credential by its key', async () => {
    await setup();
    fixture.detectChanges();
    component.toggleCredentials(component.accounts[0]);

    component.removeCredential(component.accounts[0], credential());

    expect(service.removeCarrierCredential).toHaveBeenCalledWith('acc-1', 'ApiKey');
  });

  it('flags an expired credential, because every booking fails until it is replaced', async () => {
    await setup();
    service.getCarrierCredentials.and.returnValue(ok([credential({ isExpired: true })]));
    fixture.detectChanges();

    component.toggleCredentials(component.accounts[0]);

    expect(component.hasExpiredCredential(component.accounts[0])).toBeTrue();
  });

  it('drops cached credentials when the accounts reload', async () => {
    // Showing keys cached against a changed account is worse than refetching.
    await setup();
    fixture.detectChanges();
    component.toggleCredentials(component.accounts[0]);
    expect(component.credentialsFor(component.accounts[0]).length).toBe(1);

    component.load();

    expect(component.credentialsFor(component.accounts[0]).length).toBe(0);
  });

  // ── The service surface it relies on ──────────────────────────────────────

  it('has no way to read a stored credential value', async () => {
    // Enforced server-side too — there is no endpoint behind one. Asserted here so a future
    // "just show it masked" convenience has to delete this test on the way in.
    await setup();

    const readers = Object.keys(service).filter(k => /credential/i.test(k) && /get|read|reveal/i.test(k));

    expect(readers).toEqual(['getCarrierCredentials']);
    expect(component.credentialsFor(account())).toEqual([]);
  });
});
