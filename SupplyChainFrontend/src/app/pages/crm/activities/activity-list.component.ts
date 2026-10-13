import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { CalendarModule } from 'primeng/calendar';
import { DropdownModule } from 'primeng/dropdown';
import { IconFieldModule } from 'primeng/iconfield';
import { InputIconModule } from 'primeng/inputicon';
import { InputTextModule } from 'primeng/inputtext';
import { SelectButtonModule } from 'primeng/selectbutton';
import { TableLazyLoadEvent, TableModule } from 'primeng/table';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';

import { Activity, CRM_ROUTE, CrmService, PendingActivity, activityIcon, crmLabel } from '../../../services/crm.service';
import { AuthService } from '../../service/auth.service';
import { FLOW } from '../../../shared/flow';
import { toDateOnly } from '../../../shared/date-only';
import { CrmLookupsService, Option } from '../shared/crm-lookups.service';
import { ActivityFormDialogComponent } from '../shared/activity-form-dialog.component';

type Tab = 'pending' | 'all';

/** A41 BR-20 / register 1G — the salesperson's follow-ups and tasks due, and every activity with filters. */
@Component({
  selector: 'app-crm-activity-list',
  standalone: true,
  imports: [CommonModule, RouterModule, FormsModule, ButtonModule, CalendarModule, DropdownModule, IconFieldModule, InputIconModule, InputTextModule,
    SelectButtonModule, TableModule, ToastModule, ...FLOW, ActivityFormDialogComponent],
  providers: [MessageService],
  template: `
<p-toast></p-toast>
<sf-page [crumbs]="['CRM', 'Activities']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-selectButton [options]="tabOptions" [(ngModel)]="tab" optionLabel="label" optionValue="value" [allowEmpty]="false" (onChange)="refresh()"
                    data-testid="tab"></p-selectButton>
    <span class="sf-end"><p-button icon="pi pi-refresh" label="Refresh" [text]="true" (onClick)="refresh()"></p-button></span>
  </div>
  <div class="sf-body">
    <div class="sf-lr-head">
      <div>
        <h1>{{ tab === 'pending' ? 'Follow-ups & tasks' : 'Activities' }} <span class="sf-count">{{ tab === 'pending' ? pending.length : total }}</span></h1>
        <div class="sf-sub">{{ tab === 'pending' ? 'What is due next — follow-ups, open tasks and scheduled calls or meetings' : 'Every call, meeting, email, note and task logged on leads and opportunities' }}</div>
      </div>
    </div>

    <ng-container *ngIf="tab === 'pending'">
      <div class="sf-filters">
        <p-dropdown *ngIf="isManager" [options]="pendingScope" [(ngModel)]="pendingMine" optionLabel="label" optionValue="value" (onChange)="loadPending()" appendTo="body"></p-dropdown>
        <p-dropdown [options]="dueOptions" [(ngModel)]="dueWithin" optionLabel="label" optionValue="value" (onChange)="loadPending()" appendTo="body"></p-dropdown>
      </div>
      <div class="sf-card p0">
        <p-table [value]="pending" [loading]="loading" styleClass="p-datatable-sm" data-testid="pending">
          <ng-template pTemplate="header"><tr><th>Due</th><th>What</th><th>Regarding</th><th>Next step</th><th>Owner</th><th></th></tr></ng-template>
          <ng-template pTemplate="body" let-p>
            <tr [attr.data-testid]="'pending-' + p.activity.uuid">
              <td><span class="sf-pill" [ngClass]="p.overdue ? 'er' : 'wn'">{{ p.dueOn | date:'dd MMM yyyy' }}</span>
                <div class="sf-mu sf-sm">{{ label(p.reason) }}</div></td>
              <td><i class="pi" [ngClass]="icon(p.activity.activityType)"></i> {{ p.activity.subject }}</td>
              <td><a *ngIf="p.activity.leadUuid" class="sf-mono" [routerLink]="[route, 'leads', p.activity.leadUuid]">{{ p.activity.leadNumber }}</a>
                <a *ngIf="p.activity.opportunityUuid" class="sf-mono" [routerLink]="[route, 'opportunities', p.activity.opportunityUuid]">{{ p.activity.opportunityNumber }}</a>
                <div class="sf-mu sf-sm">{{ p.activity.regardingName }}</div></td>
              <td>{{ p.activity.followUpType ? label(p.activity.followUpType) + ': ' : '' }}{{ p.activity.followUpNotes || '—' }}</td>
              <td>{{ p.activity.performedByName }}</td>
              <td class="text-right"><p-button *ngIf="p.activity.canEdit" label="Done" icon="pi pi-check" size="small" [text]="true" (onClick)="complete(p.activity)"
                                               data-testid="complete"></p-button></td>
            </tr>
          </ng-template>
          <ng-template pTemplate="emptymessage"><tr><td colspan="6" class="sf-empty">Nothing due. </td></tr></ng-template>
        </p-table>
      </div>
    </ng-container>

    <ng-container *ngIf="tab === 'all'">
      <div class="sf-filters">
        <p-iconfield class="sf-search"><p-inputicon styleClass="pi pi-search"></p-inputicon>
          <input pInputText [(ngModel)]="search" (ngModelChange)="onSearch()" placeholder="Search subject, lead or opportunity" /></p-iconfield>
        <p-dropdown [options]="typeOptions" [(ngModel)]="type" optionLabel="label" optionValue="value" [showClear]="true" placeholder="All types" (onChange)="reload()" appendTo="body"></p-dropdown>
        <p-dropdown [options]="stateOptions" [(ngModel)]="state" optionLabel="label" optionValue="value" (onChange)="reload()" appendTo="body"></p-dropdown>
        <p-dropdown [options]="ownerOptions" [(ngModel)]="mine" optionLabel="label" optionValue="value" (onChange)="reload()" appendTo="body"></p-dropdown>
        <p-calendar [(ngModel)]="range" selectionMode="range" [readonlyInput]="true" [showIcon]="true" [showButtonBar]="true" dateFormat="dd M yy"
                    placeholder="Between" (ngModelChange)="onRange()" appendTo="body"></p-calendar>
      </div>
      <div class="sf-card p0">
        <p-table [value]="activities" [lazy]="true" (onLazyLoad)="onPage($event)" [paginator]="true" [rows]="pageSize" [totalRecords]="total"
                 [loading]="loading" [rowsPerPageOptions]="[25, 50, 100]" styleClass="p-datatable-sm" data-testid="activities">
          <ng-template pTemplate="header"><tr><th>When</th><th>Type</th><th>Subject</th><th>Regarding</th><th>By</th><th>Outcome</th></tr></ng-template>
          <ng-template pTemplate="body" let-a>
            <tr class="al-row" (click)="edit(a)">
              <td>{{ a.activityDate | date:'dd MMM yyyy, HH:mm' }}</td>
              <td><i class="pi" [ngClass]="icon(a.activityType)"></i> {{ label(a.activityType) }}</td>
              <td>{{ a.subject }}<span class="sf-pill wn al-pill" *ngIf="!a.isCompleted && (a.dueDate || a.followUpDate || a.scheduledDate)">Open</span></td>
              <td (click)="$event.stopPropagation()">
                <a *ngIf="a.leadUuid" class="sf-mono" [routerLink]="[route, 'leads', a.leadUuid]">{{ a.leadNumber }}</a>
                <a *ngIf="a.opportunityUuid" class="sf-mono" [routerLink]="[route, 'opportunities', a.opportunityUuid]">{{ a.opportunityNumber }}</a>
                <div class="sf-mu sf-sm">{{ a.regardingName }}</div></td>
              <td>{{ a.performedByName }}</td>
              <td>{{ label(a.outcome) || '—' }}</td>
            </tr>
          </ng-template>
          <ng-template pTemplate="emptymessage"><tr><td colspan="6" class="sf-empty">No activities found.</td></tr></ng-template>
        </p-table>
      </div>
    </ng-container>
  </div>
</sf-page>
<app-activity-form-dialog [(visible)]="dialogVisible" [activity]="editing" [leadUuid]="editing?.leadUuid ?? null"
                          [opportunityUuid]="editing?.opportunityUuid ?? null" [canAssignOthers]="isManager" (saved)="refresh()"></app-activity-form-dialog>`,
  styles: [`.al-row { cursor: pointer; } .al-pill { margin-left: .4rem; }`]
})
export class CrmActivityListComponent implements OnInit, OnDestroy {
  readonly route = CRM_ROUTE;
  readonly label = crmLabel;
  readonly icon = activityIcon;
  readonly tabOptions = [{ label: 'Follow-ups', value: 'pending' as Tab }, { label: 'All activities', value: 'all' as Tab }];
  readonly pendingScope: Option<boolean>[] = [{ value: true, label: 'Mine' }, { value: false, label: 'Everyone\'s' }];
  readonly dueOptions: Option<number | null>[] = [{ value: null, label: 'Any date' }, { value: 0, label: 'Due today or overdue' }, { value: 7, label: 'Due within a week' }];
  readonly stateOptions: Option<string | null>[] = [{ value: null, label: 'Open and done' }, { value: 'OPEN', label: 'Open' }, { value: 'DONE', label: 'Done' }];
  readonly ownerOptions: Option<boolean>[] = [{ value: false, label: 'Everyone\'s' }, { value: true, label: 'Mine' }];

