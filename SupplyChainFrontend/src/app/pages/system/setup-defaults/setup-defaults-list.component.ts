import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { TableModule } from 'primeng/table';
import { SelectModule } from 'primeng/select';
import { DialogModule } from 'primeng/dialog';
import { ToastModule } from 'primeng/toast';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { ConfirmationService, MessageService } from 'primeng/api';
import { catchError, forkJoin, of } from 'rxjs';
import {
  SetupDefaultPackItem, SetupDefaultRow, SetupDefaultsImportResult, SetupDefaultsMeta, SetupDefaultsService
} from '../../../services/setup-defaults.service';
import { FLOW } from '../../../shared/flow';
import { SETUP_CATEGORIES, SETUP_DEFAULTS_ROUTE, categoryLabel, errorResult, onboardingError, payloadCount, saveBlob } from '../../onboarding/onboarding.shared';

/** Reads a pack file (`{ defaults: [...] }`) or a bare array of items; throws a readable message otherwise. */
export function parsePackFile(text: string): SetupDefaultPackItem[] {
  let json: any;
  try { json = JSON.parse(text); } catch { throw new Error('The file is not valid JSON.'); }
  const items = Array.isArray(json) ? json : Array.isArray(json?.defaults) ? json.defaults : null;
  if (!items) throw new Error('Expected a setup-defaults file ({ "defaults": [...] }) or an array of defaults.');
  if (!items.length) throw new Error('The file has no defaults.');
  items.forEach((it: any, i: number) => {
    if (!it || typeof it !== 'object' || !it.countryCode || !it.industryType || !it.dataCategory || typeof it.payload !== 'object' || it.payload === null) {
      throw new Error(`Item ${i + 1} needs countryCode, industryType, dataCategory and a payload object.`);
    }
  });
  return items;
}

export function packKey(i: { countryCode: string; industryType: string; dataCategory: string }): string {
  return `${i.countryCode}/${i.industryType}/${i.dataCategory}`;
}

/**
 * A50 P1-M (tasks 155–157, 164–166) — System Administration › Setup Defaults (PLATFORM_SUPER_ADMIN): every default
 * (country × industry × category) with filters, add / edit / duplicate, publish / unpublish, delete (refused while
 * applied — deactivate instead), import a pack file (upload → check → confirm) and export.
 */
