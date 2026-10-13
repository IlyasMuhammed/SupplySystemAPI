import { Component, EventEmitter, Input, OnChanges, Output, SimpleChanges, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { DialogModule } from 'primeng/dialog';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { SelectModule } from 'primeng/select';
import { TextareaModule } from 'primeng/textarea';

import { Account, AccountDetail, AccountType, AccountingService } from '../../../../services/accounting.service';
import {
  AccountDraft, MAX_CODE_LENGTH, MAX_DESCRIPTION_LENGTH, MAX_EXTERNAL_CODE_LENGTH, MAX_LEVEL, MAX_NAME_LENGTH,
  accountProblem, accountTypeOptions, glError
} from '../gl.shared';

/**
 * A42 (0264–0284) — new / edit account. The parent is a group (Level 1–3); level and root type follow from it; the code is
 * suggested by the server (GET suggest-code) and editable on create only; the type list is Appendix A for the root type.
 * The checks mirror the server (D-8, D-23) so the person sees the problem before saving; the server's refusal is shown as is.
 */
@Component({
  selector: 'app-gl-account-dialog',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, DialogModule, InputNumberModule, InputTextModule, SelectModule, TextareaModule],
  template: `
<p-dialog [visible]="visible" (visibleChange)="close($event)" [modal]="true" [draggable]="false" [style]="{ width: '40rem' }"
          [breakpoints]="{ '640px': '96vw' }" [header]="editing ? 'Edit account ' + editing.accountCode : 'New account'">
  <div class="sf-fields">
    <div class="sf-fld" style="grid-column: 1 / -1">
      <label for="gl-parent" class="sf-req">Parent account</label>
      <p-select inputId="gl-parent" [options]="parentOptions" [(ngModel)]="parentUuid" optionLabel="label" optionValue="value" [filter]="true"
                filterBy="label" placeholder="Choose a group account" [disabled]="!!editing || saving" (onChange)="onParentChange()"
                appendTo="body" styleClass="w-full" data-testid="acc-parent"></p-select>
    </div>
    <div class="sf-fld">
      <label>Level</label>
      <input pInputText [value]="level ?? '—'" readonly data-testid="acc-level" />
    </div>
    <div class="sf-fld">
      <label>Root type</label>
      <input pInputText [value]="parent?.rootType ?? '—'" readonly data-testid="acc-root" />
    </div>
    <div class="sf-fld">
      <label for="gl-code" class="sf-req">Account code</label>
      <input pInputText id="gl-code" [(ngModel)]="draft.accountCode" [maxlength]="maxCode" [readonly]="!!editing" [disabled]="saving"
             data-testid="acc-code" />
      <small class="sf-mu" *ngIf="suggesting">Suggesting a code…</small>
    </div>
    <div class="sf-fld">
      <label for="gl-name" class="sf-req">Account name</label>
      <input pInputText id="gl-name" [(ngModel)]="draft.accountName" [maxlength]="maxName" [disabled]="saving || nameLocked" data-testid="acc-name" />
    </div>
    <div class="sf-fld" *ngIf="isLeaf">
      <label for="gl-type">Account type</label>
      <p-select inputId="gl-type" [options]="typeOptions" [(ngModel)]="draft.accountType" optionLabel="label" optionValue="value" [showClear]="true"
                placeholder="Choose a type" appendTo="body" styleClass="w-full" [disabled]="saving" data-testid="acc-type"></p-select>
    </div>
    <div class="sf-fld">
      <label for="gl-ext">External code</label>
      <input pInputText id="gl-ext" [(ngModel)]="draft.externalCode" [maxlength]="maxExt" [disabled]="saving" data-testid="acc-ext" />
    </div>
    <div class="sf-fld">
      <label for="gl-sort">Sort order</label>
      <p-inputnumber inputId="gl-sort" [(ngModel)]="draft.sortOrder" [min]="0" [useGrouping]="false" [disabled]="saving" data-testid="acc-sort"></p-inputnumber>
    </div>
    <div class="sf-fld" style="grid-column: 1 / -1">
      <label for="gl-desc">Description</label>
      <textarea pTextarea id="gl-desc" rows="2" [(ngModel)]="draft.description" [maxlength]="maxDesc" [disabled]="saving" data-testid="acc-desc"></textarea>
    </div>
  </div>
  <div class="sf-mu sf-sm" *ngIf="!isLeaf && level" style="margin-top:.4rem">Level {{ level }} is a group account: it totals the accounts below it and has no type.</div>
  <div class="sf-sm" style="color: var(--sms-danger); margin-top:.5rem" *ngIf="shownProblem" data-testid="acc-problem">{{ shownProblem }}</div>
  <ng-template pTemplate="footer">
    <p-button label="Cancel" [text]="true" (onClick)="close(false)" [disabled]="saving"></p-button>
    <p-button [label]="editing ? 'Save' : 'Create account'" icon="pi pi-check" [loading]="saving" (onClick)="save()" data-testid="acc-save"></p-button>
  </ng-template>
</p-dialog>`
})
export class GlAccountDialogComponent implements OnChanges {
  private readonly api = inject(AccountingService);

