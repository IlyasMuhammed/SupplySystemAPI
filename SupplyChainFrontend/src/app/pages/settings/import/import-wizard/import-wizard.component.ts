import { Component, ElementRef, OnDestroy, OnInit, ViewChild, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { RadioButtonModule } from 'primeng/radiobutton';
import { SelectModule } from 'primeng/select';
import { TableModule } from 'primeng/table';
import { TextareaModule } from 'primeng/textarea';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';
import { Subscription } from 'rxjs';

import {
  DuplicateStrategy, FEATURE_OPENING_BALANCE_IMPORT, IMPORT_PERMS, IMPORT_ROUTE, ImportField, ImportPreview, ImportProfileDetail,
  ImportService, MappingPreset, OPENING_DATE_ROUTE, SessionDetail, ValidationError, importErrorMessage, importErrorText,
  isTerminal, saveDownload
} from '../../../../services/import.service';
import { ModuleService } from '../../../../services/module.service';
import { AuthService } from '../../../service/auth.service';
import { FLOW, FlowStage, FlowTone, flowStagesFrom } from '../../../../shared/flow';
import {
  MappingRow, OPENING_DATE_MESSAGE, defaultedRequired, fileProblem, formatBytes, formatDuration, mappingProblems, mappingRows, markDuplicates,
  matchMark, stepForStatus, unmappedRequired
} from '../import.shared';

export const WIZARD_STEPS = ['Upload', 'Map', 'Validate', 'Results'];

export interface StrategyOption { value: DuplicateStrategy; label: string; hint: string; }
export interface TargetOption { label: string; value: string | null; }
export interface PreviewRowVm { rowNumber: number; cells: { value: string; issue: string | null }[]; hasIssue: boolean; }
export interface ErrorGroupVm { code: string; count: number; }
export interface ResultCardVm { key: string; label: string; value: number; tone: FlowTone; }

const STRATEGIES: StrategyOption[] = [
  { value: 'SKIP', label: 'Skip', hint: 'Rows matching an existing record are left out; the record stays as it is.' },
  { value: 'UPDATE', label: 'Update', hint: 'Rows matching an existing record update it; empty cells leave a field unchanged.' },
  { value: 'ERROR', label: 'Error', hint: 'Rows matching an existing record are reported as errors and not imported.' }
];

/**
 * A40 — `/portal/pages/settings/import/:entityType`: Upload → Map → Validate → Results (sf-stages). `?session=<uuid>`
 * resumes a session on the step its status belongs to. Server sentences are shown verbatim. View models (mapping rows,
 * preview cells, error groups, result cards, stages) are fields rebuilt on change — never getters feeding *ngFor.
 */
@Component({
  selector: 'app-import-wizard',
  standalone: true,
  imports: [
    CommonModule, FormsModule, RouterModule, ButtonModule, InputTextModule, RadioButtonModule, SelectModule, TableModule, TextareaModule,
    ToastModule, TooltipModule, ...FLOW
  ],
  templateUrl: './import-wizard.component.html',
  styleUrls: ['../import.scss'],
  providers: [MessageService]
})
export class ImportWizardComponent implements OnInit, OnDestroy {
  private readonly api = inject(ImportService);
  private readonly auth = inject(AuthService);
  private readonly modules = inject(ModuleService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly messages = inject(MessageService);

  @ViewChild('fileInput') fileInput?: ElementRef<HTMLInputElement>;

  readonly importRoute = IMPORT_ROUTE;
  readonly openingDateRoute = OPENING_DATE_ROUTE;
  readonly openingDateMessage = OPENING_DATE_MESSAGE;

  entityType = '';
  profile: ImportProfileDetail | null = null;
  fields: ImportField[] = [];
  requiredFields: ImportField[] = [];
  isLoading = true;
  loadError: string | null = null;
  accessProblem: string | null = null;

  step = 0;
  stages: FlowStage[] = flowStagesFrom(WIZARD_STEPS, 0);
  session: SessionDetail | null = null;

  canUpload = false;
  canExecute = false;
  canCancel = false;

  // ── Upload ──────────────────────────────────────────────────────────────
  file: File | null = null;
  fileError: string | null = null;
  dragOver = false;
  strategyOptions: StrategyOption[] = [];
  duplicateStrategy: DuplicateStrategy = 'SKIP';
  presets: MappingPreset[] = [];
  presetOptions: { label: string; value: string }[] = [];
  mappingPreset: string | null = null;
  notes = '';
  uploading = false;
  uploadPercent = 0;
  uploadError: string | null = null;
  showSheetPicker = false;
  sheetChoice: string | null = null;
  sheetOptions: { label: string; value: string }[] = [];
  private sheetChosen = false;
  openingDateMissing = false;
  templateDownloading = false;

  // ── Map ─────────────────────────────────────────────────────────────────
  rows: MappingRow[] = [];
  targetOptions: TargetOption[] = [];
  problems: string[] = [];
  missingRequired: ImportField[] = [];
  /** Required fields with a server-side default (not in the server's unmappedRequiredFields while unmapped). */
  private defaulted = new Set<string>();
  mappingSaving = false;
  mappingDirty = false;
  mappingError: string | null = null;
  presetName = '';
  savingPreset = false;

  // ── Validate ────────────────────────────────────────────────────────────
  preview: ImportPreview | null = null;
  previewRows: PreviewRowVm[] = [];
  previewLoading = false;
  validating = false;
  validated = false;
  errorGroups: ErrorGroupVm[] = [];
  firstErrors: ValidationError[] = [];
  actionError: string | null = null;
  executing = false;
  cancelling = false;
  errorsDownloading = false;

  // ── Results ─────────────────────────────────────────────────────────────
  background = false;
  progressProcessed = 0;
  progressTotal = 0;
  progressPercent = 0;
  resultCards: ResultCardVm[] = [];
  duration = '';
  resultsDownloading = false;
  private progressSub: Subscription | null = null;

  ngOnInit(): void {
    this.canUpload = this.auth.hasPermission(IMPORT_PERMS.UPLOAD);
    this.canExecute = this.auth.hasPermission(IMPORT_PERMS.EXECUTE);
    this.canCancel = this.canExecute || this.auth.hasPermission(IMPORT_PERMS.MANAGE);
    this.entityType = this.route.snapshot.paramMap.get('entityType') ?? '';
    this.revalidate = this.route.snapshot.queryParamMap.get('revalidate') === '1';
    this.load(this.route.snapshot.queryParamMap.get('session'));
  }

  /** `?revalidate=1`: a FAILED / CANCELLED (interrupted) session reopens on Validate to re-run the remaining rows (D-38/D-39). */
  private revalidate = false;

  ngOnDestroy(): void {
    this.progressSub?.unsubscribe();
  }

  load(resumeUuid: string | null = null): void {
    this.isLoading = true;
    this.loadError = null;
    this.api.getProfile(this.entityType).subscribe({
      next: res => {
        this.isLoading = false;
        const p = res?.result;
        if (!p) { this.loadError = res?.message || 'This import type does not exist.'; return; }
        this.applyProfile(p);
        if (this.accessProblem) return;
        if (resumeUuid) this.resume(resumeUuid);
      },
      error: err => {
        this.isLoading = false;
        this.loadError = importErrorText(err, 'The import type could not be loaded.');
      }
    });
  }

  private applyProfile(p: ImportProfileDetail): void {
    this.profile = p;
    this.fields = [...(p.fields ?? [])].sort((a, b) => a.sortOrder - b.sortOrder);
    this.requiredFields = this.fields.filter(f => f.isRequired);
    this.targetOptions = [
      { label: '— Ignore —', value: null },
      ...this.fields.map(f => ({ label: f.displayName + (f.isRequired ? ' *' : '') + (f.isKey ? ' (key)' : ''), value: f.fieldName }))
    ];
    this.strategyOptions = p.isOpeningBalance
      ? STRATEGIES.filter(s => s.value === 'ERROR')
      : STRATEGIES.filter(s => s.value !== 'UPDATE' || p.allowUpdate);
    this.duplicateStrategy = this.strategyOptions.some(s => s.value === p.duplicateStrategy)
      ? p.duplicateStrategy : this.strategyOptions.some(s => s.value === 'SKIP') ? 'SKIP' : this.strategyOptions[0].value;

    if (p.isOpeningBalance) {
      const allowed = this.modules.isFeatureEnabled(FEATURE_OPENING_BALANCE_IMPORT) && this.auth.hasPermission(IMPORT_PERMS.OPENING_BALANCE);
      if (!allowed) {
        this.accessProblem = 'Importing opening balances needs the Import opening balances permission and the Opening balance import feature.';
        return;
      }
      this.api.getOpeningBalanceDate().subscribe({
        next: res => { this.openingDateMissing = !res?.result?.date; },
        error: () => { this.openingDateMissing = false; }
      });
    }
    this.loadPresets();
  }

  private loadPresets(): void {
    if (!this.canUpload) return;
    this.api.getPresets(this.entityType).subscribe({
      next: res => {
        this.presets = res?.result ?? [];
        this.presetOptions = this.presets.map(x => ({ label: x.name, value: x.name }));
      },
      error: () => { this.presets = []; this.presetOptions = []; }
    });
  }

  private resume(uuid: string): void {
    this.api.getSession(uuid).subscribe({
      next: res => {
        const s = res?.result;
        if (!s) return;
        this.session = s;
        const step = this.revalidate && (s.status === 'FAILED' || s.status === 'CANCELLED') ? 2 : stepForStatus(s.status);
        if (step === 1) this.enterMap();
        else if (step === 2) this.enterValidate();
        else this.enterResults(s.status === 'IMPORTING');
      },
      error: err => this.toast(importErrorText(err, 'The import session could not be loaded.'))
    });
  }

  private goTo(step: number): void {
    this.step = step;
    const failed = step === 3 && !!this.session && (this.session.status === 'FAILED' || this.session.status === 'CANCELLED');
    const done = step === 3 && !!this.session && (this.session.status === 'COMPLETED' || this.session.status === 'COMPLETED_WITH_ERRORS');
    this.stages = flowStagesFrom(WIZARD_STEPS, done ? WIZARD_STEPS.length : step, { failed });
  }

  // ── Upload ──────────────────────────────────────────────────────────────

  browse(): void {
    this.fileInput?.nativeElement.click();
  }

  onFileInput(event: Event): void {
    const input = event.target as HTMLInputElement;
    this.setFile(input.files?.[0] ?? null);
    input.value = '';
  }

  onDragOver(event: DragEvent): void {
    event.preventDefault();
    this.dragOver = true;
  }

  onDragLeave(): void {
    this.dragOver = false;
  }

  onDrop(event: DragEvent): void {
    event.preventDefault();
    this.dragOver = false;
    this.setFile(event.dataTransfer?.files?.[0] ?? null);
  }

  setFile(file: File | null): void {
    this.file = file;
    this.fileError = fileProblem(file, this.profile?.maxFileSizeMb);
    this.uploadError = null;
    this.showSheetPicker = false;
    this.sheetChosen = false;
    this.session = null;
  }

  get fileSize(): string {
    return formatBytes(this.file?.size);
  }

  get canSubmitUpload(): boolean {
    return this.canUpload && !!this.file && !this.fileError && !this.uploading && !this.openingDateMissing;
  }

  upload(sheetName: string | null = null): void {
    if (!this.canSubmitUpload || !this.file) return;
    this.uploading = true;
    this.uploadPercent = 0;
    this.uploadError = null;
    this.api.upload({
      file: this.file, entityType: this.entityType, duplicateStrategy: this.duplicateStrategy, sheetName,
      mappingPreset: this.mappingPreset, notes: this.notes
    }).subscribe({
      next: e => {
        if (e.type === 'progress') { this.uploadPercent = e.percent; return; }
        this.uploading = false;
        this.uploadPercent = 100;
        this.onUploaded(e.session);
      },
      error: err => {
        this.uploading = false;
        this.uploadError = importErrorText(err, 'The file could not be uploaded.');
      }
    });
  }

  private onUploaded(s: SessionDetail): void {
    this.session = s;
    const sheets = s.sheetNames ?? [];
    if (sheets.length > 1 && !this.sheetChosen) {
      this.sheetOptions = sheets.map(n => ({ label: n, value: n }));
      this.sheetChoice = s.sheetName ?? sheets[0];
      this.showSheetPicker = true;
      return;
    }
    this.showSheetPicker = false;
    this.enterMap();
  }

  /** Sheet picker: the sheet the server read is kept; another one is uploaded again with sheetName. */
  useSheet(): void {
    if (!this.session || !this.sheetChoice) return;
    this.sheetChosen = true;
    if (this.sheetChoice === (this.session.sheetName ?? this.session.sheetNames?.[0])) {
      this.showSheetPicker = false;
      this.enterMap();
      return;
    }
    this.upload(this.sheetChoice);
  }

  downloadTemplate(): void {
    if (this.templateDownloading) return;
    this.templateDownloading = true;
    this.api.downloadTemplate(this.entityType).subscribe({
      next: res => { this.templateDownloading = false; saveDownload(res, `${this.entityType.toLowerCase()}-import-template.xlsx`); },
      error: async err => {
        this.templateDownloading = false;
        this.toast(await importErrorMessage(err, 'The template could not be downloaded.'));
      }
    });
  }

  // ── Map ─────────────────────────────────────────────────────────────────

  private enterMap(): void {
    this.rows = mappingRows(this.session?.mappings ?? []);
    this.mappingDirty = false;
    this.mappingError = null;
    this.presetName = this.session?.savedMappingName ?? '';
    this.recheckMapping();
    this.goTo(1);
  }

  private recheckMapping(): void {
    const s = this.session;
    this.defaulted = s ? defaultedRequired(this.fields, s.mappings ?? [], s.unmappedRequiredFields ?? []) : new Set<string>();
    this.problems = mappingProblems(this.rows, this.fields, this.defaulted);
    this.missingRequired = unmappedRequired(this.rows, this.fields, this.defaulted);
  }

  get hasDuplicates(): boolean {
    return this.rows.some(r => r.duplicate);
  }

  get canProceedFromMap(): boolean {
    return this.problems.length === 0 && !this.mappingSaving;
  }

  onTargetChange(row: MappingRow, value: string | null): void {
    row.target = value ?? null;
    row.isAutoMapped = false;
    row.confidence = null;
    row.mark = matchMark(row.target, null, false);
    markDuplicates(this.rows);
    this.recheckMapping();
    this.mappingDirty = true;
    // A mapping with a field chosen twice is refused by the server (IMP-MAP-03); it is saved once that is resolved.
    if (!this.hasDuplicates) this.saveMappings();
  }

  saveMappings(then?: () => void): void {
    if (!this.session) return;
    this.mappingSaving = true;
    this.mappingError = null;
    const body = this.rows.map(r => ({ sourceColumnIndex: r.sourceColumnIndex, targetFieldName: r.target }));
    this.api.updateMappings(this.session.uuid, body).subscribe({
      next: res => {
        this.mappingSaving = false;
        if (res?.result) {
          this.session = res.result;
          this.rows = mappingRows(res.result.mappings ?? []);
          this.recheckMapping();
        }
        this.mappingDirty = false;
        then?.();
      },
      error: err => {
        this.mappingSaving = false;
        this.mappingDirty = true;
        this.mappingError = importErrorText(err, 'The mapping could not be saved.');
      }
    });
  }

  nextFromMap(): void {
    if (!this.canProceedFromMap) return;
    if (this.mappingDirty) this.saveMappings(() => this.enterValidate());
    else this.enterValidate();
  }

  backToUpload(): void {
    this.goTo(0);
  }

  /** From Validate: changing the mapping again resets the validation on the server. */
  backToMap(): void {
    this.enterMap();
  }

  savePreset(): void {
    const name = this.presetName.trim();
    if (!this.session || !name || this.savingPreset) return;
    this.savingPreset = true;
    this.api.savePreset(this.session.uuid, name).subscribe({
      next: () => {
        this.savingPreset = false;
        this.messages.add({ severity: 'success', summary: 'Mapping saved', detail: `Saved as "${name}".`, life: 4000 });
        this.loadPresets();
      },
      error: err => {
        this.savingPreset = false;
        this.toast(importErrorText(err, 'The mapping could not be saved.'));
      }
    });
  }

  // ── Validate ────────────────────────────────────────────────────────────

  private enterValidate(): void {
    this.validated = this.session?.status === 'VALIDATED';
    this.errorGroups = [];
    this.firstErrors = [];
    this.actionError = null;
    this.goTo(2);
    this.loadPreview();
  }

  loadPreview(): void {
    if (!this.session) return;
    this.previewLoading = true;
    this.api.getPreview(this.session.uuid, 20).subscribe({
      next: res => {
        this.previewLoading = false;
        this.preview = res?.result ?? null;
        const cols = this.preview?.columns ?? [];
        this.previewRows = (this.preview?.rows ?? []).map(r => {
          const cells = cols.map(c => ({ value: r.values?.[c.fieldName] ?? '', issue: r.issues?.[c.fieldName] ?? null }));
          return { rowNumber: r.rowNumber, cells, hasIssue: cells.some(c => !!c.issue) };
        });
      },
      error: () => { this.previewLoading = false; this.preview = null; this.previewRows = []; }
    });
  }

  runValidation(): void {
    if (!this.session || !this.canExecute || this.validating) return;
    this.validating = true;
    this.actionError = null;
    this.api.validate(this.session.uuid).subscribe({
      next: res => {
        this.validating = false;
        const v = res?.result;
        if (!v) return;
        this.session = v.session;
        this.validated = true;
        this.errorGroups = Object.entries(v.errorCountByCode ?? {})
          .map(([code, count]) => ({ code, count }))
          .sort((a, b) => b.count - a.count || a.code.localeCompare(b.code));
        this.firstErrors = (v.errors ?? []).slice(0, 50);
        this.loadPreview();
      },
      error: err => {
        this.validating = false;
        this.actionError = importErrorText(err, 'The file could not be validated.');
      }
    });
  }

  get canImport(): boolean {
    return this.canExecute && this.validated && !!this.session && this.session.status === 'VALIDATED'
      && this.session.validRows > 0 && !this.executing;
  }

  executeImport(): void {
    if (!this.session || !this.canImport) return;
    this.executing = true;
    this.actionError = null;
    this.api.execute(this.session.uuid).subscribe({
      next: res => {
        this.executing = false;
        const r = res?.result;
        if (!r) return;
        this.session = r.session;
        this.enterResults(r.isBackground && !isTerminal(r.session.status));
      },
      error: err => {
        this.executing = false;
        this.actionError = importErrorText(err, 'The import could not be started.');
      }
    });
  }

  downloadErrors(): void {
    if (!this.session || this.errorsDownloading) return;
    this.errorsDownloading = true;
    this.api.downloadErrors(this.session.uuid).subscribe({
      next: res => { this.errorsDownloading = false; saveDownload(res, `import-errors-${this.session!.uuid}.xlsx`); },
      error: async err => {
        this.errorsDownloading = false;
        this.toast(await importErrorMessage(err, 'The error report could not be downloaded.'));
      }
    });
  }

  cancelImport(): void {
    if (!this.session || isTerminal(this.session.status)) { this.router.navigate([IMPORT_ROUTE]); return; }
    if (!this.canCancel || this.cancelling) return;
    this.cancelling = true;
    this.api.cancel(this.session.uuid).subscribe({
      next: () => {
        this.cancelling = false;
        this.messages.add({ severity: 'info', summary: 'Import cancelled', detail: 'Nothing from this file was imported.', life: 4000 });
        this.router.navigate([IMPORT_ROUTE]);
      },
      error: err => {
        this.cancelling = false;
        this.toast(importErrorText(err, 'The import could not be cancelled.'));
      }
    });
  }

  // ── Results ─────────────────────────────────────────────────────────────

  private enterResults(watch: boolean): void {
    this.background = watch;
    this.goTo(3);
    this.buildResults();
    if (watch && this.session) this.watch(this.session.uuid);
  }

  private watch(uuid: string): void {
    this.progressSub?.unsubscribe();
    this.progressTotal = this.session?.validRows ?? 0;
    this.progressSub = this.api.watchProgress(uuid).subscribe({
      next: p => {
        this.progressProcessed = p.processed;
        this.progressTotal = p.total;
        this.progressPercent = p.total > 0 ? Math.min(100, Math.round((100 * p.processed) / p.total)) : 0;
      },
      complete: () => this.reloadSession(uuid)
    });
  }

  private reloadSession(uuid: string): void {
    this.api.getSession(uuid).subscribe({
      next: res => {
        if (!res?.result) return;
        this.session = res.result;
        this.background = false;
        this.goTo(3);
        this.buildResults();
      },
      error: () => { this.background = false; }
    });
  }

  private buildResults(): void {
    const s = this.session;
    if (!s) { this.resultCards = []; this.duration = ''; return; }
    this.resultCards = [
      { key: 'imported', label: 'Imported', value: s.importedRows ?? 0, tone: 'ok' },
      { key: 'updated', label: 'Updated', value: s.updatedRows ?? 0, tone: 'in' },
      { key: 'skipped', label: 'Skipped', value: s.skippedRows ?? 0, tone: '' },
      { key: 'errors', label: 'Errors', value: (s.errorRows ?? 0) + (s.invalidRows ?? 0), tone: 'er' }
    ];
    this.duration = formatDuration(s.importStartedAt, s.importCompletedAt);
  }

  downloadResults(): void {
    if (!this.session || this.resultsDownloading) return;
    this.resultsDownloading = true;
    this.api.downloadResults(this.session.uuid).subscribe({
      next: res => { this.resultsDownloading = false; saveDownload(res, `import-results-${this.session!.uuid}.xlsx`); },
      error: async err => {
        this.resultsDownloading = false;
        this.toast(await importErrorMessage(err, 'The results report could not be downloaded.'));
      }
    });
  }

  importMore(): void {
    this.progressSub?.unsubscribe();
    this.progressSub = null;
    this.session = null;
    this.file = null;
    this.fileError = null;
    this.uploadError = null;
    this.uploadPercent = 0;
    this.notes = '';
    this.mappingPreset = null;
    this.showSheetPicker = false;
    this.sheetChosen = false;
    this.rows = [];
    this.problems = [];
    this.preview = null;
    this.previewRows = [];
    this.validated = false;
    this.errorGroups = [];
    this.firstErrors = [];
    this.actionError = null;
    this.resultCards = [];
    this.background = false;
    this.goTo(0);
    this.loadPresets();
    this.router.navigate([], { relativeTo: this.route, queryParams: {}, replaceUrl: true });
  }

  backToDashboard(): void {
    this.router.navigate([IMPORT_ROUTE]);
  }

  private toast(detail: string): void {
    this.messages.add({ severity: 'error', summary: 'Error', detail, life: 8000 });
  }

  trackRow = (_: number, r: MappingRow) => r.sourceColumnIndex;
}
