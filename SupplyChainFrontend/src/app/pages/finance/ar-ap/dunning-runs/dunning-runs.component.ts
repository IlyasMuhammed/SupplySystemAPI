import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { DialogModule } from 'primeng/dialog';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { ToastModule } from 'primeng/toast';
import { ConfirmationService, MessageService } from 'primeng/api';
import { catchError, of } from 'rxjs';

import {
  AR_AP_PERMS, ArApService, CREDIT_READ, CREDIT_ROUTE, DUNNING_PROCEDURES_ROUTE, DunningRunDetail, DunningRunItem, DunningRunSummary
} from '../../../../services/ar-ap.service';
import { AuthService } from '../../../service/auth.service';
import { FLOW, flowStagesFrom } from '../../../../shared/flow';
import { arApError, money, reasonProblem, runTone, today } from '../ar-ap.shared';

/** REV-10 — DunningService.StuckAfter. */
export const STUCK_AFTER_MS = 15 * 60 * 1000;

/**
 * A48 C5, task 580 — `/finance/ar/dunning/runs`: preview a run for a date (a Draft run with the customers due a notice,
 * their next level, overdue amount and action), execute it (emails sent, charges raised, holds placed — BR-DN-*),
 * then the results per customer. Below, the run history; a completed run is read-only (BR-DN-008) and only one run a
 * day is allowed (BR-DN-010, the server's refusal is shown). Preview / execute need DUNNING_EXECUTE.
 */
