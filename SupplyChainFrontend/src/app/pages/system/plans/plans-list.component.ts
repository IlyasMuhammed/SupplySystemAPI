import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { CheckboxModule } from 'primeng/checkbox';
import { DialogModule } from 'primeng/dialog';
import { InputTextModule } from 'primeng/inputtext';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';
import { Observable, of, switchMap } from 'rxjs';
import {
  Plan, PlanFeatureChange, PlanFeatureItem, PlanWithFeatures, FeaturesAdminService, ApplyPlanResult, PlanApplyMode, PlanApplyOrgResult
} from '../../../services/features-admin.service';
import { FLOW } from '../../../shared/flow';
import { onboardingError } from '../../onboarding/onboarding.shared';

export interface FeatureGroup { module: PlanFeatureItem; children: PlanFeatureItem[]; }

/** True when the checkbox cannot be changed: core features stay, unbuilt ones and the licensed-by-hand ones are never in a plan. */
export function planLocked(f: PlanFeatureItem): boolean {
  return f.isCore || !f.isAvailable || f.licensedByHandOnly;
}

/**
 * A51 P2 - System Administration > Plans (api/system/plans, platform super admin). Three plan cards (name, price, how many
 * features); clicking one opens an edit view with every feature as a checkbox. Save writes the plan's features and fields. The
 * price is for display only. A plan change applies to the next read (new organizations, "apply plan template").
 */
