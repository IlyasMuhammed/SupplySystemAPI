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
      'getSyncItems', 'getMatchCandidates', 'getApiClients', 'connect', 'testConnection', 'disconnect', 'getOwnAppKeys',
      'getSyncControl', 'getRemote', 'getCoverage', 'getCallLog'
    ]);
    service.getCallLog.and.returnValue(of({ data: [], totalRecords: 0, page: 1, pageSize: 50, totalPages: 0, hasNext: false, hasPrevious: false }));
    // A44 — an older server: the Mappings tab keeps its switches, the Match tab hides "Only in QuickBooks".
    service.getSyncControl.and.returnValue(throwError(() => ({ status: 404 })));
    service.getRemote.and.returnValue(throwError(() => ({ status: 404 })));
    service.getCoverage.and.returnValue(of({
      ready: false, entities: [], warnings: [], blockingIssues: [{ code: 'TAX', message: 'Map GST17.', area: 'Tax', tab: 'tax' }]
    }));
    service.getOwnAppKeys.and.returnValue(of({ featureEnabled: false, hasOwnKeys: false, hasSecret: false, environment: 'Sandbox', connectedWithOwnKeys: false }));
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

  // ── A44 D-30 ───────────────────────────────────────────────────────────────

  it('opens the Accounts, Defaults, Sync control and Coverage tabs as soon as QuickBooks is connected', async () => {
    await setup({ connection: connection(), preflight: PASSED, settings: settings() });
    expect(component.tabs.map(t => t.key)).toEqual([
      'connection', 'preflight', 'mappings', 'accounts', 'defaults', 'sync-control', 'initial-sync', 'match', 'sync', 'coverage', 'logs', 'api-clients'
    ]);
    for (const key of ['accounts', 'defaults', 'sync-control', 'coverage', 'logs']) expect(component.isUnlocked(key as any)).withContext(key).toBeTrue();
    expect(component.isUnlocked('initial-sync')).toBeFalse();
  });

  it('keeps them locked while not connected', async () => {
    await setup();
    for (const key of ['accounts', 'defaults', 'sync-control', 'coverage']) expect(component.isUnlocked(key as any)).withContext(key).toBeFalse();
    expect(component.lockReason('coverage')).toContain('Connected');
  });

  it('a Coverage "Fix on" button opens the tab that fixes it', async () => {
    await setup({ connection: connection(), preflight: PASSED, settings: settings() });
    component.selectTab('coverage');
    fixture.detectChanges();
    expect(service.getCoverage).toHaveBeenCalled();
    (query('fix-TAX')!.querySelector('button') as HTMLButtonElement).click();
    expect(component.activeTab).toBe('mappings');
  });

  it('?tab=logs (the "QuickBooks Logs" menu entry) opens the Logs tab once connected', async () => {
    await setup({ connection: connection(), preflight: PASSED, settings: settings(), query: { tab: 'logs' } });
    expect(component.activeTab).toBe('logs');
    expect(service.getCallLog).toHaveBeenCalledWith(jasmine.objectContaining({ failedOnly: true }));
  });

  it('?tab=logs is ignored while not connected, and an unknown tab is ignored', async () => {
    await setup({ query: { tab: 'logs' } });
    expect(component.activeTab).toBe('connection');
    await setup({ connection: connection(), preflight: PASSED, settings: settings(), query: { tab: 'nope' } });
    expect(component.activeTab).toBe('mappings');
  });

  it('a log row\'s "Sync item" opens the Sync tab filtered on that record', async () => {
    await setup({
      connection: connection({ status: 'Live', mode: 'Live' }), preflight: { ...PASSED, canGoLive: true },
      settings: settings({ mode: 'Live', defaultIncomeAccountId: '1', defaultExpenseAccountId: '2', matchingConfirmedAt: '2026-09-30' }),
      summary: { ...EMPTY_SUMMARY, kinds: [{ kind: 'Customer', total: 3, countsByState: { Synced: 3 } }] }
    });
    component.selectTab('logs');
    fixture.detectChanges();
    service.getSyncItems.calls.reset();
    component.onOpenSyncItem({ kind: 'Payment', search: 'pay-1' });
    fixture.detectChanges();
    await fixture.whenStable();
    expect(component.activeTab).toBe('sync');
    expect(service.getSyncItems).toHaveBeenCalledWith(jasmine.objectContaining({ kind: 'Payment', search: 'pay-1' }));
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
