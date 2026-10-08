import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { DatePickerModule } from 'primeng/datepicker';
import { DialogModule } from 'primeng/dialog';
import { InputNumberModule } from 'primeng/inputnumber';
import { RadioButtonModule } from 'primeng/radiobutton';
import { SelectModule } from 'primeng/select';
import { TableModule } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { TextareaModule } from 'primeng/textarea';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';

import {
  CURRENCY_RATE_DECIMALS, CURRENCY_RATE_MANAGE, CurrencyRateModel, OrgCurrencyModel, OrgCurrencyService, isOpenEnd
} from '../../../services/org-currency.service';
import { AuthService } from '../../service/auth.service';
import { fromIsoDate, setupErrorMessage, toIsoDate } from '../finance-setup.shared';
import {
  MAX_RATE_NOTES, RateDraft, dayText, formatCurrencyRate, inverseText, previousRateNotice, rateProblem
} from '../currency-setup.shared';

interface Option {
  label: string;
  value: string;
}

/**
 * A35-P1-11 / P1-12 — Settings → Finance Setup → Exchange Rates (spec §11.3 / §11.4, api/currency-rates). Every rate is
 * "units of the organization's rate currency per 1 unit of X" over a date range (D-2, D-3): adding a current rate closes
 * the previous one the day before (BR-C2-03); a fixed range fills a gap; ranges never overlap (BR-C2-02). The rate
 * currency's own 1.0 row cannot be changed (BR-C2-04). Locked documents keep the rate they locked, so editing a rate here
 * never changes them. Replaces the SAP-alignment pair-based page (api/finance/exchange-rates, D-17).
 */
@Component({
  selector: 'app-exchange-rates',
  standalone: true,
  imports: [
    CommonModule, FormsModule, ButtonModule, DatePickerModule, DialogModule, InputNumberModule, RadioButtonModule, SelectModule,
    TableModule, TagModule, TextareaModule, ToastModule, TooltipModule
  ],
  templateUrl: './exchange-rates.component.html',
  styleUrls: ['../finance-setup.scss', './exchange-rates.component.scss'],
  providers: [MessageService]
})
export class ExchangeRatesComponent implements OnInit {
  readonly maxNotes = MAX_RATE_NOTES;
  readonly rateDecimals = CURRENCY_RATE_DECIMALS;
  readonly dayText = dayText;
  readonly isOpenEnd = isOpenEnd;

  rates: CurrencyRateModel[] = [];
  isLoading = false;
  loadFailed = false;

  currencies: OrgCurrencyModel[] = [];
  currenciesFailed = false;

  filterCurrencyId: string | null = null;
  filterMonth: Date | null = null;

  dialogVisible = false;
  editing: CurrencyRateModel | null = null;
  draft: RateDraft = ExchangeRatesComponent.emptyDraft();
  /** Every row of the dialog's currency (for the overlap check and the "will be closed" notice). */
  history: CurrencyRateModel[] = [];
  isSaving = false;
  saveError = '';

  quoteCurrencyId: string | null = null;
  quoteDate: Date | null = new Date();
  quote: CurrencyRateModel | null = null;
  quoteMessage = '';
  quoteChecked = false;
  isQuoting = false;

  constructor(
    private service: OrgCurrencyService,
    private authService: AuthService,
    private messages: MessageService
  ) {}

  ngOnInit(): void {
    this.loadCurrencies();
    this.load();
  }

  get canManage(): boolean {
    return this.authService.hasPermission(CURRENCY_RATE_MANAGE);
  }

  static emptyDraft(): RateDraft {
    const today = new Date();
    return {
      currencyId: null, rate: null, effectiveFrom: new Date(today.getFullYear(), today.getMonth(), today.getDate()),
      openEnded: true, effectiveTo: null, notes: ''
    };
  }

  // ── Loading ─────────────────────────────────────────────────────────────────