@Component({
  selector: 'app-plans-list',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, CheckboxModule, DialogModule, InputTextModule, ToastModule, ...FLOW],
  styleUrls: ['../../onboarding/onboarding.scss'],
  styles: [`
    .pl-cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(15rem, 1fr)); gap: 1rem; }
    .pl-card { text-align: left; width: 100%; cursor: pointer; border: 1px solid var(--sms-border); background: var(--sms-surface); color: var(--sms-text);
      border-radius: var(--sms-radius, 8px); padding: 1.1rem 1.2rem; display: flex; flex-direction: column; gap: .35rem; font: inherit; }
    .pl-card:hover, .pl-card:focus-visible { border-color: var(--sms-primary); outline: none; }
    .pl-edit { margin-top: .5rem; }
    .pl-name { font-size: 1.15rem; font-weight: 600; }
    .pl-price { font-size: 1.5rem; font-weight: 600; font-variant-numeric: tabular-nums; }
    .pl-group { margin-bottom: 1rem; }
    .pl-child { margin-left: 1.75rem; }
    .pl-feat { display: flex; align-items: center; gap: .5rem; padding: .2rem 0; }
    .pl-apply-org { border-bottom: 1px solid var(--sms-border); padding: .4rem 0; }
    .pl-apply-org summary { cursor: pointer; display: flex; gap: .75rem; align-items: baseline; flex-wrap: wrap; }
    .pl-apply-org ul { margin: .35rem 0 .35rem 1.25rem; padding: 0; }
    .pl-apply-list { max-height: 18rem; overflow: auto; margin-top: .5rem; }
    .pl-mode { display: flex; flex-direction: column; gap: .4rem; margin: .5rem 0; }
    .pl-mode label { display: flex; align-items: flex-start; gap: .5rem; cursor: pointer; }
  `],
  template: `
<p-toast></p-toast>
<sf-page [crumbs]="['System Administration', 'Plans']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button *ngIf="selected" label="Back to plans" icon="pi pi-arrow-left" [text]="true" (onClick)="back()" data-testid="back"></p-button>
    <p-button *ngIf="selected" label="Save" icon="pi pi-save" [disabled]="!canSave" [loading]="saving" (onClick)="save()" data-testid="save"></p-button>
    <span class="sf-end"><p-button label="Refresh" icon="pi pi-refresh" [text]="true" [loading]="loading" (onClick)="selected ? open(selected.code) : load()"></p-button></span>
  </div>
  <div class="sf-body">
    <div class="ob-note er" *ngIf="error" data-testid="error"><i class="pi pi-exclamation-triangle"></i>
      <div class="grow">{{ error }}</div><p-button label="Retry" size="small" [text]="true" (onClick)="selected ? open(selected.code) : load()"></p-button></div>

    <ng-container *ngIf="!selected">
      <div class="sf-lr-head">
        <div><h1>Plans <span class="sf-count">{{ plans.length }}</span></h1>
          <div class="sf-sub">What each plan includes. Click <strong>Edit plan</strong> on a plan to change its name, price and features. Prices are shown for reference only.</div></div>
      </div>
      <div class="pl-cards" data-testid="cards">
        <div class="pl-card sf-card" *ngFor="let p of plans" tabindex="0" (click)="open(p.code)" (keydown.enter)="open(p.code)" [attr.data-testid]="'plan-' + p.code">
          <span class="pl-name" [attr.data-testid]="'name-' + p.code">{{ p.name }}</span>
          <span class="ob-mu ob-sm sf-mono">{{ p.code }}</span>
          <span class="pl-price" [attr.data-testid]="'price-' + p.code">{{ priceLabel(p) }}</span>
          <span [attr.data-testid]="'count-' + p.code">{{ p.includedFeatureCount }} of {{ p.totalFeatureCount }} features</span>
          <span class="sf-pill" [ngClass]="p.isActive ? 'ok' : ''">{{ p.isActive ? 'Active' : 'Inactive' }}</span>
          <span class="pl-edit">
            <p-button label="Edit plan" icon="pi pi-pencil" size="small" (onClick)="$event.stopPropagation(); open(p.code)"
                      [attr.data-testid]="'edit-' + p.code"></p-button>
          </span>
        </div>
      </div>
      <div class="sf-empty" *ngIf="!plans.length && !loading" data-testid="empty">No plans.</div>
    </ng-container>

    <ng-container *ngIf="selected">
      <div class="sf-lr-head">
        <div><h1><i class="pi pi-pencil"></i> Edit plan: {{ selected.name }} <span class="sf-count" data-testid="included-count">{{ includedCount }}</span></h1>
          <div class="sf-sub">Tick the features this plan includes, then press <strong>Save</strong> (top left). Changes apply to organizations created or reset from this plan afterwards; after saving you can also apply them to organizations already on the plan.</div></div>
      </div>
      <div class="sf-card" data-testid="plan-form">
        <div class="sf-fields">
          <div class="sf-fld"><label class="sf-req" for="p-name">Name</label>
            <input pInputText id="p-name" [(ngModel)]="form.name" maxlength="100" data-testid="f-name" /></div>
          <div class="sf-fld"><label for="p-price">Monthly price (display only)</label>
            <input pInputText id="p-price" type="number" min="0" step="0.01" [(ngModel)]="form.monthlyPrice" data-testid="f-price" /></div>
          <div class="sf-fld"><label for="p-sort">Sort order</label>
            <input pInputText id="p-sort" type="number" [(ngModel)]="form.sortOrder" data-testid="f-sort" /></div>
          <div class="sf-fld span-3"><label for="p-desc">Description</label>
            <input pInputText id="p-desc" [(ngModel)]="form.description" maxlength="500" data-testid="f-description" /></div>
          <div class="sf-fld"><label>Status</label>
            <label class="ob-row"><p-checkbox [binary]="true" [(ngModel)]="form.isActive" inputId="p-active" data-testid="f-active"></p-checkbox> Active</label></div>
        </div>
      </div>
      <div class="sf-card" data-testid="features">
        <div class="pl-group" *ngFor="let g of groups">
          <label class="pl-feat" [attr.data-testid]="'feature-' + g.module.featureCode">
            <p-checkbox [binary]="true" [ngModel]="g.module.isIncluded" (onChange)="toggle(g.module, $event.checked)" [disabled]="locked(g.module)"
              [inputId]="'pf-' + g.module.featureCode"></p-checkbox>
            <strong>{{ g.module.featureName }}</strong>
            <span class="sf-pill" *ngIf="!g.module.isAvailable">Not built</span>
            <span class="sf-pill" *ngIf="g.module.licensedByHandOnly">By hand only</span>
            <span class="sf-pill" *ngIf="g.module.isCore">Core</span>
          </label>
          <label class="pl-feat pl-child" *ngFor="let f of g.children" [attr.data-testid]="'feature-' + f.featureCode">
            <p-checkbox [binary]="true" [ngModel]="f.isIncluded" (onChange)="toggle(f, $event.checked)" [disabled]="locked(f)"
              [inputId]="'pf-' + f.featureCode"></p-checkbox>
            {{ f.featureName }}
            <span class="sf-pill" *ngIf="!f.isAvailable">Not built</span>
            <span class="sf-pill" *ngIf="f.licensedByHandOnly">By hand only</span>
          </label>
        </div>
      </div>
    </ng-container>
  </div>
</sf-page>

<!-- A51 P7: after a feature change, offer to push it to the organizations already on the plan -->
<p-dialog [visible]="applyOpen" (visibleChange)="!$event && skipApply()" [modal]="true" [style]="{ width: '640px', maxWidth: '95vw' }"
          header="Apply to existing organizations?" [draggable]="false" data-testid="apply-dialog">
  <ng-container *ngIf="applyPreview as p">
    <p data-testid="apply-count"><strong>{{ p.organizationCount }}</strong> organization{{ p.organizationCount === 1 ? ' is' : 's are' }} on this plan — apply the change to {{ p.organizationCount === 1 ? 'it' : 'them' }}?</p>
    <p class="ob-mu ob-sm">Saving the plan only affects organizations created or reset from it afterwards. Features that are Coming Soon are never licensed this way.</p>

    <div class="pl-mode" role="radiogroup" aria-label="How to apply">
      <label><input type="radio" name="apply-mode" [checked]="applyMode === 'addOnly'" (change)="setApplyMode('addOnly')" data-testid="mode-addOnly" />
        <span><strong>Add only</strong> (recommended) — license newly included features; nothing is ever removed.</span></label>
      <label><input type="radio" name="apply-mode" [checked]="applyMode === 'full'" (change)="setApplyMode('full')" data-testid="mode-full" />
        <span><strong>Full reset</strong> — the same as each organization's "Reset to Plan Defaults": features not in the plan are removed too
          (core, grandfathered and hand-licensed features are kept).</span></label>
    </div>

    <p data-testid="apply-totals">Preview: <b>{{ p.totalAdded }}</b> added, <b>{{ p.totalRemoved }}</b> removed, <b>{{ p.totalSkipped }}</b> skipped.</p>
    <div class="pl-apply-list" data-testid="apply-list">
      <details class="pl-apply-org" *ngFor="let o of p.organizations" [attr.data-testid]="'apply-org-' + o.organizationId">
        <summary>
          <strong>{{ o.orgName || o.orgCode }}</strong>
          <span class="sf-pill ok" *ngIf="o.added.length">+{{ o.added.length }}</span>
          <span class="sf-pill er" *ngIf="o.removed.length">−{{ o.removed.length }}</span>
          <span class="sf-pill" *ngIf="o.skipped.length">{{ o.skipped.length }} skipped</span>
          <span class="ob-mu ob-sm" *ngIf="!o.added.length && !o.removed.length && !o.skipped.length">no change</span>
        </summary>
        <ul *ngIf="o.added.length"><li *ngFor="let f of o.added">Adds {{ f.featureName }}</li></ul>
        <ul *ngIf="o.removed.length"><li *ngFor="let f of o.removed">Removes {{ f.featureName }}</li></ul>
        <ul *ngIf="o.skipped.length"><li *ngFor="let f of o.skipped" class="ob-mu">Skips {{ f.featureName }} — {{ f.detail || f.reason }}</li></ul>
      </details>
    </div>

    <label class="ob-row" *ngIf="applyMode === 'full'" data-testid="full-confirm">
      <p-checkbox [binary]="true" [(ngModel)]="fullConfirmed" inputId="full-confirm"></p-checkbox>
      I understand that features not in this plan will be removed from these organizations.
    </label>
  </ng-container>
  <div class="ob-note er" *ngIf="applyError" data-testid="apply-error">{{ applyError }}</div>
  <ng-template pTemplate="footer">
    <p-button label="Not now" [text]="true" severity="secondary" (onClick)="skipApply()" data-testid="apply-skip"></p-button>
    <p-button [label]="applyMode === 'full' ? 'Apply full reset' : 'Apply (add only)'" icon="pi pi-check" [loading]="applyLoading || applying"
              [disabled]="!canApply" (onClick)="confirmApply()" data-testid="apply-confirm"></p-button>
  </ng-template>
</p-dialog>`
})
export class PlansListComponent implements OnInit {
  private readonly api = inject(FeaturesAdminService);
  private readonly messages = inject(MessageService, { optional: true });

