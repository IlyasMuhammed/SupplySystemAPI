import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { CheckboxModule } from 'primeng/checkbox';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { DialogModule } from 'primeng/dialog';
import { InputTextModule } from 'primeng/inputtext';
import { SelectModule } from 'primeng/select';
import { TableModule } from 'primeng/table';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { ConfirmationService, MessageService } from 'primeng/api';
import { Observable, catchError, of } from 'rxjs';

import { Account, AccountingService } from '../../../../services/accounting.service';
import { GL_JOURNAL_PERMS, GlJournalService, JOURNAL_TYPE_KINDS, JournalType, JournalTypeSave } from '../../../../services/gl-journal.service';
import { AuthService } from '../../../service/auth.service';
import { FLOW } from '../../../../shared/flow';
import { glError } from '../gl.shared';
import { TypeDraft, kindLabel, numberPreview, typeProblem } from '../journal.shared';

const EMPTY: TypeDraft = {
  code: '', name: '', type: 'Miscellaneous', sequencePrefix: '', defaultDebitAccountUuid: null, defaultCreditAccountUuid: null,
  requiresPartner: false, autoPost: false
};

/**
 * A47 C1 (tasks 264–269) — `/gl/journal-types`: the organization's journal types (code, name, number prefix, status),
 * New / Edit for GL_SETUP_MANAGE, a lock on system types (code and kind fixed, never deleted, only deactivated), and a
 * confirmation before deactivating. A custom type nobody used yet can also be deleted.
 */
