import { Component, signal } from '@angular/core';
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
import { SyncStatus } from '../../../models/quickbooks-integration.models';

const BASE = `${environment.apiUrl}/integrations/quickbooks`;
const WINDOW = QboSyncStatusStore.BATCH_WINDOW_MS;

function ok<T>(result: T) {
  return { success: true, message: '', result };
}

const CONNECTED = { status: 'Live', isConnected: true, appConfigured: true, environment: 'Sandbox', mode: 'Live', reconnectSoon: false };

@Component({
  standalone: true,
  imports: [QboSyncBadgeComponent],
  template: `
    @for (id of customerIds; track id) {
      <app-qbo-sync-badge kind="Customer" [externalId]="id"></app-qbo-sync-badge>
    }
    <app-qbo-sync-badge kind="Customer" [externalId]="customerIds[0]" label="Customer"></app-qbo-sync-badge>
    @for (id of itemIds; track id) {
      <app-qbo-sync-badge kind="Item" [externalId]="id"></app-qbo-sync-badge>
    }
  `
})
class HostComponent {
  customerIds = ['P-1', 'p-2', 'p-3'];
  itemIds = ['v-1', 'v-2'];
}

describe('QboSyncBadgeComponent with QboSyncStatusStore', () => {
  let fixture: ComponentFixture<HostComponent>;
  let http: HttpTestingController;
  let features: ReturnType<typeof signal<string[]>>;
  let permissions: string[];
  let toasts: jasmine.Spy;

  function badges() {
    return fixture.debugElement.queryAll(By.css('[data-testid="qbo-sync-badge"]'));
  }

  function badgeTexts(): string[] {
    return badges().map(b => (b.nativeElement.textContent as string).trim());
  }

  function lookups() {
    return http.match(r => r.url === `${BASE}/status/lookup`);
  }

  function setup(opts: { customerIds?: string[]; itemIds?: string[] } = {}) {
    TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations(), MessageService,
        { provide: TenantService, useValue: { hasFeature: (c: string) => features().includes(c) } },
        { provide: AuthService, useValue: { hasPermission: (c: string) => permissions.includes(c) } }
      ]
    });
    http = TestBed.inject(HttpTestingController);
    toasts = spyOn(TestBed.inject(MessageService), 'add');
    fixture = TestBed.createComponent(HostComponent);
    if (opts.customerIds) fixture.componentInstance.customerIds = opts.customerIds;
    if (opts.itemIds) fixture.componentInstance.itemIds = opts.itemIds;
    fixture.detectChanges();
  }

  /** Lets the batch window pass and answers "is there a connection?". */
  function flushWindow(connection: object | null = CONNECTED) {
    tick(WINDOW);
    const conn = http.match(`${BASE}/connection`);
    if (connection) conn.forEach(r => r.flush(ok(connection)));
    else conn.forEach(r => r.flush({ message: 'boom' }, { status: 500, statusText: 'Server Error' }));
    return conn.length;
  }

  beforeEach(() => {
    features = signal<string[]>(['MODULE_INTEGRATION']);
    permissions = ['INTEGRATION_VIEW'];
  });

  afterEach(() => http.verify());

  it('makes ONE status lookup per kind for every badge on the page, and shows each state', fakeAsync(() => {
    setup();
    expect(lookups().length).toBe(0);   // nothing goes out before the window closes

    expect(flushWindow()).toBe(1);      // one connection check for the whole page

    const reqs = lookups();
    expect(reqs.length).toBe(2);
    const customer = reqs.find(r => r.request.body.kind === 'Customer')!;
    const item = reqs.find(r => r.request.body.kind === 'Item')!;
    // Four customer badges, three distinct ids (the same id asked twice is asked once).
    expect(customer.request.body.externalIds).toEqual(['P-1', 'p-2', 'p-3']);
    expect(item.request.body.externalIds).toEqual(['v-1', 'v-2']);

    customer.flush(ok({ items: [
      { kind: 'Customer', externalId: 'p-1', state: 'Synced', deepLink: 'https://app.sandbox.qbo.intuit.com/app/customerdetail?nameId=58' },
      { kind: 'Customer', externalId: 'p-2', state: 'Blocked', lastError: 'Name is 124 characters; QuickBooks allows 100.' }
    ] }));
    item.flush(ok({ items: [
      { kind: 'Item', externalId: 'v-1', state: 'DryRunOk' },
      { kind: 'Item', externalId: 'v-2', state: 'WaitingOnDependency' }
    ] }));
    fixture.detectChanges();

    // p-3 was not in the answer: the gateway has never been given it.
    expect(badgeTexts()).toEqual(['Synced', 'Blocked', 'Not synced', 'Customer: Synced', 'Dry run OK', 'Waiting']);

    const states = badges().map(b => b.nativeElement.getAttribute('data-state'));
    expect(states).toEqual(['Synced', 'Blocked', 'NotSynced', 'Synced', 'DryRunOk', 'WaitingOnDependency']);

    const tags = fixture.debugElement.queryAll(By.css('p-tag'));
    expect(tags[0].componentInstance.severity).toBe('success');
    expect(tags[1].componentInstance.severity).toBe('danger');
    expect(tags[2].componentInstance.severity).toBe('secondary');
    expect(tags[4].componentInstance.styleClass).toContain('qbo-tone-teal');
    expect(tags[5].componentInstance.severity).toBe('warn');
  }));

  it('puts the last error in the tooltip', fakeAsync(() => {
    setup({ customerIds: ['p-2'], itemIds: [] });
    flushWindow();
    lookups()[0].flush(ok({ items: [{ kind: 'Customer', externalId: 'p-2', state: 'Failed', lastError: 'Duplicate name.' }] }));
    fixture.detectChanges();

    const badge = fixture.debugElement.query(By.directive(QboSyncBadgeComponent)).componentInstance as QboSyncBadgeComponent;
    expect(badge.tooltip(badge.status()!)).toContain('Duplicate name.');
  }));

  it('renders nothing, and asks nothing, when the organization has the feature off', fakeAsync(() => {
    features.set([]);
    setup();
    tick(WINDOW);
    expect(http.match(() => true).length).toBe(0);
    expect(badges().length).toBe(0);
  }));

  it('renders nothing, and asks nothing, without INTEGRATION_VIEW', fakeAsync(() => {
    permissions = ['SUPPLIER_VIEW'];
    setup();
    tick(WINDOW);
    expect(http.match(() => true).length).toBe(0);
    expect(badges().length).toBe(0);
  }));

  it('appears once the tenant (and its features) arrives after the page', fakeAsync(() => {
    features.set([]);
    setup({ itemIds: [] });
    tick(WINDOW);
    expect(badges().length).toBe(0);

    features.set(['MODULE_INTEGRATION']);
    fixture.detectChanges();
    flushWindow();
    lookups()[0].flush(ok({ items: [] }));
    fixture.detectChanges();
    expect(badges().length).toBe(4);
  }));

  it('renders nothing when the lookup fails, and the host page is unharmed', fakeAsync(() => {
    setup();
    flushWindow();
    lookups().forEach(r => r.flush({ message: 'boom' }, { status: 500, statusText: 'Server Error' }));
    fixture.detectChanges();
    expect(badges().length).toBe(0);
    expect(fixture.nativeElement).toBeTruthy();
  }));

  it('renders nothing, and makes no lookup, for an organization that never connected QuickBooks', fakeAsync(() => {
    setup();
    flushWindow({ ...CONNECTED, status: 'NotConnected', isConnected: false });
    expect(lookups().length).toBe(0);
    fixture.detectChanges();
    expect(badges().length).toBe(0);
  }));

  it('still shows states for a revoked connection', fakeAsync(() => {
    setup({ customerIds: ['p-1'], itemIds: [] });
    flushWindow({ ...CONNECTED, status: 'Revoked', isConnected: false });
    lookups()[0].flush(ok({ items: [{ kind: 'Customer', externalId: 'p-1', state: 'Synced' }] }));
    fixture.detectChanges();
    expect(badgeTexts()[0]).toBe('Synced');
  }));

  it('renders nothing when even the connection check fails', fakeAsync(() => {
    setup();
    flushWindow(null);
    expect(lookups().length).toBe(0);
    fixture.detectChanges();
    expect(badges().length).toBe(0);
  }));

  it('offers "View in QuickBooks" when there is a deep link, and "Push now" only with INTEGRATION_SYNC', fakeAsync(() => {
    setup({ customerIds: ['p-1'], itemIds: [] });
    flushWindow();
    lookups()[0].flush(ok({ items: [{ kind: 'Customer', externalId: 'p-1', state: 'Synced', deepLink: 'https://qbo/x' }] }));
    fixture.detectChanges();

    const badge = fixture.debugElement.query(By.directive(QboSyncBadgeComponent)).componentInstance as QboSyncBadgeComponent;
    expect(badge.menuItems().map(i => i.label)).toEqual(['View in QuickBooks']);
    expect(badge.menuItems()[0].url).toBe('https://qbo/x');
    expect(badge.menuItems()[0].target).toBe('_blank');

    permissions = ['INTEGRATION_VIEW', 'INTEGRATION_SYNC'];
    // menuItems is computed from the status; a new status recomputes it with the new permission.
    TestBed.inject(QboSyncStatusStore).refresh('Customer', 'p-1');
    flushWindow();
    lookups()[0].flush(ok({ items: [{ kind: 'Customer', externalId: 'p-1', state: 'Pending', deepLink: 'https://qbo/x' }] }));
    fixture.detectChanges();
    expect(badge.menuItems().map(i => i.label)).toEqual(['View in QuickBooks', 'Push now']);
  }));

  it('"Push now" posts the record to /sync/push, says so, and looks the state up again', fakeAsync(() => {
    permissions = ['INTEGRATION_VIEW', 'INTEGRATION_SYNC'];
    setup({ customerIds: ['p-1'], itemIds: [] });
    flushWindow();
    lookups()[0].flush(ok({ items: [{ kind: 'Customer', externalId: 'p-1', state: 'Failed' }] }));
    fixture.detectChanges();

    const badge = fixture.debugElement.query(By.directive(QboSyncBadgeComponent)).componentInstance as QboSyncBadgeComponent;
    expect(badge.menuItems().some(i => i.label === 'Push now')).toBeTrue();
    badge.menuItems().find(i => i.label === 'Push now')!.command!({} as any);

    const push = http.expectOne(`${BASE}/sync/push`);
    expect(push.request.method).toBe('POST');
    expect(push.request.body).toEqual({ kind: 'Customer', externalIds: ['p-1'] });
    push.flush(ok({ requested: 1 }));
    expect(toasts).toHaveBeenCalledWith(jasmine.objectContaining({ severity: 'success' }));

    tick(WINDOW);   // the connection answer is reused within its time to live
    const again = lookups();
    expect(again.length).toBe(1);
    expect(again[0].request.body).toEqual({ kind: 'Customer', externalIds: ['p-1'] });
    again[0].flush(ok({ items: [{ kind: 'Customer', externalId: 'p-1', state: 'Pending' }] }));
    fixture.detectChanges();
    // Both badges for this record (the plain one and the labelled one) share the one answer.
    expect(badgeTexts()).toEqual(['Pending', 'Customer: Pending']);
  }));

  it('"Push now" reports a refusal and keeps the badge', fakeAsync(() => {
    permissions = ['INTEGRATION_VIEW', 'INTEGRATION_SYNC'];
    setup({ customerIds: ['p-1'], itemIds: [] });
    flushWindow();
    lookups()[0].flush(ok({ items: [{ kind: 'Customer', externalId: 'p-1', state: 'Failed' }] }));
    fixture.detectChanges();

    const badge = fixture.debugElement.query(By.directive(QboSyncBadgeComponent)).componentInstance as QboSyncBadgeComponent;
    badge.pushNow();
    http.expectOne(`${BASE}/sync/push`).flush({ success: false, message: 'Not connected.' }, { status: 409, statusText: 'Conflict' });

    expect(toasts).toHaveBeenCalledWith(jasmine.objectContaining({ severity: 'error', detail: 'Not connected.' }));
    fixture.detectChanges();
    expect(badgeTexts()).toEqual(['Failed', 'Customer: Failed']);
    expect(lookups().length).toBe(0);   // nothing is looked up again after a refused push
  }));

  it('does not push without INTEGRATION_SYNC', fakeAsync(() => {
    setup({ customerIds: ['p-1'], itemIds: [] });
    flushWindow();
    lookups()[0].flush(ok({ items: [] }));
    fixture.detectChanges();

    const badge = fixture.debugElement.query(By.directive(QboSyncBadgeComponent)).componentInstance as QboSyncBadgeComponent;
    badge.pushNow();
    expect(http.match(`${BASE}/sync/push`).length).toBe(0);
  }));
});

