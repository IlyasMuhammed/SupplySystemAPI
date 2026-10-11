import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ActivatedRoute, convertToParamMap, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';

import { PosStoreCreditDetailComponent, txnRows } from './store-credit-detail.component';
import { PosReturnsAdminService } from '../../../services/pos-returns-admin.service';
import { PosAdminService } from '../../../services/pos-admin.service';
import { CustomerService } from '../../../services/customer.service';
import { AuthService } from '../../service/auth.service';
import { ModuleService } from '../../../services/module.service';
import { fail400, ok, storeCreditFixture, storeCreditTxnFixture } from '../pos-fixtures.testing';

const ALL = ['POS_CREDIT_NOTE_VIEW', 'POS_CREDIT_NOTE_CANCEL', 'POS_CREDIT_NOTE_ISSUE'];

/** A39 — `/pos/credit-notes/:uuid`: balance, transactions with order links, Cancel (note) and Issue gated by permission. */
describe('PosStoreCreditDetailComponent (A39)', () => {
  let fixture: ComponentFixture<PosStoreCreditDetailComponent>;
  let component: PosStoreCreditDetailComponent;
  let el: HTMLElement;
  let api: jasmine.SpyObj<PosReturnsAdminService>;

  const detail = (over: any = {}) => ({
    ...storeCreditFixture({ expiresAt: '2027-04-10T00:00:00' }),
    transactions: [
      storeCreditTxnFixture({ uuid: 't-2', type: 'REDEEM', amount: -40, balanceAfter: 60, orderUuid: 'o-9', orderNumber: 'POS-S2-0009',
                              storeName: 'Airport', performedAt: '2026-10-11T12:00:00' }),
      storeCreditTxnFixture()
    ],
    ...over
  });

  async function setup(credit: any = detail(), perms = ALL, features = ['FEATURE_POS_CREDIT_NOTES']) {
    api = jasmine.createSpyObj<PosReturnsAdminService>('PosReturnsAdminService', ['getStoreCredit', 'getStoreCreditTransactions', 'cancelStoreCredit']);
    api.getStoreCredit.and.returnValue(ok(credit));
    api.getStoreCreditTransactions.and.returnValue(ok([storeCreditTxnFixture()]));
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [PosStoreCreditDetailComponent],
      providers: [
        provideNoopAnimations(), provideRouter([]),
        { provide: ActivatedRoute, useValue: { paramMap: of(convertToParamMap({ uuid: 'cn-1' })) } },
        { provide: PosReturnsAdminService, useValue: api },
        { provide: PosAdminService, useValue: { getStores: () => ok({ data: [], totalRecords: 0 }) } },
        { provide: CustomerService, useValue: { getCustomers: () => ok({ data: [], totalRecords: 0 }) } },
        { provide: ModuleService, useValue: { isFeatureEnabled: (c: string) => features.includes(c) } },
        { provide: AuthService, useValue: { hasPermission: (c: string) => perms.includes(c), hasAnyPermission: (...c: string[]) => c.some(x => perms.includes(x)) } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(PosStoreCreditDetailComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    el = fixture.nativeElement;
  }

  const q = (id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  it('shows number, status, balance, expiry and the transactions oldest first with order links', async () => {
    await setup();
    expect(api.getStoreCredit).toHaveBeenCalledWith('cn-1');
    expect(q('credit-title')!.textContent).toContain('CN-S1-20261010-0001');
    expect(q('credit-status')!.textContent).toContain('Active');
    expect(q('kpi-balance')!.textContent).toContain('60.00');
    expect(q('kpi-expiry')!.textContent).toContain('Apr 2027');
    expect(q('return-link')!.getAttribute('href')).toContain('/portal/pages/pos/orders/r-1');
    expect(component.txns.map(t => t.typeLabel)).toEqual(['Issue', 'Spent']);
    const table = q('txn-table')!;
    expect(table.textContent).toContain('POS-S2-0009');
    expect(table.textContent).toContain('Airport');
    expect(table.querySelector('a[href*="/portal/pages/pos/orders/o-9"]')).not.toBeNull();
    expect(q('customer-ref')!.querySelector('a')!.getAttribute('href')).toContain('/portal/pages/customers/c-1');
    expect(api.getStoreCreditTransactions).not.toHaveBeenCalled();
  });

  it('a detail without transactions loads them from their endpoint; a bearer credit says so', async () => {
    const plain: any = detail({ customerUuid: null, customerName: null });
    delete plain.transactions;
    await setup(plain);
    expect(api.getStoreCreditTransactions).toHaveBeenCalledWith('cn-1');
    expect(component.txns.length).toBe(1);
    expect(q('customer-ref')!.textContent).toContain('Bearer');
  });

  it('Cancel: only with POS_CREDIT_NOTE_CANCEL on an ACTIVE credit; note sent; refusal verbatim', async () => {
    await setup();
    expect(q('cancel-credit')).not.toBeNull();
    component.openCancel();
    component.cancelNote = ' Customer fraud ';
    api.cancelStoreCredit.and.returnValue(fail400('Only active store credit can be cancelled.'));
    component.confirmCancel();
    expect(api.cancelStoreCredit).toHaveBeenCalledWith('cn-1', 'Customer fraud');
    expect(component.cancelError).toBe('Only active store credit can be cancelled.');
    api.cancelStoreCredit.and.returnValue(ok(storeCreditFixture({ status: 'CANCELLED' })));
    api.getStoreCredit.and.returnValue(ok(detail({ status: 'CANCELLED', remainingBalance: 0 })));
    component.confirmCancel();
    fixture.detectChanges();
    expect(component.cancelDialog).toBeFalse();
    expect(q('credit-status')!.textContent).toContain('Cancelled');
    expect(q('cancel-credit')).toBeNull();

    await setup(detail(), ['POS_CREDIT_NOTE_VIEW']);
    expect(q('cancel-credit')).toBeNull();
    expect(q('issue-credit')).toBeNull();
  });

  it('Issue needs the permission and the feature', async () => {
    await setup(detail(), ALL, []);
    expect(q('issue-credit')).toBeNull();
    await setup();
    expect(q('issue-credit')).not.toBeNull();
  });

  it('a missing store credit shows the error', async () => {
    await setup();
    api.getStoreCredit.and.returnValue(throwError(() => ({ status: 404, error: {} })));
    component.load();
    fixture.detectChanges();
    expect(q('credit-load-error')!.textContent).toContain('Store credit not found.');
  });

  it('txnRows sorts by time and labels REDEEM as Spent, ADJUST (void restore) as Restored', () => {
    const rows = txnRows([storeCreditTxnFixture({ type: 'EXPIRE', performedAt: '2027-01-01' }), storeCreditTxnFixture(),
                          storeCreditTxnFixture({ type: 'ADJUST', amount: 40, performedAt: '2026-11-01' })]);
    expect(rows.map(r => r.type)).toEqual(['ISSUE', 'ADJUST', 'EXPIRE']);
    expect(rows[1].typeLabel).toBe('Restored (sale voided)');
    expect(rows[2].tone).toBe('er');
  });
});
