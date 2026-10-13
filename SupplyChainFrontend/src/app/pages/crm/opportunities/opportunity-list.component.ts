import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router, RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { CalendarModule } from 'primeng/calendar';
import { CheckboxModule } from 'primeng/checkbox';
import { DropdownModule } from 'primeng/dropdown';
import { IconFieldModule } from 'primeng/iconfield';
import { InputIconModule } from 'primeng/inputicon';
import { InputTextModule } from 'primeng/inputtext';
import { SelectButtonModule } from 'primeng/selectbutton';
import { TableLazyLoadEvent, TableModule } from 'primeng/table';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';

import {
  CRM_ROUTE, CrmService, KanbanBoard, OpportunityListItem, crmLabel, opportunityStatusTone
} from '../../../services/crm.service';
import { AuthService } from '../../service/auth.service';
import { FLOW } from '../../../shared/flow';
import { toDateOnly } from '../../../shared/date-only';
import { CrmLookupsService, Option } from '../shared/crm-lookups.service';
import { CrmKanbanComponent, KanbanMove } from '../shared/crm-kanban.component';

type View = 'list' | 'kanban';
const VIEW_KEY = 'crm.opportunities.view';

/** A41 register 1J/1K — every opportunity: status chips, filters, list or kanban (by opportunity stage). */
@Component({
  selector: 'app-opportunity-list',
  standalone: true,
  imports: [CommonModule, RouterModule, FormsModule, ButtonModule, CalendarModule, CheckboxModule, DropdownModule, IconFieldModule, InputIconModule,
    InputTextModule, SelectButtonModule, TableModule, ToastModule, ...FLOW, CrmKanbanComponent],
  providers: [MessageService],
  template: `
<p-toast></p-toast>
<sf-page [crumbs]="['CRM', 'Opportunities']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button label="Leads" icon="pi pi-user-plus" [text]="true" [routerLink]="[route, 'leads']"></p-button>
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
        <h1>Opportunities <span class="sf-count" *ngIf="view === 'list'">{{ total }}</span></h1>
        <div class="sf-sub">Qualified deals moving through the pipeline to a sale order — opportunities come from converted leads</div>
      </div>
      <div class="sf-filters" *ngIf="view === 'list'">
        <button type="button" class="sf-chip" [class.on]="!status" (click)="setStatus(null)" data-testid="status-chip-ALL">All</button>
        <button *ngFor="let s of statusList" type="button" class="sf-chip" [class.on]="status === s" (click)="setStatus(s)"
                [attr.data-testid]="'status-chip-' + s">{{ label(s) }}</button>
      </div>
    </div>

    <div class="sf-filters">
      <p-iconfield class="sf-search">
        <p-inputicon styleClass="pi pi-search"></p-inputicon>
        <input pInputText type="text" [(ngModel)]="search" (ngModelChange)="onSearchChange()" placeholder="Search topic, number or company" data-testid="search" />
      </p-iconfield>
      <p-dropdown [options]="ownerOptions" [(ngModel)]="mine" optionLabel="label" optionValue="value" (onChange)="reload()" appendTo="body"></p-dropdown>
      <p-dropdown [options]="teamOptions" [(ngModel)]="teamUuid" optionLabel="label" optionValue="value" [showClear]="true" placeholder="All teams"
                  (onChange)="reload()" appendTo="body"></p-dropdown>
      <p-calendar [(ngModel)]="closeRange" selectionMode="range" [readonlyInput]="true" [showIcon]="true" [showButtonBar]="true" dateFormat="dd M yy"
                  placeholder="Closing between" (ngModelChange)="onDateChange()" appendTo="body"></p-calendar>
      <span class="ol-check" *ngIf="view === 'kanban'"><p-checkbox [(ngModel)]="includeClosed" [binary]="true" inputId="ol-closed" (onChange)="reload()"></p-checkbox>
        <label for="ol-closed">Show won &amp; lost</label></span>
    </div>

    <div class="sf-card p0" *ngIf="view === 'list'">
      <p-table [value]="items" [lazy]="true" (onLazyLoad)="onPage($event)" [paginator]="true" [rows]="pageSize" [totalRecords]="total"
               [loading]="loading" [rowsPerPageOptions]="[25, 50, 100]" styleClass="p-datatable-sm" dataKey="uuid"
               [tableStyle]="{ 'min-width': '60rem' }" data-testid="opportunities">
        <ng-template pTemplate="header">
          <tr><th>#</th><th>Topic</th><th>Account</th><th>Stage</th><th>Status</th><th class="text-right">Revenue</th><th>Close</th><th>Owner</th></tr>
        </ng-template>
        <ng-template pTemplate="body" let-o>
          <tr class="ol-row" (click)="open(o)" [attr.data-testid]="'opp-' + o.opportunityNumber">
            <td><a class="sf-mono" [routerLink]="[route, 'opportunities', o.uuid]" (click)="$event.stopPropagation()"><strong>{{ o.opportunityNumber }}</strong></a></td>
            <td>{{ o.topic }}<div class="sf-mu sf-sm">from {{ o.sourceLeadNumber }}</div></td>
            <td>{{ o.accountName || '—' }}<div class="sf-mu sf-sm" *ngIf="o.contactPersonName">{{ o.contactPersonName }}</div></td>
            <td>{{ o.pipelineStageName || '—' }}<span class="sf-mu" *ngIf="o.winProbability != null"> · {{ o.winProbability }}%</span></td>
            <td><span class="sf-pill" [ngClass]="tone(o.status)" data-testid="status">{{ label(o.status) }}</span></td>
            <td class="text-right">{{ revenue(o) }}</td>
            <td>{{ (o.actualCloseDate || o.estimatedCloseDate) ? ((o.actualCloseDate || o.estimatedCloseDate) | date:'dd MMM yyyy') : '—' }}</td>
            <td>{{ o.assignedToName || '—' }}</td>
          </tr>
        </ng-template>
        <ng-template pTemplate="emptymessage"><tr><td colspan="8" class="sf-empty">No opportunities found.</td></tr></ng-template>
      </p-table>
    </div>

    <ng-container *ngIf="view === 'kanban'">
      <div class="sf-empty" *ngIf="loading && !board"><i class="pi pi-spin pi-spinner"></i> Loading…</div>
      <app-crm-kanban *ngIf="board" [columns]="board.columns" [canDrag]="canEdit" [currency]="board.mixedCurrencies ? '' : boardCurrency" (moved)="onMoved($event)">
        <ng-template #card let-o>
          <a class="kc" [routerLink]="[route, 'opportunities', o.uuid]" [attr.data-testid]="'card-' + o.opportunityNumber">
            <div class="kc-top"><span class="sf-mono sf-sm">{{ o.opportunityNumber }}</span>
              <span class="sf-pill" [ngClass]="tone(o.status)" *ngIf="o.status !== 'OPEN'">{{ label(o.status) }}</span></div>
            <div class="kc-name">{{ o.topic }}</div>
            <div class="sf-mu sf-sm">{{ o.accountName }}</div>
            <div class="kc-foot"><span>{{ o.assignedToName || 'Unassigned' }}</span><span>{{ revenue(o) }}</span></div>
          </a>
        </ng-template>
      </app-crm-kanban>
    </ng-container>
  </div>
</sf-page>`,
  styles: [`
    .ol-row { cursor: pointer; }
    .ol-check { display: inline-flex; align-items: center; gap: .4rem; }
    .kc { display: block; color: inherit; text-decoration: none; }
    .kc-top { display: flex; justify-content: space-between; align-items: center; margin-bottom: .2rem; }
    .kc-name { font-weight: 600; }
    .kc-foot { display: flex; justify-content: space-between; font-size: .78rem; color: var(--sms-text-muted); margin-top: .35rem; }
  `]
})
export class OpportunityListComponent implements OnInit, OnDestroy {
  readonly route = CRM_ROUTE;
  readonly statusList = ['OPEN', 'WON', 'LOST'];
  readonly viewOptions = [{ label: 'List', value: 'list' as View }, { label: 'Kanban', value: 'kanban' as View }];
  readonly tone = opportunityStatusTone;
  readonly label = crmLabel;

