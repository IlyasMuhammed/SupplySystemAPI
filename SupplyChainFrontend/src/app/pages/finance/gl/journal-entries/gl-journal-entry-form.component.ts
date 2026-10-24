import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { AutoCompleteCompleteEvent, AutoCompleteModule } from 'primeng/autocomplete';
import { ButtonModule } from 'primeng/button';
import { DatePickerModule } from 'primeng/datepicker';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { SelectModule } from 'primeng/select';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';
import { catchError, of } from 'rxjs';

import { Account, AccountingService, FiscalYear } from '../../../../services/accounting.service';
import { BusinessPartnerModel, BusinessPartnerService } from '../../../../services/business-partner.service';
import {
  GL_JOURNAL_PERMS, GL_JOURNAL_ROUTE, GlJournalService, JournalEntryCreate, JournalEntryDetail, JournalEntryLineInput, JournalType, PartnerType
} from '../../../../services/gl-journal.service';
import { OrgCurrencyService } from '../../../../services/org-currency.service';
import { AuthService } from '../../../service/auth.service';
import { FLOW } from '../../../../shared/flow';
import { fromIsoDate, toIsoDate } from '../../../finance-setup/finance-setup.shared';
import { glError, money } from '../gl.shared';
import {
  EntryDraft, LineDraft, balancingAmount, entryProblem, entryTotals, isBlankLine, journalErrors, kindLabel, lineProblem, partnerTypeFor
} from '../journal.shared';

let lineKey = 0;
function blankLine(partnerType: PartnerType | null = null): LineDraft {
  return { key: ++lineKey, accountUuid: null, description: '', debit: null, credit: null, partnerUuid: null, partnerName: null, partnerType };
}

function newRequestId(): string {
  const c = (globalThis as { crypto?: Crypto }).crypto;
  if (c?.randomUUID) return c.randomUUID();
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, ch => {
    const r = Math.random() * 16 | 0;
    return (ch === 'x' ? r : (r & 0x3) | 0x8).toString(16);
  });
}

/**
 * A47 C6 (tasks 255–259) — `/gl/journal-entries/new` and `/:uuid/edit`: a manual journal entry — header (journal type,
 * dates, reference, description, currency + rate) and dynamic lines (leaf-account picker, description, partner where the
 * type needs one, debit / credit), running totals and a warning while debits and credits differ. Saved as a draft, or
 * saved and posted with GL_JOURNAL_POST (an auto-post type always posts). A second click never creates a second entry
 * (clientRequestId, D-FE-3).
 */
