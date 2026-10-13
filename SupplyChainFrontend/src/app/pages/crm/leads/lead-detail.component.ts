import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { Subscription } from 'rxjs';
import { ButtonModule } from 'primeng/button';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { DialogModule } from 'primeng/dialog';
import { DropdownModule } from 'primeng/dropdown';
import { TextareaModule } from 'primeng/textarea';
import { ToastModule } from 'primeng/toast';
import { ConfirmationService, MessageService } from 'primeng/api';

import {
  Activity, ActivityDocument, CRM_ROUTE, CrmService, CrossReferenceResult, LeadConversionResult, LeadDetail, TimelineEntry,
  crmLabel, leadStatusTone, ratingTone, saveBlob
} from '../../../services/crm.service';
import { AuthService } from '../../service/auth.service';
import { FLOW, FlowSection, FlowStage, flowStagesFrom } from '../../../shared/flow';
import { CrmLookupsService, Option } from '../shared/crm-lookups.service';
import { CrmTimelineComponent, TimelineQuickAdd } from '../shared/crm-timeline.component';
import { ActivityFormDialogComponent } from '../shared/activity-form-dialog.component';
import { LeadFormDialogComponent } from './lead-form-dialog.component';
import { LeadConvertDialogComponent } from './lead-convert-dialog.component';