  private loadCurrencies(): void {
    this.service.getCurrencies(true).subscribe({
      next: res => { this.currencies = res.result ?? []; },
      error: () => { this.currenciesFailed = true; }
    });
  }

  load(): void {
    this.isLoading = true;
    this.loadFailed = false;
    this.service.getRates(this.filter()).subscribe({
      next: res => {
        this.isLoading = false;
        this.rates = [...(res.result ?? [])].sort((a, b) =>
          a.currencyCode.localeCompare(b.currencyCode) || b.effectiveFrom.localeCompare(a.effectiveFrom));
      },
      error: () => {
        this.isLoading = false;
        this.loadFailed = true;
      }
    });
  }

  private filter(): { currencyId: string | null; from: string | null; to: string | null } {
    const m = this.filterMonth;
    return {
      currencyId: this.filterCurrencyId ?? null,
      from: m ? toIsoDate(new Date(m.getFullYear(), m.getMonth(), 1)) : null,
      to: m ? toIsoDate(new Date(m.getFullYear(), m.getMonth() + 1, 0)) : null
    };
  }

  onFilterChange(): void {
    this.load();
  }

  clearFilters(): void {
    this.filterCurrencyId = null;
    this.filterMonth = null;
    this.load();
  }

  get hasFilters(): boolean {
    return !!this.filterCurrencyId || !!this.filterMonth;
  }

  /** The rate currency (rate 1.0): from the org currencies, else from any rate row. */
  get rateCurrency(): OrgCurrencyModel | null {
    return this.currencies.find(c => c.isRateCurrency) ?? null;
  }

  get rateCurrencyCode(): string {
    return this.rateCurrency?.code ?? this.rates[0]?.rateCurrencyCode ?? '';
  }

  private get rateCurrencyId(): string | null {
    return this.rateCurrency?.currencyId ?? this.rates[0]?.rateCurrencyId ?? null;
  }

  /** Every org currency, for the list filter. */
  get filterOptions(): Option[] {
    return this.currencies.map(c => ({ value: c.currencyId, label: `${c.code} - ${c.name}` }));
  }

  /** Active currencies that can have a rate (not the rate currency), for the dialog. */
  get currencyOptions(): Option[] {
    const rateId = this.rateCurrencyId;
    return this.currencies
      .filter(c => c.isActive && c.currencyId !== rateId)
      .map(c => ({ value: c.currencyId, label: `${c.code} - ${c.name}` }));
  }

  /** 278.05 → '278.0500' (at least four decimals, up to ten). */
  rateText(rate: number): string {
    const full = formatCurrencyRate(rate);
    const dot = full.indexOf('.');
    const decimals = dot < 0 ? 0 : full.length - dot - 1;
    return decimals >= 4 ? full : rate.toFixed(4);
  }

  canEdit(r: CurrencyRateModel): boolean {
    return this.canManage && r.source !== 'SYSTEM' && r.currencyId !== this.rateCurrencyId;
  }

  // ── Add / edit ──────────────────────────────────────────────────────────────

  private get draftCode(): string {
    if (this.editing) return this.editing.currencyCode;
    return this.currencies.find(c => c.currencyId === this.draft.currencyId)?.code ?? '';
  }

  draftCodeLabel(): string {
    return this.draftCode || 'X';
  }

  get inverse(): string {
    const code = this.draftCode;
    return code ? inverseText(this.draft.rate, code, this.rateCurrencyCode) : '';
  }

  get notice(): string | null {
    if (this.editing || !this.draft.openEnded) return null;
    const current = this.history.find(r => isOpenEnd(r.effectiveTo)) ?? null;
    return previousRateNotice(current, this.draft.effectiveFrom);
  }

  get problem(): string | null {
    return rateProblem(this.draft, this.history, this.editing?.id ?? null, this.rateCurrencyId);
  }