  view: View = 'list';
  items: OpportunityListItem[] = [];
  board: KanbanBoard<OpportunityListItem> | null = null;
  boardCurrency = '';
  total = 0;
  page = 1;
  pageSize = 25;
  loading = false;
  status: string | null = null;
  search = '';
  mine = false;
  teamUuid: string | null = null;
  closeRange: Date[] | null = null;
  includeClosed = false;
  ownerOptions: Option<boolean>[] = [{ value: false, label: 'Everyone\'s' }, { value: true, label: 'Mine' }];
  teamOptions: Option[] = [];
  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  constructor(private crm: CrmService, private lookups: CrmLookupsService, private auth: AuthService, private router: Router,
              private messages: MessageService) {}

  get canEdit(): boolean { return this.auth.hasPermission('CRM_EDIT_OPPORTUNITY'); }

  ngOnInit(): void {
    this.lookups.reset();
    try { this.view = (localStorage.getItem(VIEW_KEY) as View) || 'list'; } catch { /* storage blocked */ }
    this.lookups.teams().subscribe(t => this.teamOptions = t.map(x => ({ value: x.uuid, label: x.name })));
    this.lookups.currencies().subscribe(c => this.boardCurrency = c.base ?? '');
    if (this.view === 'kanban') this.loadBoard();
  }

