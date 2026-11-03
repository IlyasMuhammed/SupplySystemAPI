import { Component, OnDestroy, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { AutoCompleteCompleteEvent, AutoCompleteModule } from 'primeng/autocomplete';
import { ButtonModule } from 'primeng/button';
import { CheckboxModule } from 'primeng/checkbox';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { DialogModule } from 'primeng/dialog';
import { InputTextModule } from 'primeng/inputtext';
import { SelectModule } from 'primeng/select';
import { TextareaModule } from 'primeng/textarea';
import { ToastModule } from 'primeng/toast';
import { ConfirmationService, MessageService } from 'primeng/api';
import { Observable, Subject, Subscription, catchError, exhaustMap, of, takeUntil, takeWhile, timer } from 'rxjs';

import {
  AP_PAYMENT_METHODS, AR_AP_PERMS, ArApService, PAYMENT_RUNS_ROUTE, PaymentPriority, PaymentRunDetail, PaymentRunExecution, PaymentRunItem,
  PaymentRunVendorGroup
} from '../../../../services/ar-ap.service';
import { BusinessPartnerModel, BusinessPartnerService } from '../../../../services/business-partner.service';
import { saveDownload } from '../../../../services/import.service';
import { AuthService } from '../../../service/auth.service';
import { FLOW } from '../../../../shared/flow';
import {
  RUN_STEPS, addDays, arApError, arApErrorAsync, blockedRows, criteriaProblem, dueText, groupLive, isRemoved, labelOf, money, reasonProblem, runCancellable,
  runEditable, runProgress, runTone, runTotals, stepForStatus, today
} from '../ar-ap.shared';

/** §8 `minPriority`: High = high only, Normal = high + normal, none = all. */
export const PRIORITY_OPTIONS: { label: string; value: PaymentPriority | null }[] = [
  { label: 'All priorities', value: null }, { label: 'High and normal', value: 'Normal' }, { label: 'High only', value: 'High' }
];

/**
 * A48 §8.4, tasks 560–567 / 570 — `/finance/ap/payment-runs/new` and `/:uuid`, a four-step wizard over the run's
 * status (D-9): 1 selection criteria → propose; 2 review the proposal — vendor groups (one payment per vendor and
 * currency, BR-PR-003), items with discount / overdue markers, blocked items apart (BR-PR-002), remove / restore with a
 * reason; 3 approve with the totals (BR-PR-006); 4 execute — one supplier payment per batch, a failed batch does not
 * stop the others — and the summary. CSV export of the payment file (D-15) from step 2 on.
 */
@Component({
  selector: 'app-payment-run-wizard',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule, AutoCompleteModule, ButtonModule, CheckboxModule, ConfirmDialogModule, DialogModule, InputTextModule,
    SelectModule, TextareaModule, ToastModule, ...FLOW],
  providers: [MessageService, ConfirmationService],
  styleUrls: ['../../gl/gl.scss', '../ar-ap.scss'],
  template: `
<p-toast></p-toast>
<p-confirmDialog key="pr" [style]="{ width: '32rem', maxWidth: '96vw' }"></p-confirmDialog>
<sf-page [crumbs]="['Finance', 'Payables', 'Payment runs', run?.runNumber || 'New']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button *ngIf="step === 1 && canPropose" label="Propose" icon="pi pi-play" [disabled]="busy" [loading]="busy" (onClick)="propose()" data-testid="propose"></p-button>
    <p-button *ngIf="step === 2 && run" label="Continue to approval" icon="pi pi-arrow-right" iconPos="right" [disabled]="!totals.items" (onClick)="go(3)" data-testid="to-approve"></p-button>
    <p-button *ngIf="step === 3 && canApprove && run?.status !== 'Approved' && editable" label="Approve" icon="pi pi-check" [disabled]="busy || !totals.items" [loading]="busy"
              (onClick)="confirmApprove()" data-testid="approve"></p-button>
    <p-button *ngIf="step === 4 && canExecute && run?.status === 'Approved'" label="Execute" icon="pi pi-send" [disabled]="busy" [loading]="busy" (onClick)="confirmExecute()" data-testid="execute"></p-button>
    <p-button *ngIf="step === 4 && canExecute && run?.status === 'Executing'" label="Resume" icon="pi pi-replay" [text]="true" [disabled]="busy" [loading]="resuming"
              (onClick)="confirmResume()" data-testid="resume"></p-button>
    <p-button *ngIf="run && step > 1 && canExecute && exportable" label="Export CSV" icon="pi pi-download" [text]="true" [disabled]="exporting" [loading]="exporting" (onClick)="exportCsv()" data-testid="export"></p-button>
    <ng-container *ngIf="run && canCancel && cancellable">
      <span class="sf-sep"></span>
      <p-button label="Cancel run" icon="pi pi-times" severity="danger" [text]="true" (onClick)="openCancel()" data-testid="cancel-run"></p-button>
    </ng-container>
    <span class="sf-end"><a class="p-button p-button-text" [routerLink]="listRoute"><i class="pi pi-list"></i>&nbsp;All runs</a></span>
  </div>

  <div class="sf-body">
    <div class="sf-lr-head"><div><h1>Payment run<ng-container *ngIf="run"> <span class="sf-mono">{{ run.runNumber }}</span>
      <span class="sf-pill" [ngClass]="tone(run.status)" data-testid="run-status">{{ run.status }}</span></ng-container></h1>
      <div class="sf-sub">{{ stepTitle }}</div></div></div>

    <!-- Task 560 — the stepper -->
    <nav class="gl-steps" aria-label="Steps" data-testid="steps">
      <button type="button" *ngFor="let s of steps" [class.on]="s.step === step" [class.done]="s.step < maxStep && s.step !== step" [disabled]="!canGo(s.step)"
              (click)="go(s.step)" [attr.data-testid]="'step-' + s.step"><b>{{ s.step }} · {{ s.short }}</b><span>{{ s.title }}</span></button>
    </nav>

    <div class="gl-note er" *ngIf="error" data-testid="error"><i class="pi pi-exclamation-triangle"></i><div>{{ error }}</div></div>
    <div class="sf-card sf-empty" *ngIf="loading"><i class="pi pi-spin pi-spinner"></i> Loading…</div>

    <!-- Step 1 — criteria (task 561) -->
    <section class="sf-card" *ngIf="step === 1 && !loading" data-testid="criteria">
      <div class="sf-ch"><h2>Selection criteria</h2></div>
      <div class="sf-fields">
        <div class="sf-fld"><label for="pr-pay" class="sf-req">Payment date</label><input pInputText id="pr-pay" type="date" [(ngModel)]="paymentDate" [min]="todayIso" data-testid="payment-date" /></div>
        <div class="sf-fld"><label for="pr-cut" class="sf-req">Due on or before</label><input pInputText id="pr-cut" type="date" [(ngModel)]="cutoff" data-testid="cutoff" /></div>
        <div class="sf-fld"><label for="pr-method">Payment method</label>
          <p-select inputId="pr-method" [options]="methodOptions" [(ngModel)]="method" optionLabel="label" optionValue="value" appendTo="body" data-testid="method"></p-select></div>
        <div class="sf-fld"><label for="pr-vendor">Vendor</label>
          <p-autoComplete inputId="pr-vendor" [(ngModel)]="vendor" [suggestions]="vendorSuggestions" optionLabel="companyName" (completeMethod)="searchVendors($event)"
                          [minLength]="1" appendTo="body" placeholder="Every vendor" data-testid="vendor"></p-autoComplete></div>
        <div class="sf-fld"><label for="pr-bank">Pay from (bank account)</label><input pInputText id="pr-bank" [(ngModel)]="bankAccount" maxlength="100" data-testid="bank" /></div>
        <div class="sf-fld"><label for="pr-prio">Priority</label>
          <p-select inputId="pr-prio" [options]="priorityOptions" [(ngModel)]="minPriority" optionLabel="label" optionValue="value" appendTo="body" data-testid="priority"></p-select></div>
        <div class="sf-fld"><label>&nbsp;</label><label class="gl-check"><p-checkbox [binary]="true" [(ngModel)]="includeDiscount" data-testid="include-discount"></p-checkbox> Take early-payment discounts</label></div>
      </div>
      <div class="gl-note wn" *ngIf="criteriaError" style="margin-top:.75rem" data-testid="criteria-error"><i class="pi pi-info-circle"></i><div>{{ criteriaError }}</div></div>
      <div class="gl-note" style="margin-top:.75rem" *ngIf="!canPropose"><i class="pi pi-info-circle"></i><div>Proposing a payment run needs PAYMENT_RUN_PROPOSE.</div></div>
    </section>

    <ng-container *ngIf="run && !loading && step > 1">
      <!-- D-AP-12 — e.g. no discount account assigned: approval / execution will be refused until it is. -->
      <div class="gl-note wn" *ngIf="run.warnings.length" data-testid="warnings"><i class="pi pi-exclamation-triangle"></i>
        <div><div *ngFor="let w of run.warnings">{{ w }}</div></div></div>
      <!-- The run's head and live totals (tasks 562 / 565) -->
      <section class="sf-card" data-testid="summary">
        <div class="aa-kv-grid">
          <div><span class="l">Payment date</span><span class="v">{{ run.paymentDate | date:'dd MMM yyyy' }}</span></div>
          <div><span class="l">Due cutoff</span><span class="v">{{ run.dueDateCutoff ? (run.dueDateCutoff | date:'dd MMM yyyy') : '—' }}</span></div>
          <div><span class="l">Bank account</span><span class="v">{{ run.bankAccount || '—' }}</span><span class="s">{{ label(run.paymentMethod) || 'any method' }}</span></div>
          <div><span class="l">Vendors</span><span class="v" data-testid="t-vendors">{{ totals.vendors }}</span></div>
          <div><span class="l">Items</span><span class="v" data-testid="t-items">{{ totals.items }}</span></div>
          <div><span class="l">Total</span><span class="v" data-testid="t-total">{{ amount(totals.total) }}</span></div>
          <div><span class="l">Discount</span><span class="v ok" data-testid="t-discount">{{ amount(totals.discount) }}</span></div>
          <div><span class="l">Net payment</span><span class="v" data-testid="t-net">{{ amount(totals.net) }}</span><span class="s">{{ run.baseCurrencyCode || '' }}</span></div>
        </div>
      </section>

      <!-- Step 2 — review (tasks 562–564) -->
      <ng-container *ngIf="step === 2">
        <div class="aa-group" *ngFor="let g of run.vendorGroups" [class.blocked]="g.isBlocked" [attr.data-testid]="'group-' + g.vendor.uuid">
          <div class="aa-group-head">
            <i class="pi" [ngClass]="g.isBlocked || !live(g).items ? 'pi-stop' : 'pi-check-square'" aria-hidden="true"></i>
            <b>{{ g.vendor.name }}</b><span class="sf-mu sf-sm">({{ live(g).items }} of {{ g.items.length }} items)</span>
            <span class="aa-mark lock" *ngIf="g.isBlocked">BLOCKED</span>
            <span class="sf-mu sf-sm" *ngIf="g.currencyCode">{{ g.currencyCode }}</span>
            <span class="sf-pill" *ngIf="g.batchStatus" [ngClass]="tone(g.batchStatus)">{{ g.batchStatus }}</span>
            <span class="aa-end">{{ amount(live(g).net) }}</span>
          </div>
          <div class="gl-scroll">
            <table class="aa-table" style="min-width:42rem">
              <tbody>
                <tr *ngFor="let i of g.items" [class.aa-off]="removed(i)" [attr.data-testid]="'item-' + i.uuid">
                  <td class="sf-mono">{{ i.invoiceNumber }}<span class="sf-mu sf-sm" *ngIf="i.supplierInvoiceNo"> · {{ i.supplierInvoiceNo }}</span></td>
                  <td>Due {{ i.dueDate | date:'dd MMM' }}</td>
                  <td class="gl-num">{{ amount(i.originalAmount) }}</td>
                  <td>
                    <span class="aa-mark disc" *ngIf="i.hasDiscount && i.discountAmount" [attr.data-testid]="'disc-' + i.uuid"><i class="pi pi-percentage"></i> {{ amount(i.discountAmount) }}</span>
                    <span class="aa-mark late" *ngIf="(i.daysUntilDue ?? 0) < 0" [attr.data-testid]="'late-' + i.uuid"><i class="pi pi-exclamation-triangle"></i> {{ due(i.daysUntilDue) }}</span>
                    <span class="aa-mark lock" *ngIf="i.isBlocked"><i class="pi pi-lock"></i> {{ i.blockReason || 'Blocked' }}</span>
                    <span class="sf-mu sf-sm" *ngIf="removed(i) && i.removedReason">removed: {{ i.removedReason }}</span>
                  </td>
                  <td class="gl-num"><b>{{ amount(i.netPaymentAmount) }}</b></td>
                  <td style="width:7rem">
                    <ng-container *ngIf="canEdit && editable && !i.isBlocked && !g.isBlocked">
                      <p-button *ngIf="!removed(i)" label="Remove" [text]="true" size="small" severity="danger" [disabled]="busy" (onClick)="openRemove(i)" [attr.data-testid]="'remove-' + i.uuid"></p-button>
                      <p-button *ngIf="removed(i)" label="Restore" [text]="true" size="small" [disabled]="busy" (onClick)="restore(i)" [attr.data-testid]="'restore-' + i.uuid"></p-button>
                    </ng-container>
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
        </div>
        <div class="sf-card sf-empty" *ngIf="!run.vendorGroups.length" data-testid="no-groups">Nothing is due within these criteria.</div>

        <section class="sf-card p0" *ngIf="run.removedItems.length" data-testid="removed">
          <div class="sf-ch sf-pad"><h2>Removed from this run <span class="sf-count">{{ run.removedItems.length }}</span></h2></div>
          <div class="gl-scroll"><table class="aa-table" style="min-width:36rem"><tbody>
            <tr *ngFor="let i of run.removedItems" class="aa-off" [attr.data-testid]="'removed-' + i.uuid">
              <td class="sf-mono">{{ i.invoiceNumber }}</td><td>Due {{ i.dueDate | date:'dd MMM' }}</td><td class="gl-num">{{ amount(i.netPaymentAmount) }}</td>
              <td class="sf-mu sf-sm">{{ i.removedReason || '' }}</td>
              <td style="width:7rem"><p-button *ngIf="canEdit && editable" label="Restore" [text]="true" size="small" [disabled]="busy" (onClick)="restore(i)"
                                               [attr.data-testid]="'restore-' + i.uuid"></p-button></td></tr>
          </tbody></table></div>
        </section>

        <section class="sf-card p0" *ngIf="blocked.length" data-testid="blocked">
          <div class="sf-ch sf-pad"><h2>Blocked — not in this run <span class="sf-count">{{ blocked.length }}</span></h2></div>
          <div class="gl-scroll"><table class="aa-table" style="min-width:36rem"><tbody>
            <tr *ngFor="let b of blocked" class="aa-blocked"><td>{{ b.vendorName }}</td><td class="sf-mono">{{ b.item.invoiceNumber }}</td><td>Due {{ b.item.dueDate | date:'dd MMM' }}</td>
              <td class="gl-num">{{ amount(b.item.originalAmount) }}</td><td><span class="aa-mark lock"><i class="pi pi-lock"></i> {{ b.item.blockReason || 'Blocked' }}</span></td></tr>
          </tbody></table></div>
        </section>
        <div class="sf-mu sf-sm aa-actions"><span class="aa-mark disc"><i class="pi pi-percentage"></i> early discount</span>
          <span class="aa-mark late"><i class="pi pi-exclamation-triangle"></i> overdue</span><span class="aa-mark lock"><i class="pi pi-lock"></i> blocked</span></div>
      </ng-container>

      <!-- Step 3 — approve (task 565) -->
      <section class="sf-card" *ngIf="step === 3" data-testid="approve-step">
        <div class="sf-ch"><h2>Approve</h2></div>
        <p *ngIf="run.status !== 'Approved' && editable">Approving fixes the run: {{ totals.vendors }} vendor payment{{ totals.vendors === 1 ? '' : 's' }}, {{ totals.items }} invoices,
          {{ amount(totals.net) }} net after {{ amount(totals.discount) }} discount. Items can no longer be removed afterwards.</p>
        <p *ngIf="run.status === 'Approved'" data-testid="approved-note">Approved<ng-container *ngIf="run.approvedByName"> by {{ run.approvedByName }}</ng-container>
          <ng-container *ngIf="run.approvedAt"> on {{ run.approvedAt | date:'dd MMM yyyy, HH:mm' }}</ng-container>. Go on to execute it.</p>
        <div class="gl-note" *ngIf="!canApprove && editable"><i class="pi pi-info-circle"></i><div>Approving needs PAYMENT_RUN_APPROVE.</div></div>
        <div class="aa-actions" *ngIf="run.status === 'Approved'"><p-button label="Go to execute" icon="pi pi-arrow-right" iconPos="right" (onClick)="go(4)" data-testid="to-execute"></p-button></div>
      </section>

      <!-- Step 4 — execute + summary (task 566) -->
      <section class="sf-card" *ngIf="step === 4" data-testid="execute-step">
        <div class="sf-ch"><h2>{{ run.status === 'Completed' ? 'Completed' : 'Execute' }}</h2></div>
        <div class="gl-note" *ngIf="run.status === 'Executing' || executing" data-testid="progress"><i class="pi pi-spin pi-spinner"></i>
          <div>Creating the supplier payments, one per vendor batch — {{ progress.done }} of {{ progress.total }} done<ng-container *ngIf="progress.failed">
            ({{ progress.failed }} failed)</ng-container>. The page follows it until the run completes.
            <div class="sf-sm" *ngIf="stalled" data-testid="stalled">No batch has finished for a while. If the run stopped (e.g. the server restarted), Resume carries on
              with the batches still pending<ng-container *ngIf="canCancel && !progress.paid">, or cancel it — nothing has been paid yet</ng-container>.</div></div></div>
        <div class="gl-scroll" *ngIf="run.status === 'Executing'"><table class="aa-table" style="min-width:36rem;margin-top:.5rem" data-testid="live-batches"><tbody>
          <tr *ngFor="let g of run.vendorGroups"><td>{{ g.vendor.name }}</td><td><span class="sf-pill" [ngClass]="tone(g.batchStatus)">{{ g.batchStatus || '' }}</span></td>
            <td class="sf-mono">{{ g.paymentNumber || '' }}<span class="aa-bad sf-sm" *ngIf="g.failureReason"> {{ g.failureReason }}</span></td>
            <td class="gl-num">{{ amount(g.netAmount) }} <span class="sf-mu sf-sm">{{ g.currencyCode || '' }}</span></td></tr>
        </tbody></table></div>
        <p *ngIf="run.status === 'Approved' && !executing">Executing creates one supplier payment per vendor batch, allocated to its invoices, and posts them. A failed batch is marked and the others go on.</p>
        <div class="gl-note" *ngIf="!canExecute && run.status === 'Approved'"><i class="pi pi-info-circle"></i><div>Executing needs PAYMENT_RUN_EXECUTE.</div></div>
        <ng-container *ngIf="run.status === 'Completed'">
          <div class="gl-note" [ngClass]="failedGroups.length ? 'wn' : 'ok'" data-testid="result"><i class="pi" [ngClass]="failedGroups.length ? 'pi-exclamation-triangle' : 'pi-check'"></i>
            <div>{{ paidGroups.length }} payment{{ paidGroups.length === 1 ? '' : 's' }} made<ng-container *ngIf="failedGroups.length">, {{ failedGroups.length }} failed</ng-container>
              <ng-container *ngIf="run.executedAt"> · {{ run.executedAt | date:'dd MMM yyyy, HH:mm' }}</ng-container>
              <div data-testid="paid-totals">Paid {{ amount(run.netPaymentAmount) }} {{ run.baseCurrencyCode || '' }} for {{ run.totalItems }} invoice{{ run.totalItems === 1 ? '' : 's' }}
                <ng-container *ngIf="run.totalDiscount"> after {{ amount(run.totalDiscount) }} discount</ng-container> — what the batches actually paid.</div></div></div>
          <div class="gl-note" *ngIf="run.removedItems.length" data-testid="settled"><i class="pi pi-info-circle"></i>
            <div>Not paid by this run (settled meanwhile or removed):
              <span *ngFor="let i of run.removedItems; let last = last"><span class="sf-mono">{{ i.invoiceNumber }}</span><ng-container *ngIf="i.removedReason"> ({{ i.removedReason }})</ng-container>{{ last ? '.' : ', ' }}</span></div></div>
          <div class="gl-scroll"><table class="aa-table" style="min-width:36rem;margin-top:.5rem" data-testid="batches"><tbody>
            <tr *ngFor="let g of run.vendorGroups"><td>{{ g.vendor.name }}</td><td><span class="sf-pill" [ngClass]="tone(g.batchStatus)">{{ g.batchStatus || '' }}</span></td>
              <td><a *ngIf="g.paymentUuid" class="sf-mono" [routerLink]="['/portal/pages/finance/payments', g.paymentUuid]">{{ g.paymentNumber || 'payment' }}</a>
                <span class="aa-bad sf-sm" *ngIf="g.failureReason"> {{ g.failureReason }}</span></td>
              <td class="gl-num">{{ amount(g.netAmount) }} <span class="sf-mu sf-sm">{{ g.currencyCode || '' }}</span>
                <div class="sf-mu sf-sm" *ngIf="g.bankDetailsMasked">{{ g.bankDetailsMasked }}</div></td></tr>
          </tbody></table></div>
        </ng-container>
      </section>
      <div class="gl-note er" *ngIf="run.status === 'Cancelled'" data-testid="cancelled"><i class="pi pi-ban"></i><div>Cancelled<ng-container *ngIf="run.cancelReason">: {{ run.cancelReason }}</ng-container>. Its invoices are free for another run.</div></div>
    </ng-container>
  </div>
</sf-page>

<p-dialog [visible]="reasonFor !== null" (visibleChange)="$event ? null : reasonFor = null" [modal]="true" [header]="reasonFor === 'cancel' ? 'Cancel payment run' : 'Remove from run'"
          [style]="{ width: '28rem', maxWidth: '96vw' }" styleClass="sdlg">
  <div class="aa-dlg-body">
    <div class="sf-mu" *ngIf="removing">{{ removing.invoiceNumber }} · {{ amount(removing.netPaymentAmount) }}</div>
    <div class="sf-fld"><label for="pr-reason" class="sf-req">Reason</label><textarea pTextarea id="pr-reason" rows="3" [(ngModel)]="reason" maxlength="200" data-testid="reason"></textarea></div>
    <div class="gl-note er" *ngIf="dialogError" data-testid="dialog-error"><i class="pi pi-exclamation-triangle"></i><div>{{ dialogError }}</div></div>
  </div>
  <ng-template pTemplate="footer">
    <p-button label="Back" [text]="true" severity="secondary" (onClick)="reasonFor = null"></p-button>
    <p-button [label]="reasonFor === 'cancel' ? 'Cancel run' : 'Remove'" severity="danger" [disabled]="busy" [loading]="busy" (onClick)="submitReason()" data-testid="confirm-reason"></p-button>
  </ng-template>
</p-dialog>`
})
export class PaymentRunWizardComponent implements OnInit, OnDestroy {
  private readonly api = inject(ArApService);
  private readonly partners = inject(BusinessPartnerService);
  private readonly auth = inject(AuthService);
  private readonly confirm = inject(ConfirmationService);
  private readonly messages = inject(MessageService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute, { optional: true });

