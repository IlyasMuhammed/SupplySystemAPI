import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { DialogModule } from 'primeng/dialog';
import { InputTextModule } from 'primeng/inputtext';
import { SelectModule } from 'primeng/select';
import { TableModule } from 'primeng/table';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { ConfirmationService, MessageService } from 'primeng/api';
import { catchError, of } from 'rxjs';

import {
  Account, AccountingService, ExternalMapping, GL_PERMS, MappingImportResult, SyncDirection, SystemType
} from '../../../../services/accounting.service';
import { importErrorMessage, saveDownload } from '../../../../services/import.service';
import { AuthService } from '../../../service/auth.service';
import { FLOW } from '../../../../shared/flow';
import { SYNC_DIRECTIONS, SYSTEM_TYPES, glError, syncStatusLabel, syncStatusTone, systemTypeLabel } from '../gl.shared';

interface MappingDraft { accountUuid: string | null; externalAccountCode: string; externalAccountName: string; syncDirection: SyncDirection; }

/**
 * A42 (0548–0557) — `/gl/integrations`: SMS account ↔ external system account mappings per system type. Direction is
 * changed inline (PUT); + Add Mapping; Import CSV (created / updated / errors); Export; sync status (Never until a
 * connector runs — none in A42).
 */
@Component({
  selector: 'app-gl-integrations',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, ConfirmDialogModule, DialogModule, InputTextModule, SelectModule, TableModule, ToastModule,
    TooltipModule, ...FLOW],
  providers: [ConfirmationService],
  styleUrls: ['../gl.scss'],
  template: `
<p-toast></p-toast>
<p-confirmDialog key="gl-map"></p-confirmDialog>
<sf-page [crumbs]="['Finance', 'General Ledger', 'Integrations']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button *ngIf="canManage" label="Add mapping" icon="pi pi-plus" (onClick)="openNew()" data-testid="add-mapping"></p-button>
    <p-button *ngIf="canManage" label="Import CSV" icon="pi pi-upload" [text]="true" [loading]="importing" (onClick)="fileInput.click()"
              data-testid="import"></p-button>
    <p-button label="Export" icon="pi pi-download" [text]="true" (onClick)="export()" data-testid="export"></p-button>
    <span class="sf-sep"></span>
    <p-button icon="pi pi-refresh" [text]="true" (onClick)="load()" ariaLabel="Refresh"></p-button>
  </div>
  <input #fileInput type="file" accept=".csv" hidden (change)="onFile($event)" data-testid="import-file" />

  <div class="sf-body">
    <div class="sf-lr-head"><div><h1>External account mappings <span class="sf-count">{{ mappings.length }}</span></h1>
      <div class="sf-sub">Which account in another accounting system each SMS account corresponds to</div></div></div>

    <div class="gl-note" data-testid="connector-note"><i class="pi pi-info-circle"></i>
      <div>Connectors are configured in a future addendum. The QuickBooks integration keeps its own account settings.</div></div>

    <div class="sf-filters">
      <p-select [options]="systemOptions" [(ngModel)]="systemType" optionLabel="label" optionValue="value" (onChange)="load()" appendTo="body"
                data-testid="system-type"></p-select>
      <input pInputText [(ngModel)]="systemName" maxlength="100" placeholder="Connection name, e.g. Head office books" data-testid="system-name" />
    </div>

    <div class="gl-note er" *ngIf="loadError" data-testid="load-error"><i class="pi pi-exclamation-triangle"></i><div>{{ loadError }}</div></div>

    <div class="gl-note" *ngIf="importResult" [ngClass]="importResult.errors.length ? 'wn' : 'ok'" data-testid="import-result">
      <i class="pi pi-upload"></i>
      <div>Import: <strong data-testid="imp-created">{{ importResult.created }}</strong> created, <strong data-testid="imp-updated">{{ importResult.updated }}</strong> updated,
        <strong>{{ importResult.errors.length }}</strong> errors.
        <ul *ngIf="importResult.errors.length"><li *ngFor="let e of importResult.errors" data-testid="imp-error">Line {{ e.line }}: {{ e.message }}</li></ul></div>
    </div>

    <div class="sf-card p0">
      <div class="gl-scroll">
        <p-table [value]="mappings" [loading]="loading" styleClass="p-datatable-sm" [tableStyle]="{ 'min-width': '56rem' }" data-testid="mappings">
          <ng-template pTemplate="header">
            <tr><th>SMS code</th><th>SMS account</th><th>External code</th><th>External name</th><th>Connection</th><th style="width:11rem">Direction</th><th>Sync</th><th></th></tr>
          </ng-template>
          <ng-template pTemplate="body" let-m>
            <tr [attr.data-testid]="'map-' + m.account.accountCode">
              <td class="sf-mono">{{ m.account.accountCode }}</td>
              <td>{{ m.account.accountName }}</td>
              <td class="sf-mono">{{ m.externalAccountCode }}</td>
              <td>{{ m.externalAccountName || '—' }}</td>
              <td class="sf-sm">{{ m.systemName }}<div class="sf-mu">{{ sysLabel(m.systemType) }}</div></td>
              <td>
                <p-select [options]="directions" [ngModel]="m.syncDirection" (ngModelChange)="setDirection(m, $event)" optionLabel="label" optionValue="value"
                          appendTo="body" [disabled]="!canManage || busyUuid === m.uuid" styleClass="w-full" [attr.data-testid]="'dir-' + m.account.accountCode"></p-select>
              </td>
              <td><span class="sf-pill" [ngClass]="syncTone(m.syncStatus)" [pTooltip]="m.syncErrorMessage || ''" [tooltipDisabled]="!m.syncErrorMessage"
                        data-testid="sync">{{ syncLabel(m.syncStatus) }}</span>
                <div class="sf-mu sf-sm" *ngIf="m.lastSyncedAt">{{ m.lastSyncedAt | date:'d MMM y, HH:mm' }}</div></td>
              <td><p-button *ngIf="canManage" icon="pi pi-trash" [text]="true" severity="danger" size="small" (onClick)="confirmDelete(m)"
                            [ariaLabel]="'Delete mapping ' + m.account.accountCode"></p-button></td>
            </tr>
          </ng-template>
          <ng-template pTemplate="emptymessage"><tr><td colspan="8" class="sf-empty">No mappings for {{ sysLabel(systemType) }} yet.</td></tr></ng-template>
        </p-table>
      </div>
    </div>
  </div>
</sf-page>

<p-dialog [(visible)]="dialogVisible" [modal]="true" [draggable]="false" header="Add mapping" [style]="{ width: '36rem' }" [breakpoints]="{ '640px': '96vw' }">
  <div class="sf-fields">
    <div class="sf-fld"><label>System</label><input pInputText [value]="sysLabel(systemType)" readonly /></div>
    <div class="sf-fld"><label class="sf-req" for="m-sys">Connection name</label>
      <input pInputText id="m-sys" [(ngModel)]="systemName" maxlength="100" data-testid="m-system-name" /></div>
    <div class="sf-fld" style="grid-column: 1 / -1"><label class="sf-req" for="m-acc">SMS account</label>
      <p-select inputId="m-acc" [options]="accountOptions" [(ngModel)]="draft.accountUuid" optionLabel="label" optionValue="value" [filter]="true"
                filterBy="label" placeholder="A postable (Level 4) active account" appendTo="body" styleClass="w-full" data-testid="m-account"></p-select></div>
    <div class="sf-fld"><label class="sf-req" for="m-code">External code</label>
      <input pInputText id="m-code" [(ngModel)]="draft.externalAccountCode" maxlength="50" data-testid="m-code" /></div>
    <div class="sf-fld"><label for="m-name">External name</label>
      <input pInputText id="m-name" [(ngModel)]="draft.externalAccountName" maxlength="200" data-testid="m-name" /></div>
    <div class="sf-fld"><label for="m-dir">Direction</label>
      <p-select inputId="m-dir" [options]="directions" [(ngModel)]="draft.syncDirection" optionLabel="label" optionValue="value" appendTo="body"></p-select></div>
  </div>
  <div class="sf-sm" style="color: var(--sms-danger); margin-top:.5rem" *ngIf="shownProblem" data-testid="m-problem">{{ shownProblem }}</div>
  <ng-template pTemplate="footer">
    <p-button label="Cancel" [text]="true" (onClick)="dialogVisible = false"></p-button>
    <p-button label="Add mapping" icon="pi pi-check" [loading]="saving" (onClick)="save()" data-testid="m-save"></p-button>
  </ng-template>
</p-dialog>`
})
export class GlIntegrationsComponent implements OnInit {
  private readonly api = inject(AccountingService);
  private readonly auth = inject(AuthService);
  private readonly confirm = inject(ConfirmationService);
  private readonly messages = inject(MessageService, { optional: true });

