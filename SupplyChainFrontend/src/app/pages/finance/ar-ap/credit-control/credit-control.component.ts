import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { DialogModule } from 'primeng/dialog';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { SelectModule } from 'primeng/select';
import { TableLazyLoadEvent, TableModule } from 'primeng/table';
import { TextareaModule } from 'primeng/textarea';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';
import { Observable, catchError, of } from 'rxjs';

import {
  AR_AP_PERMS, AR_ROUTE, ArApService, CREDIT_CONTROL_MODES, CREDIT_ROUTE, CreditExposure, CreditHistoryEntry, DunningHistoryEvent,
  DunningProcedure, PartnerFinancialProfile, RISK_TIERS, RiskTier, STATEMENT_ROUTE
} from '../../../../services/ar-ap.service';
import { AuthService } from '../../../service/auth.service';
import { FLOW } from '../../../../shared/flow';
import { addDays, arApError, creditModeHelp, creditStatus, daysBetween, labelOf, money, reasonProblem, riskTone, tempLimitProblem, today } from '../ar-ap.shared';

type Modal = 'hold' | 'release' | 'temp' | 'clearTemp' | 'tier' | 'reset' | null;

export interface DunningView {
  procedureCode: string | null;
  currentLevel: number;
  maxLevel: number | null;
  lastDunningDate: string | null;
  nextLevel: number | null;
  nextDate: string | null;
  blockSalesAtLevel: number | null;
  blockApplies: boolean;
  excluded: boolean;
}

/**
 * The dunning status from the profile (level, last date, procedure, block flag) and the procedure's levels: the next
 * level is due `daysAfterPrevious` after the last notice (BR-DN-003, never skipping — BR-DN-004).
 */
export function dunningView(p: PartnerFinancialProfile | null, procedures: DunningProcedure[]): DunningView | null {
  if (!p) return null;
  const proc = procedures.find(x => x.uuid === p.dunningProcedureUuid) ?? (p.dunningProcedureUuid ? undefined : procedures.find(x => x.isDefault));
  const level = p.currentDunningLevel || 0;
  const max = proc?.levels.length ?? null;
  const next = proc && level < proc.levels.length ? proc.levels[level] : null;
  return {
    procedureCode: p.dunningProcedureCode ?? proc?.code ?? null,
    currentLevel: level,
    maxLevel: max,
    lastDunningDate: p.lastDunningDate ?? null,
    nextLevel: next?.levelNumber ?? null,
    nextDate: next && level > 0 && p.lastDunningDate ? addDays(p.lastDunningDate, next.daysAfterPrevious) : null,
    blockSalesAtLevel: proc?.blockSalesAtLevel ?? null,
    blockApplies: !!p.blockSalesAtDunning,
    excluded: !!p.excludeFromDunning
  };
}

/**
 * A48 §8.2, tasks 545–551 — `/finance/ar/credit`: the credit-control list (contract §2 `credit/exposures`, filters
 * over limit / on hold / risk tier / search) and, for FINANCE_AR_AP_ADMIN, the organization's credit control mode
 * (§1.4, Off by default — D-5). `/finance/ar/credit/:uuid`: the permanent limit (customer master, D-4), temporary
 * limit + expiry, exposure = open orders + open invoices (base, D-13), available credit, hold, risk tier, dunning status,
 * the hold / release / temporary limit / risk tier (CREDIT_MANAGE) and reset dunning (DUNNING_ADMIN) actions with a
 * reason (BR-CR-006), and the credit and dunning history.
 */
