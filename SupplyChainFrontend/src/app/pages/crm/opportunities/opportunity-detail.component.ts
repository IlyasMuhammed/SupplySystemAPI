import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import { ButtonModule } from 'primeng/button';
import { CalendarModule } from 'primeng/calendar';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { DialogModule } from 'primeng/dialog';
import { DropdownModule } from 'primeng/dropdown';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { ToastModule } from 'primeng/toast';
import { ConfirmationService, MessageService } from 'primeng/api';

import {
  Activity, ActivityDocument, CRM_ROUTE, CrmService, CrossReferenceResult, LinkedSalesDocument, OpportunityDetail, OpportunityUpdate,
  PipelineStage, TimelineEntry, crmLabel, opportunityStatusTone, saveBlob
} from '../../../services/crm.service';
import { AuthService } from '../../service/auth.service';
import { FLOW, FlowSection } from '../../../shared/flow';
import { fromDateOnly, toDateOnly } from '../../../shared/date-only';
import { CrmLookupsService, Option } from '../shared/crm-lookups.service';
import { CrmTimelineComponent, TimelineQuickAdd } from '../shared/crm-timeline.component';
import { ActivityFormDialogComponent } from '../shared/activity-form-dialog.component';

const DOC_ROUTES: Record<string, string> = {
  INQUIRY: '/portal/pages/sales/inquiries', QUOTATION: '/portal/pages/sales/quotations', ORDER: '/portal/pages/sales/orders'
};