  readonly steps = RUN_STEPS;
  readonly listRoute = PAYMENT_RUNS_ROUTE;
  readonly priorityOptions = PRIORITY_OPTIONS;
  readonly methodOptions = [{ label: 'Any method', value: null as string | null }, ...AP_PAYMENT_METHODS.map(m => ({ label: labelOf(m), value: m as string | null }))];
  readonly todayIso = today();

  run: PaymentRunDetail | null = null;
  step = 1;
  loading = false;
  error = '';
  busy = false;
  executing = false;
  exporting = false;

  // Step 1
  paymentDate = today();
  cutoff = addDays(today(), 7);
  method: string | null = null;
  vendor: BusinessPartnerModel | string | null = null;
  vendorSuggestions: BusinessPartnerModel[] = [];
  bankAccount = '';
  minPriority: PaymentPriority | null = null;
  includeDiscount = true;
  criteriaError = '';

  // Remove / cancel dialog
  reasonFor: 'remove' | 'cancel' | null = null;
  removing: PaymentRunItem | null = null;
  reason = '';
  dialogError = '';

  private poll: Subscription | null = null;
  private readonly destroy$ = new Subject<void>();
  private destroyed = false;

  get canPropose(): boolean { return this.auth.hasPermission(AR_AP_PERMS.PAYMENT_RUN_PROPOSE); }
  get canEdit(): boolean { return this.auth.hasPermission(AR_AP_PERMS.PAYMENT_RUN_EDIT); }
  get canApprove(): boolean { return this.auth.hasPermission(AR_AP_PERMS.PAYMENT_RUN_APPROVE); }
  /** §8 — executing and the CSV export both need PAYMENT_RUN_EXECUTE. */
  get canExecute(): boolean { return this.auth.hasPermission(AR_AP_PERMS.PAYMENT_RUN_EXECUTE); }
  get canCancel(): boolean { return this.auth.hasPermission(AR_AP_PERMS.PAYMENT_RUN_ADMIN); }
  get editable(): boolean { return runEditable(this.run?.status); }
  /** §8.1 — also an Executing run with no batch paid yet (409 otherwise: Resume instead). */
  get cancellable(): boolean {
    return runCancellable(this.run?.status) || (this.run?.status === 'Executing' && !this.progress.paid);
  }
  get progress() { return runProgress(this.run); }
  /** Polls in a row with no batch finished — after ~12 s the stuck-run help shows. */
  private unchangedPolls = 0;
  private lastDone = -1;
  get stalled(): boolean { return this.run?.status === 'Executing' && this.unchangedPolls >= 4; }
  resuming = false;
  get totals() { return runTotals(this.run); }
  get blocked() { return blockedRows(this.run); }
  get paidGroups(): PaymentRunVendorGroup[] { return (this.run?.vendorGroups ?? []).filter(g => g.batchStatus === 'Paid'); }
  get failedGroups(): PaymentRunVendorGroup[] { return (this.run?.vendorGroups ?? []).filter(g => g.batchStatus === 'Failed'); }
  /** The furthest step the run's status reaches. */
  get maxStep(): number { return this.run ? stepForStatus(this.run.status) : 1; }
  get stepTitle(): string { return RUN_STEPS.find(s => s.step === this.step)?.title ?? ''; }

