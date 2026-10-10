import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { DialogModule } from 'primeng/dialog';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { SelectModule } from 'primeng/select';
import { TableModule } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { ToggleSwitchModule } from 'primeng/toggleswitch';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';

import {
  CURRENCY_MANAGE, OrgCurrencyModel, OrgCurrencyService, SaveOrgCurrencyRequest
} from '../../../services/org-currency.service';
import { CurrenciesService, CurrencyModel } from '../../../services/currencies.service';
import { MoneyService } from '../../../services/money.service';
import { AuthService } from '../../service/auth.service';
import { setupErrorMessage } from '../finance-setup.shared';
import {
  MAX_CURRENCY_NAME, MAX_CURRENCY_SYMBOL, OrgCurrencyDraft, SYMBOL_POSITIONS, currencyLockText, orgCurrencyProblem
} from '../currency-setup.shared';
import { formatMoney } from '../../../shared/money/money-format';
import { FLOW } from '../../../shared/flow';

/**
 * A35-P1-05 — Settings → Finance Setup → Currencies (spec §11.2): the organization's currencies (api/currencies, D-1) with
 * how each one is written (symbol, decimals, rounding, symbol position) and whether documents may use it. A base or rate
 * currency cannot be deactivated (🔒, BR-C1-03); currencies are never deleted (BR-C1-04). The global reference list
 * (Master Data → Currencies) is a separate page and is not changed here.
 */
@Component({
  selector: 'app-org-currencies',
  standalone: true,
  imports: [
    CommonModule, FormsModule, ButtonModule, DialogModule, InputNumberModule, InputTextModule, SelectModule, TableModule, TagModule,
    ToastModule, ToggleSwitchModule, TooltipModule,
    ...FLOW
  ],
  templateUrl: './org-currencies.component.html',
  styleUrls: ['../finance-setup.scss', './org-currencies.component.scss'],
  providers: [MessageService]
})
export class OrgCurrenciesComponent implements OnInit {
  readonly maxName = MAX_CURRENCY_NAME;
  readonly maxSymbol = MAX_CURRENCY_SYMBOL;
  readonly symbolPositions = SYMBOL_POSITIONS;

  currencies: OrgCurrencyModel[] = [];
  isLoading = false;
  loadFailed = false;
  busy: Record<string, boolean> = {};

  catalog: CurrencyModel[] = [];

  dialogVisible = false;
  editing: OrgCurrencyModel | null = null;
  draft: OrgCurrencyDraft = OrgCurrenciesComponent.emptyDraft(1);
  isSaving = false;
  saveError = '';

  constructor(
    private service: OrgCurrencyService,
    private catalogService: CurrenciesService,
    private money: MoneyService,
    private authService: AuthService,
    private messages: MessageService
  ) {}

  ngOnInit(): void {
    this.load();
    this.catalogService.getAll().subscribe({
      next: res => { this.catalog = res.result ?? []; },
      error: () => { this.catalog = []; }
    });
  }

  get canManage(): boolean {
    return this.authService.hasPermission(CURRENCY_MANAGE);
  }

  static emptyDraft(order: number): OrgCurrencyDraft {
    return { code: '', name: '', symbol: '', decimalPlaces: 2, rounding: 0.01, symbolPosition: 'before', displayOrder: order, isActive: true };
  }

  load(): void {
    this.isLoading = true;
    this.loadFailed = false;
    this.service.getCurrencies(true).subscribe({
      next: res => {
        this.isLoading = false;
        this.currencies = [...(res.result ?? [])]
          .sort((a, b) => (a.displayOrder - b.displayOrder) || a.code.localeCompare(b.code));
      },
      error: () => {
        this.isLoading = false;
        this.loadFailed = true;
      }
    });
  }

  lockText(c: OrgCurrencyModel): string | null {
    return currencyLockText(c);
  }

  /** How 1,234 is written in this currency. */
  example(c: OrgCurrencyModel): string {
    return formatMoney(1234, c);
  }

  /** The draft's look, as the person types. */
  get draftExample(): string {
    const dp = this.draft.decimalPlaces;
    if (dp === null || dp < 0 || dp > 3) return '';
    const code = (this.draft.code || 'XXX').toUpperCase();
    return formatMoney(1234.5, { code, symbol: this.draft.symbol || this.catalogEntry?.symbol || null, decimalPlaces: dp, symbolPosition: this.draft.symbolPosition });
  }

  // ── Add / edit ──────────────────────────────────────────────────────────────

  private get catalogEntry(): CurrencyModel | null {
    const code = (this.draft.code ?? '').trim().toUpperCase();
    if (code.length !== 3) return null;
    return this.catalog.find(c => (c.code ?? '').trim().toUpperCase() === code) ?? null;
  }

