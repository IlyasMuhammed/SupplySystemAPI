import { Component, EventEmitter, Input, OnInit, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormBuilder, FormGroup, FormsModule, ReactiveFormsModule, Validators } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { DatePickerModule } from 'primeng/datepicker';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { SelectModule } from 'primeng/select';
import { SelectButtonModule } from 'primeng/selectbutton';
import { TableModule } from 'primeng/table';
import { ToggleSwitchModule } from 'primeng/toggleswitch';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';
import { forkJoin, of } from 'rxjs';
import { catchError, map } from 'rxjs/operators';

import {
  IntegrationSettingsModel, QuickBooksIntegrationService, ReferenceDataModel, TaxCodeMappingItem, TaxCodeMappingModel,
  TermMappingModel, UpdateIntegrationSettingsRequest, qboErrorMessage
} from '../../../../services/quickbooks-integration.service';
import { PaymentTermsService } from '../../../../services/payment-terms.service';
import { fromDateOnly, toDateOnly } from '../../../../shared/date-only';
import {
  Option, ScmPaymentTerm, TermRow, accountOptions, formatPercent, mergeTermRows, suggestTermsByDays, taxCodeOptions,
  termOptions
} from '../quickbooks.shared';

/**
 * One row of either tax table: an SMS tax code (`sourceTaxCode` set) or, for lines without a code, a bare
 * rate (`sourceTaxCode` null).
 */
export interface TaxRow {
  sourceTaxCode: string | null;
  /** The code's name in Settings → Tax Codes, for an active SMS tax code; else null. Display only — never saved. */
  sourceTaxCodeName: string | null;
  /** 'SALES' | 'PURCHASE' | 'BOTH' for an active SMS tax code; else null. Display only — never saved. */
  sourceTaxCodeUsage: string | null;
  taxPercent: number;
  qboTaxCodeId: string | null;
  timesSeen: number;
  suggestedQboTaxCodeId?: string | null;
  suggestedQboTaxCodeName?: string | null;
}

/** Who may use an SMS tax code, in words; null when it is not known. */
export function taxCodeUsageLabel(usage: string | null | undefined): string | null {
  switch ((usage ?? '').trim().toUpperCase()) {
    case 'SALES': return 'Sales';
    case 'PURCHASE': return 'Purchases';
    case 'BOTH': return 'Sales & purchases';
    default: return null;
  }
}

/** The longest SMS tax code a mapping holds (the server refuses longer ones). */
export const MAX_TAX_CODE_LENGTH = 20;

/** An SMS tax code as the server stores and compares it: trimmed, upper-case; null when blank. */
export function normalizeTaxCode(code: string | null | undefined): string | null {
  const c = (code ?? '').trim().toUpperCase();
  return c ? c : null;
}

/** Code rows (by code) and percent rows (by rate), from what the server returned. */
export function splitTaxRows(rows: TaxCodeMappingModel[]): { codes: TaxRow[]; rates: TaxRow[] } {
  const toRow = (r: TaxCodeMappingModel): TaxRow => ({
    sourceTaxCode: normalizeTaxCode(r.sourceTaxCode),
    sourceTaxCodeName: r.sourceTaxCodeName?.trim() || null,
    sourceTaxCodeUsage: r.sourceTaxCodeUsage?.trim() || null,
    taxPercent: r.taxPercent,
    qboTaxCodeId: r.qboTaxCodeId || null,
    timesSeen: r.timesSeen ?? 0,
    suggestedQboTaxCodeId: r.suggestedQboTaxCodeId || null,
    suggestedQboTaxCodeName: r.suggestedQboTaxCodeName ?? null
  });
  const all = rows.map(toRow);
  return {
    codes: all.filter(r => r.sourceTaxCode).sort((a, b) => a.sourceTaxCode!.localeCompare(b.sourceTaxCode!)),
    rates: all.filter(r => !r.sourceTaxCode).sort((a, b) => a.taxPercent - b.taxPercent)
  };
}

