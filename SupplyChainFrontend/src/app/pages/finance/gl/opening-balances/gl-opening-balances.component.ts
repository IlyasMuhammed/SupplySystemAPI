import { Component, EventEmitter, Input, OnInit, Output, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { CheckboxModule } from 'primeng/checkbox';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { InputNumberModule } from 'primeng/inputnumber';
import { ToastModule } from 'primeng/toast';
import { ConfirmationService, MessageService } from 'primeng/api';

import {
  AccountingService, GL_PERMS, OpeningBalances, OpeningEntryInput, OpeningRow, PostOpeningResult, RootType, SubledgerReference
} from '../../../../services/accounting.service';
import { AuthService } from '../../../service/auth.service';
import { FLOW } from '../../../../shared/flow';
import { OpeningBalanceDateComponent } from '../../../finance-setup/opening-balance-date/opening-balance-date.component';
import { OpeningTotals, differs, glError, money, openingRowProblem, openingTotals, round2 } from '../gl.shared';

interface Group { rootType: RootType; rows: EditRow[]; open: boolean; }
interface EditRow extends OpeningRow { savedDebit: number; savedCredit: number; }

const PL_WARNING = 'Profit & loss balance — only needed when converting part-way through a fiscal year.';

/**
 * A42 (0468–0484) — `/gl/opening-balances`: the conversion date (the A40 opening balance date, edited in place), leaf
 * accounts per root type with Debit / Credit inputs (never both), 3900 as the calculated plug, running totals, the
 * difference acknowledgement, "Skip (new company)", the D-12 sub-ledger reference and Save Draft → Post & Continue.
 * Read-only once posted. Also step 5 of the wizard (`embedded`).
 */
@Component({
  selector: 'app-gl-opening-balances',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, CheckboxModule, ConfirmDialogModule, InputNumberModule, ToastModule, ...FLOW,
    OpeningBalanceDateComponent],
  providers: [ConfirmationService],
  styleUrls: ['../gl.scss'],
  template: `
<p-toast *ngIf="!embedded"></p-toast>
<p-confirmDialog key="gl-ob"></p-confirmDialog>

<ng-template #actions>
  <ng-container *ngIf="canManage && !readOnly">
    <p-button [label]="embedded ? 'Post & continue' : 'Post opening balances'" icon="pi pi-send" [disabled]="!canPost" [loading]="posting"
              (onClick)="confirmPost()" data-testid="post"></p-button>
    <p-button label="Save draft" icon="pi pi-save" [text]="true" [disabled]="!dirty || saving || skip || !!rowProblem" [loading]="saving"
              (onClick)="saveDraft()" data-testid="save-draft"></p-button>
  </ng-container>
  <span class="sf-pill ok" *ngIf="data?.isPosted" data-testid="posted">Posted</span>
  <p-button icon="pi pi-refresh" [text]="true" (onClick)="load()" ariaLabel="Refresh"></p-button>
</ng-template>

<ng-template #content>
  <app-opening-balance-date (changed)="load()"></app-opening-balance-date>

  <div class="sf-empty sf-card" *ngIf="loading"><i class="pi pi-spin pi-spinner"></i> Loading…</div>
  <div class="gl-note er" *ngIf="loadError" data-testid="load-error"><i class="pi pi-exclamation-triangle"></i><div>{{ loadError }}</div></div>
  <div class="gl-note wn" *ngIf="data && !data.conversionDate && !data.isPosted" data-testid="no-date">
    <i class="pi pi-calendar"></i><div>Set the opening balance date above first. It must fall within the first fiscal year.</div>
  </div>
  <div class="gl-note ok" *ngIf="data?.isPosted && data?.isSkipped" data-testid="skipped-note">
    <i class="pi pi-check-circle"></i><div>Opening balances were skipped (new company) — the ledger starts at zero.<ng-container *ngIf="data?.postedAt"> ({{ data!.postedAt | date:'d MMM y' }})</ng-container></div>
  </div>
  <div class="gl-note ok" *ngIf="data?.isPosted && !data?.isSkipped" data-testid="posted-note">
    <i class="pi pi-check-circle"></i><div>Opening balances were posted<ng-container *ngIf="data?.conversionDate"> as at {{ data!.conversionDate | date:'d MMM y' }}</ng-container><ng-container *ngIf="data?.postedAt"> on {{ data!.postedAt | date:'d MMM y, HH:mm' }}</ng-container><ng-container *ngIf="journalNumber"> ({{ journalNumber }})</ng-container>. They can no longer be changed.</div>
  </div>

  <ng-container *ngIf="data">
    <section class="sf-card" *ngIf="canManage && !readOnly">
      <label class="gl-check"><p-checkbox [(ngModel)]="skip" [binary]="true" inputId="ob-skip" data-testid="skip"></p-checkbox>
        <span>Skip opening balances (new company) — the ledger starts at zero.</span></label>
      <div class="sf-sm" style="color: var(--sms-warn); margin-top:.35rem" *ngIf="skip && draftRows.length" data-testid="skip-drafts">
        {{ draftRows.length }} saved draft balance(s) will be cleared first.</div>
    </section>

    <ng-container *ngIf="!skip">
      <section class="sf-card p0" *ngFor="let g of groups" [attr.data-testid]="'group-' + g.rootType">
        <div class="sf-ch sf-pad">
          <button type="button" class="gl-toggle" (click)="g.open = !g.open" [attr.aria-expanded]="g.open" [attr.aria-label]="'Toggle ' + g.rootType">
            <i class="pi" [ngClass]="g.open ? 'pi-chevron-down' : 'pi-chevron-right'"></i></button>
          <h2 style="flex:1">{{ groupLabel(g.rootType) }} <span class="sf-count">{{ g.rows.length }}</span></h2>
          <span class="sf-mu sf-sm gl-num">Dr {{ groupDebit(g) | number:'1.2-2' }} · Cr {{ groupCredit(g) | number:'1.2-2' }}</span>
        </div>
        <div class="gl-note wn" *ngIf="g.open && isPl(g.rootType)" style="margin:0 1rem .5rem"><i class="pi pi-info-circle"></i><div>{{ plWarning }}</div></div>
        <div class="gl-scroll" *ngIf="g.open">
          <table class="gl-obtable">
            <thead><tr><th>Account</th><th class="gl-num">Debit</th><th class="gl-num">Credit</th></tr></thead>
            <tbody>
              <tr *ngFor="let r of g.rows" [class.gl-plug]="r.isPlug" [attr.data-testid]="'ob-' + r.accountCode">
                <td>
                  <span class="sf-mono">{{ r.accountCode }}</span> {{ r.accountName }}
                  <span class="sf-pill" *ngIf="r.isPlug" data-testid="calculated">calculated</span>
                  <div class="sf-sm" style="color: var(--sms-warn)" *ngIf="!r.isPlug && r.warning">{{ r.warning }}</div>
                  <div class="sf-sm" style="color: var(--sms-danger)" *ngIf="problemOf(r)" data-testid="row-problem">{{ problemOf(r) }}</div>
                </td>
                <ng-container *ngIf="r.isPlug; else editable">
                  <td class="gl-num" data-testid="plug-debit">{{ totals.plugSide === 'Debit' ? (totals.plugAmount | number:'1.2-2') : '' }}</td>
                  <td class="gl-num" data-testid="plug-credit">{{ totals.plugSide === 'Credit' ? (totals.plugAmount | number:'1.2-2') : '' }}</td>
                </ng-container>
                <ng-template #editable>
                  <td class="gl-num">
                    <p-inputnumber *ngIf="!readOnly && canManage; else roDr" [(ngModel)]="r.debitAmount" (ngModelChange)="onAmount(r, 'debit')" mode="decimal"
                                   [minFractionDigits]="2" [maxFractionDigits]="2" [min]="0" locale="en-US" styleClass="gl-ob-input"
                                   [inputStyle]="{ 'text-align': 'right', width: '100%' }" [ariaLabel]="'Debit ' + r.accountCode"
                                   [attr.data-testid]="'dr-' + r.accountCode"></p-inputnumber>
                    <ng-template #roDr>{{ r.debitAmount ? (r.debitAmount | number:'1.2-2') : '' }}</ng-template>
                  </td>
                  <td class="gl-num">
                    <p-inputnumber *ngIf="!readOnly && canManage; else roCr" [(ngModel)]="r.creditAmount" (ngModelChange)="onAmount(r, 'credit')" mode="decimal"
                                   [minFractionDigits]="2" [maxFractionDigits]="2" [min]="0" locale="en-US" styleClass="gl-ob-input"
                                   [inputStyle]="{ 'text-align': 'right', width: '100%' }" [ariaLabel]="'Credit ' + r.accountCode"
                                   [attr.data-testid]="'cr-' + r.accountCode"></p-inputnumber>
                    <ng-template #roCr>{{ r.creditAmount ? (r.creditAmount | number:'1.2-2') : '' }}</ng-template>
                  </td>
                </ng-template>
              </tr>
            </tbody>
          </table>
        </div>
      </section>

      <section class="sf-card" data-testid="totals">
        <div class="gl-totals">
          <div><span class="l">Total debits</span><span class="v" data-testid="total-debits">{{ totals.totalDebits | number:'1.2-2' }}</span></div>
          <div><span class="l">Total credits</span><span class="v" data-testid="total-credits">{{ totals.totalCredits | number:'1.2-2' }}</span></div>
          <div><span class="l">Difference</span><span class="v" [ngClass]="totals.difference ? 'er' : 'ok'" data-testid="difference">{{ totals.difference | number:'1.2-2' }}</span></div>
          <div><span class="l">→ To Opening Balance Equity (3900)</span>
            <span class="v" data-testid="to-obe">{{ totals.plugAmount | number:'1.2-2' }}<ng-container *ngIf="totals.plugSide"> {{ totals.plugSide === 'Debit' ? 'Dr' : 'Cr' }}</ng-container></span></div>
        </div>
        <div class="sf-mu sf-sm" style="margin-top:.4rem" *ngIf="!readOnly" data-testid="entry-count">
          {{ data.summary.entryCount ?? draftRows.length }} draft {{ (data.summary.entryCount ?? draftRows.length) === 1 ? 'entry' : 'entries' }} saved<ng-container *ngIf="dirty"> · unsaved changes</ng-container>.</div>
        <ng-container *ngIf="totals.difference && !readOnly">
          <div class="gl-note wn" style="margin-top:.75rem" data-testid="difference-warning">
            <i class="pi pi-exclamation-triangle"></i>
            <div>Debits and credits differ by {{ money(totals.plugAmount) }}. Posting puts the difference into 3900 Opening Balance Equity
              as a {{ totals.plugSide === 'Debit' ? 'debit' : 'credit' }}. Check for a missing or mistyped balance first.</div>
          </div>
          <label class="gl-check" style="margin-top:.5rem" *ngIf="canManage">
            <p-checkbox [(ngModel)]="acknowledge" [binary]="true" inputId="ob-ack" data-testid="acknowledge"></p-checkbox>
            <span>I confirm this difference is intentional</span></label>
        </ng-container>
      </section>
    </ng-container>

    <section class="sf-card" data-testid="subledger" *ngIf="data.subledger">
      <div class="sf-ch"><div><h2>Sub-ledger reference</h2>
        <div class="sf-mu sf-sm">The opening totals imported for customers, suppliers and stock, next to the balance of their default GL account.
          For information only — a difference never blocks posting.</div></div></div>
      <div class="gl-scroll">
        <table class="gl-obtable">
          <thead><tr><th></th><th class="gl-num">Sub-ledger</th><th class="gl-num">GL default account</th><th></th></tr></thead>
          <tbody>
            <tr *ngFor="let s of subledgerRows" [attr.data-testid]="'sub-' + s.key">
              <td>{{ s.label }}</td>
              <td class="gl-num">{{ s.sub | number:'1.2-2' }}</td>
              <td class="gl-num">{{ s.gl == null ? '—' : (s.gl | number:'1.2-2') }}
                <div class="sf-mu sf-sm" *ngIf="s.code">{{ s.code }}</div><div class="sf-mu sf-sm" *ngIf="!s.code">no default account</div></td>
              <td><span class="sf-pill wn" *ngIf="s.differs" data-testid="differs">differs</span></td>
            </tr>
          </tbody>
        </table>
      </div>
      <div class="sf-mu sf-sm" *ngIf="data.subledger?.currencyCode">Amounts in {{ data.subledger.currencyCode }}.</div>
    </section>
  </ng-container>
</ng-template>

<sf-page *ngIf="!embedded" [crumbs]="['Finance', 'General Ledger', 'Opening Balances']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands"><ng-container *ngTemplateOutlet="actions"></ng-container></div>
  <div class="sf-body">
    <div class="sf-lr-head"><div><h1>Opening balances</h1>
      <div class="sf-sub">What each account held on the conversion date — debits must equal credits, any difference goes to 3900</div></div></div>
    <ng-container *ngTemplateOutlet="content"></ng-container>
  </div>
</sf-page>
<div *ngIf="embedded" class="gl-embedded">
  <div class="sf-cmd" role="toolbar" aria-label="Opening balance commands"><ng-container *ngTemplateOutlet="actions"></ng-container></div>
  <ng-container *ngTemplateOutlet="content"></ng-container>
</div>`,
  styles: [`
    .gl-embedded { display: flex; flex-direction: column; gap: .75rem; }
    .gl-obtable { width: 100%; min-width: 34rem; border-collapse: collapse; }
    .gl-obtable th { text-align: left; font-size: .75rem; color: var(--sms-text-muted); font-weight: 600; padding: .4rem 1rem; }
    .gl-obtable th.gl-num { text-align: right; }
    .gl-obtable td { padding: .35rem 1rem; border-top: 1px solid var(--sms-border-soft); vertical-align: middle; }
    .sf-ch.sf-pad { display: flex; align-items: center; gap: .4rem; }
  `]
})
export class GlOpeningBalancesComponent implements OnInit {
  private readonly api = inject(AccountingService);
  private readonly auth = inject(AuthService);
  private readonly confirm = inject(ConfirmationService);
  private readonly messages = inject(MessageService, { optional: true });

