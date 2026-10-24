import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
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

import {
  CRM_ROUTE, CrmService, KanbanBoard, LEAD_STATUSES, LeadDetail, LeadListItem, LeadStatus, crmLabel, leadStatusTone, ratingTone
} from '../../../services/crm.service';
import { AuthService } from '../../service/auth.service';
import { FLOW } from '../../../shared/flow';
import { toDateOnly } from '../../../shared/date-only';
import { CrmLookupsService, Option } from '../shared/crm-lookups.service';
import { CrmKanbanComponent, KanbanMove } from '../shared/crm-kanban.component';
import { LeadFormDialogComponent } from './lead-form-dialog.component';

type View = 'list' | 'kanban';
const VIEW_KEY = 'crm.leads.view';

/** A41 §15.1 / §15.2 — every lead: status chips, filters, search, list or kanban, "+ New lead". */
@Component({
  selector: 'app-lead-list',
  standalone: true,
  imports: [CommonModule, RouterModule, FormsModule, ButtonModule, CalendarModule, DropdownModule, IconFieldModule, InputIconModule,
    InputTextModule, SelectButtonModule, TableModule, ToastModule, ...FLOW, CrmKanbanComponent, LeadFormDialogComponent],
  providers: [MessageService],
  template: `
<p-toast></p-toast>
<sf-page [crumbs]="['CRM', 'Leads']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button *ngIf="canCreate" label="New lead" icon="pi pi-plus" (onClick)="formVisible = true" data-testid="new-lead"></p-button>
    <span class="sf-sep"></span>
    <p-button icon="pi pi-filter-slash" label="Clear filters" [text]="true" (onClick)="resetFilters()" data-testid="reset"></p-button>
    <span class="sf-end">
      <p-selectButton [options]="viewOptions" [(ngModel)]="view" optionLabel="label" optionValue="value" (onChange)="onViewChange()"
                      [allowEmpty]="false" data-testid="view-toggle"></p-selectButton>
    </span>
  </div>

  <div class="sf-body">
    <div class="sf-lr-head">
      <div>
        <h1>Leads <span class="sf-count" *ngIf="view === 'list'">{{ total }}</span></h1>
        <div class="sf-sub">Prospects not yet qualified — capture, contact, qualify and convert them</div>
      </div>
      <div class="sf-filters" *ngIf="view === 'list'">
        <button type="button" class="sf-chip" [class.on]="statuses.size === 0" (click)="toggleStatus(null)" data-testid="status-chip-ALL">All</button>
        <button *ngFor="let s of statusList" type="button" class="sf-chip" [class.on]="statuses.has(s.value)" (click)="toggleStatus(s.value)"
                [attr.data-testid]="'status-chip-' + s.value">{{ s.label }}</button>
      </div>
    </div>

    <div class="sf-filters">
      <p-iconfield class="sf-search">
        <p-inputicon styleClass="pi pi-search"></p-inputicon>
        <input pInputText type="text" [(ngModel)]="search" (ngModelChange)="onSearchChange()" placeholder="Search topic, name, company, email, phone or number" data-testid="search" />
      </p-iconfield>
      <p-dropdown [options]="ownerOptions" [(ngModel)]="owner" optionLabel="label" optionValue="value" (onChange)="reload()" appendTo="body" data-testid="owner"></p-dropdown>
      <p-dropdown *ngIf="view === 'list'" [options]="sourceOptions" [(ngModel)]="sourceUuid" optionLabel="label" optionValue="value" [showClear]="true"
                  placeholder="All sources" (onChange)="reload()" appendTo="body" data-testid="source"></p-dropdown>
      <p-dropdown [options]="teamOptions" [(ngModel)]="teamUuid" optionLabel="label" optionValue="value" [showClear]="true"
                  placeholder="All teams" (onChange)="reload()" appendTo="body" data-testid="team"></p-dropdown>
      <p-dropdown [options]="ratingOptions" [(ngModel)]="rating" optionLabel="label" optionValue="value" [showClear]="true"
                  placeholder="Any rating" (onChange)="reload()" appendTo="body" data-testid="rating"></p-dropdown>
      <p-calendar [(ngModel)]="dateRange" selectionMode="range" [readonlyInput]="true" [showIcon]="true" [showButtonBar]="true" todayButtonStyleClass="" clearButtonStyleClass="" dateFormat="dd M yy"
                  placeholder="Created between" (ngModelChange)="onDateChange()" appendTo="body"></p-calendar>
    </div>

    <div class="sf-card p0" *ngIf="view === 'list'">
      <p-table [value]="leads" [lazy]="true" (onLazyLoad)="onPage($event)" [paginator]="true" [rows]="pageSize" [totalRecords]="total"
               [loading]="loading" [rowsPerPageOptions]="[25, 50, 100]" styleClass="p-datatable-sm" dataKey="uuid"
               [tableStyle]="{ 'min-width': '60rem' }" data-testid="leads">
        <ng-template pTemplate="header">
          <tr><th>#</th><th>Lead</th><th>Company</th><th>Source</th><th>Rating</th><th>Stage</th><th>Status</th><th class="text-right">Estimate</th></tr>
        </ng-template>
        <ng-template pTemplate="body" let-l>
          <tr class="ll-row" (click)="open(l)" [attr.data-testid]="'lead-' + l.leadNumber">
            <td><a class="sf-mono" [routerLink]="[route, 'leads', l.uuid]" (click)="$event.stopPropagation()"><strong>{{ l.leadNumber }}</strong></a></td>
            <td><strong>{{ l.fullName }}</strong><div class="sf-mu sf-sm">{{ l.topic }}</div></td>
            <td>{{ l.companyName }}</td>
            <td>{{ l.leadSourceName || '—' }}<div class="sf-mu sf-sm" *ngIf="l.campaignName">{{ l.campaignName }}</div></td>
            <td><span class="sf-pill" [ngClass]="rTone(l.rating)">{{ label(l.rating) }}</span></td>
            <td>{{ l.pipelineStageName || '—' }}</td>
            <td><span class="sf-pill" [ngClass]="tone(l.status)" data-testid="status">{{ label(l.status) }}</span>
              <div class="sf-mu sf-sm">{{ l.assignedToName || 'Unassigned' }}</div></td>
            <td class="text-right">{{ l.estimatedRevenue != null ? (l.estimatedRevenue | number:'1.0-0') + ' ' + (l.currencyCode || '') : '—' }}</td>
          </tr>
        </ng-template>
        <ng-template pTemplate="emptymessage"><tr><td colspan="8" class="sf-empty">No leads found.</td></tr></ng-template>
      </p-table>
    </div>

    <ng-container *ngIf="view === 'kanban'">
      <div class="sf-empty" *ngIf="loading && !board"><i class="pi pi-spin pi-spinner"></i> Loading…</div>
      <app-crm-kanban *ngIf="board" [columns]="board.columns" [canDrag]="canEdit" [currency]="board.mixedCurrencies ? '' : boardCurrency"
                      (moved)="onMoved($event)">
        <ng-template #card let-l>
          <a class="kc" [routerLink]="[route, 'leads', l.uuid]" [attr.data-testid]="'card-' + l.leadNumber">
            <div class="kc-top"><span class="sf-mono sf-sm">{{ l.leadNumber }}</span><span class="sf-pill" [ngClass]="rTone(l.rating)">{{ label(l.rating) }}</span></div>
            <div class="kc-name">{{ l.fullName }}</div>
            <div class="sf-mu sf-sm">{{ l.companyName }}</div>
            <div class="kc-foot"><span>{{ l.assignedToName || 'Unassigned' }}</span>
              <span *ngIf="l.estimatedRevenue != null">{{ l.estimatedRevenue | number:'1.0-0' }} {{ l.currencyCode }}</span></div>
          </a>
        </ng-template>
      </app-crm-kanban>
      <div class="sf-mu sf-sm" *ngIf="board?.unstaged?.length">{{ board!.unstaged.length }} lead(s) are in a stage outside this pipeline (another team's).</div>
    </ng-container>
  </div>
</sf-page>

<app-lead-form-dialog [(visible)]="formVisible" [canAssign]="canAssign" (saved)="onCreated($event)"></app-lead-form-dialog>`,
  styles: [`
    .ll-row { cursor: pointer; }
    .kc { display: block; color: inherit; text-decoration: none; }
    .kc-top { display: flex; justify-content: space-between; align-items: center; margin-bottom: .2rem; }
    .kc-name { font-weight: 600; }
    .kc-foot { display: flex; justify-content: space-between; font-size: .78rem; color: var(--sms-text-muted); margin-top: .35rem; }
  `]
})
export class LeadListComponent implements OnInit, OnDestroy {
  readonly route = CRM_ROUTE;
  readonly statusList = LEAD_STATUSES;
  readonly viewOptions = [{ label: 'List', value: 'list' as View }, { label: 'Kanban', value: 'kanban' as View }];
  readonly tone = leadStatusTone;
  readonly rTone = ratingTone;
  readonly label = crmLabel;

