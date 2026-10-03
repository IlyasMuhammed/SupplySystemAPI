import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { ConfirmationService, MessageService } from 'primeng/api';
import { of } from 'rxjs';

import { SyncTabComponent } from './sync-tab.component';
import {
  IntegrationSettingsModel, PreflightResultModel, QuickBooksIntegrationService, SyncItemModel, SyncSummaryModel
} from '../../../../services/quickbooks-integration.service';

function settings(overrides: Partial<IntegrationSettingsModel> = {}): IntegrationSettingsModel {
  return {
    mode: 'DryRun', autoPushCustomers: true, autoPushVendors: true, autoPushItems: true, autoPushSalesInvoices: true,
    autoPushBills: true, itemTypeDefault: 'NonInventory', partnerScope: 'OnlyWhenReferenced', matchingConfirmedAt: null,
    ...overrides
  };
}

const SUMMARY: SyncSummaryModel = {
  connectionStatus: 'NeedsSetup', mode: 'DryRun', queueDepth: 3, lastRunAt: '2026-09-30T09:00:00Z',
  kinds: [
    { kind: 'Customer', total: 12, countsByState: { DryRunOk: 9, Blocked: 2, Failed: 1 } },
    { kind: 'Item', total: 4, countsByState: { DryRunOk: 4 } }
  ]
};

const NOT_READY: PreflightResultModel = {
  passed: false, canGoLive: false,
  checks: [
    { code: 'CUSTOM_TXN_NUMBERS', title: 'Custom transaction numbers', status: 'Fail', message: 'Turn on custom transaction numbers in QuickBooks.' },
    { code: 'HOME_CURRENCY', title: 'Home currency', status: 'Pass', message: 'PKR' }
  ]
};

const READY: PreflightResultModel = { passed: true, canGoLive: true, checks: [] };

function item(overrides: Partial<SyncItemModel> = {}): SyncItemModel {
  return {
    id: 's1', kind: 'Customer', externalId: 'p-1', sourceSystem: 'SCM', displayLabel: 'Acme Ltd', state: 'Failed',
    remoteId: null, remoteDocNumber: null, deepLink: null, lastErrorCode: '6240', lastError: 'Duplicate Name Exists Error',
    warning: null, lastSyncedAt: null, updatedAt: '2026-09-30T09:00:00Z', attemptCount: 3, ...overrides
  };
}