@Component({
  selector: 'app-dunning-runs',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule, ButtonModule, ConfirmDialogModule, DialogModule, InputTextModule, TextareaModule, ToastModule, ...FLOW],
  providers: [MessageService, ConfirmationService],
  styleUrls: ['../../gl/gl.scss', '../ar-ap.scss'],
  template: `
<p-toast></p-toast>
<p-confirmDialog key="dun" [style]="{ width: '30rem', maxWidth: '96vw' }"></p-confirmDialog>
<sf-page [crumbs]="['Finance', 'Receivables', 'Dunning runs', run?.runNumber]">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button *ngIf="canExecute && (!run || run.status !== 'Draft')" label="Preview run" icon="pi pi-eye" [disabled]="busy" [loading]="previewing" (onClick)="preview()" data-testid="preview"></p-button>
    <p-button *ngIf="canExecute && run?.status === 'Draft'" label="Execute run" icon="pi pi-send" [disabled]="busy || !pendingCount" [loading]="executing"
              (onClick)="confirmExecute()" data-testid="execute"></p-button>
    <p-button *ngIf="run" label="Close" icon="pi pi-times" [text]="true" (onClick)="run = null" data-testid="close-run"></p-button>
    <p-button *ngIf="canExecute && stuck" label="Resume run" icon="pi pi-replay" [disabled]="busy" [loading]="executing" (onClick)="confirmExecute()" data-testid="resume"></p-button>
    <ng-container *ngIf="canExecute && (run?.status === 'Draft' || stuck)">
      <span class="sf-sep"></span>
      <p-button label="Cancel run" icon="pi pi-ban" severity="danger" [text]="true" [disabled]="busy" (onClick)="confirmCancel()" data-testid="cancel-run"></p-button>
    </ng-container>
    <span class="sf-end"><a class="p-button p-button-text" [routerLink]="proceduresRoute"><i class="pi pi-cog"></i>&nbsp;Procedures</a></span>
  </div>
  <div class="sf-body">
    <div class="sf-lr-head"><div><h1>Dunning runs</h1><div class="sf-sub">Remind overdue customers, level by level — one run per day</div></div></div>
    <div class="gl-note er" *ngIf="error" data-testid="error"><i class="pi pi-exclamation-triangle"></i><div>{{ error }}</div></div>

    <section class="sf-card" *ngIf="canExecute && !run">
      <div class="sf-fields"><div class="sf-fld"><label for="dr-asof">As of</label>
        <input pInputText id="dr-asof" type="date" [(ngModel)]="asOfDate" data-testid="as-of" /></div></div>
    </section>

    <ng-container *ngIf="run as r">
      <header class="sf-hdr sf-compact">
        <div class="sf-t"><div><div class="sf-kicker">Dunning run<ng-container *ngIf="r.asOfDate"> · as of {{ r.asOfDate | date:'dd MMM yyyy' }}</ng-container></div>
          <h1><span class="sf-mono">{{ r.runNumber || (r.runDate | date:'dd MMM yyyy') }}</span> <span class="sf-pill" [ngClass]="tone(r.status)" data-testid="run-status">{{ r.status }}</span></h1></div></div>
        <sf-stages [stages]="stages"></sf-stages>
      </header>
      <div class="sf-grid sf-tiles" data-testid="run-totals">
        <div class="sf-tile"><span class="l">Customers</span><span class="v">{{ r.totalCustomers }}</span></div>
        <div class="sf-tile"><span class="l">Overdue</span><span class="v">{{ amount(r.totalAmount) }}</span></div>
        <div class="sf-tile"><span class="l">Sent</span><span class="v">{{ r.itemsSent }}</span><span class="s">{{ r.itemsSkipped }} skipped<ng-container *ngIf="r.itemsFailed"> · {{ r.itemsFailed }} failed</ng-container></span></div>
        <div class="sf-tile" *ngIf="r.autoResetCount"><span class="l">Reset to level 0</span><span class="v">{{ r.autoResetCount }}</span><span class="s">paid up since the last notice</span></div>
      </div>
      <div class="gl-note wn" *ngIf="stuck" data-testid="stuck-note"><i class="pi pi-exclamation-triangle"></i>
        <div>This run has been processing since {{ r.executedAt | date:'dd MMM yyyy, HH:mm' }} — its request probably stopped. Resume sends the notices still pending; cancel skips them.</div></div>
      <div class="gl-note" *ngIf="r.status === 'Draft'" data-testid="draft-note"><i class="pi pi-info-circle"></i>
        <div>Nothing is sent yet. Executing emails each customer at the level shown, raises any level charge and places the holds the procedure asks for.</div></div>
      <section class="sf-card p0">
        <div class="gl-scroll">
          <table class="aa-table" style="min-width:52rem" data-testid="run-items">
            <thead><tr><th>Customer</th><th>Level</th><th class="gl-num">Overdue</th><th class="gl-num">Documents</th><th>Oldest due</th><th>Action</th><th class="gl-num">Charge</th><th>Status</th><th></th></tr></thead>
            <tbody>
              <tr *ngFor="let i of r.items" [attr.data-testid]="'item-' + i.uuid">
                <td><a *ngIf="canCredit; else nm" [routerLink]="[creditRoute, i.customerUuid]">{{ i.customerName }}</a><ng-template #nm>{{ i.customerName }}</ng-template>
                  <div class="sf-mu sf-sm" *ngIf="i.currencySummary || i.procedureCode">{{ i.procedureCode || '' }}<ng-container *ngIf="i.currencySummary"> · {{ i.currencySummary }}</ng-container></div></td>
                <td>{{ i.previousLevel }} → <b>{{ i.dunningLevel }}</b></td>
                <td class="gl-num">{{ amount(i.overdueAmount) }}</td>
                <td class="gl-num">{{ i.openItemCount }}</td>
                <td>{{ i.oldestDueDate ? (i.oldestDueDate | date:'dd MMM yyyy') : '' }}</td>
                <td>{{ i.actionType }}<span class="sf-mu sf-sm" *ngIf="i.sentTo"> · {{ i.sentTo }}</span></td>
                <td class="gl-num">{{ i.chargeAmount ? amount(i.chargeAmount) : '' }}<span class="sf-mu sf-sm" *ngIf="i.chargeDocumentNumber"> · {{ i.chargeDocumentNumber }}</span></td>
                <td><span class="sf-pill" [ngClass]="tone(i.status)">{{ i.status }}</span><span class="sf-mu sf-sm" *ngIf="i.skipReason"> {{ i.skipReason }}</span>
                  <span class="aa-bad sf-sm" *ngIf="i.failureReason"> {{ i.failureReason }}</span></td>
                <td><p-button *ngIf="canExecute && r.status === 'Draft' && i.status === 'Pending'" label="Skip" [text]="true" size="small" [disabled]="busy"
                              (onClick)="openSkip(i)" [attr.data-testid]="'skip-' + i.uuid"></p-button></td>
              </tr>
              <tr *ngIf="!r.items.length"><td colspan="9" class="sf-empty" data-testid="no-items">No customer is due a notice on this date.</td></tr>
            </tbody>
          </table>
        </div>
      </section>
    </ng-container>

    <section class="sf-card p0" data-testid="history">
      <div class="sf-ch sf-pad"><h2>History <span class="sf-count">{{ runs.length }}</span></h2></div>
      <div class="gl-scroll">
        <table class="aa-table" style="min-width:44rem">
          <thead><tr><th>Run</th><th>Date</th><th>Status</th><th class="gl-num">Customers</th><th class="gl-num">Overdue</th><th class="gl-num">Sent</th><th>By</th></tr></thead>
          <tbody>
            <tr *ngFor="let h of runs" class="aa-click" (click)="openRun(h)" [attr.data-testid]="'run-' + h.uuid">
              <td class="sf-mono">{{ h.runNumber || '—' }}</td><td>{{ h.runDate | date:'dd MMM yyyy' }}</td>
              <td><span class="sf-pill" [ngClass]="tone(h.status)">{{ h.status }}</span></td>
              <td class="gl-num">{{ h.totalCustomers }}</td><td class="gl-num">{{ amount(h.totalAmount) }}</td><td class="gl-num">{{ h.itemsSent }}</td>
              <td>{{ h.executedByName || '' }}</td>
            </tr>
            <tr *ngIf="!runs.length"><td colspan="7" class="sf-empty" data-testid="history-empty">No dunning run yet.</td></tr>
          </tbody>
        </table>
      </div>
      <div class="aa-actions sf-pad" *ngIf="runsTotal > runs.length"><p-button label="Show more" [text]="true" size="small" (onClick)="loadHistory(runsPage + 1)" data-testid="more"></p-button></div>
    </section>
  </div>
</sf-page>

<p-dialog [visible]="skipping !== null" (visibleChange)="$event ? null : skipping = null" [modal]="true" header="Skip customer" [style]="{ width: '28rem', maxWidth: '96vw' }" styleClass="sdlg">
  <div class="aa-dlg-body">
    <div class="sf-mu" *ngIf="skipping">{{ skipping.customerName }} · level {{ skipping.dunningLevel }} · {{ amount(skipping.overdueAmount) }}</div>
    <div class="sf-fld"><label for="dr-reason" class="sf-req">Reason</label><textarea pTextarea id="dr-reason" rows="3" [(ngModel)]="reason" maxlength="200" data-testid="reason"></textarea></div>
    <div class="gl-note er" *ngIf="dialogError" data-testid="dialog-error"><i class="pi pi-exclamation-triangle"></i><div>{{ dialogError }}</div></div>
  </div>
  <ng-template pTemplate="footer">
    <p-button label="Back" [text]="true" severity="secondary" (onClick)="skipping = null"></p-button>
    <p-button label="Skip" [disabled]="busy" [loading]="busy" (onClick)="skip()" data-testid="confirm-skip"></p-button>
  </ng-template>
</p-dialog>`
})
export class DunningRunsComponent implements OnInit {
  private readonly api = inject(ArApService);
  private readonly auth = inject(AuthService);
  private readonly confirm = inject(ConfirmationService);
  private readonly messages = inject(MessageService);

