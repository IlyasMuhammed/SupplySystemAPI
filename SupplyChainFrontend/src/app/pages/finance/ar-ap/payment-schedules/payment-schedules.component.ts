import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { DialogModule } from 'primeng/dialog';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';

import {
  AR_AP_PERMS, AP_ROUTE, ArApService, CashFlowForecast, PAYMENT_RUNS_ROUTE, PaymentSchedule
} from '../../../../services/ar-ap.service';
import { AuthService } from '../../../service/auth.service';
import { FLOW } from '../../../../shared/flow';
import { addDays, arApError, dueText, labelOf, money, reasonProblem, scheduleTone, today } from '../ar-ap.shared';

export type ScheduleView = 'upcoming' | 'discounts' | 'cashflow';

/**
 * A48 C10 / C15 — `/finance/ap/payment-schedules[?view=]`: the approved, unpaid supplier invoices by due date (D-8),
 * the early-payment discount opportunities (pay by the discount date, BR-EP-001), block / unblock a payment with a
 * reason (blocked items stay out of payment runs, BR-PR-002 — AP_EXECUTE) and the weekly payables cash-flow forecast.
 */
@Component({
  selector: 'app-payment-schedules',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule, ButtonModule, DialogModule, InputNumberModule, InputTextModule, TextareaModule, ToastModule, ...FLOW],
  providers: [MessageService],
  styleUrls: ['../../gl/gl.scss', '../ar-ap.scss'],
  template: `
<p-toast></p-toast>
<sf-page [crumbs]="['Finance', 'Payables', 'Payment schedules']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <a *ngIf="canPropose" class="p-button" [routerLink]="[runsRoute, 'new']" data-testid="go-run"><i class="pi pi-plus"></i>&nbsp;Create payment run</a>
    <span class="sf-end">
      <a class="p-button p-button-text" [routerLink]="apRoute"><i class="pi pi-arrow-left"></i>&nbsp;AP dashboard</a>
      <p-button label="Refresh" icon="pi pi-refresh" [text]="true" (onClick)="load()" data-testid="refresh"></p-button>
    </span>
  </div>
  <div class="sf-body">
    <div class="sf-lr-head">
      <div><h1>Payment schedules</h1><div class="sf-sub">What falls due, what can be paid early for a discount, and what is held back</div></div>
      <div class="sf-filters">
        <button type="button" class="sf-chip" [class.on]="view === 'upcoming'" (click)="setView('upcoming')" data-testid="view-upcoming">Upcoming</button>
        <button type="button" class="sf-chip" [class.on]="view === 'discounts'" (click)="setView('discounts')" data-testid="view-discounts">Discount opportunities</button>
        <button type="button" class="sf-chip" [class.on]="view === 'cashflow'" (click)="setView('cashflow')" data-testid="view-cashflow">Cash-flow forecast</button>
      </div>
    </div>

    <div class="sf-filters">
      <ng-container *ngIf="view === 'upcoming'">
        <label class="sf-mu sf-sm" for="ps-from">From</label><input pInputText id="ps-from" type="date" [(ngModel)]="from" (change)="load()" data-testid="from" />
        <label class="sf-mu sf-sm" for="ps-to">To</label><input pInputText id="ps-to" type="date" [(ngModel)]="to" (change)="load()" data-testid="to" />
      </ng-container>
      <ng-container *ngIf="view === 'discounts'">
        <label class="sf-mu sf-sm" for="ps-look">Discount date within</label>
        <p-inputNumber inputId="ps-look" [(ngModel)]="lookAhead" [min]="1" [max]="90" suffix=" days" (onBlur)="load()" data-testid="look-ahead"></p-inputNumber>
      </ng-container>
      <ng-container *ngIf="view === 'cashflow'">
        <label class="sf-mu sf-sm" for="ps-weeks">Weeks</label>
        <p-inputNumber inputId="ps-weeks" [(ngModel)]="weeks" [min]="1" [max]="26" (onBlur)="load()" data-testid="weeks"></p-inputNumber>
      </ng-container>
    </div>

    <div class="gl-note er" *ngIf="loadError" data-testid="load-error"><i class="pi pi-exclamation-triangle"></i><div>{{ loadError }}</div></div>
    <div class="sf-card sf-empty" *ngIf="loading"><i class="pi pi-spin pi-spinner"></i> Loading…</div>

    <ng-container *ngIf="!loading && view !== 'cashflow'">
      <div class="sf-grid sf-tiles" data-testid="totals">
        <div class="sf-tile"><span class="l">Invoices</span><span class="v">{{ rows.length }}</span></div>
        <div class="sf-tile"><span class="l">Remaining</span><span class="v">{{ amount(totalRemaining) }}</span></div>
        <div class="sf-tile" *ngIf="view === 'discounts'"><span class="l">Discount available</span><span class="v">{{ amount(totalDiscount) }}</span></div>
        <div class="sf-tile" *ngIf="view === 'upcoming'"><span class="l">Blocked</span><span class="v">{{ blockedCount }}</span></div>
      </div>
      <section class="sf-card p0">
        <div class="gl-scroll">
          <table class="aa-table" style="min-width:58rem" data-testid="schedules">
            <thead><tr><th>Vendor</th><th>Invoice</th><th>Due</th><th class="gl-num">Original</th><th class="gl-num">Remaining</th>
              <th>Discount</th><th>Priority</th><th>Status</th><th style="width:7rem"></th></tr></thead>
            <tbody>
              <tr *ngFor="let s of rows" [class.aa-blocked]="s.paymentBlock" [attr.data-testid]="'sched-' + s.uuid">
                <td>{{ s.vendorName }}</td>
                <td><a class="sf-mono" [routerLink]="['/portal/pages/finance/invoices', s.invoiceUuid]">{{ s.invoiceNumber }}</a>
                  <span class="sf-mu sf-sm" *ngIf="s.supplierInvoiceNo"> · {{ s.supplierInvoiceNo }}</span></td>
                <td class="sf-nowrap">{{ s.dueDate | date:'dd MMM yyyy' }} <span class="aa-mark late" *ngIf="(s.daysUntilDue ?? 0) < 0">{{ due(s.daysUntilDue) }}</span></td>
                <td class="gl-num">{{ amount(s.originalAmount) }} <span class="sf-mu sf-sm">{{ s.currencyCode || '' }}</span></td>
                <td class="gl-num">{{ amount(s.amountRemaining) }}</td>
                <td><span class="aa-mark disc" *ngIf="s.discountAmount">{{ amount(s.discountAmount) }} by {{ s.discountDate | date:'dd MMM' }}</span></td>
                <td>{{ s.paymentPriority || '' }}</td>
                <td><span class="sf-pill" [ngClass]="tone(s.paymentBlock ? 'Blocked' : s.status)">{{ label(s.paymentBlock ? 'Blocked' : s.status) }}</span>
                  <div class="sf-mu sf-sm" *ngIf="s.paymentBlock && s.blockReason">{{ s.blockReason }}</div>
                  <div class="sf-mu sf-sm" *ngIf="s.reservedByRunNumber" data-testid="in-run">in run {{ s.reservedByRunNumber }}</div>
                  <div class="sf-mu sf-sm" *ngIf="s.paidInRunNumber">paid in {{ s.paidInRunNumber }}</div></td>
                <td><ng-container *ngIf="canExecute">
                  <p-button *ngIf="!s.paymentBlock" label="Block" icon="pi pi-lock" [text]="true" size="small" severity="danger" (onClick)="openBlock(s)" [attr.data-testid]="'block-' + s.uuid"></p-button>
                  <p-button *ngIf="s.paymentBlock" label="Unblock" icon="pi pi-lock-open" [text]="true" size="small" [disabled]="busy" (onClick)="unblock(s)" [attr.data-testid]="'unblock-' + s.uuid"></p-button>
                </ng-container></td>
              </tr>
              <tr *ngIf="!rows.length"><td colspan="9" class="sf-empty" data-testid="empty">{{ view === 'discounts' ? 'No early-payment discount to take in this window.' : 'Nothing falls due in this period.' }}</td></tr>
            </tbody>
          </table>
        </div>
      </section>
    </ng-container>

    <section class="sf-card" *ngIf="!loading && view === 'cashflow' && forecast as f" data-testid="cashflow">
      <div class="sf-ch"><h2>Payables by week</h2><span class="sf-end sf-mu sf-sm" data-testid="cashflow-total">{{ amount(f.totalDue) }} {{ f.baseCurrencyCode || '' }}
        <ng-container *ngIf="f.overdueAmount"> · {{ amount(f.overdueAmount) }} already overdue ({{ f.overdueCount }})</ng-container>
        <ng-container *ngIf="f.laterAmount"> · {{ amount(f.laterAmount) }} later</ng-container>
        <ng-container *ngIf="f.totalDiscountAvailable"> · {{ amount(f.totalDiscountAvailable) }} discount available</ng-container></span></div>
      <div class="aa-bars">
        <div class="aa-bar-row" *ngFor="let w of f.weekRows" [attr.data-testid]="'week-' + w.weekStart">
          <span>{{ w.weekStart | date:'dd MMM' }} – {{ w.weekEnd | date:'dd MMM' }}</span>
          <span class="aa-bar-track"><span class="aa-bar" [style.display]="'block'" [style.width.%]="barWidth(w.dueAmount)"></span></span>
          <span class="gl-num">{{ amount(w.dueAmount) }} <span class="sf-mu sf-sm">({{ w.itemCount }}<ng-container *ngIf="w.blockedAmount">, {{ amount(w.blockedAmount) }} blocked</ng-container>)</span></span>
        </div>
        <div class="sf-empty" *ngIf="!f.weekRows.length">Nothing falls due in these weeks.</div>
      </div>
    </section>
  </div>
</sf-page>

<p-dialog [(visible)]="blockOpen" [modal]="true" header="Block payment" [style]="{ width: '28rem', maxWidth: '96vw' }" styleClass="sdlg">
  <div class="aa-dlg-body">
    <div class="sf-mu" *ngIf="blocking">{{ blocking.invoiceNumber }} · {{ blocking.vendorName }} · {{ amount(blocking.amountRemaining) }}</div>
    <div class="sf-fld"><label for="ps-reason" class="sf-req">Reason</label><textarea pTextarea id="ps-reason" rows="3" [(ngModel)]="reason" maxlength="200" data-testid="reason"></textarea></div>
    <div class="gl-note er" *ngIf="dialogError" data-testid="dialog-error"><i class="pi pi-exclamation-triangle"></i><div>{{ dialogError }}</div></div>
  </div>
  <ng-template pTemplate="footer">
    <p-button label="Cancel" [text]="true" severity="secondary" (onClick)="blockOpen = false"></p-button>
    <p-button label="Block" severity="danger" [disabled]="busy" [loading]="busy" (onClick)="block()" data-testid="confirm-block"></p-button>
  </ng-template>
</p-dialog>`,
  styles: [`.sf-nowrap { white-space: nowrap; }`]
})
export class PaymentSchedulesComponent implements OnInit {
  private readonly api = inject(ArApService);
  private readonly auth = inject(AuthService);
  private readonly messages = inject(MessageService);
  private readonly route = inject(ActivatedRoute, { optional: true });

