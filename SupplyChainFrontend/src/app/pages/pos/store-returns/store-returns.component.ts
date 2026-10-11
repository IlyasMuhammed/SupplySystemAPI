import { Component, Input, OnChanges, SimpleChanges, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { RadioButtonModule } from 'primeng/radiobutton';
import { ConfirmationService, MessageService } from 'primeng/api';

import {
  EffectivePolicy, PosReturnsAdminService, ReturnPolicyUpsert, cleanPolicy, policyProblem, policySummary, policyUpsertFrom
} from '../../../services/pos-returns-admin.service';
import { posError } from '../../../services/pos-admin.service';
import { ReturnPolicyFormComponent } from '../return-policy-form/return-policy-form.component';

export type StorePolicyMode = 'ORG' | 'STORE';

export const POLICY_CONFLICT = 'Someone else changed this return policy. Reload and try again.';

/**
 * A39 — the store page's Returns tab (API-CONTRACT §1): "Use organization default" (no store policy; DELETE removes one)
 * vs "Use store-specific policy" (PUT, rowVersion when editing an existing one). Read: POS_STORE_VIEW; write:
 * POS_RETURN_POLICY_MANAGE. Server messages are shown verbatim.
 */
@Component({
  selector: 'app-pos-store-returns',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule, ButtonModule, RadioButtonModule, ReturnPolicyFormComponent],
  template: `
<div *ngIf="loading" class="sf-empty"><i class="pi pi-spin pi-spinner"></i> Loading the return policy…</div>
<div *ngIf="!loading">
  <div class="sf-pill er sr-err" *ngIf="loadError" role="alert" data-testid="returns-load-error">{{ loadError }}</div>
  <div class="sr-modes" role="radiogroup" aria-label="Return policy source">
    <div class="sr-mode">
      <p-radiobutton inputId="srOrg" name="srMode" value="ORG" [(ngModel)]="mode" [disabled]="!canManage || busy"
                     (onClick)="onModeChange()" data-testid="mode-org"></p-radiobutton>
      <label for="srOrg">Use organization default</label>
    </div>
    <div class="sr-mode">
      <p-radiobutton inputId="srStore" name="srMode" value="STORE" [(ngModel)]="mode" [disabled]="!canManage || busy"
                     (onClick)="onModeChange()" data-testid="mode-store"></p-radiobutton>
      <label for="srStore">Use store-specific policy</label>
    </div>
    <span class="sf-mu sf-sm" *ngIf="!canManage">read only</span>
  </div>

  <!-- Organization default (read only here) -->
  <ng-container *ngIf="mode === 'ORG'">
    <p class="sf-mu sf-sm" data-testid="org-summary">
      <ng-container *ngIf="orgPolicy; else noOrg">This store follows the organization default: {{ orgSummary }}.</ng-container>
      <ng-template #noOrg>This store follows the organization default.</ng-template>
      <a *ngIf="canManage" [routerLink]="['/portal/pages/pos/return-policy']"> Edit the default</a>
    </p>
    <app-pos-return-policy-form *ngIf="orgModel" id="srOrgPol" [model]="orgModel" [disabled]="true"></app-pos-return-policy-form>
    <div class="sf-fbar sr-bar" *ngIf="canManage && hasOwn">
      <span class="sf-mu sf-sm">The store's own policy is removed; the organization default applies from the next return.</span>
      <span class="sf-end">
        <p-button label="Keep store policy" [text]="true" severity="secondary" (onClick)="mode = 'STORE'"></p-button>
        <p-button label="Use organization default" icon="pi pi-check" [loading]="busy" (onClick)="useDefault()" data-testid="use-default"></p-button>
      </span>
    </div>
  </ng-container>

  <!-- Store-specific -->
  <ng-container *ngIf="mode === 'STORE'">
    <p class="sf-mu sf-sm" *ngIf="!hasOwn" data-testid="store-new-hint">Starts from the organization default's values; nothing changes until you save.</p>
    <div class="sf-pill er sr-err" *ngIf="error" role="alert" data-testid="policy-error">{{ error }}</div>
    <app-pos-return-policy-form id="srPol" [model]="model" [disabled]="!canManage"></app-pos-return-policy-form>
    <div class="sf-fbar sr-bar" *ngIf="canManage">
      <span class="sf-mu sf-sm">Terminals pick the policy up on their next sync.</span>
      <span class="sf-end">
        <p-button label="Undo changes" [text]="true" severity="secondary" (onClick)="reset()"></p-button>
        <p-button label="Save store policy" icon="pi pi-check" [loading]="busy" (onClick)="save()" data-testid="save-store-policy"></p-button>
      </span>
    </div>
  </ng-container>
</div>
  `,
  styles: [`
    .sr-err { display: block; margin-bottom: .75rem; white-space: pre-line; }
    .sr-modes { display: flex; flex-wrap: wrap; gap: 1.25rem; align-items: center; margin-bottom: .75rem; }
    .sr-mode { display: flex; align-items: center; gap: .4rem; }
    .sr-bar { margin-top: 1rem; }
  `]
})
export class PosStoreReturnsComponent implements OnChanges {
  private readonly service = inject(PosReturnsAdminService);
  private readonly confirm = inject(ConfirmationService, { optional: true });
  private readonly messages = inject(MessageService, { optional: true });