@Component({
  selector: 'app-credit-control',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule, ButtonModule, DialogModule, InputNumberModule, InputTextModule, SelectModule, TableModule,
    TextareaModule, ToastModule, ...FLOW],
  providers: [MessageService],
  styleUrls: ['../../gl/gl.scss', '../ar-ap.scss'],
  template: `
<p-toast></p-toast>
<sf-page [crumbs]="['Finance', 'Receivables', 'Credit control', exposure?.customerName]">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <ng-container *ngIf="uuid && exposure && canManage">
      <p-button *ngIf="!exposure.isOnHold" label="Place hold" icon="pi pi-lock" severity="danger" (onClick)="open('hold')" data-testid="place-hold"></p-button>
      <p-button *ngIf="exposure.isOnHold" label="Release hold" icon="pi pi-lock-open" (onClick)="open('release')" data-testid="release-hold"></p-button>
      <p-button label="Set temporary limit" icon="pi pi-arrow-up" [text]="true" (onClick)="open('temp')" data-testid="set-temp"></p-button>
      <p-button *ngIf="tempActive" label="Clear temporary limit" icon="pi pi-times" [text]="true" (onClick)="open('clearTemp')" data-testid="clear-temp"></p-button>
      <p-button label="Risk tier" icon="pi pi-flag" [text]="true" (onClick)="open('tier')" data-testid="set-tier"></p-button>
    </ng-container>
    <p-button *ngIf="uuid && exposure && canResetDunning" label="Reset dunning" icon="pi pi-replay" [text]="true" [disabled]="!dunning?.currentLevel"
              (onClick)="open('reset')" data-testid="reset-dunning"></p-button>
    <p-button *ngIf="uuid && exposure" label="View history" icon="pi pi-history" [text]="true" (onClick)="scrollToHistory()" data-testid="view-history"></p-button>
    <span class="sf-end">
      <a *ngIf="uuid && exposure && canStatement" class="p-button p-button-text" [routerLink]="[statementRoute]" [queryParams]="{ customer: uuid }" data-testid="statement-link">
        <i class="pi pi-file"></i>&nbsp;Statement</a>
      <a *ngIf="uuid" class="p-button p-button-text" [routerLink]="creditRoute" data-testid="back-list"><i class="pi pi-list"></i>&nbsp;All customers</a>
      <a class="p-button p-button-text" [routerLink]="arRoute" data-testid="back"><i class="pi pi-arrow-left"></i>&nbsp;Back to AR</a>
    </span>
  </div>

  <!-- The list -->
  <div class="sf-body" *ngIf="!uuid">
    <div class="sf-lr-head"><div><h1>Credit control <span class="sf-count">{{ listTotal }}</span></h1>
      <div class="sf-sub">Customers with a credit profile or an open balance: limit, exposure and holds</div></div>
      <div class="sf-filters">
        <button type="button" class="sf-chip" [class.on]="!onHold && !overLimit" (click)="setFilter(null)" data-testid="chip-all">All</button>
        <button type="button" class="sf-chip" [class.on]="overLimit" (click)="setFilter('over')" data-testid="chip-over">Over limit</button>
        <button type="button" class="sf-chip" [class.on]="onHold" (click)="setFilter('hold')" data-testid="chip-hold">On hold</button>
      </div></div>

    <section class="sf-card" *ngIf="canAdmin" data-testid="mode">
      <div class="sf-fields">
        <div class="sf-fld"><label for="cc-mode">Credit control mode</label>
          <p-select inputId="cc-mode" [options]="modes" [(ngModel)]="mode" (ngModelChange)="saveMode()" [disabled]="modeSaving" appendTo="body" data-testid="mode-select"></p-select></div>
        <div class="sf-fld" style="grid-column: span 2"><label>&nbsp;</label><span class="sf-mu sf-sm">{{ modeHelp }}</span></div>
      </div>
    </section>

    <div class="sf-filters">
      <input pInputText class="sf-search" [(ngModel)]="search" (keyup.enter)="loadList(1)" placeholder="Customer name or code…" data-testid="search" />
      <p-select [options]="tierOptions" [(ngModel)]="tierFilter" (ngModelChange)="loadList(1)" optionLabel="label" optionValue="value" appendTo="body" data-testid="tier-filter"></p-select>
    </div>
    <div class="gl-note er" *ngIf="listError" data-testid="list-error"><i class="pi pi-exclamation-triangle"></i><div>{{ listError }}</div></div>
    <section class="sf-card p0">
      <div class="gl-scroll">
        <p-table [value]="list" [lazy]="true" (onLazyLoad)="onLazy($event)" [loading]="listLoading" [paginator]="true" [rows]="pageSize" [totalRecords]="listTotal"
                 [rowsPerPageOptions]="[25, 50, 100]" dataKey="customerUuid" styleClass="p-datatable-sm" [tableStyle]="{ 'min-width': '52rem' }" data-testid="list">
          <ng-template pTemplate="header"><tr><th>Customer</th><th class="gl-num">Limit</th><th class="gl-num">Exposure</th><th class="gl-num">Available</th>
            <th>Risk</th><th>Status</th></tr></ng-template>
          <ng-template pTemplate="body" let-e>
            <tr class="aa-click" (click)="openCustomer(e.customerUuid)" [attr.data-testid]="'row-' + e.customerUuid">
              <td><a [routerLink]="[creditRoute, e.customerUuid]" (click)="$event.stopPropagation()">{{ e.customerName }}</a>
                <span class="sf-mu sf-sm" *ngIf="e.customerCode"> · {{ e.customerCode }}</span></td>
              <td class="gl-num">{{ e.effectiveCreditLimit ? amount(e.effectiveCreditLimit) : 'No limit' }}</td>
              <td class="gl-num" [class.aa-bad]="e.isOverLimit">{{ amount(e.totalExposure) }}</td>
              <td class="gl-num">{{ e.availableCredit === null || e.availableCredit === undefined ? '—' : amount(e.availableCredit) }}</td>
              <td><span class="sf-pill" [ngClass]="tone(e.riskTier)">{{ e.riskTier }}</span></td>
              <td><span class="sf-pill" [ngClass]="statusOf(e).tone">{{ statusOf(e).label }}</span></td>
            </tr>
          </ng-template>
          <ng-template pTemplate="emptymessage"><tr><td colspan="6" class="sf-empty" data-testid="list-empty">No customer matches.</td></tr></ng-template>
        </p-table>
      </div>
    </section>
  </div>

  <div class="sf-body" *ngIf="uuid && (loading || loadError)">
    <div class="sf-card sf-empty" *ngIf="loading"><i class="pi pi-spin pi-spinner"></i> Loading…</div>
    <div class="gl-note er" *ngIf="loadError" data-testid="load-error"><i class="pi pi-exclamation-triangle"></i><div>{{ loadError }}</div></div>
  </div>

  <ng-container *ngIf="uuid && !loading && exposure as e">
    <header class="sf-hdr">
      <div class="sf-t"><div>
        <div class="sf-kicker">Credit control<ng-container *ngIf="e.customerCode"> · {{ e.customerCode }}</ng-container></div>
        <h1 data-testid="title">{{ e.customerName }}
          <span class="sf-pill" [ngClass]="status.tone" data-testid="status">{{ status.label }}</span>
          <span class="sf-pill" [ngClass]="tone(e.riskTier)" data-testid="risk">{{ e.riskTier }} risk</span></h1>
        <div class="sf-meta" *ngIf="e.isOnHold && profile?.creditHoldReason" data-testid="hold-reason">On hold: {{ profile?.creditHoldReason }}
          <ng-container *ngIf="profile?.creditHoldByName"> · {{ profile?.creditHoldByName }}</ng-container></div>
      </div></div>
      <div class="sf-facts">
        <div class="sf-kpi"><span class="l">Exposure</span><span class="v sm" data-testid="kpi-exposure">{{ amount(e.totalExposure) }}</span><span class="s">{{ e.baseCurrencyCode || '' }}</span></div>
        <div class="sf-kpi"><span class="l">Available</span>
          <span class="v sm" [class.er]="(e.availableCredit ?? 0) < 0" data-testid="kpi-available">{{ e.availableCredit === null || e.availableCredit === undefined ? 'No limit' : amount(e.availableCredit) }}</span></div>
      </div>
    </header>

    <div class="sf-body">
      <div class="gl-note" *ngIf="e.creditControlMode === 'Off'" data-testid="mode-off"><i class="pi pi-info-circle"></i>
        <div>Credit control is switched off for this organization: sale orders are not checked and holds do not block them (D-5).</div></div>
      <div class="gl-note" *ngIf="e.creditControlMode === 'Warn'" data-testid="mode-warn"><i class="pi pi-info-circle"></i>
        <div>Credit control warns only: a hold blocks new sale orders, an exceeded limit is a warning.</div></div>

      <!-- Task 546 -->
      <section class="sf-card" data-testid="credit">
        <div class="sf-ch"><h2>Credit</h2></div>
        <div class="aa-kv-grid">
          <div><span class="l">Credit limit</span><span class="v" data-testid="limit">{{ e.creditLimit ? amount(e.creditLimit) : 'No limit' }}</span>
            <span class="s">from the customer master<ng-container *ngIf="canEditCustomer"> · <a [routerLink]="['/portal/pages/customers', uuid]">change</a></ng-container></span></div>
          <div><span class="l">Temporary limit</span><span class="v" data-testid="temp">{{ tempActive ? amount(e.temporaryCreditLimit) : '—' }}</span>
            <span class="s" *ngIf="tempActive">expires {{ e.temporaryCreditExpiry | date:'dd MMM yyyy' }}</span>
            <span class="s" *ngIf="!tempActive && e.temporaryCreditLimit">expired {{ e.temporaryCreditExpiry | date:'dd MMM yyyy' }}</span></div>
          <div><span class="l">Effective limit</span><span class="v" data-testid="effective">{{ e.effectiveCreditLimit ? amount(e.effectiveCreditLimit) : 'No limit' }}</span></div>
          <div><span class="l">Current exposure</span><span class="v" [class.er]="e.isOverLimit" data-testid="exposure">{{ amount(e.totalExposure) }}</span>
            <span class="s" data-testid="breakdown">Open orders {{ amount(e.openOrdersAmount) }} · Open invoices {{ amount(e.openInvoicesAmount) }}</span></div>
          <div><span class="l">Available credit</span>
            <span class="v" [class.er]="(e.availableCredit ?? 0) < 0" [class.ok]="(e.availableCredit ?? -1) >= 0" data-testid="available">
              {{ e.availableCredit === null || e.availableCredit === undefined ? '—' : amount(e.availableCredit) }}</span></div>
          <div><span class="l">Status</span><span class="v"><span class="sf-pill" [ngClass]="status.tone">{{ status.label }}</span></span></div>
        </div>
      </section>

      <!-- Task 547 -->
      <section class="sf-card" data-testid="dunning">
        <div class="sf-ch"><h2>Dunning status</h2><span class="sf-end sf-mu sf-sm" *ngIf="dunning?.procedureCode">{{ dunning?.procedureCode }}</span></div>
        <div class="aa-kv-grid" *ngIf="dunning as d">
          <div><span class="l">Current level</span><span class="v" data-testid="dunning-level">{{ d.currentLevel }}<ng-container *ngIf="d.maxLevel"> of {{ d.maxLevel }}</ng-container></span></div>
          <div><span class="l">Last dunning</span><span class="v">{{ d.lastDunningDate ? (d.lastDunningDate | date:'dd MMM yyyy') : '—' }}</span></div>
          <div><span class="l">Next dunning</span><span class="v" data-testid="dunning-next">{{ nextDunningText }}</span></div>
          <div><span class="l">Block sales</span><span class="v" data-testid="dunning-block">{{ blockText }}</span></div>
        </div>
        <div class="sf-empty" *ngIf="!dunning">The dunning status could not be read.</div>
      </section>

      <!-- Task 549 -->
      <section id="cc-history" class="sf-card p0" data-testid="history">
        <div class="sf-ch sf-pad"><h2>Credit hold history <span class="sf-count">{{ history.length }}</span></h2></div>
        <div class="gl-scroll">
          <table class="aa-table" style="min-width:40rem">
            <thead><tr><th>Date</th><th>Action</th><th>Reason</th><th class="gl-num">Amount</th><th>By</th></tr></thead>
            <tbody>
              <tr *ngFor="let h of history" [attr.data-testid]="'history-' + h.uuid">
                <td>{{ h.requestedAt | date:'dd MMM yyyy' }}</td>
                <td>{{ label(h.action) }}<span class="sf-mu sf-sm" *ngIf="h.newValue"> {{ h.previousValue ? h.previousValue + ' → ' : '→ ' }}{{ h.newValue }}</span>
                  <span class="sf-mu sf-sm sf-mono" *ngIf="h.sourceDocumentNumber"> · {{ h.sourceDocumentNumber }}</span></td>
                <td>{{ h.reason }}</td>
                <td class="gl-num">{{ h.amount ? amount(h.amount) : '' }}<span class="sf-mu sf-sm" *ngIf="h.expiryDate"> until {{ h.expiryDate | date:'dd MMM yyyy' }}</span></td>
                <td>{{ h.requestedByName || '' }}</td>
              </tr>
              <tr *ngIf="!history.length"><td colspan="5" class="sf-empty" data-testid="history-empty">No holds, releases or limit changes yet.</td></tr>
            </tbody>
          </table>
        </div>
      </section>

      <section class="sf-card p0" data-testid="dunning-history">
        <div class="sf-ch sf-pad"><h2>Dunning history <span class="sf-count">{{ dunningHistory.length }}</span></h2></div>
        <div class="gl-scroll">
          <table class="aa-table" style="min-width:36rem">
            <thead><tr><th>When</th><th>Event</th><th>Level</th><th class="gl-num">Amount</th><th>Detail</th><th>Status</th></tr></thead>
            <tbody>
              <tr *ngFor="let d of dunningHistory" data-testid="dunning-event">
                <td>{{ d.at | date:'dd MMM yyyy' }}<span class="sf-mu sf-sm sf-mono" *ngIf="d.runNumber"> · {{ d.runNumber }}</span></td>
                <td>{{ d.kind }}</td><td>{{ d.level ?? '' }}</td>
                <td class="gl-num">{{ d.amount ? amount(d.amount) : '' }}</td>
                <td>{{ d.detail || '' }}<span class="sf-mu sf-sm" *ngIf="d.byName"> · {{ d.byName }}</span></td><td>{{ d.status || '' }}</td>
              </tr>
              <tr *ngIf="!dunningHistory.length"><td colspan="6" class="sf-empty">No dunning notices sent to this customer.</td></tr>
            </tbody>
          </table>
        </div>
      </section>
    </div>
  </ng-container>
</sf-page>

<!-- Tasks 550 / 551 -->
<p-dialog [visible]="modal !== null" (visibleChange)="$event ? null : close()" [modal]="true" [header]="modalTitle" [style]="{ width: '30rem', maxWidth: '96vw' }"
          styleClass="sdlg" data-testid="dialog">
  <div class="aa-dlg-body">
    <ng-container *ngIf="modal === 'temp'">
      <div class="sf-fld"><label for="cc-amt" class="sf-req">Temporary limit</label>
        <p-inputNumber inputId="cc-amt" [(ngModel)]="tempAmount" mode="decimal" [minFractionDigits]="2" [maxFractionDigits]="2" [min]="0" styleClass="aa-wide"
                       data-testid="temp-amount"></p-inputNumber></div>
      <div class="sf-fld"><label for="cc-exp" class="sf-req">Expires on</label>
        <input pInputText id="cc-exp" type="date" [(ngModel)]="tempExpiry" [min]="tomorrow" data-testid="temp-expiry" /></div>
    </ng-container>
    <div class="sf-fld" *ngIf="modal === 'tier'"><label for="cc-tier" class="sf-req">Risk tier</label>
      <p-select inputId="cc-tier" [options]="tiers" [(ngModel)]="tier" appendTo="body" data-testid="tier"></p-select></div>
    <div class="sf-fld"><label for="cc-reason" class="sf-req">Reason</label>
      <textarea pTextarea id="cc-reason" rows="3" [(ngModel)]="reason" maxlength="500" data-testid="reason"></textarea></div>
    <div class="gl-note wn" *ngIf="modal === 'hold'"><i class="pi pi-info-circle"></i><div>While on hold every new sale order for this customer is refused, whatever the limit (BR-CR-003), unless credit control is off.</div></div>
    <div class="gl-note er" *ngIf="dialogError" data-testid="dialog-error"><i class="pi pi-exclamation-triangle"></i><div>{{ dialogError }}</div></div>
  </div>
  <ng-template pTemplate="footer">
    <p-button label="Cancel" [text]="true" severity="secondary" (onClick)="close()"></p-button>
    <p-button [label]="modalAction" [severity]="modal === 'hold' ? 'danger' : undefined" [disabled]="busy" [loading]="busy" (onClick)="submit()" data-testid="submit"></p-button>
  </ng-template>
</p-dialog>`
})
export class CreditControlComponent implements OnInit {
  private readonly api = inject(ArApService);
  private readonly auth = inject(AuthService);
  private readonly messages = inject(MessageService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute, { optional: true });