  readonly proceduresRoute = DUNNING_PROCEDURES_ROUTE;
  readonly creditRoute = CREDIT_ROUTE;

  asOfDate = today();
  run: DunningRunDetail | null = null;
  runs: DunningRunSummary[] = [];
  runsTotal = 0;
  runsPage = 1;
  error = '';
  previewing = false;
  executing = false;
  acting = false;
  skipping: DunningRunItem | null = null;
  reason = '';
  dialogError = '';

  get busy(): boolean { return this.previewing || this.executing || this.acting; }
  get canExecute(): boolean { return this.auth.hasPermission(AR_AP_PERMS.DUNNING_EXECUTE); }
  get canCredit(): boolean { return this.auth.hasAnyPermission(...CREDIT_READ); }
  /** REV-10 — a run left Processing for 15 minutes or more can be resumed (execute) or cancelled. */
  get stuck(): boolean {
    const r = this.run;
    if (r?.status !== 'Processing') return false;
    const since = Date.parse(r.executedAt ?? r.createdAt ?? '');
    return isFinite(since) && Date.now() - since >= STUCK_AFTER_MS;
  }

  get pendingCount(): number { return (this.run?.items ?? []).filter(i => i.status === 'Pending').length; }

  get stages() {
    const s = this.run?.status;
    const idx = s === 'Draft' ? 0 : s === 'Processing' ? 1 : s === 'Completed' ? 3 : 1;
    return flowStagesFrom(['Preview', 'Execute', 'Results'], idx, { failed: s === 'Cancelled' });
  }