  plans: Plan[] = [];
  selected: PlanWithFeatures | null = null;
  groups: FeatureGroup[] = [];
  form = { name: '', description: '', monthlyPrice: null as number | null, isActive: true, sortOrder: 0 };
  loading = false;
  saving = false;
  error = '';
  private original = new Map<string, boolean>();
  private originalForm = '';

  // A51 P7 - "N organizations are on this plan - apply to them?"
  applyOpen = false;
  applyCode = '';
  applyMode: PlanApplyMode = 'addOnly';
  applyPreview: ApplyPlanResult | null = null;
  applyLoading = false;
  applying = false;
  fullConfirmed = false;
  applyError = '';

  ngOnInit(): void { this.load(); }

  load(): void {
    this.loading = true;
    this.error = '';
    this.api.listPlans().subscribe({
      next: r => { this.loading = false; this.plans = r.result ?? []; },
      error: err => { this.loading = false; this.error = onboardingError(err, 'The plans could not be loaded.'); }
    });
  }

  open(code: string): void {
    this.loading = true;
    this.error = '';
    this.api.getPlan(code).subscribe({
      next: r => { this.loading = false; if (r.result) this.select(r.result); },
      error: err => { this.loading = false; this.error = onboardingError(err, 'The plan could not be loaded.'); }
    });
  }