  tab: Tab = 'pending';
  loading = false;
  pending: PendingActivity[] = [];
  pendingMine = true;
  dueWithin: number | null = null;
  activities: Activity[] = [];
  total = 0;
  page = 1;
  pageSize = 25;
  search = '';
  type: string | null = null;
  state: string | null = null;
  mine = false;
  range: Date[] | null = null;
  typeOptions: Option[] = [];
  dialogVisible = false;
  editing: Activity | null = null;
  private timer: ReturnType<typeof setTimeout> | null = null;

  constructor(private crm: CrmService, private lookups: CrmLookupsService, private auth: AuthService, private messages: MessageService) {}

  get isManager(): boolean { return this.auth.hasPermission('CRM_ASSIGN_LEAD'); }

  ngOnInit(): void {
    this.lookups.meta().subscribe(m => this.typeOptions = CrmLookupsService.options(m?.activityTypes));
    this.loadPending();
  }

  ngOnDestroy(): void { if (this.timer) clearTimeout(this.timer); }

  refresh(): void { if (this.tab === 'pending') this.loadPending(); else this.load(); }

  loadPending(): void {
    this.loading = true;
    const dueBy = this.dueWithin == null ? undefined : toDateOnly(new Date(Date.now() + this.dueWithin * 86400000));
    this.crm.getPendingActivities({ mine: this.isManager ? this.pendingMine : true, dueBy }).subscribe({
      next: r => { this.loading = false; this.pending = r.result ?? []; },
      error: () => { this.loading = false; this.pending = []; this.toastError('Failed to load follow-ups.'); }
    });
  }

