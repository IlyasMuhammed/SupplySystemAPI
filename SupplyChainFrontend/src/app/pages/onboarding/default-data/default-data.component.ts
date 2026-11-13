import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { DialogModule } from 'primeng/dialog';
import { TableModule } from 'primeng/table';
import { ToastModule } from 'primeng/toast';
import { CheckboxModule } from 'primeng/checkbox';
import { MessageService } from 'primeng/api';
import {
  DefaultDataCategory, DefaultDataOverview, OnboardingSetupService, SetupReferenceBlock, SetupResetMode
} from '../../../services/onboarding-setup.service';
import { FLOW } from '../../../shared/flow';
import { moduleName } from '../../../services/module.models';
import {
  CATEGORY_INFO, SETUP_WIZARD_ROUTE, categoryLabel, errorResult, onboardingError, resultLabel, resultTone, setupStatusLabel, setupStatusTone
} from '../onboarding.shared';

export const RESET_MODES: { mode: SetupResetMode; title: string; description: string }[] = [
  { mode: 'Merge', title: 'Merge — add what is missing',
    description: 'Adds back default records that are missing. Your own records and your changes to default ones are kept.' },
  { mode: 'Overwrite', title: 'Overwrite — restore the defaults',
    description: 'Puts every default record back to its default values. Records you added yourself are kept.' },
  { mode: 'Full', title: 'Full reset — start this category again',
    description: 'Deletes every record of this category and creates the defaults again. Refused while any record is used on a document.' }
];

/**
 * A50 P1-L (tasks 147–154) — Settings › Organization › Default Data: per category the seeded / current / modified
 * counts, when it was last applied and which version, a View link to the screen that maintains it, and a Reset dialog
 * (Merge / Overwrite / Full). A Full reset blocked by references shows "X records are referenced by documents"
 * (BR-50-008). "Run setup wizard again" opens the wizard (re-running only adds what is missing).
 */