  readonly arRoute = AR_ROUTE;
  readonly creditRoute = CREDIT_ROUTE;
  readonly statementRoute = STATEMENT_ROUTE;
  readonly tiers = RISK_TIERS;
  readonly modes = CREDIT_CONTROL_MODES;
  readonly tierOptions = [{ label: 'Any risk', value: null as string | null }, ...RISK_TIERS.map(t => ({ label: `${t} risk`, value: t as string | null }))];
  readonly tomorrow = addDays(today(), 1);

  uuid = '';
  // List
  list: CreditExposure[] = [];
  listTotal = 0;
  pageSize = 25;
  listLoading = false;
  listError = '';
  search = '';
  tierFilter: string | null = null;
  onHold = false;
  overLimit = false;
  mode = 'Off';
  modeSaving = false;
  private listReady = false;

  // Detail
  exposure: CreditExposure | null = null;
  profile: PartnerFinancialProfile | null = null;
  procedures: DunningProcedure[] = [];
  history: CreditHistoryEntry[] = [];
  dunningHistory: DunningHistoryEvent[] = [];
  loading = false;
  loadError = '';

  modal: Modal = null;
  reason = '';
  tempAmount: number | null = null;
  tempExpiry = '';
  tier: RiskTier | string = 'Medium';
  dialogError = '';
  busy = false;