@Component({
  selector: 'app-gl-journal-entry-form',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule, AutoCompleteModule, ButtonModule, DatePickerModule, InputNumberModule, InputTextModule,
    SelectModule, ToastModule, TooltipModule, ...FLOW],
  providers: [MessageService],
  styleUrls: ['../gl.scss'],
  template: `
<p-toast></p-toast>
<sf-page [crumbs]="['Finance', 'General Ledger', 'Journal Entries', editUuid ? (entry?.entryNumber || 'Edit') : 'New manual entry']">
  <header class="sf-hdr sf-compact">
    <h1>{{ editUuid ? 'Edit ' + (entry?.entryNumber || 'draft entry') : 'New manual entry' }}</h1>
  </header>
  <div class="sf-body">
    <div class="sf-card sf-empty" *ngIf="loading"><i class="pi pi-spin pi-spinner"></i> Loading…</div>
    <div class="gl-note er" *ngIf="loadError" data-testid="load-error"><i class="pi pi-exclamation-triangle"></i><div>{{ loadError }}</div></div>

    <ng-container *ngIf="!loading && !loadError">
      <section class="sf-card">
        <div class="sf-ch"><h2>Entry</h2></div>
        <div class="sf-fields">
          <div class="sf-fld">
            <label for="je-type" class="sf-req">Journal type</label>
            <p-select inputId="je-type" [options]="typeOptions" [(ngModel)]="draft.journalTypeUuid" (ngModelChange)="onTypeChange()"
                      optionLabel="label" optionValue="value" [disabled]="!!editUuid" appendTo="body" placeholder="Choose…" data-testid="je-type"></p-select>
            <span class="gl-sub" *ngIf="type?.requiresPartner">Every line needs a {{ partnerWord }}.</span>
            <span class="gl-sub" *ngIf="type?.autoPost">Entries of this type are posted when saved.</span>
          </div>
          <div class="sf-fld">
            <label for="je-date" class="sf-req">Entry date</label>
            <p-datepicker inputId="je-date" [(ngModel)]="draft.entryDate" dateFormat="dd M yy" [showIcon]="true" appendTo="body" data-testid="je-date"></p-datepicker>
          </div>
          <div class="sf-fld">
            <label for="je-docdate">Document date</label>
            <p-datepicker inputId="je-docdate" [(ngModel)]="draft.documentDate" dateFormat="dd M yy" [showIcon]="true" [showClear]="true" appendTo="body"
                          data-testid="je-docdate"></p-datepicker>
          </div>
          <div class="sf-fld">
            <label for="je-ref">Reference</label>
            <input pInputText id="je-ref" [(ngModel)]="draft.reference" maxlength="200" data-testid="je-ref" />
          </div>
          <div class="sf-fld" style="grid-column: 1 / -1">
            <label for="je-desc">Description</label>
            <input pInputText id="je-desc" [(ngModel)]="draft.description" maxlength="500" placeholder="What the entry is for" data-testid="je-desc" />
          </div>
          <div class="sf-fld">
            <label for="je-cur">Currency</label>
            <p-select inputId="je-cur" [options]="currencyOptions" [(ngModel)]="draft.currencyCode" appendTo="body" [filter]="true"
                      [placeholder]="baseCurrency || 'Base currency'" data-testid="je-currency"></p-select>
          </div>
          <div class="sf-fld" *ngIf="foreign">
            <label for="je-rate" class="sf-req">Exchange rate</label>
            <p-inputnumber inputId="je-rate" [(ngModel)]="draft.exchangeRate" [minFractionDigits]="2" [maxFractionDigits]="6" [min]="0" data-testid="je-rate"></p-inputnumber>
            <span class="gl-sub">1 {{ draft.currencyCode }} = {{ draft.exchangeRate || '?' }} {{ baseCurrency }}</span>
          </div>
        </div>
      </section>

      <section class="sf-card p0">
        <div class="sf-ch sf-pad"><h2>Lines <span class="sf-count">{{ filledLines }}</span></h2>
          <span class="sf-end"><p-button label="Add line" icon="pi pi-plus" [text]="true" size="small" (onClick)="addLine()" data-testid="add-line"></p-button></span></div>
        <div class="gl-scroll">
          <table class="je-lines" data-testid="lines">
            <thead><tr><th style="width:2.5rem">#</th><th>Account</th><th>Description</th><th *ngIf="showPartner">{{ partnerHeading }}</th>
              <th class="gl-num">Debit</th><th class="gl-num">Credit</th><th style="width:5rem"></th></tr></thead>
            <tbody>
              <tr *ngFor="let l of draft.lines; let i = index; trackBy: trackLine" [attr.data-testid]="'line-' + i" [class.je-bad]="tried && lineError(l)">
                <td class="sf-mu">{{ i + 1 }}</td>
                <td class="je-acc">
                  <p-select [options]="accountOptions" [(ngModel)]="l.accountUuid" optionLabel="label" optionValue="value" [filter]="true" filterBy="label"
                            appendTo="body" placeholder="Account…" styleClass="w-full" [attr.data-testid]="'acc-' + i"></p-select>
                  <div class="je-err" *ngIf="tried && lineError(l)" [attr.data-testid]="'line-error-' + i">{{ lineError(l) }}</div>
                </td>
                <td><input pInputText [(ngModel)]="l.description" maxlength="500" class="w-full" [attr.data-testid]="'desc-' + i" /></td>
                <td *ngIf="showPartner" class="je-partner">
                  <p-select *ngIf="!fixedPartnerType" [options]="partnerTypeOptions" [(ngModel)]="l.partnerType" (ngModelChange)="clearPartner(l)"
                            optionLabel="label" optionValue="value" appendTo="body" placeholder="Type" styleClass="je-ptype"></p-select>
                  <p-autoComplete [ngModel]="partnerValue(l)" (ngModelChange)="onPartnerText(l, $event)" [suggestions]="partnerSuggestions"
                                  optionLabel="companyName" (completeMethod)="searchPartners(l, $event)" (onSelect)="pickPartner(l, $event.value)"
                                  (onClear)="clearPartner(l)" [minLength]="1" [disabled]="!(l.partnerType || fixedPartnerType)" appendTo="body"
                                  placeholder="Search…" [attr.data-testid]="'partner-' + i"></p-autoComplete>
                </td>
                <td class="je-amt"><p-inputnumber [(ngModel)]="l.debit" (ngModelChange)="onAmount(l, 'debit')" [minFractionDigits]="2" [maxFractionDigits]="2"
                                  [min]="0" inputStyleClass="je-num" [attr.data-testid]="'dr-' + i"></p-inputnumber></td>
                <td class="je-amt"><p-inputnumber [(ngModel)]="l.credit" (ngModelChange)="onAmount(l, 'credit')" [minFractionDigits]="2" [maxFractionDigits]="2"
                                  [min]="0" inputStyleClass="je-num" [attr.data-testid]="'cr-' + i"></p-inputnumber></td>
                <td class="je-acts">
                  <p-button icon="pi pi-equals" [text]="true" size="small" severity="secondary" pTooltip="Balance the entry on this line"
                            (onClick)="balanceOn(i)" [attr.data-testid]="'balance-' + i"></p-button>
                  <p-button icon="pi pi-trash" [text]="true" size="small" severity="danger" pTooltip="Remove line" [disabled]="draft.lines.length <= 2"
                            (onClick)="removeLine(i)" [attr.data-testid]="'remove-' + i"></p-button>
                </td>
              </tr>
            </tbody>
            <tfoot>
              <tr class="sf-tot">
                <td [attr.colspan]="showPartner ? 4 : 3">Total <span class="sf-mu sf-sm">{{ draft.currencyCode || baseCurrency || '' }}</span></td>
                <td class="gl-num" data-testid="total-debit">{{ amount(totals.debit) }}</td>
                <td class="gl-num" data-testid="total-credit">{{ amount(totals.credit) }}</td>
                <td></td>
              </tr>
              <tr>
                <td [attr.colspan]="showPartner ? 4 : 3" class="sf-mu">Difference</td>
                <td colspan="2" class="gl-num" [class.je-diff]="!totals.balanced" data-testid="difference">{{ amount(totals.difference) }}</td>
                <td></td>
              </tr>
            </tfoot>
          </table>
        </div>
        <div class="gl-note wn je-inset" *ngIf="!totals.balanced && totals.debit + totals.credit > 0" data-testid="balance-warning">
          <i class="pi pi-exclamation-triangle"></i>
          <div>Debits and credits differ by {{ amount(abs(totals.difference)) }}. The entry can only be saved when they agree.</div>
        </div>
      </section>

      <div class="gl-note er" *ngIf="shownProblem || serverErrors.length" data-testid="problem">
        <i class="pi pi-exclamation-triangle"></i>
        <div>{{ shownProblem }}<ul *ngIf="serverErrors.length"><li *ngFor="let e of serverErrors">{{ e }}</li></ul></div>
      </div>
    </ng-container>
  </div>

  <div class="sf-fbar" *ngIf="!loading && !loadError">
    <span class="sf-mu sf-sm">{{ hint }}</span>
    <span class="sf-end">
      <p-button label="Cancel" [text]="true" (onClick)="cancel()" data-testid="cancel"></p-button>
      <p-button *ngIf="!type?.autoPost" label="Save draft" icon="pi pi-save" [outlined]="canPost" [loading]="saving && !posting" [disabled]="saving"
                (onClick)="save(false)" data-testid="save-draft"></p-button>
      <span class="sf-sm je-diff" *ngIf="autoPostBlocked" data-testid="autopost-no-perm">
        {{ type?.code }} entries are posted when saved, and you may not post journal entries. Choose another journal type.</span>
      <p-button *ngIf="canPost || type?.autoPost" label="Save and post" icon="pi pi-check" [loading]="saving && posting" [disabled]="saving || autoPostBlocked"
                (onClick)="save(true)" data-testid="save-post"></p-button>
    </span>
  </div>
</sf-page>`,
  styles: [`
    .je-lines { width: 100%; min-width: 60rem; border-collapse: collapse; font-size: .875rem; }
    .je-lines th, .je-lines td { padding: .35rem .5rem; border-top: 1px solid var(--sms-border-soft); text-align: left; vertical-align: top; }
    .je-lines th.gl-num, .je-lines td.gl-num { text-align: right; }
    .je-lines tfoot td { font-weight: 600; }
    .je-acc { min-width: 16rem; }
    .je-partner { min-width: 14rem; display: flex; gap: .25rem; }
    .je-amt { width: 9rem; }
    :host ::ng-deep .je-num { width: 8.5rem; text-align: right; }
    :host ::ng-deep .je-ptype { width: 6.5rem; }
    .je-acts { white-space: nowrap; }
    .je-err { color: var(--sms-danger); font-size: .75rem; margin-top: .15rem; }
    tr.je-bad td { background: var(--sms-danger-soft); }
    .je-diff { color: var(--sms-danger); }
    .je-inset { margin: .5rem 1rem .75rem; }
  `]
})
export class GlJournalEntryFormComponent implements OnInit {
  private readonly api = inject(GlJournalService);
  private readonly accounting = inject(AccountingService);
  private readonly partners = inject(BusinessPartnerService);
  private readonly currencies = inject(OrgCurrencyService);
  private readonly auth = inject(AuthService);
  private readonly messages = inject(MessageService);
  private readonly router = inject(Router);
  private readonly activated = inject(ActivatedRoute);

