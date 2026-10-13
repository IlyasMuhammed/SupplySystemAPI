import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { ToastModule } from 'primeng/toast';
import { ConfirmationService, MessageService } from 'primeng/api';
import { catchError, forkJoin, of } from 'rxjs';

import {
  AccountingDefaults, AccountingService, FiscalYear, GL_PERMS, GL_ROUTE, OpeningSummary, SetupStatus
} from '../../../../services/accounting.service';
import { AuthService } from '../../../service/auth.service';
import { FLOW } from '../../../../shared/flow';
import { WIZARD_STEPS, glError, setupStatusLabel, setupStatusTone, stepDone, unmetList } from '../gl.shared';
import { GlHelpComponent } from '../gl-help.component';
import { GlTemplateStepComponent } from './gl-template-step.component';
import { GlAccountsComponent } from '../accounts/gl-accounts.component';
import { GlFiscalYearsComponent } from '../fiscal-years/gl-fiscal-years.component';
import { GlDefaultsComponent } from '../defaults/gl-defaults.component';
import { GlOpeningBalancesComponent } from '../opening-balances/gl-opening-balances.component';

/** The furthest step the person may open: every step up to the first one not done (all when Complete). */
export function maxReachableStep(s: SetupStatus | null): number {
  if (!s) return 1;
  if (s.status === 'Complete') return 6;
  for (let i = 1; i <= 6; i++) if (!stepDone(s, i)) return i;
  return 6;
}

/**
 * A42 Phase 5 (0485–0509) — `/gl/setup`: six steps with a stepper, Back / Continue (Continue off until the step's own
 * prerequisite is met), done steps can be revisited, and it opens on GET setup/status `nextStep`. Every step has the
 * FSD §11.3 help panel. `?gate=1` (from the dashboard guard or a FINANCE_SETUP_REQUIRED 403) shows a banner naming the next step.
 */
