import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { SelectModule } from 'primeng/select';
import { ToggleSwitchModule } from 'primeng/toggleswitch';
import { ToastModule } from 'primeng/toast';
import { TextareaModule } from 'primeng/textarea';
import { MessageService } from 'primeng/api';
import { catchError, of } from 'rxjs';
import { SetupDefaultDetail, SetupDefaultsMeta, SetupDefaultsService } from '../../../services/setup-defaults.service';
import { FLOW } from '../../../shared/flow';
import { CoaTreeEditorComponent } from '../../onboarding/editors/coa-tree-editor.component';
import { TaxTableEditorComponent } from '../../onboarding/editors/tax-table-editor.component';
import {
  SETUP_CATEGORIES, SETUP_DEFAULTS_ROUTE, categoryLabel, clone, coaProblem, onboardingError, payloadCount, taxProblem
} from '../../onboarding/onboarding.shared';
import { RoleMatrixEditorComponent } from './editors/role-matrix-editor.component';
import { ApprovalRulesEditorComponent } from './editors/approval-rules-editor.component';
import { UomGroupsEditorComponent } from './editors/uom-groups-editor.component';

/** The categories with their own editor (task 158–162); every other one uses the JSON editor (task 163). */
export const STRUCTURED_EDITORS = ['COA', 'TAX', 'ROLES', 'APPROVAL_RULES', 'UOM'];

const COUNTRY_RE = /^(GLOBAL|[A-Z]{2})$/;
const INDUSTRY_RE = /^[A-Za-z0-9_]{1,50}$/;

/** A sensible empty payload per category for a new default. */
export function emptyPayload(category: string): any {
  switch (category) {
    case 'COA': return { accounts: ['Asset', 'Liability', 'Equity', 'Revenue', 'Expense'].map((t, i) => ({
      code: String(i + 1), name: t === 'Asset' ? 'Assets' : t === 'Liability' ? 'Liabilities' : t === 'Expense' ? 'Expenses' : t,
      root_type: t, level: 1, is_group: true, children: [] })) };
    case 'TAX': return { verified: false, taxCodes: [] };
    case 'CURRENCY': return { base: { code: '', name: '' }, additional: [] };
    case 'FISCAL': return { startMonth: 1, periodType: 'Monthly', dateFormat: 'DD/MM/YYYY' };
    case 'PAYMENT_TERMS': return { terms: [] };
    case 'UOM': return { groups: [] };
    case 'PRODUCT_CATEGORIES': return { categories: [] };
    case 'DOC_NUMBERING': return { sequences: [] };
    case 'APPROVAL_RULES': return { rules: [] };
    case 'ROLES': return { roles: [] };
    case 'WAREHOUSE': return { zoneTypes: [] };
    case 'DOC_TEMPLATES': case 'EMAIL_TEMPLATES': return { templates: [] };
    case 'HRM': return { verified: false, leaveTypes: [], salaryComponents: [], taxSlabTables: [] };
    default: return {};
  }
}

/**
 * A50 tasks 157–163 — add / edit one setup default (super admin). New: country, industry and category, then the
 * payload; existing: the key is fixed (D-CORE) and only name, notes, published and the payload change. The payload
 * has a category editor (COA tree, TAX table, ROLES matrix, APPROVAL_RULES levels, UOM groups) and, for every
 * category, "Edit as JSON". "Check" runs the server's schema + seeder validation; Save validates again.
 */
