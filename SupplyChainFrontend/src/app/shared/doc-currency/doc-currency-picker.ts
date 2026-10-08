import { AbstractControl } from '@angular/forms';

import { MoneyService } from '../../services/money.service';
import { CurrencyDomain } from '../../services/org-currency.service';
import { DocCurrencyInfo } from './doc-currency';

/** A global-catalogue currency as CurrenciesService returns it. */
export interface CatalogCurrency { id: string; name: string; code?: string | null; }

/**
 * A35 D-1 — narrows a form's global-catalogue currency list to the organization's active currencies, keeping `keep` (the
 * document's own, even if deactivated since). Until (or unless) the org list is known, every catalogue currency is offered.
 */
export function orgActiveCurrencyOptions(
  money: MoneyService, catalog: CatalogCurrency[], keep: string | null | undefined
): { label: string; value: string }[] {
  const org = money.currencies();
  const offered = org.length ? catalog.filter(c => c.id === keep || money.find(c.id)?.isActive === true) : catalog;
  return offered.map(c => ({ label: c.code ? `${c.name} (${c.code})` : c.name, value: c.id }));
}

/**
 * A35 — the currency picker of a document form (PO, inquiry, quotation): offers the organization's active currencies
 * (D-1; plus the one the document already has), starts an empty control in the domain's base, and follows the partner's
 * default currency (D-9, D-14) until the user picks one by hand.
 *
 *   picker = new DocCurrencyPicker(money, form.get('currencyId')!, 'PURCHASE');
 *   picker.load();                                   // in ngOnInit; again with the kept id after loading a document
 *   picker.applyPartnerDefault(supplier.defaultPurchaseCurrencyId);
 *   <p-dropdown [options]="picker.options" formControlName="currencyId" …>
 *   <app-doc-currency-panel [doc]="picker.doc" lockPoint="approval" domain="PURCHASE">
 */
export class DocCurrencyPicker {
  options: { label: string; value: string }[] = [];
  private fromPartner: string | null = null;
  private docCache: DocCurrencyInfo | null = null;

  constructor(
    private readonly money: MoneyService,
    private readonly control: AbstractControl,
    private readonly domain: CurrencyDomain
  ) {}

  /** Loads the org currencies (once per session) and builds the options; `applyBase` fills an empty control. */
  load(applyBase = true): void {
    this.money.load().subscribe(() => {
      this.rebuild();
      if (applyBase) this.applyBaseIfEmpty();
    });
  }

  /** Rebuilds the options, keeping the control's current currency even if deactivated since. */
  rebuild(): void {
    const keep = (this.control.value as string | null) ?? null;
    this.options = this.money.currencies()
      .filter(c => c.isActive || c.currencyId === keep)
      .map(c => ({ label: `${c.code} - ${c.name}`, value: c.currencyId }));
  }

  /** The active org currency that is this domain's base, else null. */
  get domainBase(): string | null {
    const base = this.money.currencies().find(c => (c.baseFor ?? []).map(x => String(x).toUpperCase()).includes(this.domain));
    return base && base.isActive ? base.currencyId : null;
  }

  applyBaseIfEmpty(): void {
    if (this.control.value) return;
    const base = this.domainBase;
    if (base) this.control.setValue(base);
  }

  /**
   * The partner's default currency, unless the user picked one by hand. A partner with none leaves the currency alone,
   * unless it was the previous partner's default: then back to the domain base.
   */
  applyPartnerDefault(partnerDefault: string | null | undefined): void {
    if (this.control.dirty) return;
    const own = partnerDefault && this.options.some(o => o.value === partnerDefault) ? partnerDefault : null;
    const fromPrevious = this.fromPartner !== null && this.control.value === this.fromPartner;
    this.fromPartner = own;
    const wanted = own ?? (fromPrevious ? this.domainBase : null);
    if (wanted && wanted !== this.control.value) this.control.setValue(wanted);
  }

  /** The chosen currency for the header panel — not locked (forms edit drafts). Same object while unchanged. */
  get doc(): DocCurrencyInfo | null {
    const id = (this.control.value as string | null) ?? null;
    if (!id) return null;
    const code = this.money.find(id)?.code ?? null;
    if (this.docCache?.currencyId !== id || this.docCache?.currencyCode !== code) {
      this.docCache = { currencyId: id, currencyCode: code, exchangeRate: null };
    }
    return this.docCache;
  }

  /** The chosen currency's code (for totals), '' when none. */
  get code(): string {
    return this.doc?.currencyCode ?? '';
  }
}