/** A41 §15.5 — an opportunity: SCM actions, Won/Lost, the pipeline bar, account / contact / deal, linked documents, timeline. */
@Component({
  selector: 'app-opportunity-detail',
  standalone: true,
  imports: [CommonModule, RouterModule, FormsModule, ButtonModule, CalendarModule, ConfirmDialogModule, DialogModule, DropdownModule, InputNumberModule,
    InputTextModule, TextareaModule, ToastModule, ...FLOW, CrmTimelineComponent, ActivityFormDialogComponent],
  providers: [MessageService, ConfirmationService],
  template: `
<p-toast></p-toast>
<p-confirmDialog></p-confirmDialog>

<div class="sf-empty" *ngIf="loading && !opp" data-testid="loading"><i class="pi pi-spin pi-spinner"></i> Loading…</div>
<div class="sf-empty" *ngIf="loadFailed && !loading" data-testid="load-failed">
  <i class="pi pi-exclamation-triangle" style="font-size:2rem"></i>
  <h2>This opportunity could not be loaded</h2>
  <p-button label="Back to opportunities" icon="pi pi-arrow-left" [routerLink]="[route, 'opportunities']"></p-button>
</div>

<sf-page *ngIf="opp as o" [crumbs]="['CRM', 'Opportunities', o.opportunityNumber]">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands" data-testid="actions">
    <p-button *ngIf="can('CREATE_INQUIRY')" label="Create inquiry" icon="pi pi-inbox" [text]="!!o.salesDocuments.length" [loading]="busy"
              (onClick)="createInquiry()" data-testid="create-inquiry"></p-button>
    <p-button *ngIf="can('CREATE_QUOTATION')" label="Create quotation" icon="pi pi-file-edit" [text]="true" [loading]="busy" (onClick)="createQuotation()" data-testid="create-quotation"></p-button>
    <p-button *ngIf="can('CREATE_ORDER')" label="Create order" icon="pi pi-shopping-bag" [text]="true" [loading]="busy" (onClick)="createOrder()" data-testid="create-order"></p-button>
    <p-button *ngIf="can('WON')" label="Won" icon="pi pi-check" severity="success" [loading]="busy" (onClick)="won()" data-testid="won"></p-button>
    <p-button *ngIf="can('LOST')" label="Lost" icon="pi pi-times" severity="danger" [text]="true" (onClick)="lostVisible = true" data-testid="lost"></p-button>
    <p-button *ngIf="can('REOPEN')" label="Re-open" icon="pi pi-replay" [loading]="busy" (onClick)="reopen()" data-testid="reopen"></p-button>
    <span class="sf-sep"></span>
    <p-button *ngIf="canLog && o.status === 'OPEN'" label="Log activity" icon="pi pi-plus" [text]="true" (onClick)="openActivity('PHONE_CALL')"></p-button>
    <p-button *ngIf="o.canEdit" label="Edit" icon="pi pi-pencil" [text]="true" (onClick)="openEdit()" data-testid="edit"></p-button>
    <span class="sf-end"><p-button icon="pi pi-arrow-left" label="All opportunities" [text]="true" [routerLink]="[route, 'opportunities']"></p-button></span>
  </div>

  <header class="sf-hdr">
    <div class="sf-t"><div>
      <div class="sf-kicker">Opportunity · from lead <a class="sf-mono" [routerLink]="[route, 'leads', o.sourceLeadUuid]" data-testid="lead-link">{{ o.sourceLeadNumber }}</a></div>
      <h1><span class="sf-mono">{{ o.opportunityNumber }}</span>
        <span class="sf-pill" [ngClass]="tone(o.status)" data-testid="status">{{ label(o.status) }}</span></h1>
      <div class="sf-meta">{{ o.topic }} · {{ o.accountName }} · {{ o.assignedToName || 'Unassigned' }}</div>
    </div></div>
    <div class="op-pipe" data-testid="pipeline">
      <button type="button" *ngFor="let s of o.pipeline; let i = index" class="op-step"
              [class.done]="isDone(s)" [class.on]="s.uuid === o.pipelineStageUuid" [class.won]="s.isWon" [class.lost]="s.isLost"
              [disabled]="!canMove(s)" (click)="moveTo(s)" [attr.data-testid]="'step-' + s.code">
        <span>{{ s.name }}</span><small *ngIf="s.winProbability != null">{{ s.winProbability }}%</small>
      </button>
    </div>
    <div class="sf-facts">
      <div class="sf-kpi"><span class="l">Stage</span><span class="v sm">{{ o.pipelineStageName || '—' }}</span>
        <span class="s" *ngIf="o.winProbability != null">{{ o.winProbability }}%</span></div>
      <div class="sf-kpi"><span class="l">Estimated</span>
        <span class="v sm">{{ o.estimatedRevenue != null ? (o.estimatedRevenue | number:'1.0-2') + ' ' + (o.currencyCode || '') : '—' }}</span>
        <span class="s" *ngIf="o.estimatedCloseDate">close {{ o.estimatedCloseDate | date:'dd MMM yyyy' }}</span></div>
      <div class="sf-kpi" *ngIf="o.status === 'WON'"><span class="l">Won</span>
        <span class="v sm ok">{{ o.actualRevenue | number:'1.0-2' }} {{ o.currencyCode }}</span>
        <span class="s">{{ o.actualCloseDate | date:'dd MMM yyyy' }}</span></div>
      <div class="sf-kpi" *ngIf="o.status === 'LOST'"><span class="l">Lost</span>
        <span class="v sm er">{{ o.actualCloseDate | date:'dd MMM yyyy' }}</span><span class="s">{{ o.competitorName ? 'to ' + o.competitorName : '' }}</span></div>
    </div>
  </header>

  <div class="sf-pill er op-reason" *ngIf="o.status === 'LOST'" data-testid="lost-reason">Lost: {{ o.winLossReason }}</div>

  <sf-anchors [sections]="sections" mode="scroll"></sf-anchors>
  <div class="sf-body">
    <section id="sec-details" class="sf-section op-grid">
      <div class="sf-card">
        <div class="sf-ch"><h2>Account</h2></div>
        <div class="sf-kv">
          <span>Account</span><span><a *ngIf="o.account" [routerLink]="['/portal/pages/customers', o.account.uuid]" data-testid="account-link">{{ o.account.name }}</a>
            <span class="sf-mono sf-mu" *ngIf="o.account"> {{ o.account.code }}</span><span *ngIf="!o.account">{{ o.accountName }}</span></span>
          <span>Contact</span><span>{{ o.contactPerson?.name || '—' }}<span class="sf-mu" *ngIf="o.contactPerson?.title"> — {{ o.contactPerson?.title }}</span></span>
          <span>Email</span><span>{{ o.contactPerson?.email || '—' }}</span>
          <span>Phone</span><span>{{ o.contactPerson?.phone || '—' }}</span>
        </div>
        <div class="sf-ch op-sub"><h2>Ownership</h2></div>
        <div class="sf-kv">
          <span>Assigned</span><span>{{ o.assignedToName || 'Unassigned' }}</span>
          <span>Team</span><span>{{ o.salesTeamName || '—' }}</span>
          <span>Created</span><span>{{ o.createdAt | date:'dd MMM yyyy' }} by {{ o.createdByName || '—' }}</span>
        </div>
      </div>
      <div class="sf-card">
        <div class="sf-ch"><h2>Deal</h2></div>
        <div class="sf-kv">
          <span>Topic</span><span>{{ o.topic }}</span>
          <span>Lead</span><span><a class="sf-mono" [routerLink]="[route, 'leads', o.sourceLeadUuid]">{{ o.sourceLeadNumber }}</a></span>
          <span>Campaign</span><span>{{ o.campaignName || '—' }}</span>
          <span>Revenue</span><span>{{ o.estimatedRevenue != null ? (o.estimatedRevenue | number:'1.0-2') + ' ' + (o.currencyCode || '') : '—' }}</span>
          <span>Close by</span><span>{{ o.estimatedCloseDate ? (o.estimatedCloseDate | date:'dd MMM yyyy') : '—' }}</span>
          <span>Description</span><span class="op-desc">{{ o.description || '—' }}</span>
        </div>
        <div class="sf-ch op-sub"><h2>Linked documents</h2></div>
        <div data-testid="sales-documents">
          <div class="op-doc" *ngFor="let d of o.salesDocuments">
            <span class="sf-mu op-kind">{{ label(d.kind) }}</span>
            <a class="sf-mono" [routerLink]="[docRoute(d), d.uuid]" [attr.data-testid]="'doc-' + d.number">{{ d.number }}</a>
            <span class="sf-pill" [ngClass]="docTone(d.status)">{{ label(d.status) }}</span>
            <span class="sf-mu" *ngIf="d.grandTotal">{{ d.grandTotal | number:'1.2-2' }} {{ d.currencyCode }}</span>
          </div>
          <div class="sf-mu sf-sm" *ngIf="!o.salesDocuments.length">No inquiry, quotation or sale order yet.</div>
        </div>
      </div>
    </section>

    <section id="sec-timeline" class="sf-section sf-card">
      <div class="sf-ch"><h2>Timeline</h2></div>
      <app-crm-timeline [entries]="timeline" [total]="timelineTotal" [loading]="timelineLoading" [canAdd]="canLog && o.status === 'OPEN'"
                        [canExport]="canExport" (add)="openActivity($event)" (export)="exportTimeline()" (loadMore)="loadTimeline(true)"
                        (openActivity)="editActivity($event)" (download)="download($event)" (searchChange)="onTimelineSearch($event)"></app-crm-timeline>
    </section>
  </div>

  <sf-panel #panel [tabs]="['Cross-references']">
    <ng-container *ngIf="panel.active() === 'Cross-references'">
      <div data-testid="cross-references">
        <h4 class="op-xh">Other leads for {{ xref?.account?.name || o.accountName }}</h4>
        <div class="sf-mu sf-sm" *ngIf="!xref?.accountLeads?.length">(none)</div>
        <a class="op-x" *ngFor="let x of xref?.accountLeads ?? []" [routerLink]="[route, 'leads', x.uuid]">
          <span class="sf-mono">{{ x.leadNumber }}</span> {{ x.topic }}<br><span class="sf-mu sf-sm">{{ label(x.status) }} · {{ x.createdAt | date:'dd MMM yyyy' }}</span></a>
        <ng-container *ngIf="xref?.contactPerson">
          <h4 class="op-xh">Other leads for {{ xref!.contactPerson!.name }}</h4>
          <div class="sf-mu sf-sm" *ngIf="!xref!.contactLeads.length">(none)</div>
          <a class="op-x" *ngFor="let x of xref!.contactLeads" [routerLink]="[route, 'leads', x.uuid]">
            <span class="sf-mono">{{ x.leadNumber }}</span> {{ x.topic }}<br><span class="sf-mu sf-sm">{{ label(x.status) }} · {{ x.createdAt | date:'dd MMM yyyy' }}</span></a>
        </ng-container>
      </div>
    </ng-container>
  </sf-panel>
</sf-page>

<app-activity-form-dialog [(visible)]="activityVisible" [opportunityUuid]="opp?.uuid ?? null" [accountUuid]="opp?.accountUuid ?? null"
                          [initialType]="activityType" [activity]="editingActivity" [canAssignOthers]="canAssign" (saved)="load()"></app-activity-form-dialog>

<p-dialog [(visible)]="lostVisible" [modal]="true" [draggable]="false" header="Mark as lost" [style]="{ width: '32rem' }" data-testid="lost-dialog">
  <div class="sf-fields">
    <div class="sf-fld span-3"><label for="lo-reason" class="sf-req">Reason</label>
      <textarea pTextarea id="lo-reason" [(ngModel)]="lostReason" rows="3" maxlength="500" class="w-full" data-testid="lost-reason-input"></textarea></div>
    <div class="sf-fld span-3"><label for="lo-comp">Lost to (competitor)</label>
      <input pInputText id="lo-comp" [(ngModel)]="competitor" maxlength="200" /></div>
  </div>
  <ng-template pTemplate="footer">
    <p-button label="Cancel" [text]="true" severity="secondary" (onClick)="lostVisible = false"></p-button>
    <p-button label="Mark lost" severity="danger" [disabled]="!lostReason.trim()" [loading]="busy" (onClick)="lost()" data-testid="lost-save"></p-button>
  </ng-template>
</p-dialog>

<p-dialog [(visible)]="editVisible" [modal]="true" [draggable]="false" header="Edit opportunity" [style]="{ width: '42rem' }" [breakpoints]="{ '760px': '96vw' }"
          data-testid="edit-dialog">
  <div class="sf-pill er op-err" *ngIf="editError">{{ editError }}</div>
  <div class="sf-fields">
    <div class="sf-fld span-3"><label for="oe-topic" class="sf-req">Topic</label><input pInputText id="oe-topic" [(ngModel)]="edit.topic" maxlength="200" /></div>
    <div class="sf-fld span-3"><label for="oe-desc">Description</label><textarea pTextarea id="oe-desc" [(ngModel)]="edit.description" rows="2" maxlength="4000" class="w-full"></textarea></div>
    <div class="sf-fld"><label for="oe-contact">Contact</label>
      <p-dropdown inputId="oe-contact" [options]="contactOptions" [(ngModel)]="edit.contactPersonId" optionLabel="label" optionValue="value" [showClear]="true" appendTo="body" styleClass="w-full"></p-dropdown></div>
    <div class="sf-fld"><label for="oe-rev">Estimated revenue</label>
      <p-inputnumber inputId="oe-rev" [(ngModel)]="edit.estimatedRevenue" mode="decimal" [maxFractionDigits]="2" [min]="0" styleClass="w-full"></p-inputnumber></div>
    <div class="sf-fld"><label for="oe-cur">Currency</label>
      <p-dropdown inputId="oe-cur" [options]="currencyOptions" [(ngModel)]="edit.currencyCode" [showClear]="true" appendTo="body" styleClass="w-full"></p-dropdown></div>
    <div class="sf-fld"><label for="oe-close">Close by</label>
      <p-calendar inputId="oe-close" [(ngModel)]="editClose" dateFormat="dd M yy" [showIcon]="true" [showButtonBar]="true" todayButtonStyleClass="" clearButtonStyleClass="" appendTo="body" styleClass="w-full"></p-calendar></div>
    <div class="sf-fld" *ngIf="canAssign"><label for="oe-owner">Owner</label>
      <p-dropdown inputId="oe-owner" [options]="userOptions" [(ngModel)]="edit.assignedToUserId" optionLabel="label" optionValue="value" [filter]="true" filterBy="label" [showClear]="true" appendTo="body" styleClass="w-full"></p-dropdown></div>
    <div class="sf-fld"><label for="oe-team">Team</label>
      <p-dropdown inputId="oe-team" [options]="teamOptions" [(ngModel)]="edit.salesTeamUuid" optionLabel="label" optionValue="value" [showClear]="true" appendTo="body" styleClass="w-full"></p-dropdown></div>
  </div>
  <ng-template pTemplate="footer">
    <p-button label="Cancel" [text]="true" severity="secondary" (onClick)="editVisible = false"></p-button>
    <p-button label="Save" icon="pi pi-check" [loading]="busy" (onClick)="saveEdit()" data-testid="edit-save"></p-button>
  </ng-template>
</p-dialog>`,
  styles: [`
    .op-pipe { display: flex; gap: 2px; margin: .5rem 0 .25rem; flex-wrap: wrap; }
    .op-step { flex: 1 1 7rem; border: 0; background: var(--sms-bg); color: var(--sms-text-muted); padding: .45rem .5rem; cursor: pointer;
               display: flex; flex-direction: column; align-items: center; font-size: .82rem; clip-path: polygon(0 0, 92% 0, 100% 50%, 92% 100%, 0 100%, 8% 50%); }
    .op-step:first-child { clip-path: polygon(0 0, 92% 0, 100% 50%, 92% 100%, 0 100%); }
    .op-step:disabled { cursor: default; }
    .op-step.done { background: var(--sms-primary-soft); color: var(--sms-primary-text); }
    .op-step.on { background: var(--sms-primary); color: var(--sms-on-primary); font-weight: 600; }
    .op-step.on.won { background: var(--sms-ok); }
    .op-step.on.lost { background: var(--sms-danger); }
    .op-step small { opacity: .8; }
    .op-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(22rem, 1fr)); gap: var(--sms-pad, 1rem); }
    .op-sub { margin-top: 1rem; }
    .op-desc { white-space: pre-line; }
    .op-doc { display: flex; gap: .5rem; align-items: center; padding: .35rem 0; border-bottom: 1px solid var(--sms-border-soft); flex-wrap: wrap; }
    .op-kind { min-width: 5.5rem; }
    .op-reason { display: block; margin: 0 1.25rem .5rem; }
    .op-err { display: block; margin-bottom: .75rem; }
    .op-xh { margin: .75rem 0 .35rem; font-size: .85rem; }
    .op-x { display: block; padding: .35rem 0; border-bottom: 1px solid var(--sms-border-soft); color: inherit; text-decoration: none; }
    .w-full { width: 100%; }
    :host ::ng-deep .w-full { width: 100%; }
  `]
})
export class OpportunityDetailComponent implements OnInit, OnDestroy {
  readonly route = CRM_ROUTE;
  readonly tone = opportunityStatusTone;
  readonly label = crmLabel;
  readonly sections: FlowSection[] = [{ id: 'sec-details', label: 'Details' }, { id: 'sec-timeline', label: 'Timeline' }];

