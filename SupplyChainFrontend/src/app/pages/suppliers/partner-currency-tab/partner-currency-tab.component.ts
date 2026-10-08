import { Component, EventEmitter, Input, OnInit, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { SelectModule } from 'primeng/select';

import { BusinessPartnerModel, BusinessPartnerService } from '../../../services/business-partner.service';
import { OrgCurrencyModel, OrgCurrencyService } from '../../../services/org-currency.service';
import { AuthService } from '../../service/auth.service';
import { setupErrorMessage } from '../../finance-setup/finance-setup.shared';

interface Option {
  label: string;
  value: string | null;
}

/**
 * A35-P2-09 — the Currency tab of a business partner (spec §11.6, API-CONTRACT.md §5, D-9): the default sale currency
 * (new inquiries, quotations and orders for this customer start in it) and the default purchase currency (= the
 * supplier's preferred currency; new POs start in it). Blank = the organization's base for that side (BR-C4-02/03).
 * Saved through PUT api/partners/{uuid} with the partner's other fields unchanged; a blank sends the clear flag, because
 * null alone means "unchanged" there.
 */
@Component({
  selector: 'app-partner-currency-tab',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, SelectModule],
  templateUrl: './partner-currency-tab.component.html',
  styleUrls: ['./partner-currency-tab.component.scss']
})
export class PartnerCurrencyTabComponent implements OnInit {
  @Input({ required: true }) partner!: Partial<BusinessPartnerModel>;
  @Output() saved = new EventEmitter<void>();

  currencies: OrgCurrencyModel[] = [];
  saleCurrencyId: string | null = null;
  purchaseCurrencyId: string | null = null;
  isSaving = false;
  saveError = '';
  savedMessage = '';

  constructor(
    private partners: BusinessPartnerService,
    private orgCurrencies: OrgCurrencyService,
    private authService: AuthService
  ) {}

  ngOnInit(): void {
    this.saleCurrencyId = this.partner.defaultSaleCurrencyId ?? null;
    this.purchaseCurrencyId = this.partner.defaultPurchaseCurrencyId ?? null;
    this.orgCurrencies.getCurrencies(true).subscribe({
      next: res => { this.currencies = res.result ?? []; },
      error: () => { this.currencies = []; }
    });
  }

  /** Same codes as the partner edit route; the server checks again. */
  get canEdit(): boolean {
    return this.authService.hasAnyPermission('SUPPLIER_EDIT', 'SUPPLIER_MANAGE');
  }

  private baseCode(domain: 'SALE' | 'PURCHASE'): string {
    return this.currencies.find(c => (c.baseFor ?? []).map(d => String(d).toUpperCase()).includes(domain))?.code ?? '';
  }

  get saleBlankLabel(): string {
    const code = this.baseCode('SALE');
    return code ? `Use org default: ${code}` : 'Use org default';
  }

  get purchaseBlankLabel(): string {
    const code = this.baseCode('PURCHASE');
    return code ? `Use org default: ${code}` : 'Use org default';
  }

  /** Active org currencies, plus the partner's current choice if it has since been switched off. */
  private optionsFor(selected: string | null, blank: string): Option[] {
    const list: Option[] = [{ label: blank, value: null }];
    for (const c of this.currencies) {
      if (c.isActive || c.currencyId === selected) {
        list.push({ value: c.currencyId, label: `${c.code} - ${c.name}${c.isActive ? '' : ' (inactive)'}` });
      }
    }
    return list;
  }

  get saleOptions(): Option[] {
    return this.optionsFor(this.partner.defaultSaleCurrencyId ?? null, this.saleBlankLabel);
  }

  get purchaseOptions(): Option[] {
    return this.optionsFor(this.partner.defaultPurchaseCurrencyId ?? null, this.purchaseBlankLabel);
  }

  private codeOf(id: string | null, domain: 'SALE' | 'PURCHASE'): string {
    if (!id) return this.baseCode(domain) || 'the organization\'s base currency';
    return this.currencies.find(c => c.currencyId === id)?.code
      ?? (domain === 'SALE' ? this.partner.defaultSaleCurrencyCode : this.partner.defaultPurchaseCurrencyCode) ?? '';
  }

  get saleCode(): string {
    return this.codeOf(this.saleCurrencyId, 'SALE');
  }

  get dirty(): boolean {
    return (this.saleCurrencyId ?? null) !== (this.partner.defaultSaleCurrencyId ?? null)
      || (this.purchaseCurrencyId ?? null) !== (this.partner.defaultPurchaseCurrencyId ?? null);
  }

  save(): void {
    if (!this.canEdit || this.isSaving || !this.partner.uuid) return;
    const body: BusinessPartnerModel = {
      ...(this.partner as BusinessPartnerModel),
      defaultSaleCurrencyId: this.saleCurrencyId,
      defaultPurchaseCurrencyId: this.purchaseCurrencyId,
      clearDefaultSaleCurrency: !this.saleCurrencyId,
      clearDefaultPurchaseCurrency: !this.purchaseCurrencyId
    };
    this.isSaving = true;
    this.saveError = '';
    this.savedMessage = '';
    this.partners.updatePartner(this.partner.uuid, body).subscribe({
      next: () => {
        this.isSaving = false;
        this.partner = { ...this.partner, defaultSaleCurrencyId: this.saleCurrencyId, defaultPurchaseCurrencyId: this.purchaseCurrencyId };
        this.savedMessage = 'Currency defaults saved.';
        this.saved.emit();
      },
      error: err => {
        this.isSaving = false;
        this.saveError = setupErrorMessage(err, 'The currency defaults could not be saved.');
      }
    });
  }
}