/** The settings form's value, as the controls hold it. */
export interface MappingFormValue {
  defaultIncomeAccountId: string | null;
  defaultExpenseAccountId: string | null;
  freightExpenseAccountId: string | null;
  discountAccountId: string | null;
  defaultPurchaseTaxCodeId: string | null;
  itemTypeDefault: 'NonInventory' | 'Service';
  partnerScope: 'OnlyWhenReferenced' | 'AllActive';
  documentStartDate: Date | null;
  autoPushCustomers: boolean;
  autoPushVendors: boolean;
  autoPushItems: boolean;
  autoPushSalesInvoices: boolean;
  autoPushBills: boolean;
}

/** The request the settings form saves: every setting, with blanks sent as null and the start date as yyyy-MM-dd. */
export function toSettingsRequest(v: MappingFormValue): UpdateIntegrationSettingsRequest {
  const id = (x: string | null | undefined) => (x && String(x).trim()) || null;
  return {
    autoPushCustomers: !!v.autoPushCustomers,
    autoPushVendors: !!v.autoPushVendors,
    autoPushItems: !!v.autoPushItems,
    autoPushSalesInvoices: !!v.autoPushSalesInvoices,
    autoPushBills: !!v.autoPushBills,
    itemTypeDefault: v.itemTypeDefault,
    partnerScope: v.partnerScope,
    defaultIncomeAccountId: id(v.defaultIncomeAccountId),
    defaultExpenseAccountId: id(v.defaultExpenseAccountId),
    freightExpenseAccountId: id(v.freightExpenseAccountId),
    discountAccountId: id(v.discountAccountId),
    defaultPurchaseTaxCodeId: id(v.defaultPurchaseTaxCodeId),
    documentStartDate: v.documentStartDate ? toDateOnly(v.documentStartDate) : null
  };
}

/** A saved QuickBooks id that QuickBooks no longer lists still needs a label in its row. */
function withSaved(options: Option[], saved: Map<string, string | null>): Option[] {
  const missing: Option[] = [];
  saved.forEach((name, id) => {
    if (!options.some(o => o.value === id)) {
      missing.push({ label: `${name || 'QuickBooks id ' + id} (not found in QuickBooks)`, value: id });
    }
  });
  return [...missing, ...options];
}

/**
 * How SCM's data lands in QuickBooks: default accounts, item type, which partners go, from which
 * date, what is sent automatically — plus the tax (SMS tax codes, then bare rates) and payment-term mappings.
 */
@Component({
  selector: 'app-qbo-mappings-tab',
  standalone: true,
  imports: [
    CommonModule, FormsModule, ReactiveFormsModule,
    ButtonModule, DatePickerModule, InputNumberModule, InputTextModule, SelectModule, SelectButtonModule, TableModule,
    ToggleSwitchModule, TooltipModule
  ],
  templateUrl: './mappings-tab.component.html',
  styleUrls: ['../quickbooks-tab.scss', './mappings-tab.component.scss']
})
export class MappingsTabComponent implements OnInit {
  @Input() canManage = false;
  @Output() settingsChange = new EventEmitter<IntegrationSettingsModel>();

  readonly itemTypeOptions: Option[] = [
    { label: 'Non-inventory', value: 'NonInventory' },
    { label: 'Service', value: 'Service' }
  ];
  readonly scopeOptions: Option[] = [
    { label: 'Only when used on a document', value: 'OnlyWhenReferenced' },
    { label: 'All active', value: 'AllActive' }
  ];
  readonly autoPushRows: { name: keyof MappingFormValue; label: string; hint: string }[] = [
    { name: 'autoPushCustomers', label: 'Customers', hint: 'When a customer is created or changed.' },
    { name: 'autoPushVendors', label: 'Vendors', hint: 'When a vendor is created or changed.' },
    { name: 'autoPushItems', label: 'Items', hint: 'When a product or variant is changed.' },
    { name: 'autoPushSalesInvoices', label: 'Sales invoices', hint: 'When an invoice is issued, changed while issued, or cancelled.' },
    { name: 'autoPushBills', label: 'Bills', hint: 'When a supplier invoice is approved, or changed while approved.' }
  ];

  isLoading = true;
  loadFailed = false;
  loadError = '';
  isRefreshingReference = false;