@Component({
  selector: 'app-default-data',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule, ButtonModule, DialogModule, TableModule, ToastModule, CheckboxModule, ...FLOW],
  styleUrls: ['../onboarding.scss'],
  template: `
<p-toast></p-toast>
<sf-page [crumbs]="['Settings', 'Organization', 'Default data']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button label="Run setup wizard again" icon="pi pi-compass" (onClick)="runWizard()" data-testid="run-wizard"></p-button>
    <p-button label="Refresh" icon="pi pi-refresh" [text]="true" [loading]="loading" (onClick)="load()" data-testid="refresh"></p-button>
  </div>
  <div class="sf-body">
    <div class="sf-lr-head">
      <div><h1>Default data <span class="sf-count" *ngIf="data">{{ data.categories.length }}</span></h1>
        <div class="sf-sub">What the setup created for your organization, what you changed since, and how to put it back.</div></div>
    </div>

    <div class="sf-card sf-empty" *ngIf="loading && !data" data-testid="loading"><i class="pi pi-spin pi-spinner"></i> Loading…</div>
    <div class="ob-note er" *ngIf="error" data-testid="error"><i class="pi pi-exclamation-triangle"></i>
      <div class="grow">{{ error }}</div><p-button label="Retry" size="small" [text]="true" (onClick)="load()"></p-button></div>

    <ng-container *ngIf="data">
      <div class="sf-grid sf-tiles" data-testid="template-info">
        <div class="sf-tile"><span class="l">Country</span><span class="v" data-testid="t-country">{{ data.countryCode || '—' }}</span></div>
        <div class="sf-tile"><span class="l">Industry</span><span class="v" data-testid="t-industry">{{ data.industryType || '—' }}</span></div>
        <div class="sf-tile"><span class="l">Setup</span><span class="v"><span class="sf-pill" [ngClass]="statusTone(data.setupStatus)" data-testid="t-status">{{ statusLabel(data.setupStatus) }}</span></span>
          <span class="s" *ngIf="data.completedAt" data-testid="t-applied">Applied {{ data.completedAt | date:'d MMM y, HH:mm' }}</span></div>
      </div>

      <div class="ob-note" *ngIf="data.setupStatus === 'NotStarted' || data.setupStatus === 'InProgress'" data-testid="not-applied">
        <i class="pi pi-info-circle"></i><div>The setup has not been applied yet. <a (click)="runWizard()" href="javascript:void(0)">Open the setup wizard</a>.</div></div>

      <div class="sf-card p0 ob-scroll">
        <p-table [value]="data.categories" dataKey="category" [tableStyle]="{ 'min-width': '52rem' }">
          <ng-template pTemplate="header">
            <tr><th>Category</th><th class="ob-num">Seeded</th><th class="ob-num">Current</th><th>Modified</th><th>Last applied</th><th>Version</th><th style="width:12rem"></th></tr>
          </ng-template>
          <ng-template pTemplate="body" let-c>
            <tr [attr.data-testid]="'row-' + c.category">
              <td><div>{{ label(c.category) }}</div>
                <div class="ob-mu ob-sm" *ngIf="!c.moduleEnabled && c.requiresModule">{{ modName(c.requiresModule) }} is off</div>
                <div class="ob-mu ob-sm" *ngIf="!c.hasSeeder">Not available yet</div></td>
              <td class="ob-num" [attr.data-testid]="'seeded-' + c.category">{{ c.seededCount }}</td>
              <td class="ob-num" [attr.data-testid]="'current-' + c.category">{{ c.currentCount }}</td>
              <td [attr.data-testid]="'modified-' + c.category">
                <span class="sf-pill wn" *ngIf="c.isModified || c.modifiedCount" [title]="c.modifiedKeys.join(', ')">{{ c.modifiedCount }} changed</span>
                <span class="sf-pill er" *ngIf="c.missingCount" [title]="c.missingKeys.join(', ')">{{ c.missingCount }} missing</span>
                <span class="ob-mu" *ngIf="!c.isModified && !c.modifiedCount && !c.missingCount">—</span></td>
              <td><ng-container *ngIf="c.lastAppliedAt; else never">{{ c.lastAppliedAt | date:'d MMM y' }}
                <div class="ob-mu ob-sm">{{ c.lastAction }}</div></ng-container><ng-template #never><span class="ob-mu">Never</span></ng-template></td>
              <td [attr.data-testid]="'version-' + c.category">{{ c.appliedVersion ? 'v' + c.appliedVersion : '—' }}
                <span class="sf-pill in" *ngIf="c.updateAvailable" [title]="'Version ' + c.latestVersion + ' is available'">v{{ c.latestVersion }} available</span></td>
              <td>
                <a *ngIf="viewRoute(c.category) as r" [routerLink]="r" class="p-button p-button-text p-button-sm" [attr.data-testid]="'view-' + c.category">View</a>
                <p-button label="Reset" icon="pi pi-refresh" [text]="true" size="small" [disabled]="!c.hasSeeder || !c.moduleEnabled"
                          (onClick)="openReset(c)" [attr.data-testid]="'reset-' + c.category"></p-button>
              </td>
            </tr>
          </ng-template>
          <ng-template pTemplate="emptymessage"><tr><td colspan="7" class="sf-empty" data-testid="empty">No default data categories.</td></tr></ng-template>
        </p-table>
      </div>
    </ng-container>
  </div>
</sf-page>

<p-dialog [(visible)]="resetOpen" [modal]="true" [style]="{ width: '34rem', maxWidth: '95vw' }" [header]="'Reset ' + label(resetCat?.category)"
          styleClass="sdlg" (onHide)="closeReset()">
  <div data-testid="reset-dialog">
    <div class="ob-radio" *ngFor="let m of modes" [class.on]="resetMode === m.mode" (click)="resetMode = m.mode; block = null"
         [attr.data-testid]="'mode-' + m.mode" role="radio" [attr.aria-checked]="resetMode === m.mode" tabindex="0"
         (keydown.enter)="resetMode = m.mode; block = null">
      <i class="pi" [ngClass]="resetMode === m.mode ? 'pi-check-circle' : 'pi-circle'"></i>
      <div><div class="t">{{ m.title }}</div><div class="d">{{ m.description }}</div></div>
    </div>
    <div class="ob-row ob-sm" *ngIf="resetCat?.updateAvailable" style="margin-bottom:.5rem">
      <p-checkbox [binary]="true" [(ngModel)]="useLatest" inputId="ob-latest" data-testid="use-latest"></p-checkbox>
      <label for="ob-latest">Use the newest defaults (v{{ resetCat?.latestVersion }}) instead of the version applied (v{{ resetCat?.appliedVersion }})</label>
    </div>
    <div class="ob-note er" *ngIf="block" data-testid="reset-blocked"><i class="pi pi-lock"></i>
      <div><strong>{{ block.referencedCount }} records are referenced by documents</strong>, so a full reset is not possible.
        Use Merge or Overwrite instead.<div class="ob-mu ob-sm" *ngIf="block.referencedKeys.length">{{ block.referencedKeys.join(', ') }}</div></div></div>
    <div class="ob-note er" *ngIf="resetError" data-testid="reset-error"><i class="pi pi-times-circle"></i><div>{{ resetError }}</div></div>
    <div class="ob-note ok" *ngIf="resetDone" data-testid="reset-done"><i class="pi pi-check-circle"></i>
      <div>{{ rLabel(resetDone.status) }}: {{ resetDone.created }} created<ng-container *ngIf="resetDone.removed">, {{ resetDone.removed }} removed</ng-container>.
        <div *ngFor="let w of resetDone.warnings" class="ob-sm">{{ w }}</div></div></div>
  </div>
  <ng-template pTemplate="footer">
    <p-button label="Close" [text]="true" (onClick)="closeReset()"></p-button>
    <p-button [label]="resetMode === 'Full' ? 'Delete and recreate' : 'Reset'" icon="pi pi-refresh" [severity]="resetMode === 'Full' ? 'danger' : 'primary'"
              [loading]="resetting" [disabled]="!!block && resetMode === 'Full'" (onClick)="doReset()" data-testid="reset-go"></p-button>
  </ng-template>
</p-dialog>`
})
export class DefaultDataComponent implements OnInit {
  private readonly api = inject(OnboardingSetupService);
  private readonly router = inject(Router);
  private readonly messages = inject(MessageService, { optional: true });

