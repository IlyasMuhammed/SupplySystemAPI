import { Component, EventEmitter, Input, OnInit, Output, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { DrawerModule } from 'primeng/drawer';
import { InputTextModule } from 'primeng/inputtext';
import { SelectModule } from 'primeng/select';
import { TableModule } from 'primeng/table';
import { ConfirmationService, MessageService } from 'primeng/api';

import {
  AccountingService, ApplyResult, CoaTemplate, FileFormat, TemplateFilters, TemplateItem, UploadError
} from '../../../../services/accounting.service';
import { importErrorMessage, saveDownload } from '../../../../services/import.service';
import { accountTypeLabel, glError, uploadErrors } from '../gl.shared';

export const MAX_UPLOAD_BYTES = 5 * 1024 * 1024;

/**
 * A42 step 1 — template selection (0127–0141): country / business-type filters, template cards, preview side panel,
 * CSV/JSON downloads, Apply (confirm; off while the template has no accounts yet), and the upload zone for an own chart.
 * Sub-component of the wizard (no sf-page); toasts go to the host's MessageService.
 */
@Component({
  selector: 'app-gl-template-step',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, ConfirmDialogModule, DrawerModule, InputTextModule, SelectModule, TableModule],
  providers: [ConfirmationService],
  styleUrls: ['../gl.scss'],
  template: `
<p-confirmDialog key="gl-tpl"></p-confirmDialog>
<div class="gl-note wn" *ngIf="locked" data-testid="tpl-locked">
  <i class="pi pi-lock"></i><div>The chart can only be replaced before the fiscal year is set and before opening balances are posted.</div>
</div>

<section class="sf-card">
  <div class="sf-ch"><div><h2>Templates</h2><div class="sf-mu sf-sm">Pick the chart closest to your business. You can change accounts after applying it.</div></div></div>
  <div class="sf-filters">
    <p-select [options]="countryOptions" [(ngModel)]="countryCode" optionLabel="label" optionValue="value" [showClear]="true"
              placeholder="All countries" (onChange)="load()" appendTo="body" data-testid="tpl-country"></p-select>
    <p-select [options]="businessOptions" [(ngModel)]="businessType" optionLabel="label" optionValue="value" [showClear]="true"
              placeholder="All business types" (onChange)="load()" appendTo="body" data-testid="tpl-business"></p-select>
    <input pInputText type="text" [(ngModel)]="search" (keyup.enter)="load()" placeholder="Search templates" data-testid="tpl-search" />
  </div>

  <div class="sf-empty" *ngIf="loading"><i class="pi pi-spin pi-spinner"></i> Loading templates…</div>
  <div class="gl-note er" *ngIf="loadError" data-testid="tpl-load-error"><i class="pi pi-exclamation-triangle"></i><div>{{ loadError }}</div></div>
  <div class="sf-empty" *ngIf="!loading && !loadError && !templates.length">No templates match these filters.</div>

  <div class="gl-cards" *ngIf="templates.length" style="margin-top:.75rem">
    <div class="gl-tcard" *ngFor="let t of templates" [class.applied]="t.uuid === appliedTemplateUuid" [attr.data-testid]="'tpl-' + t.templateCode">
      <div class="gl-tcard-top">
        <strong>{{ t.templateName }}</strong>
        <span class="sf-pill" [ngClass]="t.accountCount ? 'in' : ''" data-testid="tpl-count">{{ t.accountCount | number }} accounts</span>
      </div>
      <div class="sf-mu sf-sm">
        <span class="sf-mono">{{ t.templateCode }}</span>
        <ng-container *ngIf="t.currencyCode"> · {{ t.currencyCode }}</ng-container>
        <ng-container *ngIf="t.businessType"> · {{ t.businessType }}</ng-container>
        <ng-container *ngIf="t.uuid === appliedTemplateUuid"> · <strong>applied</strong></ng-container>
      </div>
      <div class="gl-desc">{{ t.description }}</div>
      <div class="sf-mu sf-sm" *ngIf="!t.accountCount" data-testid="tpl-coming">Accounts for this template are coming.</div>
      <div class="gl-tcard-foot">
        <p-button label="Preview" icon="pi pi-eye" [text]="true" size="small" [disabled]="!t.accountCount" (onClick)="preview(t)"
                  [attr.data-testid]="'preview-' + t.templateCode"></p-button>
        <p-button label="CSV" icon="pi pi-download" [text]="true" size="small" [disabled]="!t.accountCount" (onClick)="download(t, 'csv')"
                  [attr.data-testid]="'csv-' + t.templateCode"></p-button>
        <p-button label="JSON" icon="pi pi-download" [text]="true" size="small" [disabled]="!t.accountCount" (onClick)="download(t, 'json')"></p-button>
        <span class="sf-end" style="margin-left:auto">
          <p-button *ngIf="canManage" label="Apply" icon="pi pi-check" size="small" [disabled]="!t.accountCount || locked || busy"
                    [loading]="applyingUuid === t.uuid" (onClick)="confirmApply(t)" [attr.data-testid]="'apply-' + t.templateCode"></p-button>
        </span>
      </div>
    </div>
  </div>
</section>

<div class="gl-note er" *ngIf="actionError" data-testid="action-error"><i class="pi pi-times-circle"></i><div>{{ actionError }}</div></div>

<section class="sf-card" *ngIf="canManage" data-testid="blank-option">
  <div class="sf-ch"><div><h2>Or start with a blank chart</h2>
    <div class="sf-mu sf-sm">Only the five root accounts and 3900 Opening Balance Equity are created — you add every other account yourself in the next step.</div></div>
    <span class="sf-ch-actions">
      <p-button label="Start with a blank chart (add accounts yourself)" icon="pi pi-file" [outlined]="true" size="small" [disabled]="locked || busy"
                [loading]="blankStarting" (onClick)="confirmBlank()" data-testid="start-blank"></p-button>
    </span>
  </div>
</section>

<section class="sf-card" *ngIf="canManage">
  <div class="sf-ch"><div><h2>Or upload your own chart</h2>
    <div class="sf-mu sf-sm">.csv or .json · up to 5 MB and 2,000 accounts · start from the blank template so the columns match. All problems are listed at once.</div></div>
    <span class="sf-ch-actions">
      <p-button label="Blank CSV" icon="pi pi-download" [text]="true" size="small" (onClick)="downloadBlank('csv')" data-testid="blank-csv"></p-button>
      <p-button label="Blank JSON" icon="pi pi-download" [text]="true" size="small" (onClick)="downloadBlank('json')" data-testid="blank-json"></p-button>
    </span>
  </div>
  <div class="gl-drop" [class.over]="dragOver" (click)="fileInput.click()" (dragover)="$event.preventDefault(); dragOver = true"
       (dragleave)="dragOver = false" (drop)="onDrop($event)" role="button" tabindex="0" (keydown.enter)="fileInput.click()" data-testid="drop-zone">
    <i class="pi" [ngClass]="file ? 'pi-file' : 'pi-cloud-upload'"></i>
    <strong *ngIf="!file">Drop a .csv or .json file here</strong><span class="sf-sm" *ngIf="!file">or click to choose one</span>
    <strong *ngIf="file" data-testid="file-name">{{ file.name }}</strong>
  </div>
  <input #fileInput type="file" accept=".csv,.json" hidden (change)="onFileInput($event)" data-testid="file-input" />
  <div class="gl-note er" *ngIf="fileError" style="margin-top:.6rem" data-testid="file-error"><i class="pi pi-times-circle"></i><div>{{ fileError }}</div></div>
  <div class="gl-note er" *ngIf="uploadMessage" style="margin-top:.6rem" data-testid="upload-error">
    <i class="pi pi-times-circle"></i>
    <div>{{ uploadMessage }}
      <ul *ngIf="errors.length" data-testid="upload-errors">
        <li *ngFor="let e of errors" data-testid="upload-error-line">
          <strong *ngIf="e.line != null">Line {{ e.line }}</strong><strong *ngIf="e.line == null && e.path" class="sf-mono">{{ e.path }}</strong>
          <ng-container *ngIf="e.line != null || e.path">: </ng-container>
          <span class="sf-mono" *ngIf="e.code">{{ e.code }} </span>{{ e.message }} <span class="sf-mu">({{ e.rule }})</span>
        </li>
      </ul>
    </div>
  </div>
  <div style="margin-top:.6rem; display:flex; justify-content:flex-end">
    <p-button label="Upload chart" icon="pi pi-upload" [disabled]="!file || locked || busy" [loading]="uploading" (onClick)="confirmUpload()"
              data-testid="upload"></p-button>
  </div>
</section>

<p-drawer [(visible)]="previewVisible" position="right" [style]="{ width: 'min(44rem, 100vw)' }" [header]="previewTemplate?.templateName || 'Preview'"
          data-testid="preview-drawer">
  <div class="sf-empty" *ngIf="previewLoading"><i class="pi pi-spin pi-spinner"></i> Loading…</div>
  <div class="gl-scroll" *ngIf="!previewLoading">
    <p-table [value]="previewItems" styleClass="p-datatable-sm" [tableStyle]="{ 'min-width': '32rem' }" data-testid="preview-table">
      <ng-template pTemplate="header"><tr><th>Code</th><th>Name</th><th>Type</th><th>Default</th></tr></ng-template>
      <ng-template pTemplate="body" let-i>
        <tr [class.gl-group]="i.isGroup" data-testid="preview-row">
          <td class="sf-mono" [style.padding-left.rem]="0.5 + (i.level - 1) * 0.9">{{ i.accountCode }}</td>
          <td>{{ i.accountName }}</td>
          <td>{{ typeLabel(i.accountType) }}</td>
          <td class="sf-sm sf-mono">{{ i.defaultAccountKey || '' }}</td>
        </tr>
      </ng-template>
    </p-table>
  </div>
</p-drawer>`
})
export class GlTemplateStepComponent implements OnInit {
  private readonly api = inject(AccountingService);
  private readonly confirm = inject(ConfirmationService);
  private readonly messages = inject(MessageService, { optional: true });