describe('SyncTabComponent', () => {
  let fixture: ComponentFixture<SyncTabComponent>;
  let component: SyncTabComponent;
  let service: jasmine.SpyObj<QuickBooksIntegrationService>;
  let toasts: jasmine.Spy;
  let confirm: jasmine.Spy;

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  function button(testId: string): HTMLButtonElement | null {
    return query(testId)?.querySelector('button') ?? null;
  }

  async function setup(opts: { preflight?: PreflightResultModel; settings?: IntegrationSettingsModel; canManage?: boolean; canSync?: boolean; items?: SyncItemModel[] } = {}) {
    service = jasmine.createSpyObj<QuickBooksIntegrationService>('QuickBooksIntegrationService', [
      'getSyncSummary', 'getPreflight', 'getSyncItems', 'setMode', 'retrySyncItem', 'resolveSyncItem', 'pushNow',
      'getSyncLog', 'backfill'
    ]);
    service.getSyncSummary.and.returnValue(of(SUMMARY));
    service.getPreflight.and.returnValue(of(opts.preflight ?? NOT_READY));
    const rows = opts.items ?? [item(), item({ id: 's2', state: 'NeedsResolution', displayLabel: 'INV-9', kind: 'SalesInvoice' }),
      item({ id: 's3', state: 'Synced', lastError: null, remoteId: '145', remoteDocNumber: 'SINV-1', deepLink: 'https://qbo/145', sourceSystem: 'Shop POS' })];
    service.getSyncItems.and.returnValue(of({ data: rows, totalRecords: rows.length, page: 1, pageSize: 25, totalPages: 1, hasNext: false, hasPrevious: false }));
    service.setMode.and.callFake(mode => of(settings({ mode, matchingConfirmedAt: '2026-09-29T00:00:00Z' })));
    service.retrySyncItem.and.callFake(id => of(item({ id, state: 'Pending' })));
    service.resolveSyncItem.and.callFake(id => of(item({ id, state: 'Synced' })));
    service.pushNow.and.returnValue(of({ requested: 1 }));
    service.backfill.and.returnValue(of({ kind: 'Customer', sent: 12 }));
    service.getSyncLog.and.returnValue(of([
      { id: 'l1', operation: 'Create', outcome: 'Failed', errorCode: '6240', message: 'Duplicate', durationMs: 420, intuitTid: 'tid-1',
        createdAt: '2026-09-30T09:00:00Z', requestJson: '{"DisplayName":"Acme"}', responseJson: '{"Fault":{}}' }
    ]));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [SyncTabComponent],
      providers: [provideNoopAnimations(), provideRouter([]), MessageService, { provide: QuickBooksIntegrationService, useValue: service }]
    }).compileComponents();

    fixture = TestBed.createComponent(SyncTabComponent);
    component = fixture.componentInstance;
    component.canManage = opts.canManage ?? true;
    component.canSync = opts.canSync ?? true;
    component.settings = opts.settings ?? settings();
    toasts = spyOn(TestBed.inject(MessageService), 'add');
    confirm = spyOn(fixture.debugElement.injector.get(ConfirmationService), 'confirm');
    fixture.detectChanges();
  }

  // ── Mode switch gating ─────────────────────────────────────────────────────

  it('keeps Live off until the preflight allows it, and lists why', async () => {
    await setup({ preflight: NOT_READY });
    expect(query('mode-dry-run')).not.toBeNull();
    expect(button('go-live')!.disabled).toBeTrue();

    const blockers = query('live-blockers')!.textContent!;
    expect(blockers).toContain('Custom transaction numbers: Turn on custom transaction numbers in QuickBooks.');
    expect(blockers).toContain('Matching has not been marked complete');

    component.requestMode('Live');
    expect(confirm).not.toHaveBeenCalled();
    expect(service.setMode).not.toHaveBeenCalled();
  });

  it('switches to Live after confirmation once canGoLive is true', async () => {
    await setup({ preflight: READY, settings: settings({ matchingConfirmedAt: '2026-09-29T00:00:00Z' }) });
    const emitted: IntegrationSettingsModel[] = [];
    component.settingsChange.subscribe(s => emitted.push(s));

    expect(query('live-blockers')).toBeNull();
    const goLive = button('go-live')!;
    expect(goLive.disabled).toBeFalse();
    goLive.click();

    expect(confirm).toHaveBeenCalledTimes(1);
    const options = confirm.calls.mostRecent().args[0];
    expect(options.header).toBe('Switch to Live?');
    expect(service.setMode).not.toHaveBeenCalled();

    options.accept();
    expect(service.setMode).toHaveBeenCalledOnceWith('Live');
    expect(emitted[0].mode).toBe('Live');
    fixture.detectChanges();
    expect(query('mode-live')).not.toBeNull();
    expect(query('go-dry-run')).not.toBeNull();
  });

  it('does nothing when the confirmation is declined', async () => {
    await setup({ preflight: READY });
    component.requestMode('Live');
    confirm.calls.mostRecent().args[0].reject?.();
    expect(service.setMode).not.toHaveBeenCalled();
  });

  it('switches back to dry run after confirmation, whatever the preflight says', async () => {
    await setup({ preflight: NOT_READY, settings: settings({ mode: 'Live' }) });
    expect(query('live-blockers')).toBeNull();
    button('go-dry-run')!.click();
    expect(confirm.calls.mostRecent().args[0].header).toBe('Switch back to dry run?');
    confirm.calls.mostRecent().args[0].accept();
    expect(service.setMode).toHaveBeenCalledOnceWith('DryRun');
  });

  it('offers no mode switch without the manage permission', async () => {
    await setup({ preflight: READY, canManage: false });
    expect(query('go-live')).toBeNull();
    component.requestMode('Live');
    expect(confirm).not.toHaveBeenCalled();
  });

  // ── Summary ────────────────────────────────────────────────────────────────

  it('shows a card per kind with its counts by state, and a count filters the table', async () => {
    await setup();
    expect(query('kind-card-Customer')!.textContent).toContain('12');
    expect(query('chip-Customer-Blocked')!.textContent).toContain('2');
    expect(query('chip-Customer-Blocked')!.className).toContain('qbo-tone-red');
    expect(query('chip-Item-DryRunOk')!.className).toContain('qbo-tone-teal');
    expect(query('kind-card-Bill')!.textContent).toContain('Nothing yet');

    service.getSyncItems.calls.reset();
    query('chip-Customer-Blocked')!.click();
    expect(service.getSyncItems).toHaveBeenCalledOnceWith(jasmine.objectContaining({ kind: 'Customer', state: 'Blocked', page: 1 }));
  });

  it('reads the state counts whatever the casing of their keys', async () => {
    await setup();
    service.getSyncSummary.and.returnValue(of({ ...SUMMARY, kinds: [{ kind: 'Bill', total: 2, countsByState: { dryRunOk: 2 } as any }] }));
    component.loadSummary();
    fixture.detectChanges();
    expect(query('chip-Bill-DryRunOk')!.textContent).toContain('2');
  });

  it('"Sync all" sends the kind to the gateway and refreshes', async () => {
    await setup();
    service.getSyncSummary.calls.reset();
    button('sync-all-Customer')!.click();
    expect(service.backfill).toHaveBeenCalledOnceWith('Customer');
    expect(service.getSyncSummary).toHaveBeenCalledTimes(1);
  });

  // ── Rows ───────────────────────────────────────────────────────────────────

  it('lists records with state, QuickBooks reference and error, and asks with the filters', async () => {
    await setup();
    const rows = fixture.nativeElement.querySelectorAll('[data-testid="item-row"]');
    expect(rows.length).toBe(3);
    expect(rows[0].textContent).toContain('Duplicate Name Exists Error');
    expect(rows[2].textContent).toContain('SINV-1');
    expect(query('deep-link')!.getAttribute('href')).toBe('https://qbo/145');

    service.getSyncItems.calls.reset();
    component.kindFilter = 'Bill';
    component.stateFilter = 'Failed';
    component.search = 'acme';
    component.onFilterChange();
    expect(service.getSyncItems).toHaveBeenCalledOnceWith({ kind: 'Bill', state: 'Failed', search: 'acme', page: 1, pageSize: 25 });
  });

  it('offers Retry (SYNC) on failed rows only, Resolve (MANAGE) on unresolved ones, Push now only for SCM records', async () => {
    await setup();
    expect(query('retry-s1')).not.toBeNull();
    expect(query('retry-s2')).toBeNull();    // needs resolution: a person decides
    expect(query('resolve-s2')).not.toBeNull();
    expect(query('resolve-s3')).toBeNull();  // synced
    expect(query('push-s1')).not.toBeNull();
    expect(query('push-s3')).toBeNull();     // sent by another system

    button('retry-s1')!.click();
    expect(service.retrySyncItem).toHaveBeenCalledOnceWith('s1');
    expect(component.items[0].state).toBe('Pending');
  });

  it('hides Retry and Push now without INTEGRATION_SYNC, and Resolve without INTEGRATION_MANAGE', async () => {
    await setup({ canSync: false, canManage: false });
    expect(query('retry-s1')).toBeNull();
    expect(query('push-s1')).toBeNull();
    expect(query('resolve-s2')).toBeNull();
    expect(query('log-s1')).not.toBeNull();
  });

  it('resolves by linking to a QuickBooks id, which it requires', async () => {
    await setup();
    component.openResolve(component.items[1]);
    component.resolveAction = 'LinkRemote';
    component.resolveRemoteId = '  ';
    expect(component.canSubmitResolve).toBeFalse();

    component.resolveRemoteId = ' 145 ';
    component.resolve();
    expect(service.resolveSyncItem).toHaveBeenCalledOnceWith('s2', { action: 'LinkRemote', remoteId: '145' });
    expect(component.resolveVisible).toBeFalse();
  });

  it('sends no id for Mark resolved or Requeue', async () => {
    await setup();
    component.openResolve(component.items[1]);
    component.resolveAction = 'Requeue';
    component.resolveRemoteId = '999';
    component.resolve();
    expect(service.resolveSyncItem).toHaveBeenCalledOnceWith('s2', { action: 'Requeue', remoteId: null });
  });

  it('opens the attempt log with request and response JSON', async () => {
    await setup();
    button('log-s1')!.click();
    fixture.detectChanges();
    expect(service.getSyncLog).toHaveBeenCalledOnceWith('s1');
    expect(component.logVisible).toBeTrue();
    expect(component.logs.length).toBe(1);
    expect(component.prettyJson(component.logs[0].requestJson)).toContain('"DisplayName": "Acme"');
  });
});