  reference: ReferenceDataModel | null = null;
  settings: IntegrationSettingsModel | null = null;

  incomeOptions: Option[] = [];
  expenseOptions: Option[] = [];
  freightOptions: Option[] = [];
  discountOptions: Option[] = [];
  purchaseTaxOptions: Option[] = [];
  taxCodeOpts: Option[] = [];
  termOpts: Option[] = [];

  form: FormGroup;
  isSavingSettings = false;

  /** Every active SMS tax code, plus codes seen on invoices and bills or already mapped (plan S-11: mapped by code first). */
  taxCodeRows: TaxRow[] = [];
  /** Bare rates seen on lines without a tax code, or already mapped (older documents, other systems). */
  taxRows: TaxRow[] = [];
  private taxBaseline = '[]';
  newRate: number | null = null;
  newCode = '';
  newCodeRate: number | null = null;
  isSavingTax = false;
  readonly maxTaxCodeLength = MAX_TAX_CODE_LENGTH;

  termRows: TermRow[] = [];
  private termBaseline = '[]';
  scmTermsFailed = false;
  isSavingTerms = false;

  readonly formatPercent = formatPercent;
  readonly taxCodeUsageLabel = taxCodeUsageLabel;

  constructor(
    private fb: FormBuilder,
    private service: QuickBooksIntegrationService,
    private paymentTerms: PaymentTermsService,
    private messages: MessageService
  ) {
    this.form = this.fb.group({
      defaultIncomeAccountId: [null as string | null, Validators.required],
      defaultExpenseAccountId: [null as string | null, Validators.required],
      freightExpenseAccountId: [null as string | null],
      discountAccountId: [null as string | null],
      defaultPurchaseTaxCodeId: [null as string | null],
      itemTypeDefault: ['NonInventory', Validators.required],
      partnerScope: ['OnlyWhenReferenced', Validators.required],
      documentStartDate: [null as Date | null],
      autoPushCustomers: [true],
      autoPushVendors: [true],
      autoPushItems: [true],
      autoPushSalesInvoices: [true],
      autoPushBills: [true]
    });
  }

  ngOnInit(): void {
    this.load();
  }

  // ── Loading ─────────────────────────────────────────────────────────────────

  load(): void {
    this.isLoading = true;
    this.loadFailed = false;

    forkJoin({
      reference: this.service.getReference().pipe(catchError(() => of(null))),
      settings: this.service.getSettings(),
      tax: this.service.getTaxMappings(),
      terms: this.service.getTermMappings(),
      scmTerms: this.paymentTerms.getAll().pipe(
        map(res => ({ ok: true, list: (res?.result ?? []) as ScmPaymentTerm[] })),
        catchError(() => of({ ok: false, list: [] as ScmPaymentTerm[] })))
    }).subscribe({
      next: (r) => {
        this.isLoading = false;
        this.reference = r.reference;
        this.scmTermsFailed = !r.scmTerms.ok;
        this.applySettings(r.settings);
        this.applyTax(r.tax ?? []);
        this.applyTerms(r.scmTerms.list, r.terms ?? []);
      },
      error: (err) => {
        this.isLoading = false;
        this.loadFailed = true;
        this.loadError = qboErrorMessage(err, 'The mappings could not be loaded.');
      }
    });
  }

  get hasReference(): boolean {
    return !!this.reference && (this.reference.accounts?.length ?? 0) > 0;
  }

  /** Reads accounts, tax codes and terms from QuickBooks again. */
  refreshReference(): void {
    if (!this.canManage || this.isRefreshingReference) return;
    this.isRefreshingReference = true;
    this.service.refreshReference().subscribe({
      next: (ref) => {
        this.isRefreshingReference = false;
        this.reference = ref;
        this.rebuildOptions();
        this.messages.add({ severity: 'success', summary: 'Refreshed', detail: 'Accounts, tax codes and terms were read from QuickBooks.' });
      },
      error: (err) => {
        this.isRefreshingReference = false;
        this.messages.add({ severity: 'error', summary: 'Not refreshed', detail: qboErrorMessage(err, 'QuickBooks could not be read.') });
      }
    });
  }

