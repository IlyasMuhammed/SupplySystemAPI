import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { SelectModule } from 'primeng/select';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';

import {
  ORG_CURRENCY_SETTINGS_MANAGE, OrgCurrencyModel, OrgCurrencyService, OrgCurrencySettingsModel, SaveOrgCurrencySettingsRequest
} from '../../../services/org-currency.service';
import { MoneyService } from '../../../services/money.service';
import { AuthService } from '../../service/auth.service';
import { setupErrorMessage } from '../finance-setup.shared';

export type BaseDomain = 'sale' | 'purchase' | 'service';

interface SettingsForm {
  saleBaseCurrencyId: string | null;
  purchaseBaseCurrencyId: string | null;
  serviceBaseCurrencyId: string | null;
  exchangeGainAccountCode: string;
  exchangeLossAccountCode: string;
  unrealizedGainAccountCode: string;
  unrealizedLossAccountCode: string;
}

const MAX_ACCOUNT_CODE = 20;
const DOMAINS: { key: BaseDomain; label: string; field: 'saleBaseCurrencyId' | 'purchaseBaseCurrencyId' | 'serviceBaseCurrencyId'; hint: string }[] = [
  { key: 'sale', label: 'Sale', field: 'saleBaseCurrencyId', hint: 'Quotations, sale orders, sales invoices and customer payments convert to it.' },
  { key: 'purchase', label: 'Purchase', field: 'purchaseBaseCurrencyId', hint: 'Purchase orders, supplier invoices and supplier payments convert to it.' },
  { key: 'service', label: 'Service', field: 'serviceBaseCurrencyId', hint: 'For service and project billing (not used by any document yet).' }
];

/**
 * A35-P2-05 — Settings → Finance Setup → Currency Configuration (spec §11.1, api/organization/currency-settings): the
 * organization's three base currencies (D-7) and the four exchange-difference account codes (D-15). A base already used by
 * locked documents cannot be changed (BR-C3-03): its dropdown is disabled with the reason the server gives. The rate
 * currency (D-2) is shown read-only.
 */
@Component({
  selector: 'app-currency-configuration',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, InputTextModule, SelectModule, ToastModule, TooltipModule],
  templateUrl: './currency-configuration.component.html',
  styleUrls: ['../finance-setup.scss', './currency-configuration.component.scss'],
  providers: [MessageService]
})
export class CurrencyConfigurationComponent implements OnInit {
  readonly domains = DOMAINS;
  readonly maxAccountCode = MAX_ACCOUNT_CODE;

  model: OrgCurrencySettingsModel | null = null;
  currencies: OrgCurrencyModel[] = [];
  form: SettingsForm = CurrencyConfigurationComponent.emptyForm();
  isLoading = false;
  loadFailed = false;
  isSaving = false;

  constructor(
    private service: OrgCurrencyService,
    private money: MoneyService,
    private authService: AuthService,
    private messages: MessageService
  ) {}

  ngOnInit(): void {
    this.service.getCurrencies(false).subscribe({
      next: res => { this.currencies = res.result ?? []; },
      error: () => { this.currencies = []; }
    });
    this.load();
  }

  static emptyForm(): SettingsForm {
    return {
      saleBaseCurrencyId: null, purchaseBaseCurrencyId: null, serviceBaseCurrencyId: null,
      exchangeGainAccountCode: '', exchangeLossAccountCode: '', unrealizedGainAccountCode: '', unrealizedLossAccountCode: ''
    };
  }

  get canManage(): boolean {
    return this.authService.hasPermission(ORG_CURRENCY_SETTINGS_MANAGE);
  }

  load(): void {
    this.isLoading = true;
    this.loadFailed = false;
    this.service.getSettings().subscribe({
      next: res => {
        this.isLoading = false;
        this.apply(res.result ?? null);
      },
      error: () => {
        this.isLoading = false;
        this.loadFailed = true;
      }
    });
  }

  private apply(model: OrgCurrencySettingsModel | null): void {
    this.model = model;
    if (!model) return;
    this.form = {
      saleBaseCurrencyId: model.saleBaseCurrencyId, purchaseBaseCurrencyId: model.purchaseBaseCurrencyId,
      serviceBaseCurrencyId: model.serviceBaseCurrencyId,
      exchangeGainAccountCode: model.exchangeGainAccountCode ?? '', exchangeLossAccountCode: model.exchangeLossAccountCode ?? '',
      unrealizedGainAccountCode: model.unrealizedGainAccountCode ?? '', unrealizedLossAccountCode: model.unrealizedLossAccountCode ?? ''
    };
  }