  editUuid: string | null = null;
  entry: JournalEntryDetail | null = null;
  types: JournalType[] = [];
  typeOptions: { label: string; value: string }[] = [];
  accountOptions: { label: string; value: string }[] = [];
  currencyOptions: string[] = [];
  baseCurrency: string | null = null;
  private years: FiscalYear[] = [];
  partnerSuggestions: BusinessPartnerModel[] = [];
  readonly partnerTypeOptions = [{ label: 'Customer', value: 'Customer' }, { label: 'Vendor', value: 'Vendor' }];

  draft: EntryDraft = {
    journalTypeUuid: null, entryDate: new Date(), documentDate: null, reference: '', description: '', currencyCode: null, exchangeRate: null,
    lines: [blankLine(), blankLine()]
  };
  /** One id per form: a double click or a retry after a lost answer returns the same entry (key manual:{id}). */
  readonly clientRequestId = newRequestId();

  loading = false;
  loadError = '';
  tried = false;
  saving = false;
  posting = false;
  serverError = '';
  serverErrors: string[] = [];

  get canPost(): boolean { return this.auth.hasPermission(GL_JOURNAL_PERMS.POST); }
  get type(): JournalType | null { return this.types.find(t => t.uuid === this.draft.journalTypeUuid) ?? null; }
  /** REV-16 — an auto-post type always posts on save (GL_JOURNAL_POST); without the code it would only 403. */
  get autoPostBlocked(): boolean { return !!this.type?.autoPost && !this.canPost; }
  get fixedPartnerType(): PartnerType | null { return partnerTypeFor(this.type?.type); }
  /** D-FE-5 — the partner column is always there: required on a requires-partner type, optional (AR/AP lines) otherwise. */
  readonly showPartner = true;
  get partnerWord(): string { return this.fixedPartnerType === 'Vendor' ? 'vendor' : this.fixedPartnerType === 'Customer' ? 'customer' : 'partner'; }
  get partnerHeading(): string { return this.fixedPartnerType === 'Vendor' ? 'Vendor' : this.fixedPartnerType === 'Customer' ? 'Customer' : 'Partner'; }
  get effectiveBase(): string | null {
    const d = this.draft.entryDate ? toIsoDate(this.draft.entryDate) : null;
    const y = d ? this.years.find(x => x.startDate <= d && d <= x.endDate) : null;
    return y?.reportingCurrencyCode ?? this.baseCurrency;
  }
  get foreign(): boolean { return !!this.draft.currencyCode && !!this.effectiveBase && this.draft.currencyCode !== this.effectiveBase; }
  get totals() { return entryTotals(this.draft.lines); }
  get filledLines(): number { return this.draft.lines.filter(l => !isBlankLine(l)).length; }
  get problem(): string | null { return entryProblem(this.draft, !!this.type?.requiresPartner, this.effectiveBase); }
  get shownProblem(): string | null { return this.serverError || (this.tried ? this.problem : null); }
  get hint(): string {
    const t = this.totals;
    if (!t.debit && !t.credit) return 'Enter at least two lines: debits on one side, credits on the other.';
    return t.balanced ? 'Balanced.' : `Out of balance by ${money(Math.abs(t.difference))}.`;
  }