describe('QboSyncStatusStore', () => {
  let http: HttpTestingController;
  let store: QboSyncStatusStore;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(), provideHttpClientTesting(),
        { provide: TenantService, useValue: { hasFeature: () => true } },
        { provide: AuthService, useValue: { hasPermission: () => true } }
      ]
    });
    http = TestBed.inject(HttpTestingController);
    store = TestBed.inject(QboSyncStatusStore);
  });

  afterEach(() => http.verify());

  it('reuses an answer within its time to live instead of asking again', fakeAsync(() => {
    const seen: (SyncStatus | null)[] = [];
    store.status('Bill', 'b-1').subscribe(s => seen.push(s));
    tick(WINDOW);
    http.expectOne(`${BASE}/connection`).flush(ok(CONNECTED));
    http.expectOne(`${BASE}/status/lookup`).flush(ok({ items: [{ kind: 'Bill', externalId: 'b-1', state: 'Synced' }] }));

    store.status('Bill', 'B-1').subscribe(s => seen.push(s));
    tick(WINDOW);
    http.expectNone(`${BASE}/status/lookup`);
    expect(seen.filter(Boolean).map(s => s!.state)).toEqual(['Synced', 'Synced']);
  }));

  it('asks again once the answer is older than its time to live', fakeAsync(() => {
    store.status('Bill', 'b-1').subscribe();
    tick(WINDOW);
    http.expectOne(`${BASE}/connection`).flush(ok(CONNECTED));
    http.expectOne(`${BASE}/status/lookup`).flush(ok({ items: [] }));

    const later = Date.now() + QboSyncStatusStore.STATUS_TTL_MS + QboSyncStatusStore.CONNECTION_TTL_MS + 1;
    spyOn(Date, 'now').and.returnValue(later);
    store.status('Bill', 'b-1').subscribe();
    tick(WINDOW);
    http.expectOne(`${BASE}/connection`).flush(ok(CONNECTED));
    http.expectOne(`${BASE}/status/lookup`).flush(ok({ items: [] }));
  }));

  it('answers null for a missing kind or id without asking', fakeAsync(() => {
    let value: SyncStatus | null | undefined;
    store.status('Customer', '').subscribe(v => (value = v));
    store.status(null, 'x').subscribe();
    tick(WINDOW);
    expect(value).toBeNull();
    http.expectNone(() => true);
  }));
});

describe('QboSyncStatusStore without an HTTP client', () => {
  it('is simply unavailable instead of breaking the page that holds a badge', () => {
    TestBed.configureTestingModule({
      providers: [{ provide: AuthService, useValue: { hasPermission: () => true } }]
    });
    const store = TestBed.inject(QboSyncStatusStore);
    expect(store.isAvailable()).toBeFalse();
    let value: SyncStatus | null | undefined;
    store.status('Customer', 'p-1').subscribe(v => (value = v));
    expect(value).toBeNull();
  });
});