  @Input() visible = false;
  @Output() visibleChange = new EventEmitter<boolean>();
  /** Every account in the chart (parents and the duplicate-code check). */
  @Input() accounts: Account[] = [];
  /** The account being edited; null for a new one. */
  @Input() editing: Account | AccountDetail | null = null;
  /** Pre-selected parent for "+ Add" on a group row. */
  @Input() presetParentUuid: string | null = null;
  @Output() saved = new EventEmitter<AccountDetail>();

  readonly maxCode = MAX_CODE_LENGTH;
  readonly maxName = MAX_NAME_LENGTH;
  readonly maxDesc = MAX_DESCRIPTION_LENGTH;
  readonly maxExt = MAX_EXTERNAL_CODE_LENGTH;

  parentUuid: string | null = null;
  draft: AccountDraft = this.blank();
  saving = false;
  suggesting = false;
  tried = false;
  serverError = '';

  ngOnChanges(ch: SimpleChanges): void {
    if (ch['visible'] && this.visible) this.reset();
  }

  get parentOptions() {
    return this.accounts
      .filter(a => a.isGroup && a.level < MAX_LEVEL && a.isActive !== false)
      .sort((a, b) => a.materializedPath?.localeCompare(b.materializedPath ?? '') || a.accountCode.localeCompare(b.accountCode))
      .map(a => ({ value: a.uuid, label: `${'  '.repeat(a.level - 1)}${a.accountCode} · ${a.accountName}` }));
  }

  get parent(): Account | null {
    return this.accounts.find(a => a.uuid === this.parentUuid) ?? null;
  }

  get level(): number | null {
    if (this.editing) return this.editing.level;
    return this.parent ? this.parent.level + 1 : null;
  }

  get isLeaf(): boolean {
    if (this.editing) return !this.editing.isGroup;
    return this.level === MAX_LEVEL;
  }

  /** 3900 Opening Balance Equity cannot be renamed (D-9). */
  get nameLocked(): boolean {
    return !!this.editing?.isSystemAccount && this.editing.accountCode === '3900';
  }

  get typeOptions() {
    return accountTypeOptions(this.editing?.rootType ?? this.parent?.rootType);
  }

  get problem(): string | null {
    const d = { ...this.draft, parent: this.editing ? { uuid: this.editing.parentUuid ?? '', accountCode: '', level: this.editing.level - 1, rootType: this.editing.rootType, isGroup: true } : this.parent };
    return accountProblem(d, this.accounts.map(a => a.accountCode), !!this.editing);
  }

  get shownProblem(): string | null {
    return this.serverError || (this.tried ? this.problem : null);
  }

  private blank(): AccountDraft {
    return { parent: null, accountCode: '', accountName: '', accountType: null, description: '', externalCode: '', sortOrder: null };
  }

  private reset(): void {
    this.tried = false;
    this.serverError = '';
    this.saving = false;
    const e = this.editing;
    if (e) {
      this.parentUuid = e.parentUuid ?? null;
      this.draft = {
        parent: null, accountCode: e.accountCode, accountName: e.accountName, accountType: (e.accountType as AccountType) ?? null,
        description: e.description ?? '', externalCode: e.externalCode ?? '', sortOrder: e.sortOrder ?? null
      };
    } else {
      this.draft = this.blank();
      this.parentUuid = this.presetParentUuid;
      if (this.parentUuid) this.onParentChange();
    }
  }

  /** New parent → the type list changes (a type that no longer fits is cleared) and the next free code is suggested. */
  onParentChange(): void {
    this.serverError = '';
    const p = this.parent;
    if (this.draft.accountType && !accountTypeOptions(p?.rootType).some(o => o.value === this.draft.accountType)) this.draft.accountType = null;
    if (!p) return;
    this.suggesting = true;
    this.api.suggestCode(p.uuid).subscribe({
      next: r => { this.suggesting = false; if (r?.result?.code) this.draft.accountCode = r.result.code; },
      error: () => { this.suggesting = false; }
    });
  }

  close(v: boolean): void {
    if (v) return;
    this.visible = false;
    this.visibleChange.emit(false);
  }

  save(): void {
    this.tried = true;
    this.serverError = '';
    if (this.problem || this.saving) return;
    const d = this.draft;
    const common = {
      accountName: d.accountName.trim(),
      accountType: this.isLeaf ? d.accountType : null,
      description: d.description.trim() || null,
      externalCode: d.externalCode.trim() || null,
      sortOrder: d.sortOrder ?? null
    };
    this.saving = true;
    const call = this.editing
      ? this.api.updateAccount(this.editing.uuid, common)
      : this.api.createAccount({ ...common, parentUuid: this.parentUuid!, accountCode: d.accountCode.trim() });
    call.subscribe({
      next: r => { this.saving = false; if (r?.result) this.saved.emit(r.result); this.close(false); },
      error: err => { this.saving = false; this.serverError = glError(err, 'The account could not be saved.'); }
    });
  }
}