/** A41 §15.3 — a lead: actions by status, contact / source / BANT / deal / ownership, topic, the timeline, cross-references. */
@Component({
  selector: 'app-lead-detail',
  standalone: true,
  imports: [CommonModule, RouterModule, FormsModule, ButtonModule, ConfirmDialogModule, DialogModule, DropdownModule, TextareaModule, ToastModule,
    ...FLOW, CrmTimelineComponent, ActivityFormDialogComponent, LeadFormDialogComponent, LeadConvertDialogComponent],
  providers: [MessageService, ConfirmationService],
  template: `
<p-toast></p-toast>
<p-confirmDialog></p-confirmDialog>

<div class="sf-empty" *ngIf="loading && !lead" data-testid="loading"><i class="pi pi-spin pi-spinner"></i> Loading…</div>
<div class="sf-empty" *ngIf="loadFailed && !loading" data-testid="load-failed">
  <i class="pi pi-exclamation-triangle" style="font-size:2rem"></i>
  <h2>This lead could not be loaded</h2>
  <p-button label="Back to leads" icon="pi pi-arrow-left" [routerLink]="[route, 'leads']"></p-button>
</div>

<sf-page *ngIf="lead as l" [crumbs]="['CRM', 'Leads', l.leadNumber]">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands" data-testid="actions">
    <p-button *ngIf="can('QUALIFY')" label="Qualify" icon="pi pi-check" [loading]="busy" (onClick)="qualify()" data-testid="qualify"></p-button>
    <p-button *ngIf="can('CONVERT')" label="Convert" icon="pi pi-sync" (onClick)="convertVisible = true" data-testid="convert"></p-button>
    <p-button *ngIf="can('MARK_CONTACTED')" label="Mark contacted" icon="pi pi-phone" [loading]="busy" (onClick)="markContacted()" data-testid="mark-contacted"></p-button>
    <p-button *ngIf="can('REOPEN')" label="Re-open" icon="pi pi-replay" [loading]="busy" (onClick)="reopen()" data-testid="reopen"></p-button>
    <p-button *ngIf="canLog" label="Log activity" icon="pi pi-plus" [text]="true" (onClick)="openActivity('PHONE_CALL')" data-testid="log-activity"></p-button>
    <p-button *ngIf="l.canEdit" label="Edit" icon="pi pi-pencil" [text]="true" (onClick)="formVisible = true" data-testid="edit"></p-button>
    <p-button *ngIf="can('ASSIGN')" label="Assign" icon="pi pi-user-edit" [text]="true" (onClick)="openAssign()" data-testid="assign"></p-button>
    <span class="sf-sep" *ngIf="can('DISQUALIFY') || can('DELETE')"></span>
    <p-button *ngIf="can('DISQUALIFY')" label="Disqualify" icon="pi pi-ban" severity="danger" [text]="true" (onClick)="disqualifyVisible = true" data-testid="disqualify"></p-button>
    <p-button *ngIf="can('DELETE')" label="Delete" icon="pi pi-trash" severity="danger" [text]="true" (onClick)="remove()" data-testid="delete"></p-button>
    <span class="sf-end"><p-button icon="pi pi-arrow-left" label="All leads" [text]="true" [routerLink]="[route, 'leads']"></p-button></span>
  </div>

  <header class="sf-hdr">
    <div class="sf-t"><div>
      <div class="sf-kicker">Lead · {{ l.leadSourceName || 'no source' }}<span *ngIf="l.campaignName"> · {{ l.campaignName }}</span></div>
      <h1>
        <span class="sf-mono">{{ l.leadNumber }}</span>
        <span class="sf-pill" [ngClass]="tone(l.status)" data-testid="status">{{ label(l.status) }}</span>
        <span class="sf-pill" [ngClass]="rTone(l.rating)" data-testid="rating">{{ label(l.rating) }}</span>
      </h1>
      <div class="sf-meta">{{ l.fullName }}<span *ngIf="l.jobTitle"> ({{ l.jobTitle }})</span> · {{ l.companyName }} · {{ l.assignedToName || 'Unassigned' }}</div>
    </div></div>
    <sf-stages [stages]="stages" data-testid="stages"></sf-stages>
    <div class="sf-facts">
      <div class="sf-kpi"><span class="l">Stage</span><span class="v sm">{{ l.pipelineStageName || '—' }}</span>
        <span class="s" *ngIf="l.pipelineStageProbability != null">{{ l.pipelineStageProbability }}% win probability</span></div>
      <div class="sf-kpi"><span class="l">Estimate</span>
        <span class="v sm">{{ l.estimatedRevenue != null ? (l.estimatedRevenue | number:'1.0-2') + ' ' + (l.currencyCode || '') : '—' }}</span>
        <span class="s" *ngIf="l.estimatedCloseDate">close {{ l.estimatedCloseDate | date:'dd MMM yyyy' }}</span></div>
      <div class="sf-kpi"><span class="l">Activities</span><span class="v">{{ l.activityCount }}</span></div>
      <div class="sf-kpi" *ngIf="l.convertedOpportunity as op"><span class="l">Opportunity</span>
        <span class="v sm"><a class="sf-mono" [routerLink]="[route, 'opportunities', op.uuid]" data-testid="opportunity-link">{{ op.number }}</a></span></div>
    </div>
  </header>

  <div class="sf-pill er ld-reason" *ngIf="l.status === 'DISQUALIFIED' && l.statusReason" data-testid="disqualified-reason">
    Disqualified{{ l.disqualifiedAt ? ' on ' + (l.disqualifiedAt | date:'dd MMM yyyy') : '' }}: {{ l.statusReason }}</div>

  <sf-anchors [sections]="sections" mode="tabs"></sf-anchors>
  <div class="sf-body">
    <section id="sec-details" class="sf-section ld-grid">
      <div class="sf-card">
        <div class="sf-ch"><h2>Contact</h2></div>
        <div class="sf-kv">
          <span>Name</span><span>{{ l.salutation ? l.salutation + ' ' : '' }}{{ l.fullName }}</span>
          <span>Title</span><span>{{ l.jobTitle || '—' }}</span>
          <span>Company</span><span>{{ l.companyName }}<span class="sf-mu" *ngIf="l.companyName === l.fullName"> (individual)</span></span>
          <span>Email</span><span><a *ngIf="l.email" [href]="'mailto:' + l.email">{{ l.email }}</a><span *ngIf="!l.email">—</span></span>
          <span>Phone</span><span>{{ l.phone || '—' }}</span>
          <span>Mobile</span><span>{{ l.mobilePhone || '—' }}</span>
          <span>Website</span><span>{{ l.website || '—' }}</span>
          <span>Address</span><span>{{ address || '—' }}</span>
        </div>
        <div class="sf-ch ld-sub"><h2>Linked account</h2></div>
        <div class="sf-kv" *ngIf="l.account; else notLinked">
          <span>Account</span><span><a [routerLink]="['/portal/pages/customers', l.account.uuid]">{{ l.account.name }}</a> <span class="sf-mono sf-mu">{{ l.account.code }}</span></span>
          <span>Contact</span><span>{{ l.contactPerson?.name || '—' }}</span>
        </div>
        <ng-template #notLinked><div class="sf-mu ld-warn" data-testid="not-linked"><i class="pi pi-info-circle"></i> Not linked yet — the account is created or linked on conversion.</div></ng-template>
      </div>
      <div class="sf-card">
        <div class="sf-ch"><h2>Source</h2></div>
        <div class="sf-kv">
          <span>Source</span><span>{{ l.leadSourceName || '—' }}</span>
          <span>Campaign</span><span>{{ l.campaignName || '—' }}</span>
          <span>Medium</span><span>{{ l.sourceMedium || '—' }}</span>
          <span>Reference</span><span>{{ l.sourceReference || '—' }}</span>
          <ng-container *ngIf="l.externalSourceSystem"><span>External</span><span>{{ l.externalSourceSystem }} {{ l.externalSourceId }}</span></ng-container>
        </div>
        <div class="sf-ch ld-sub"><h2>Qualification (BANT)</h2></div>
        <div class="sf-kv">
          <span>Budget</span><span><i class="pi" [ngClass]="l.budgetConfirmed ? 'pi-check-square' : 'pi-stop'"></i>
            {{ l.budgetAmount != null ? (l.budgetAmount | number:'1.0-2') + ' ' + (l.budgetCurrencyCode || '') : 'not known' }}</span>
          <span>Authority</span><span><i class="pi" [ngClass]="l.authorityConfirmed ? 'pi-check-square' : 'pi-stop'"></i> {{ l.authorityConfirmed ? 'Decision maker' : 'Not confirmed' }}</span>
          <span>Need</span><span><i class="pi" [ngClass]="l.needIdentified ? 'pi-check-square' : 'pi-stop'"></i> {{ l.needIdentified ? 'Identified' : 'Not identified' }}</span>
          <span>Timeline</span><span>{{ label(l.purchaseTimeframe) || '—' }}</span>
        </div>
        <div class="sf-ch ld-sub"><h2>Ownership</h2></div>
        <div class="sf-kv">
          <span>Assigned</span><span>{{ l.assignedToName || 'Unassigned' }}<span class="sf-mu" *ngIf="l.assignedAt"> since {{ l.assignedAt | date:'dd MMM yyyy' }}</span></span>
          <span>Team</span><span>{{ l.salesTeamName || '—' }}</span>
          <span>Previous owner</span><span>{{ l.previousOwnerName || '—' }}</span>
          <span>Priority</span><span>{{ label(l.priority) }}</span>
          <span>Created</span><span>{{ l.createdAt | date:'dd MMM yyyy, HH:mm' }} by {{ l.createdByName || '—' }}</span>
        </div>
      </div>
    </section>

    <section id="sec-topic" class="sf-section sf-card">
      <div class="sf-ch"><h2>Topic</h2></div>
      <p class="ld-topic" data-testid="topic">{{ l.topic }}</p>
      <p class="ld-desc" *ngIf="l.description">{{ l.description }}</p>
    </section>

    <section id="sec-timeline" class="sf-section sf-card">
      <div class="sf-ch"><h2>Timeline</h2></div>
      <app-crm-timeline [entries]="timeline" [total]="timelineTotal" [loading]="timelineLoading" [canAdd]="canLog && l.status !== 'CONVERTED'"
                        [canExport]="canExport" (add)="openActivity($event)" (export)="exportTimeline()" (loadMore)="loadTimeline(true)"
                        (openActivity)="editActivity($event)" (download)="download($event)" (searchChange)="onTimelineSearch($event)"></app-crm-timeline>
    </section>
  </div>

  <sf-panel #panel [tabs]="['Cross-references']">
    <ng-container *ngIf="panel.active() === 'Cross-references'">
      <div data-testid="cross-references">
        <div class="sf-mu sf-sm" *ngIf="!xref?.account">Other leads show here once the lead is linked to an account.</div>
        <ng-container *ngIf="xref?.account">
          <h4 class="ld-xh">Other leads for {{ xref!.account!.name }}</h4>
          <div class="sf-mu sf-sm" *ngIf="!xref!.accountLeads.length">(none)</div>
          <a class="ld-x" *ngFor="let x of xref!.accountLeads" [routerLink]="[route, 'leads', x.uuid]" data-testid="xref-account">
            <span class="sf-mono">{{ x.leadNumber }}</span> {{ x.topic }}<br><span class="sf-mu sf-sm">{{ label(x.status) }} · {{ x.createdAt | date:'dd MMM yyyy' }}</span></a>
          <ng-container *ngIf="xref!.contactPerson">
            <h4 class="ld-xh">Other leads for {{ xref!.contactPerson!.name }}</h4>
            <div class="sf-mu sf-sm" *ngIf="!xref!.contactLeads.length">(none)</div>
            <a class="ld-x" *ngFor="let x of xref!.contactLeads" [routerLink]="[route, 'leads', x.uuid]" data-testid="xref-contact">
              <span class="sf-mono">{{ x.leadNumber }}</span> {{ x.topic }}<br><span class="sf-mu sf-sm">{{ label(x.status) }} · {{ x.createdAt | date:'dd MMM yyyy' }}</span></a>
          </ng-container>
        </ng-container>
      </div>
    </ng-container>
  </sf-panel>
</sf-page>

<app-lead-form-dialog [(visible)]="formVisible" [lead]="lead" (saved)="onSaved($event)"></app-lead-form-dialog>
<app-lead-convert-dialog [(visible)]="convertVisible" [lead]="lead" [canAssign]="canAssign" (converted)="onConverted($event)"></app-lead-convert-dialog>
<app-activity-form-dialog [(visible)]="activityVisible" [leadUuid]="lead?.uuid ?? null" [accountUuid]="lead?.account?.uuid ?? null"
                          [initialType]="activityType" [activity]="editingActivity" [canAssignOthers]="canAssign" (saved)="onActivitySaved()"></app-activity-form-dialog>

<p-dialog [(visible)]="disqualifyVisible" [modal]="true" [draggable]="false" header="Disqualify lead" [style]="{ width: '30rem' }" data-testid="disqualify-dialog">
  <div class="sf-fld"><label for="dq-reason" class="sf-req">Reason</label>
    <textarea pTextarea id="dq-reason" [(ngModel)]="reason" rows="3" maxlength="500" class="w-full" data-testid="disqualify-reason"></textarea></div>
  <ng-template pTemplate="footer">
    <p-button label="Cancel" [text]="true" severity="secondary" (onClick)="disqualifyVisible = false"></p-button>
    <p-button label="Disqualify" severity="danger" [disabled]="!reason.trim()" [loading]="busy" (onClick)="disqualify()" data-testid="disqualify-save"></p-button>
  </ng-template>
</p-dialog>

<p-dialog [(visible)]="assignVisible" [modal]="true" [draggable]="false" header="Assign lead" [style]="{ width: '30rem' }" data-testid="assign-dialog">
  <div class="sf-fields">
    <div class="sf-fld span-3"><label for="as-user">Salesperson</label>
      <p-dropdown inputId="as-user" [options]="userOptions" [(ngModel)]="assignUserId" optionLabel="label" optionValue="value" [filter]="true"
                  filterBy="label" [showClear]="true" placeholder="Unassigned" appendTo="body" styleClass="w-full" data-testid="assign-user"></p-dropdown></div>
    <div class="sf-fld span-3"><label for="as-team">Sales team</label>
      <p-dropdown inputId="as-team" [options]="teamOptions" [(ngModel)]="assignTeamUuid" optionLabel="label" optionValue="value" [showClear]="true"
                  placeholder="Keep current team" appendTo="body" styleClass="w-full"></p-dropdown></div>
  </div>
  <ng-template pTemplate="footer">
    <p-button label="Cancel" [text]="true" severity="secondary" (onClick)="assignVisible = false"></p-button>
    <p-button label="Assign" icon="pi pi-check" [loading]="busy" (onClick)="assign()" data-testid="assign-save"></p-button>
  </ng-template>
</p-dialog>`,
  styles: [`
    .ld-grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(22rem, 1fr)); gap: var(--sms-pad, 1rem); }
    .ld-sub { margin-top: 1rem; }
    .ld-warn { padding: .25rem 0; }
    .ld-topic { font-weight: 600; margin: 0 0 .35rem; }
    .ld-desc { white-space: pre-line; margin: 0; }
    .ld-reason { display: block; margin: 0 1.25rem .5rem; }
    .ld-xh { margin: .75rem 0 .35rem; font-size: .85rem; }
    .ld-x { display: block; padding: .35rem 0; border-bottom: 1px solid var(--sms-border-soft); color: inherit; text-decoration: none; }
    .w-full { width: 100%; }
    :host ::ng-deep .w-full { width: 100%; }
  `]
})
export class LeadDetailComponent implements OnInit, OnDestroy {
  readonly route = CRM_ROUTE;
  readonly tone = leadStatusTone;
  readonly rTone = ratingTone;
  readonly label = crmLabel;
  readonly sections: FlowSection[] = [
    { id: 'sec-details', label: 'Details' }, { id: 'sec-topic', label: 'Topic' }, { id: 'sec-timeline', label: 'Timeline' }
  ];

