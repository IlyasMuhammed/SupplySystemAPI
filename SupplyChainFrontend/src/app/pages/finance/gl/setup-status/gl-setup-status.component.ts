import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { DialogModule } from 'primeng/dialog';
import { InputTextModule } from 'primeng/inputtext';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';

import { AccountingService, GL_PERMS, GL_ROUTE, GL_SETUP_ROUTE, SetupHistoryEntry, SetupStatus } from '../../../../services/accounting.service';
import { AuthService } from '../../../service/auth.service';
import { FLOW, flowLabel } from '../../../../shared/flow';
import { WIZARD_STEPS, glError, setupStatusLabel, setupStatusTone, stepDone } from '../gl.shared';

/** The word the reset confirmation asks to be typed. */
export const RESET_WORD = 'RESET';

/**
 * A42 (0432–0467 UI) — `/gl/setup-status`: the step checklist, status, conversion date, who completed it, the history
 * (GlSetupHistory, newest first) and Reset with a typed confirmation (D-17).
 */
@Component({
  selector: 'app-gl-setup-status',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule, ButtonModule, DialogModule, InputTextModule, ToastModule, ...FLOW],
  styleUrls: ['../gl.scss'],
  template: `
<p-toast></p-toast>
<sf-page [crumbs]="['Finance', 'General Ledger', 'Setup Status']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button [label]="status?.status === 'Complete' ? 'Open setup' : 'Continue setup'" icon="pi pi-arrow-right" [routerLink]="[setupRoute]"
              data-testid="to-wizard"></p-button>
    <span class="sf-sep"></span>
    <p-button *ngIf="canManage" label="Reset setup" icon="pi pi-replay" [text]="true" severity="danger" (onClick)="openReset()"
              [disabled]="!status || status.status === 'NotStarted'" data-testid="reset"></p-button>
    <p-button icon="pi pi-refresh" [text]="true" (onClick)="load()" ariaLabel="Refresh"></p-button>
  </div>

  <header class="sf-hdr">
    <div class="sf-t"><div>
      <div class="sf-kicker">General ledger</div>
      <h1>Setup status <span class="sf-pill" *ngIf="status" [ngClass]="tone(status.status)" data-testid="status">{{ label(status.status) }}</span></h1>
      <div class="sf-meta" *ngIf="status">
        <ng-container *ngIf="status.templateName">Template {{ status.templateName }}<ng-container *ngIf="status.templateAppliedAt"> · applied {{ status.templateAppliedAt | date:'d MMM y' }}</ng-container> · </ng-container>
        {{ status.accountsCreatedCount | number }} accounts
      </div>
    </div></div>
    <div class="sf-facts" *ngIf="status">
      <div class="sf-kpi"><span class="l">Conversion date</span><span class="v" data-testid="conversion-date">{{ status.conversionDate ? (status.conversionDate | date:'d MMM y') : '—' }}</span></div>
      <div class="sf-kpi"><span class="l">Completed</span>
        <span class="v" data-testid="completed">{{ status.setupCompletedAt ? (status.setupCompletedAt | date:'d MMM y') : '—' }}</span>
        <span class="s" *ngIf="status.setupCompletedByName" data-testid="completed-by">by {{ status.setupCompletedByName }}</span></div>
      <div class="sf-kpi"><span class="l">Opening balances</span>
        <span class="v">{{ status.openingBalancesEntered ? (status.openingBalancesBalanced ? 'Balanced' : 'Posted with difference') : 'Not entered' }}</span></div>
    </div>
  </header>

  <div class="sf-body">
    <div class="sf-card sf-empty" *ngIf="loading && !status"><i class="pi pi-spin pi-spinner"></i> Loading…</div>
    <div class="gl-note er" *ngIf="loadError" data-testid="load-error"><i class="pi pi-exclamation-triangle"></i><div>{{ loadError }}</div></div>

    <section class="sf-card" *ngIf="status">
      <div class="sf-ch"><h2>Steps</h2></div>
      <ul class="gl-checklist" data-testid="checklist">
        <li *ngFor="let s of steps" [class.done]="done(s.step)" [attr.data-testid]="'check-' + s.step">
          <span class="st"><i class="pi" [ngClass]="done(s.step) ? 'pi-check-circle' : 'pi-circle'"></i></span>
          <span class="grow">{{ s.step }}. {{ s.title }}</span>
          <span class="sf-pill" [ngClass]="done(s.step) ? 'ok' : (s.step === status.nextStep ? 'in' : '')">{{ done(s.step) ? 'Done' : (s.step === status.nextStep ? 'Next' : 'To do') }}</span>
        </li>
      </ul>
    </section>

    <section class="sf-card">
      <div class="sf-ch"><h2>History</h2></div>
      <div class="sf-empty" *ngIf="!history.length">No setup activity yet.</div>
      <div class="sf-tl" *ngIf="history.length" data-testid="history">
        <div class="sf-tl-e" *ngFor="let h of history" data-testid="history-entry">
          <div><strong>{{ eventLabel(h.eventType) }}</strong> — {{ h.summary }}</div>
          <div class="sf-mu sf-sm">{{ h.at | date:'d MMM y, HH:mm' }}<ng-container *ngIf="h.userName"> · {{ h.userName }}</ng-container></div>
        </div>
      </div>
    </section>
  </div>
</sf-page>

<p-dialog [(visible)]="resetVisible" [modal]="true" [draggable]="false" header="Reset general ledger setup" [style]="{ width: '34rem' }"
          [breakpoints]="{ '640px': '96vw' }">
  <div class="gl-note er"><i class="pi pi-exclamation-triangle"></i>
    <div>This deletes the chart of accounts, the fiscal years and periods, the default assignments, the opening balances and their journal,
      and the external mappings. The setup starts again from step 1. It is refused once journal entries have been posted.</div></div>
  <div class="sf-fld" style="margin-top:.75rem">
    <label for="reset-word">Type <strong>{{ resetWord }}</strong> to confirm</label>
    <input pInputText id="reset-word" [(ngModel)]="typed" autocomplete="off" data-testid="reset-word" />
  </div>
  <div class="sf-sm" style="color: var(--sms-danger); margin-top:.5rem" *ngIf="resetError" data-testid="reset-error">{{ resetError }}</div>
  <ng-template pTemplate="footer">
    <p-button label="Cancel" [text]="true" (onClick)="resetVisible = false"></p-button>
    <p-button label="Reset everything" icon="pi pi-replay" severity="danger" [disabled]="typed.trim() !== resetWord || resetting" [loading]="resetting"
              (onClick)="reset()" data-testid="reset-confirm"></p-button>
  </ng-template>
</p-dialog>`
})
export class GlSetupStatusComponent implements OnInit {
  private readonly api = inject(AccountingService);
  private readonly auth = inject(AuthService);
  private readonly messages = inject(MessageService, { optional: true });