  ngOnDestroy(): void { if (this.searchTimer) clearTimeout(this.searchTimer); }

  revenue(o: OpportunityListItem): string {
    const v = o.status === 'WON' ? o.actualRevenue : o.estimatedRevenue;
    return v == null ? '—' : `${v.toLocaleString(undefined, { maximumFractionDigits: 0 })} ${o.currencyCode ?? ''}`.trim();
  }

  private range(): { closeFrom?: string; closeTo?: string } {
    const [from, to] = this.closeRange ?? [];
    return { closeFrom: from ? toDateOnly(from) : undefined, closeTo: (to ?? from) ? toDateOnly((to ?? from)!) : undefined };
  }

  load(): void {
    this.loading = true;
    this.crm.getOpportunities({
      page: this.page, pageSize: this.pageSize, status: this.status ?? undefined, search: this.search.trim() || undefined,
      mine: this.mine || undefined, salesTeamUuid: this.teamUuid ?? undefined, ...this.range()
    }).subscribe({
      next: r => { this.loading = false; this.items = r.result?.data ?? []; this.total = r.result?.totalRecords ?? 0; },
      error: () => { this.loading = false; this.items = []; this.total = 0; this.toastError('Failed to load opportunities.'); }
    });
  }

  loadBoard(): void {
    this.loading = true;
    this.crm.getOpportunityKanban({
      search: this.search.trim() || undefined, mine: this.mine || undefined, salesTeamUuid: this.teamUuid ?? undefined,
      includeClosed: this.includeClosed || undefined, ...this.range()
    }).subscribe({
      next: r => { this.loading = false; this.board = r.result; },
      error: () => { this.loading = false; this.board = null; this.toastError('Failed to load the pipeline.'); }
    });
  }

  reload(): void { this.page = 1; if (this.view === 'list') this.load(); else this.loadBoard(); }

  onViewChange(): void {
    try { localStorage.setItem(VIEW_KEY, this.view); } catch { /* ignore */ }
    if (this.view === 'kanban') this.loadBoard(); else this.load();
  }

  setStatus(s: string | null): void { this.status = s; this.reload(); }

  onSearchChange(): void {
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => this.reload(), 400);
  }

  onDateChange(): void {
    if (this.closeRange && this.closeRange.length === 2 && !this.closeRange[1]) return;
    this.reload();
  }

  onPage(e: TableLazyLoadEvent): void {
    this.pageSize = e.rows ?? this.pageSize;
    this.page = Math.floor((e.first ?? 0) / this.pageSize) + 1;
    this.load();
  }

  resetFilters(): void {
    this.status = null;
    this.search = '';
    this.mine = false;
    this.teamUuid = null;
    this.closeRange = null;
    this.includeClosed = false;
    this.reload();
  }

  open(o: OpportunityListItem): void { this.router.navigate([this.route, 'opportunities', o.uuid]); }

  onMoved(m: KanbanMove<OpportunityListItem>): void {
    this.crm.moveOpportunityStage(m.item.uuid, m.to.stageUuid).subscribe({
      next: () => this.loadBoard(),
      error: err => { this.toastError(err?.error?.message || 'The opportunity could not be moved.'); this.loadBoard(); }
    });
  }

  private toastError(detail: string): void { this.messages.add({ severity: 'error', summary: 'Error', detail }); }
}
