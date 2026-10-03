import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { CheckboxModule } from 'primeng/checkbox';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { DialogModule } from 'primeng/dialog';
import { IconFieldModule } from 'primeng/iconfield';
import { InputIconModule } from 'primeng/inputicon';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { SelectModule } from 'primeng/select';
import { SelectButtonModule } from 'primeng/selectbutton';
import { TableModule } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { TextareaModule } from 'primeng/textarea';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { ConfirmationService, MessageService } from 'primeng/api';

import {
  FinanceSetupService, SaveTaxCodeRequest, TaxCodeModel, TaxCodesFromRatesResult
} from '../../../services/finance-setup.service';
import { AuthService } from '../../service/auth.service';
import {
  FINANCE_SETUP_MANAGE, MAX_TAX_CODE_LENGTH, MAX_TAX_DESCRIPTION_LENGTH, MAX_TAX_NAME_LENGTH, TaxCodeDraft, USAGE_OPTIONS,
  defaultsTakenOver, formatPercent, normalizeTaxCode, roundRatePercent, setupErrorMessage, taxCodeProblem, usageAllows, usageLabel
} from '../finance-setup.shared';

type SideFilter = 'ALL' | 'SALES' | 'PURCHASE';

/**
 * Settings → Tax Codes (SAP alignment, docs/finance/SAP-ALIGNMENT-PLAN.md). The organization's tax codes:
 * sale-order lines and supplier invoices pick one, and keep its rate as a snapshot, so changing a rate
 * here never changes a document already raised. Codes are deactivated, never deleted. One code per side
 * can be the default — making a code the default takes it from the code that had it.
 */
