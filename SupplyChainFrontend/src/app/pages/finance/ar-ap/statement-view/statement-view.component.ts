import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';

import { StatementResult } from '../../../../services/ar-ap.service';
import { labelOf, money } from '../ar-ap.shared';

/**
 * A48 C4 / C13 — a generated statement on screen: one section per document currency (D-13) with opening balance,
 * the lines (Activity: every debit and credit with a running balance; Open items: the unpaid documents) and the closing
 * balance (BR-ST-001..004), then the aging summary when the server sent one. Shared by the customer and vendor pages.
 */
@Component({
  selector: 'app-statement-view',
  standalone: true,
  imports: [CommonModule],
  styleUrls: ['../../gl/gl.scss', '../ar-ap.scss'],
  template: `
<ng-container *ngIf="statement as s">
  <div class="sf-empty" *ngIf="!s.sections.length" data-testid="statement-empty">Nothing to show for this period.</div>
  <section class="sf-card p0" *ngFor="let sec of s.sections" [attr.data-testid]="'statement-' + sec.currencyCode">
    <div class="sf-ch sf-pad"><h2>{{ sec.currencyCode }}</h2>
      <span class="sf-end sf-mu sf-sm">Opening {{ amount(sec.openingBalance) }} · Debits {{ amount(sec.totalDebits) }} · Credits {{ amount(sec.totalCredits) }}</span></div>
    <div class="gl-scroll">
      <table class="aa-table" style="min-width:48rem">
        <thead><tr><th>Date</th><th>Document</th><th>Reference</th><th *ngIf="openItems">Due</th><th class="gl-num">Debit</th><th class="gl-num">Credit</th>
          <th class="gl-num">Balance</th><th *ngIf="openItems">Status</th></tr></thead>
        <tbody>
          <tr class="aa-sub" *ngIf="!openItems"><td></td><td colspan="4">Opening balance</td><td class="gl-num">{{ amount(sec.openingBalance) }}</td></tr>
          <tr *ngFor="let l of sec.lines">
            <td>{{ l.date | date:'dd MMM yyyy' }}</td>
            <td><span class="sf-mono">{{ l.entryNumber }}</span><span class="sf-mu sf-sm" *ngIf="l.documentType"> · {{ label(l.documentType) }}</span></td>
            <td>{{ l.reference || l.description || '' }}</td>
            <td *ngIf="openItems">{{ l.dueDate ? (l.dueDate | date:'dd MMM yyyy') : '' }}</td>
            <td class="gl-num">{{ l.debit ? amount(l.debit) : '' }}</td>
            <td class="gl-num">{{ l.credit ? amount(l.credit) : '' }}</td>
            <td class="gl-num">{{ amount(l.runningBalance) }}</td>
            <td *ngIf="openItems">{{ l.status || '' }}</td>
          </tr>
          <tr *ngIf="!sec.lines.length"><td [attr.colspan]="openItems ? 8 : 6" class="sf-empty">No documents in this period.</td></tr>
        </tbody>
        <tfoot><tr><td></td><td [attr.colspan]="openItems ? 5 : 4">Closing balance</td><td class="gl-num" [attr.data-testid]="'closing-' + sec.currencyCode">{{ amount(sec.closingBalance) }}</td>
          <td *ngIf="openItems"></td></tr></tfoot>
      </table>
    </div>
    <!-- The section's open items by bucket at the To date (due basis), in its currency. -->
    <div class="aa-kv-grid sf-pad" *ngIf="sec.aging?.length" [attr.data-testid]="'statement-aging-' + sec.currencyCode">
      <div *ngFor="let a of sec.aging!; let i = index"><span class="l">{{ s.agingLabels[i] || ('Bucket ' + (i + 1)) }}</span><span class="v">{{ amount(a) }}</span></div>
      <div><span class="l">Total open</span><span class="v">{{ amount(sum(sec.aging!)) }}</span></div>
    </div>
  </section>
  <div class="sf-mu sf-sm" *ngIf="s.sections.length > 1" data-testid="statement-base">Base currency ({{ s.baseCurrencyCode }}): opening {{ amount(s.openingBalanceBase) }} · closing {{ amount(s.closingBalanceBase) }}</div>
</ng-container>`
})
export class StatementViewComponent {
  @Input() statement: StatementResult | null = null;

  get openItems(): boolean { return this.statement?.type === 'OpenItems'; }

  amount(n: number | null | undefined): string { return money(n); }
  sum(a: number[]): number { return a.reduce((s, x) => s + Number(x || 0), 0); }
  label(s: string | null | undefined): string { return labelOf(s); }
}
