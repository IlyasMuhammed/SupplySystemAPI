import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { DatePickerModule } from 'primeng/datepicker';
import { DialogModule } from 'primeng/dialog';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { SelectModule } from 'primeng/select';
import { TableModule } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { TextareaModule } from 'primeng/textarea';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { ConfirmationService, MessageService } from 'primeng/api';

import {
  ExchangeRateModel, ExchangeRateQuoteModel, FinanceSetupService, SaveExchangeRateRequest
} from '../../../services/finance-setup.service';
import { CurrenciesService } from '../../../services/currencies.service';
import { AuthService } from '../../service/auth.service';
import {
  ExchangeRateDraft, FINANCE_SETUP_MANAGE, MAX_RATE_NOTES_LENGTH, RATE_DECIMALS, exchangeRateProblem, formatRate, fromIsoDate,
  roundExchangeRate, setupErrorMessage, toIsoDate
} from '../finance-setup.shared';

interface CurrencyOption {
  label: string;
  value: string;
}

/**
 * Settings → Exchange Rates (SAP alignment S-4). One unit of the first currency is worth the rate in the
 * second, from the effective date until the next rate for the same pair. A document uses the latest rate
 * on or before its date, else the reciprocal of the opposite pair; the "rate on a date" check shows
 * exactly what a document would get. Final documents record the rate they used, so changing or deleting
 * a rate here never changes them.
 */
