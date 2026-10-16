import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { LogsTabComponent, isRecovered, outcomeSeverity } from './logs-tab.component';
import { QuickBooksIntegrationService, SyncLogItemModel } from '../../../../services/quickbooks-integration.service';
import { QboRetryService } from '../../../../shared/components/qbo-sync-badge/qbo-retry.service';

// GET sync/log rows as CTRL answers them (PostingAdminModels.SyncLogItemModel).
function row(overrides: Partial<SyncLogItemModel> = {}): SyncLogItemModel {
  return {
    id: 'l1', createdAt: '2026-10-16T09:00:00Z', kind: 'SalesInvoice', itemId: 'm1', externalId: 'inv-1', sourceSystem: 'SCM',
    displayLabel: 'SI-2026-00012', operation: 'Create', outcome: 'Refused', errorCode: '6000', message: 'Amount is over the balance.',
    durationMs: 812, intuitTid: 'tid-9', state: 'Failed', remoteId: null, remoteDocNumber: null, deepLink: null, ...overrides
  };
}

describe('LogsTabComponent (A44 D-34)', () => {
  let fixture: ComponentFixture<LogsTabComponent>;
  let component: LogsTabComponent;
  let service: jasmine.SpyObj<QuickBooksIntegrationService>;
  let retry: jasmine.SpyObj<QboRetryService>;
  let toasts: jasmine.Spy;

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  async function setup(opts: {
    rows?: SyncLogItemModel[]; canManage?: boolean; fail?: boolean; canPush?: boolean; mayAct?: boolean; actable?: string[];
  } = {}) {
    service = jasmine.createSpyObj<QuickBooksIntegrationService>('QuickBooksIntegrationService', ['getCallLog', 'getCallLogEntry']);
    const rows = opts.rows ?? [
      row(),
      row({ id: 'l2', kind: 'Customer', externalId: 'bp-1', displayLabel: 'Acme', operation: 'Update', outcome: 'Succeeded', errorCode: null,
            message: null, state: 'Synced', remoteId: '58', deepLink: 'https://app.qbo.intuit.com/app/customerdetail?nameId=58' }),
      row({ id: 'l3', kind: null, itemId: null, externalId: null, displayLabel: null, operation: 'Query', outcome: 'Transient', state: null })
    ];
    service.getCallLog.and.returnValue(opts.fail
      ? throwError(() => ({ status: 500, error: { message: 'Log down.' } }))
      : of({ data: rows, totalRecords: rows.length, page: 1, pageSize: 50, totalPages: 1, hasNext: false, hasPrevious: false }));
    service.getCallLogEntry.and.callFake(id => of(row({ id, requestJson: '{"Line":[{"Amount":10}]}', responseJson: '{"Fault":{"Error":[]}}' })));
    retry = jasmine.createSpyObj<QboRetryService>('QboRetryService', ['run', 'canPush', 'mayActOnPostings', 'hasActableTarget']);
    retry.run.and.returnValue(of({ result: 'queued', postingsRetried: 0, pushed: true }));
    retry.canPush.and.returnValue(opts.canPush ?? true);
    retry.mayActOnPostings.and.returnValue(opts.mayAct ?? false);
    retry.hasActableTarget.and.callFake((_k, id) => of((opts.actable ?? []).includes(id)));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [LogsTabComponent],
      providers: [provideNoopAnimations(), provideRouter([]), MessageService,
        { provide: QuickBooksIntegrationService, useValue: service }, { provide: QboRetryService, useValue: retry }]
    }).compileComponents();

    fixture = TestBed.createComponent(LogsTabComponent);
    component = fixture.componentInstance;
    component.canManage = opts.canManage ?? true;
    toasts = spyOn(TestBed.inject(MessageService), 'add');
    fixture.detectChanges();
  }

  it('asks for failed calls only by default, then with every filter', async () => {
    await setup();
    expect(service.getCallLog).toHaveBeenCalledWith(jasmine.objectContaining({ failedOnly: true, page: 1, pageSize: 50 }));
    component.failedOnly = false;
    component.kind = 'Payment';
    component.operation = 'Void';
    component.outcome = 'Refused';
    component.from = '2026-10-01';
    component.to = '2026-10-16';
    component.search = 'SI-12';
    component.reload();
    expect(service.getCallLog.calls.mostRecent().args[0]).toEqual({
      failedOnly: false, unresolvedOnly: true, kind: 'Payment', operation: 'Void', outcome: 'Refused', from: '2026-10-01', to: '2026-10-16', search: 'SI-12', page: 1, pageSize: 50
    });
    component.clearFilters();
    expect(service.getCallLog.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({ kind: null, search: '', failedOnly: false }));
  });

  it('a row: time, document link + Sync item, operation, outcome chip, error, duration, intuit_tid', async () => {
    await setup();
    const r = query('log-row-l1')!;
    expect(r.textContent).toContain('Sales invoice');
    expect(query('log-doc-l1')!.getAttribute('href')).toContain('/portal/pages/finance/sales-invoices/inv-1');
    expect(query('log-item-l1')!.textContent).toContain('Sync item · Failed');
    expect(r.textContent).toContain('Create');
    expect(query('log-outcome-l1')!.textContent).toContain('Refused');
    expect(r.textContent).toContain('6000');
    expect(r.textContent).toContain('Amount is over the balance.');
    expect(r.textContent).toContain('812 ms');
    expect(r.textContent).toContain('tid-9');
    // A call that belongs to no record (a matching scan).
    expect(query('log-row-l3')!.textContent).toContain('Query');
    expect(query('log-item-l3')).toBeNull();
  });

  it('Retry on failed SCM records (the D-33 action), Open in QuickBooks when linked', async () => {
    await setup();
    expect(query('log-retry-l1')).not.toBeNull();
    expect(query('log-retry-l2')).toBeNull();   // synced
    expect(query('log-retry-l3')).toBeNull();   // no record
    expect(query('log-open-l2')!.getAttribute('href')).toContain('nameId=58');
    query('log-retry-l1')!.querySelector('button')!.click();
    expect(retry.run).toHaveBeenCalledOnceWith('SalesInvoice', 'inv-1');
    expect(toasts.calls.mostRecent().args[0].detail).toContain('SI-2026-00012 is queued');
    expect(service.getCallLog).toHaveBeenCalledTimes(2);
  });

  // ── REV-19: the same gate as the badge ───────────────────────────────────────

  it('INTEGRATION_SYNC: Retry on failed SCM rows without asking the posting log', async () => {
    await setup({ canPush: true });
    expect(query('log-retry-l1')).not.toBeNull();
    expect(retry.hasActableTarget).not.toHaveBeenCalled();
  });

  it('INTEGRATION_MANAGE without SYNC: Retry only where a posting target has canAct (asked once per document)', async () => {
    const rows = [row(), row({ id: 'l4' }), row({ id: 'l5', externalId: 'inv-2', displayLabel: 'SI-2' })];
    await setup({ rows, canPush: false, mayAct: true, actable: ['inv-1'] });
    fixture.detectChanges();
    expect(retry.hasActableTarget.calls.allArgs()).toEqual([['SalesInvoice', 'inv-1'], ['SalesInvoice', 'inv-2']]);
    expect(query('log-retry-l1')).not.toBeNull();
    expect(query('log-retry-l4')).not.toBeNull();   // same document
    expect(query('log-retry-l5')).toBeNull();
  });

  it('REV-20: Retry acts on the log row\'s own kind (a POS sale logged as a SalesReceipt is pushed as one)', async () => {
    await setup({ rows: [row({ id: 'l9', kind: 'SalesReceipt', externalId: 'inv-7', displayLabel: 'SI-7' })] });
    query('log-retry-l9')!.querySelector('button')!.click();
    expect(retry.run).toHaveBeenCalledOnceWith('SalesReceipt', 'inv-7');
  });

  it('neither SYNC nor a posting code: no Retry', async () => {
    await setup({ canPush: false, mayAct: false });
    expect(query('log-retry-l1')).toBeNull();
    expect(retry.hasActableTarget).not.toHaveBeenCalled();
  });

  it('a row opens to the redacted request and response (loaded once), with copy', async () => {
    await setup();
    query('log-toggle-l1')!.querySelector('button')!.click();
    fixture.detectChanges();
    expect(service.getCallLogEntry).toHaveBeenCalledOnceWith('l1');
    const detail = query('log-detail-l1')!;
    expect(detail.textContent).toContain('"Amount": 10');
    expect(detail.textContent).toContain('"Fault"');
    expect(query('copy-response-l1')).not.toBeNull();
    if (navigator.clipboard) {   // a secure context (Karma on localhost) has one
      const clip = spyOn(navigator.clipboard, 'writeText').and.returnValue(Promise.resolve());
      query('copy-request-l1')!.querySelector('button')!.click();
      expect(clip).toHaveBeenCalledWith(JSON.stringify({ Line: [{ Amount: 10 }] }, null, 2));
    }

    component.toggle(component.rows[0]);   // closes
    component.toggle(component.rows[0]);   // opens again: cached
    expect(service.getCallLogEntry).toHaveBeenCalledTimes(1);
  });

  it('a failed body load says so', async () => {
    await setup();
    service.getCallLogEntry.and.returnValue(throwError(() => ({ status: 404 })));
    component.toggle(component.rows[0]);
    fixture.detectChanges();
    expect(query('log-detail-failed-l1')).not.toBeNull();
  });

  it('"Sync item" asks the page to open the Sync tab on that record', async () => {
    await setup();
    const opened: unknown[] = [];
    component.openSyncItem.subscribe(f => opened.push(f));
    query('log-item-l1')!.click();
    expect(opened).toEqual([{ kind: 'SalesInvoice', search: 'inv-1' }]);
  });

  it('empty and error states', async () => {
    await setup({ rows: [] });
    expect(query('log-empty')!.textContent).toContain('No failed call to QuickBooks');
    await setup({ fail: true });
    expect(query('log-error')!.textContent).toContain('Log down.');
  });

  // ── QA-7: Hide recovered ───────────────────────────────────────────────────

  const RECOVERED = () => [
    row(),                                                                     // Refused, record Failed now
    row({ id: 'r1', state: 'Synced', externalId: 'inv-5', displayLabel: 'SI-5' }),  // Refused, synced since
    row({ id: 'r2', outcome: 'Transient', state: 'Voided', externalId: 'inv-6' })    // voided since
  ];

  it('"Hide recovered" is on by default and asks the server for unresolved calls only', async () => {
    await setup();
    expect(component.hideRecovered).toBeTrue();
    expect(service.getCallLog).toHaveBeenCalledWith(jasmine.objectContaining({ unresolvedOnly: true, failedOnly: true }));
    await fixture.whenStable();   // ngModel writes the switch asynchronously
    fixture.detectChanges();
    expect((query('hide-recovered')!.querySelector('input') as HTMLInputElement).checked).toBeTrue();
  });

  it('the server does the filtering (D-69a): the page shows what it answers, and no chip while hiding', async () => {
    await setup({ rows: RECOVERED() });
    expect(component.rows.map(r => r.id)).toEqual(['l1', 'r1', 'r2']);
    expect(component.total).toBe(3);
    expect(query('log-recovered-r1')).toBeNull();
  });

  it('toggle off: every failed call, with a "Recovered" chip on those whose record has since synced or been voided', async () => {
    await setup({ rows: RECOVERED() });
    component.hideRecovered = false;
    component.reload();
    fixture.detectChanges();
    expect(service.getCallLog.calls.mostRecent().args[0]!.unresolvedOnly).toBeFalse();
    expect(component.rows.length).toBe(3);
    expect(query('log-recovered-r1')!.textContent).toContain('Recovered');
    expect(query('log-recovered-r2')).not.toBeNull();
    expect(query('log-recovered-l1')).toBeNull();
  });

  it('a succeeded call is never "recovered"', () => {
    expect(isRecovered({ outcome: 'Succeeded', state: 'Synced' })).toBeFalse();
    expect(isRecovered({ outcome: 'Refused', state: 'Synced' })).toBeTrue();
    expect(isRecovered({ outcome: 'Refused', state: 'Failed' })).toBeFalse();
    expect(isRecovered({ outcome: 'Transient', state: null })).toBeFalse();
  });

  it('outcome colours', () => {
    expect(outcomeSeverity('Succeeded')).toBe('success');
    expect(outcomeSeverity('DryRun')).toBe('info');
    expect(outcomeSeverity('Refused')).toBe('danger');
    expect(outcomeSeverity('AuthRevoked')).toBe('danger');
    expect(outcomeSeverity('Transient')).toBe('warn');
    expect(outcomeSeverity('Throttled')).toBe('warn');
  });
});