  get canManage(): boolean { return this.auth.hasPermission(AR_AP_PERMS.CREDIT_MANAGE); }
  get canResetDunning(): boolean { return this.auth.hasPermission(AR_AP_PERMS.DUNNING_ADMIN); }
  get canAdmin(): boolean { return this.auth.hasPermission(AR_AP_PERMS.ADMIN); }
  get canStatement(): boolean { return this.auth.hasAnyPermission(AR_AP_PERMS.AR_VIEW, AR_AP_PERMS.AR_STATEMENT_SEND); }
  get canEditCustomer(): boolean { return this.auth.hasPermission('CUSTOMER_EDIT'); }
  get status() { return creditStatus(this.exposure); }
  get dunning(): DunningView | null { return dunningView(this.profile, this.procedures); }

  get modeHelp(): string { return creditModeHelp(this.mode); }

  get tempActive(): boolean {
    const e = this.exposure;
    if (this.profile?.temporaryLimitActive !== undefined && this.profile?.temporaryLimitActive !== null) return this.profile.temporaryLimitActive;
    return !!e?.temporaryCreditLimit && (!e.temporaryCreditExpiry || e.temporaryCreditExpiry.slice(0, 10) >= today());
  }

  get nextDunningText(): string {
    const d = this.dunning;
    if (!d) return '—';
    if (d.excluded) return 'Excluded from dunning';
    if (!d.nextLevel) return d.maxLevel && d.currentLevel >= d.maxLevel ? 'Final level reached' : '—';
    if (!d.nextDate) return `Level ${d.nextLevel} when overdue`;
    const days = daysBetween(today(), d.nextDate);
    return days <= 0 ? `Level ${d.nextLevel} at the next run` : `Level ${d.nextLevel} in ${days} day${days === 1 ? '' : 's'}`;
  }