@Component({
  selector: 'app-setup-default-editor',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule, ButtonModule, InputTextModule, SelectModule, ToggleSwitchModule, ToastModule, TextareaModule,
    ...FLOW, CoaTreeEditorComponent, TaxTableEditorComponent, RoleMatrixEditorComponent, ApprovalRulesEditorComponent, UomGroupsEditorComponent],
  styleUrls: ['../../onboarding/onboarding.scss'],
  template: `
<p-toast></p-toast>
<sf-page [crumbs]="['System administration', 'Setup defaults', isNew ? 'New' : (row?.displayName || '…')]">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button label="Back to list" icon="pi pi-arrow-left" [text]="true" [routerLink]="listRoute"></p-button>
    <span class="sf-end" *ngIf="row && !isNew"><span class="ob-mu ob-sm">v{{ row.version }} · used by {{ row.appliedCount }} organization(s)</span></span>
  </div>
  <header class="sf-hdr sf-compact"><div class="sf-t"><div>
    <div class="sf-kicker">Setup default</div>
    <h1 data-testid="title">{{ isNew ? 'New setup default' : row?.displayName }}</h1>
  </div></div></header>

  <div class="sf-body">
    <div class="sf-card sf-empty" *ngIf="loading" data-testid="loading"><i class="pi pi-spin pi-spinner"></i> Loading…</div>
    <div class="ob-note er" *ngIf="loadError" data-testid="load-error"><i class="pi pi-exclamation-triangle"></i><div>{{ loadError }}</div></div>

    <ng-container *ngIf="!loading && !loadError">
      <section class="sf-card">
        <div class="sf-ch"><h2>Key and details</h2></div>
        <div class="sf-fields">
          <div class="sf-fld"><label for="sd-country">Country <span class="sf-req">*</span></label>
            <input pInputText id="sd-country" [(ngModel)]="countryCode" [readonly]="!isNew" maxlength="10" list="sd-countries" placeholder="PK or GLOBAL"
                   (ngModelChange)="countryCode = ($event || '').toUpperCase()" data-testid="country" />
            <datalist id="sd-countries"><option *ngFor="let c of meta?.countries ?? []" [value]="c"></option></datalist></div>
          <div class="sf-fld"><label for="sd-industry">Industry <span class="sf-req">*</span></label>
            <input pInputText id="sd-industry" [(ngModel)]="industryType" [readonly]="!isNew" maxlength="50" list="sd-industries" placeholder="Trading or GLOBAL"
                   data-testid="industry" />
            <datalist id="sd-industries"><option *ngFor="let c of meta?.industries ?? []" [value]="c"></option></datalist></div>
          <div class="sf-fld"><label for="sd-category">Category <span class="sf-req">*</span></label>
            <p-select inputId="sd-category" [options]="categoryOptions" optionLabel="label" optionValue="value" [ngModel]="dataCategory"
                      (ngModelChange)="setCategory($event)" [disabled]="!isNew" [style]="{ width: '100%' }" data-testid="category"></p-select></div>
          <div class="sf-fld"><label for="sd-name">Display name <span class="sf-req">*</span></label>
            <input pInputText id="sd-name" [(ngModel)]="displayName" maxlength="200" data-testid="name" /></div>
          <div class="sf-fld"><label for="sd-notes">Notes</label>
            <input pInputText id="sd-notes" [(ngModel)]="notes" maxlength="1000" /></div>
          <div class="sf-fld"><label for="sd-active">Published</label>
            <p-toggleswitch inputId="sd-active" [(ngModel)]="isActive" data-testid="active"></p-toggleswitch></div>
        </div>
        <div class="ob-note" *ngIf="!isNew && (row?.appliedCount ?? 0) > 0" style="margin-top:.75rem"><i class="pi pi-info-circle"></i>
          <div>Organizations already set up keep the data they copied. A payload change creates version {{ (row?.version ?? 0) + 1 }} for organizations set up from now on.</div></div>
      </section>

      <section class="sf-card" *ngIf="dataCategory">
        <div class="sf-ch">
          <h2>{{ label(dataCategory) }} <span class="ob-mu ob-sm">{{ recordCount }} records</span></h2>
          <div class="sf-ch-actions">
            <p-button *ngIf="hasEditor" [label]="jsonMode ? 'Use the editor' : 'Edit as JSON'" icon="pi pi-code" [text]="true" size="small"
                      (onClick)="toggleJson()" data-testid="toggle-json"></p-button>
            <p-button label="Check" icon="pi pi-check-square" [text]="true" size="small" [loading]="validating" (onClick)="validate()" data-testid="validate"></p-button>
          </div>
        </div>
        <div class="ob-note er" *ngIf="jsonError" data-testid="json-error"><i class="pi pi-exclamation-triangle"></i><div>{{ jsonError }}</div></div>
        <div class="ob-note" [ngClass]="validation.valid ? 'ok' : 'er'" *ngIf="validation" data-testid="validation">
          <i class="pi" [ngClass]="validation.valid ? 'pi-check-circle' : 'pi-times-circle'"></i>
          <div>{{ validation.valid ? 'The payload is valid.' : 'The payload has problems:' }}<ul *ngIf="validation.errors.length"><li *ngFor="let e of validation.errors">{{ e }}</li></ul></div></div>

        <ng-container *ngIf="!jsonMode && hasEditor; else jsonTpl" [ngSwitch]="dataCategory">
          <app-coa-tree-editor *ngSwitchCase="'COA'" [payload]="payload" (payloadChange)="onPayload($event)"></app-coa-tree-editor>
          <app-tax-table-editor *ngSwitchCase="'TAX'" [payload]="payload" [allowVerify]="true" (payloadChange)="onPayload($event)"></app-tax-table-editor>
          <app-role-matrix-editor *ngSwitchCase="'ROLES'" [payload]="payload" (payloadChange)="onPayload($event)"></app-role-matrix-editor>
          <app-approval-rules-editor *ngSwitchCase="'APPROVAL_RULES'" [payload]="payload" (payloadChange)="onPayload($event)"></app-approval-rules-editor>
          <app-uom-groups-editor *ngSwitchCase="'UOM'" [payload]="payload" (payloadChange)="onPayload($event)"></app-uom-groups-editor>
        </ng-container>
        <ng-template #jsonTpl>
          <textarea pTextarea class="ob-json" [(ngModel)]="jsonText" (ngModelChange)="onJsonText()" spellcheck="false" aria-label="Payload JSON"
                    data-testid="json"></textarea>
        </ng-template>
      </section>
      <div class="ob-note er" *ngIf="saveError" data-testid="save-error"><i class="pi pi-times-circle"></i><div>{{ saveError }}</div></div>
    </ng-container>
  </div>

  <div class="sf-fbar" *ngIf="!loading && !loadError">
    <span class="sf-mu sf-sm" data-testid="hint">{{ problem || 'Saved defaults reach only organizations set up afterwards.' }}</span>
    <span class="sf-end">
      <p-button label="Cancel" [text]="true" [routerLink]="listRoute"></p-button>
      <p-button label="Save" icon="pi pi-save" [loading]="saving" [disabled]="!!problem" (onClick)="save()" data-testid="save"></p-button>
    </span>
  </div>
</sf-page>`
})
export class SetupDefaultEditorComponent implements OnInit {
  private readonly api = inject(SetupDefaultsService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly messages = inject(MessageService, { optional: true });

  readonly listRoute = SETUP_DEFAULTS_ROUTE;
  readonly label = categoryLabel;

  isNew = true;
  uuid: string | null = null;
  row: SetupDefaultDetail | null = null;
  meta: SetupDefaultsMeta | null = null;
  loading = false;
  loadError = '';

  countryCode = '';
  industryType = '';
  dataCategory: string | null = null;
  displayName = '';
  notes: string | null = null;
  isActive = true;
  payload: any = null;

  jsonMode = false;
  jsonText = '';
  jsonError = '';
  validating = false;
  validation: { valid: boolean; errors: string[] } | null = null;
  saving = false;
  saveError = '';

  get categoryOptions(): { label: string; value: string }[] {
    const codes = this.meta?.categories?.map(c => c.code) ?? [...SETUP_CATEGORIES];
    return codes.map(c => ({ label: `${categoryLabel(c)} (${c})`, value: c }));
  }

  get hasEditor(): boolean { return !!this.dataCategory && STRUCTURED_EDITORS.includes(this.dataCategory); }
  get recordCount(): number { return this.dataCategory ? payloadCount(this.dataCategory, this.payload) : 0; }

  /** The first thing that stops Save. */
  get problem(): string | null {
    if (this.isNew) {
      if (!COUNTRY_RE.test(this.countryCode)) return 'Country: GLOBAL or a 2-letter ISO code (e.g. PK).';
      if (!INDUSTRY_RE.test(this.industryType)) return 'Industry: GLOBAL or letters, digits and _ (at most 50).';
      if (!this.dataCategory) return 'Choose the category.';
    }
    if (!this.displayName.trim()) return 'Enter the display name.';
    if (this.jsonError) return 'Fix the JSON first.';
    if (!this.payload || typeof this.payload !== 'object' || Array.isArray(this.payload)) return 'The payload must be a JSON object.';
    if (this.dataCategory === 'COA') return coaProblem(this.payload.accounts ?? []);
    if (this.dataCategory === 'TAX') return taxProblem(this.payload.taxCodes ?? []);
    if (this.dataCategory === 'APPROVAL_RULES') return ApprovalRulesEditorComponent.problemOf(this.payload.rules ?? []);
    if (this.dataCategory === 'UOM') return UomGroupsEditorComponent.problemOf(this.payload.groups ?? []);
    return null;
  }

  ngOnInit(): void {
    this.api.meta().pipe(catchError(() => of(null))).subscribe(r => (this.meta = r?.result ?? null));
    const id = this.route.snapshot.paramMap.get('uuid');
    const from = this.route.snapshot.queryParamMap.get('from');
    this.isNew = !id || id === 'new';
    this.uuid = this.isNew ? null : id;
    const source = this.uuid ?? from;
    if (!source) return;
    this.loading = true;
    this.api.get(source).subscribe({
      next: r => {
        this.loading = false;
        const d = r?.result;
        if (!d) { this.loadError = 'The default was not found.'; return; }
        this.countryCode = d.countryCode;
        this.industryType = d.industryType;
        this.dataCategory = d.dataCategory;
        this.displayName = this.isNew ? `${d.displayName} (copy)` : d.displayName;
        this.notes = d.notes;
        this.isActive = this.isNew ? false : d.isActive;
        this.payload = clone(d.dataPayload ?? {});
        if (!this.isNew) this.row = d;
        this.jsonMode = !this.hasEditor;
        if (this.jsonMode) this.jsonText = JSON.stringify(this.payload, null, 2);
      },
      error: err => { this.loading = false; this.loadError = onboardingError(err, 'The default could not be loaded.'); }
    });
  }

  setCategory(c: string): void {
    this.dataCategory = c;
    this.payload = emptyPayload(c);
    this.validation = null;
    this.jsonError = '';
    this.jsonMode = !this.hasEditor;
    this.jsonText = JSON.stringify(this.payload, null, 2);
  }

  onPayload(p: any): void {
    this.payload = p;
    this.validation = null;
  }

  toggleJson(): void {
    if (this.jsonMode) {
      if (this.jsonError) return;
      this.jsonMode = false;
    } else {
      this.jsonText = JSON.stringify(this.payload ?? {}, null, 2);
      this.jsonError = '';
      this.jsonMode = true;
    }
  }

  onJsonText(): void {
    this.validation = null;
    try {
      const v = JSON.parse(this.jsonText);
      if (!v || typeof v !== 'object' || Array.isArray(v)) { this.jsonError = 'The payload must be a JSON object ({ … }).'; return; }
      this.payload = v;
      this.jsonError = '';
    } catch (e: any) {
      this.jsonError = `Not valid JSON: ${e?.message ?? ''}`.trim();
    }
  }

  validate(): void {
    if (!this.dataCategory || this.jsonError) return;
    this.validating = true;
    this.api.validate(this.dataCategory, this.payload).subscribe({
      next: r => { this.validating = false; this.validation = { valid: !!r?.result?.valid, errors: r?.result?.errors ?? [] }; },
      error: err => { this.validating = false; this.validation = { valid: false, errors: [onboardingError(err, 'The payload could not be checked.')] }; }
    });
  }

  save(): void {
    if (this.problem || this.saving) return;
    this.saving = true;
    this.saveError = '';
    const done = (d: SetupDefaultDetail | null | undefined, msg: string) => {
      this.saving = false;
      this.messages?.add({ severity: 'success', summary: 'Saved', detail: msg, life: 3500 });
      if (d?.uuid) {
        if (this.isNew) this.router.navigate([SETUP_DEFAULTS_ROUTE, d.uuid]);
        else { this.row = d; this.payload = clone(d.dataPayload ?? this.payload); }
      }
    };
    const fail = (err: any) => {
      this.saving = false;
      this.saveError = err?.status === 409
        ? onboardingError(err, 'Someone else changed this default, or the key already exists. Reload and try again.')
        : onboardingError(err, 'The default could not be saved.');
    };
    if (this.isNew) {
      this.api.create({
        countryCode: this.countryCode, industryType: this.industryType, dataCategory: this.dataCategory!, displayName: this.displayName.trim(),
        notes: this.notes?.trim() || null, isActive: this.isActive, dataPayload: this.payload
      }).subscribe({ next: r => done(r?.result, 'The default was created.'), error: fail });
    } else {
      this.api.update(this.uuid!, {
        displayName: this.displayName.trim(), notes: this.notes?.trim() || null, isActive: this.isActive, dataPayload: this.payload,
        rowVersion: this.row?.rowVersion ?? null
      }).subscribe({ next: r => done(r?.result, `Saved as version ${r?.result?.version ?? ''}.`), error: fail });
    }
  }
}