  ngOnInit(): void {
    this.route?.paramMap?.subscribe(pm => {
      const uuid = pm.get('uuid');
      if (uuid) this.load(uuid, true);
      else { this.run = null; this.step = 1; }
    });
  }

  ngOnDestroy(): void {
    this.destroyed = true;
    this.stopPoll();
    this.destroy$.next();
    this.destroy$.complete();
  }

  /** `keepError`: a reload after a failed action keeps that action's message. Cancelled when the page goes. */
  load(uuid: string, jump = false, keepError = false): void {
    this.loading = !this.run;
    if (!keepError) this.error = '';
    this.api.getPaymentRun(uuid).pipe(takeUntil(this.destroy$)).subscribe({
      next: r => {
        this.loading = false;
        this.run = r;
        if (jump || this.step > this.maxStep) this.step = this.maxStep;
        this.trackProgress();
        if (r.status === 'Executing') this.startPoll(); else this.stopPoll();
      },
      error: err => { this.loading = false; this.error = arApError(err, 'The payment run could not be loaded.'); }
    });
  }

  /** A step can be opened once reached; step 1 only for a new run. Step 3 also from review (approval). */
  canGo(s: number): boolean {
    if (!this.run) return s === 1;
    if (s === 1) return false;
    if (s === 3 && this.editable) return true;
    return s <= this.maxStep;
  }