  @Input() canManage = false;
  /** Apply/upload refused (D-16): status past AccountsCreated or opening balances posted. */
  @Input() locked = false;
  @Input() appliedTemplateUuid: string | null | undefined = null;
  /** Accounts already in the chart — applying replaces them. */
  @Input() existingAccounts = 0;
  @Output() applied = new EventEmitter<ApplyResult>();

  templates: CoaTemplate[] = [];
  filters: TemplateFilters = { countries: [], businessTypes: [] };
  countryCode: string | null = null;
  businessType: string | null = null;
  search = '';
  loading = false;
  loadError = '';

  previewVisible = false;
  previewLoading = false;
  previewTemplate: CoaTemplate | null = null;
  previewItems: TemplateItem[] = [];

  file: File | null = null;
  fileError = '';
  dragOver = false;
  uploading = false;
  uploadMessage = '';
  errors: UploadError[] = [];
  applyingUuid: string | null = null;
  blankStarting = false;
  /** A refusal of apply / upload / start-blank (e.g. the 409 once a fiscal year exists), in the server's words. */
  actionError = '';

  readonly typeLabel = accountTypeLabel;

  get countryOptions() { return this.filters.countries.map(c => ({ label: c.name, value: c.code })); }
  get businessOptions() { return this.filters.businessTypes.map(b => ({ label: b, value: b })); }
  get busy(): boolean { return this.uploading || !!this.applyingUuid || this.blankStarting; }