  @Input() storeUuid = '';
  @Input() storeName = '';
  @Input() canManage = false;

  loading = false;
  loadError: string | null = null;
  effective: EffectivePolicy | null = null;
  /** The store has its own policy (source STORE). */
  hasOwn = false;
  mode: StorePolicyMode = 'ORG';
  /** The org default's values when this store follows it (read only). */
  orgPolicy: EffectivePolicy | null = null;
  orgModel: ReturnPolicyUpsert | null = null;
  orgSummary = '';
  /** The store-specific form. */
  model: ReturnPolicyUpsert | null = null;
  busy = false;
  error: string | null = null;

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['storeUuid'] && this.storeUuid) this.load();
  }

  load(): void {
    this.loading = true;
    this.loadError = null;
    this.error = null;
    this.service.getStorePolicy(this.storeUuid).subscribe({
      next: res => { this.loading = false; this.setEffective(res.result ?? null); },
      error: err => {
        this.loading = false;
        // e.g. "No return policy configured. Contact your administrator." — verbatim.
        this.loadError = posError(err, 'The return policy could not be loaded.');
        this.setEffective(null);
      }
    });
  }

  private setEffective(p: EffectivePolicy | null): void {
    this.effective = p;
    this.hasOwn = p?.source === 'STORE';
    this.mode = this.hasOwn ? 'STORE' : 'ORG';
    this.orgPolicy = p && p.source === 'ORG_DEFAULT' ? p : null;
    this.orgModel = this.orgPolicy ? policyUpsertFrom(this.orgPolicy, false) : null;
    this.orgSummary = policySummary(this.orgPolicy);
    this.reset();
  }

  /** The store form: its own policy (with rowVersion), else a copy of the org default without rowVersion. */
  reset(): void {
    this.error = null;
    if (this.hasOwn) { this.model = policyUpsertFrom(this.effective); return; }
    const base = policyUpsertFrom(this.orgPolicy, false);
    this.model = { ...base, name: this.storeName ? `${this.storeName} return policy` : base.name };
  }

  onModeChange(): void { this.error = null; }

  save(): void {
    if (!this.model || this.busy || !this.canManage) return;
    const problem = policyProblem(this.model);
    if (problem) { this.error = problem; return; }
    this.busy = true;
    this.error = null;
    const req = cleanPolicy(this.model);
    if (!this.hasOwn) delete req.rowVersion;
    this.service.saveStorePolicy(this.storeUuid, req).subscribe({
      next: res => {
        this.busy = false;
        if (res && res.success === false) { this.error = res.message || 'The policy was not saved.'; return; }
        this.messages?.add({ severity: 'success', summary: 'Saved', detail: 'Store return policy saved.' });
        this.load();
      },
      error: err => {
        this.busy = false;
        this.error = err?.status === 409 ? (err?.error?.message || POLICY_CONFLICT) : posError(err, 'The policy was not saved.');
      }
    });
  }

  useDefault(): void {
    if (!this.hasOwn || this.busy || !this.canManage) return;
    const go = () => this.applyDefault();
    if (!this.confirm) { go(); return; }
    this.confirm.confirm({
      header: 'Use the organization default?',
      message: 'This store\'s own return policy is deleted. Returns here then follow the organization default.',
      icon: 'pi pi-exclamation-triangle',
      acceptButtonStyleClass: 'p-button-danger',
      rejectButtonStyleClass: 'p-button-text',
      accept: go
    });
  }

  applyDefault(): void {
    this.busy = true;
    this.error = null;
    this.service.deleteStorePolicy(this.storeUuid).subscribe({
      next: res => {
        this.busy = false;
        if (res && res.success === false) { this.error = res.message || 'Not changed.'; this.mode = 'STORE'; return; }
        this.messages?.add({ severity: 'success', summary: 'Done', detail: 'The store now follows the organization default.' });
        this.load();
      },
      error: err => { this.busy = false; this.mode = 'STORE'; this.error = posError(err, 'Not changed.'); }
    });
  }
}