  go(s: number): void { if (this.canGo(s)) this.step = s; }

  searchVendors(e: AutoCompleteCompleteEvent): void {
    this.partners.getPartners({ isVendor: true, active: true, search: e.query, pageSize: 20 }).pipe(catchError(() => of(null)))
      .subscribe(r => this.vendorSuggestions = r?.result?.data ?? []);
  }

  propose(): void {
    this.criteriaError = criteriaProblem(this.paymentDate, this.cutoff) ?? '';
    if (this.criteriaError) return;
    const vendorUuid = this.vendor && typeof this.vendor === 'object' ? this.vendor.uuid ?? null : null;
    this.busy = true;
    this.error = '';
    this.api.proposePaymentRun({
      paymentDate: this.paymentDate, dueDateCutoff: this.cutoff, paymentMethod: this.method, vendorUuid, bankAccount: this.bankAccount.trim() || null,
      includeDiscountItems: this.includeDiscount, minPriority: this.minPriority
    }).subscribe({
      next: r => {
        this.busy = false;
        this.run = r;
        this.step = 2;
        this.router.navigate([PAYMENT_RUNS_ROUTE, r.uuid], { replaceUrl: true });
      },
      error: err => { this.busy = false; this.error = arApError(err, 'The payment run could not be proposed.'); }
    });
  }

