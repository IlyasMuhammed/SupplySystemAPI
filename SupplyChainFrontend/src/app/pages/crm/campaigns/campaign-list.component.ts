import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { CalendarModule } from 'primeng/calendar';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { DialogModule } from 'primeng/dialog';
import { DropdownModule } from 'primeng/dropdown';
import { IconFieldModule } from 'primeng/iconfield';
import { InputIconModule } from 'primeng/inputicon';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { TableLazyLoadEvent, TableModule } from 'primeng/table';
import { TextareaModule } from 'primeng/textarea';
import { ToastModule } from 'primeng/toast';
import { ConfirmationService, MessageService } from 'primeng/api';

import { CRM_ROUTE, Campaign, CampaignUpsert, CrmService, crmLabel } from '../../../services/crm.service';
import { AuthService } from '../../service/auth.service';
import { FLOW, flowTone } from '../../../shared/flow';
import { fromDateOnly, toDateOnly } from '../../../shared/date-only';
import { CrmLookupsService, Option } from '../shared/crm-lookups.service';

/** A41 C2 §4.3 — marketing campaigns: list, create / edit (status DRAFT → ACTIVE → PAUSED → COMPLETED / CANCELLED), leads brought. */
@Component({
  selector: 'app-campaign-list',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule, ButtonModule, CalendarModule, ConfirmDialogModule, DialogModule, DropdownModule, IconFieldModule,
    InputIconModule, InputNumberModule, InputTextModule, TableModule, TextareaModule, ToastModule, ...FLOW],
  providers: [MessageService, ConfirmationService],
  template: `
<p-toast></p-toast>
<p-confirmDialog></p-confirmDialog>
<sf-page [crumbs]="['CRM', 'Campaigns']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button *ngIf="canManage" label="New campaign" icon="pi pi-plus" (onClick)="openNew()" data-testid="new-campaign"></p-button>
  </div>
  <div class="sf-body">
    <div class="sf-lr-head">
      <div><h1>Campaigns <span class="sf-count">{{ total }}</span></h1>
        <div class="sf-sub">Marketing initiatives leads are attributed to — budget, expected and actual leads</div></div>
      <div class="sf-filters">
        <button type="button" class="sf-chip" [class.on]="!status" (click)="setStatus(null)">All</button>
        <button *ngFor="let s of statusCodes" type="button" class="sf-chip" [class.on]="status === s" (click)="setStatus(s)">{{ label(s) }}</button>
      </div>
    </div>
    <div class="sf-filters">
      <p-iconfield class="sf-search"><p-inputicon styleClass="pi pi-search"></p-inputicon>
        <input pInputText [(ngModel)]="search" (ngModelChange)="reload()" placeholder="Search name or code" /></p-iconfield>
    </div>
    <div class="sf-empty" *ngIf="unavailable" data-testid="campaigns-off">Campaign tracking is switched off for the organization (Settings › Modules).</div>
    <div class="sf-card p0" *ngIf="!unavailable">
      <p-table [value]="campaigns" [lazy]="true" (onLazyLoad)="onPage($event)" [paginator]="true" [rows]="pageSize" [totalRecords]="total"
               [loading]="loading" styleClass="p-datatable-sm" data-testid="campaigns">
        <ng-template pTemplate="header"><tr><th>Code</th><th>Campaign</th><th>Type</th><th>Source</th><th>Dates</th>
          <th class="text-right">Budget</th><th class="text-right">Leads</th><th>Status</th></tr></ng-template>
        <ng-template pTemplate="body" let-c>
          <tr class="cl-row" (click)="openEdit(c)" [attr.data-testid]="'campaign-' + c.code">
            <td class="sf-mono">{{ c.code }}</td>
            <td><strong>{{ c.name }}</strong></td>
            <td>{{ label(c.campaignType) }}</td>
            <td>{{ c.leadSourceName || '—' }}</td>
            <td>{{ c.startDate ? (c.startDate | date:'dd MMM yy') : '—' }} – {{ c.endDate ? (c.endDate | date:'dd MMM yy') : '—' }}</td>
            <td class="text-right">{{ c.budgetAmount != null ? (c.budgetAmount | number:'1.0-0') + ' ' + c.currencyCode : '—' }}</td>
            <td class="text-right">{{ c.actualLeads }}<span class="sf-mu" *ngIf="c.expectedLeads"> / {{ c.expectedLeads }}</span></td>
            <td><span class="sf-pill" [ngClass]="tone(c.status)">{{ label(c.status) }}</span></td>
          </tr>
        </ng-template>
        <ng-template pTemplate="emptymessage"><tr><td colspan="8" class="sf-empty">No campaigns yet.</td></tr></ng-template>
      </p-table>
    </div>
  </div>
</sf-page>

<p-dialog [(visible)]="dialogVisible" [modal]="true" [draggable]="false" [header]="editing ? 'Campaign ' + editing.code : 'New campaign'"
          [style]="{ width: '46rem' }" [breakpoints]="{ '760px': '96vw' }" data-testid="campaign-dialog">
  <div class="sf-pill er cl-err" *ngIf="error">{{ error }}</div>
  <div class="cl-stats" *ngIf="editing?.leadsByStatus as by">
    <span *ngFor="let k of statusKeys(by)" class="sf-pill">{{ label(k) }} {{ by[k] }}</span>
    <span class="sf-pill in" *ngIf="editing?.opportunityCount">Opportunities {{ editing?.opportunityCount }}</span>
  </div>
  <div class="sf-fields">
    <div class="sf-fld"><label for="c-code" class="sf-req">Code</label><input pInputText id="c-code" [(ngModel)]="f.code" maxlength="30" [disabled]="!canManage" data-testid="campaign-code" /></div>
    <div class="sf-fld span-2"><label for="c-name" class="sf-req">Name</label><input pInputText id="c-name" [(ngModel)]="f.name" maxlength="200" [disabled]="!canManage" data-testid="campaign-name" /></div>
    <div class="sf-fld"><label for="c-type">Type</label>
      <p-dropdown inputId="c-type" [options]="typeOptions" [(ngModel)]="f.campaignType" optionLabel="label" optionValue="value" appendTo="body" styleClass="w-full" [disabled]="!canManage"></p-dropdown></div>
    <div class="sf-fld"><label for="c-status">Status</label>
      <p-dropdown inputId="c-status" [options]="statusOptions" [(ngModel)]="f.status" optionLabel="label" optionValue="value" appendTo="body" styleClass="w-full" [disabled]="!canManage"></p-dropdown></div>
    <div class="sf-fld"><label for="c-source">Lead source</label>
      <p-dropdown inputId="c-source" [options]="sourceOptions" [(ngModel)]="f.leadSourceUuid" optionLabel="label" optionValue="value" [showClear]="true" appendTo="body" styleClass="w-full" [disabled]="!canManage"></p-dropdown></div>
    <div class="sf-fld"><label for="c-start">Start</label>
      <p-calendar inputId="c-start" [(ngModel)]="start" dateFormat="dd M yy" [showIcon]="true" [showButtonBar]="true" todayButtonStyleClass="" clearButtonStyleClass="" styleClass="w-full" [disabled]="!canManage"></p-calendar></div>
    <div class="sf-fld"><label for="c-end">End</label>
      <p-calendar inputId="c-end" [(ngModel)]="end" dateFormat="dd M yy" [showIcon]="true" [showButtonBar]="true" todayButtonStyleClass="" clearButtonStyleClass="" styleClass="w-full" [disabled]="!canManage"></p-calendar></div>
    <div class="sf-fld"><label for="c-expected">Expected leads</label>
      <p-inputnumber inputId="c-expected" [(ngModel)]="f.expectedLeads" [min]="0" [useGrouping]="false" styleClass="w-full" [disabled]="!canManage"></p-inputnumber></div>
    <div class="sf-fld"><label for="c-budget">Budget</label>
      <p-inputnumber inputId="c-budget" [(ngModel)]="f.budgetAmount" mode="decimal" [maxFractionDigits]="2" [min]="0" styleClass="w-full" [disabled]="!canManage"></p-inputnumber></div>
    <div class="sf-fld"><label for="c-cur">Currency</label>
      <p-dropdown inputId="c-cur" [options]="currencyOptions" [(ngModel)]="f.currencyCode" [showClear]="true" appendTo="body" styleClass="w-full" [disabled]="!canManage"></p-dropdown></div>
    <div class="sf-fld"><label for="c-ext">External reference</label><input pInputText id="c-ext" [(ngModel)]="f.externalReference" maxlength="200" placeholder="Facebook / Google Ads id" [disabled]="!canManage" /></div>
    <div class="sf-fld span-3"><label for="c-desc">Description</label><textarea pTextarea id="c-desc" [(ngModel)]="f.description" rows="2" maxlength="2000" class="w-full" [disabled]="!canManage"></textarea></div>
  </div>
  <ng-template pTemplate="footer">
    <p-button *ngIf="editing && canManage" label="Delete" icon="pi pi-trash" severity="danger" [text]="true" (onClick)="remove()" styleClass="cl-del"></p-button>
    <p-button label="Close" [text]="true" severity="secondary" (onClick)="dialogVisible = false"></p-button>
    <p-button *ngIf="canManage" [label]="editing ? 'Save' : 'Create'" icon="pi pi-check" [loading]="saving" (onClick)="save()" data-testid="campaign-save"></p-button>
  </ng-template>
</p-dialog>`,
  styles: [`
    .cl-row { cursor: pointer; }
    .cl-err { display: block; margin-bottom: .75rem; }
    .cl-stats { display: flex; gap: .35rem; flex-wrap: wrap; margin-bottom: .75rem; }
    .w-full { width: 100%; }
    :host ::ng-deep .w-full { width: 100%; }
    :host ::ng-deep .cl-del { margin-right: auto; }
  `]
})
export class CampaignListComponent implements OnInit {
  readonly route = CRM_ROUTE;
  readonly label = crmLabel;
  readonly tone = flowTone;
  readonly statusCodes = ['DRAFT', 'ACTIVE', 'PAUSED', 'COMPLETED', 'CANCELLED'];

