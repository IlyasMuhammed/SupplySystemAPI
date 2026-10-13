import { Component, OnDestroy, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { TableLazyLoadEvent, TableModule } from 'primeng/table';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';
import { Subscription } from 'rxjs';

import {
  IMPORT_PERMS, IMPORT_ROUTE, ImportErrorRow, ImportService, SessionDetail, importErrorMessage, importErrorText, isTerminal, saveDownload
} from '../../../../services/import.service';
import { AuthService } from '../../../service/auth.service';
import { FLOW, FlowTone } from '../../../../shared/flow';
import { MatchMark, formatBytes, formatDuration, matchMark, sessionStatusPill, stepForStatus } from '../import.shared';

export interface MappingVm { index: number; source: string; target: string; mark: MatchMark; }
export interface ErrorRowVm { rowNumber: number; status: string; problems: string; }

/**
 * A40 — `/portal/pages/settings/import/sessions/:uuid`: one import — summary, column mappings, errors (paginated from the
 * server), the error / results reports, Cancel while it is still running (IMPORT_EXECUTE or IMPORT_MANAGE) and Continue in
 * the wizard while it is not imported yet. A background import's progress is followed live (hub, polling fallback).
 */
@Component({
  selector: 'app-import-session-detail',
  standalone: true,
  imports: [CommonModule, RouterModule, ButtonModule, TableModule, ToastModule, ...FLOW],
  template: `
<p-toast></p-toast>
<sf-page [crumbs]="['Settings', 'Data import', s?.fileName || 'Import']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <ng-container *ngIf="s">
      <p-button *ngIf="canContinue" label="Continue import" icon="pi pi-arrow-right" (onClick)="continueInWizard()" data-testid="continue"></p-button>
      <p-button *ngIf="canRevalidate" label="Re-validate remaining rows" icon="pi pi-replay" (onClick)="revalidateInWizard()"
                data-testid="revalidate"></p-button>
      <p-button *ngIf="hasErrors" label="Download error report" icon="pi pi-download" [text]="true" [loading]="downloading === 'errors'"
                (onClick)="downloadErrors()" data-testid="download-errors"></p-button>
      <p-button *ngIf="finished" label="Download results report" icon="pi pi-download" [text]="true" [loading]="downloading === 'results'"
                (onClick)="downloadResults()" data-testid="download-results"></p-button>
      <span class="sf-sep"></span>
      <p-button *ngIf="canCancel && !finished" label="Cancel import" severity="danger" [text]="true" [loading]="cancelling"
                (onClick)="cancel()" data-testid="cancel"></p-button>
    </ng-container>
    <p-button label="Data import" icon="pi pi-arrow-left" [text]="true" [routerLink]="importRoute"></p-button>
  </div>

  <div class="sf-body" *ngIf="isLoading"><div class="sf-card sf-empty"><i class="pi pi-spin pi-spinner"></i> Loading…</div></div>
  <div class="sf-body" *ngIf="loadError"><div class="imp-note er" data-testid="load-error"><i class="pi pi-exclamation-triangle"></i><div>{{ loadError }}</div></div></div>

  <ng-container *ngIf="s">
    <header class="sf-hdr">
      <div class="sf-t"><div>
        <div class="sf-kicker">{{ s.entityName }} import</div>
        <h1><span class="sf-mono">{{ s.fileName }}</span> <span class="sf-pill" [ngClass]="statusTone" data-testid="status">{{ statusLabel }}</span></h1>
        <div class="sf-meta">{{ s.createdByName }} · {{ s.createdAt | date:'dd MMM yyyy, HH:mm' }}</div>
      </div></div>
      <div class="sf-facts">
        <div class="sf-kpi"><span class="l">Rows</span><span class="v" data-testid="kpi-total">{{ s.totalRows | number }}</span><span class="s">{{ s.validRows | number }} valid · {{ s.invalidRows | number }} invalid</span></div>
        <div class="sf-kpi"><span class="l">Imported</span><span class="v" data-testid="kpi-imported">{{ s.importedRows | number }}</span><span class="s">{{ s.updatedRows | number }} updated · {{ s.skippedRows | number }} skipped</span></div>
        <div class="sf-kpi"><span class="l">Errors</span><span class="v" data-testid="kpi-errors">{{ s.errorRows + s.invalidRows | number }}</span><span class="s" *ngIf="duration">took {{ duration }}</span></div>
      </div>
    </header>

    <div class="sf-body">
      <section class="sf-card" *ngIf="watching" data-testid="progress">
        <div class="sf-ch"><h2>Importing…</h2></div>
        <div class="imp-progress"><span [style.width.%]="progressPercent"></span></div>
        <div class="sf-sm sf-mu" data-testid="progress-text">{{ progressProcessed | number }} of {{ progressTotal | number }} rows · {{ progressPercent }}%</div>
      </section>

      <div class="imp-note er" *ngIf="s.errorMessage" data-testid="error-message"><i class="pi pi-exclamation-triangle"></i><div>{{ s.errorMessage }}</div></div>

      <section class="sf-card">
        <div class="sf-ch"><h2>Summary</h2></div>
        <div class="sf-kv" data-testid="summary">
          <span>Entity</span><span>{{ s.entityName }}</span>
          <span>File</span><span>{{ s.fileName }} · {{ s.fileType }} · {{ fileSize }}</span>
          <span>Sheet</span><span>{{ s.sheetName || '—' }}</span>
          <span>When a row matches</span><span>{{ strategyLabel }}</span>
          <span>Saved mapping</span><span>{{ s.savedMappingName || '—' }}</span>
          <span>Validated</span><span>{{ s.validationCompletedAt ? (s.validationCompletedAt | date:'dd MMM yyyy, HH:mm') : '—' }}</span>
          <span>Import started</span><span>{{ s.importStartedAt ? (s.importStartedAt | date:'dd MMM yyyy, HH:mm:ss') : '—' }}</span>
          <span>Import finished</span><span>{{ s.importCompletedAt ? (s.importCompletedAt | date:'dd MMM yyyy, HH:mm:ss') : '—' }}</span>
          <span>Notes</span><span>{{ s.notes || '—' }}</span>
        </div>
      </section>

      <section class="sf-card p0">
        <div class="sf-ch sf-pad"><h2>Column mapping</h2></div>
        <p-table [value]="mappings" data-testid="mappings">
          <ng-template pTemplate="header"><tr><th>Column in the file</th><th>Imported into</th><th>Match</th></tr></ng-template>
          <ng-template pTemplate="body" let-m>
            <tr data-testid="mapping-row"><td>{{ m.source }}</td><td>{{ m.target }}</td>
              <td><span class="imp-mark" [ngClass]="m.mark.mark === '✗' ? 'none' : m.mark.tone">{{ m.mark.mark }} <span class="sf-sm">{{ m.mark.label }}</span></span></td></tr>
          </ng-template>
        </p-table>
      </section>

      <section class="sf-card p0" *ngIf="hasErrors" data-testid="errors">
        <div class="sf-ch sf-pad"><h2>Errors <span class="sf-count">{{ errorTotal }}</span></h2></div>
        <p-table [value]="errorRows" [lazy]="true" [paginator]="true" [rows]="errorPageSize" [totalRecords]="errorTotal"
                 [loading]="errorsLoading" (onLazyLoad)="onErrorsPage($event)" [rowsPerPageOptions]="[25, 50, 100]">
          <ng-template pTemplate="header"><tr><th style="width:6rem">Row</th><th style="width:9rem">Status</th><th>Problems</th></tr></ng-template>
          <ng-template pTemplate="body" let-e>
            <tr data-testid="error-row"><td>{{ e.rowNumber }}</td><td><span class="sf-pill er">{{ e.status }}</span></td><td>{{ e.problems }}</td></tr>
          </ng-template>
          <ng-template pTemplate="emptymessage"><tr><td colspan="3" class="sf-empty">No errors.</td></tr></ng-template>
        </p-table>
      </section>
    </div>
  </ng-container>
</sf-page>
  `,
  styleUrls: ['../import.scss'],
  providers: [MessageService]
})
export class ImportSessionDetailComponent implements OnInit, OnDestroy {
  private readonly api = inject(ImportService);
  private readonly auth = inject(AuthService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly messages = inject(MessageService);

  readonly importRoute = IMPORT_ROUTE;

  uuid = '';
  s: SessionDetail | null = null;
  isLoading = true;
  loadError: string | null = null;

  statusLabel = '';
  statusTone: FlowTone = '';
  strategyLabel = '';
  fileSize = '';
  duration = '';
  finished = false;
  hasErrors = false;
  canContinue = false;
  canRevalidate = false;
  mappings: MappingVm[] = [];

  canCancel = false;
  canExecute = false;
  canUpload = false;
  cancelling = false;
  downloading: 'errors' | 'results' | null = null;

  errorRows: ErrorRowVm[] = [];
  errorTotal = 0;
  errorPage = 1;
  errorPageSize = 25;
  errorsLoading = false;

  watching = false;
  progressProcessed = 0;
  progressTotal = 0;
  progressPercent = 0;
  private progressSub: Subscription | null = null;

  ngOnInit(): void {
    this.canExecute = this.auth.hasPermission(IMPORT_PERMS.EXECUTE);
    this.canCancel = this.canExecute || this.auth.hasPermission(IMPORT_PERMS.MANAGE);
    this.canUpload = this.auth.hasPermission(IMPORT_PERMS.UPLOAD);
    this.uuid = this.route.snapshot.paramMap.get('uuid') ?? '';
    this.load();
  }

  ngOnDestroy(): void {
    this.progressSub?.unsubscribe();
  }

  load(): void {
    this.isLoading = true;
    this.loadError = null;
    this.api.getSession(this.uuid).subscribe({
      next: res => {
        this.isLoading = false;
        if (!res?.result) { this.loadError = res?.message || 'The import was not found.'; return; }
        this.apply(res.result);
        if (this.s?.status === 'IMPORTING' && !this.watching) this.watch();
      },
      error: err => {
        this.isLoading = false;
        this.loadError = importErrorText(err, 'The import could not be loaded.');
      }
    });
  }

  private apply(s: SessionDetail): void {
    const hadErrors = this.hasErrors;
    this.s = s;
    const pill = sessionStatusPill(s.status);
    this.statusLabel = pill.label;
    this.statusTone = pill.tone;
    this.strategyLabel = ({ SKIP: 'Skip', UPDATE: 'Update', CREATE_NEW: 'Create new', ERROR: 'Error' } as Record<string, string>)[s.duplicateStrategy] ?? s.duplicateStrategy;
    this.fileSize = formatBytes(s.fileSizeBytes);
    this.duration = formatDuration(s.importStartedAt, s.importCompletedAt);
    this.finished = isTerminal(s.status);
    this.hasErrors = (s.invalidRows ?? 0) + (s.errorRows ?? 0) > 0;
    this.canContinue = this.canUpload && stepForStatus(s.status) < 3;
    // D-38/D-39 — an interrupted (FAILED / CANCELLED) import can be validated again and the rows not yet applied re-run.
    this.canRevalidate = this.canExecute && (s.status === 'FAILED' || s.status === 'CANCELLED');
    this.mappings = [...(s.mappings ?? [])].sort((a, b) => a.sourceColumnIndex - b.sourceColumnIndex).map(m => ({
      index: m.sourceColumnIndex,
      source: m.sourceColumnName,
      target: m.targetFieldName ? (m.targetDisplayName || m.targetFieldName) : '— Ignored —',
      mark: matchMark(m.targetFieldName, m.confidenceScore, m.isAutoMapped)
    }));
    // The table's first lazy load fetches page 1 when the section appears; reload when it was already shown.
    if (hadErrors && this.hasErrors) this.loadErrors();
  }

  onErrorsPage(e: TableLazyLoadEvent): void {
    this.errorPageSize = e.rows ?? this.errorPageSize;
    this.errorPage = Math.floor((e.first ?? 0) / this.errorPageSize) + 1;
    this.loadErrors();
  }

  loadErrors(): void {
    this.errorsLoading = true;
    this.api.getErrors(this.uuid, this.errorPage, this.errorPageSize).subscribe({
      next: res => {
        this.errorsLoading = false;
        this.errorRows = (res?.result?.data ?? []).map((r: ImportErrorRow) => ({
          rowNumber: r.rowNumber,
          status: r.status,
          problems: (r.errors ?? []).map(e => (e.field ? `${e.field}: ` : '') + e.message).join(' · ')
        }));
        this.errorTotal = res?.result?.totalRecords ?? 0;
      },
      error: () => { this.errorsLoading = false; this.errorRows = []; }
    });
  }

  private watch(): void {
    this.watching = true;
    this.progressTotal = this.s?.validRows ?? 0;
    this.progressSub = this.api.watchProgress(this.uuid).subscribe({
      next: p => {
        this.progressProcessed = p.processed;
        this.progressTotal = p.total;
        this.progressPercent = p.total > 0 ? Math.min(100, Math.round((100 * p.processed) / p.total)) : 0;
      },
      complete: () => { this.watching = false; this.load(); }
    });
  }

  revalidateInWizard(): void {
    if (!this.s) return;
    this.router.navigate([IMPORT_ROUTE, this.s.entityType], { queryParams: { session: this.s.uuid, revalidate: 1 } });
  }

  continueInWizard(): void {
    if (!this.s) return;
    this.router.navigate([IMPORT_ROUTE, this.s.entityType], { queryParams: { session: this.s.uuid } });
  }

  cancel(): void {
    if (!this.s || this.cancelling) return;
    this.cancelling = true;
    this.api.cancel(this.uuid).subscribe({
      next: res => {
        this.cancelling = false;
        this.progressSub?.unsubscribe();
        this.watching = false;
        if (res?.result) this.apply(res.result);
        this.messages.add({ severity: 'info', summary: 'Import cancelled', detail: 'The import was cancelled.', life: 4000 });
      },
      error: err => {
        this.cancelling = false;
        this.messages.add({ severity: 'error', summary: 'Not cancelled', detail: importErrorText(err, 'The import could not be cancelled.'), life: 8000 });
      }
    });
  }

  downloadErrors(): void {
    this.download('errors', () => this.api.downloadErrors(this.uuid), `import-errors-${this.uuid}.xlsx`, 'The error report could not be downloaded.');
  }

  downloadResults(): void {
    this.download('results', () => this.api.downloadResults(this.uuid), `import-results-${this.uuid}.xlsx`, 'The results report could not be downloaded.');
  }

  private download(kind: 'errors' | 'results', call: () => ReturnType<ImportService['downloadErrors']>, name: string, fallback: string): void {
    if (this.downloading) return;
    this.downloading = kind;
    call().subscribe({
      next: res => { this.downloading = null; saveDownload(res, name); },
      error: async err => {
        this.downloading = null;
        this.messages.add({ severity: 'error', summary: 'Error', detail: await importErrorMessage(err, fallback), life: 8000 });
      }
    });
  }
}
