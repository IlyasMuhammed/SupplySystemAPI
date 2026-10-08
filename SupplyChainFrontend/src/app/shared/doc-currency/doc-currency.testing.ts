// Test-only: seeds MoneyService with org currencies by stubbing OrgCurrencyService.getCurrencies (A35 FE-DOC specs).
import { Provider } from '@angular/core';
import { of } from 'rxjs';

import { OrgCurrencyModel, OrgCurrencyService } from '../../services/org-currency.service';

const base = { decimalPlaces: 2, rounding: 0.01, symbolPosition: 'before', isActive: true } as const;

export const TEST_PKR: OrgCurrencyModel = { ...base, currencyId: 'cur-pkr', code: 'PKR', name: 'Pakistani Rupee', symbol: '₨', displayOrder: 1, baseFor: ['SALE', 'SERVICE'], isRateCurrency: true };
export const TEST_AED: OrgCurrencyModel = { ...base, currencyId: 'cur-aed', code: 'AED', name: 'UAE Dirham', symbol: null, displayOrder: 2, baseFor: [] };
export const TEST_USD: OrgCurrencyModel = { ...base, currencyId: 'cur-usd', code: 'USD', name: 'US Dollar', symbol: '$', displayOrder: 3, baseFor: ['PURCHASE'] };

/** Provider that answers api/currencies with `list` (default PKR sale base, AED, USD purchase base), synchronously. */
export function provideTestOrgCurrencies(list: OrgCurrencyModel[] = [TEST_PKR, TEST_AED, TEST_USD]): Provider {
  return {
    provide: OrgCurrencyService,
    useValue: { getCurrencies: () => of({ success: true, message: '', result: list }) }
  };
}