@Component({
  selector: 'app-gl-setup-wizard',
  standalone: true,
  imports: [CommonModule, RouterModule, ButtonModule, ConfirmDialogModule, ToastModule, ...FLOW, GlHelpComponent, GlTemplateStepComponent,
    GlAccountsComponent, GlFiscalYearsComponent, GlDefaultsComponent, GlOpeningBalancesComponent],
  providers: [ConfirmationService],
  styleUrls: ['../gl.scss'],
  template: `
<p-toast></p-toast>
<p-confirmDialog key="gl-wiz"></p-confirmDialog>
<sf-page [crumbs]="['Finance', 'General Ledger', 'Setup']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button label="Setup status" icon="pi pi-list-check" [text]="true" [routerLink]="[route, 'setup-status']" data-testid="to-status"></p-button>
    <p-button *ngIf="status?.status === 'Complete'" label="GL dashboard" icon="pi pi-chart-bar" [text]="true" [routerLink]="[route]"></p-button>
  </div>

  <header class="sf-hdr">
    <div class="sf-t"><div>
      <div class="sf-kicker">General ledger</div>
      <h1>Set up the general ledger
        <span class="sf-pill" *ngIf="status" [ngClass]="tone(status.status)" data-testid="status">{{ label(status.status) }}</span></h1>
      <div class="sf-meta">Chart of accounts, fiscal year, default accounts and opening balances — in six steps.</div>
    </div></div>
    <nav class="gl-steps" aria-label="Setup steps" data-testid="stepper">
      <button *ngFor="let s of steps" type="button" [class.done]="isDone(s.step) && s.step !== step" [class.on]="s.step === step"
              [disabled]="s.step > maxStep" (click)="go(s.step)" [attr.aria-current]="s.step === step ? 'step' : null"
              [attr.data-testid]="'step-' + s.step">
        <b>{{ isDone(s.step) ? '✓ ' : '' }}{{ s.step }}. {{ s.short }}</b><span>{{ isDone(s.step) ? 'done' : s.step === maxStep ? 'next' : '' }}</span>
      </button>
    </nav>
  </header>

  <div class="sf-body">
    <div class="gl-note wn" *ngIf="gate && status?.status !== 'Complete'" data-testid="gate-banner">
      <i class="pi pi-lock"></i>
      <div>The general ledger is not set up yet, so the GL dashboard and postings are closed. Next step:
        <strong>{{ nextTitle }}</strong>.</div>
    </div>
    <div class="gl-note" *ngIf="!canSetup" data-testid="read-only"><i class="pi pi-eye"></i><div>You can view the setup. Changing it needs the "Manage GL setup" permission.</div></div>
    <div class="sf-card sf-empty" *ngIf="loading && !status"><i class="pi pi-spin pi-spinner"></i> Loading…</div>
    <div class="gl-note er" *ngIf="loadError" data-testid="load-error"><i class="pi pi-exclamation-triangle"></i><div>{{ loadError }}</div></div>

    <ng-container *ngIf="status">
      <div class="sf-card">
        <div class="sf-ch" style="margin-bottom:0"><div><h2 data-testid="step-title">Step {{ step }} of 6 — {{ steps[step - 1].title }}</h2></div></div>
        <app-gl-help [step]="step"></app-gl-help>
      </div>

      <ng-container [ngSwitch]="step">
        <app-gl-template-step *ngSwitchCase="1" [canManage]="canSetup" [locked]="templateLocked" [appliedTemplateUuid]="status.templateUuid"
                              [existingAccounts]="status.accountsCreatedCount" (applied)="onApplied()"></app-gl-template-step>
        <app-gl-accounts *ngSwitchCase="2" [embedded]="true" (changed)="refresh()"></app-gl-accounts>
        <app-gl-fiscal-years *ngSwitchCase="3" [embedded]="true" (changed)="refresh()"></app-gl-fiscal-years>
        <app-gl-defaults *ngSwitchCase="4" [embedded]="true" (changed)="refresh()"></app-gl-defaults>
        <app-gl-opening-balances *ngSwitchCase="5" [embedded]="true" (changed)="refresh()" (posted)="onPosted()"></app-gl-opening-balances>

        <ng-container *ngSwitchCase="6">
          <div class="gl-note ok" *ngIf="status.status === 'Complete'" data-testid="complete-note">
            <i class="pi pi-check-circle"></i>
            <div>Setup completed<ng-container *ngIf="status.setupCompletedAt"> on {{ status.setupCompletedAt | date:'d MMM y, HH:mm' }}</ng-container>
              <ng-container *ngIf="status.setupCompletedByName"> by {{ status.setupCompletedByName }}</ng-container>.
              <a [routerLink]="[route]">Open the GL dashboard</a></div>
          </div>
          <div class="sf-grid sf-tiles" data-testid="review">
            <div class="sf-tile"><span class="l">Accounts</span><span class="v" data-testid="r-accounts">{{ status.accountsCreatedCount | number }}</span>
              <span class="s">{{ status.templateName || 'own chart' }}</span></div>
            <div class="sf-tile"><span class="l">Fiscal year</span><span class="v" data-testid="r-year">{{ firstYear?.fiscalYearName || (status.fiscalYearConfigured ? 'Set' : '—') }}</span>
              <span class="s" *ngIf="firstYear">{{ firstYear.startDate | date:'d MMM y' }} – {{ firstYear.endDate | date:'d MMM y' }}</span></div>
            <div class="sf-tile"><span class="l">Required defaults</span>
              <span class="v" data-testid="r-defaults">{{ defaults ? defaults.assignedRequiredCount + ' / ' + defaults.requiredCount : (status.defaultsConfigured ? 'Done' : '—') }}</span></div>
            <div class="sf-tile"><span class="l">Opening balances</span>
              <span class="v" data-testid="r-opening">{{ status.openingBalancesEntered ? (opening && (opening.totalDebits || opening.totalCredits) ? 'Posted' : 'Done') : 'Not entered' }}</span>
              <span class="s" *ngIf="opening && status.openingBalancesEntered">Dr {{ opening.totalDebits | number:'1.2-2' }} · Cr {{ opening.totalCredits | number:'1.2-2' }}</span></div>
            <div class="sf-tile"><span class="l">Conversion date</span><span class="v" data-testid="r-date">{{ status.conversionDate ? (status.conversionDate | date:'d MMM y') : '—' }}</span></div>
          </div>
          <section class="sf-card">
            <ul class="gl-checklist">
              <li *ngFor="let s of steps.slice(0, 5)" [class.done]="isDone(s.step)">
                <span class="st"><i class="pi" [ngClass]="isDone(s.step) ? 'pi-check-circle' : 'pi-circle'"></i></span>
                <span class="grow">{{ s.title }}</span>
                <p-button *ngIf="!isDone(s.step)" label="Go" [text]="true" size="small" [disabled]="s.step > maxStep" (onClick)="go(s.step)"></p-button>
              </li>
            </ul>
          </section>
          <div class="gl-note er" *ngIf="finalizeError" data-testid="finalize-error">
            <i class="pi pi-times-circle"></i><div>{{ finalizeError }}<ul *ngIf="unmet.length"><li *ngFor="let u of unmet">{{ u }}</li></ul></div>
          </div>
        </ng-container>
      </ng-container>
    </ng-container>
  </div>

  <div class="sf-fbar" *ngIf="status">
    <span class="sf-mu sf-sm" data-testid="footer-hint">{{ hint }}</span>
    <span class="sf-end">
      <p-button label="Back" icon="pi pi-arrow-left" [text]="true" [disabled]="step === 1" (onClick)="back()" data-testid="back"></p-button>
      <p-button *ngIf="step < 6" label="Continue" icon="pi pi-arrow-right" iconPos="right" [disabled]="!canContinue" (onClick)="next()"
                data-testid="continue"></p-button>
      <p-button *ngIf="step === 6 && status.status !== 'Complete' && canSetup" label="Finish setup" icon="pi pi-check" [disabled]="!canFinalize"
                [loading]="finalizing" (onClick)="confirmFinalize()" data-testid="finalize"></p-button>
    </span>
  </div>
</sf-page>`
})
export class GlSetupWizardComponent implements OnInit {
  private readonly api = inject(AccountingService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly routeInfo = inject(ActivatedRoute);
  private readonly confirm = inject(ConfirmationService);
  private readonly messages = inject(MessageService, { optional: true });

  readonly route = GL_ROUTE;
  readonly steps = WIZARD_STEPS;
  readonly label = setupStatusLabel;
  readonly tone = setupStatusTone;

  status: SetupStatus | null = null;
  step = 1;
  loading = false;
  loadError = '';
  gate = false;
  finalizing = false;
  finalizeError = '';
  unmet: string[] = [];
  firstYear: FiscalYear | null = null;
  defaults: AccountingDefaults | null = null;
  opening: OpeningSummary | null = null;

  get canSetup(): boolean { return this.auth.hasPermission(GL_PERMS.SETUP_MANAGE); }
  get maxStep(): number { return maxReachableStep(this.status); }
  get canContinue(): boolean { return this.isDone(this.step) && this.step < 6; }
  get canFinalize(): boolean { return [1, 2, 3, 4, 5].every(s => this.isDone(s)) && !this.finalizing; }
  get nextTitle(): string { return this.steps[(this.status?.nextStep ?? 1) - 1]?.title ?? ''; }

  /** D-16 / BR-TPL-004 — the chart is replaceable only before the fiscal year is set and before opening balances are posted. */
  get templateLocked(): boolean {
    const s = this.status;
    if (!s) return true;
    return !(s.status === 'NotStarted' || s.status === 'AccountsCreated') || !!s.openingBalancesEntered;
  }

  get hint(): string {
    if (this.step === 6) return this.canFinalize ? 'Everything is in place — finish to open the general ledger.' : 'Finish the open steps first.';
    if (this.isDone(this.step)) return `Step ${this.step} of 6 is done.`;
    switch (this.step) {
      case 1: case 2: return 'Apply a template or upload a chart to continue.';
      case 3: return 'Create the first fiscal year to continue.';
      case 4: return 'Assign and save every required default to continue.';
      case 5: return 'Post the opening balances (or skip them for a new company) to continue.';
      default: return '';
    }
  }

  isDone(step: number): boolean { return stepDone(this.status, step); }

  ngOnInit(): void {
    const q = this.routeInfo.snapshot?.queryParamMap;
    this.gate = q?.get('gate') === '1';
    this.loading = true;
    this.api.getSetupStatus().subscribe({
      next: r => {
        this.loading = false;
        this.status = r?.result ?? null;
        const s = this.status;
        const requested = Number(q?.get('step'));
        this.step = s?.status === 'Complete' ? 6 : this.clamp(requested || s?.nextStep || 1);
        if (this.step === 6) this.loadReview();
      },
      error: err => { this.loading = false; this.loadError = glError(err, 'The setup status could not be loaded.'); }
    });
  }

  private clamp(n: number): number { return Math.max(1, Math.min(n, this.maxStep, 6)); }

  /** Re-reads the status after a step changed something (Continue follows it). */
  refresh(): void {
    this.api.getSetupStatus().subscribe({ next: r => { if (r?.result) this.status = r.result; }, error: () => {} });
  }

  go(n: number): void {
    if (n < 1 || n > 6 || n > this.maxStep) return;
    this.step = n;
    this.finalizeError = '';
    if (n === 6) this.loadReview();
  }

  next(): void { if (this.canContinue) this.go(this.step + 1); }
  back(): void { if (this.step > 1) this.go(this.step - 1); }

  onApplied(): void {
    this.api.getSetupStatus().subscribe({ next: r => { if (r?.result) { this.status = r.result; this.go(2); } }, error: () => {} });
  }

  onPosted(): void {
    this.api.getSetupStatus().subscribe({ next: r => { if (r?.result) { this.status = r.result; this.go(6); } }, error: () => {} });
  }

  loadReview(): void {
    forkJoin({
      years: this.api.getFiscalYears().pipe(catchError(() => of(null))),
      defaults: this.api.getDefaults().pipe(catchError(() => of(null))),
      opening: this.api.getOpeningSummary().pipe(catchError(() => of(null)))
    }).subscribe(r => {
      const years = [...(r.years?.result ?? [])].sort((a, b) => a.startDate.localeCompare(b.startDate));
      this.firstYear = years[0] ?? null;
      this.defaults = r.defaults?.result ?? null;
      this.opening = r.opening?.result ?? null;
    });
  }

  confirmFinalize(): void {
    if (!this.canFinalize) return;
    this.confirm.confirm({
      key: 'gl-wiz', header: 'Finish setup', icon: 'pi pi-check-circle', acceptLabel: 'Finish', rejectLabel: 'Cancel',
      message: 'Mark the general ledger ready? The root accounts and the conversion date are locked from now on.',
      accept: () => this.finalize()
    });
  }

  finalize(): void {
    this.finalizing = true;
    this.finalizeError = '';
    this.unmet = [];
    this.api.completeSetup().subscribe({
      next: r => {
        this.finalizing = false;
        if (r?.result) this.status = r.result;
        this.messages?.add({ severity: 'success', summary: 'Done', detail: 'The general ledger is set up.', life: 4000 });
        this.router.navigate([GL_ROUTE]);
      },
      error: err => {
        this.finalizing = false;
        this.finalizeError = glError(err, 'The setup could not be completed.');
        this.unmet = unmetList(err).filter(u => u !== this.finalizeError);
      }
    });
  }
}