  ngOnInit(): void {
    this.api.getTemplateFilters().subscribe({ next: r => this.filters = r?.result ?? this.filters, error: () => {} });
    this.load();
  }

  load(): void {
    this.loading = true;
    this.loadError = '';
    this.api.getTemplates({ countryCode: this.countryCode, businessType: this.businessType, search: this.search.trim() || null }).subscribe({
      next: r => { this.loading = false; this.templates = r?.result ?? []; },
      error: err => { this.loading = false; this.templates = []; this.loadError = glError(err, 'The templates could not be loaded.'); }
    });
  }

  preview(t: CoaTemplate): void {
    this.previewTemplate = t;
    this.previewVisible = true;
    this.previewLoading = true;
    this.previewItems = [];
    this.api.previewTemplate(t.uuid).subscribe({
      next: r => { this.previewLoading = false; this.previewItems = r?.result ?? []; },
      error: err => { this.previewLoading = false; this.toast('error', glError(err, 'The preview could not be loaded.')); }
    });
  }

  download(t: CoaTemplate, format: FileFormat): void {
    this.api.downloadTemplate(t.uuid, format).subscribe({
      next: res => saveDownload(res, `${t.templateCode}.${format}`),
      error: async err => this.toast('error', await importErrorMessage(err, 'The template could not be downloaded.'))
    });
  }

  downloadBlank(format: FileFormat): void {
    this.api.downloadBlankTemplate(format).subscribe({
      next: res => saveDownload(res, `coa-template.${format}`),
      error: async err => this.toast('error', await importErrorMessage(err, 'The blank template could not be downloaded.'))
    });
  }

