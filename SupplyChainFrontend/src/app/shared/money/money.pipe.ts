import { Pipe, PipeTransform } from '@angular/core';

import { MoneyService } from '../../services/money.service';
import { MoneyCurrency, MoneyFormatOptions } from './money-format';

/**
 * A35 D-21 — `{{ amount | money:currencyIdOrCode }}` → '¥1,234', 'BD 1.567', '100.00 CHF';
 * `{{ amount | money:currencyIdOrCode:{ display: 'code' } }}` → 'AED 6,000.00'. See MoneyService.
 *
 * Impure so the text updates when the org currency list arrives; the work per check is a lookup in a short list and a
 * number format, and the last answer is reused while nothing changed.
 */
@Pipe({ name: 'money', standalone: true, pure: false })
export class MoneyPipe implements PipeTransform {
  private lastAmount: unknown = undefined;
  private lastCurrency: unknown = undefined;
  private lastOptions: unknown = undefined;
  private lastList: unknown = undefined;
  private lastText = '';

  constructor(private money: MoneyService) {
    this.money.ensureLoaded();
  }

  transform(
    amount: number | null | undefined,
    currency: string | MoneyCurrency | null | undefined,
    options?: MoneyFormatOptions
  ): string {
    // Cheap when loaded; after a login into another organization it fetches that one's list (REV-03).
    this.money.ensureLoaded();
    const list = this.money.currencies();
    const optionsKey = options ? JSON.stringify(options) : '';
    if (amount === this.lastAmount && currency === this.lastCurrency && optionsKey === this.lastOptions && list === this.lastList) {
      return this.lastText;
    }
    this.lastAmount = amount;
    this.lastCurrency = currency;
    this.lastOptions = optionsKey;
    this.lastList = list;
    this.lastText = this.money.format(amount, currency, options);
    return this.lastText;
  }
}