  campaigns: Campaign[] = [];
  total = 0;
  page = 1;
  pageSize = 25;
  loading = false;
  unavailable = false;
  status: string | null = null;
  search = '';

  dialogVisible = false;
  editing: Campaign | null = null;
  f: CampaignUpsert = {};
  start: Date | null = null;
  end: Date | null = null;
  saving = false;
  error: string | null = null;
  typeOptions: Option[] = [];
  statusOptions: Option[] = [];
  sourceOptions: Option[] = [];
  currencyOptions: string[] = [];

  constructor(private crm: CrmService, private lookups: CrmLookupsService, private auth: AuthService, private messages: MessageService,
              private confirm: ConfirmationService) {}

  get canManage(): boolean { return this.auth.hasPermission('CRM_MANAGE_CAMPAIGNS'); }

  ngOnInit(): void {
    this.lookups.meta().subscribe(m => {
      this.typeOptions = CrmLookupsService.options(m?.campaignTypes);
      this.statusOptions = CrmLookupsService.options(m?.campaignStatuses);
    });
    this.lookups.sources().subscribe(s => this.sourceOptions = s.map(x => ({ value: x.uuid, label: x.name })));
    this.lookups.currencies().subscribe(c => this.currencyOptions = c.codes);
  }

  statusKeys(by: Record<string, number>): string[] { return Object.keys(by); }