  get blockText(): string {
    const d = this.dunning;
    if (!d?.blockSalesAtLevel) return 'Never';
    return d.blockApplies ? `At level ${d.blockSalesAtLevel}` : `At level ${d.blockSalesAtLevel} (not for this customer)`;
  }

  get modalTitle(): string {
    switch (this.modal) {
      case 'hold': return 'Place credit hold';
      case 'release': return 'Release credit hold';
      case 'temp': return 'Set temporary limit';
      case 'clearTemp': return 'Clear temporary limit';
      case 'tier': return 'Change risk tier';
      case 'reset': return 'Reset dunning level';
      default: return '';
    }
  }

  get modalAction(): string {
    switch (this.modal) {
      case 'hold': return 'Place hold';
      case 'release': return 'Release';
      case 'temp': return 'Set limit';
      case 'clearTemp': return 'Clear';
      case 'tier': return 'Save';
      default: return 'Reset to 0';
    }
  }

  ngOnInit(): void {
    this.route?.paramMap?.subscribe(pm => {
      this.uuid = pm.get('uuid') ?? '';
      if (this.uuid) this.load();
      else if (this.canAdmin) this.api.getSettings().pipe(catchError(() => of(null))).subscribe(s => { if (s) this.mode = s.creditControlMode; });
    });
  }