  @Input() embedded = false;
  @Output() changed = new EventEmitter<void>();
  /** Posted (or skipped): the wizard moves on. */
  @Output() posted = new EventEmitter<PostOpeningResult | null>();

  data: OpeningBalances | null = null;
  groups: Group[] = [];
  loading = false;
  loadError = '';
  saving = false;
  posting = false;
  skip = false;
  acknowledge = false;
  journalNumber: string | null = null;
  /** Per-row refusals of the last batch save / post (400 result.errors), by account uuid. */
  rowErrors: Record<string, string> = {};

  readonly plWarning = PL_WARNING;
  readonly money = money;

  get canManage(): boolean { return this.auth.hasPermission(GL_PERMS.SETUP_MANAGE); }
  get readOnly(): boolean { return !!this.data?.isPosted; }
  get rows(): EditRow[] { return this.groups.flatMap(g => g.rows); }
  get totals(): OpeningTotals { return openingTotals(this.rows); }
  get rowProblem(): string | null { for (const r of this.rows) { const p = openingRowProblem(r); if (p) return p; } return null; }
  get dirty(): boolean { return this.rows.some(r => !r.isPlug && (round2(r.debitAmount) !== r.savedDebit || round2(r.creditAmount) !== r.savedCredit)); }

  /** Post needs a conversion date, valid rows, and the acknowledgement when debits ≠ credits (D-19); Skip needs nothing more. */
  get canPost(): boolean {
    if (!this.data || this.readOnly || this.posting || this.saving) return false;
    if (this.skip) return true;
    if (!this.data.conversionDate || this.rowProblem) return false;
    return !this.totals.difference || this.acknowledge;
  }

