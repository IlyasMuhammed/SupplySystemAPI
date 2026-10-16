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
import { SyncKind } from '../../../models/quickbooks-integration.models';

const BASE = `${environment.apiUrl}/integrations/quickbooks`;
const POSTING = `${environment.apiUrl}/finance/posting`;
const ok = <T>(result: T) => ({ success: true, message: '', result });
const LIVE = { status: 'Live', isConnected: true, appConfigured: true, environment: 'Sandbox', mode: 'Live', reconnectSoon: false };

const POS_ALSO: SyncKind[] = ['SalesReceipt'];
const CREDIT_ALSO: SyncKind[] = ['RefundReceipt'];

@Component({
  standalone: true,
  imports: [QboSyncBadgeComponent],
  template: `<app-qbo-sync-badge [kind]="kind" [alsoKinds]="also" externalId="doc-1" label="QuickBooks"></app-qbo-sync-badge>`
})
class HostComponent {
  kind: SyncKind = 'SalesInvoice';
  also: SyncKind[] = POS_ALSO;
}

/** REV-20 — a POS document's badge acts on the kind it was posted as, never on a guessed SalesInvoice. */
describe('QboSyncBadgeComponent — POS kind (REV-20)', () => {
  let fixture: ComponentFixture<HostComponent>;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations(), MessageService,
        { provide: TenantService, useValue: { hasFeature: (c: string) => c === 'MODULE_INTEGRATION' } },
        { provide: AuthService, useValue: { hasPermission: (c: string) => ['INTEGRATION_VIEW', 'INTEGRATION_SYNC'].includes(c) } }
      ]
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  const action = () => fixture.nativeElement.querySelector('[data-testid="qbo-badge-action"]') as HTMLButtonElement | null;
  const badge = () => fixture.debugElement.query(By.directive(QboSyncBadgeComponent)).componentInstance as QboSyncBadgeComponent;

  /** Renders and answers the connection and both kinds' lookups. */
  function render(kind: SyncKind, also: SyncKind[], answers: Record<string, object[]>) {
    fixture = TestBed.createComponent(HostComponent);
    fixture.componentInstance.kind = kind;
    fixture.componentInstance.also = also;
    fixture.detectChanges();
    tick(QboSyncStatusStore.BATCH_WINDOW_MS);
    http.expectOne(`${BASE}/connection`).flush(ok(LIVE));
    for (const k of [kind, ...also]) {
      http.expectOne(r => r.url === `${BASE}/status/lookup` && r.body.kind === k).flush(ok({ items: answers[k] ?? [] }));
    }
    fixture.detectChanges();
    tick();
    fixture.detectChanges();
  }

  function bySource(postings: object[]) {
    http.expectOne(`${POSTING}/by-source/doc-1`).flush(ok(postings));
    fixture.detectChanges();
    tick();
    fixture.detectChanges();
  }

  function clickAndExpectPush(kind: SyncKind) {
    action()!.click();
    const push = http.expectOne(`${BASE}/sync/push`);
    expect(push.request.body).toEqual({ kind, externalIds: ['doc-1'] });
    push.flush(ok({ requested: 1 }));
    tick(QboSyncStatusStore.BATCH_WINDOW_MS);
    http.match(`${BASE}/status/lookup`).forEach(r => r.flush(ok({ items: [] })));
  }

  it('the gateway holds it as a SalesReceipt: Retry pushes SalesReceipt, without asking the posting log', fakeAsync(() => {
    render('SalesInvoice', POS_ALSO, { SalesReceipt: [{ kind: 'SalesReceipt', externalId: 'doc-1', state: 'Failed' }] });
    expect(badge().effectiveKind()).toBe('SalesReceipt');
    expect(action()!.textContent!.trim()).toBe('Retry');
    http.expectNone(r => r.url.startsWith(`${POSTING}/`));
    clickAndExpectPush('SalesReceipt');
  }));

  it('a held SalesReceipt counts as found', fakeAsync(() => {
    render('SalesInvoice', POS_ALSO, { SalesReceipt: [{ kind: 'SalesReceipt', externalId: 'doc-1', state: 'NotSynced', lastErrorCode: 'HELD' }] });
    expect(badge().effectiveKind()).toBe('SalesReceipt');
    expect(action()).toBeNull();   // held: nothing to send
  }));

  it('in the gateway under no kind: the posting says SalesReceipt → "Send to QuickBooks" pushes SalesReceipt', fakeAsync(() => {
    render('SalesInvoice', POS_ALSO, {});
    expect(action()).toBeNull();   // not before the kind is known
    bySource([{ uuid: 'p1', kind: 'Post', documentType: 'SalesReceipt', targets: [] }]);
    expect(badge().effectiveKind()).toBe('SalesReceipt');
    expect(action()!.textContent!.trim()).toBe('Send to QuickBooks');
    clickAndExpectPush('SalesReceipt');
  }));

  it('the posting says SalesInvoice (an on-account POS sale) → pushes SalesInvoice', fakeAsync(() => {
    render('SalesInvoice', POS_ALSO, {});
    bySource([{ uuid: 'p1', kind: 'Post', documentType: 'SalesInvoice', targets: [] }]);
    clickAndExpectPush('SalesInvoice');
  }));

  it('no posting, or an undecisive POSSale posting: no Send and no Push now (no duplicate Invoice)', fakeAsync(() => {
    render('SalesInvoice', POS_ALSO, {});
    bySource([]);
    expect(badge().effectiveKind()).toBeNull();
    expect(action()).toBeNull();
    expect(badge().menuItems().some(i => i.label === 'Push now')).toBeFalse();
    badge().pushNow();
    http.expectNone(`${BASE}/sync/push`);
  }));

  it('an undecisive POSSale posting: no Send', fakeAsync(() => {
    render('SalesInvoice', POS_ALSO, {});
    bySource([{ uuid: 'p1', kind: 'Post', documentType: 'POSSale', targets: [] }]);
    expect(action()).toBeNull();
  }));

  it('credit notes: CreditNote → CreditMemo, RefundReceipt → RefundReceipt', fakeAsync(() => {
    render('CreditMemo', CREDIT_ALSO, {});
    bySource([{ uuid: 'p1', kind: 'Post', documentType: 'CreditNote', targets: [] }]);
    clickAndExpectPush('CreditMemo');
  }));

  it('a cash refund posted as a RefundReceipt pushes RefundReceipt', fakeAsync(() => {
    render('CreditMemo', CREDIT_ALSO, {});
    bySource([{ uuid: 'p1', kind: 'Post', documentType: 'RefundReceipt', targets: [] }, { uuid: 'p2', kind: 'Void', documentType: 'RefundReceipt', targets: [] }]);
    clickAndExpectPush('RefundReceipt');
  }));
});