  private applySettings(saved: IntegrationSettingsModel | null): void {
    const s: Partial<IntegrationSettingsModel> = saved ?? {};
    this.settings = saved;
    this.form.reset({
      defaultIncomeAccountId: s.defaultIncomeAccountId ?? null,
      defaultExpenseAccountId: s.defaultExpenseAccountId ?? null,
      freightExpenseAccountId: s.freightExpenseAccountId ?? null,
      discountAccountId: s.discountAccountId ?? null,
      defaultPurchaseTaxCodeId: s.defaultPurchaseTaxCodeId ?? null,
      itemTypeDefault: s.itemTypeDefault ?? 'NonInventory',
      partnerScope: s.partnerScope ?? 'OnlyWhenReferenced',
      documentStartDate: s.documentStartDate ? fromDateOnly(s.documentStartDate) : null,
      autoPushCustomers: s.autoPushCustomers ?? true,
      autoPushVendors: s.autoPushVendors ?? true,
      autoPushItems: s.autoPushItems ?? true,
      autoPushSalesInvoices: s.autoPushSalesInvoices ?? true,
      autoPushBills: s.autoPushBills ?? true
    });
    if (this.canManage) this.form.enable({ emitEvent: false });
    else this.form.disable({ emitEvent: false });
    this.rebuildOptions();
  }

  private rebuildOptions(): void {
    const s = this.settings;
    const accounts = this.reference?.accounts ?? [];
    this.incomeOptions = accountOptions(accounts, 'income', s?.defaultIncomeAccountId);
    this.expenseOptions = accountOptions(accounts, 'expense', s?.defaultExpenseAccountId);
    this.freightOptions = accountOptions(accounts, 'expense', s?.freightExpenseAccountId);
    this.discountOptions = accountOptions(accounts, 'discount', s?.discountAccountId);
    this.purchaseTaxOptions = taxCodeOptions(this.reference?.taxCodes, s?.defaultPurchaseTaxCodeId);
    this.taxCodeOpts = withSaved(taxCodeOptions(this.reference?.taxCodes), this.savedTaxCodes);
    this.termOpts = withSaved(termOptions(this.reference?.terms), this.savedTerms);
  }

  /** QuickBooks ids already saved in a mapping, with the name the server gave them. */
  private savedTaxCodes = new Map<string, string | null>();
  private savedTerms = new Map<string, string | null>();

  private applyTax(rows: TaxCodeMappingModel[]): void {
    const { codes, rates } = splitTaxRows(rows);
    this.taxCodeRows = codes;
    this.taxRows = rates;
    this.taxBaseline = this.taxSnapshot();
    this.savedTaxCodes = new Map(rows.filter(r => r.qboTaxCodeId).map(r => [r.qboTaxCodeId!, r.qboTaxCodeName ?? null]));
    this.taxCodeOpts = withSaved(taxCodeOptions(this.reference?.taxCodes), this.savedTaxCodes);
  }

  private applyTerms(scmTerms: ScmPaymentTerm[], mappings: TermMappingModel[]): void {
    this.termRows = mergeTermRows(scmTerms, mappings);
    this.termBaseline = this.termSnapshot();
    this.savedTerms = new Map(mappings.filter(m => m.qboTermId).map(m => [m.qboTermId!, m.qboTermName ?? null]));
    this.termOpts = withSaved(termOptions(this.reference?.terms), this.savedTerms);
  }

  // ── Settings ────────────────────────────────────────────────────────────────

  get settingsDirty(): boolean { return this.form.dirty; }

  /** The first thing wrong with the settings, in words. */
  firstProblem(): string | null {
    if (this.form.get('defaultIncomeAccountId')?.invalid) return 'Choose the income account items are sold to.';
    if (this.form.get('defaultExpenseAccountId')?.invalid) return 'Choose the expense account items are bought from.';
    if (this.form.invalid) return 'Some settings are not valid.';
    return null;
  }