  readonly modes = RESET_MODES;
  readonly label = categoryLabel;
  readonly statusLabel = setupStatusLabel;
  readonly statusTone = setupStatusTone;
  readonly rLabel = resultLabel;
  readonly rTone = resultTone;
  readonly modName = moduleName;

  data: DefaultDataOverview | null = null;
  loading = false;
  error = '';

  resetOpen = false;
  resetCat: DefaultDataCategory | null = null;
  resetMode: SetupResetMode = 'Merge';
  useLatest = false;
  resetting = false;
  resetError = '';
  block: SetupReferenceBlock | null = null;
  resetDone: { status: string; created: number; removed: number; warnings: string[] } | null = null;

  ngOnInit(): void { this.load(); }

  load(): void {
    this.loading = true;
    this.error = '';
    this.api.categories().subscribe({
      next: r => { this.loading = false; this.data = r?.result ?? null; if (!this.data) this.error = 'No setup information was returned.'; },
      error: err => { this.loading = false; this.error = onboardingError(err, 'The default data could not be loaded.'); }
    });
  }

  viewRoute(category: string): string | null { return CATEGORY_INFO[category]?.viewRoute ?? null; }

  runWizard(): void { this.router.navigate([SETUP_WIZARD_ROUTE]); }

  openReset(c: DefaultDataCategory): void {
    this.resetCat = c;
    this.resetMode = 'Merge';
    this.useLatest = false;
    this.block = null;
    this.resetError = '';
    this.resetDone = null;
    this.resetOpen = true;
  }

  closeReset(): void {
    const changed = !!this.resetDone;
    this.resetOpen = false;
    this.resetCat = null;
    if (changed) this.load();
  }

  doReset(): void {
    if (!this.resetCat || this.resetting) return;
    this.resetting = true;
    this.resetError = '';
    this.block = null;
    this.resetDone = null;
    this.api.reset(this.resetCat.category, this.resetMode, this.useLatest ? 'latest' : 'applied').subscribe({
      next: r => {
        this.resetting = false;
        const res = r?.result;
        this.resetDone = { status: res?.status ?? 'Applied', created: res?.created ?? 0, removed: res?.removed ?? 0, warnings: res?.warnings ?? [] };
        if (res && !res.ok) this.resetError = res.message || 'The reset did not complete.';
        else this.messages?.add({ severity: 'success', summary: 'Reset done', detail: `${this.label(this.resetCat?.category)} was reset (${this.resetMode}).`, life: 4000 });
      },
      error: err => {
        this.resetting = false;
        const block = err?.status === 409 ? errorResult<SetupReferenceBlock>(err) : null;
        if (block && typeof block.referencedCount === 'number') {
          this.block = { referencedCount: block.referencedCount, referencedKeys: block.referencedKeys ?? [] };
        } else {
          this.resetError = onboardingError(err, 'The category could not be reset.');
        }
      }
    });
  }
}