  opp: OpportunityDetail | null = null;
  loading = true;
  loadFailed = false;
  busy = false;
  timeline: TimelineEntry[] = [];
  timelineTotal = 0;
  timelineLoading = false;
  private timelinePage = 1;
  private timelineSearch = '';
  xref: CrossReferenceResult | null = null;

  activityVisible = false;
  activityType: string | null = null;
  editingActivity: Activity | null = null;
  lostVisible = false;
  lostReason = '';
  competitor = '';
  editVisible = false;
  editError: string | null = null;
  edit: OpportunityUpdate = {};
  editClose: Date | null = null;
  contactOptions: Option<number>[] = [];
  userOptions: Option<number>[] = [];
  teamOptions: Option[] = [];
  currencyOptions: string[] = [];

  private sub?: Subscription;

  constructor(private crm: CrmService, private lookups: CrmLookupsService, private auth: AuthService, private routeInfo: ActivatedRoute,
              private router: Router, private messages: MessageService, private confirm: ConfirmationService) {}

  get canLog(): boolean { return this.auth.hasPermission('CRM_CREATE_ACTIVITY'); }
  get canExport(): boolean { return this.auth.hasPermission('CRM_EXPORT_TIMELINE'); }
  get canAssign(): boolean { return this.auth.hasPermission('CRM_ASSIGN_LEAD'); }
  can(action: string): boolean { return !!this.opp?.allowedActions.includes(action as never); }