  ngOnInit(): void {
    this.editUuid = this.activated.snapshot.paramMap.get('uuid');
    this.api.getJournalTypes(true).pipe(catchError(() => of([] as JournalType[]))).subscribe(list => {
      this.types = [...list].filter(t => t.isActive !== false).sort((a, b) => (a.sortOrder ?? 0) - (b.sortOrder ?? 0));
      this.typeOptions = this.types.map(t => ({ label: `${t.code} — ${t.name} (${kindLabel(t.type)})`, value: t.uuid }));
      if (!this.editUuid && !this.draft.journalTypeUuid) {
        // D-FE-4 — a new manual entry starts on the Miscellaneous journal (JV).
        const misc = this.types.find(t => t.code === 'MISC') ?? this.types.find(t => t.type === 'Miscellaneous');
        if (misc) { this.draft.journalTypeUuid = misc.uuid; this.onTypeChange(); }
      }
      this.ensureTypeOption();
    });
    this.accounting.getPostableAccounts().pipe(catchError(() => of([] as Account[]))).subscribe(list => {
      this.accountOptions = list.map(a => ({ label: `${a.accountCode} — ${a.accountName}`, value: a.uuid }));
      this.ensureAccountOptions();
    });
    this.accounting.getFiscalYears().pipe(catchError(() => of(null))).subscribe(r => this.years = r?.result ?? []);
    this.currencies.getCurrencies(false).pipe(catchError(() => of(null))).subscribe(r => {
      const codes = (r?.result ?? []).filter(c => c.isActive).map(c => c.code);
      this.currencyOptions = [...new Set([...this.currencyOptions, ...codes])];
    });
    this.currencies.getSettings().pipe(catchError(() => of(null))).subscribe(s => {
      this.baseCurrency = s?.result?.saleBaseCurrencyCode ?? null;
      if (this.baseCurrency && !this.currencyOptions.includes(this.baseCurrency)) this.currencyOptions = [this.baseCurrency, ...this.currencyOptions];
    });
    if (this.editUuid) this.loadEntry(this.editUuid);
  }

