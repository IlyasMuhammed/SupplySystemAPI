import { Component, EventEmitter, Input, OnInit, Output, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { SelectModule } from 'primeng/select';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';
import { catchError, of } from 'rxjs';

import {
  Account, AccountingDefault, AccountingDefaults, AccountingService, DEFAULT_OPENING_BALANCE, DefaultsValidation, GL_PERMS
} from '../../../../services/accounting.service';
import { AuthService } from '../../../service/auth.service';
import { FLOW } from '../../../../shared/flow';
import { accountTypeLabel, glError } from '../gl.shared';

/** "X of Y required defaults assigned", from the current (unsaved) selections. */
export function requiredSummary(items: AccountingDefault[], selection: Record<string, string | null>): { assigned: number; required: number } {
  const req = items.filter(i => i.isRequired);
  // BE2: a row whose saved account has a `problem` (inactive / group / missing) counts as unassigned until it is changed.
  const assigned = (i: AccountingDefault) => {
    const sel = selection[i.defaultKey] ?? null;
    return !!sel && !(i.problem && sel === (i.account?.uuid ?? null));
  };
  return { required: req.length, assigned: req.filter(assigned).length };
}

/**
 * A42 (0337–0373 UI) — `/gl/defaults`: the default accounts grouped by category; each row a leaf-account picker (filterable)
 * with a Required / Optional badge; DEFAULT_OPENING_BALANCE locked to 3900; Validate All (problems + warnings); Save Defaults;
 * "X of Y required defaults assigned". Also step 4 of the wizard (`embedded`).
 */
@Component({
  selector: 'app-gl-defaults',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, SelectModule, ToastModule, TooltipModule, ...FLOW],
  styleUrls: ['../gl.scss'],
  template: `
<p-toast *ngIf="!embedded"></p-toast>

<ng-template #actions>
  <p-button *ngIf="canManage" label="Save defaults" icon="pi pi-save" [disabled]="!dirty || saving" [loading]="saving" (onClick)="save()"
            data-testid="save-defaults"></p-button>
  <p-button label="Validate all" icon="pi pi-check-square" [text]="true" [loading]="validating" (onClick)="validate()" data-testid="validate"></p-button>
  <span class="sf-mu sf-sm" *ngIf="dirty">Unsaved changes — Validate checks the saved defaults.</span>
  <span class="sf-end"><span class="sf-pill" [ngClass]="summary.assigned === summary.required ? 'ok' : 'wn'" data-testid="required-summary">
    {{ summary.assigned }} of {{ summary.required }} required defaults assigned</span></span>
</ng-template>

<ng-template #content>
  <div class="sf-empty sf-card" *ngIf="loading"><i class="pi pi-spin pi-spinner"></i> Loading…</div>
  <div class="gl-note er" *ngIf="loadError" data-testid="load-error"><i class="pi pi-exclamation-triangle"></i><div>{{ loadError }}</div></div>
  <div class="gl-note wn" *ngIf="!loading && !accounts.length && !loadError" data-testid="no-accounts">
    <i class="pi pi-info-circle"></i><div>There are no postable accounts yet. Create the chart of accounts first.</div>
  </div>

  <div class="gl-note" *ngIf="validation" [ngClass]="validation.isValid && !validation.warnings.length ? 'ok' : validation.isValid ? 'wn' : 'er'" data-testid="validation">
    <i class="pi" [ngClass]="validation.isValid ? 'pi-check-circle' : 'pi-times-circle'"></i>
    <div>
      <strong>{{ validation.isValid ? 'Defaults are valid.' : 'Some defaults need attention.' }}</strong>
      <ul *ngIf="validation.missingRequired.length || validation.problems.length || validation.warnings.length">
        <li *ngFor="let k of validation.missingRequired" data-testid="v-missing">{{ labelOf(k) }}: required, not assigned.</li>
        <li *ngFor="let p of validation.problems" data-testid="v-problem">{{ labelOf(p.defaultKey) }}: {{ p.message }}</li>
        <li *ngFor="let w of validation.warnings" data-testid="v-warning" class="sf-mu">Warning — {{ labelOf(w.defaultKey) }}: {{ w.message }}</li>
      </ul>
    </div>
  </div>

  <section class="sf-card p0" *ngFor="let c of data?.categories" [attr.data-testid]="'cat-' + c.category">
    <div class="sf-ch sf-pad"><h2>{{ c.category }}</h2></div>
    <div class="gl-scroll">
      <table class="gl-dtable">
        <tbody>
          <tr *ngFor="let d of c.items" [attr.data-testid]="'def-' + d.defaultKey">
            <td class="gl-dlabel">
              <div><strong>{{ d.defaultLabel }}</strong>
                <i *ngIf="d.helpText" class="pi pi-info-circle sf-mu" [pTooltip]="d.helpText" tooltipPosition="top" style="font-size:.8rem"></i></div>
              <div class="sf-mu sf-sm" *ngIf="d.expectedAccountType">Expected type: {{ typeLabel(d.expectedAccountType) }}</div>
            </td>
            <td class="gl-dpick">
              <ng-container *ngIf="!isLocked(d); else locked">
                <p-select [options]="accountOptions" [(ngModel)]="selection[d.defaultKey]" optionLabel="label" optionValue="value" [filter]="true"
                          filterBy="label" [showClear]="!d.isRequired || !!selection[d.defaultKey]" placeholder="Choose an account" appendTo="body"
                          [disabled]="!canManage || saving" styleClass="w-full" [attr.data-testid]="'pick-' + d.defaultKey"></p-select>
                <div class="sf-sm" style="color: var(--sms-warn)" *ngIf="mismatch(d)" data-testid="type-mismatch">
                  Type is {{ typeLabel(mismatch(d)) }}, expected {{ typeLabel(d.expectedAccountType) }} (allowed, check it is intended).</div>
                <div class="sf-sm" style="color: var(--sms-danger)" *ngIf="saveProblems[d.defaultKey] || (d.problem && !rowChanged(d))" data-testid="row-problem">
                  {{ saveProblems[d.defaultKey] || d.problem }}</div>
                <div class="sf-sm" style="color: var(--sms-warn)" *ngIf="d.warning && !rowChanged(d) && !mismatch(d)" data-testid="row-warning">{{ d.warning }}</div>
              </ng-container>
              <ng-template #locked>
                <span data-testid="locked-default"><i class="pi pi-lock sf-mu"></i>
                  <span class="sf-mono">{{ d.account?.accountCode || '3900' }}</span> {{ d.account?.accountName || 'Opening Balance Equity' }}
                  <span class="sf-mu sf-sm"> · fixed</span></span>
              </ng-template>
            </td>
            <td class="gl-dbadge"><span class="sf-pill" [ngClass]="d.isRequired ? 'in' : ''" data-testid="badge">{{ d.isRequired ? 'Required' : 'Optional' }}</span></td>
          </tr>
        </tbody>
      </table>
    </div>
  </section>
</ng-template>

<sf-page *ngIf="!embedded" [crumbs]="['Finance', 'General Ledger', 'Defaults']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands"><ng-container *ngTemplateOutlet="actions"></ng-container></div>
  <div class="sf-body">
    <div class="sf-lr-head"><div><h1>Accounting defaults</h1>
      <div class="sf-sub">Where automatic postings go — receivables, payables, tax, inventory, cash and more</div></div></div>
    <ng-container *ngTemplateOutlet="content"></ng-container>
  </div>
</sf-page>
<div *ngIf="embedded" class="gl-embedded">
  <div class="sf-cmd" role="toolbar" aria-label="Defaults commands"><ng-container *ngTemplateOutlet="actions"></ng-container></div>
  <ng-container *ngTemplateOutlet="content"></ng-container>
</div>`,
  styles: [`
    .gl-embedded { display: flex; flex-direction: column; gap: .75rem; }
    .gl-dtable { width: 100%; min-width: 40rem; border-collapse: collapse; }
    .gl-dtable td { padding: .55rem 1rem; border-top: 1px solid var(--sms-border-soft); vertical-align: middle; }
    .gl-dlabel { width: 40%; }
    .gl-dpick { width: 45%; }
    .gl-dbadge { text-align: right; white-space: nowrap; }
  `]
})
export class GlDefaultsComponent implements OnInit {
  private readonly api = inject(AccountingService);
  private readonly auth = inject(AuthService);
  private readonly messages = inject(MessageService, { optional: true });