@Component({
  selector: 'app-setup-defaults-list',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule, ButtonModule, TableModule, SelectModule, DialogModule, ToastModule, ConfirmDialogModule, ...FLOW],
  providers: [ConfirmationService],
  styleUrls: ['../../onboarding/onboarding.scss'],
  template: `
<p-toast></p-toast>
<p-confirmDialog key="sd-list"></p-confirmDialog>
<sf-page [crumbs]="['System administration', 'Setup defaults']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button label="New default" icon="pi pi-plus" [routerLink]="[route, 'new']" data-testid="new"></p-button>
    <p-button label="Import JSON" icon="pi pi-upload" [text]="true" (onClick)="openImport()" data-testid="import"></p-button>
    <p-button label="Export all" icon="pi pi-download" [text]="true" [loading]="exporting" (onClick)="exportAll()" data-testid="export"></p-button>
    <span class="sf-end"><p-button label="Refresh" icon="pi pi-refresh" [text]="true" [loading]="loading" (onClick)="load()"></p-button></span>
  </div>
  <div class="sf-body">
    <div class="sf-lr-head">
      <div><h1>Setup defaults <span class="sf-count">{{ filtered.length }}</span></h1>
        <div class="sf-sub">The starting data new organizations get from the setup wizard, per country, industry and category.
          Changes reach only organizations set up afterwards.</div></div>
    </div>
    <div class="sf-filters" data-testid="filters">
      <p-select [options]="countryOptions" [(ngModel)]="fCountry" placeholder="Country" [showClear]="true" data-testid="f-country"></p-select>
      <p-select [options]="industryOptions" [(ngModel)]="fIndustry" placeholder="Industry" [showClear]="true" data-testid="f-industry"></p-select>
      <p-select [options]="categoryOptions" optionLabel="label" optionValue="value" [(ngModel)]="fCategory" placeholder="Category" [showClear]="true"
                data-testid="f-category"></p-select>
      <p-select [options]="versionOptions" [(ngModel)]="fVersion" placeholder="Version" [showClear]="true" data-testid="f-version"></p-select>
      <p-select [options]="activeOptions" optionLabel="label" optionValue="value" [(ngModel)]="fActive" placeholder="Status" [showClear]="true"
                data-testid="f-active"></p-select>
    </div>
    <div class="ob-note er" *ngIf="error" data-testid="error"><i class="pi pi-exclamation-triangle"></i>
      <div class="grow">{{ error }}</div><p-button label="Retry" size="small" [text]="true" (onClick)="load()"></p-button></div>
    <div class="sf-card p0 ob-scroll">
      <p-table [value]="filtered" [loading]="loading" dataKey="uuid" [tableStyle]="{ 'min-width': '56rem' }" [paginator]="filtered.length > 50" [rows]="50">
        <ng-template pTemplate="header">
          <tr><th>Country</th><th>Industry</th><th>Category</th><th>Name</th><th class="ob-num">Records</th><th class="ob-num">Version</th>
            <th class="ob-num">Orgs</th><th>Status</th><th style="width:13rem"></th></tr>
        </ng-template>
        <ng-template pTemplate="body" let-r>
          <tr [attr.data-testid]="'row-' + r.uuid">
            <td class="sf-mono">{{ r.countryCode }}</td>
            <td>{{ r.industryType }}</td>
            <td>{{ label(r.dataCategory) }}<div class="ob-mu ob-sm sf-mono">{{ r.dataCategory }}</div></td>
            <td><a [routerLink]="[route, r.uuid]">{{ r.displayName }}</a><span class="sf-pill" *ngIf="r.isSystemPack" title="Shipped with the system">pack</span></td>
            <td class="ob-num">{{ r.recordCount }}</td>
            <td class="ob-num">v{{ r.version }}</td>
            <td class="ob-num">{{ r.appliedCount }}</td>
            <td><span class="sf-pill" [ngClass]="r.isActive ? 'ok' : ''" [attr.data-testid]="'status-' + r.uuid">{{ r.isActive ? 'Published' : 'Unpublished' }}</span></td>
            <td>
              <p-button [label]="r.isActive ? 'Unpublish' : 'Publish'" [text]="true" size="small" [loading]="busy === r.uuid"
                        (onClick)="togglePublish(r)" [attr.data-testid]="'publish-' + r.uuid"></p-button>
              <p-button icon="pi pi-copy" [text]="true" size="small" ariaLabel="Duplicate" (onClick)="duplicate(r)"></p-button>
              <p-button icon="pi pi-trash" [text]="true" size="small" severity="danger" ariaLabel="Delete" (onClick)="confirmDelete(r)"
                        [attr.data-testid]="'delete-' + r.uuid"></p-button>
            </td>
          </tr>
        </ng-template>
        <ng-template pTemplate="emptymessage"><tr><td colspan="9" class="sf-empty" data-testid="empty">{{ rows.length ? 'Nothing matches the filters.' : 'No setup defaults yet.' }}</td></tr></ng-template>
      </p-table>
    </div>
  </div>
</sf-page>

<p-dialog [(visible)]="importOpen" [modal]="true" header="Import setup defaults" [style]="{ width: '46rem', maxWidth: '96vw' }" styleClass="sdlg">
  <div data-testid="import-dialog">
    <p class="ob-mu ob-sm">Upload a JSON file exported from this page (or an array of defaults). Nothing is saved until you confirm, and an invalid
      item stops the whole import.</p>
    <input type="file" accept=".json,application/json" (change)="onFile($event)" data-testid="import-file" />
    <div class="ob-row" style="margin:.5rem 0" *ngIf="importItems.length">
      <span class="ob-sm">When a default already exists:</span>
      <p-select [options]="modeOptions" optionLabel="label" optionValue="value" [(ngModel)]="importMode" (ngModelChange)="importCheck = null" data-testid="import-mode"></p-select>
    </div>
    <div class="ob-note er" *ngIf="importError" data-testid="import-error"><i class="pi pi-times-circle"></i><div>{{ importError }}</div></div>
    <div class="ob-scroll" *ngIf="importItems.length" data-testid="import-preview">
      <table class="ob-matrix" style="width:100%">
        <thead><tr><th style="text-align:left">Default</th><th style="text-align:left">Name</th><th class="ob-num">Records</th><th>Check</th></tr></thead>
        <tbody>
          <tr *ngFor="let it of importItems; let i = index" [attr.data-testid]="'imp-' + i">
            <td class="p">{{ key(it) }}</td><td>{{ it.displayName }}</td><td class="ob-num">{{ count(it) }}</td>
            <td class="c"><span class="sf-pill" *ngIf="actionOf(it) as a" [ngClass]="a === 'Invalid' ? 'er' : a === 'Unchanged' ? '' : 'ok'">{{ a }}</span></td>
          </tr>
        </tbody>
      </table>
    </div>
    <div class="ob-note" *ngIf="importCheck" data-testid="import-summary"><i class="pi pi-info-circle"></i>
      <div>{{ importCheck.dryRun ? 'Check:' : 'Imported:' }} {{ importCheck.created }} new, {{ importCheck.updated }} updated, {{ importCheck.unchanged }} unchanged.
        <ul *ngIf="importCheck.errors.length"><li *ngFor="let e of importCheck.errors">{{ e.key }}: {{ e.errors.join('; ') }}</li></ul></div></div>
  </div>
  <ng-template pTemplate="footer">
    <p-button label="Close" [text]="true" (onClick)="importOpen = false"></p-button>
    <p-button label="Check" icon="pi pi-search" [text]="true" [disabled]="!importItems.length" [loading]="importing" (onClick)="runImport(true)" data-testid="import-check"></p-button>
    <p-button label="Import" icon="pi pi-check" [disabled]="!canConfirmImport" [loading]="importing" (onClick)="runImport(false)" data-testid="import-confirm"></p-button>
  </ng-template>
</p-dialog>`
})
export class SetupDefaultsListComponent implements OnInit {
  private readonly api = inject(SetupDefaultsService);
  private readonly router = inject(Router);
  private readonly confirm = inject(ConfirmationService);
  private readonly messages = inject(MessageService, { optional: true });

