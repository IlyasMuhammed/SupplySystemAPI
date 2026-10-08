import { Component, EventEmitter, Input, Output, inject } from '@angular/core';
import { CommonModule } from '@angular/common';

import { MoneyService } from '../../services/money.service';
import { CurrencyDomain } from '../../services/org-currency.service';
import { AmountView, DocCurrencyInfo, hasBaseAmounts, rateText } from './doc-currency';

/**
 * A35 §11.5 — a document's currency header: "Currency: AED - UAE Dirham · Exchange rate: 76.3000 (locked 7 Oct 2026) ·
 * Base currency: PKR", and once base amounts exist the "Show in AED / Show in PKR" toggle (`[(view)]`).
 * Before the lock the base comes from the org currency flagged as the `domain`'s base.
 */
@Component({
  selector: 'app-doc-currency-panel',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div class="doc-currency" data-testid="doc-currency">
      <div class="dc-item"><span class="dc-label">Currency</span><span class="dc-value" data-testid="dc-currency">{{ currencyLabel }}</span></div>
      <div class="dc-item"><span class="dc-label">Exchange rate</span><span class="dc-value" data-testid="dc-rate">{{ rateLabel }}</span></div>
      <div class="dc-item"><span class="dc-label">Base currency</span><span class="dc-value" data-testid="dc-base">{{ baseLabel }}</span></div>
      <div class="dc-toggle" *ngIf="toggleable" role="group" aria-label="Show amounts in" data-testid="dc-toggle">
        <button type="button" [class.active]="view === 'DOC'" [attr.aria-pressed]="view === 'DOC'" (click)="setView('DOC')">Show in {{ docCode }}</button>
        <button type="button" [class.active]="view === 'BASE'" [attr.aria-pressed]="view === 'BASE'" (click)="setView('BASE')">Show in {{ baseLabel }}</button>
      </div>
    </div>
  `,
  styles: [`
    .doc-currency { display: flex; flex-wrap: wrap; align-items: center; gap: .5rem 1.5rem; padding: .5rem 0; }
    .dc-item { display: flex; flex-direction: column; min-width: 8rem; }
    .dc-label { font-size: .75rem; color: var(--p-text-muted-color, #64748b); text-transform: uppercase; letter-spacing: .02em; }
    .dc-value { font-weight: 500; }
    .dc-toggle { display: inline-flex; margin-left: auto; border: 1px solid var(--p-content-border-color, #cbd5e1); border-radius: 6px; overflow: hidden; }
    .dc-toggle button { border: 0; background: transparent; padding: .35rem .75rem; cursor: pointer; color: inherit; font: inherit; }
    .dc-toggle button.active { background: var(--p-primary-color, #3b82f6); color: var(--p-primary-contrast-color, #fff); }
  `]
})
export class DocCurrencyPanelComponent {
  private readonly money = inject(MoneyService);

  @Input() doc: DocCurrencyInfo | null = null;
  /** Where the rate locks, for "Locked at …" before then: confirmation, approval, sending, issue, posting. */
  @Input() lockPoint = 'confirmation';
  /** The document's domain, to name its base before the lock. */
  @Input() domain: CurrencyDomain = 'SALE';
  @Input() view: AmountView = 'DOC';
  /** False on screens that show base amounts in a column of their own and have nothing to toggle. */
  @Input() showToggle = true;
  @Output() viewChange = new EventEmitter<AmountView>();

  constructor() {
    this.money.ensureLoaded();
  }

  get docCode(): string {
    const d = this.doc;
    return d?.currencyCode || this.money.find(d?.currencyId)?.code || '—';
  }

  get currencyLabel(): string {
    const d = this.doc;
    const c = this.money.find(d?.currencyId) ?? this.money.find(d?.currencyCode);
    if (c) return `${c.code} - ${c.name}`;
    return d?.currencyCode || '—';
  }

  get rateLabel(): string {
    return rateText(this.doc, this.lockPoint);
  }

  get baseLabel(): string {
    const d = this.doc;
    if (d?.baseCurrencyCode) return d.baseCurrencyCode;
    const byId = this.money.find(d?.baseCurrencyId);
    if (byId) return byId.code;
    const domainBase = this.money.currencies().find(c => (c.baseFor ?? []).map(x => String(x).toUpperCase()).includes(this.domain));
    return domainBase?.code ?? '—';
  }

  get toggleable(): boolean {
    return this.showToggle && hasBaseAmounts(this.doc);
  }

  setView(view: AmountView) {
    this.view = view;
    this.viewChange.emit(view);
  }
}
