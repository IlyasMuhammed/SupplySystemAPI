import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { TableModule } from 'primeng/table';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';

import {
  PosReturnsAdminService, ReturnPolicy, ReturnPolicyUpsert, cleanPolicy, policyProblem, policySummary, policyUpsertFrom
} from '../../../services/pos-returns-admin.service';
import { POS_ROUTE, posError } from '../../../services/pos-admin.service';
import { FLOW } from '../../../shared/flow';
import { ReturnPolicyFormComponent } from '../return-policy-form/return-policy-form.component';
import { POLICY_CONFLICT } from '../store-returns/store-returns.component';

/** A store override row (built in load()). */
export interface StorePolicyRow { storeUuid: string; storeName: string; name: string; summary: string; isActive: boolean; }

/**
 * A39 — `/portal/pages/pos/return-policy`: the organization default return policy (`PUT return-policies/default`, rowVersion,
 * 409 → reload) and the stores that override it (POS_RETURN_POLICY_MANAGE).
 */
@Component({
  selector: 'app-pos-return-policy-page',
  standalone: true,
  imports: [CommonModule, RouterModule, ButtonModule, TableModule, ToastModule, ReturnPolicyFormComponent, ...FLOW],
  template: `
<p-toast></p-toast>
<sf-page [crumbs]="['Point of Sale', 'Return policy']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button label="Save default policy" icon="pi pi-check" [loading]="saving" [disabled]="!model || loading" (onClick)="save()"
              data-testid="save-default"></p-button>
    <p-button label="Reload" icon="pi pi-refresh" [text]="true" (onClick)="load()" data-testid="reload"></p-button>
    <a class="p-button p-button-text" [routerLink]="[posRoute, 'return-reasons']"><i class="pi pi-list"></i>&nbsp;Return reasons</a>
  </div>
  <header class="sf-hdr sf-compact">
    <div class="sf-t"><div>
      <div class="sf-kicker">Point of Sale · returns and exchanges</div>
      <h1>Organization return policy</h1>
      <div class="sf-meta">Applies to every store that has no policy of its own. {{ summary }}</div>
    </div></div>
  </header>
  <div class="sf-body">
    <div class="sf-card sf-empty" *ngIf="loading"><i class="pi pi-spin pi-spinner"></i> Loading…</div>
    <div class="sf-pill er rpp-err" *ngIf="loadError" role="alert" data-testid="load-error">{{ loadError }}</div>
    <section class="sf-card" *ngIf="!loading && model" data-testid="section-default">
      <div class="sf-ch"><h2>Default policy</h2>
        <span class="sf-mu sf-sm" *ngIf="defaultPolicy?.modifiedAt">last changed {{ defaultPolicy!.modifiedAt | date:'dd MMM yyyy, HH:mm' }}</span></div>
      <div class="sf-pill er rpp-err" *ngIf="error" role="alert" data-testid="policy-error">{{ error }}</div>
      <app-pos-return-policy-form id="orgPol" [model]="model"></app-pos-return-policy-form>
    </section>

    <section class="sf-card p0" *ngIf="!loading" data-testid="section-overrides">
      <div class="sf-ch"><h2>Stores with their own policy</h2><span class="sf-count">{{ overrides.length }}</span></div>
      <p-table [value]="overrides" data-testid="override-table">
        <ng-template pTemplate="header"><tr><th>Store</th><th>Policy</th><th>Rules</th><th>Status</th></tr></ng-template>
        <ng-template pTemplate="body" let-r>
          <tr>
            <td><a [routerLink]="[posRoute, 'stores', r.storeUuid]">{{ r.storeName }}</a></td>
            <td>{{ r.name }}</td>
            <td class="sf-sm">{{ r.summary }}</td>
            <td><span class="sf-pill" [ngClass]="r.isActive ? 'ok' : ''">{{ r.isActive ? 'Active' : 'Inactive' }}</span></td>
          </tr>
        </ng-template>
        <ng-template pTemplate="emptymessage">
          <tr><td colspan="4" class="sf-empty">Every store follows the default. A store's own policy is set on its Returns tab.</td></tr>
        </ng-template>
      </p-table>
    </section>
  </div>
</sf-page>
  `,
  styles: [`.rpp-err { display: block; margin-bottom: .75rem; white-space: pre-line; }`],
  providers: [MessageService]
})
export class PosReturnPolicyPageComponent implements OnInit {
  private readonly service = inject(PosReturnsAdminService);
  private readonly messages = inject(MessageService);

  readonly posRoute = POS_ROUTE;

  loading = true;
  loadError: string | null = null;
  defaultPolicy: ReturnPolicy | null = null;
  model: ReturnPolicyUpsert | null = null;
  overrides: StorePolicyRow[] = [];
  summary = '';
  saving = false;
  error: string | null = null;

  ngOnInit(): void { this.load(); }

  load(): void {
    this.loading = true;
    this.loadError = null;
    this.error = null;
    this.service.getPolicies().subscribe({
      next: res => {
        this.loading = false;
        const all = res.result ?? [];
        this.defaultPolicy = all.find(p => !p.storeUuid) ?? null;
        // No default yet (the server seeds one lazily): start from the recommended values; the PUT creates it.
        this.model = policyUpsertFrom(this.defaultPolicy);
        if (!this.defaultPolicy) this.model.name = 'Organization return policy';
        this.summary = this.defaultPolicy ? policySummary(this.defaultPolicy) + '.' : '';
        this.overrides = all.filter(p => !!p.storeUuid).map(p => ({
          storeUuid: p.storeUuid!, storeName: p.storeName || p.storeUuid!, name: p.name, summary: policySummary(p), isActive: p.isActive
        }));
      },
      error: err => {
        this.loading = false;
        this.loadError = posError(err, 'The return policies could not be loaded.');
        this.model = null;
        this.overrides = [];
      }
    });
  }

  save(): void {
    if (!this.model || this.saving) return;
    const problem = policyProblem(this.model);
    if (problem) { this.error = problem; return; }
    this.saving = true;
    this.error = null;
    this.service.saveDefaultPolicy(cleanPolicy(this.model)).subscribe({
      next: res => {
        this.saving = false;
        if (res && res.success === false) { this.error = res.message || 'The policy was not saved.'; return; }
        this.messages.add({ severity: 'success', summary: 'Saved', detail: 'Organization return policy saved.' });
        if (res.result) {
          this.defaultPolicy = res.result;
          this.model = policyUpsertFrom(res.result);
          this.summary = policySummary(res.result) + '.';
        } else this.load();
      },
      error: err => {
        this.saving = false;
        this.error = err?.status === 409 ? (err?.error?.message || POLICY_CONFLICT) : posError(err, 'The policy was not saved.');
      }
    });
  }
}