  lead: LeadDetail | null = null;
  loading = true;
  loadFailed = false;
  busy = false;
  stages: FlowStage[] = [];
  timeline: TimelineEntry[] = [];
  timelineTotal = 0;
  timelineLoading = false;
  private timelinePage = 1;
  private timelineSearch = '';
  xref: CrossReferenceResult | null = null;

  formVisible = false;
  convertVisible = false;
  activityVisible = false;
  activityType: string | null = null;
  editingActivity: Activity | null = null;
  disqualifyVisible = false;
  reason = '';
  assignVisible = false;
  assignUserId: number | null = null;
  assignTeamUuid: string | null = null;
  userOptions: Option<number>[] = [];
  teamOptions: Option[] = [];

  private sub?: Subscription;

  constructor(private crm: CrmService, private lookups: CrmLookupsService, private auth: AuthService, private routeInfo: ActivatedRoute,
              private router: Router, private messages: MessageService, private confirm: ConfirmationService) {}

  get canLog(): boolean { return this.auth.hasPermission('CRM_CREATE_ACTIVITY'); }
  get canExport(): boolean { return this.auth.hasPermission('CRM_EXPORT_TIMELINE'); }
  get canAssign(): boolean { return this.auth.hasPermission('CRM_ASSIGN_LEAD'); }
  can(action: string): boolean { return !!this.lead?.allowedActions.includes(action as never); }

