import { Component, Input, OnChanges, SimpleChanges, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { TableModule } from 'primeng/table';

import { PosReturnsAdminService, STORE_CREDIT_ROUTE } from '../../../services/pos-returns-admin.service';
import { posError } from '../../../services/pos-admin.service';
import { StoreCreditRow, storeCreditRow } from '../store-credit-list/store-credit-list.component';

/**
 * A39 — the customer page's "Store credit" list (`GET api/pos/customers/{uuid}/credit-notes`, POS_CREDIT_NOTE_VIEW):
 * every store credit (CN-…) held by the customer with its balance; the active total on top. Bearer credit is not listed.
 */
@Component({
  selector: 'app-customer-store-credit',
  standalone: true,
  imports: [CommonModule, RouterModule, TableModule],
  template: `
<div *ngIf="loadError" class="sf-empty" data-testid="csc-load-error">{{ loadError }}</div>
<ng-container *ngIf="!loadError">
  <div class="csc-sum" data-testid="csc-total">
    <span>Spendable balance</span>
    <strong>{{ activeTotal | number:'1.2-2' }}</strong>
    <span class="sf-mu sf-sm">{{ activeCount }} active of {{ rows.length }}</span>
    <a class="sf-sm" [routerLink]="[creditRoute]" [queryParams]="{ customer: customerUuid }">open in Store credit</a>
  </div>
  <p-table [value]="rows" [loading]="loading" styleClass="p-datatable-sm" data-testid="csc-table">
    <ng-template pTemplate="header">
      <tr><th>Store credit</th><th>Issued</th><th class="text-right">Original</th><th class="text-right">Balance</th><th>Expires</th><th>Status</th></tr>
    </ng-template>
    <ng-template pTemplate="body" let-c>
      <tr data-testid="csc-row">
        <td><a class="sf-mono" [routerLink]="[creditRoute, c.uuid]">{{ c.number }}</a></td>
        <td>{{ c.issuedAt | date:'dd MMM yyyy' }}<div class="sf-mu sf-sm">{{ c.issuedAtStoreName }}</div></td>
        <td class="text-right">{{ c.originalAmount | number:'1.2-2' }}</td>
        <td class="text-right"><strong>{{ c.remainingBalance | number:'1.2-2' }}</strong></td>
        <td>{{ c.expiresAt ? (c.expiresAt | date:'dd MMM yyyy') : 'Never' }}</td>
        <td><span class="sf-pill" [ngClass]="c.statusTone">{{ c.statusLabel }}</span></td>
      </tr>
    </ng-template>
    <ng-template pTemplate="emptymessage"><tr><td colspan="6" class="sf-empty">No store credit.</td></tr></ng-template>
  </p-table>
</ng-container>
  `,
  styles: [`.csc-sum { display: flex; flex-wrap: wrap; gap: .75rem; align-items: baseline; margin-bottom: .75rem; }`]
})
export class CustomerStoreCreditComponent implements OnChanges {
  private readonly service = inject(PosReturnsAdminService);

  @Input() customerUuid = '';

  readonly creditRoute = STORE_CREDIT_ROUTE;
  rows: StoreCreditRow[] = [];
  loading = false;
  loadError: string | null = null;
  activeTotal = 0;
  activeCount = 0;

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['customerUuid'] && this.customerUuid) this.load();
  }

  load(): void {
    this.loading = true;
    this.loadError = null;
    this.service.getCustomerStoreCredits(this.customerUuid).subscribe({
      next: res => {
        this.loading = false;
        const now = Date.now();
        this.rows = (res.result ?? []).map(c => storeCreditRow(c, now));
        const active = this.rows.filter(r => r.status === 'ACTIVE');
        this.activeCount = active.length;
        this.activeTotal = Math.round(active.reduce((s, r) => s + (r.remainingBalance ?? 0), 0) * 100) / 100;
      },
      error: err => { this.loading = false; this.rows = []; this.loadError = posError(err, 'The store credit could not be loaded.'); }
    });
  }
}