  get subledgerRows() {
    const s: SubledgerReference | null | undefined = this.data?.subledger;
    if (!s) return [];
    return [
      { key: 'ar', label: 'Customers (A/R)', sub: s.customerOpeningTotal, gl: s.receivableAccountBalance, code: s.receivableAccountCode },
      { key: 'ap', label: 'Suppliers (A/P)', sub: s.supplierOpeningTotal, gl: s.payableAccountBalance, code: s.payableAccountCode },
      { key: 'inv', label: 'Opening stock (Inventory)', sub: s.openingStockValue, gl: s.inventoryAccountBalance, code: s.inventoryAccountCode }
    ].map(x => ({ ...x, differs: differs(x.sub, x.gl) }));
  }

  ngOnInit(): void { this.load(); }

  load(): void {
    this.loading = true;
    this.loadError = '';
    this.api.getOpeningBalances().subscribe({
      next: r => { this.loading = false; this.apply(r?.result ?? null); },
      error: err => { this.loading = false; this.loadError = glError(err, 'The opening balances could not be loaded.'); }
    });
  }

  private apply(d: OpeningBalances | null): void {
    const open = new Map(this.groups.map(g => [g.rootType, g.open]));
    this.data = d;
    this.groups = (d?.groups ?? []).map(g => ({
      rootType: g.rootType,
      open: open.get(g.rootType) ?? true,
      rows: (g.rows ?? []).map(r => ({
        ...r,
        debitAmount: round2(r.debitAmount), creditAmount: round2(r.creditAmount),
        savedDebit: round2(r.debitAmount), savedCredit: round2(r.creditAmount),
        warning: r.warning ?? null
      }))
    }));
  }