  // ── Review (task 563) ──
  live(g: PaymentRunVendorGroup) { return groupLive(g); }
  removed(i: PaymentRunItem): boolean { return isRemoved(i); }

  openRemove(i: PaymentRunItem): void { this.removing = i; this.reason = ''; this.dialogError = ''; this.reasonFor = 'remove'; }
  openCancel(): void { this.removing = null; this.reason = ''; this.dialogError = ''; this.reasonFor = 'cancel'; }

  submitReason(): void {
    this.dialogError = reasonProblem(this.reason, this.reasonFor === 'remove' ? 'A removal needs a reason.' : undefined) ?? '';
    if (this.dialogError || !this.run) return;
    const reason = this.reason.trim();
    this.busy = true;
    const call = this.reasonFor === 'cancel' ? this.api.cancelPaymentRun(this.run.uuid, reason) : this.api.removePaymentRunItem(this.run.uuid, this.removing!.uuid, reason);
    const cancel = this.reasonFor === 'cancel';
    call.subscribe({
      next: r => { this.busy = false; this.reasonFor = null; this.run = r; if (cancel) this.toast('success', `${r.runNumber} is cancelled.`); },
      error: err => { this.busy = false; this.dialogError = arApError(err, cancel ? 'The run could not be cancelled.' : 'The item could not be removed.'); }
    });
  }