  @Input() embedded = false;
  @Output() changed = new EventEmitter<void>();

  data: AccountingDefaults | null = null;
  accounts: Account[] = [];
  selection: Record<string, string | null> = {};
  saved: Record<string, string | null> = {};
  validation: DefaultsValidation | null = null;
  loading = false;
  loadError = '';
  saving = false;
  validating = false;
  /** Per-row refusals of the last save-all (400 result.problems), by default key. */
  saveProblems: Record<string, string> = {};

  readonly typeLabel = accountTypeLabel;

  get canManage(): boolean { return this.auth.hasPermission(GL_PERMS.SETUP_MANAGE); }

  get items(): AccountingDefault[] { return (this.data?.categories ?? []).flatMap(c => c.items); }

  get summary() { return requiredSummary(this.items, this.selection); }

  get dirty(): boolean {
    return this.items.some(i => !this.isLocked(i) && (this.selection[i.defaultKey] ?? null) !== (this.saved[i.defaultKey] ?? null));
  }

  get accountOptions() {
    return this.accounts.map(a => ({ value: a.uuid, label: `${a.accountCode} · ${a.accountName}${a.accountType ? ' (' + accountTypeLabel(a.accountType) + ')' : ''}` }));
  }

  ngOnInit(): void { this.load(); }