  get address(): string {
    const l = this.lead;
    return l ? [l.addressLine1, l.addressLine2, l.city, l.stateProvince, l.postalCode, l.country].filter(Boolean).join(', ') : '';
  }

  ngOnInit(): void {
    this.sub = this.routeInfo.paramMap.subscribe(p => { const uuid = p.get('uuid'); if (uuid) this.load(uuid); });
  }

  ngOnDestroy(): void { this.sub?.unsubscribe(); }

  load(uuid = this.lead?.uuid): void {
    if (!uuid) return;
    this.loading = true;
    this.crm.getLead(uuid).subscribe({
      next: r => {
        this.loading = false;
        this.loadFailed = false;
        this.setLead(r.result);
        this.loadTimeline();
        this.crm.getLeadCrossReferences(uuid).subscribe({ next: x => this.xref = x.result, error: () => this.xref = null });
      },
      error: () => { this.loading = false; this.loadFailed = true; }
    });
  }

  private setLead(l: LeadDetail): void {
    this.lead = l;
    const disqualified = l.status === 'DISQUALIFIED';
    const flow = ['New', 'Contacted', 'Qualified', disqualified ? 'Disqualified' : 'Converted'];
    const index = { NEW: 0, CONTACTED: 1, QUALIFIED: 2, CONVERTED: 4, DISQUALIFIED: 3 }[l.status] ?? 0;
    this.stages = flowStagesFrom(flow, index, { failed: disqualified });
  }