  ngOnInit(): void {
    this.sub = this.routeInfo.paramMap.subscribe(p => { const uuid = p.get('uuid'); if (uuid) this.load(uuid); });
  }

  ngOnDestroy(): void { this.sub?.unsubscribe(); }

  load(uuid = this.opp?.uuid): void {
    if (!uuid) return;
    this.loading = true;
    this.crm.getOpportunity(uuid).subscribe({
      next: r => {
        this.loading = false;
        this.loadFailed = false;
        this.opp = r.result;
        this.loadTimeline();
        if (r.result.sourceLeadUuid)
          this.crm.getLeadCrossReferences(r.result.sourceLeadUuid).subscribe({ next: x => this.xref = x.result, error: () => this.xref = null });
      },
      error: () => { this.loading = false; this.loadFailed = true; }
    });
  }

  onTimelineSearch(text: string): void {
    this.timelineSearch = text;
    this.loadTimeline();
  }

  loadTimeline(more = false): void {
    if (!this.opp) return;
    this.timelinePage = more ? this.timelinePage + 1 : 1;
    this.timelineLoading = true;
    this.crm.getOpportunityTimeline(this.opp.uuid, this.timelinePage, 50, this.timelineSearch).subscribe({
      next: r => {
        this.timelineLoading = false;
        this.timeline = more ? [...this.timeline, ...(r.result?.timeline ?? [])] : (r.result?.timeline ?? []);
        this.timelineTotal = r.result?.totalCount ?? 0;
      },
      error: () => { this.timelineLoading = false; }
    });
  }