  private loadEntry(uuid: string): void {
    this.loading = true;
    this.api.getEntry(uuid).subscribe({
      next: e => {
        this.loading = false;
        this.entry = e;
        if (e.status !== 'Draft' || e.canEdit === false) {
          this.loadError = e.status !== 'Draft' ? `${e.entryNumber} is ${e.status.toLowerCase()} and cannot be changed.` : 'You cannot edit this entry.';
          return;
        }
        this.draft = {
          journalTypeUuid: e.journalType?.uuid ?? null, entryDate: fromIsoDate(e.entryDate), documentDate: fromIsoDate(e.documentDate),
          reference: e.reference ?? '', description: e.description ?? '', currencyCode: e.currencyCode ?? null, exchangeRate: e.exchangeRate ?? null,
          lines: e.lines.map(l => {
            // Lines come back in base; a foreign entry's typed amounts are |amountCurrency|.
            const fx = l.amountCurrency != null && l.currencyCode && l.currencyCode !== this.effectiveBase;
            const dr = fx ? (l.amountCurrency! > 0 ? l.amountCurrency! : null) : (l.debit || null);
            const cr = fx ? (l.amountCurrency! < 0 ? -l.amountCurrency! : null) : (l.credit || null);
            return { key: ++lineKey, accountUuid: l.account?.uuid ?? null, description: l.description ?? '', debit: dr, credit: cr,
              partnerUuid: l.partnerUuid ?? null, partnerName: l.partnerName ?? null, partnerType: (l.partnerType as PartnerType) ?? null };
          })
        };
        while (this.draft.lines.length < 2) this.draft.lines.push(blankLine());
        if (e.currencyCode && !this.currencyOptions.includes(e.currencyCode)) this.currencyOptions = [...this.currencyOptions, e.currencyCode];
        this.ensureTypeOption();
        this.ensureAccountOptions();
      },
      error: err => { this.loading = false; this.loadError = glError(err, 'The journal entry could not be loaded.'); }
    });
  }

