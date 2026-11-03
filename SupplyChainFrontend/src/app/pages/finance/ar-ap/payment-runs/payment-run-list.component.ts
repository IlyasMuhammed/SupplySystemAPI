import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router, RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';

import { AR_AP_PERMS, ArApService, MAX_PAGE_SIZE, PAYMENT_RUNS_ROUTE, PaymentRunSummary } from '../../../../services/ar-ap.service';
import { AuthService } from '../../../service/auth.service';
import { FLOW } from '../../../../shared/flow';
import { arApError, money, runTone } from '../ar-ap.shared';

/** A48 task 568 — `/finance/ap/payment-runs`: every payment run, newest first; a row opens the run's wizard at its step. */
@Component({
  selector: 'app-payment-run-list',
  standalone: true,
  imports: [CommonModule, RouterModule, ButtonModule, ...FLOW],
  styleUrls: ['../../gl/gl.scss', '../ar-ap.scss'],
  template: `
<sf-page [crumbs]="['Finance', 'Payables', 'Payment runs']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <a *ngIf="canPropose" class="p-button" [routerLink]="[route, 'new']" data-testid="new"><i class="pi pi-plus"></i>&nbsp;New payment run</a>
    <span class="sf-end"><p-button label="Refresh" icon="pi pi-refresh" [text]="true" (onClick)="load()"></p-button></span>
  </div>
  <div class="sf-body">
    <div class="sf-lr-head"><div><h1>Payment runs <span class="sf-count">{{ runs.length }}</span></h1>
      <div class="sf-sub">Batches of supplier invoices paid together: proposed, reviewed, approved, executed</div></div>
      <div class="sf-filters">
        <button type="button" class="sf-chip" [class.on]="!status" (click)="status = null">All</button>
        <button type="button" class="sf-chip" *ngFor="let s of statuses" [class.on]="status === s" (click)="status = s" [attr.data-testid]="'chip-' + s">{{ s }}</button>
      </div></div>
    <div class="gl-note er" *ngIf="loadError" data-testid="load-error"><i class="pi pi-exclamation-triangle"></i><div>{{ loadError }}</div></div>
    <section class="sf-card p0">
      <div class="sf-empty" *ngIf="loading"><i class="pi pi-spin pi-spinner"></i> Loading…</div>
      <div class="gl-scroll" *ngIf="!loading">
        <table class="aa-table" style="min-width:54rem" data-testid="runs">
          <thead><tr><th>Run</th><th>Created</th><th>Payment date</th><th>Status</th><th class="gl-num">Vendors</th><th class="gl-num">Items</th>
            <th class="gl-num">Discount</th><th class="gl-num">Net payment</th><th>Proposed by</th></tr></thead>
          <tbody>
            <tr *ngFor="let r of shown" class="aa-click" (click)="open(r)" [attr.data-testid]="'run-' + r.runNumber">
              <td class="sf-mono"><a [routerLink]="[route, r.uuid]" (click)="$event.stopPropagation()">{{ r.runNumber }}</a></td>
              <td>{{ r.runDate | date:'dd MMM yyyy' }}</td><td>{{ r.paymentDate | date:'dd MMM yyyy' }}</td>
              <td><span class="sf-pill" [ngClass]="tone(r.status)">{{ r.status }}</span></td>
              <td class="gl-num">{{ r.totalVendors }}</td><td class="gl-num">{{ r.totalItems }}</td>
              <td class="gl-num">{{ r.totalDiscount ? amount(r.totalDiscount) : '' }}</td><td class="gl-num"><b>{{ amount(r.netPaymentAmount) }}</b></td>
              <td>{{ r.proposedByName || '' }}</td>
            </tr>
            <tr *ngIf="!shown.length"><td colspan="9" class="sf-empty" data-testid="empty">No payment run{{ status ? ' with this status' : ' yet' }}.</td></tr>
          </tbody>
        </table>
      </div>
    </section>
  </div>
</sf-page>`
})
export class PaymentRunListComponent implements OnInit {
  private readonly api = inject(ArApService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly route = PAYMENT_RUNS_ROUTE;
  readonly statuses = ['Proposed', 'Approved', 'Completed', 'Cancelled'];

  runs: PaymentRunSummary[] = [];
  status: string | null = null;
  loading = false;
  loadError = '';

  get canPropose(): boolean { return this.auth.hasPermission(AR_AP_PERMS.PAYMENT_RUN_PROPOSE); }
  get shown(): PaymentRunSummary[] {
    if (!this.status) return this.runs;
    // Draft / Reviewing read as proposed, Executing as approved (D-9 states shown by their step).
    const alias: Record<string, string[]> = { Proposed: ['Draft', 'Proposed', 'Reviewing'], Approved: ['Approved', 'Executing'] };
    const set = alias[this.status] ?? [this.status];
    return this.runs.filter(r => set.includes(r.status));
  }

  ngOnInit(): void { this.load(); }

  load(): void {
    this.loading = true;
    this.loadError = '';
    this.api.getPaymentRuns(null, 1, MAX_PAGE_SIZE).subscribe({
      next: r => { this.loading = false; this.runs = [...r.data].sort((a, b) => (b.runDate ?? '').localeCompare(a.runDate ?? '') || b.runNumber.localeCompare(a.runNumber)); },
      error: err => { this.loading = false; this.runs = []; this.loadError = arApError(err, 'The payment runs could not be loaded.'); }
    });
  }

  open(r: PaymentRunSummary): void { this.router.navigate([PAYMENT_RUNS_ROUTE, r.uuid]); }

  tone(s: string | null | undefined): string { return runTone(s); }
  amount(n: number | null | undefined): string { return money(n); }
}
