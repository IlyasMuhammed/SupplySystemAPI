import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';

import { PosStoreCreditListComponent, storeCreditRow } from './store-credit-list.component';
import { PosReturnsAdminService } from '../../../services/pos-returns-admin.service';
import { PosAdminService } from '../../../services/pos-admin.service';
import { CustomerService } from '../../../services/customer.service';
import { AuthService } from '../../service/auth.service';
import { ModuleService } from '../../../services/module.service';
import { ok, storeCreditFixture, storeFixture } from '../pos-fixtures.testing';

/** A39 — `/pos/credit-notes`: store credit list with filters, balance / expiry / status, Issue gated by permission + feature. */
describe('PosStoreCreditListComponent (A39)', () => {
  let fixture: ComponentFixture<PosStoreCreditListComponent>;
  let component: PosStoreCreditListComponent;
  let el: HTMLElement;
  let api: jasmine.SpyObj<PosReturnsAdminService>;

  const soon = () => new Date(Date.now() + 5 * 24 * 3600 * 1000).toISOString();

  async function setup(perms = ['POS_CREDIT_NOTE_VIEW', 'POS_CREDIT_NOTE_ISSUE', 'CUSTOMER_VIEW'], features = ['FEATURE_POS_CREDIT_NOTES'],
                       query: Record<string, string> = {}) {
    api = jasmine.createSpyObj<PosReturnsAdminService>('PosReturnsAdminService', ['getStoreCredits', 'issueStoreCredit']);
    api.getStoreCredits.and.returnValue(ok({ data: [
      storeCreditFixture({ expiresAt: soon() }),
      storeCreditFixture({ uuid: 'cn-2', number: 'CN-S1-20261010-0002', customerUuid: null, customerName: null, returnOrderUuid: null,
                           returnOrderNumber: null, status: 'FULLY_REDEEMED', remainingBalance: 0 })
    ], totalRecords: 2 }));
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [PosStoreCreditListComponent],
      providers: [
        provideNoopAnimations(), provideRouter([]),
        { provide: ActivatedRoute, useValue: { snapshot: { queryParamMap: convertToParamMap(query) } } },
        { provide: PosReturnsAdminService, useValue: api },
        { provide: PosAdminService, useValue: { getStores: () => ok({ data: [storeFixture()], totalRecords: 1 }) } },
        { provide: CustomerService, useValue: { getCustomers: () => ok({ data: [{ uuid: 'c-1', name: 'Acme', code: 'C-1' }], totalRecords: 1 }) } },
        { provide: ModuleService, useValue: { isFeatureEnabled: (c: string) => features.includes(c) } },
        { provide: AuthService, useValue: { hasPermission: (c: string) => perms.includes(c), hasAnyPermission: (...c: string[]) => c.some(x => perms.includes(x)) } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(PosStoreCreditListComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    el = fixture.nativeElement;
  }

  const q = (id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;
  const all = (id: string) => Array.from(el.querySelectorAll(`[data-testid="${id}"]`)) as HTMLElement[];

  it('lists store credit with holder, balance, expiry and status; says "Store credit" not credit note', async () => {
    await setup();
    expect(api.getStoreCredits).toHaveBeenCalled();
    expect(el.textContent).toContain('Store credit');
    expect(el.textContent).toContain('not Finance credit notes');
    const rows = all('credit-row');
    expect(rows.length).toBe(2);
    expect(rows[0].textContent).toContain('CN-S1-20261010-0001');
    expect(rows[0].textContent).toContain('Acme');
    expect(rows[0].textContent).toContain('RET-S1-20261010-0001');
    expect(all('balance')[0].textContent).toContain('60.00');
    expect(all('expiry')[0].textContent).toContain('in 5 days');
    expect(rows[1].textContent).toContain('Bearer');
    expect(rows[1].textContent).toContain('Issued directly');
    expect(all('status').map(s => s.textContent!.trim())).toEqual(['Active', 'Used up']);
    expect(component.storeOptions.length).toBe(1);
    expect(component.customerOptions.length).toBe(1);
  });

  it('filters: status chip, store, customer, search go to the API; query params preset them', async () => {
    await setup(undefined, undefined, { customer: 'c-1', status: 'ACTIVE' });
    expect(api.getStoreCredits).toHaveBeenCalledWith(jasmine.objectContaining({ customerUuid: 'c-1', status: 'ACTIVE' }));
    (q('status-chip-EXPIRED') as HTMLButtonElement).click();
    expect(api.getStoreCredits.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({ status: 'EXPIRED', page: 1 }));
    component.storeUuid = 's-1';
    component.search = ' CN-S1 ';
    component.applyFilters();
    expect(api.getStoreCredits.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({ storeUuid: 's-1', search: 'CN-S1' }));
    component.resetFilters();
    expect(api.getStoreCredits.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({ status: '', storeUuid: null, customerUuid: null, search: null }));
  });

  it('Issue needs POS_CREDIT_NOTE_ISSUE and FEATURE_POS_CREDIT_NOTES; feature off shows a note', async () => {
    await setup();
    expect(q('issue-credit')).not.toBeNull();
    expect(q('feature-off')).toBeNull();
    await setup(['POS_CREDIT_NOTE_VIEW']);
    expect(q('issue-credit')).toBeNull();
    await setup(undefined, []);
    expect(q('issue-credit')).toBeNull();
    expect(q('feature-off')).not.toBeNull();
  });

  it('issuing opens the new store credit', async () => {
    await setup();
    const nav = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    component.onIssued(storeCreditFixture({ uuid: 'cn-9', number: 'CN-S1-9' }));
    expect(nav).toHaveBeenCalledWith(['/portal/pages/pos/credit-notes', 'cn-9']);
  });

  it('row helper: expired / cancelled have no countdown; never expires', () => {
    const now = Date.parse('2026-10-11T00:00:00Z');
    expect(storeCreditRow(storeCreditFixture({ expiresAt: null }), now).expiryLabel).toBe('Never');
    expect(storeCreditRow(storeCreditFixture({ expiresAt: '2026-10-12T00:00:00Z' }), now).expiringSoon).toBeTrue();
    expect(storeCreditRow(storeCreditFixture({ status: 'EXPIRED', expiresAt: '2026-10-01T00:00:00Z' }), now).expiryLabel).toBe('');
    expect(storeCreditRow(storeCreditFixture({ status: 'CANCELLED' }), now).statusTone).toBe('er');
  });
});
