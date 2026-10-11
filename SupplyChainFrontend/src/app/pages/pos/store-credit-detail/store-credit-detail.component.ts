import { Component, DestroyRef, OnInit, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { DialogModule } from 'primeng/dialog';
import { TableModule } from 'primeng/table';
import { TextareaModule } from 'primeng/textarea';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';

import {
  PosReturnsAdminService, STORE_CREDIT_ROUTE, StoreCredit, StoreCreditDetail, StoreCreditTxn
} from '../../../services/pos-returns-admin.service';
import { POS_ROUTE, posError } from '../../../services/pos-admin.service';
import { AuthService } from '../../service/auth.service';
import { ModuleService } from '../../../services/module.service';
import { FLOW, FlowSection, flowLabel } from '../../../shared/flow';
import { storeCreditStatusLabel, storeCreditStatusTone } from '../store-credit-list/store-credit-list.component';
import { StoreCreditIssueDialogComponent } from '../store-credit-issue-dialog/store-credit-issue-dialog.component';

/** A transaction row (built in setCredit(), not in a getter). */
export interface StoreCreditTxnRow extends StoreCreditTxn { typeLabel: string; tone: string; }

const TXN_TONES: Record<string, string> = { ISSUE: 'ok', REDEEM: 'in', EXPIRE: 'er', CANCEL: 'er', ADJUST: 'wn' };
/** POSR: ADJUST = the amount put back when a sale paid by this store credit is voided. */
const TXN_LABELS: Record<string, string> = { REDEEM: 'Spent', ADJUST: 'Restored (sale voided)' };

export function txnRows(txns: readonly StoreCreditTxn[]): StoreCreditTxnRow[] {
  return [...txns]
    .sort((a, b) => (a.performedAt ?? '').localeCompare(b.performedAt ?? ''))
    .map(t => ({ ...t, typeLabel: TXN_LABELS[t.type] ?? flowLabel(t.type), tone: TXN_TONES[t.type] ?? '' }));
}

/**
 * A39 — `/portal/pages/pos/credit-notes/:uuid`: one store credit (CN-…) — balance, its transactions (issue / spent /
 * expire / cancel) with links to the return that issued it and the orders that spent it; Cancel (POS_CREDIT_NOTE_CANCEL,
 * ACTIVE only, optional note) and Issue store credit (POS_CREDIT_NOTE_ISSUE + FEATURE_POS_CREDIT_NOTES).
 */
@Component({
  selector: 'app-pos-store-credit-detail',
  standalone: true,
  imports: [
    CommonModule, FormsModule, RouterModule, ButtonModule, DialogModule, TableModule, TextareaModule, ToastModule,
    StoreCreditIssueDialogComponent, ...FLOW
  ],
  template: `
<p-toast></p-toast>
<sf-page [crumbs]="['Point of Sale', 'Store credit', credit?.number || '…']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button *ngIf="canIssue" label="Issue store credit" icon="pi pi-plus" [text]="true" (onClick)="issueOpen = true" data-testid="issue-credit"></p-button>
    <p-button label="Store credit" icon="pi pi-arrow-left" [text]="true" (onClick)="back()"></p-button>
    <ng-container *ngIf="showCancel">
      <span class="sf-sep"></span>
      <p-button label="Cancel store credit" icon="pi pi-ban" severity="danger" [text]="true" (onClick)="openCancel()" data-testid="cancel-credit"></p-button>
    </ng-container>
  </div>

  <div class="sf-body" *ngIf="isLoading || loadError">
    <div class="sf-card sf-empty" *ngIf="isLoading"><i class="pi pi-spin pi-spinner"></i> Loading store credit…</div>
    <div class="sf-card sf-empty" *ngIf="loadError" data-testid="credit-load-error">{{ loadError }}</div>
  </div>

  <ng-container *ngIf="!isLoading && credit as c">
    <header class="sf-hdr">
      <div class="sf-t"><div>
        <div class="sf-kicker">Store credit · issued at {{ c.issuedAtStoreName }}
          <ng-container *ngIf="c.returnOrderUuid"> · for return <a class="sf-mono" [routerLink]="[posRoute, 'orders', c.returnOrderUuid]"
            data-testid="return-link">{{ c.returnOrderNumber }}</a></ng-container>
        </div>
        <h1 data-testid="credit-title">
          <span class="sf-mono">{{ c.number }}</span>
          <span class="sf-pill" [ngClass]="statusTone" data-testid="credit-status">{{ statusLabel }}</span>
        </h1>
        <div class="sf-meta">{{ c.customerName || 'Bearer — anyone holding the number' }} · issued {{ c.issuedAt | date:'dd MMM yyyy, HH:mm' }} by {{ c.issuedByName }}</div>
      </div></div>
      <div class="sf-facts">
        <div class="sf-kpi" data-testid="kpi-balance"><span class="l">Balance</span><span class="v sm">{{ c.remainingBalance | number:'1.2-2' }}</span>
          <span class="s">{{ c.currencyCode }}</span></div>
        <div class="sf-kpi"><span class="l">Issued</span><span class="v sm">{{ c.originalAmount | number:'1.2-2' }}</span>
          <span class="s">{{ c.redeemedAmount | number:'1.2-2' }} spent</span></div>
        <div class="sf-kpi" data-testid="kpi-expiry"><span class="l">Expires</span>
          <span class="v sm">{{ c.expiresAt ? (c.expiresAt | date:'dd MMM yyyy') : 'Never' }}</span></div>
      </div>
    </header>

    <sf-anchors [sections]="sections"></sf-anchors>

    <div class="sf-body">
      <section id="sec-txns" class="sf-section sf-card p0" data-testid="section-transactions">
        <div class="sf-ch"><h2>Transactions</h2></div>
        <p-table [value]="txns" [loading]="txnsLoading" [tableStyle]="{ 'min-width': '56rem' }" data-testid="txn-table">
          <ng-template pTemplate="header">
            <tr><th>When</th><th>What</th><th class="text-right">Amount</th><th class="text-right">Balance after</th><th>Order</th><th>Store</th><th>By</th><th>Note</th></tr>
          </ng-template>
          <ng-template pTemplate="body" let-t>
            <tr data-testid="txn-row">
              <td>{{ t.performedAt | date:'dd MMM yyyy, HH:mm' }}</td>
              <td><span class="sf-pill" [ngClass]="t.tone">{{ t.typeLabel }}</span></td>
              <td class="text-right">{{ t.amount | number:'1.2-2' }}</td>
              <td class="text-right">{{ t.balanceAfter | number:'1.2-2' }}</td>
              <td><a *ngIf="t.orderUuid; else noOrder" class="sf-mono" [routerLink]="[posRoute, 'orders', t.orderUuid]">{{ t.orderNumber || 'Order' }}</a>
                <ng-template #noOrder><span class="sf-mu">—</span></ng-template></td>
              <td>{{ t.storeName }}</td>
              <td>{{ t.performedByName }}</td>
              <td>{{ t.note || '—' }}</td>
            </tr>
          </ng-template>
          <ng-template pTemplate="emptymessage"><tr><td colspan="8" class="sf-empty">No transactions.</td></tr></ng-template>
        </p-table>
      </section>

      <section id="sec-info" class="sf-section sf-card" data-testid="section-info">
        <div class="sf-ch"><h2>Details</h2></div>
        <div class="sf-kv">
          <span>Number</span><span class="sf-mono">{{ c.number }}<ng-container *ngIf="c.offlineNumber"> <span class="sf-mu">(issued offline as {{ c.offlineNumber }})</span></ng-container></span>
          <span>Customer</span>
          <span data-testid="customer-ref">
            <a *ngIf="c.customerUuid; else bearer" [routerLink]="['/portal/pages/customers', c.customerUuid]">{{ c.customerName || 'Customer' }}</a>
            <ng-template #bearer>Bearer — anyone holding the number can spend it</ng-template>
          </span>
          <span>From return</span>
          <span><a *ngIf="c.returnOrderUuid; else direct" class="sf-mono" [routerLink]="[posRoute, 'orders', c.returnOrderUuid]">{{ c.returnOrderNumber }}</a>
            <ng-template #direct>Issued directly</ng-template></span>
          <span>Issued</span><span>{{ c.issuedAt | date:'dd MMM yyyy, HH:mm' }} · {{ c.issuedAtStoreName }} · {{ c.issuedByName }}</span>
          <span>Last used</span><span>{{ c.lastUsedAt ? (c.lastUsedAt | date:'dd MMM yyyy, HH:mm') + (c.lastUsedAtStoreName ? ' · ' + c.lastUsedAtStoreName : '') : '—' }}</span>
          <span>Note</span><span class="scd-note">{{ c.note || '—' }}</span>
        </div>
        <p class="sf-mu sf-sm" style="margin-top: .75rem">Store credit is spent at a till as a payment, online only, never giving change.
          It is not a Finance credit note (SCN-…).</p>
      </section>
    </div>
  </ng-container>
</sf-page>

<p-dialog header="Cancel store credit" [(visible)]="cancelDialog" [modal]="true" [style]="{ width: '28rem' }" data-testid="cancel-dialog">
  <p class="dialog-lead">The remaining {{ credit?.remainingBalance | number:'1.2-2' }} can no longer be spent. This cannot be undone.</p>
  <div class="sf-pill er scd-err" *ngIf="cancelError" role="alert" data-testid="cancel-error">{{ cancelError }}</div>
  <textarea pTextarea rows="3" [(ngModel)]="cancelNote" style="width: 100%" placeholder="Note (why it is cancelled)" maxlength="500"
            data-testid="cancel-note"></textarea>
  <ng-template pTemplate="footer">
    <p-button label="Keep it" severity="secondary" [text]="true" (onClick)="cancelDialog = false"></p-button>
    <p-button label="Cancel store credit" icon="pi pi-ban" severity="danger" [loading]="cancelBusy" (onClick)="confirmCancel()"
              data-testid="confirm-cancel"></p-button>
  </ng-template>
</p-dialog>

<app-pos-store-credit-issue-dialog [(visible)]="issueOpen" [customerPreset]="credit?.customerUuid || null"
                                   (issued)="onIssued($event)"></app-pos-store-credit-issue-dialog>
  `,
  styles: [`
    .scd-err { display: block; margin-bottom: .75rem; white-space: pre-line; }
    .scd-note { white-space: pre-line; }
  `],
  providers: [MessageService]
})
export class PosStoreCreditDetailComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly service = inject(PosReturnsAdminService);
  private readonly auth = inject(AuthService);
  private readonly modules = inject(ModuleService);
  private readonly messages = inject(MessageService);
  private readonly destroyRef = inject(DestroyRef);

  readonly posRoute = POS_ROUTE;

  uuid = '';
  credit: StoreCreditDetail | null = null;
  isLoading = true;
  loadError: string | null = null;
  statusLabel = '';
  statusTone = '';
  txns: StoreCreditTxnRow[] = [];
  txnsLoading = false;
  sections: FlowSection[] = [];

  cancelDialog = false;
  cancelNote = '';
  cancelBusy = false;
  cancelError: string | null = null;
  issueOpen = false;

  get canCancel(): boolean { return this.auth.hasPermission('POS_CREDIT_NOTE_CANCEL'); }
  get canIssue(): boolean { return this.auth.hasPermission('POS_CREDIT_NOTE_ISSUE') && this.modules.isFeatureEnabled('FEATURE_POS_CREDIT_NOTES'); }
  get showCancel(): boolean { return this.canCancel && this.credit?.status === 'ACTIVE'; }

  ngOnInit(): void {
    this.route.paramMap.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(p => {
      this.uuid = p.get('uuid') ?? '';
      this.load();
    });
  }

  load(): void {
    this.isLoading = true;
    this.loadError = null;
    this.service.getStoreCredit(this.uuid).subscribe({
      next: res => {
        this.isLoading = false;
        const c = res.result ?? null;
        if (!c) { this.credit = null; this.loadError = res.message || 'Store credit not found.'; return; }
        this.setCredit(c);
      },
      error: err => {
        this.isLoading = false;
        this.credit = null;
        this.loadError = posError(err, err?.status === 404 ? 'Store credit not found.' : 'Failed to load the store credit.');
      }
    });
  }

  private setCredit(c: StoreCreditDetail | StoreCredit): void {
    this.credit = { transactions: [], ...c };
    this.statusLabel = storeCreditStatusLabel(c.status);
    this.statusTone = storeCreditStatusTone(c.status);
    if (Array.isArray((c as StoreCreditDetail).transactions)) this.txns = txnRows((c as StoreCreditDetail).transactions);
    else this.loadTransactions();
    this.sections = [{ id: 'sec-txns', label: 'Transactions', count: this.txns.length }, { id: 'sec-info', label: 'Details' }];
  }

  /** The detail normally carries its transactions; otherwise they come from their own endpoint. */
  private loadTransactions(): void {
    this.txnsLoading = true;
    this.service.getStoreCreditTransactions(this.uuid).subscribe({
      next: res => {
        this.txnsLoading = false;
        this.txns = txnRows(res.result ?? []);
        this.sections = [{ id: 'sec-txns', label: 'Transactions', count: this.txns.length }, { id: 'sec-info', label: 'Details' }];
      },
      error: () => { this.txnsLoading = false; this.txns = []; }
    });
  }

  openCancel(): void { this.cancelNote = ''; this.cancelError = null; this.cancelDialog = true; }

  confirmCancel(): void {
    if (this.cancelBusy) return;
    this.cancelBusy = true;
    this.cancelError = null;
    this.service.cancelStoreCredit(this.uuid, this.cancelNote.trim() || null).subscribe({
      next: res => {
        this.cancelBusy = false;
        if (res && res.success === false) { this.cancelError = res.message || 'The store credit was not cancelled.'; return; }
        this.cancelDialog = false;
        this.messages.add({ severity: 'success', summary: 'Cancelled', detail: 'Store credit cancelled.' });
        this.load();
      },
      error: err => { this.cancelBusy = false; this.cancelError = posError(err, 'The store credit was not cancelled.'); }
    });
  }

  onIssued(c: StoreCredit): void {
    this.messages.add({ severity: 'success', summary: 'Issued', detail: `Store credit ${c.number} issued.` });
    this.router.navigate([STORE_CREDIT_ROUTE, c.uuid]);
  }

  back(): void { this.router.navigate([STORE_CREDIT_ROUTE]); }
}