  loadTimeline(more = false): void {
    if (!this.lead) return;
    this.timelinePage = more ? this.timelinePage + 1 : 1;
    this.timelineLoading = true;
    this.crm.getLeadTimeline(this.lead.uuid, this.timelinePage, 50, this.timelineSearch).subscribe({
      next: r => {
        this.timelineLoading = false;
        this.timeline = more ? [...this.timeline, ...(r.result?.timeline ?? [])] : (r.result?.timeline ?? []);
        this.timelineTotal = r.result?.totalCount ?? 0;
      },
      error: () => { this.timelineLoading = false; }
    });
  }

  onTimelineSearch(text: string): void {
    this.timelineSearch = text;
    this.loadTimeline();
  }

  private act(call: ReturnType<CrmService['qualifyLead']>, done: string): void {
    this.busy = true;
    call.subscribe({
      next: r => { this.busy = false; this.setLead(r.result); this.loadTimeline(); this.messages.add({ severity: 'success', summary: done }); },
      error: err => { this.busy = false; this.toastError(err?.error?.message || 'The change was refused.'); }
    });
  }

  qualify(): void { this.act(this.crm.qualifyLead(this.lead!.uuid), 'Lead qualified'); }
  markContacted(): void { this.act(this.crm.changeLeadStatus(this.lead!.uuid, 'CONTACTED'), 'Lead marked contacted'); }