  saveSettings(): void {
    if (!this.canManage || this.isSavingSettings) return;
    this.form.markAllAsTouched();
    const problem = this.firstProblem();
    if (problem) {
      this.messages.add({ severity: 'warn', summary: 'Check the settings', detail: problem });
      return;
    }

    this.isSavingSettings = true;
    this.service.updateSettings(toSettingsRequest(this.form.getRawValue() as MappingFormValue)).subscribe({
      next: (saved) => {
        this.isSavingSettings = false;
        this.applySettings(saved);
        this.messages.add({ severity: 'success', summary: 'Saved', detail: 'The QuickBooks settings are saved.' });
        this.settingsChange.emit(saved);
      },
      error: (err) => {
        this.isSavingSettings = false;
        this.messages.add({ severity: 'error', summary: 'Not saved', detail: qboErrorMessage(err, 'The settings could not be saved.') });
      }
    });
  }

  discardSettings(): void {
    if (this.settings) this.applySettings(this.settings);
  }

  // ── Tax codes and rates ─────────────────────────────────────────────────────

  get unmappedTaxCodeCount(): number { return this.taxCodeRows.filter(r => !r.qboTaxCodeId).length; }

  get unmappedRateCount(): number { return this.taxRows.filter(r => !r.qboTaxCodeId).length; }

  get unmappedTaxCount(): number { return this.unmappedTaxCodeCount + this.unmappedRateCount; }

  /** Unmapped code rows whose rate is mapped as a percent row — what "Use suggestions" would fill. */
  get suggestableCount(): number {
    return this.taxCodeRows.filter(r => !r.qboTaxCodeId && r.suggestedQboTaxCodeId).length;
  }

  get taxDirty(): boolean { return this.taxSnapshot() !== this.taxBaseline; }

  private taxSnapshot(): string {
    return JSON.stringify([
      this.taxCodeRows.map(r => [r.sourceTaxCode, r.qboTaxCodeId || null]),
      this.taxRows.map(r => [r.taxPercent, r.qboTaxCodeId || null])
    ]);
  }

  /** A label for the suggestion: the name the server gave it, else the option's label. */
  suggestionLabel(row: TaxRow): string {
    const id = row.suggestedQboTaxCodeId;
    if (!id) return '';
    return row.suggestedQboTaxCodeName || this.taxCodeOpts.find(o => o.value === id)?.label || `QuickBooks id ${id}`;
  }

  /** Takes the QuickBooks code the row's rate is mapped to. Unsaved until "Save tax mappings". */
  useSuggestion(row: TaxRow): void {
    if (!this.canManage || !row.suggestedQboTaxCodeId) return;
    row.qboTaxCodeId = row.suggestedQboTaxCodeId;
  }

  /** Fills every unmapped code that has a suggestion. Two codes can share a rate, so it is for a person to check. */
  useAllSuggestions(): void {
    if (!this.canManage) return;
    const rows = this.taxCodeRows.filter(r => !r.qboTaxCodeId && r.suggestedQboTaxCodeId);
    rows.forEach(r => (r.qboTaxCodeId = r.suggestedQboTaxCodeId!));
    this.messages.add(rows.length
      ? { severity: 'info', summary: 'Suggested', detail: `${rows.length} ${rows.length === 1 ? 'code was' : 'codes were'} filled from their rate's mapping. Check, then save.` }
      : { severity: 'info', summary: 'Nothing to suggest', detail: 'No unmapped tax code has a rate that is mapped already.' });
  }

  /** Maps an SMS tax code no document has used yet, so it is ready when one does. */
  addCode(): void {
    const code = normalizeTaxCode(this.newCode);
    const rate = this.newCodeRate;
    if (!this.canManage || !code || rate == null || rate < 0 || rate > 100) return;
    if (code.length > MAX_TAX_CODE_LENGTH) {
      this.messages.add({ severity: 'warn', summary: 'Too long', detail: `A tax code has at most ${MAX_TAX_CODE_LENGTH} characters.` });
      return;
    }
    if (this.taxCodeRows.some(r => r.sourceTaxCode === code)) {
      this.messages.add({ severity: 'info', summary: 'Already listed', detail: `${code} is already in the table.` });
      return;
    }
    this.taxCodeRows = [...this.taxCodeRows, {
      sourceTaxCode: code, sourceTaxCodeName: null, sourceTaxCodeUsage: null, taxPercent: rate, qboTaxCodeId: null, timesSeen: 0
    }].sort((a, b) => a.sourceTaxCode!.localeCompare(b.sourceTaxCode!));
    this.newCode = '';
    this.newCodeRate = null;
  }

