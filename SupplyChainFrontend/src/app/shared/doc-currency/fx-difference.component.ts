import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';

import { MoneyPipe } from '../money/money.pipe';

/**
 * A35 D-15 — a realized or unrealized exchange difference in the base currency: "+PKR 1,582.50" (gain, green) or
 * "-PKR 5,500.00" (loss, red); "—" when there is none.
 */
@Component({
  selector: 'app-fx-difference',
  standalone: true,
  imports: [CommonModule, MoneyPipe],
  template: `<span class="fx-diff" [class.gain]="(amount ?? 0) > 0" [class.loss]="(amount ?? 0) < 0"
      [attr.title]="(amount ?? 0) > 0 ? 'Exchange gain' : (amount ?? 0) < 0 ? 'Exchange loss' : null"
      data-testid="fx-difference">{{ amount === null || amount === undefined ? '—' : ((amount > 0 ? '+' : '') + (amount | money:currency:moneyCode)) }}</span>`,
  styles: [`
    .fx-diff.gain { color: var(--sms-ok); font-weight: 600; }
    .fx-diff.loss { color: var(--sms-danger); font-weight: 600; }
  `]
})
export class FxDifferenceComponent {
  @Input() amount: number | null | undefined = null;
  /** Base currency id or code. */
  @Input() currency: string | null | undefined = null;
  readonly moneyCode = { display: 'code' } as const;
}