  /** Active org currencies; a saved base that is not among them (data drift) is still listed so it can be shown. */
  get options(): { label: string; value: string }[] {
    const list = this.currencies.map(c => ({ value: c.currencyId, label: `${c.code} - ${c.name}` }));
    const m = this.model;
    if (m) {
      for (const [id, code] of [[m.saleBaseCurrencyId, m.saleBaseCurrencyCode], [m.purchaseBaseCurrencyId, m.purchaseBaseCurrencyCode],
                                [m.serviceBaseCurrencyId, m.serviceBaseCurrencyCode]]) {
        if (id && !list.some(o => o.value === id)) list.push({ value: id, label: code });
      }
    }
    return list;
  }

  private codeOf(id: string | null): string {
    if (!id) return '';
    return this.currencies.find(c => c.currencyId === id)?.code ?? this.options.find(o => o.value === id)?.label ?? '';
  }

  isLocked(domain: BaseDomain): boolean {
    return !!this.model?.locks?.[domain]?.locked;
  }

  lockTooltip(domain: BaseDomain): string | null {
    if (!this.isLocked(domain)) return null;
    const reason = this.model?.locks?.[domain]?.reason;
    return `Cannot change — transactions exist in this base currency${reason ? ` (${reason})` : ''}.`;
  }

  /** §11.1 — one line per domain whose base differs from the others (the sale base is the reference when all differ). */
  get warnings(): string[] {
    const ids = DOMAINS.map(d => this.form[d.field]);
    if (ids.some(id => !id) || new Set(ids).size === 1) return [];
    const count = (id: string | null) => ids.filter(x => x === id).length;
    const reference = count(ids[1]) > 1 ? ids[1] : ids[0];
    return DOMAINS
      .filter(d => this.form[d.field] !== reference)
      .map(d => {
        const code = this.codeOf(this.form[d.field]);
        return `${d.label} base set to ${code} — all ${d.key} amounts will be converted to ${code} for accounting.`;
      });
  }

  get problem(): string | null {
    if (!this.form.saleBaseCurrencyId || !this.form.purchaseBaseCurrencyId || !this.form.serviceBaseCurrencyId) {
      return 'Choose all three base currencies.';
    }
    const codes = [this.form.exchangeGainAccountCode, this.form.exchangeLossAccountCode, this.form.unrealizedGainAccountCode,
      this.form.unrealizedLossAccountCode];
    if (codes.some(c => (c ?? '').trim().length > MAX_ACCOUNT_CODE)) return `Account codes can be at most ${MAX_ACCOUNT_CODE} characters.`;
    return null;
  }

  save(): void {
    if (!this.canManage || this.isSaving || !this.model || this.problem) return;
    const code = (v: string) => (v ?? '').trim() || null;
    const body: SaveOrgCurrencySettingsRequest = {
      saleBaseCurrencyId: this.form.saleBaseCurrencyId!,
      purchaseBaseCurrencyId: this.form.purchaseBaseCurrencyId!,
      serviceBaseCurrencyId: this.form.serviceBaseCurrencyId!,
      rateCurrencyId: null,
      exchangeGainAccountCode: code(this.form.exchangeGainAccountCode),
      exchangeLossAccountCode: code(this.form.exchangeLossAccountCode),
      unrealizedGainAccountCode: code(this.form.unrealizedGainAccountCode),
      unrealizedLossAccountCode: code(this.form.unrealizedLossAccountCode)
    };
    this.isSaving = true;
    this.service.saveSettings(body).subscribe({
      next: res => {
        this.isSaving = false;
        if (res.result) this.apply(res.result);
        this.messages.add({ severity: 'success', summary: 'Settings saved', detail: 'The currency configuration is saved.', life: 4000 });
        // Base flags (baseFor) of the org currencies changed — documents read them through MoneyService.
        this.money.reload();
      },
      error: err => {
        this.isSaving = false;
        this.messages.add({ severity: 'error', summary: 'Not saved', detail: setupErrorMessage(err, 'The settings could not be saved.'), life: 8000 });
      }
    });
  }
}