  isDone(s: PipelineStage): boolean {
    const current = this.opp?.pipeline.find(p => p.uuid === this.opp?.pipelineStageUuid);
    return !!current && !s.isWon && !s.isLost && s.sequence < current.sequence;
  }

  canMove(s: PipelineStage): boolean {
    return !!this.opp?.canEdit && !s.isWon && !s.isLost && s.uuid !== this.opp.pipelineStageUuid;
  }

  moveTo(s: PipelineStage): void {
    if (!this.canMove(s)) return;
    this.busy = true;
    this.crm.moveOpportunityStage(this.opp!.uuid, s.uuid).subscribe({
      next: r => { this.busy = false; this.opp = r.result; this.loadTimeline(); },
      error: err => { this.busy = false; this.toastError(err?.error?.message || 'The opportunity could not be moved.'); }
    });
  }

  private apply(call: ReturnType<CrmService['markWon']>, done: string): void {
    this.busy = true;
    call.subscribe({
      next: r => { this.busy = false; this.opp = r.result; this.loadTimeline(); this.messages.add({ severity: 'success', summary: done }); },
      error: err => { this.busy = false; this.toastError(err?.error?.message || 'The change was refused.'); }
    });
  }

  won(): void {
    this.confirm.confirm({
      header: 'Mark as won', icon: 'pi pi-check',
      message: 'The confirmed sale order\'s total becomes the actual revenue. Mark this opportunity won?',
      accept: () => this.apply(this.crm.markWon(this.opp!.uuid), 'Opportunity won')
    });
  }

