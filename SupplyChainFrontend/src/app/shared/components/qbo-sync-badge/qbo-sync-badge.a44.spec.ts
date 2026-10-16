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
import { SyncKind } from '../../../models/quickbooks-integration.models';

const BASE = `${environment.apiUrl}/integrations/quickbooks`;
const ok = <T>(result: T) => ({ success: true, message: '', result });
const CONNECTED = { status: 'Live', isConnected: true, appConfigured: true, environment: 'Sandbox', mode: 'Live', reconnectSoon: false };
const POS_ALSO: SyncKind[] = ['SalesReceipt'];

@Component({
  standalone: true,
  imports: [QboSyncBadgeComponent],
  template: `
    <app-qbo-sync-badge kind="SalesInvoice" [alsoKinds]="also" externalId="inv-1" label="QuickBooks"></app-qbo-sync-badge>
    <app-qbo-sync-badge kind="Payment" externalId="pay-1"></app-qbo-sync-badge>
  `
})
class HostComponent {
  also: SyncKind[] = POS_ALSO;
}

/** A44 — badges for the new kinds, a POS invoice sent as a sales receipt, and records held by sync control. */
describe('QboSyncBadgeComponent — A44', () => {
  let fixture: ComponentFixture<HostComponent>;
  let http: HttpTestingController;

  function texts(): string[] {
    return fixture.debugElement.queryAll(By.css('[data-testid="qbo-sync-badge"]')).map(b => (b.nativeElement.textContent as string).trim());
  }

  function lookup(kind: string) {
    return http.expectOne(r => r.url === `${BASE}/status/lookup` && r.body.kind === kind);
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [
        provideHttpClient(), provideHttpClientTesting(), provideNoopAnimations(), MessageService,
        { provide: TenantService, useValue: { hasFeature: (c: string) => signal(['MODULE_INTEGRATION'])().includes(c) } },
        { provide: AuthService, useValue: { hasPermission: (c: string) => c === 'INTEGRATION_VIEW' } }
      ]
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  /** Inside fakeAsync: renders the badges, lets the batch window pass and answers the lookups. */
  function answer(invoice: object[], receipt: object[], payment: object[]) {
    fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    tick(QboSyncStatusStore.BATCH_WINDOW_MS);
    http.expectOne(`${BASE}/connection`).flush(ok(CONNECTED));
    lookup('SalesInvoice').flush(ok({ items: invoice }));
    lookup('SalesReceipt').flush(ok({ items: receipt }));
    lookup('Payment').flush(ok({ items: payment }));
    fixture.detectChanges();
  }

  it('shows the kind the gateway holds the record as (a POS sale sent as a sales receipt)', fakeAsync(() => {
    answer([], [{ kind: 'SalesReceipt', externalId: 'inv-1', state: 'Synced', remoteDocNumber: 'POS-1' }],
           [{ kind: 'Payment', externalId: 'pay-1', state: 'Failed', lastError: 'Invoice not synced' }]);
    expect(texts()).toEqual(['QuickBooks: Synced', 'Failed']);
    const badge = fixture.debugElement.queryAll(By.directive(QboSyncBadgeComponent))[0].componentInstance as QboSyncBadgeComponent;
    expect(badge.tooltip(badge.status()!)).toContain('Sent as a sales receipt.');
  }));

  it('falls back to the primary kind when neither is in the gateway', fakeAsync(() => {
    answer([], [], []);
    expect(texts()).toEqual(['QuickBooks: Not synced', 'Not synced']);
  }));

  it('a record held by sync control reads "Held"', fakeAsync(() => {
    answer([{ kind: 'SalesInvoice', externalId: 'inv-1', state: 'NotSynced', lastErrorCode: 'POS_SALES_HELD' }], [],
           [{ kind: 'Payment', externalId: 'pay-1', state: 'NotSynced', lastErrorCode: 'SYNC_HELD' }]);
    expect(texts()).toEqual(['QuickBooks: Held', 'Held']);
    const badge = fixture.debugElement.queryAll(By.directive(QboSyncBadgeComponent))[1].componentInstance as QboSyncBadgeComponent;
    expect(badge.severity(badge.status()!)).toBe('warn');
    expect(badge.tooltip(badge.status()!)).toContain('sync control');
  }));
});