  // ── List ──
  onLazy(e: TableLazyLoadEvent): void {
    this.listReady = true;
    this.pageSize = e.rows ?? this.pageSize;
    this.loadList(Math.floor((e.first ?? 0) / this.pageSize) + 1);
  }

  setFilter(f: 'over' | 'hold' | null): void {
    this.overLimit = f === 'over';
    this.onHold = f === 'hold';
    if (this.listReady) this.loadList(1);
  }

  private listSeq = 0;

  loadList(page: number): void {
    const seq = ++this.listSeq;
    this.listLoading = true;
    this.listError = '';
    this.api.getExposures({ overLimit: this.overLimit, onHold: this.onHold, riskTier: this.tierFilter, search: this.search, page, pageSize: this.pageSize }).subscribe({
      next: r => { if (seq !== this.listSeq) return; this.listLoading = false; this.list = r.data; this.listTotal = r.totalRecords; },
      error: err => { if (seq !== this.listSeq) return; this.listLoading = false; this.list = []; this.listTotal = 0; this.listError = arApError(err, 'The customers could not be loaded.'); }
    });
  }

  saveMode(): void {
    this.modeSaving = true;
    this.api.updateSettings({ creditControlMode: this.mode }).subscribe({
      next: s => { this.modeSaving = false; this.mode = s?.creditControlMode ?? this.mode; this.toast('success', `Credit control is now ${this.mode}.`); },
      error: err => {
        this.modeSaving = false;
        this.toast('error', arApError(err, 'The mode could not be saved.'));
        this.api.getSettings().pipe(catchError(() => of(null))).subscribe(s => { if (s) this.mode = s.creditControlMode; });
      }
    });
  }