  readonly apRoute = AP_ROUTE;
  readonly runsRoute = PAYMENT_RUNS_ROUTE;

  view: ScheduleView = 'upcoming';
  from = today();
  to = addDays(today(), 30);
  lookAhead = 7;
  weeks = 8;

  rows: PaymentSchedule[] = [];
  forecast: CashFlowForecast | null = null;
  loading = false;
  loadError = '';

  blockOpen = false;
  blocking: PaymentSchedule | null = null;
  reason = '';
  dialogError = '';
  busy = false;

  get canExecute(): boolean { return this.auth.hasPermission(AR_AP_PERMS.AP_EXECUTE); }
  get canPropose(): boolean { return this.auth.hasPermission(AR_AP_PERMS.PAYMENT_RUN_PROPOSE); }
  get totalRemaining(): number { return this.rows.reduce((s, r) => s + Number(r.amountRemaining || 0), 0); }
  get totalDiscount(): number { return this.rows.reduce((s, r) => s + Number(r.discountAmount || 0), 0); }
  get blockedCount(): number { return this.rows.filter(r => r.paymentBlock).length; }

  ngOnInit(): void {
    const v = this.route?.snapshot?.queryParamMap?.get('view');
    if (v === 'discounts' || v === 'cashflow') this.view = v;
    this.load();
  }