  /** A new code the global catalog does not know must come with its name and symbol. */
  get needsNameAndSymbol(): boolean {
    return !this.editing && !this.catalogEntry;
  }

  get problem(): string | null {
    return orgCurrencyProblem(this.draft, this.currencies, this.editing?.currencyId ?? null, this.needsNameAndSymbol);
  }

  openCreate(): void {
    if (!this.canManage) return;
    this.editing = null;
    const next = this.currencies.reduce((m, c) => Math.max(m, c.displayOrder ?? 0), 0) + 1;
    this.draft = OrgCurrenciesComponent.emptyDraft(next);
    this.saveError = '';
    this.dialogVisible = true;
  }

  openEdit(c: OrgCurrencyModel): void {
    if (!this.canManage) return;
    this.editing = c;
    this.draft = {
      code: c.code, name: c.name ?? '', symbol: c.symbol ?? '', decimalPlaces: c.decimalPlaces, rounding: c.rounding,
      symbolPosition: (c.symbolPosition ?? 'before').toLowerCase(), displayOrder: c.displayOrder, isActive: c.isActive
    };
    this.saveError = '';
    this.dialogVisible = true;
  }

  /** Typing a code the catalog knows fills in its name and symbol (still editable). */
  onCodeChange(): void {
    this.draft.code = (this.draft.code ?? '').toUpperCase();
    const entry = this.catalogEntry;
    if (!entry) return;
    if (!this.draft.name.trim()) this.draft.name = entry.name ?? '';
    if (!this.draft.symbol.trim()) this.draft.symbol = entry.symbol ?? '';
  }

  /** Rounding follows the decimals (0 → 1, 2 → 0.01, 3 → 0.001) — the smallest unit. */
  onDecimalsChange(): void {
    const dp = this.draft.decimalPlaces;
    if (dp === null || dp < 0 || dp > 3) return;
    this.draft.rounding = Number((1 / Math.pow(10, dp)).toFixed(dp));
  }

  save(): void {
    if (!this.canManage || !this.dialogVisible || this.isSaving || this.problem) return;
    const body: SaveOrgCurrencyRequest = {
      code: this.draft.code.trim().toUpperCase(),
      name: this.draft.name.trim() || null,
      symbol: this.draft.symbol.trim() || null,
      decimalPlaces: this.draft.decimalPlaces,
      rounding: this.draft.rounding,
      symbolPosition: this.draft.symbolPosition,
      displayOrder: this.draft.displayOrder,
      isActive: this.draft.isActive
    };
    const editing = this.editing;
    this.isSaving = true;
    this.saveError = '';
    const call = editing ? this.service.updateCurrency(editing.currencyId, body) : this.service.createCurrency(body);
    call.subscribe({
      next: () => {
        this.isSaving = false;
        this.dialogVisible = false;
        this.messages.add({
          severity: 'success', summary: editing ? 'Currency saved' : 'Currency added',
          detail: `${body.code} — ${body.name ?? ''}`.replace(/ — $/, ''), life: 4000
        });
        this.money.reload();
        this.load();
      },
      error: err => {
        this.isSaving = false;
        this.saveError = setupErrorMessage(err, 'The currency could not be saved.');
      }
    });
  }

  // ── Active toggle ───────────────────────────────────────────────────────────

  canToggle(c: OrgCurrencyModel): boolean {
    // Switching a base / rate currency off is refused (BR-C1-03); switching it on is always allowed.
    return this.canManage && !this.busy[c.currencyId] && (!c.isActive || !currencyLockText(c));
  }

  toggleActive(c: OrgCurrencyModel, active: boolean): void {
    if (!this.canToggle(c) || c.isActive === active) return;
    const previous = c.isActive;
    c.isActive = active;
    this.busy = { ...this.busy, [c.currencyId]: true };
    const body: SaveOrgCurrencyRequest = {
      code: c.code, name: c.name, symbol: c.symbol, decimalPlaces: c.decimalPlaces, rounding: c.rounding,
      symbolPosition: c.symbolPosition, displayOrder: c.displayOrder, isActive: active
    };
    this.service.updateCurrency(c.currencyId, body).subscribe({
      next: () => {
        this.busy = { ...this.busy, [c.currencyId]: false };
        this.messages.add({ severity: 'success', summary: active ? 'Activated' : 'Deactivated', detail: `${c.code} — ${c.name}`, life: 3000 });
        this.money.reload();
      },
      error: err => {
        this.busy = { ...this.busy, [c.currencyId]: false };
        c.isActive = previous;
        this.messages.add({ severity: 'error', summary: 'Not changed', detail: setupErrorMessage(err, 'The currency could not be changed.') });
      }
    });
  }
}
