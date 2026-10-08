import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';

import { DocCurrencyPanelComponent } from './doc-currency-panel.component';
import { provideTestOrgCurrencies } from './doc-currency.testing';

describe('DocCurrencyPanelComponent (A35 §11.5)', () => {
  let fixture: ComponentFixture<DocCurrencyPanelComponent>;
  let component: DocCurrencyPanelComponent;

  const text = (id: string) => (fixture.nativeElement.querySelector(`[data-testid="${id}"]`)?.textContent ?? '').trim();

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [DocCurrencyPanelComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideTestOrgCurrencies()]
    }).compileComponents();
    fixture = TestBed.createComponent(DocCurrencyPanelComponent);
    component = fixture.componentInstance;
  });

  it('shows the currency by name, the rate locked with its date, the base, and offers the toggle', () => {
    component.doc = {
      currencyId: 'cur-aed', currencyCode: 'AED', exchangeRate: 76.3, baseCurrencyId: 'cur-pkr', baseCurrencyCode: 'PKR',
      rateLockedAt: '2026-10-07T09:12:00Z'
    };
    component.lockPoint = 'confirmation';
    fixture.detectChanges();

    expect(text('dc-currency')).toBe('AED - UAE Dirham');
    expect(text('dc-rate')).toBe('76.3000 (locked 7 Oct 2026)');
    expect(text('dc-base')).toBe('PKR');
    const buttons: HTMLButtonElement[] = Array.from(fixture.nativeElement.querySelectorAll('[data-testid="dc-toggle"] button'));
    expect(buttons.map(b => b.textContent!.trim())).toEqual(['Show in AED', 'Show in PKR']);

    let emitted: string | null = null;
    component.viewChange.subscribe(v => emitted = v);
    buttons[1].click();
    expect(emitted as string | null).toBe('BASE');
    expect(component.view).toBe('BASE');
  });

  it('before the lock: says when the rate will be locked, names the domain base from the org currencies, no toggle', () => {
    component.doc = { currencyId: 'cur-aed', currencyCode: 'AED', exchangeRate: null };
    component.lockPoint = 'approval';
    component.domain = 'PURCHASE';
    fixture.detectChanges();

    expect(text('dc-rate')).toBe('Locked at approval');
    expect(text('dc-base')).toBe('USD');
    expect(fixture.nativeElement.querySelector('[data-testid="dc-toggle"]')).toBeNull();
  });

  it('a document in its base currency has no toggle', () => {
    component.doc = {
      currencyId: 'cur-pkr', exchangeRate: 1, baseCurrencyId: 'cur-pkr', rateLockedAt: '2026-10-07T09:12:00Z'
    };
    fixture.detectChanges();
    expect(text('dc-currency')).toBe('PKR - Pakistani Rupee');
    expect(text('dc-base')).toBe('PKR');
    expect(fixture.nativeElement.querySelector('[data-testid="dc-toggle"]')).toBeNull();
  });
});