  lost(): void {
    this.lostVisible = false;
    this.apply(this.crm.markLost(this.opp!.uuid, this.lostReason.trim(), this.competitor.trim() || null), 'Opportunity marked lost');
    this.lostReason = '';
    this.competitor = '';
  }

  reopen(): void { this.apply(this.crm.reopenOpportunity(this.opp!.uuid), 'Opportunity re-opened'); }

  private openDoc(d: LinkedSalesDocument, what: string): void {
    this.messages.add({ severity: 'success', summary: `${what} ${d.number} created` });
    this.router.navigate([this.docRoute(d), d.uuid]);
  }

  createInquiry(): void {
    this.busy = true;
    this.crm.createInquiry(this.opp!.uuid).subscribe({
      next: r => { this.busy = false; this.openDoc(r.result, 'Sale inquiry'); },
      error: err => { this.busy = false; this.toastError(err?.error?.message || 'The inquiry could not be created.'); }
    });
  }

  createQuotation(): void {
    this.busy = true;
    this.crm.createQuotation(this.opp!.uuid).subscribe({
      next: r => { this.busy = false; this.openDoc(r.result, 'Sale quotation'); },
      error: err => { this.busy = false; this.toastError(err?.error?.message || 'The quotation could not be created.'); }
    });
  }