  load(): void {
    this.loading = true;
    this.crm.getCampaigns({ page: this.page, pageSize: this.pageSize, status: this.status ?? undefined, search: this.search.trim() || undefined }).subscribe({
      next: r => { this.loading = false; this.unavailable = false; this.campaigns = r.result?.data ?? []; this.total = r.result?.totalRecords ?? 0; },
      error: err => { this.loading = false; this.campaigns = []; this.total = 0; this.unavailable = err?.status === 403; }
    });
  }

  reload(): void { this.page = 1; this.load(); }
  setStatus(s: string | null): void { this.status = s; this.reload(); }
  onPage(e: TableLazyLoadEvent): void { this.pageSize = e.rows ?? this.pageSize; this.page = Math.floor((e.first ?? 0) / this.pageSize) + 1; this.load(); }

  openNew(): void {
    this.editing = null;
    this.error = null;
    this.f = { campaignType: 'GENERAL', status: 'DRAFT' };
    this.start = this.end = null;
    this.dialogVisible = true;
  }

  openEdit(c: Campaign): void {
    this.error = null;
    this.crm.getCampaign(c.uuid).subscribe({
      next: r => {
        const d = r.result;
        this.editing = d;
        this.f = {
          code: d.code, name: d.name, description: d.description, campaignType: d.campaignType, leadSourceUuid: d.leadSourceUuid, status: d.status,
          budgetAmount: d.budgetAmount, currencyCode: d.currencyCode, expectedLeads: d.expectedLeads, externalReference: d.externalReference, notes: d.notes
        };
        this.start = d.startDate ? fromDateOnly(d.startDate) : null;
        this.end = d.endDate ? fromDateOnly(d.endDate) : null;
        this.dialogVisible = true;
      }
    });
  }

  save(): void {
    this.error = null;
    if (this.start && this.end && this.end < this.start) { this.error = 'The end date cannot be before the start date.'; return; }
    const req: CampaignUpsert = { ...this.f, startDate: this.start ? toDateOnly(this.start) : null, endDate: this.end ? toDateOnly(this.end) : null };
    this.saving = true;
    (this.editing ? this.crm.updateCampaign(this.editing.uuid, req) : this.crm.createCampaign(req)).subscribe({
      next: () => { this.saving = false; this.dialogVisible = false; this.lookups.reset(); this.load(); this.messages.add({ severity: 'success', summary: 'Campaign saved' }); },
      error: err => { this.saving = false; this.error = err?.error?.message || 'The campaign could not be saved.'; }
    });
  }

  remove(): void {
    const c = this.editing!;
    this.confirm.confirm({
      header: 'Delete campaign', message: `Delete ${c.name}? Leads keep it in their history.`, icon: 'pi pi-trash',
      accept: () => this.crm.deleteCampaign(c.uuid).subscribe({
        next: () => { this.dialogVisible = false; this.load(); },
        error: err => this.error = err?.error?.message || 'The campaign could not be deleted.'
      })
    });
  }
}