  /** An inactive type / account an existing draft uses still shows by name. */
  private ensureTypeOption(): void {
    const t = this.entry?.journalType;
    if (t && !this.typeOptions.some(o => o.value === t.uuid)) this.typeOptions = [...this.typeOptions, { label: `${t.code} — ${t.name}`, value: t.uuid }];
  }

  private ensureAccountOptions(): void {
    for (const l of this.entry?.lines ?? []) {
      const a = l.account;
      if (a?.uuid && !this.accountOptions.some(o => o.value === a.uuid)) this.accountOptions = [...this.accountOptions, { label: `${a.accountCode} — ${a.accountName}`, value: a.uuid }];
    }
  }

  onTypeChange(): void {
    const fixed = this.fixedPartnerType;
    const t = this.type;
    for (const l of this.draft.lines) {
      if (fixed && l.partnerType !== fixed) { l.partnerType = fixed; l.partnerUuid = null; l.partnerName = null; }
    }
    // Default accounts of the type go on the first two empty lines (debit, credit).
    if (t && !this.editUuid) {
      const [a, b] = this.draft.lines;
      if (t.defaultDebitAccount && a && !a.accountUuid) a.accountUuid = t.defaultDebitAccount.uuid;
      if (t.defaultCreditAccount && b && !b.accountUuid) b.accountUuid = t.defaultCreditAccount.uuid;
      for (const ref of [t.defaultDebitAccount, t.defaultCreditAccount]) {
        if (ref && !this.accountOptions.some(o => o.value === ref.uuid)) this.accountOptions = [...this.accountOptions, { label: `${ref.accountCode} — ${ref.accountName}`, value: ref.uuid }];
      }
    }
  }

  trackLine(_: number, l: LineDraft): number { return l.key; }
  lineError(l: LineDraft): string | null { return isBlankLine(l) ? null : lineProblem(l, !!this.type?.requiresPartner); }
  amount(n: number): string { return money(n); }
  abs(n: number): number { return Math.abs(n); }

  addLine(): void { this.draft.lines = [...this.draft.lines, blankLine(this.fixedPartnerType)]; }

  removeLine(i: number): void {
    if (this.draft.lines.length <= 2) return;
    this.draft.lines = this.draft.lines.filter((_, j) => j !== i);
  }

  /** Typing on one side clears the other (BR-JE-003). */
  onAmount(l: LineDraft, side: 'debit' | 'credit'): void {
    if (side === 'debit' && Number(l.debit) > 0) l.credit = null;
    if (side === 'credit' && Number(l.credit) > 0) l.debit = null;
  }

  balanceOn(i: number): void {
    const b = balancingAmount(this.draft.lines, i);
    this.draft.lines[i].debit = b.debit;
    this.draft.lines[i].credit = b.credit;
  }

  // ── Partner picker ──
  partnerValue(l: LineDraft): BusinessPartnerModel | string | null {
    return l.partnerUuid ? ({ uuid: l.partnerUuid, companyName: l.partnerName ?? '' } as BusinessPartnerModel) : null;
  }

  searchPartners(l: LineDraft, e: AutoCompleteCompleteEvent): void {
    const type = this.fixedPartnerType ?? l.partnerType;
    this.partners.getPartners({ ...(type === 'Vendor' ? { isVendor: true } : { isCustomer: true }), active: true, search: e.query, pageSize: 20 })
      .pipe(catchError(() => of(null))).subscribe(r => this.partnerSuggestions = r?.result?.data ?? []);
  }