  view: View = 'list';
  leads: LeadListItem[] = [];
  board: KanbanBoard<LeadListItem> | null = null;
  boardCurrency = '';
  total = 0;
  page = 1;
  pageSize = 25;
  loading = false;
  formVisible = false;

  statuses = new Set<LeadStatus>();
  search = '';
  owner: 'all' | 'mine' | 'unassigned' = 'all';
  sourceUuid: string | null = null;
  teamUuid: string | null = null;
  rating: string | null = null;
  dateRange: Date[] | null = null;

  ownerOptions: Option<'all' | 'mine' | 'unassigned'>[] = [
    { value: 'all', label: 'Everyone\'s leads' }, { value: 'mine', label: 'My leads' }, { value: 'unassigned', label: 'Unassigned' }
  ];
  sourceOptions: Option[] = [];
  teamOptions: Option[] = [];
  ratingOptions: Option[] = [];

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  constructor(private crm: CrmService, private lookups: CrmLookupsService, private auth: AuthService, private router: Router,
              private routeInfo: ActivatedRoute, private messages: MessageService) {}

  get canCreate(): boolean { return this.auth.hasPermission('CRM_CREATE_LEAD'); }
  get canAssign(): boolean { return this.auth.hasPermission('CRM_ASSIGN_LEAD'); }
  get canEdit(): boolean { return this.auth.hasPermission('CRM_EDIT_LEAD'); }

