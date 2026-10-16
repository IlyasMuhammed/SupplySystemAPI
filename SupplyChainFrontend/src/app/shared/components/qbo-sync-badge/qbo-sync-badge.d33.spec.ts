import { Component } from '@angular/core';
import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { By } from '@angular/platform-browser';
import { MessageService } from 'primeng/api';

import { QboSyncBadgeComponent } from './qbo-sync-badge.component';
import { QboSyncStatusStore } from './qbo-sync-status.store';
import { AuthService } from '../../../pages/service/auth.service';
import { TenantService } from '../../../pages/service/tenant.service';
import { environment } from '../../../../environments/environment';

const BASE = `${environment.apiUrl}/integrations/quickbooks`;
const POSTING = `${environment.apiUrl}/finance/posting`;
const ok = <T>(result: T) => ({ success: true, message: '', result });
const LIVE = { status: 'Live', isConnected: true, appConfigured: true, environment: 'Sandbox', mode: 'Live', reconnectSoon: false };

@Component({
  standalone: true,
  imports: [QboSyncBadgeComponent],
  template: `<app-qbo-sync-badge kind="SalesInvoice" externalId="inv-1" label="QuickBooks" [inlineAction]="inline"></app-qbo-sync-badge>`
})
class HostComponent { inline = true; }