  groupLabel(t: RootType): string {
    return t === 'Asset' ? 'Assets' : t === 'Liability' ? 'Liabilities' : t === 'Expense' ? 'Expenses' : t;
  }

  isPl(t: RootType): boolean { return t === 'Revenue' || t === 'Expense'; }
  groupDebit(g: Group): number { return round2(g.rows.filter(r => !r.isPlug).reduce((s, r) => s + Number(r.debitAmount || 0), 0)); }
  groupCredit(g: Group): number { return round2(g.rows.filter(r => !r.isPlug).reduce((s, r) => s + Number(r.creditAmount || 0), 0)); }
  problemOf(r: EditRow): string | null { return openingRowProblem(r) ?? this.rowErrors[r.accountUuid] ?? null; }

  /** Saved draft entries (D-56: the server refuses Skip while any exist). */
  get draftRows(): EditRow[] { return this.readOnly ? [] : this.rows.filter(r => !r.isPlug && (r.savedDebit || r.savedCredit)); }

  private takeRowErrors(err: unknown): void {
    const body = (err as any)?.error;
    const list = body?.result?.errors ?? body?.errors;
    this.rowErrors = {};
    if (Array.isArray(list)) for (const e of list) if (e?.accountUuid) this.rowErrors[e.accountUuid] = e.message;
  }