  /** D-13 — an accepted quotation is converted on its own page; otherwise the sale order form opens for this customer. */
  createOrder(): void {
    this.busy = true;
    this.crm.createOrder(this.opp!.uuid).subscribe({
      next: r => {
        this.busy = false;
        const p = r.result;
        if (p.acceptedQuotationUuid) {
          this.messages.add({ severity: 'info', summary: `Quotation ${p.acceptedQuotationNumber} is accepted`, detail: 'Convert it to a sale order there.' });
          this.router.navigate(['/portal/pages/sales/quotations', p.acceptedQuotationUuid]);
        } else {
          this.router.navigate(['/portal/pages/sales/orders/new'], { queryParams: { customer: p.accountUuid, opportunity: p.opportunityUuid } });
        }
      },
      error: err => { this.busy = false; this.toastError(err?.error?.message || 'The sale order cannot be started.'); }
    });
  }

  openEdit(): void {
    const o = this.opp!;
    this.editError = null;
    this.edit = {
      topic: o.topic, description: o.description, contactPersonId: o.contactPersonId, estimatedRevenue: o.estimatedRevenue,
      currencyCode: o.currencyCode, assignedToUserId: o.assignedToUserId, salesTeamUuid: o.salesTeamUuid
    };
    this.editClose = o.estimatedCloseDate ? fromDateOnly(o.estimatedCloseDate) : null;
    this.crm.getAccountContacts(o.accountUuid).subscribe({
      next: r => this.contactOptions = (r.result ?? []).map(c => ({ value: c.id, label: c.title ? `${c.name} — ${c.title}` : c.name }))
    });
    this.lookups.assignableUsers().subscribe(u => this.userOptions = CrmLookupsService.userOptions(u));
    this.lookups.teams().subscribe(t => this.teamOptions = t.map(x => ({ value: x.uuid, label: x.name })));
    this.lookups.currencies().subscribe(c => this.currencyOptions = c.codes);
    this.editVisible = true;
  }

  saveEdit(): void {
    if (!this.edit.topic?.trim()) { this.editError = 'Topic is required.'; return; }
    this.busy = true;
    this.crm.updateOpportunity(this.opp!.uuid, { ...this.edit, estimatedCloseDate: this.editClose ? toDateOnly(this.editClose) : null }).subscribe({
      next: r => { this.busy = false; this.editVisible = false; this.opp = r.result; this.messages.add({ severity: 'success', summary: 'Opportunity saved' }); },
      error: err => { this.busy = false; this.editError = err?.error?.message || 'The opportunity could not be saved.'; }
    });
  }

  openActivity(type: TimelineQuickAdd | string): void {
    this.editingActivity = null;
    this.activityType = type;
    this.activityVisible = true;
  }

  editActivity(uuid: string): void {
    this.crm.getActivity(uuid).subscribe({
      next: r => { this.editingActivity = r.result; this.activityType = null; this.activityVisible = true; },
      error: () => this.toastError('The activity could not be opened.')
    });
  }

  exportTimeline(): void {
    this.crm.exportOpportunityTimeline(this.opp!.uuid).subscribe({
      next: blob => saveBlob(blob, `${this.opp!.opportunityNumber}-timeline.xlsx`),
      error: () => this.toastError('The timeline could not be exported.')
    });
  }

  download(d: ActivityDocument): void {
    this.crm.downloadDocument(d.uuid).subscribe({ next: blob => saveBlob(blob, d.documentName), error: () => this.toastError('The document could not be downloaded.') });
  }

  docRoute(d: LinkedSalesDocument): string { return DOC_ROUTES[d.kind] ?? '/portal/pages/sales/orders'; }
  docTone(status: string): string {
    return /CONFIRMED|ACCEPTED|FULFILLED|INVOICED|CLOSED|QUOTED|REVIEW_COMPLETE/.test(status) ? 'ok'
      : /CANCELLED|REJECTED|DECLINED|EXPIRED/.test(status) ? 'er' : status === 'DRAFT' || status === 'RECEIVED' ? '' : 'in';
  }

  private toastError(detail: string): void { this.messages.add({ severity: 'error', summary: 'Error', detail }); }
}