  setView(v: ScheduleView): void { if (this.view !== v) { this.view = v; this.load(); } }

  private seq = 0;

  load(): void {
    const seq = ++this.seq;
    this.loading = true;
    this.loadError = '';
    const fail = (err: unknown) => { if (seq !== this.seq) return; this.loading = false; this.rows = []; this.forecast = null; this.loadError = arApError(err, 'The schedules could not be loaded.'); };
    if (this.view === 'cashflow') {
      this.api.getCashFlow(today(), this.weeks || 8).subscribe({ next: f => { if (seq !== this.seq) return; this.loading = false; this.forecast = f; }, error: fail });
      return;
    }
    const call = this.view === 'discounts' ? this.api.getDiscountOpportunities(today(), this.lookAhead || 7) : this.api.getUpcomingPayments(this.from || null, this.to || null);
    const key = (s: PaymentSchedule) => (this.view === 'discounts' ? s.discountDate ?? s.dueDate : s.dueDate) ?? '';
    call.subscribe({
      next: list => { if (seq !== this.seq) return; this.loading = false; this.rows = [...list].sort((a, b) => key(a).localeCompare(key(b))); },
      error: fail
    });
  }

  openBlock(s: PaymentSchedule): void { this.blocking = s; this.reason = ''; this.dialogError = ''; this.blockOpen = true; }