  /** One side per row: typing a debit clears the credit and vice versa (D-19 "not both sides"). */
  onAmount(r: EditRow, side: 'debit' | 'credit'): void {
    if (side === 'debit' && Number(r.debitAmount) > 0) r.creditAmount = 0;
    if (side === 'credit' && Number(r.creditAmount) > 0) r.debitAmount = 0;
    if (r.debitAmount === null || r.debitAmount === undefined) r.debitAmount = 0;
    if (r.creditAmount === null || r.creditAmount === undefined) r.creditAmount = 0;
    this.acknowledge = false; // a changed difference must be confirmed again
    delete this.rowErrors[r.accountUuid];
  }

  /** The non-plug rows that have an amount, plus those that had one (zero rows delete the draft entry). */
  entries(): OpeningEntryInput[] {
    return this.rows
      .filter(r => !r.isPlug && (round2(r.debitAmount) || round2(r.creditAmount) || r.savedDebit || r.savedCredit))
      .map(r => ({ accountUuid: r.accountUuid, debitAmount: round2(r.debitAmount), creditAmount: round2(r.creditAmount), notes: r.notes ?? null }));
  }

  saveDraft(then?: () => void): void {
    if (this.rowProblem) { this.toast('error', this.rowProblem); return; }
    this.saving = true;
    this.api.saveOpeningBalances(this.entries()).subscribe({
      next: r => {
        this.saving = false;
        this.rowErrors = {};
        if (r?.result) this.apply(r.result);
        this.changed.emit();
        if (then) then(); else this.toast('success', 'The draft is saved.');
      },
      error: err => { this.saving = false; this.takeRowErrors(err); this.toast('error', glError(err, 'The opening balances could not be saved.')); }
    });
  }