  back(): void {
    this.selected = null;
    this.groups = [];
    this.error = '';
    this.load();
  }

  priceLabel(p: Plan): string {
    return p.monthlyPrice == null ? 'No price' : `${p.monthlyPrice.toFixed(2)} / month`;
  }

  locked = planLocked;

  get includedCount(): number {
    return this.groups.reduce((n, g) => n + (g.module.isIncluded ? 1 : 0) + g.children.filter(c => c.isIncluded).length, 0);
  }

  /** Features whose box differs from what the server holds. */
  get changes(): PlanFeatureChange[] {
    const all = this.groups.flatMap(g => [g.module, ...g.children]);
    return all.filter(f => this.original.get(f.featureCode) !== f.isIncluded).map(f => ({ featureCode: f.featureCode, isIncluded: f.isIncluded }));
  }

  get formChanged(): boolean { return JSON.stringify(this.form) !== this.originalForm; }

  get canSave(): boolean {
    return !this.saving && this.form.name.trim().length > 0 && (this.changes.length > 0 || this.formChanged);
  }

  /** A module takes its sub-features with it when unticked; a sub-feature ticked on brings its module in. */
  toggle(f: PlanFeatureItem, checked: boolean): void {
    f.isIncluded = checked;
    for (const g of this.groups) {
      if (g.module === f && !checked) g.children.forEach(c => { if (!planLocked(c)) c.isIncluded = false; });
      if (g.children.includes(f) && checked && !g.module.isIncluded && !planLocked(g.module)) g.module.isIncluded = true;
    }
  }