  readonly steps = [...WIZARD_STEPS.slice(0, 5), { step: 6, title: 'Finish setup', short: 'Finish' }];
  readonly setupRoute = GL_SETUP_ROUTE;
  readonly glRoute = GL_ROUTE;
  readonly label = setupStatusLabel;
  readonly tone = setupStatusTone;
  readonly resetWord = RESET_WORD;

  status: SetupStatus | null = null;
  history: SetupHistoryEntry[] = [];
  loading = false;
  loadError = '';
  resetVisible = false;
  typed = '';
  resetting = false;
  resetError = '';

  get canManage(): boolean { return this.auth.hasPermission(GL_PERMS.SETUP_MANAGE); }

  done(step: number): boolean { return stepDone(this.status, step); }

  eventLabel(t: string): string { return flowLabel(t?.replace(/([a-z])([A-Z])/g, '$1_$2').toUpperCase() ?? ''); }

  ngOnInit(): void { this.load(); }

  load(): void {
    this.loading = true;
    this.loadError = '';
    this.api.getSetupStatus().subscribe({
      next: r => { this.loading = false; this.status = r?.result ?? null; },
      error: err => { this.loading = false; this.loadError = glError(err, 'The setup status could not be loaded.'); }
    });
    this.api.getSetupHistory().subscribe({ next: r => this.history = r?.result ?? [], error: () => this.history = [] });
  }

  openReset(): void {
    this.typed = '';
    this.resetError = '';
    this.resetVisible = true;
  }

  reset(): void {
    if (this.typed.trim() !== RESET_WORD || this.resetting) return;
    this.resetting = true;
    this.resetError = '';
    this.api.resetSetup().subscribe({
      next: r => {
        this.resetting = false;
        this.resetVisible = false;
        if (r?.result) this.status = r.result;
        this.messages?.add({ severity: 'success', summary: 'Reset', detail: 'The general ledger setup was reset.', life: 4000 });
        this.load();
      },
      error: err => { this.resetting = false; this.resetError = glError(err, 'The setup could not be reset.'); }
    });
  }
}