  readonly systemOptions = SYSTEM_TYPES;
  readonly directions = SYNC_DIRECTIONS;
  readonly sysLabel = systemTypeLabel;
  readonly syncLabel = syncStatusLabel;
  readonly syncTone = syncStatusTone;

  systemType: SystemType = 'QUICKBOOKS_ONLINE';
  systemName = '';
  mappings: ExternalMapping[] = [];
  accounts: Account[] = [];
  loading = false;
  loadError = '';
  busyUuid: string | null = null;
  importing = false;
  importResult: MappingImportResult | null = null;

  dialogVisible = false;
  draft: MappingDraft = this.blank();
  tried = false;
  saving = false;
  serverError = '';

  get canManage(): boolean { return this.auth.hasPermission(GL_PERMS.SETUP_MANAGE); }
  get accountOptions() { return this.accounts.map(a => ({ value: a.uuid, label: `${a.accountCode} · ${a.accountName}` })); }

  get problem(): string | null {
    if (!this.systemName.trim()) return 'Give the connection a name.';
    if (!this.draft.accountUuid) return 'Choose the SMS account.';
    if (!this.draft.externalAccountCode.trim()) return 'Enter the external account code.';
    const dup = this.mappings.find(m => m.account.uuid === this.draft.accountUuid && m.systemType === this.systemType);
    if (dup) return `This account is already mapped for ${systemTypeLabel(this.systemType)}.`;
    return null;
  }