  load(): void {
    this.loading = true;
    this.loadError = '';
    this.api.getPostableAccounts().pipe(catchError(() => of([] as Account[]))).subscribe(a => this.accounts = a);
    this.api.getDefaults().subscribe({
      next: r => { this.loading = false; this.apply(r?.result ?? null); },
      error: err => { this.loading = false; this.loadError = glError(err, 'The accounting defaults could not be loaded.'); }
    });
  }

  private apply(d: AccountingDefaults | null): void {
    this.data = d;
    this.selection = {};
    for (const i of this.items) this.selection[i.defaultKey] = i.account?.uuid ?? null;
    this.saved = { ...this.selection };
  }

  isLocked(d: AccountingDefault): boolean { return d.isLocked || d.defaultKey === DEFAULT_OPENING_BALANCE; }

  /** The row's selection differs from what was saved (the server's per-row problem no longer applies). */
  rowChanged(d: AccountingDefault): boolean { return (this.selection[d.defaultKey] ?? null) !== (this.saved[d.defaultKey] ?? null); }

  labelOf(key: string): string { return this.items.find(i => i.defaultKey === key)?.defaultLabel ?? key; }

  /** The chosen account's type when it differs from the expected one (D-24: a warning, not a refusal). */
  mismatch(d: AccountingDefault): string | null {
    const uuid = this.selection[d.defaultKey];
    if (!uuid || !d.expectedAccountType) return null;
    const a = this.accounts.find(x => x.uuid === uuid);
    return a?.accountType && a.accountType !== d.expectedAccountType ? a.accountType : null;
  }

  save(): void {
    if (!this.canManage || this.saving) return;
    const items = this.items.filter(i => !this.isLocked(i)).map(i => ({ defaultKey: i.defaultKey, accountUuid: this.selection[i.defaultKey] ?? null }));
    this.saving = true;
    this.api.saveDefaults(items).subscribe({
      next: r => {
        this.saving = false;
        this.saveProblems = {};
        if (r?.result) this.apply(r.result); else this.saved = { ...this.selection };
        this.messages?.add({ severity: 'success', summary: 'Saved', detail: 'The accounting defaults are saved.', life: 3000 });
        this.changed.emit();
        this.validate();
      },
      error: err => {
        this.saving = false;
        // BE2 — save-all is all-or-nothing: 400 { message, result: { problems: { defaultKey, message }[] } }.
        const list = (err as any)?.error?.result?.problems ?? (err as any)?.error?.problems;
        this.saveProblems = {};
        if (Array.isArray(list)) for (const p of list) if (p?.defaultKey) this.saveProblems[p.defaultKey] = p.message;
        this.messages?.add({ severity: 'error', summary: 'Not saved', detail: glError(err, 'The defaults could not be saved.'), life: 8000 });
      }
    });
  }

  validate(): void {
    this.validating = true;
    this.api.validateDefaults().subscribe({
      next: r => {
        this.validating = false;
        const v = r?.result;
        this.validation = v ? { isValid: !!v.isValid, missingRequired: v.missingRequired ?? [], problems: v.problems ?? [], warnings: v.warnings ?? [] } : null;
      },
      error: err => { this.validating = false; this.messages?.add({ severity: 'error', summary: 'Error', detail: glError(err, 'The defaults could not be validated.'), life: 8000 }); }
    });
  }
}