@Component({
  selector: 'app-tax-codes',
  standalone: true,
  imports: [
    CommonModule, FormsModule, ButtonModule, CheckboxModule, ConfirmDialogModule, DialogModule, IconFieldModule, InputIconModule,
    InputNumberModule, InputTextModule, SelectModule, SelectButtonModule, TableModule, TagModule, TextareaModule, ToastModule,
    TooltipModule
  ],
  templateUrl: './tax-codes.component.html',
  styleUrls: ['../finance-setup.scss', './tax-codes.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class TaxCodesComponent implements OnInit {
  readonly usageOptions = USAGE_OPTIONS;
  readonly sideOptions: { label: string; value: SideFilter }[] = [
    { label: 'All codes', value: 'ALL' },
    { label: 'Usable on sales', value: 'SALES' },
    { label: 'Usable on purchases', value: 'PURCHASE' }
  ];
  readonly maxCode = MAX_TAX_CODE_LENGTH;
  readonly maxName = MAX_TAX_NAME_LENGTH;
  readonly maxDescription = MAX_TAX_DESCRIPTION_LENGTH;
  readonly usageLabel = usageLabel;
  readonly formatPercent = formatPercent;

  codes: TaxCodeModel[] = [];
  isLoading = false;
  loadFailed = false;
  busy: Record<string, boolean> = {};

  // Filters (on the loaded list; inactive codes are loaded too, and shown on request)
  search = '';
  sideFilter: SideFilter = 'ALL';
  showInactive = false;

  // Add / edit
  dialogVisible = false;
  editing: TaxCodeModel | null = null;
  draft: TaxCodeDraft = TaxCodesComponent.emptyDraft();
  isSaving = false;
  saveError = '';

  // "Create codes from rates already used"
  isCreatingFromRates = false;
  fromRatesResult: TaxCodesFromRatesResult | null = null;
  fromRatesMessage = '';

  constructor(
    private service: FinanceSetupService,
    private authService: AuthService,
    private messages: MessageService,
    private confirmation: ConfirmationService
  ) {}

  ngOnInit(): void {
    this.load();
  }

  get canManage(): boolean {
    return this.authService.hasPermission(FINANCE_SETUP_MANAGE);
  }

  static emptyDraft(): TaxCodeDraft {
    return { code: '', name: '', description: '', ratePercent: null, usage: 'BOTH', isDefault: false, isActive: true };
  }

  load(): void {
    this.isLoading = true;
    this.loadFailed = false;
    this.service.getTaxCodes(undefined, true).subscribe({
      next: (res) => {
        this.isLoading = false;
        this.codes = res.result ?? [];
      },
      error: () => {
        this.isLoading = false;
        this.loadFailed = true;
      }
    });
  }

  // ── What the table shows ────────────────────────────────────────────────────

  get visibleCodes(): TaxCodeModel[] {
    const term = this.search.trim().toLowerCase();
    return this.codes.filter(c =>
      (this.showInactive || c.isActive)
      && (this.sideFilter === 'ALL' || usageAllows(c.usage, this.sideFilter))
      && (!term
        || c.code.toLowerCase().includes(term)
        || c.name.toLowerCase().includes(term)
        || (c.description ?? '').toLowerCase().includes(term)));
  }

  get inactiveCount(): number {
    return this.codes.filter(c => !c.isActive).length;
  }

  /** "Default for sales" etc., for the default column. */
  defaultLabel(code: TaxCodeModel): string {
    if (!code.isDefault) return '';
    return code.usage === 'BOTH' ? 'Default for both' : `Default for ${code.usage === 'SALES' ? 'sales' : 'purchases'}`;
  }

  usageSeverity(usage: string): 'info' | 'warn' | 'secondary' {
    return usage === 'SALES' ? 'info' : usage === 'PURCHASE' ? 'warn' : 'secondary';
  }

  // ── Add / edit ──────────────────────────────────────────────────────────────

  openCreate(): void {
    if (!this.canManage) return;
    this.editing = null;
    this.draft = TaxCodesComponent.emptyDraft();
    this.saveError = '';
    this.dialogVisible = true;
  }

  openEdit(code: TaxCodeModel): void {
    if (!this.canManage) return;
    this.editing = code;
    this.draft = {
      code: code.code, name: code.name, description: code.description ?? '', ratePercent: code.ratePercent,
      usage: code.usage, isDefault: code.isDefault, isActive: code.isActive
    };
    this.saveError = '';
    this.dialogVisible = true;
  }

  get problem(): string | null {
    return taxCodeProblem(this.draft, this.codes, this.editing?.uuid ?? null);
  }

  /** The codes that stop being a default if this is saved. */
  get takesDefaultFrom(): TaxCodeModel[] {
    return defaultsTakenOver(this.draft, this.codes, this.editing?.uuid ?? null);
  }

  get defaultTakeoverText(): string {
    const codes = this.takesDefaultFrom.map(c => c.code);
    if (!codes.length) return '';
    return codes.length === 1
      ? `${codes[0]} stops being the default.`
      : `${codes.slice(0, -1).join(', ')} and ${codes[codes.length - 1]} stop being defaults.`;
  }

  /** Said before saving a new rate on an existing code: documents keep the old one. */
  get rateChangeNote(): string | null {
    const before = this.editing?.ratePercent;
    const after = this.draft.ratePercent;
    if (!this.editing || after === null || after === undefined || before === after) return null;
    return `Documents already raised keep ${formatPercent(before)}%; lines added from now on use ${formatPercent(after)}%.`;
  }

  get renameNote(): string | null {
    if (!this.editing) return null;
    const code = normalizeTaxCode(this.draft.code);
    return code && code !== this.editing.code
      ? 'A code already used on a document, or mapped to a QuickBooks tax code, cannot be renamed — the server refuses it. Create a new code and deactivate this one instead.'
      : null;
  }

  onActiveChange(active: boolean): void {
    // An inactive code cannot be the default (the server clears it too).
    if (!active) this.draft = { ...this.draft, isDefault: false };
  }

  save(): void {
    if (!this.canManage || this.isSaving || this.problem) return;
    const req: SaveTaxCodeRequest = {
      code: normalizeTaxCode(this.draft.code),
      name: this.draft.name.trim(),
      description: this.draft.description.trim() || null,
      // Rounded: the rate box's arrow keys can leave float noise (1.5699999999999998) the server would refuse.
      ratePercent: roundRatePercent(Number(this.draft.ratePercent)),
      usage: this.draft.usage!,
      isDefault: this.draft.isActive && this.draft.isDefault,
      isActive: this.draft.isActive
    };

    this.isSaving = true;
    this.saveError = '';
    const call = this.editing ? this.service.updateTaxCode(this.editing.uuid, req) : this.service.createTaxCode(req);
    call.subscribe({
      next: (res) => {
        this.isSaving = false;
        this.dialogVisible = false;
        this.messages.add({ severity: 'success', summary: this.editing ? 'Tax code saved' : 'Tax code created', detail: res.message, life: 6000 });
        // A save can take the default from other codes, so the whole list is read again.
        this.load();
      },
      error: (err) => {
        this.isSaving = false;
        this.saveError = setupErrorMessage(err, 'The tax code could not be saved.');
      }
    });
  }

  // ── Deactivate / reactivate ─────────────────────────────────────────────────

  confirmDeactivate(code: TaxCodeModel): void {
    if (!this.canManage || !code.isActive) return;
    this.confirmation.confirm({
      key: 'tax-codes',
      header: `Deactivate ${code.code}?`,
      icon: 'pi pi-exclamation-triangle',
      message: (code.isDefault ? 'It is a default code; no code will be pre-selected in its place until you choose another. ' : '')
        + 'It can no longer be picked on new lines. Documents that already use it keep it.',
      acceptLabel: 'Deactivate',
      rejectLabel: 'Keep it',
      acceptButtonStyleClass: 'p-button-danger',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => this.setActive(code, false)
    });
  }

  reactivate(code: TaxCodeModel): void {
    if (!this.canManage || code.isActive) return;
    this.setActive(code, true);
  }

  private setActive(code: TaxCodeModel, isActive: boolean): void {
    if (this.busy[code.uuid]) return;
    this.busy = { ...this.busy, [code.uuid]: true };
    this.service.updateTaxCode(code.uuid, {
      code: code.code, name: code.name, description: code.description, ratePercent: code.ratePercent, usage: code.usage,
      isDefault: isActive && code.isDefault, isActive
    }).subscribe({
      next: (res) => {
        this.busy = { ...this.busy, [code.uuid]: false };
        this.messages.add({ severity: 'success', summary: isActive ? 'Reactivated' : 'Deactivated', detail: res.message, life: 6000 });
        this.load();
      },
      error: (err) => {
        this.busy = { ...this.busy, [code.uuid]: false };
        this.messages.add({ severity: 'error', summary: 'Not changed', detail: setupErrorMessage(err, 'The tax code could not be changed.') });
      }
    });
  }

  // ── Codes from the rates already used ───────────────────────────────────────

  confirmFromRates(): void {
    if (!this.canManage || this.isCreatingFromRates) return;
    this.confirmation.confirm({
      key: 'tax-codes',
      header: 'Create codes from rates already used?',
      icon: 'pi pi-percentage',
      message: 'Creates one sales tax code (TAX17, TAX7_5 …) for every tax % already used on sale orders that no active code '
        + 'covers yet. Nothing already raised changes. Asking again creates nothing new.',
      acceptLabel: 'Create codes',
      rejectLabel: 'Cancel',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => this.createFromRates()
    });
  }

  createFromRates(): void {
    if (!this.canManage || this.isCreatingFromRates) return;
    this.isCreatingFromRates = true;
    this.service.createTaxCodesFromRatesInUse().subscribe({
      next: (res) => {
        this.isCreatingFromRates = false;
        this.fromRatesResult = res.result ?? { created: [], skippedRates: [] };
        this.fromRatesMessage = res.message;
        if (this.fromRatesResult.created.length) this.load();
      },
      error: (err) => {
        this.isCreatingFromRates = false;
        this.messages.add({ severity: 'error', summary: 'Nothing created', detail: setupErrorMessage(err, 'The codes could not be created.') });
      }
    });
  }

  dismissFromRates(): void {
    this.fromRatesResult = null;
    this.fromRatesMessage = '';
  }

  skippedText(rates: number[]): string {
    return rates.map(r => `${formatPercent(r)}%`).join(', ');
  }
}