  restore(i: PaymentRunItem): void {
    if (!this.run) return;
    this.busy = true;
    this.api.restorePaymentRunItem(this.run.uuid, i.uuid).subscribe({
      next: r => { this.busy = false; this.run = r; },
      error: err => { this.busy = false; this.toast('error', arApError(err, 'The item could not be restored.')); }
    });
  }

  // ── Approve / execute (tasks 565 / 566) ──
  confirmApprove(): void {
    const t = this.totals;
    this.confirm.confirm({
      key: 'pr', header: 'Approve payment run', icon: 'pi pi-check', acceptLabel: 'Approve', rejectLabel: 'Cancel',
      message: `Approve ${t.vendors} vendor payment${t.vendors === 1 ? '' : 's'} for ${t.items} invoices, ${money(t.net)} net (${money(t.discount)} discount)?`,
      accept: () => this.approve()
    });
  }

  approve(): void {
    if (!this.run) return;
    this.busy = true;
    this.api.approvePaymentRun(this.run.uuid).subscribe({
      next: r => { this.busy = false; this.run = r; this.toast('success', `${r.runNumber} is approved.`); this.step = 4; },
      error: err => { this.busy = false; this.error = arApError(err, 'The payment run could not be approved.'); }
    });
  }

  confirmExecute(): void {
    const t = this.totals;
    this.confirm.confirm({
      key: 'pr', header: 'Execute payment run', icon: 'pi pi-send', acceptLabel: 'Execute', rejectLabel: 'Cancel',
      message: `Create ${t.vendors} supplier payment${t.vendors === 1 ? '' : 's'} totalling ${money(t.net)} dated ${this.run?.paymentDate}? This cannot be undone from here.`,
      accept: () => this.execute()
    });
  }

