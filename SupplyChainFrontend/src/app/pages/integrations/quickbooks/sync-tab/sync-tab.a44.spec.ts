import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { ConfirmationService, MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { DEAD_LETTER_FILTER, SyncTabComponent } from './sync-tab.component';
import { QuickBooksIntegrationService, SyncItemModel, SyncSummaryModel } from '../../../../services/quickbooks-integration.service';

const SUMMARY: SyncSummaryModel = {
  connectionStatus: 'Live', mode: 'Live', queueDepth: 0,
  kinds: [
    { kind: 'Customer', total: 5, countsByState: { Synced: 3, Failed: 2 } },
    { kind: 'Payment', total: 4, countsByState: { Synced: 3, NeedsResolution: 1 } },
    { kind: 'VendorCredit', total: 0, countsByState: {} }
  ]
};

function item(overrides: Partial<SyncItemModel> = {}): SyncItemModel {
  return {
    id: 's1', kind: 'Customer', externalId: 'p-1', sourceSystem: 'SCM', displayLabel: 'Acme Ltd', state: 'Synced',
    remoteId: '58', remoteDocNumber: null, deepLink: null, lastErrorCode: null, lastError: null, warning: null,
    lastSyncedAt: null, updatedAt: '2026-10-16T09:00:00Z', attemptCount: 1, ...overrides
  };
}

/** A44 D-11 / D-17 / D-30 — new kinds, dead letter, bulk retry, skip and unlink on the Sync tab. */
describe('SyncTabComponent — A44', () => {
  let fixture: ComponentFixture<SyncTabComponent>;
  let component: SyncTabComponent;
  let service: jasmine.SpyObj<QuickBooksIntegrationService>;
  let toasts: jasmine.Spy;
  let confirm: jasmine.Spy;

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  async function setup(canManage = true) {
    service = jasmine.createSpyObj<QuickBooksIntegrationService>('QuickBooksIntegrationService', [
      'getSyncSummary', 'getPreflight', 'getSyncItems', 'retryFailed', 'skipSyncItem', 'unlinkSyncItem', 'backfill', 'pushNow'
    ]);
    service.getSyncSummary.and.returnValue(of(SUMMARY));
    service.getPreflight.and.returnValue(of({ passed: true, canGoLive: true, checks: [] }));
    const rows = [
      item({ id: 'c1', state: 'Failed', lastError: 'Duplicate', remoteId: null }),
      item({ id: 'c2' }),
      item({ id: 'p1', kind: 'Payment', state: 'NeedsResolution', remoteId: null, displayLabel: 'CPAY-1' }),
      item({ id: 'h1', kind: 'SalesReceipt', state: 'NotSynced', remoteId: null, lastErrorCode: 'SYNC_HELD', displayLabel: 'POS-9' })
    ];
    service.getSyncItems.and.returnValue(of({ data: rows, totalRecords: rows.length, page: 1, pageSize: 25, totalPages: 1, hasNext: false, hasPrevious: false }));
    service.retryFailed.and.returnValue(of({ requeued: 3 }));
    service.skipSyncItem.and.callFake(id => of(item({ id, state: 'NotSynced', remoteId: null })));
    service.unlinkSyncItem.and.callFake(id => of(item({ id, state: 'NotSynced', remoteId: null })));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [SyncTabComponent],
      providers: [provideNoopAnimations(), provideRouter([]), MessageService, { provide: QuickBooksIntegrationService, useValue: service }]
    }).compileComponents();

    fixture = TestBed.createComponent(SyncTabComponent);
    component = fixture.componentInstance;
    component.canManage = canManage;
    component.canSync = true;
    toasts = spyOn(TestBed.inject(MessageService), 'add');
    confirm = spyOn(fixture.debugElement.injector.get(ConfirmationService), 'confirm');
    fixture.detectChanges();
  }

  it('shows a card for a new kind once it has records, without "Sync all"', async () => {
    await setup();
    expect(query('kind-card-Payment')!.textContent).toContain('Payments');
    expect(query('kind-card-VendorCredit')).toBeNull();
    expect(query('kind-card-Bill')).not.toBeNull();
    expect(query('sync-all-Payment')).toBeNull();
    expect(query('sync-all-Customer')).not.toBeNull();
  });

  it('labels the new kinds in the filter and the rows, and marks held records', async () => {
    await setup();
    expect(component.kindOptions.map(o => o.label)).toContain('Bill payment');
    expect(component.kindOptions.map(o => o.label)).toContain('Refund receipt');
    expect(component.labelOf('SalesReceipt')).toBe('Sales receipt');
    expect(query('held-h1')!.textContent).toContain('Held by sync control');
    expect(query('held-c2')).toBeNull();
  });

  it('the dead letter filter asks for Failed + NeedsResolution together', async () => {
    await setup();
    expect(component.deadLetterCount).toBe(3);
    service.getSyncItems.calls.reset();
    query('show-dead-letter')!.querySelector('button')!.click();
    expect(component.stateFilter).toBe(DEAD_LETTER_FILTER);
    expect(service.getSyncItems).toHaveBeenCalledOnceWith(jasmine.objectContaining({ deadLetter: true, state: null, page: 1 }));
    expect(component.stateOptions[1].label).toContain('Dead letter');
  });

  it('"Retry failed" confirms, then queues the failed records (of the filtered kind)', async () => {
    await setup();
    component.kindFilter = 'Payment';
    query('retry-failed')!.querySelector('button')!.click();
    expect(confirm).toHaveBeenCalledTimes(1);
    expect(confirm.calls.mostRecent().args[0].message).toContain('1 failed or unresolved record (payments) is queued');
    expect(service.retryFailed).not.toHaveBeenCalled();
    confirm.calls.mostRecent().args[0].accept();
    expect(service.retryFailed).toHaveBeenCalledOnceWith('Payment');
    expect(toasts.calls.mostRecent().args[0].detail).toContain('3 records are queued');
  });

  it('skip is offered on failed / unresolved / blocked rows only and confirms first', async () => {
    await setup();
    expect(query('skip-c1')).not.toBeNull();
    expect(query('skip-p1')).not.toBeNull();
    expect(query('skip-c2')).toBeNull();
    query('skip-c1')!.querySelector('button')!.click();
    expect(service.skipSyncItem).not.toHaveBeenCalled();
    confirm.calls.mostRecent().args[0].accept();
    expect(service.skipSyncItem).toHaveBeenCalledOnceWith('c1');
    expect(component.items.find(i => i.id === 'c1')!.state).toBe('NotSynced');
  });

  it('unlink is offered on linked customers / vendors / items and reports a 409 in the server\'s words', async () => {
    await setup();
    expect(query('unlink-c2')).not.toBeNull();
    expect(query('unlink-p1')).toBeNull();
    service.unlinkSyncItem.and.returnValue(throwError(() => ({ status: 409, error: { message: '3 open documents were sent against it.' } })));
    component.requestUnlink(component.items.find(i => i.id === 'c2')!);
    expect(confirm.calls.mostRecent().args[0].header).toBe('Unlink from QuickBooks?');
    confirm.calls.mostRecent().args[0].accept();
    expect(service.unlinkSyncItem).toHaveBeenCalledOnceWith('c2');
    expect(toasts.calls.mostRecent().args[0].detail).toBe('3 open documents were sent against it.');
    expect(component.items.find(i => i.id === 'c2')!.remoteId).toBe('58');
  });

  it('a successful unlink clears the QuickBooks link on the row', async () => {
    await setup();
    component.unlink(component.items.find(i => i.id === 'c2')!);
    expect(component.items.find(i => i.id === 'c2')!.remoteId).toBeNull();
  });

  it('Push now: a document SCM has nothing to send for gets an info toast, and nothing reloads', async () => {
    await setup();
    service.pushNow.and.returnValue(of({ requested: 0, skippedInMatching: [], notFound: ['P-1'] }));
    service.getSyncItems.calls.reset();
    component.pushNow(component.items.find(i => i.id === 'c2')!);
    expect(toasts.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({ severity: 'info', detail: 'Nothing to send for this document.' }));
    expect(service.getSyncItems).not.toHaveBeenCalled();
  });

  it('none of it without manage', async () => {
    await setup(false);
    expect(query('retry-failed')).toBeNull();
    expect(query('skip-c1')).toBeNull();
    expect(query('unlink-c2')).toBeNull();
  });
});