/** A44 D-33 — the visible "Send to QuickBooks" / "Retry" button on the badge. */
describe('QboSyncBadgeComponent — inline retry (D-33)', () => {
  let fixture: ComponentFixture<HostComponent>;
  let http: HttpTestingController;
  let permissions: string[];
  let toasts: jasmine.Spy;

  beforeEach(() => {
    permissions = ['INTEGRATION_VIEW', 'INTEGRATION_SYNC'];
    TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations(), MessageService,
        { provide: TenantService, useValue: { hasFeature: (c: string) => c === 'MODULE_INTEGRATION' } },
        { provide: AuthService, useValue: { hasPermission: (c: string) => permissions.includes(c) } }
      ]
    });
    http = TestBed.inject(HttpTestingController);
    toasts = spyOn(TestBed.inject(MessageService), 'add');
  });

  afterEach(() => http.verify());

  function action(): HTMLButtonElement | null {
    return fixture.nativeElement.querySelector('[data-testid="qbo-badge-action"]');
  }

  /** Renders, answers the connection and the lookup (inside fakeAsync). */
  function render(status: object | null, connection: object = LIVE, inline = true) {
    fixture = TestBed.createComponent(HostComponent);
    fixture.componentInstance.inline = inline;
    fixture.detectChanges();
    tick(QboSyncStatusStore.BATCH_WINDOW_MS);
    http.expectOne(`${BASE}/connection`).flush(ok(connection));
    http.expectOne(`${BASE}/status/lookup`).flush(ok({ items: status ? [status] : [] }));
    fixture.detectChanges();
    tick();
    fixture.detectChanges();
  }

  it('"Send to QuickBooks" for a record the gateway never received', fakeAsync(() => {
    render(null);
    expect(action()!.textContent!.trim()).toBe('Send to QuickBooks');
  }));

  it('"Retry" for Failed, Blocked and NeedsResolution', fakeAsync(() => {
    render({ kind: 'SalesInvoice', externalId: 'inv-1', state: 'Blocked', lastError: 'Tax code GST17 is not mapped.' });
    expect(action()!.textContent!.trim()).toBe('Retry');
  }));

  it('nothing for a synced or held record', fakeAsync(() => {
    render({ kind: 'SalesInvoice', externalId: 'inv-1', state: 'Synced' });
    expect(action()).toBeNull();
  }));

  it('neither INTEGRATION_SYNC nor a posting code: hidden, and the posting log is not asked', fakeAsync(() => {
    permissions = ['INTEGRATION_VIEW'];
    render(null);
    expect(action()).toBeNull();
  }));

  it('nothing on a revoked connection', fakeAsync(() => {
    render(null, { ...LIVE, status: 'Revoked', isConnected: false });
    expect(fixture.nativeElement.querySelector('[data-testid="qbo-sync-badge"]')).not.toBeNull();
    expect(action()).toBeNull();
  }));

  it('nothing when the page turns the inline action off (list pages)', fakeAsync(() => {
    render(null, LIVE, false);
    expect(action()).toBeNull();
  }));

  it('REV-19 — INTEGRATION_MANAGE without SYNC: shown when a posting target has canAct; the click retries it, no push', fakeAsync(() => {
    permissions = ['INTEGRATION_VIEW', 'INTEGRATION_MANAGE'];
    render({ kind: 'SalesInvoice', externalId: 'inv-1', state: 'Failed' });
    http.expectOne(`${POSTING}/by-source/inv-1`).flush(ok([{ uuid: 'p1', targets: [{ targetCode: 'quickbooks', status: 'Failed', canAct: true }] }]));
    fixture.detectChanges();
    expect(action()!.textContent!.trim()).toBe('Retry');

    action()!.click();
    http.expectOne(`${POSTING}/by-source/inv-1`).flush(ok([{ uuid: 'p1', targets: [{ targetCode: 'quickbooks', status: 'Failed', canAct: true }] }]));
    http.expectOne(`${POSTING}/log/p1/retry`).flush(ok({ uuid: 'p1', targets: [] }));
    http.expectNone(`${BASE}/sync/push`);
    expect(toasts.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({ summary: 'Retried' }));
    tick(QboSyncStatusStore.BATCH_WINDOW_MS);
    http.expectOne(`${BASE}/status/lookup`).flush(ok({ items: [{ kind: 'SalesInvoice', externalId: 'inv-1', state: 'Pending' }] }));
  }));

  it('REV-19 — INTEGRATION_MANAGE without SYNC and nothing it may act on: hidden', fakeAsync(() => {
    permissions = ['INTEGRATION_VIEW', 'INTEGRATION_MANAGE'];
    render({ kind: 'SalesInvoice', externalId: 'inv-1', state: 'Failed' });
    http.expectOne(`${POSTING}/by-source/inv-1`).flush(ok([{ uuid: 'p1', targets: [{ targetCode: 'internal_gl', status: 'Failed', canAct: false }] }]));
    fixture.detectChanges();
    expect(action()).toBeNull();
  }));

  it('REV-19 — INTEGRATION_SYNC only: shown without asking the posting log', fakeAsync(() => {
    render({ kind: 'SalesInvoice', externalId: 'inv-1', state: 'Failed' });
    http.expectNone(r => r.url.startsWith(`${POSTING}/`));
    expect(action()!.textContent!.trim()).toBe('Retry');
  }));

  it('nothing to send (push notFound): an info toast, and the badge stays as it is', fakeAsync(() => {
    render(null);
    action()!.click();
    http.expectOne(`${BASE}/sync/push`).flush(ok({ requested: 0, skippedInMatching: [], notFound: ['inv-1'] }));
    expect(toasts.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({ severity: 'info', detail: 'Nothing to send for this document.' }));
    tick(QboSyncStatusStore.BATCH_WINDOW_MS);
    http.expectNone(`${BASE}/status/lookup`);
    fixture.detectChanges();
    expect(action()!.textContent!.trim()).toBe('Send to QuickBooks');
  }));

  it('a click retries the failed postings, pushes, toasts and looks the record up again', fakeAsync(() => {
    permissions = ['INTEGRATION_VIEW', 'INTEGRATION_SYNC', 'INTEGRATION_MANAGE', 'GL_SETUP_MANAGE'];
    render({ kind: 'SalesInvoice', externalId: 'inv-1', state: 'Failed' });
    action()!.click();
    http.expectOne(`${POSTING}/by-source/inv-1`).flush(ok([
      { uuid: 'p1', targets: [{ targetCode: 'quickbooks', status: 'Failed', canAct: true }] }
    ]));
    http.expectOne(`${POSTING}/log/p1/retry`).flush(ok({ uuid: 'p1', targets: [] }));
    http.expectOne(`${BASE}/sync/push`).flush(ok({ requested: 1 }));
    expect(toasts.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({ severity: 'success', summary: 'Queued for QuickBooks' }));
    expect(toasts.calls.mostRecent().args[0].detail).toContain('1 failed posting was retried first');

    tick(QboSyncStatusStore.BATCH_WINDOW_MS);
    http.expectOne(`${BASE}/status/lookup`).flush(ok({ items: [{ kind: 'SalesInvoice', externalId: 'inv-1', state: 'Pending' }] }));
    fixture.detectChanges();
    expect(action()).toBeNull();
  }));
});