  reopen(): void {
    this.confirm.confirm({
      header: 'Re-open lead', message: 'Bring this lead back to New?', icon: 'pi pi-replay',
      accept: () => this.act(this.crm.reopenLead(this.lead!.uuid), 'Lead re-opened')
    });
  }

  disqualify(): void {
    this.disqualifyVisible = false;
    this.act(this.crm.disqualifyLead(this.lead!.uuid, this.reason.trim()), 'Lead disqualified');
    this.reason = '';
  }

  openAssign(): void {
    this.assignUserId = this.lead?.assignedToUserId ?? null;
    this.assignTeamUuid = null;
    this.lookups.users().subscribe(u => this.userOptions = CrmLookupsService.userOptions(u));
    this.lookups.teams().subscribe(t => this.teamOptions = t.map(x => ({ value: x.uuid, label: x.name })));
    this.assignVisible = true;
  }

  assign(): void {
    this.assignVisible = false;
    this.act(this.crm.assignLead(this.lead!.uuid, this.assignUserId, this.assignTeamUuid), 'Lead assigned');
  }

  remove(): void {
    this.confirm.confirm({
      header: 'Delete lead', message: `Delete ${this.lead!.leadNumber}? It disappears from every list.`, icon: 'pi pi-trash',
      acceptButtonStyleClass: 'p-button-danger',
      accept: () => this.crm.deleteLead(this.lead!.uuid).subscribe({
        next: () => this.router.navigate([this.route, 'leads']),
        error: err => this.toastError(err?.error?.message || 'The lead could not be deleted.')
      })
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

  onActivitySaved(): void { this.load(); }

  onSaved(l: LeadDetail): void {
    this.setLead(l);
    this.messages.add({ severity: 'success', summary: 'Lead saved' });
  }

  onConverted(r: LeadConversionResult): void {
    this.messages.add({ severity: 'success', summary: 'Lead converted', detail: `Opportunity ${r.opportunityNumber}` });
    this.router.navigate([this.route, 'opportunities', r.opportunityUuid]);
  }

  exportTimeline(): void {
    this.crm.exportLeadTimeline(this.lead!.uuid).subscribe({
      next: blob => saveBlob(blob, `${this.lead!.leadNumber}-timeline.xlsx`),
      error: () => this.toastError('The timeline could not be exported.')
    });
  }

  download(d: ActivityDocument): void {
    this.crm.downloadDocument(d.uuid).subscribe({
      next: blob => saveBlob(blob, d.documentName),
      error: () => this.toastError('The document could not be downloaded.')
    });
  }

  private toastError(detail: string): void { this.messages.add({ severity: 'error', summary: 'Error', detail }); }
}