  ngOnInit(): void { this.loadHistory(); }

  loadHistory(page = 1): void {
    this.api.getDunningRuns(page).pipe(catchError(() => of(null))).subscribe(r => {
      if (!r) return;
      const rows = [...r.data].sort((a, b) => (b.runDate ?? '').localeCompare(a.runDate ?? ''));
      this.runs = page === 1 ? rows : [...this.runs, ...rows];
      this.runsTotal = r.totalRecords;
      this.runsPage = page;
    });
  }

  openSkip(i: DunningRunItem): void { this.skipping = i; this.reason = ''; this.dialogError = ''; }

  skip(): void {
    const i = this.skipping;
    if (!i || !this.run) return;
    this.dialogError = reasonProblem(this.reason) ?? '';
    if (this.dialogError) return;
    this.acting = true;
    this.api.skipDunningItem(this.run.uuid, i.uuid, this.reason.trim()).subscribe({
      next: r => { this.acting = false; this.skipping = null; this.run = r; },
      error: err => { this.acting = false; this.dialogError = arApError(err, 'The customer could not be skipped.'); }
    });
  }

  confirmCancel(): void {
    const r = this.run;
    if (!r) return;
    this.confirm.confirm({
      key: 'dun', header: 'Cancel dunning run', icon: 'pi pi-ban', acceptLabel: 'Cancel run', rejectLabel: 'Back', acceptButtonStyleClass: 'p-button-danger',
      message: `Cancel ${r.runNumber || 'this draft run'}? Nothing was sent; a new run can be previewed for the same day.`,
      accept: () => {
        this.acting = true;
        this.api.cancelDunningRun(r.uuid).subscribe({
          next: x => { this.acting = false; this.run = x; this.loadHistory(); },
          error: err => { this.acting = false; this.error = arApError(err, 'The dunning run could not be cancelled.'); }
        });
      }
    });
  }

  preview(): void {
    if (!this.asOfDate) { this.error = 'Choose the date.'; return; }
    this.previewing = true;
    this.error = '';
    this.api.previewDunningRun(this.asOfDate).subscribe({
      next: r => { this.previewing = false; this.run = r; this.loadHistory(); },
      error: err => { this.previewing = false; this.error = arApError(err, 'The dunning run could not be previewed.'); }
    });
  }

  confirmExecute(): void {
    const r = this.run;
    if (!r) return;
    this.confirm.confirm({
      key: 'dun', header: 'Execute dunning run', icon: 'pi pi-send', acceptLabel: 'Execute', rejectLabel: 'Cancel',
      message: `Send ${this.pendingCount} notice${this.pendingCount === 1 ? '' : 's'} for ${money(r.totalAmount)} overdue? Charges and holds are applied as the procedure says. This cannot be undone.`,
      accept: () => this.execute()
    });
  }

  execute(): void {
    if (!this.run) return;
    this.executing = true;
    this.error = '';
    this.api.executeDunningRun(this.run.uuid).subscribe({
      next: r => {
        this.executing = false;
        this.run = r;
        this.messages.add({ severity: 'success', summary: 'Done', detail: `${r.itemsSent} sent, ${r.itemsSkipped} skipped${r.itemsFailed ? ', ' + r.itemsFailed + ' failed' : ''}.`, life: 4000 });
        this.loadHistory();
      },
      error: err => { this.executing = false; this.error = arApError(err, 'The dunning run could not be executed.'); }
    });
  }

  openRun(h: DunningRunSummary): void {
    this.error = '';
    this.api.getDunningRun(h.uuid).subscribe({
      next: r => this.run = r,
      error: err => this.error = arApError(err, 'The dunning run could not be loaded.')
    });
  }

  tone(s: string | null | undefined): string { return runTone(s); }
  amount(n: number | null | undefined): string { return money(n); }
}