  execute(): void {
    if (!this.run) return;
    this.runExecution(this.api.executePaymentRun(this.run.uuid), 'The payment run could not be executed.');
  }

  /** §8.1 / D-AP-13 — a run left Executing (the job stopped) carries on with its Pending batches. */
  confirmResume(): void {
    this.confirm.confirm({
      key: 'pr', header: 'Resume payment run', icon: 'pi pi-replay', acceptLabel: 'Resume', rejectLabel: 'Back',
      message: `Carry on with the ${this.progress.total - this.progress.done} batch(es) still pending? Paid and failed batches are left as they are.`,
      accept: () => { if (this.run) { this.resuming = true; this.runExecution(this.api.resumePaymentRun(this.run.uuid), 'The payment run could not be resumed.'); } }
    });
  }

  /** 200 → the run is done; 202 → it executes in the background: poll GET until Completed, batches shown as they finish. */
  private runExecution(call: Observable<PaymentRunExecution>, fallback: string): void {
    const uuid = this.run!.uuid;
    this.busy = true;
    this.executing = true;
    this.error = '';
    call.pipe(takeUntil(this.destroy$)).subscribe({
      next: x => {
        this.busy = false; this.executing = false; this.resuming = false; this.run = x.run;
        this.step = Math.max(this.step, this.maxStep);
        this.unchangedPolls = 0; this.lastDone = -1; this.trackProgress();
        if (x.accepted || x.run.status === 'Executing') this.startPoll();
        else { this.stopPoll(); this.completed(x.run); }
      },
      error: err => {
        this.busy = false; this.executing = false; this.resuming = false;
        this.error = arApError(err, fallback);
        this.load(uuid, false, true);
      }
    });
  }

