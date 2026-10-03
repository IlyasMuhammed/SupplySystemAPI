import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';

import { QuickBooksIntegrationComponent } from './quickbooks-integration.component';
import {
  ConnectionStatusModel, IntegrationSettingsModel, PreflightResultModel, QuickBooksIntegrationService, SyncSummaryModel
} from '../../../services/quickbooks-integration.service';
import { PaymentTermsService } from '../../../services/payment-terms.service';
import { AuthService } from '../../service/auth.service';

function connection(overrides: Partial<ConnectionStatusModel> = {}): ConnectionStatusModel {
  return {
    appConfigured: true, status: 'NeedsSetup', isConnected: true, companyName: 'Acme Traders', environment: 'Sandbox',
    reconnectSoon: false, mode: 'DryRun', ...overrides
  };
}

function settings(overrides: Partial<IntegrationSettingsModel> = {}): IntegrationSettingsModel {
  return {
    mode: 'DryRun', autoPushCustomers: true, autoPushVendors: true, autoPushItems: true, autoPushSalesInvoices: true,
    autoPushBills: true, itemTypeDefault: 'NonInventory', partnerScope: 'OnlyWhenReferenced', ...overrides
  };
}

const PASSED: PreflightResultModel = { passed: true, canGoLive: false, checks: [{ code: 'COUNTRY', title: 'Country', status: 'Pass', message: 'PK' }] };
const EMPTY_SUMMARY: SyncSummaryModel = { connectionStatus: 'NeedsSetup', mode: 'DryRun', queueDepth: 0, kinds: [] };