  readonly route = SETUP_DEFAULTS_ROUTE;
  readonly label = categoryLabel;
  readonly key = packKey;
  readonly activeOptions = [{ label: 'Published', value: true }, { label: 'Unpublished', value: false }];
  readonly modeOptions = [{ label: 'Update it (upsert)', value: 'upsert' }, { label: 'Leave it alone (insert only)', value: 'insertOnly' }];

  rows: SetupDefaultRow[] = [];
  meta: SetupDefaultsMeta | null = null;
  loading = false;
  error = '';
  busy: string | null = null;
  exporting = false;

  fCountry: string | null = null;
  fIndustry: string | null = null;
  fCategory: string | null = null;
  fVersion: number | null = null;
  fActive: boolean | null = null;

  importOpen = false;
  importItems: SetupDefaultPackItem[] = [];
  importError = '';
  importMode: 'upsert' | 'insertOnly' = 'upsert';
  importCheck: SetupDefaultsImportResult | null = null;
  importing = false;

  get countryOptions(): string[] { return [...new Set([...(this.meta?.countries ?? []), ...this.rows.map(r => r.countryCode)])].sort(); }
  get industryOptions(): string[] { return [...new Set([...(this.meta?.industries ?? []), ...this.rows.map(r => r.industryType)])].sort(); }
  get categoryOptions(): { label: string; value: string }[] {
    const codes = this.meta?.categories?.map(c => c.code) ?? [...SETUP_CATEGORIES];
    return codes.map(c => ({ label: categoryLabel(c), value: c }));
  }
  get versionOptions(): number[] { return [...new Set(this.rows.map(r => r.version))].sort((a, b) => a - b); }

  get filtered(): SetupDefaultRow[] {
    return this.rows.filter(r =>
      (!this.fCountry || r.countryCode === this.fCountry) &&
      (!this.fIndustry || r.industryType === this.fIndustry) &&
      (!this.fCategory || r.dataCategory === this.fCategory) &&
      (this.fVersion === null || this.fVersion === undefined || r.version === this.fVersion) &&
      (this.fActive === null || this.fActive === undefined || r.isActive === this.fActive));
  }

  ngOnInit(): void { this.load(); }

  load(): void {
    this.loading = true;
    this.error = '';
    forkJoin({
      list: this.api.list(),
      meta: this.api.meta().pipe(catchError(() => of(null)))
    }).subscribe({
      next: r => { this.loading = false; this.rows = r.list?.result ?? []; this.meta = r.meta?.result ?? null; },
      error: err => { this.loading = false; this.error = onboardingError(err, 'The setup defaults could not be loaded.'); }
    });
  }

  togglePublish(r: SetupDefaultRow): void {
    this.busy = r.uuid;
    (r.isActive ? this.api.unpublish(r.uuid) : this.api.publish(r.uuid)).subscribe({
      next: res => {
        this.busy = null;
        const updated = res?.result;
        this.rows = this.rows.map(x => x.uuid === r.uuid ? { ...x, ...(updated ?? {}), isActive: updated?.isActive ?? !r.isActive } : x);
      },
      error: err => {
        this.busy = null;
        this.messages?.add({ severity: 'error', summary: 'Not changed', detail: onboardingError(err, 'The default could not be changed.'), life: 6000 });
      }
    });
  }