  /** Maps a rate no document has used yet, so it is ready when one does. */
  addRate(): void {
    const rate = this.newRate;
    if (!this.canManage || rate == null || rate < 0 || rate > 100) return;
    if (this.taxRows.some(r => r.taxPercent === rate)) {
      this.messages.add({ severity: 'info', summary: 'Already listed', detail: `${formatPercent(rate)} is already in the table.` });
      return;
    }
    this.taxRows = [...this.taxRows, {
      sourceTaxCode: null, sourceTaxCodeName: null, sourceTaxCodeUsage: null, taxPercent: rate, qboTaxCodeId: null, timesSeen: 0
    }].sort((a, b) => a.taxPercent - b.taxPercent);
    this.newRate = null;
  }

  /** Saves both kinds: code rows carry their code, percent rows only their rate (the request they always sent). */
  saveTax(): void {
    if (!this.canManage || this.isSavingTax) return;
    this.isSavingTax = true;
    const mappings: TaxCodeMappingItem[] = [
      ...this.taxCodeRows.map(r => ({ sourceTaxCode: r.sourceTaxCode, taxPercent: r.taxPercent, qboTaxCodeId: r.qboTaxCodeId || null })),
      ...this.taxRows.map(r => ({ taxPercent: r.taxPercent, qboTaxCodeId: r.qboTaxCodeId || null }))
    ];

    this.service.saveTaxMappings({ mappings }).subscribe({
      next: (rows) => {
        this.isSavingTax = false;
        this.applyTax(rows ?? []);
        this.messages.add({ severity: 'success', summary: 'Saved', detail: 'The tax mappings are saved.' });
      },
      error: (err) => {
        this.isSavingTax = false;
        this.messages.add({ severity: 'error', summary: 'Not saved', detail: qboErrorMessage(err, 'The tax mappings could not be saved.') });
      }
    });
  }

  // ── Payment terms ───────────────────────────────────────────────────────────

  get unmappedTermCount(): number { return this.termRows.filter(r => !r.qboTermId && !r.orphan).length; }

  get termsDirty(): boolean { return this.termSnapshot() !== this.termBaseline; }

  private termSnapshot(): string {
    return JSON.stringify(this.termRows.map(r => [r.paymentTermExternalId.toLowerCase(), r.qboTermId || null]));
  }

  suggestTerms(): void {
    const filled = suggestTermsByDays(this.termRows, this.reference?.terms ?? []);
    this.messages.add(filled
      ? { severity: 'info', summary: 'Suggested', detail: `${filled} ${filled === 1 ? 'term was' : 'terms were'} matched by due days. Check, then save.` }
      : { severity: 'info', summary: 'Nothing to suggest', detail: 'No unmapped term has exactly one QuickBooks term with the same due days.' });
  }

  saveTerms(): void {
    if (!this.canManage || this.isSavingTerms) return;
    this.isSavingTerms = true;
    const mappings = this.termRows.map(r => ({
      paymentTermExternalId: r.paymentTermExternalId,
      paymentTermName: r.paymentTermName || null,
      qboTermId: r.qboTermId || null
    }));

    this.service.saveTermMappings({ mappings }).subscribe({
      next: (saved) => {
        this.isSavingTerms = false;
        // Keep the rows' SCM names and days; take what the server now holds for the mapping.
        const scm = this.termRows.filter(r => !r.orphan).map(r => ({ id: r.paymentTermExternalId, name: r.paymentTermName, days: r.days }));
        this.applyTerms(scm, saved ?? []);
        this.messages.add({ severity: 'success', summary: 'Saved', detail: 'The payment term mappings are saved.' });
      },
      error: (err) => {
        this.isSavingTerms = false;
        this.messages.add({ severity: 'error', summary: 'Not saved', detail: qboErrorMessage(err, 'The payment term mappings could not be saved.') });
      }
    });
  }
}
