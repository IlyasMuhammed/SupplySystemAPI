import { Component, Input, OnChanges, Output, EventEmitter, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';

import { HrmApi } from '../core/hrm.api';
import { HRM_ROUTE } from '../core/hrm.nav';
import { hrmError } from '../core/hrm.shared';

export interface Readiness {
  isReady: boolean; noTarget: boolean; internalGlActive: boolean; quickBooksActive: boolean; internalGlReady: boolean; quickBooksReady: boolean;
  missingGlRoles: string[]; missingQboAccounts: { role: string; accountCode?: string | null }[]; missingCostCenters: { code: string; name: string }[];
  currencyMismatch?: { orgCurrency: string; qboCurrency: string } | null; unverifiedStatutory: { kind: string; name: string }[];
}

/** Task 333 (G-3, contract §10.1) — what stands between this run and posting, with where to fix each thing. Reused by step 5 of the wizard. */
@Component({
  selector: 'hrm-readiness',
  standalone: true,
  imports: [CommonModule, RouterModule, ButtonModule],
  styleUrls: ['../../finance/gl/gl.scss', '../core/hrm.scss'],
  template: `
<section class="sf-card" data-testid="readiness">
  <div class="sf-ch"><h2>Posting readiness</h2>
    <span class="sf-ch-actions"><p-button label="Check again" icon="pi pi-refresh" [text]="true" size="small" [disabled]="loading" (onClick)="load()" data-testid="readiness-refresh"></p-button></span></div>
  <div class="sf-empty" *ngIf="loading"><i class="pi pi-spin pi-spinner"></i> Checking…</div>
  <div class="gl-note er" *ngIf="error" role="alert" data-testid="readiness-error"><i class="pi pi-exclamation-triangle"></i><div>{{ error }}</div></div>
  <ng-container *ngIf="r && !loading">
    <div class="gl-note ok" *ngIf="r.isReady && !r.noTarget" data-testid="ready"><i class="pi pi-check"></i><div>Ready to post.</div></div>
    <div class="gl-note" *ngIf="r.noTarget" data-testid="no-target"><i class="pi pi-info-circle"></i><div>No accounting system is active for this organization. Finalizing keeps the run as Finalized, nothing is posted, and the journal can be exported from here. You can still pay it.</div></div>

    <div class="hrm-check" *ngIf="r.internalGlActive"><i class="pi" [ngClass]="r.internalGlReady ? 'pi-check-circle' : 'pi-times-circle'"></i><div>General ledger accounts
      <ul *ngIf="r.missingGlRoles?.length" data-testid="missing-gl"><li *ngFor="let m of r.missingGlRoles">{{ m }} has no account. <a [routerLink]="defaultsRoute">Assign it</a></li></ul></div></div>
    <div class="hrm-check" *ngIf="r.quickBooksActive"><i class="pi" [ngClass]="r.quickBooksReady ? 'pi-check-circle' : 'pi-times-circle'"></i><div>QuickBooks
      <ul *ngIf="r.missingQboAccounts?.length" data-testid="missing-qbo"><li *ngFor="let m of r.missingQboAccounts">{{ m.role }}<ng-container *ngIf="m.accountCode"> ({{ m.accountCode }})</ng-container> is not mapped to a QuickBooks account. <a [routerLink]="qboRoute" [queryParams]="{ tab: 'accounts' }">Map it</a></li></ul>
      <ul *ngIf="r.missingCostCenters?.length" data-testid="missing-cc"><li *ngFor="let m of r.missingCostCenters">Cost center {{ m.code }} {{ m.name }} has no Class. <a [routerLink]="mapRoute">Map it</a></li></ul>
      <div *ngIf="r.currencyMismatch" class="gl-note er" data-testid="currency"><i class="pi pi-exclamation-triangle"></i><div>The organization works in {{ r.currencyMismatch.orgCurrency }} but QuickBooks is in {{ r.currencyMismatch.qboCurrency }}. A run cannot post across currencies.</div></div>
    </div></div>
    <div class="hrm-check" *ngIf="r.unverifiedStatutory?.length" data-testid="unverified"><i class="pi pi-times-circle"></i><div><b>Statutory data not reviewed.</b> These values were seeded and need a qualified payroll consultant before payroll can be finalized.
      <ul><li *ngFor="let u of r.unverifiedStatutory">{{ u.kind }}: {{ u.name }}. <a [routerLink]="statutoryRoute">Review</a></li></ul></div></div>
  </ng-container>
</section>`
})
export class ReadinessPanelComponent implements OnChanges {
  private readonly api = inject(HrmApi);

  @Input({ required: true }) runUuid = '';
  @Output() checked = new EventEmitter<Readiness | null>();

  readonly defaultsRoute = '/portal/pages/finance/gl/setup';
  readonly qboRoute = '/portal/pages/integrations/quickbooks';
  readonly mapRoute = `${HRM_ROUTE}/settings/cost-center-mapping`;
  readonly statutoryRoute = `${HRM_ROUTE}/salary/statutory-rates`;

  r: Readiness | null = null;
  loading = false;
  error = '';

  ngOnChanges(): void { if (this.runUuid) this.load(); }

  load(): void {
    this.loading = true;
    this.error = '';
    this.api.get<Readiness>(`hrm/payroll-runs/${this.runUuid}/posting-readiness`).subscribe({
      next: r => { this.loading = false; this.r = r; this.checked.emit(r); },
      error: err => { this.loading = false; this.r = null; this.error = hrmError(err, 'Readiness could not be checked.'); this.checked.emit(null); }
    });
  }
}