  get shownProblem(): string | null { return this.serverError || (this.tried ? this.problem : null); }

  ngOnInit(): void {
    this.load();
    this.api.getPostableAccounts().pipe(catchError(() => of([] as Account[]))).subscribe(a => this.accounts = a);
  }

  private blank(): MappingDraft { return { accountUuid: null, externalAccountCode: '', externalAccountName: '', syncDirection: 'Outbound' }; }

  load(): void {
    this.loading = true;
    this.loadError = '';
    this.api.getMappings(this.systemType).subscribe({
      next: r => {
        this.loading = false;
        this.mappings = [...(r?.result ?? [])].sort((a, b) => a.account.accountCode.localeCompare(b.account.accountCode));
        if (!this.systemName.trim() && this.mappings.length) this.systemName = this.mappings[0].systemName;
      },
      error: err => { this.loading = false; this.mappings = []; this.loadError = glError(err, 'The mappings could not be loaded.'); }
    });
  }

  openNew(): void {
    this.draft = this.blank();
    this.tried = false;
    this.serverError = '';
    this.dialogVisible = true;
  }

  save(): void {
    this.tried = true;
    this.serverError = '';
    if (this.problem || this.saving) return;
    this.saving = true;
    this.api.createMapping({
      systemType: this.systemType, systemName: this.systemName.trim(), accountUuid: this.draft.accountUuid!,
      externalAccountCode: this.draft.externalAccountCode.trim(), externalAccountName: this.draft.externalAccountName.trim() || null,
      syncDirection: this.draft.syncDirection
    }).subscribe({
      next: () => { this.saving = false; this.dialogVisible = false; this.toast('success', 'The mapping is added.'); this.load(); },
      error: err => { this.saving = false; this.serverError = glError(err, 'The mapping could not be added.'); }
    });
  }

  /** Inline direction change (PUT); the row goes back on a refusal. */
  setDirection(m: ExternalMapping, dir: SyncDirection): void {
    if (dir === m.syncDirection) return;
    const before = m.syncDirection;
    m.syncDirection = dir;
    this.busyUuid = m.uuid;
    this.api.updateMapping(m.uuid, {
      systemName: m.systemName, externalAccountCode: m.externalAccountCode, externalAccountName: m.externalAccountName ?? null,
      syncDirection: dir, isActive: m.isActive
    }).subscribe({
      next: r => { this.busyUuid = null; if (r?.result) Object.assign(m, r.result); },
      error: err => { this.busyUuid = null; m.syncDirection = before; this.toast('error', glError(err, 'The direction could not be changed.')); }
    });
  }

  confirmDelete(m: ExternalMapping): void {
    this.confirm.confirm({
      key: 'gl-map', header: 'Delete mapping', icon: 'pi pi-trash', acceptLabel: 'Delete', rejectLabel: 'Cancel', acceptButtonStyleClass: 'p-button-danger',
      message: `Remove the mapping of ${m.account.accountCode} to ${m.externalAccountCode}?`,
      accept: () => this.api.deleteMapping(m.uuid).subscribe({
        next: () => this.load(),
        error: err => this.toast('error', glError(err, 'The mapping could not be deleted.'))
      })
    });
  }

  onFile(e: Event): void {
    const input = e.target as HTMLInputElement;
    const file = input.files?.[0] ?? null;
    input.value = '';
    if (!file) return;
    if (!/\.csv$/i.test(file.name)) { this.toast('error', 'Only .csv files can be imported.'); return; }
    if (!this.systemName.trim()) { this.toast('error', 'Give the connection a name first.'); return; }
    this.importing = true;
    this.importResult = null;
    this.api.importMappings(file, this.systemType, this.systemName.trim()).subscribe({
      next: r => {
        this.importing = false;
        const x = r?.result;
        this.importResult = { created: x?.created ?? 0, updated: x?.updated ?? 0, errors: x?.errors ?? [] };
        this.load();
      },
      error: err => {
        this.importing = false;
        const body = (err as any)?.error;
        const errors = body?.errors ?? body?.result?.errors;
        if (Array.isArray(errors)) this.importResult = { created: 0, updated: 0, errors };
        else this.toast('error', glError(err, 'The file could not be imported.'));
      }
    });
  }

  export(): void {
    this.api.exportMappings(this.systemType).subscribe({
      next: res => saveDownload(res, 'external-mappings.csv'),
      error: async err => this.toast('error', await importErrorMessage(err, 'The mappings could not be exported.'))
    });
  }

  private toast(severity: 'success' | 'error', detail: string): void {
    this.messages?.add({ severity, summary: severity === 'success' ? 'Done' : 'Error', detail, life: severity === 'error' ? 8000 : 3000 });
  }
}