  confirmPost(): void {
    if (!this.canPost) return;
    const t = this.totals;
    const drafts = this.draftRows.length;
    const message = this.skip && drafts
      ? `This will clear ${drafts} saved draft balance${drafts === 1 ? '' : 's'} and post zero opening balances.`
      : this.skip ? 'Skip opening balances? The ledger starts at zero and this step is marked done. It cannot be undone except by resetting the setup.'
      : `Post opening balances as at ${this.data?.conversionDate ?? '—'}? Debits ${money(t.totalDebits)}, credits ${money(t.totalCredits)}`
        + (t.difference ? `, ${money(t.plugAmount)} to Opening Balance Equity.` : '.')
        + ' Posted balances cannot be changed and the opening balance date is locked.';
    this.confirm.confirm({
      key: 'gl-ob', header: this.skip ? 'Skip opening balances' : 'Post opening balances', icon: 'pi pi-send', message,
      acceptLabel: this.skip ? 'Skip' : 'Post', rejectLabel: 'Cancel',
      accept: () => this.skip ? (drafts ? this.clearDraftsThenSkip() : this.post())
        : this.dirty ? this.saveDraft(() => this.post()) : this.post()
    });
  }

  /** D-56 — Skip is refused while drafts exist: zero every saved draft (the batch PUT deletes it), then post { skip: true }. */
  clearDraftsThenSkip(): void {
    const entries = this.draftRows.map(r => ({ accountUuid: r.accountUuid, debitAmount: 0, creditAmount: 0, notes: r.notes ?? null }));
    this.saving = true;
    this.api.saveOpeningBalances(entries).subscribe({
      next: r => { this.saving = false; if (r?.result) this.apply(r.result); this.changed.emit(); this.post(); },
      error: err => { this.saving = false; this.toast('error', glError(err, 'The draft balances could not be cleared.')); }
    });
  }

  post(): void {
    this.posting = true;
    const body = this.skip ? { skip: true } : { acknowledgeDifference: !!this.totals.difference && this.acknowledge };
    this.api.postOpeningBalances(body).subscribe({
      next: r => {
        this.posting = false;
        this.rowErrors = {};
        this.journalNumber = r?.result?.journalEntryNumber ?? null;
        this.toast('success', this.skip ? 'Opening balances skipped.' : `Opening balances posted${this.journalNumber ? ' (' + this.journalNumber + ')' : ''}.`);
        this.skip = false;
        this.changed.emit();
        this.posted.emit(r?.result ?? null);
        this.load();
      },
      error: err => { this.posting = false; this.takeRowErrors(err); this.toast('error', glError(err, 'The opening balances could not be posted.')); }
    });
  }

  private toast(severity: 'success' | 'error', detail: string): void {
    this.messages?.add({ severity, summary: severity === 'success' ? 'Done' : 'Error', detail, life: severity === 'error' ? 8000 : 3000 });
  }
}