@Component({
  selector: 'app-gl-journal-types',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, CheckboxModule, ConfirmDialogModule, DialogModule, InputTextModule, SelectModule,
    TableModule, ToastModule, TooltipModule, ...FLOW],
  providers: [MessageService, ConfirmationService],
  styleUrls: ['../gl.scss'],
  template: `
<p-toast></p-toast>
<p-confirmDialog key="gl-jt" [style]="{ width: '30rem', maxWidth: '96vw' }"></p-confirmDialog>
<sf-page [crumbs]="['Finance', 'General Ledger', 'Journal Types']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button *ngIf="canManage" label="New" icon="pi pi-plus" (onClick)="openNew()" data-testid="new-type"></p-button>
    <p-button icon="pi pi-refresh" label="Refresh" [text]="true" (onClick)="load()" data-testid="refresh"></p-button>
  </div>
  <div class="sf-body">
    <div class="sf-lr-head"><div><h1>Journal types <span class="sf-count">{{ types.length }}</span></h1>
      <div class="sf-sub">Classify journal entries and number them per type (prefix/year/sequence)</div></div></div>

    <div class="gl-note er" *ngIf="loadError" data-testid="load-error"><i class="pi pi-exclamation-triangle"></i><div>{{ loadError }}</div></div>

    <section class="sf-card p0">
      <div class="gl-scroll">
        <p-table [value]="types" [loading]="loading" styleClass="p-datatable-sm" [tableStyle]="{ 'min-width': '48rem' }" dataKey="uuid" data-testid="types">
          <ng-template pTemplate="header">
            <tr><th>Code</th><th>Name</th><th>Kind</th><th>Prefix</th><th>Partner</th><th>Status</th><th style="width:12rem"></th></tr>
          </ng-template>
          <ng-template pTemplate="body" let-t>
            <tr [attr.data-testid]="'type-' + t.code" [class.gl-inactive]="!t.isActive">
              <td class="sf-mono">{{ t.code }}
                <i *ngIf="t.isSystem" class="pi pi-lock sf-mu" pTooltip="System type: cannot be deleted, only deactivated" [attr.data-testid]="'lock-' + t.code"></i></td>
              <td>{{ t.name }}<div class="sf-mu sf-sm" *ngIf="t.autoPost">posts automatically</div></td>
              <td>{{ kind(t.type) }}</td>
              <td class="sf-mono">{{ t.sequencePrefix }}</td>
              <td>{{ t.requiresPartner ? 'Required' : '—' }}</td>
              <td><span class="sf-pill" [ngClass]="t.isActive ? 'ok' : ''" [attr.data-testid]="'status-' + t.code">{{ t.isActive ? 'Active' : 'Inactive' }}</span></td>
              <td class="text-right">
                <span class="gl-actions" *ngIf="canManage">
                  <p-button label="Edit" icon="pi pi-pencil" [text]="true" size="small" (onClick)="openEdit(t)" [attr.data-testid]="'edit-' + t.code"></p-button>
                  <p-button *ngIf="t.isActive" label="Deactivate" [text]="true" size="small" severity="danger" [disabled]="busy === t.uuid"
                            (onClick)="confirmDeactivate(t)" [attr.data-testid]="'deactivate-' + t.code"></p-button>
                  <p-button *ngIf="!t.isActive" label="Activate" [text]="true" size="small" [disabled]="busy === t.uuid"
                            (onClick)="activate(t)" [attr.data-testid]="'activate-' + t.code"></p-button>
                  <p-button *ngIf="deletable(t)" icon="pi pi-trash" [text]="true" size="small" severity="danger" pTooltip="Delete"
                            [disabled]="busy === t.uuid" (onClick)="confirmDelete(t)" [attr.data-testid]="'delete-' + t.code"></p-button>
                </span>
              </td>
            </tr>
          </ng-template>
          <ng-template pTemplate="emptymessage">
            <tr><td colspan="7" class="sf-empty" data-testid="empty">No journal type yet. The system types are created when the general ledger setup completes.</td></tr>
          </ng-template>
        </p-table>
      </div>
      <div class="gl-foot"><span><i class="pi pi-lock"></i> System types cannot be deleted, only deactivated; their code and kind are fixed.</span>
        <span>Custom types can be edited fully and deleted until they are used.</span></div>
    </section>
  </div>
</sf-page>

<p-dialog [(visible)]="dialogVisible" [modal]="true" [draggable]="false" [style]="{ width: '40rem' }" [breakpoints]="{ '640px': '96vw' }"
          [header]="editing ? 'Edit ' + editing.code : 'New journal type'">
  <div class="sf-fields">
    <div class="sf-fld">
      <label for="jt-code" class="sf-req">Code</label>
      <input pInputText id="jt-code" [(ngModel)]="draft.code" (ngModelChange)="draft.code = upper($event)" maxlength="10"
             [disabled]="!!editing?.isSystem" placeholder="PROJ" data-testid="jt-code" />
    </div>
    <div class="sf-fld">
      <label for="jt-name" class="sf-req">Name</label>
      <input pInputText id="jt-name" [(ngModel)]="draft.name" maxlength="100" placeholder="Project billing" data-testid="jt-name" />
    </div>
    <div class="sf-fld">
      <label for="jt-kind" class="sf-req">Kind</label>
      <p-select inputId="jt-kind" [options]="kindOptions" [(ngModel)]="draft.type" optionLabel="label" optionValue="value" appendTo="body"
                [disabled]="!!editing?.isSystem" data-testid="jt-kind"></p-select>
    </div>
    <div class="sf-fld">
      <label for="jt-prefix" class="sf-req">Number prefix</label>
      <input pInputText id="jt-prefix" [(ngModel)]="draft.sequencePrefix" (ngModelChange)="draft.sequencePrefix = upper($event)" maxlength="10"
             [disabled]="!!editing?.isUsed" placeholder="JV" data-testid="jt-prefix" />
      <span class="gl-sub" *ngIf="preview">Numbers look like <span class="sf-mono">{{ preview }}</span></span>
      <span class="gl-sub" *ngIf="editing?.isUsed">Fixed: entries were numbered with it.</span>
    </div>
    <div class="sf-fld">
      <label for="jt-dr">Default debit account</label>
      <p-select inputId="jt-dr" [options]="accountOptions" [(ngModel)]="draft.defaultDebitAccountUuid" optionLabel="label" optionValue="value"
                [filter]="true" filterBy="label" [showClear]="true" appendTo="body" placeholder="None" data-testid="jt-dr"></p-select>
    </div>
    <div class="sf-fld">
      <label for="jt-cr">Default credit account</label>
      <p-select inputId="jt-cr" [options]="accountOptions" [(ngModel)]="draft.defaultCreditAccountUuid" optionLabel="label" optionValue="value"
                [filter]="true" filterBy="label" [showClear]="true" appendTo="body" placeholder="None" data-testid="jt-cr"></p-select>
    </div>
    <div class="sf-fld" style="grid-column: 1 / -1">
      <label class="gl-check"><p-checkbox [(ngModel)]="draft.requiresPartner" [binary]="true" data-testid="jt-partner"></p-checkbox>
        Every line needs a partner (customer or vendor)</label>
      <label class="gl-check"><p-checkbox [(ngModel)]="draft.autoPost" [binary]="true" data-testid="jt-autopost"></p-checkbox>
        Post entries automatically (no draft stage)</label>
    </div>
  </div>
  <div class="sf-sm" style="color: var(--sms-danger); margin-top:.5rem" *ngIf="shownProblem" data-testid="jt-problem">{{ shownProblem }}</div>
  <ng-template pTemplate="footer">
    <p-button label="Cancel" [text]="true" (onClick)="dialogVisible = false"></p-button>
    <p-button [label]="editing ? 'Save' : 'Create'" icon="pi pi-check" [loading]="saving" (onClick)="save()" data-testid="jt-save"></p-button>
  </ng-template>
</p-dialog>`
})
export class GlJournalTypesComponent implements OnInit {
  private readonly api = inject(GlJournalService);
  private readonly accounting = inject(AccountingService);
  private readonly auth = inject(AuthService);
  private readonly confirm = inject(ConfirmationService);
  private readonly messages = inject(MessageService);

