import { Component, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { of, throwError } from 'rxjs';

import { MoneyPipe } from './money.pipe';
import { OrgCurrencyService } from '../../services/org-currency.service';
import { TenantService } from '../../pages/service/tenant.service';

const JPY_ID = '22222222-2222-2222-2222-222222222222';

@Component({
  standalone: true,
  imports: [MoneyPipe],
  template: `<span data-testid="a">{{ amount() | money:cur() }}</span><span data-testid="b">{{ 6000 | money:'AED':{ display: 'code' } }}</span>`
})
class HostComponent {
  amount = signal<number | null>(1234);
  cur = signal<string | null>(JPY_ID);
}

/** A35 D-21 — `{{ amount | money:currencyIdOrCode[:options] }}`; the org currency list loads once, by itself. */
describe('MoneyPipe', () => {
  let getCurrencies: jasmine.Spy;

  beforeEach(async () => {
    getCurrencies = jasmine.createSpy('getCurrencies').and.returnValue(of({ success: true, message: '', result: [
      { currencyId: JPY_ID, code: 'JPY', name: 'Japanese Yen', symbol: '¥', decimalPlaces: 0, rounding: 1, symbolPosition: 'before', isActive: true, displayOrder: 1 }
    ] }));
    await TestBed.configureTestingModule({
      imports: [HostComponent],
      providers: [provideHttpClient(), { provide: OrgCurrencyService, useValue: { getCurrencies } }]
    }).compileComponents();
  });

  it('formats with the org currency and loads the list only once', () => {
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    fixture.detectChanges();
    const el: HTMLElement = fixture.nativeElement;
    expect(el.querySelector('[data-testid="a"]')!.textContent).toBe('¥1,234');
    expect(el.querySelector('[data-testid="b"]')!.textContent).toBe('AED 6,000.00');
    expect(getCurrencies).toHaveBeenCalledTimes(1);
  });

  it('REV-04: the tenant arrives after the pipe is built (hard refresh) — the org list is fetched and the text updates', () => {
    const lists = [
      [],   // what came back before the organization was known
      [{ currencyId: JPY_ID, code: 'JPY', name: 'Japanese Yen', symbol: '¥', decimalPlaces: 0, rounding: 1, symbolPosition: 'before', isActive: true, displayOrder: 1 }]
    ];
    getCurrencies.and.callFake(() => of({ success: true, message: '', result: lists.shift() ?? [] }));
    const tenants = TestBed.inject(TenantService);
    tenants.tenant.set(null);

    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    const a = () => (fixture.nativeElement as HTMLElement).querySelector('[data-testid="a"]')!.textContent;
    expect(a()).toBe('1,234.00');

    tenants.tenant.set({ id: 'org-1', orgCode: 'O1', orgName: 'Org', plan: 'ENTERPRISE', enabledFeatureCodes: [], isSuperAdmin: false, roleName: 'x', permissions: [] });
    TestBed.flushEffects();
    fixture.detectChanges();
    expect(a()).toBe('¥1,234');
    expect(getCurrencies).toHaveBeenCalledTimes(2);
  });

  it('REV-05: when the list cannot be loaded, repeated change detection asks exactly once', () => {
    getCurrencies.and.returnValue(throwError(() => new HttpErrorResponse({ status: 403, statusText: 'Forbidden' })));
    const fixture = TestBed.createComponent(HostComponent);
    for (let i = 0; i < 10; i++) {
      fixture.componentInstance.amount.set(1000 + i);
      fixture.detectChanges();
    }
    expect(getCurrencies).toHaveBeenCalledTimes(1);
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="a"]')!.textContent).toBe('1,009.00');
  });

  it('follows changes of amount and currency, and shows nothing for no amount', () => {
    const fixture = TestBed.createComponent(HostComponent);
    fixture.detectChanges();
    const a = () => (fixture.nativeElement as HTMLElement).querySelector('[data-testid="a"]')!.textContent;

    fixture.componentInstance.amount.set(99.5);
    fixture.detectChanges();
    expect(a()).toBe('¥100');

    fixture.componentInstance.cur.set('usd');
    fixture.detectChanges();
    expect(a()).toBe('USD 99.50');

    fixture.componentInstance.amount.set(null);
    fixture.detectChanges();
    expect(a()).toBe('');
  });
});