  confirmApply(t: CoaTemplate): void {
    if (!this.canManage || !t.accountCount || this.locked) return;
    const replace = this.existingAccounts > 0
      ? ` It replaces the ${this.existingAccounts} accounts in your chart now.` : '';
    this.confirm.confirm({
      key: 'gl-tpl', header: 'Apply template', icon: 'pi pi-sitemap',
      message: `Create ${t.accountCount} accounts from "${t.templateName}"?${replace}`,
      acceptLabel: 'Apply', rejectLabel: 'Cancel',
      accept: () => this.apply(t)
    });
  }

  apply(t: CoaTemplate): void {
    this.applyingUuid = t.uuid;
    this.actionError = '';
    this.api.applyTemplate(t.uuid).subscribe({
      next: r => {
        this.applyingUuid = null;
        this.toast('success', `${r?.result?.accountsCreated ?? t.accountCount} accounts created; ${r?.result?.defaultsAssigned ?? 0} defaults assigned.`);
        if (r?.result) this.applied.emit(r.result);
      },
      error: err => { this.applyingUuid = null; this.actionError = glError(err, 'The template could not be applied.'); }
    });
  }

  confirmBlank(): void {
    if (!this.canManage || this.locked || this.busy) return;
    const replace = this.existingAccounts > 0 ? ` It replaces the ${this.existingAccounts} accounts in your chart now.` : '';
    this.confirm.confirm({
      key: 'gl-tpl', header: 'Start with a blank chart', icon: 'pi pi-file',
      message: `Create only the five root accounts and 3900 Opening Balance Equity, and add the rest yourself?${replace}`,
      acceptLabel: 'Start blank', rejectLabel: 'Cancel',
      accept: () => this.startBlank()
    });
  }

  startBlank(): void {
    this.blankStarting = true;
    this.actionError = '';
    this.api.startBlankChart().subscribe({
      next: r => {
        this.blankStarting = false;
        this.toast('success', `Blank chart created (${r?.result?.accountsCreated ?? 0} accounts). Add your accounts next.`);
        if (r?.result) this.applied.emit(r.result);
      },
      error: err => { this.blankStarting = false; this.actionError = glError(err, 'The blank chart could not be created.'); }
    });
  }

  onFileInput(e: Event): void {    const input = e.target as HTMLInputElement;
    this.pick(input.files?.[0] ?? null);
    input.value = '';
  }

  onDrop(e: DragEvent): void {
    e.preventDefault();
    this.dragOver = false;
    this.pick(e.dataTransfer?.files?.[0] ?? null);
  }

  /** Client check of what the server refuses outright: the extension and the 5 MB limit. */
  pick(f: File | null): void {
    this.fileError = '';
    this.uploadMessage = '';
    this.errors = [];
    this.file = null;
    if (!f) return;
    if (!/\.(csv|json)$/i.test(f.name)) { this.fileError = 'Only .csv or .json files can be uploaded.'; return; }
    if (f.size > MAX_UPLOAD_BYTES) { this.fileError = 'The file is larger than 5 MB.'; return; }
    this.file = f;
  }

  confirmUpload(): void {
    if (!this.file || this.locked) return;
    if (this.existingAccounts > 0) {
      this.confirm.confirm({
        key: 'gl-tpl', header: 'Upload chart', icon: 'pi pi-upload',
        message: `Replace the ${this.existingAccounts} accounts in your chart with ${this.file.name}?`,
        acceptLabel: 'Upload', rejectLabel: 'Cancel', accept: () => this.upload()
      });
    } else this.upload();
  }

  upload(): void {
    if (!this.file) return;
    this.uploading = true;
    this.uploadMessage = '';
    this.errors = [];
    this.api.uploadChart(this.file).subscribe({
      next: r => {
        this.uploading = false;
        this.file = null;
        this.toast('success', `${r?.result?.accountsCreated ?? 0} accounts created from the file.`);
        if (r?.result) this.applied.emit(r.result);
      },
      error: err => {
        this.uploading = false;
        this.errors = [...uploadErrors(err)].sort((a, b) => (a.line ?? 0) - (b.line ?? 0) || (a.path ?? '').localeCompare(b.path ?? ''));
        this.uploadMessage = glError(err, 'The file could not be imported.');
      }
    });
  }

  private toast(severity: 'success' | 'error', detail: string): void {
    this.messages?.add({ severity, summary: severity === 'success' ? 'Done' : 'Error', detail, life: severity === 'error' ? 8000 : 4000 });
  }
}
