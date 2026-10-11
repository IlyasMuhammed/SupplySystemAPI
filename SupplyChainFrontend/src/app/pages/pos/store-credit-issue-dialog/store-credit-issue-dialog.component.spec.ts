import { TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';

import { StoreCreditIssueDialogComponent } from './store-credit-issue-dialog.component';
import { PosReturnsAdminService } from '../../../services/pos-returns-admin.service';
import { PosAdminService } from '../../../services/pos-admin.service';
import { CustomerService } from '../../../services/customer.service';
import { AuthService } from '../../service/auth.service';
import { fail400, ok, storeCreditFixture, storeFixture } from '../pos-fixtures.testing';

/** A39 — Issue store credit: store + amount required, customer optional (bearer), expiry as yyyy-MM-dd, refusal verbatim. */
describe('StoreCreditIssueDialogComponent (A39)', () => {
  let api: jasmine.SpyObj<PosReturnsAdminService>;

  async function create(perms = ['CUSTOMER_VIEW']) {
    api = jasmine.createSpyObj<PosReturnsAdminService>('PosReturnsAdminService', ['issueStoreCredit']);
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [StoreCreditIssueDialogComponent],
      providers: [
        provideNoopAnimations(),
        { provide: PosReturnsAdminService, useValue: api },
        { provide: PosAdminService, useValue: { getStores: () => ok({ data: [storeFixture()], totalRecords: 1 }) } },
        { provide: CustomerService, useValue: { getCustomers: () => ok({ data: [{ uuid: 'c-1', name: 'Acme', code: 'C-1' }], totalRecords: 1 }) } },
        { provide: AuthService, useValue: { hasPermission: (c: string) => perms.includes(c) } }
      ]
    }).compileComponents();
    const fixture = TestBed.createComponent(StoreCreditIssueDialogComponent);
    const c = fixture.componentInstance;
    c.storePreset = 's-1';
    c.visible = true;
    c.ngOnChanges({ visible: {} as any });
    fixture.detectChanges();
    return { fixture, c };
  }

  it('opens with the presets and the store / customer options; needs a store and an amount', async () => {
    const { c } = await create();
    expect(c.storeUuid).toBe('s-1');
    expect(c.storeOptions.length).toBe(1);
    expect(c.customerOptions.length).toBe(1);
    expect(c.canSave).toBeFalse();
    c.amount = 25;
    expect(c.canSave).toBeTrue();
  });

  it('POSTs the store credit (bearer when no customer) and emits it; a refusal stays in the dialog', async () => {
    const { c } = await create([]);
    expect(c.customerOptions.length).toBe(0);   // no CUSTOMER_VIEW
    const issued = jasmine.createSpy('issued');
    c.issued.subscribe(issued);
    c.amount = 50;
    c.expiresAt = new Date(2027, 0, 31);
    c.note = ' Goodwill ';
    api.issueStoreCredit.and.returnValue(fail400('Store credit is switched off for this organization.'));
    c.save();
    const body = api.issueStoreCredit.calls.mostRecent().args[0];
    expect(body).toEqual(jasmine.objectContaining({ storeUuid: 's-1', customerUuid: null, amount: 50, expiresAt: '2027-01-31', note: 'Goodwill' }));
    expect(body.localId).toBeTruthy();
    expect(c.error).toBe('Store credit is switched off for this organization.');
    expect(c.visible).toBeTrue();

    api.issueStoreCredit.and.returnValue(ok(storeCreditFixture({ customerUuid: null })));
    c.save();
    expect(issued).toHaveBeenCalled();
    expect(c.visible).toBeFalse();
  });
});