  types: JournalType[] = [];
  loading = false;
  loadError = '';
  busy: string | null = null;

  accountOptions: { label: string; value: string }[] = [];
  readonly kindOptions = JOURNAL_TYPE_KINDS.map(k => ({ label: kindLabel(k), value: k }));

  dialogVisible = false;
  editing: JournalType | null = null;
  draft: TypeDraft = { ...EMPTY };
  tried = false;
  saving = false;
  serverError = '';

  get canManage(): boolean { return this.auth.hasPermission(GL_JOURNAL_PERMS.SETUP_MANAGE); }
  get problem(): string | null { return typeProblem(this.draft, this.types, this.editing?.uuid ?? null); }
  get shownProblem(): string | null { return this.serverError || (this.tried ? this.problem : null); }
  get preview(): string { return numberPreview(this.draft.sequencePrefix); }

  ngOnInit(): void {
    this.load();
    if (this.canManage) {
      this.accounting.getPostableAccounts().pipe(catchError(() => of([] as Account[]))).subscribe(list => {
        this.accountOptions = list.map(a => ({ label: `${a.accountCode} — ${a.accountName}`, value: a.uuid }));
      });
    }
  }

  load(): void {
    this.loading = true;
    this.loadError = '';
    this.api.getJournalTypes().subscribe({
      next: list => {
        this.loading = false;
        this.types = [...list].sort((a, b) => (a.sortOrder ?? 0) - (b.sortOrder ?? 0) || a.code.localeCompare(b.code));
      },
      error: err => { this.loading = false; this.loadError = glError(err, 'The journal types could not be loaded.'); }
    });
  }

  kind(k: string): string { return kindLabel(k); }
  upper(v: string): string { return (v ?? '').toUpperCase(); }

  /** A custom type nobody used (the server's canDelete wins when sent). */
  deletable(t: JournalType): boolean { return t.canDelete ?? (!t.isSystem && !t.isUsed); }