  openCreate(): void {
    if (!this.canManage) return;
    this.editing = null;
    this.draft = { ...ExchangeRatesComponent.emptyDraft(), currencyId: this.filterCurrencyId };
    this.history = [];
    this.saveError = '';
    this.dialogVisible = true;
    if (this.draft.currencyId) this.onDraftCurrencyChange();
  }

  openEdit(r: CurrencyRateModel): void {
    if (!this.canEdit(r)) return;
    this.editing = r;
    const open = isOpenEnd(r.effectiveTo);
    this.draft = {
      currencyId: r.currencyId, rate: r.rate, effectiveFrom: fromIsoDate(r.effectiveFrom), openEnded: open,
      effectiveTo: open ? null : fromIsoDate(r.effectiveTo), notes: r.notes ?? ''
    };
    this.history = [];
    this.saveError = '';
    this.dialogVisible = true;
    this.onDraftCurrencyChange();
  }

  onDraftCurrencyChange(): void {
    const id = this.draft.currencyId;
    this.history = [];
    if (!id) return;
    this.service.getRateHistory(id).subscribe({
      next: res => { if (this.draft.currencyId === id) this.history = res.result ?? []; },
      error: () => { /* the server still refuses an overlap; the notice is just not shown */ }
    });
  }

  save(): void {
    if (!this.canManage || !this.dialogVisible || this.isSaving || this.problem) return;
    const from = toIsoDate(this.draft.effectiveFrom!);
    const to = this.draft.openEnded ? null : toIsoDate(this.draft.effectiveTo!);
    const rate = Number(Number(this.draft.rate).toFixed(CURRENCY_RATE_DECIMALS));
    const notes = this.draft.notes.trim() || null;
    const code = this.draftCode;
    const editing = this.editing;

    this.isSaving = true;
    this.saveError = '';
    const done = (detail: string) => {
      this.isSaving = false;
      this.dialogVisible = false;
      this.messages.add({ severity: 'success', summary: editing ? 'Rate saved' : 'Rate added', detail, life: 6000 });
      this.load();
    };
    const fail = (err: unknown) => {
      this.isSaving = false;
      this.saveError = setupErrorMessage(err, 'The rate could not be saved.');
    };

    if (editing) {
      this.service.updateRate(editing.id, { rate, effectiveFrom: from, effectiveTo: to, notes }).subscribe({
        next: () => done(`1 ${code} = ${formatCurrencyRate(rate)} ${this.rateCurrencyCode} from ${dayText(from)}.`),
        error: fail
      });
      return;
    }
    this.service.createRate({ currencyId: this.draft.currencyId!, rate, effectiveFrom: from, effectiveTo: to, notes }).subscribe({
      next: res => {
        const closed = res.result?.closedPrevious;
        done(`1 ${code} = ${formatCurrencyRate(rate)} ${this.rateCurrencyCode} from ${dayText(from)}.`
          + (closed ? ` The previous rate now ends on ${dayText(closed.effectiveTo)}.` : ''));
      },
      error: fail
    });
  }

  // ── Rate on a date ──────────────────────────────────────────────────────────

  get canQuote(): boolean {
    return !!this.quoteCurrencyId && !!this.quoteDate && !this.isQuoting;
  }

  checkQuote(): void {
    if (!this.canQuote) return;
    this.isQuoting = true;
    this.service.getRateOn(this.quoteCurrencyId!, toIsoDate(this.quoteDate!)).subscribe({
      next: res => {
        this.isQuoting = false;
        this.quoteChecked = true;
        this.quote = res.result ?? null;
        this.quoteMessage = res.message ?? '';
      },
      error: err => {
        this.isQuoting = false;
        this.quoteChecked = true;
        this.quote = null;
        this.quoteMessage = setupErrorMessage(err, 'The rate could not be looked up.');
      }
    });
  }

  onQuoteInputChange(): void {
    this.quoteChecked = false;
    this.quote = null;
    this.quoteMessage = '';
  }
}
