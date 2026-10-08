import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { FormControl } from '@angular/forms';

import { DocCurrencyPicker, orgActiveCurrencyOptions } from './doc-currency-picker';
import { MoneyService } from '../../services/money.service';
import { TenantService } from '../../pages/service/tenant.service';
import { TEST_AED, TEST_PKR, TEST_USD, provideTestOrgCurrencies } from './doc-currency.testing';

describe('DocCurrencyPicker (A35 D-1, D-9, D-14)', () => {
  let money: MoneyService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(), provideHttpClientTesting(),
        provideTestOrgCurrencies([TEST_PKR, TEST_AED, { ...TEST_USD, isActive: false }, { ...TEST_AED, currencyId: 'cur-eur', code: 'EUR', name: 'Euro', baseFor: [], isActive: true }]),
        { provide: TenantService, useValue: { tenant: signal({ id: 'org-1' }) } }
      ]
    });
    money = TestBed.inject(MoneyService);
  });

  it('offers the active org currencies (plus the one kept), and starts empty controls in the domain base', () => {
    const control = new FormControl<string | null>(null);
    const picker = new DocCurrencyPicker(money, control, 'SALE');
    picker.load();
    expect(picker.options.map(o => o.value)).toEqual(['cur-pkr', 'cur-aed', 'cur-eur']);
    expect(picker.options[1].label).toBe('AED - UAE Dirham');
    expect(control.value).toBe('cur-pkr');

    const kept = new FormControl<string | null>('cur-usd');
    const picker2 = new DocCurrencyPicker(money, kept, 'SALE');
    picker2.load();
    expect(picker2.options.map(o => o.value)).toEqual(['cur-pkr', 'cur-aed', 'cur-usd', 'cur-eur']);
    expect(kept.value).withContext('a value is never replaced by the base').toBe('cur-usd');
  });

  it('the domain base is the purchase base for purchase documents (none active here → no default)', () => {
    const control = new FormControl<string | null>(null);
    new DocCurrencyPicker(money, control, 'PURCHASE').load();
    expect(control.value).withContext('USD is the purchase base but deactivated').toBeNull();
  });

  it('follows the partner default until the user picks by hand; a partner without one undoes the previous default', () => {
    const control = new FormControl<string | null>(null);
    const picker = new DocCurrencyPicker(money, control, 'SALE');
    picker.load();

    picker.applyPartnerDefault('cur-aed');
    expect(control.value).toBe('cur-aed');
    picker.applyPartnerDefault(null);
    expect(control.value).toBe('cur-pkr');
    picker.applyPartnerDefault('cur-usd');
    expect(control.value).withContext('inactive default ignored').toBe('cur-pkr');

    control.setValue('cur-eur');
    control.markAsDirty();
    picker.applyPartnerDefault('cur-aed');
    expect(control.value).toBe('cur-eur');
  });

  it('narrows a catalogue list to the active org currencies, keeping the document\'s own; all of it while the org list is unknown', () => {
    const catalog = [
      { id: 'cur-pkr', name: 'Pakistani Rupee', code: 'PKR' }, { id: 'cur-usd', name: 'US Dollar', code: 'USD' },
      { id: 'cur-jpy', name: 'Yen', code: 'JPY' }
    ];
    expect(orgActiveCurrencyOptions(money, catalog, null).map(o => o.value)).withContext('org list not loaded yet').toEqual(['cur-pkr', 'cur-usd', 'cur-jpy']);
    money.ensureLoaded();
    expect(orgActiveCurrencyOptions(money, catalog, null).map(o => o.value)).toEqual(['cur-pkr']);
    expect(orgActiveCurrencyOptions(money, catalog, 'cur-usd').map(o => o.value)).toEqual(['cur-pkr', 'cur-usd']);
    expect(orgActiveCurrencyOptions(money, catalog, null)[0].label).toBe('Pakistani Rupee (PKR)');
  });

  it('describes the chosen currency for the panel, the same object while nothing changed', () => {
    const control = new FormControl<string | null>('cur-aed');
    const picker = new DocCurrencyPicker(money, control, 'SALE');
    picker.load();
    const a = picker.doc;
    expect(a).toEqual({ currencyId: 'cur-aed', currencyCode: 'AED', exchangeRate: null });
    expect(picker.doc).toBe(a);
    control.setValue(null);
    expect(picker.doc).toBeNull();
  });
});