  openNew(): void {
    this.editing = null;
    this.draft = { ...EMPTY };
    this.open();
  }

  openEdit(t: JournalType): void {
    this.editing = t;
    this.draft = {
      code: t.code, name: t.name, type: t.type, sequencePrefix: t.sequencePrefix,
      defaultDebitAccountUuid: t.defaultDebitAccount?.uuid ?? null, defaultCreditAccountUuid: t.defaultCreditAccount?.uuid ?? null,
      requiresPartner: !!t.requiresPartner, autoPost: !!t.autoPost
    };
    for (const a of [t.defaultDebitAccount, t.defaultCreditAccount]) {
      if (a && !this.accountOptions.some(o => o.value === a.uuid)) this.accountOptions = [...this.accountOptions, { label: `${a.accountCode} — ${a.accountName}`, value: a.uuid }];
    }
    this.open();
  }

  private open(): void {
    this.tried = false;
    this.serverError = '';
    this.dialogVisible = true;
  }

  save(): void {
    this.tried = true;
    this.serverError = '';
    if (this.problem || this.saving) return;
    const d = this.draft;
    const body: JournalTypeSave = {
      code: d.code.trim(), name: d.name.trim(), type: d.type!, sequencePrefix: d.sequencePrefix.trim(),
      defaultDebitAccountUuid: d.defaultDebitAccountUuid, defaultCreditAccountUuid: d.defaultCreditAccountUuid,
      requiresPartner: d.requiresPartner, autoPost: d.autoPost, sortOrder: this.editing?.sortOrder ?? null
    };
    this.saving = true;
    const call = this.editing ? this.api.updateJournalType(this.editing.uuid, body) : this.api.createJournalType(body);
    call.subscribe({
      next: () => {
        this.saving = false;
        this.dialogVisible = false;
        this.toast('success', `Journal type ${body.code} is saved.`);
        this.load();
      },
      error: err => { this.saving = false; this.serverError = glError(err, 'The journal type could not be saved.'); }
    });
  }

  confirmDeactivate(t: JournalType): void {
    this.confirm.confirm({
      key: 'gl-jt', header: 'Deactivate journal type', icon: 'pi pi-ban', acceptButtonStyleClass: 'p-button-danger',
      message: `Deactivate ${t.code} — ${t.name}? It can no longer be chosen for new entries. Existing entries keep it; you can activate it again.`,
      acceptLabel: 'Deactivate', rejectLabel: 'Cancel',
      accept: () => this.run(t, this.api.deactivateJournalType(t.uuid), `${t.code} is deactivated.`, 'The journal type could not be deactivated.')
    });
  }

  activate(t: JournalType): void {
    this.run(t, this.api.activateJournalType(t.uuid), `${t.code} is active again.`, 'The journal type could not be activated.');
  }

  confirmDelete(t: JournalType): void {
    this.confirm.confirm({
      key: 'gl-jt', header: 'Delete journal type', icon: 'pi pi-trash', acceptButtonStyleClass: 'p-button-danger',
      message: `Delete ${t.code} — ${t.name}? It has no entries. This cannot be undone.`,
      acceptLabel: 'Delete', rejectLabel: 'Cancel',
      accept: () => this.run(t, this.api.deleteJournalType(t.uuid), `${t.code} is deleted.`, 'The journal type could not be deleted.')
    });
  }

  private run(t: JournalType, call: Observable<unknown>, ok: string, fail: string): void {
    this.busy = t.uuid;
    call.subscribe({
      next: () => { this.busy = null; this.toast('success', ok); this.load(); },
      error: err => { this.busy = null; this.toast('error', glError(err, fail)); }
    });
  }

  private toast(severity: 'success' | 'error', detail: string): void {
    this.messages.add({ severity, summary: severity === 'success' ? 'Done' : 'Error', detail, life: severity === 'error' ? 8000 : 3000 });
  }
}