  private trackProgress(): void {
    const done = this.progress.done;
    if (this.run?.status !== 'Executing') { this.unchangedPolls = 0; this.lastDone = done; return; }
    this.unchangedPolls = done === this.lastDone ? this.unchangedPolls + 1 : 0;
    this.lastDone = done;
  }

  /**
   * Review fix — one poll at a time: every 3 s a GET, never overlapping (exhaustMap), stopped by the page going
   * (takeUntil destroy$), by the run leaving Executing (one completion toast) or by a failed read (its error shown).
   */
  private startPoll(): void {
    if (this.poll || !this.run || this.destroyed) return;
    const uuid = this.run.uuid;
    this.poll = timer(3000, 3000).pipe(
      exhaustMap(() => this.api.getPaymentRun(uuid)),
      takeWhile(r => r.status === 'Executing', true),
      takeUntil(this.destroy$)
    ).subscribe({
      next: r => {
        this.run = r;
        this.step = Math.max(this.step, this.maxStep);
        this.trackProgress();
        if (r.status !== 'Executing') { this.poll = null; this.completed(r); }
      },
      error: err => { this.poll = null; this.error = arApError(err, 'The payment run could not be read; refresh the page to follow it.'); }
    });
  }

  private stopPoll(): void { this.poll?.unsubscribe(); this.poll = null; }

  /** The single completion message of an execution. */
  private completed(r: PaymentRunDetail): void {
    if (r.status === 'Completed') this.toast('success', `${r.runNumber} is executed.`);
  }

  /** The server exports Approved / Executing / Completed runs only. */
  get exportable(): boolean { return ['Approved', 'Executing', 'Completed'].includes(this.run?.status ?? ''); }

  exportCsv(): void {
    if (!this.run) return;
    const r = this.run;
    this.exporting = true;
    this.api.exportPaymentRun(r.uuid).subscribe({
      next: res => { this.exporting = false; saveDownload(res, `${r.runNumber}.csv`); },
      error: async err => { this.exporting = false; this.toast('error', await arApErrorAsync(err, 'The payment file could not be exported.')); }
    });
  }

  due(d: number | null | undefined): string { return dueText(d); }
  tone(s: string | null | undefined): string { return runTone(s); }
  label(s: string | null | undefined): string { return labelOf(s); }
  amount(n: number | null | undefined): string { return money(n); }

  private toast(severity: 'success' | 'error', detail: string): void {
    this.messages.add({ severity, summary: severity === 'success' ? 'Done' : 'Error', detail, life: severity === 'error' ? 8000 : 3000 });
  }
}
