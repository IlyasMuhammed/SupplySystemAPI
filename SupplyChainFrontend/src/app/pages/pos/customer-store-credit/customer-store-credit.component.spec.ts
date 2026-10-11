import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { throwError } from 'rxjs';

import { CustomerStoreCreditComponent } from './customer-store-credit.component';
import { PosReturnsAdminService } from '../../../services/pos-returns-admin.service';
import { ok, storeCreditFixture } from '../pos-fixtures.testing';

/** A39 — the customer page's "Store credit" list with the spendable total. */
describe('CustomerStoreCreditComponent (A39)', () => {
  async function create(result: any) {
    const api = jasmine.createSpyObj<PosReturnsAdminService>('PosReturnsAdminService', ['getCustomerStoreCredits']);
    api.getCustomerStoreCredits.and.returnValue(result);
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [CustomerStoreCreditComponent],
      providers: [provideRouter([]), { provide: PosReturnsAdminService, useValue: api }]
    }).compileComponents();
    const fixture = TestBed.createComponent(CustomerStoreCreditComponent);
    fixture.componentInstance.customerUuid = 'c-1';
    fixture.componentInstance.ngOnChanges({ customerUuid: {} as any });
    fixture.detectChanges();
    return { fixture, api, el: fixture.nativeElement as HTMLElement };
  }

  it('lists the customer\'s store credit with balances and totals the active ones', async () => {
    const { api, el, fixture } = await create(ok([
      storeCreditFixture(), storeCreditFixture({ uuid: 'cn-2', number: 'CN-2', remainingBalance: 15.5 }),
      storeCreditFixture({ uuid: 'cn-3', number: 'CN-3', status: 'EXPIRED', remainingBalance: 30 })
    ]));
    expect(api.getCustomerStoreCredits).toHaveBeenCalledWith('c-1');
    expect(fixture.componentInstance.activeTotal).toBe(75.5);
    expect(el.querySelector('[data-testid="csc-total"]')!.textContent).toContain('75.50');
    expect(el.querySelector('[data-testid="csc-total"]')!.textContent).toContain('2 active of 3');
    expect(el.querySelectorAll('[data-testid="csc-row"]').length).toBe(3);
    expect(el.querySelector('a[href*="/portal/pages/pos/credit-notes/cn-1"]')).not.toBeNull();
  });

  it('a failure is shown', async () => {
    const { el } = await create(throwError(() => ({ status: 403, error: { message: 'Forbidden.' } })));
    expect(el.querySelector('[data-testid="csc-load-error"]')!.textContent).toContain('Forbidden.');
  });
});