  load(): void {
    this.loading = true;
    const [from, to] = this.range ?? [];
    this.crm.getActivities({
      page: this.page, pageSize: this.pageSize, search: this.search.trim() || undefined, activityType: this.type ?? undefined,
      state: (this.state as 'OPEN' | 'DONE') ?? undefined, mine: this.mine || undefined,
      from: from ? toDateOnly(from) : undefined, to: (to ?? from) ? toDateOnly((to ?? from)!) : undefined
    }).subscribe({
      next: r => { this.loading = false; this.activities = r.result?.data ?? []; this.total = r.result?.totalRecords ?? 0; },
      error: () => { this.loading = false; this.activities = []; this.total = 0; this.toastError('Failed to load activities.'); }
    });
  }

  reload(): void { this.page = 1; this.load(); }
  onSearch(): void { if (this.timer) clearTimeout(this.timer); this.timer = setTimeout(() => this.reload(), 400); }
  onRange(): void { if (this.range && this.range.length === 2 && !this.range[1]) return; this.reload(); }
  onPage(e: TableLazyLoadEvent): void { this.pageSize = e.rows ?? this.pageSize; this.page = Math.floor((e.first ?? 0) / this.pageSize) + 1; this.load(); }

  complete(a: Activity): void {
    this.crm.completeActivity(a.uuid).subscribe({
      next: () => { this.messages.add({ severity: 'success', summary: 'Done', detail: a.subject }); this.loadPending(); },
      error: err => this.toastError(err?.error?.message || 'The activity could not be completed.')
    });
  }

  edit(a: Activity): void {
    if (!a.canEdit) return;
    this.crm.getActivity(a.uuid).subscribe({ next: r => { this.editing = r.result; this.dialogVisible = true; } });
  }

  private toastError(detail: string): void { this.messages.add({ severity: 'error', summary: 'Error', detail }); }
}