  openCustomer(uuid: string): void { this.router.navigate([CREDIT_ROUTE, uuid]); }
  statusOf(e: CreditExposure) { return creditStatus(e); }

  // ── Detail ──
  load(): void {
    this.loading = !this.exposure;
    this.loadError = '';
    this.api.getExposure(this.uuid).subscribe({
      next: e => { this.loading = false; this.exposure = e; this.tier = e.riskTier || 'Medium'; },
      error: err => { this.loading = false; this.exposure = null; this.loadError = arApError(err, 'The customer\'s credit could not be loaded.'); }
    });
    this.api.getProfile(this.uuid, 'Customer').pipe(catchError(() => of(null))).subscribe(p => this.profile = p);
    if (!this.procedures.length) this.api.getProcedures().pipe(catchError(() => of([] as DunningProcedure[]))).subscribe(p => this.procedures = p);
    // Newest first, as the server sends them.
    this.api.getCreditHistory(this.uuid).pipe(catchError(() => of(null))).subscribe(h => this.history = h?.data ?? []);
    this.api.getCustomerDunningHistory(this.uuid).pipe(catchError(() => of(null)))
      .subscribe(h => this.dunningHistory = [...(h?.events ?? [])].sort((a, b) => (b.at ?? '').localeCompare(a.at ?? '')));
  }