  ngOnInit(): void {
    this.lookups.reset();
    try { this.view = (localStorage.getItem(VIEW_KEY) as View) || 'list'; } catch { /* storage blocked */ }
    if (this.routeInfo.snapshot.queryParamMap.get('view') === 'kanban') this.view = 'kanban';
    this.lookups.sources().subscribe(s => this.sourceOptions = s.map(x => ({ value: x.uuid, label: x.name })));
    this.lookups.teams().subscribe(t => this.teamOptions = t.map(x => ({ value: x.uuid, label: x.name })));
    this.lookups.meta().subscribe(m => this.ratingOptions = CrmLookupsService.options(m?.ratings));
    this.lookups.currencies().subscribe(c => this.boardCurrency = c.base ?? '');
    if (this.view === 'kanban') this.loadBoard(); // the list's first load is the table's onLazyLoad
  }

  ngOnDestroy(): void { if (this.searchTimer) clearTimeout(this.searchTimer); }

  private dates(): { from?: string; to?: string } {
    const [from, to] = this.dateRange ?? [];
    return { from: from ? toDateOnly(from) : undefined, to: (to ?? from) ? toDateOnly((to ?? from)!) : undefined };
  }

  load(): void {
    this.loading = true;
    this.crm.getLeads({
      page: this.page, pageSize: this.pageSize, search: this.search.trim() || undefined, status: [...this.statuses],
      leadSourceUuid: this.sourceUuid ?? undefined, salesTeamUuid: this.teamUuid ?? undefined, rating: this.rating ?? undefined,
      mine: this.owner === 'mine' || undefined, unassigned: this.owner === 'unassigned' || undefined, ...this.dates()
    }).subscribe({
      next: r => { this.loading = false; this.leads = r.result?.data ?? []; this.total = r.result?.totalRecords ?? 0; },
      error: () => { this.loading = false; this.leads = []; this.total = 0; this.toastError('Failed to load leads.'); }
    });
  }

  loadBoard(): void {
    this.loading = true;
    this.crm.getLeadKanban({
      search: this.search.trim() || undefined, salesTeamUuid: this.teamUuid ?? undefined, rating: this.rating ?? undefined,
      mine: this.owner === 'mine' || undefined, ...this.dates()
    }).subscribe({
      next: r => { this.loading = false; this.board = r.result; },
      error: () => { this.loading = false; this.board = null; this.toastError('Failed to load the lead pipeline.'); }
    });
  }

  reload(): void {
    this.page = 1;
    if (this.view === 'list') this.load(); else this.loadBoard();
  }

  onViewChange(): void {
    try { localStorage.setItem(VIEW_KEY, this.view); } catch { /* ignore */ }
    if (this.view === 'kanban') this.loadBoard(); else this.load();
  }

  toggleStatus(s: LeadStatus | null): void {
    if (s === null) this.statuses.clear();
    else if (this.statuses.has(s)) this.statuses.delete(s);
    else this.statuses.add(s);
    this.reload();
  }

  onSearchChange(): void {
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => this.reload(), 400);
  }

  onDateChange(): void {
    if (this.dateRange && this.dateRange.length === 2 && !this.dateRange[1]) return;
    this.reload();
  }

  onPage(e: TableLazyLoadEvent): void {
    this.pageSize = e.rows ?? this.pageSize;
    this.page = Math.floor((e.first ?? 0) / this.pageSize) + 1;
    this.load();
  }

  resetFilters(): void {
    this.statuses.clear();
    this.search = '';
    this.owner = 'all';
    this.sourceUuid = this.teamUuid = this.rating = null;
    this.dateRange = null;
    this.reload();
  }

  open(l: LeadListItem): void { this.router.navigate([this.route, 'leads', l.uuid]); }

  onCreated(lead: LeadDetail): void {
    this.messages.add({ severity: 'success', summary: 'Lead created', detail: lead.leadNumber });
    this.router.navigate([this.route, 'leads', lead.uuid]);
  }

  onMoved(m: KanbanMove<LeadListItem>): void {
    this.crm.moveLeadStage(m.item.uuid, m.to.stageUuid).subscribe({
      next: () => this.loadBoard(),
      error: err => { this.toastError(err?.error?.message || 'The lead could not be moved.'); this.loadBoard(); }
    });
  }

  private toastError(detail: string): void { this.messages.add({ severity: 'error', summary: 'Error', detail }); }
}