  pickPartner(l: LineDraft, p: BusinessPartnerModel): void {
    l.partnerUuid = p?.uuid ?? null;
    l.partnerName = p?.companyName ?? null;
    l.partnerType = this.fixedPartnerType ?? l.partnerType;
  }

  /** Typed text that was not picked from the list is no partner. */
  onPartnerText(l: LineDraft, v: BusinessPartnerModel | string | null): void {
    if (!v || typeof v === 'string') { l.partnerUuid = null; l.partnerName = null; }
  }

  clearPartner(l: LineDraft): void { l.partnerUuid = null; l.partnerName = null; }

  // ── Save ──
  private lineInputs(): JournalEntryLineInput[] {
    return this.draft.lines.filter(l => !isBlankLine(l)).map(l => ({
      accountUuid: l.accountUuid!, debit: Number(l.debit || 0), credit: Number(l.credit || 0),
      description: l.description.trim() || null,
      ...(l.partnerUuid ? { partnerUuid: l.partnerUuid, partnerType: this.fixedPartnerType ?? l.partnerType ?? 'Customer' } : {})
    }));
  }

  save(post: boolean): void {
    this.tried = true;
    this.serverError = '';
    this.serverErrors = [];
    if (this.problem || this.saving) return;
    if (this.autoPostBlocked) { this.serverError = 'You do not have permission to post journal entries.'; return; }
    const d = this.draft;
    const kind = this.type?.type;
    // REV-17 — an edited draft keeps its own flags (its type cannot change and may be inactive / not in the list);
    // the Opening / Closing kind still forces its flag.
    const opening = (this.editUuid ? !!this.entry?.isOpeningEntry : false) || kind === 'Opening';
    const closing = (this.editUuid ? !!this.entry?.isClosingEntry : false) || kind === 'Closing';
    const header = {
      entryDate: toIsoDate(d.entryDate!), documentDate: d.documentDate ? toIsoDate(d.documentDate) : null,
      reference: d.reference.trim() || null, description: d.description.trim() || null,
      currencyCode: d.currencyCode || null, exchangeRate: this.foreign ? Number(d.exchangeRate) : null,
      isOpeningEntry: opening, isClosingEntry: closing,
      lines: this.lineInputs()
    };
    this.saving = true;
    this.posting = post;
    if (this.editUuid) {
      const uuid = this.editUuid;
      this.api.updateEntry(uuid, header).subscribe({
        next: e => post ? this.post(e?.uuid ?? uuid, e?.entryNumber) : this.done(e?.uuid ?? uuid, `${e?.entryNumber ?? 'The draft'} is saved.`),
        error: err => this.fail(err)
      });
      return;
    }
    const body: JournalEntryCreate = { journalTypeUuid: d.journalTypeUuid!, ...header, clientRequestId: this.clientRequestId, postImmediately: post };
    this.api.createEntry(body).subscribe({
      next: e => this.done(e.uuid, e.status === 'Posted' ? `${e.entryNumber} is posted.` : `${e.entryNumber} is saved as a draft.`),
      error: err => this.fail(err)
    });
  }

  private post(uuid: string, number?: string): void {
    this.api.postEntry(uuid).subscribe({
      next: e => this.done(uuid, `${e?.entryNumber ?? number ?? 'The entry'} is posted.`),
      // Saved but not posted: stay on the draft, say why.
      error: err => { this.saving = false; this.serverError = `Saved as a draft, but not posted: ${glError(err, 'the entry could not be posted.')}`; }
    });
  }

  private done(uuid: string, text: string): void {
    this.saving = false;
    this.messages.add({ severity: 'success', summary: 'Saved', detail: text, life: 3000 });
    this.router.navigate([GL_JOURNAL_ROUTE, uuid]);
  }

  private fail(err: unknown): void {
    this.saving = false;
    this.serverError = glError(err, 'The journal entry could not be saved.');
    this.serverErrors = journalErrors(err).filter(e => e !== this.serverError);
  }

  cancel(): void {
    this.router.navigate(this.editUuid ? [GL_JOURNAL_ROUTE, this.editUuid] : [GL_JOURNAL_ROUTE]);
  }
}