@Component({
  selector: 'app-exchange-rates',
  standalone: true,
  imports: [
    CommonModule, FormsModule, ButtonModule, ConfirmDialogModule, DatePickerModule, DialogModule, InputNumberModule, InputTextModule,
    SelectModule, TableModule, TagModule, TextareaModule, ToastModule, TooltipModule
  ],
  templateUrl: './exchange-rates.component.html',
  styleUrls: ['../finance-setup.scss', './exchange-rates.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class ExchangeRatesComponent implements OnInit {
  readonly maxNotes = MAX_RATE_NOTES_LENGTH;
  readonly rateDecimals = RATE_DECIMALS;
  readonly formatRate = formatRate;

  rates: ExchangeRateModel[] = [];
  isLoading = false;
  loadFailed = false;
  busy: Record<string, boolean> = {};

  currencies: CurrencyOption[] = [];
  currenciesFailed = false;

  // Filters (sent to the server)
  filterFrom: string | null = null;
  filterTo: string | null = null;

  // Add / edit
  dialogVisible = false;
  editing: ExchangeRateModel | null = null;
  draft: ExchangeRateDraft = ExchangeRatesComponent.emptyDraft();
  isSaving = false;
  saveError = '';

  // Rate on a date
  quoteFrom: string | null = null;
  quoteTo: string | null = null;
  quoteDate: Date | null = new Date();
  quote: ExchangeRateQuoteModel | null = null;
  quoteMessage = '';
  quoteChecked = false;
  isQuoting = false;

  constructor(
    private service: FinanceSetupService,
    private currenciesService: CurrenciesService,
    private authService: AuthService,
    private messages: MessageService,
    private confirmation: ConfirmationService
  ) {}

  ngOnInit(): void {
    this.loadCurrencies();
    this.load();
  }

  get canManage(): boolean {
    return this.authService.hasPermission(FINANCE_SETUP_MANAGE);
  }

  static emptyDraft(): ExchangeRateDraft {
    return { fromCurrencyCode: null, toCurrencyCode: null, rate: null, effectiveDate: new Date(), notes: '' };
  }

  private loadCurrencies(): void {
    this.currenciesService.getAll().subscribe({
      next: (res) => {
        // Rates are keyed by code; a catalog currency without one cannot be used.
        const seen = new Set<string>();
        this.currencies = (res.result ?? [])
          .filter(c => !!c.code?.trim())
          .map(c => ({ value: c.code!.trim().toUpperCase(), label: `${c.code!.trim().toUpperCase()} — ${c.name}` }))
          .filter(o => !seen.has(o.value) && !!seen.add(o.value))
          .sort((a, b) => a.value.localeCompare(b.value));
      },
      error: () => { this.currenciesFailed = true; }
    });
  }

  load(): void {
    this.isLoading = true;
    this.loadFailed = false;
    this.service.getExchangeRates(this.filterFrom ?? undefined, this.filterTo ?? undefined).subscribe({
      next: (res) => {
        this.isLoading = false;
        this.rates = res.result ?? [];
      },
      error: () => {
        this.isLoading = false;
        this.loadFailed = true;
      }
    });
  }

  onFilterChange(): void {
    this.load();
  }

  clearFilters(): void {
    this.filterFrom = null;
    this.filterTo = null;
    this.load();
  }

  get hasFilters(): boolean {
    return !!this.filterFrom || !!this.filterTo;
  }

  // ── Add / edit ──────────────────────────────────────────────────────────────

  openCreate(): void {
    if (!this.canManage) return;
    this.editing = null;
    this.draft = {
      ...ExchangeRatesComponent.emptyDraft(),
      // Most often a new rate for the pair being looked at.
      fromCurrencyCode: this.filterFrom, toCurrencyCode: this.filterTo
    };
    this.saveError = '';
    this.dialogVisible = true;
  }

  openEdit(rate: ExchangeRateModel): void {
    if (!this.canManage) return;
    this.editing = rate;
    this.draft = {
      fromCurrencyCode: rate.fromCurrencyCode, toCurrencyCode: rate.toCurrencyCode, rate: rate.rate,
      effectiveDate: fromIsoDate(rate.effectiveDate), notes: rate.notes ?? ''
    };
    this.saveError = '';
    this.dialogVisible = true;
  }

  get problem(): string | null {
    return exchangeRateProblem(this.draft, this.rates, this.editing?.uuid ?? null);
  }

  /** "1 USD = 278.5 PKR" for what is being typed. */
  get draftReading(): string {
    const { fromCurrencyCode: from, toCurrencyCode: to, rate } = this.draft;
    if (!from || !to || !rate || rate <= 0) return '';
    return `1 ${from} = ${formatRate(rate)} ${to}, and so 1 ${to} = ${formatRate(Number((1 / rate).toFixed(RATE_DECIMALS)))} ${from}.`;
  }

  swapDraft(): void {
    this.draft = {
      ...this.draft,
      fromCurrencyCode: this.draft.toCurrencyCode,
      toCurrencyCode: this.draft.fromCurrencyCode,
      rate: this.draft.rate && this.draft.rate > 0 ? Number((1 / this.draft.rate).toFixed(RATE_DECIMALS)) : this.draft.rate
    };
  }

  save(): void {
    if (!this.canManage || this.isSaving || this.problem) return;
    const req: SaveExchangeRateRequest = {
      fromCurrencyCode: this.draft.fromCurrencyCode!,
      toCurrencyCode: this.draft.toCurrencyCode!,
      // Rounded: the rate box's arrow keys can leave float noise (1.5699999999999998) the server would refuse.
      rate: roundExchangeRate(Number(this.draft.rate)),
      effectiveDate: toIsoDate(this.draft.effectiveDate!),
      notes: this.draft.notes.trim() || null
    };

    this.isSaving = true;
    this.saveError = '';
    const call = this.editing ? this.service.updateExchangeRate(this.editing.uuid, req) : this.service.createExchangeRate(req);
    call.subscribe({
      next: (res) => {
        this.isSaving = false;
        this.dialogVisible = false;
        this.messages.add({
          severity: 'success', summary: this.editing ? 'Rate saved' : 'Rate added',
          detail: res.message || `1 ${req.fromCurrencyCode} = ${formatRate(req.rate)} ${req.toCurrencyCode} from ${req.effectiveDate}.`,
          life: 5000
        });
        this.load();
      },
      error: (err) => {
        this.isSaving = false;
        this.saveError = setupErrorMessage(err, 'The rate could not be saved.');
      }
    });
  }

  // ── Delete ──────────────────────────────────────────────────────────────────

  confirmDelete(rate: ExchangeRateModel): void {
    if (!this.canManage) return;
    this.confirmation.confirm({
      key: 'exchange-rates',
      header: 'Delete this rate?',
      icon: 'pi pi-exclamation-triangle',
      message: `1 ${rate.fromCurrencyCode} = ${formatRate(rate.rate)} ${rate.toCurrencyCode} from ${rate.effectiveDate}. `
        + 'Documents that already recorded it keep it; new documents use the rate before it, if there is one.',
      acceptLabel: 'Delete',
      rejectLabel: 'Keep it',
      acceptButtonStyleClass: 'p-button-danger',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => this.delete(rate)
    });
  }

  delete(rate: ExchangeRateModel): void {
    if (!this.canManage || this.busy[rate.uuid]) return;
    this.busy = { ...this.busy, [rate.uuid]: true };
    this.service.deleteExchangeRate(rate.uuid).subscribe({
      next: () => {
        this.busy = { ...this.busy, [rate.uuid]: false };
        this.rates = this.rates.filter(r => r.uuid !== rate.uuid);
        this.messages.add({ severity: 'success', summary: 'Rate deleted', detail: `The ${rate.fromCurrencyCode} → ${rate.toCurrencyCode} rate of ${rate.effectiveDate} is deleted.` });
      },
      error: (err) => {
        this.busy = { ...this.busy, [rate.uuid]: false };
        this.messages.add({ severity: 'error', summary: 'Not deleted', detail: setupErrorMessage(err, 'The rate could not be deleted.') });
        // 404: someone else deleted it already — show the list as it now is rather than a row that cannot be acted on.
        if ((err as HttpErrorResponse | null)?.status === 404) this.load();
      }
    });
  }

  // ── Rate on a date ──────────────────────────────────────────────────────────

  get canQuote(): boolean {
    return !!this.quoteFrom && !!this.quoteTo && !!this.quoteDate && !this.isQuoting;
  }

  checkQuote(): void {
    if (!this.canQuote) return;
    this.isQuoting = true;
    this.service.quoteExchangeRate(this.quoteFrom!, this.quoteTo!, toIsoDate(this.quoteDate!)).subscribe({
      next: (res) => {
        this.isQuoting = false;
        this.quoteChecked = true;
        this.quote = res.result ?? null;
        this.quoteMessage = res.message ?? '';
      },
      error: (err) => {
        this.isQuoting = false;
        this.quoteChecked = true;
        this.quote = null;
        this.quoteMessage = setupErrorMessage(err, 'The rate could not be looked up.');
      }
    });
  }

  onQuoteInputChange(): void {
    // An answer for other inputs would be misleading.
    this.quoteChecked = false;
    this.quote = null;
    this.quoteMessage = '';
  }
}