  block(): void {
    const s = this.blocking;
    if (!s) return;
    this.dialogError = reasonProblem(this.reason) ?? '';
    if (this.dialogError) return;
    this.busy = true;
    this.api.blockSchedule(s.uuid, this.reason.trim()).subscribe({
      next: () => { this.busy = false; this.blockOpen = false; this.toast('success', `${s.invoiceNumber} is blocked from payment runs.`); this.load(); },
      error: err => { this.busy = false; this.dialogError = arApError(err, 'The payment could not be blocked.'); }
    });
  }

  unblock(s: PaymentSchedule): void {
    this.busy = true;
    this.api.unblockSchedule(s.uuid).subscribe({
      next: () => { this.busy = false; this.toast('success', `${s.invoiceNumber} can be paid again.`); this.load(); },
      error: err => { this.busy = false; this.toast('error', arApError(err, 'The payment could not be unblocked.')); }
    });
  }

  barWidth(n: number): number {
    const max = Math.max(...(this.forecast?.weekRows ?? []).map(w => Number(w.dueAmount || 0)), 0);
    return max > 0 ? Math.max(1, Math.round((Number(n || 0) / max) * 100)) : 0;
  }

  due(d: number | null | undefined): string { return dueText(d); }
  tone(s: string | null | undefined): string { return scheduleTone(s); }
  label(s: string | null | undefined): string { return labelOf(s); }
  amount(n: number | null | undefined): string { return money(n); }

  private toast(severity: 'success' | 'error', detail: string): void {
    this.messages.add({ severity, summary: severity === 'success' ? 'Done' : 'Error', detail, life: severity === 'error' ? 8000 : 3000 });
  }
}