  save(): void {
    if (!this.canSave || !this.selected) return;
    const code = this.selected.code;
    const changes = this.changes;
    const fieldsChanged = this.formChanged;
    this.saving = true;
    this.error = '';
    const fields$: Observable<unknown> = fieldsChanged
      ? this.api.updatePlan(code, {
          name: this.form.name.trim(), description: this.form.description.trim() || null,
          monthlyPrice: this.form.monthlyPrice === null || (this.form.monthlyPrice as unknown) === '' ? null : Number(this.form.monthlyPrice),
          isActive: this.form.isActive, sortOrder: Number(this.form.sortOrder) || 0
        })
      : of(null);
    fields$.pipe(switchMap(() => changes.length ? this.api.updatePlanFeatures(code, changes) : of(null))).subscribe({
      next: () => {
        this.saving = false;
        this.messages?.add({ severity: 'success', summary: 'Saved', detail: 'Plan saved.' });
        this.open(code);
        // The feature set changed: organizations already on the plan keep what they have unless the super admin pushes it.
        if (changes.length > 0) this.offerApply(code);
      },
      error: err => { this.saving = false; this.error = onboardingError(err, 'The plan could not be saved.'); }
    });
  }

  // ── A51 P7: apply the plan to the organizations on it ─────────────────────

  /** A dry run (add only) tells how many organizations are on the plan and what would change; with none, nothing is offered. */
  offerApply(code: string): void {
    this.applyCode = code;
    this.applyMode = 'addOnly';
    this.fullConfirmed = false;
    this.applyError = '';
    this.applyLoading = true;
    this.api.applyToOrganizations(code, 'addOnly', true).subscribe({
      next: r => {
        this.applyLoading = false;
        const result = r.result;
        if (result && result.organizationCount > 0) { this.applyPreview = result; this.applyOpen = true; }
      },
      error: () => { this.applyLoading = false; }
    });
  }

  setApplyMode(mode: PlanApplyMode): void {
    if (this.applyMode === mode) return;
    this.applyMode = mode;
    this.fullConfirmed = false;
    this.applyError = '';
    this.applyLoading = true;
    this.api.applyToOrganizations(this.applyCode, mode, true).subscribe({
      next: r => { this.applyLoading = false; if (r.result && this.applyMode === mode) this.applyPreview = r.result; },
      error: err => { this.applyLoading = false; this.applyError = onboardingError(err, 'The preview could not be loaded.'); }
    });
  }

  get canApply(): boolean {
    return !!this.applyPreview && !this.applyLoading && !this.applying && (this.applyMode === 'addOnly' || this.fullConfirmed);
  }

  confirmApply(): void {
    if (!this.canApply) return;
    const mode = this.applyMode;
    this.applying = true;
    this.applyError = '';
    this.api.applyToOrganizations(this.applyCode, mode, false).subscribe({
      next: r => {
        this.applying = false;
        const res = r.result;
        this.messages?.add({
          severity: 'success', summary: 'Applied',
          detail: res ? `${res.organizationCount} organization(s): ${res.totalAdded} added, ${res.totalRemoved} removed, ${res.totalSkipped} skipped.` : 'Applied.'
        });
        this.skipApply();
      },
      error: err => { this.applying = false; this.applyError = onboardingError(err, 'The plan could not be applied.'); }
    });
  }

  skipApply(): void {
    this.applyOpen = false;
    this.applyPreview = null;
    this.fullConfirmed = false;
    this.applyError = '';
  }

  orgLabel(o: PlanApplyOrgResult): string { return o.orgName || o.orgCode || o.organizationId; }

  private select(plan: PlanWithFeatures): void {
    this.selected = plan;
    this.form = {
      name: plan.name, description: plan.description ?? '', monthlyPrice: plan.monthlyPrice, isActive: plan.isActive, sortOrder: plan.sortOrder
    };
    this.originalForm = JSON.stringify(this.form);
    this.original = new Map(plan.features.map(f => [f.featureCode, f.isIncluded]));
    const modules = plan.features.filter(f => !f.parentModuleCode).sort((a, b) => a.displayOrder - b.displayOrder);
    this.groups = modules.map(m => ({
      module: m,
      children: plan.features.filter(f => f.parentModuleCode === m.featureCode).sort((a, b) => a.displayOrder - b.displayOrder)
    }));
  }
}