  duplicate(r: SetupDefaultRow): void { this.router.navigate([this.route, 'new'], { queryParams: { from: r.uuid } }); }

  confirmDelete(r: SetupDefaultRow): void {
    this.confirm.confirm({
      key: 'sd-list', header: 'Delete setup default', icon: 'pi pi-trash', acceptLabel: 'Delete', rejectLabel: 'Cancel',
      acceptButtonStyleClass: 'p-button-danger',
      message: `Delete "${r.displayName}" (${packKey(r)})? A default that organizations were set up with cannot be deleted — unpublish it instead.`,
      accept: () => this.delete(r)
    });
  }

  delete(r: SetupDefaultRow): void {
    this.api.delete(r.uuid).subscribe({
      next: () => {
        this.rows = this.rows.filter(x => x.uuid !== r.uuid);
        this.messages?.add({ severity: 'success', summary: 'Deleted', detail: r.displayName, life: 3000 });
      },
      error: err => {
        const detail = err?.status === 409
          ? onboardingError(err, 'Organizations were set up with this default, so it cannot be deleted. Unpublish it instead.')
          : onboardingError(err, 'The default could not be deleted.');
        this.messages?.add({ severity: 'error', summary: 'Not deleted', detail, life: 7000 });
      }
    });
  }

  exportAll(): void {
    this.exporting = true;
    this.api.export({ country: this.fCountry, industry: this.fIndustry, category: this.fCategory }).subscribe({
      next: blob => {
        this.exporting = false;
        const d = new Date();
        const stamp = `${d.getFullYear()}${String(d.getMonth() + 1).padStart(2, '0')}${String(d.getDate()).padStart(2, '0')}`;
        saveBlob(blob, `setup-defaults-${stamp}.json`);
      },
      error: err => {
        this.exporting = false;
        this.messages?.add({ severity: 'error', summary: 'Export failed', detail: onboardingError(err, 'The defaults could not be exported.'), life: 6000 });
      }
    });
  }

  // ── Import ────────────────────────────────────────────────────────────────

  openImport(): void {
    this.importItems = [];
    this.importError = '';
    this.importCheck = null;
    this.importMode = 'upsert';
    this.importOpen = true;
  }

  onFile(event: Event): void {
    const file = (event.target as HTMLInputElement)?.files?.[0];
    this.importItems = [];
    this.importError = '';
    this.importCheck = null;
    if (!file) return;
    if (file.size > 10 * 1024 * 1024) { this.importError = 'The file is larger than 10 MB.'; return; }
    file.text().then(text => this.readText(text)).catch(() => (this.importError = 'The file could not be read.'));
  }

  readText(text: string): void {
    try { this.importItems = parsePackFile(text); } catch (e: any) { this.importError = e?.message ?? 'The file could not be read.'; }
  }

  count(it: SetupDefaultPackItem): number { return payloadCount(it.dataCategory, it.payload); }

  actionOf(it: SetupDefaultPackItem): string | null {
    const k = packKey(it);
    if (this.importCheck?.errors?.some(e => e.key === k)) return 'Invalid';
    return this.importCheck?.items?.find(i => i.key === k)?.action ?? null;
  }

  /** Confirm only after a clean check for the same file and mode. */
  get canConfirmImport(): boolean {
    return !!this.importItems.length && !!this.importCheck?.dryRun && !this.importCheck.errors?.length && !this.importing;
  }

  runImport(dryRun: boolean): void {
    if (!this.importItems.length) return;
    this.importing = true;
    this.importError = '';
    this.api.import({ format: 'sms-setup-defaults', version: 1, defaults: this.importItems }, dryRun, this.importMode).subscribe({
      next: r => {
        this.importing = false;
        this.importCheck = r?.result ?? null;
        if (!dryRun) {
          this.messages?.add({ severity: 'success', summary: 'Imported',
            detail: `${this.importCheck?.created ?? 0} new, ${this.importCheck?.updated ?? 0} updated.`, life: 4000 });
          this.importOpen = false;
          this.load();
        }
      },
      error: err => {
        this.importing = false;
        const res = errorResult<SetupDefaultsImportResult>(err);
        if (res && Array.isArray(res.errors)) this.importCheck = { ...res, dryRun: true };
        this.importError = onboardingError(err, 'The import was refused.');
      }
    });
  }
}