describe('QuickBooksIntegrationComponent', () => {
  let fixture: ComponentFixture<QuickBooksIntegrationComponent>;
  let component: QuickBooksIntegrationComponent;
  let service: jasmine.SpyObj<QuickBooksIntegrationService>;
  let permissions: string[];

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  async function setup(opts: {
    connection?: ConnectionStatusModel; preflight?: PreflightResultModel | null; settings?: IntegrationSettingsModel | null;
    summary?: SyncSummaryModel | null; query?: Record<string, string>; connectionFails?: boolean;
  } = {}) {
    service = jasmine.createSpyObj<QuickBooksIntegrationService>('QuickBooksIntegrationService', [
      'getConnection', 'getPreflight', 'getSettings', 'getSyncSummary', 'getReference', 'getTaxMappings', 'getTermMappings',
      'getSyncItems', 'getMatchCandidates', 'getApiClients', 'connect', 'testConnection', 'disconnect'
    ]);
    service.getConnection.and.returnValue(opts.connectionFails
      ? throwError(() => ({ status: 500, error: { message: 'Integration module is down.' } }))
      : of(opts.connection ?? connection({ status: 'NotConnected', isConnected: false })));
    service.getPreflight.and.returnValue(opts.preflight ? of(opts.preflight) : throwError(() => ({ status: 409 })));
    service.getSettings.and.returnValue(opts.settings ? of(opts.settings) : throwError(() => ({ status: 409 })));
    service.getSyncSummary.and.returnValue(of(opts.summary ?? EMPTY_SUMMARY));
    service.getReference.and.returnValue(of({ accounts: [], taxCodes: [], terms: [], currencies: [] }));
    service.getTaxMappings.and.returnValue(of([]));
    service.getTermMappings.and.returnValue(of([]));
    service.getSyncItems.and.returnValue(of({ data: [], totalRecords: 0, page: 1, pageSize: 25, totalPages: 0, hasNext: false, hasPrevious: false }));
    service.getMatchCandidates.and.returnValue(of([]));
    service.getApiClients.and.returnValue(of([]));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [QuickBooksIntegrationComponent],
      providers: [
        provideNoopAnimations(), provideRouter([]),
        { provide: QuickBooksIntegrationService, useValue: service },
        { provide: PaymentTermsService, useValue: { getAll: () => of({ success: true, message: '', result: [] }) } },
        { provide: AuthService, useValue: { hasPermission: (c: string) => permissions.includes(c) } },
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(opts.query ?? {}) } } }
      ]
    }).compileComponents();

    spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    fixture = TestBed.createComponent(QuickBooksIntegrationComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  function stepDone(key: string): boolean {
    return query(`step-${key}`)!.getAttribute('data-done') === 'true';
  }

  function tabDisabled(key: string): boolean {
    const tab = query(`tab-${key}`);
    return !!tab && (tab.getAttribute('data-p-disabled') === 'true' || tab.hasAttribute('disabled') || tab.getAttribute('aria-disabled') === 'true');
  }

  beforeEach(() => { permissions = ['INTEGRATION_VIEW', 'INTEGRATION_MANAGE', 'INTEGRATION_SYNC']; });

  it('starts on Connection with only that tab open when nothing is connected', async () => {
    await setup();
    expect(component.activeTab).toBe('connection');
    expect(service.getPreflight).not.toHaveBeenCalled();
    expect(service.getSettings).not.toHaveBeenCalled();
    expect(query('checklist')).not.toBeNull();
    expect(['connected', 'preflight', 'mappings', 'initialSync', 'matching', 'live'].every(k => !stepDone(k))).toBeTrue();

    expect(tabDisabled('connection')).toBeFalse();
    for (const key of ['preflight', 'mappings', 'initial-sync', 'match', 'sync']) expect(tabDisabled(key)).withContext(key).toBeTrue();
    expect(tabDisabled('api-clients')).toBeFalse();
    expect(query('not-connected')).not.toBeNull();
  });

  it('opens on the next step to do, with the tabs before it open', async () => {
    await setup({ connection: connection(), preflight: PASSED, settings: settings() });
    expect(stepDone('connected')).toBeTrue();
    expect(stepDone('preflight')).toBeTrue();
    expect(stepDone('mappings')).toBeFalse();
    expect(component.activeTab).toBe('mappings');
    expect(component.isUnlocked('mappings')).toBeTrue();
    expect(component.isUnlocked('initial-sync')).toBeFalse();
    expect(component.lockReason('initial-sync')).toContain('Mappings saved');
    // Only the open tab's component exists, so only the mappings were loaded.
    expect(service.getReference).toHaveBeenCalledTimes(1);
    expect(service.getMatchCandidates).not.toHaveBeenCalled();
  });

  it('opens every tab and the dashboard once Live, without the checklist', async () => {
    await setup({
      connection: connection({ status: 'Live', mode: 'Live' }), preflight: { ...PASSED, canGoLive: true },
      settings: settings({ mode: 'Live', defaultIncomeAccountId: '1', defaultExpenseAccountId: '2', matchingConfirmedAt: '2026-09-30' }),
      summary: { ...EMPTY_SUMMARY, kinds: [{ kind: 'Customer', total: 3, countsByState: { Synced: 3 } }] }
    });
    expect(query('checklist')).toBeNull();
    expect(component.activeTab).toBe('sync');
    for (const key of ['connection', 'preflight', 'mappings', 'initial-sync', 'match', 'sync']) expect(component.isUnlocked(key as any)).toBeTrue();
    expect(query('header-mode')!.textContent).toContain('Live');
  });

  it('shows the Connection tab when Intuit sends the admin back, whatever step is next', async () => {
    await setup({ connection: connection(), preflight: PASSED, settings: settings(), query: { result: 'connected' } });
    expect(component.activeTab).toBe('connection');
  });

  it('hides the API clients tab without the manage permission', async () => {
    permissions = ['INTEGRATION_VIEW'];
    await setup();
    expect(query('tab-api-clients')).toBeNull();
    expect(component.tabs.some(t => t.key === 'api-clients')).toBeFalse();
  });

  it('moves the checklist on when a tab saves settings, and re-reads the preflight', async () => {
    await setup({ connection: connection(), preflight: PASSED, settings: settings() });
    expect(component.isUnlocked('initial-sync')).toBeFalse();
    service.getPreflight.calls.reset();

    component.onSettingsChange(settings({ defaultIncomeAccountId: '79', defaultExpenseAccountId: '80' }));
    fixture.detectChanges();

    expect(stepDone('mappings')).toBeTrue();
    expect(component.isUnlocked('initial-sync')).toBeTrue();
    expect(service.getPreflight).toHaveBeenCalledTimes(1);
  });

  it('refuses to open a locked tab', async () => {
    await setup();
    component.selectTab('sync');
    expect(component.activeTab).toBe('connection');
  });

  it('goes back to the Connection tab when a disconnect locks the others', async () => {
    await setup({ connection: connection(), preflight: PASSED, settings: settings() });
    expect(component.activeTab).toBe('mappings');
    component.onConnectionChange(connection({ status: 'NotConnected', isConnected: false }));
    expect(component.activeTab).toBe('connection');
    expect(component.isUnlocked('mappings')).toBeFalse();
  });

  it('says so when the page cannot load, and tries again', async () => {
    await setup({ connectionFails: true });
    expect(query('load-failed')!.textContent).toContain('Integration module is down.');
    service.getConnection.and.returnValue(of(connection({ status: 'NotConnected', isConnected: false })));
    (query('retry')!.querySelector('button') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(query('load-failed')).toBeNull();
    expect(query('checklist')).not.toBeNull();
  });
});