  scrollToHistory(): void {
    if (typeof document !== 'undefined') document.getElementById('cc-history')?.scrollIntoView({ behavior: 'smooth', block: 'start' });
  }

  open(m: Exclude<Modal, null>): void {
    this.modal = m;
    this.reason = '';
    this.dialogError = '';
    if (m === 'temp') { this.tempAmount = this.tempActive ? this.exposure?.temporaryCreditLimit ?? null : null; this.tempExpiry = ''; }
    if (m === 'tier') this.tier = this.exposure?.riskTier ?? 'Medium';
  }

  close(): void { this.modal = null; this.busy = false; }

  /** The dialog's refusal before sending, in the contract's words. */
  problem(): string | null {
    const e = this.exposure;
    if (this.modal === 'temp') return tempLimitProblem(this.tempAmount, this.tempExpiry, this.reason, e?.creditLimit, e?.customerName ?? 'The customer');
    if (this.modal === 'tier' && this.tier === e?.riskTier) return 'Choose a different tier.';
    return reasonProblem(this.reason);
  }

  submit(): void {
    this.dialogError = this.problem() ?? '';
    if (this.dialogError) return;
    const reason = this.reason.trim();
    const done: Record<string, string> = {
      hold: 'The customer is on credit hold.', release: 'The credit hold is released.', temp: 'The temporary limit is set.',
      clearTemp: 'The temporary limit is cleared.', tier: 'The risk tier is changed.', reset: 'The dunning level is reset to 0.'
    };
    const kind = this.modal!;
    const call: Observable<unknown> = kind === 'hold' ? this.api.placeHold(this.uuid, reason)
      : kind === 'release' ? this.api.releaseHold(this.uuid, reason)
      : kind === 'temp' ? this.api.setTemporaryLimit(this.uuid, { amount: this.tempAmount!, expiryDate: this.tempExpiry, reason })
      : kind === 'clearTemp' ? this.api.setTemporaryLimit(this.uuid, { amount: null, expiryDate: null, reason })
      : kind === 'tier' ? this.api.setRiskTier(this.uuid, this.tier, reason)
      : this.api.resetDunning(this.uuid, reason, 0);
    this.busy = true;
    call.subscribe({
      next: () => { this.busy = false; this.modal = null; this.toast('success', done[kind]); this.load(); },
      error: err => { this.busy = false; this.dialogError = arApError(err, 'The change could not be saved.'); }
    });
  }

  tone(t: string | null | undefined): string { return riskTone(t); }
  label(s: string | null | undefined): string { return labelOf(s); }
  amount(n: number | null | undefined): string { return money(n); }

  private toast(severity: 'success' | 'error', detail: string): void {
    this.messages.add({ severity, summary: severity === 'success' ? 'Done' : 'Error', detail, life: severity === 'error' ? 8000 : 3000 });
  }
}
