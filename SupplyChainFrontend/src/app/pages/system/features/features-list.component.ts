import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TableModule } from 'primeng/table';
import { SelectModule } from 'primeng/select';
import { ToggleSwitchModule } from 'primeng/toggleswitch';
import { InputTextModule } from 'primeng/inputtext';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';
import { FEATURE_STATUSES, FeatureDefinition, FeatureStatus, FeaturesAdminService } from '../../../services/features-admin.service';
import { FLOW } from '../../../shared/flow';
import { onboardingError } from '../../onboarding/onboarding.shared';

export const STATUS_LABELS: Record<FeatureStatus, string> = {
  Live: 'Live', Beta: 'Beta', ComingSoon: 'Coming soon', Deprecated: 'Deprecated'
};

/**
 * A51 P2 - System Administration > Features (api/system/features, platform super admin). Every feature of the catalog with
 * its platform status and visibility. Each change is saved at once; if the server refuses it the control goes back to what it was.
 * Organizations that already hold the licence keep access whatever is set here (grandfathered).
 */
@Component({
  selector: 'app-features-list',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, TableModule, SelectModule, ToggleSwitchModule, InputTextModule, ToastModule, ...FLOW],
  styleUrls: ['../../onboarding/onboarding.scss'],
  template: `
<p-toast></p-toast>
<sf-page [crumbs]="['System Administration', 'Features']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <span class="sf-end"><p-button label="Refresh" icon="pi pi-refresh" [text]="true" [loading]="loading" (onClick)="load()"></p-button></span>
  </div>
  <div class="sf-body">
    <div class="sf-lr-head">
      <div><h1>Features <span class="sf-count">{{ filtered.length }}</span></h1>
        <div class="sf-sub">Whether each feature is live, in beta, coming soon or deprecated, and whether new organizations see it. Changes save at once.
          Organizations that already hold the licence keep access.</div></div>
    </div>
    <div class="ob-note er" *ngIf="error" data-testid="error"><i class="pi pi-exclamation-triangle"></i>
      <div class="grow">{{ error }}</div><p-button label="Retry" size="small" [text]="true" (onClick)="load()"></p-button></div>
    <div class="ob-row" style="margin-bottom:.75rem">
      <input pInputText type="search" [(ngModel)]="search" placeholder="Search code or name" aria-label="Search features" data-testid="search" />
    </div>
    <div class="sf-card p0 ob-scroll">
      <p-table [value]="filtered" [loading]="loading" dataKey="featureCode" [tableStyle]="{ 'min-width': '52rem' }">
        <ng-template pTemplate="header">
          <tr><th>Code</th><th>Name</th><th>Category</th><th style="width:12rem">Status</th><th style="width:7rem">Visible</th></tr>
        </ng-template>
        <ng-template pTemplate="body" let-r>
          <tr [attr.data-testid]="'row-' + r.featureCode">
            <td class="sf-mono">{{ r.featureCode }}</td>
            <td>{{ r.featureName }}<span class="sf-pill" style="margin-left:.4rem" *ngIf="!r.isAvailable">Not built</span></td>
            <td>{{ r.category }}</td>
            <td>
              <p-select [options]="statusOptions" optionLabel="label" optionValue="value" [ngModel]="r.status" appendTo="body"
                (onChange)="changeStatus(r, $event.value)" [disabled]="busy.has(r.featureCode)" [attr.data-testid]="'status-' + r.featureCode"
                [ariaLabel]="'Status of ' + r.featureName" [style]="{ width: '100%' }"></p-select>
            </td>
            <td>
              <p-toggleswitch [ngModel]="r.isVisible" (onChange)="changeVisible(r, $event.checked)" [disabled]="busy.has(r.featureCode)"
                [attr.data-testid]="'visible-' + r.featureCode" [ariaLabel]="'Visible: ' + r.featureName"></p-toggleswitch>
            </td>
          </tr>
        </ng-template>
        <ng-template pTemplate="emptymessage"><tr><td colspan="5" class="sf-empty" data-testid="empty">No features.</td></tr></ng-template>
      </p-table>
    </div>
  </div>
</sf-page>`
})
export class FeaturesListComponent implements OnInit {
  private readonly api = inject(FeaturesAdminService);
  private readonly messages = inject(MessageService, { optional: true });

  readonly statusOptions = FEATURE_STATUSES.map(s => ({ value: s, label: STATUS_LABELS[s] }));

  rows: FeatureDefinition[] = [];
  search = '';
  loading = false;
  error = '';
  /** Feature codes with a save in flight (their controls are disabled meanwhile). */
  readonly busy = new Set<string>();

  get filtered(): FeatureDefinition[] {
    const q = this.search.trim().toLowerCase();
    return q ? this.rows.filter(r => r.featureCode.toLowerCase().includes(q) || r.featureName.toLowerCase().includes(q)) : this.rows;
  }

  ngOnInit(): void { this.load(); }

  load(): void {
    this.loading = true;
    this.error = '';
    this.api.listFeatures().subscribe({
      next: r => { this.loading = false; this.rows = r.result ?? []; },
      error: err => { this.loading = false; this.error = onboardingError(err, 'The features could not be loaded.'); }
    });
  }

  /** Saves at once; the row shows the new value straight away and goes back to the old one if the save fails. */
  changeStatus(row: FeatureDefinition, status: FeatureStatus): void {
    const previous = row.status;
    if (status === previous) return;
    row.status = status;
    this.busy.add(row.featureCode);
    this.api.setStatus(row.featureCode, status).subscribe({
      next: r => { this.busy.delete(row.featureCode); if (r.result) row.status = r.result.status; },
      error: err => { this.busy.delete(row.featureCode); row.status = previous; this.fail(err, 'The status could not be saved.'); }
    });
  }

  changeVisible(row: FeatureDefinition, isVisible: boolean): void {
    const previous = row.isVisible;
    if (isVisible === previous) return;
    row.isVisible = isVisible;
    this.busy.add(row.featureCode);
    this.api.setVisibility(row.featureCode, isVisible).subscribe({
      next: r => { this.busy.delete(row.featureCode); if (r.result) row.isVisible = r.result.isVisible; },
      error: err => { this.busy.delete(row.featureCode); row.isVisible = previous; this.fail(err, 'The visibility could not be saved.'); }
    });
  }

  private fail(err: unknown, fallback: string): void {
    this.messages?.add({ severity: 'error', summary: 'Not saved', detail: onboardingError(err, fallback) });
  }
}
