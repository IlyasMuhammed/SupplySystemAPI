import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { CheckboxModule } from 'primeng/checkbox';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { DialogModule } from 'primeng/dialog';
import { DropdownModule } from 'primeng/dropdown';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { SelectButtonModule } from 'primeng/selectbutton';
import { TableModule } from 'primeng/table';
import { ToastModule } from 'primeng/toast';
import { ConfirmationService, MessageService } from 'primeng/api';

import {
  CrmService, LeadApiKey, LeadSource, LeadSourceUpsert, PipelineStage, PipelineStageUpsert, SalesTeam, SalesTeamUpsert, crmLabel
} from '../../../services/crm.service';
import { environment } from '../../../../environments/environment';
import { FLOW } from '../../../shared/flow';
import { CrmLookupsService, Option } from '../shared/crm-lookups.service';

type Tab = 'sources' | 'stages' | 'teams' | 'keys';

/** A41 C2 / C3 / C5 / D-16 — CRM configuration (CRM_ADMIN): lead sources, pipeline stages per pipeline, sales teams, public API keys. */
@Component({
  selector: 'app-crm-settings',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, CheckboxModule, ConfirmDialogModule, DialogModule, DropdownModule, InputNumberModule, InputTextModule,
    SelectButtonModule, TableModule, ToastModule, ...FLOW],
  providers: [MessageService, ConfirmationService],
  template: `
<p-toast></p-toast>
<p-confirmDialog></p-confirmDialog>
<sf-page [crumbs]="['Settings', 'CRM']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-selectButton [options]="tabs" [(ngModel)]="tab" optionLabel="label" optionValue="value" [allowEmpty]="false" (onChange)="load()" data-testid="settings-tab"></p-selectButton>
    <span class="sf-end">
      <p-button *ngIf="tab === 'sources'" label="New lead source" icon="pi pi-plus" (onClick)="editSource(null)" data-testid="new-source"></p-button>
      <p-button *ngIf="tab === 'stages'" label="New stage" icon="pi pi-plus" (onClick)="editStage(null)" data-testid="new-stage"></p-button>
      <p-button *ngIf="tab === 'teams'" label="New team" icon="pi pi-plus" (onClick)="editTeam(null)" data-testid="new-team"></p-button>
      <p-button *ngIf="tab === 'keys'" label="New API key" icon="pi pi-plus" (onClick)="keyDialog = true; keyName = ''; keyLimit = 100; newKey = null" data-testid="new-key"></p-button>
    </span>
  </div>
  <div class="sf-body">
    <!-- Lead sources -->
    <ng-container *ngIf="tab === 'sources'">
      <div class="sf-lr-head"><div><h1>Lead sources</h1><div class="sf-sub">How leads reach you; the built-in ones keep their code</div></div></div>
      <div class="sf-card p0">
        <p-table [value]="sources" styleClass="p-datatable-sm" data-testid="sources">
          <ng-template pTemplate="header"><tr><th>Order</th><th>Code</th><th>Name</th><th>Description</th><th>Active</th><th></th></tr></ng-template>
          <ng-template pTemplate="body" let-s>
            <tr><td>{{ s.sortOrder }}</td><td class="sf-mono">{{ s.code }}<span class="sf-mu" *ngIf="s.isSystem"> · built-in</span></td><td>{{ s.name }}</td>
              <td>{{ s.description || '—' }}</td><td><span class="sf-pill" [ngClass]="s.isActive ? 'ok' : ''">{{ s.isActive ? 'Active' : 'Inactive' }}</span></td>
              <td class="text-right"><p-button icon="pi pi-pencil" [text]="true" (onClick)="editSource(s)" ariaLabel="Edit"></p-button></td></tr>
          </ng-template>
        </p-table>
      </div>
    </ng-container>

    <!-- Pipeline stages -->
    <ng-container *ngIf="tab === 'stages'">
      <div class="sf-lr-head"><div><h1>Pipeline stages</h1><div class="sf-sub">The organization's pipeline, or a team's own; a team without stages of its own uses the organization's</div></div></div>
      <div class="sf-filters">
        <p-dropdown [options]="pipelineOptions" [(ngModel)]="pipelineTeam" optionLabel="label" optionValue="value" (onChange)="loadStages()" appendTo="body" data-testid="pipeline"></p-dropdown>
        <span class="cs-check"><p-checkbox [(ngModel)]="showInactive" [binary]="true" inputId="cs-inactive" (onChange)="loadStages()"></p-checkbox><label for="cs-inactive">Show inactive</label></span>
      </div>
      <div class="sf-card p0">
        <p-table [value]="stages" styleClass="p-datatable-sm" data-testid="stages">
          <ng-template pTemplate="header"><tr><th>Type</th><th>Seq.</th><th>Code</th><th>Name</th><th>Win %</th><th>Closing</th><th>Pipeline</th><th></th></tr></ng-template>
          <ng-template pTemplate="body" let-s>
            <tr [class.cs-off]="!s.isActive"><td>{{ label(s.stageType) }}</td><td>{{ s.sequence }}</td><td class="sf-mono">{{ s.code }}</td><td>{{ s.name }}</td>
              <td>{{ s.winProbability ?? '—' }}</td><td>{{ s.isWon ? 'Won' : s.isLost ? 'Lost' : '' }}</td><td>{{ s.salesTeamName || 'Organization' }}</td>
              <td class="text-right">
                <p-button icon="pi pi-pencil" [text]="true" (onClick)="editStage(s)" ariaLabel="Edit"></p-button>
                <p-button *ngIf="s.isActive" icon="pi pi-eye-slash" [text]="true" severity="danger" (onClick)="deactivateStage(s)" ariaLabel="Deactivate"></p-button>
              </td></tr>
          </ng-template>
        </p-table>
      </div>
    </ng-container>

    <!-- Sales teams -->
    <ng-container *ngIf="tab === 'teams'">
      <div class="sf-lr-head"><div><h1>Sales teams</h1><div class="sf-sub">Salespeople grouped for assignment and pipelines; one person can be in several teams</div></div></div>
      <div class="sf-card p0">
        <p-table [value]="teams" styleClass="p-datatable-sm" dataKey="uuid" data-testid="teams">
          <ng-template pTemplate="header"><tr><th>Code</th><th>Team</th><th>Leader</th><th>Members</th><th>Default campaign</th><th>Pipeline</th><th></th></tr></ng-template>
          <ng-template pTemplate="body" let-t>
            <tr [class.cs-off]="!t.isActive"><td class="sf-mono">{{ t.code }}</td><td>{{ t.name }}</td><td>{{ t.teamLeaderName || '—' }}</td>
              <td>{{ memberNames(t) }}</td><td>{{ t.defaultCampaignName || '—' }}</td><td>{{ t.hasOwnStages ? 'Own stages' : 'Organization' }}</td>
              <td class="text-right"><p-button icon="pi pi-pencil" [text]="true" (onClick)="editTeam(t)" ariaLabel="Edit"></p-button></td></tr>
          </ng-template>
        </p-table>
      </div>
    </ng-container>

    <!-- API keys -->
    <ng-container *ngIf="tab === 'keys'">
      <div class="sf-lr-head"><div><h1>Public lead API</h1>
        <div class="sf-sub">Website forms and other systems create leads with <span class="sf-mono">POST {{ publicUrl }}</span> and the header <span class="sf-mono">X-Api-Key</span></div></div></div>
      <div class="sf-card p0">
        <p-table [value]="keys" styleClass="p-datatable-sm" data-testid="keys">
          <ng-template pTemplate="header"><tr><th>Name</th><th>Key</th><th class="text-right">Per minute</th><th>Last used</th><th>Status</th><th></th></tr></ng-template>
          <ng-template pTemplate="body" let-k>
            <tr><td>{{ k.name }}</td><td class="sf-mono">{{ k.keyPrefix }}…</td><td class="text-right">{{ k.requestsPerMinute }}</td>
              <td>{{ k.lastUsedAt ? (k.lastUsedAt | date:'dd MMM yyyy, HH:mm') : 'Never' }}</td>
              <td><span class="sf-pill" [ngClass]="k.isActive ? 'ok' : 'er'">{{ k.isActive ? 'Active' : 'Revoked' }}</span></td>
              <td class="text-right">
                <p-button [label]="k.isActive ? 'Revoke' : 'Restore'" [text]="true" size="small" (onClick)="toggleKey(k)"></p-button>
                <p-button icon="pi pi-trash" [text]="true" severity="danger" (onClick)="deleteKey(k)" ariaLabel="Delete"></p-button>
              </td></tr>
          </ng-template>
          <ng-template pTemplate="emptymessage"><tr><td colspan="6" class="sf-empty">No API keys yet.</td></tr></ng-template>
        </p-table>
      </div>
    </ng-container>
  </div>
</sf-page>

<!-- Lead source dialog -->
<p-dialog [(visible)]="sourceDialog" [modal]="true" [draggable]="false" [header]="sourceEditing ? 'Edit lead source' : 'New lead source'" [style]="{ width: '32rem' }">
  <div class="sf-pill er cs-err" *ngIf="error">{{ error }}</div>
  <div class="sf-fields">
    <div class="sf-fld"><label class="sf-req" for="ls-code">Code</label><input pInputText id="ls-code" [(ngModel)]="sf.code" maxlength="30" [disabled]="!!sourceEditing?.isSystem" data-testid="source-code" /></div>
    <div class="sf-fld span-2"><label class="sf-req" for="ls-name">Name</label><input pInputText id="ls-name" [(ngModel)]="sf.name" maxlength="100" data-testid="source-name" /></div>
    <div class="sf-fld span-3"><label for="ls-desc">Description</label><input pInputText id="ls-desc" [(ngModel)]="sf.description" maxlength="500" /></div>
    <div class="sf-fld"><label for="ls-order">Order</label><p-inputnumber inputId="ls-order" [(ngModel)]="sf.sortOrder" [useGrouping]="false" styleClass="w-full"></p-inputnumber></div>
    <div class="sf-fld"><label>&nbsp;</label><span class="cs-check"><p-checkbox [(ngModel)]="sf.isActive" [binary]="true" inputId="ls-active"></p-checkbox><label for="ls-active">Active</label></span></div>
  </div>
  <ng-template pTemplate="footer">
    <p-button label="Cancel" [text]="true" severity="secondary" (onClick)="sourceDialog = false"></p-button>
    <p-button label="Save" icon="pi pi-check" [loading]="saving" (onClick)="saveSource()" data-testid="source-save"></p-button>
  </ng-template>
</p-dialog>

<!-- Stage dialog -->
<p-dialog [(visible)]="stageDialog" [modal]="true" [draggable]="false" [header]="stageEditing ? 'Edit stage' : 'New stage'" [style]="{ width: '36rem' }">
  <div class="sf-pill er cs-err" *ngIf="error">{{ error }}</div>
  <div class="sf-fields">
    <div class="sf-fld"><label class="sf-req" for="st-code">Code</label><input pInputText id="st-code" [(ngModel)]="stf.code" maxlength="30" data-testid="stage-code" /></div>
    <div class="sf-fld span-2"><label class="sf-req" for="st-name">Name</label><input pInputText id="st-name" [(ngModel)]="stf.name" maxlength="100" data-testid="stage-name" /></div>
    <div class="sf-fld"><label for="st-type">Type</label>
      <p-dropdown inputId="st-type" [options]="stageTypeOptions" [(ngModel)]="stf.stageType" optionLabel="label" optionValue="value" appendTo="body" styleClass="w-full"></p-dropdown></div>
    <div class="sf-fld"><label for="st-seq">Sequence</label><p-inputnumber inputId="st-seq" [(ngModel)]="stf.sequence" [useGrouping]="false" styleClass="w-full"></p-inputnumber></div>
    <div class="sf-fld"><label for="st-prob">Win probability %</label><p-inputnumber inputId="st-prob" [(ngModel)]="stf.winProbability" [min]="0" [max]="100" styleClass="w-full"></p-inputnumber></div>
    <div class="sf-fld" *ngIf="!stageEditing"><label for="st-team">Pipeline</label>
      <p-dropdown inputId="st-team" [options]="pipelineOptions" [(ngModel)]="stageTeam" optionLabel="label" optionValue="value" appendTo="body" styleClass="w-full"></p-dropdown></div>
    <div class="sf-fld span-2" *ngIf="stf.stageType === 'OPPORTUNITY'"><label>Closing stage</label>
      <span class="cs-check"><p-checkbox [(ngModel)]="stf.isWon" [binary]="true" inputId="st-won" (onChange)="stf.isLost = stf.isWon ? false : stf.isLost"></p-checkbox><label for="st-won">Won</label>
        <p-checkbox [(ngModel)]="stf.isLost" [binary]="true" inputId="st-lost" (onChange)="stf.isWon = stf.isLost ? false : stf.isWon"></p-checkbox><label for="st-lost">Lost</label></span></div>
    <div class="sf-fld span-3"><label for="st-desc">Description</label><input pInputText id="st-desc" [(ngModel)]="stf.description" maxlength="500" /></div>
  </div>
  <ng-template pTemplate="footer">
    <p-button label="Cancel" [text]="true" severity="secondary" (onClick)="stageDialog = false"></p-button>
    <p-button label="Save" icon="pi pi-check" [loading]="saving" (onClick)="saveStage()" data-testid="stage-save"></p-button>
  </ng-template>
</p-dialog>

<!-- Team dialog -->
<p-dialog [(visible)]="teamDialog" [modal]="true" [draggable]="false" [header]="teamEditing ? 'Team ' + teamEditing.name : 'New team'" [style]="{ width: '40rem' }"
          [breakpoints]="{ '700px': '96vw' }">
  <div class="sf-pill er cs-err" *ngIf="error">{{ error }}</div>
  <div class="sf-fields">
    <div class="sf-fld"><label class="sf-req" for="tm-code">Code</label><input pInputText id="tm-code" [(ngModel)]="tf.code" maxlength="30" data-testid="team-code" /></div>
    <div class="sf-fld span-2"><label class="sf-req" for="tm-name">Name</label><input pInputText id="tm-name" [(ngModel)]="tf.name" maxlength="100" data-testid="team-name" /></div>
    <div class="sf-fld"><label for="tm-lead">Leader</label>
      <p-dropdown inputId="tm-lead" [options]="userOptions" [(ngModel)]="tf.teamLeaderUserId" optionLabel="label" optionValue="value" [filter]="true" filterBy="label" [showClear]="true" appendTo="body" styleClass="w-full"></p-dropdown></div>
    <div class="sf-fld"><label for="tm-camp">Default campaign</label>
      <p-dropdown inputId="tm-camp" [options]="campaignOptions" [(ngModel)]="tf.defaultCampaignUuid" optionLabel="label" optionValue="value" [showClear]="true" appendTo="body" styleClass="w-full"></p-dropdown></div>
    <div class="sf-fld"><label>&nbsp;</label><span class="cs-check"><p-checkbox [(ngModel)]="tf.isActive" [binary]="true" inputId="tm-active"></p-checkbox><label for="tm-active">Active</label></span></div>
    <div class="sf-fld span-3"><label for="tm-desc">Description</label><input pInputText id="tm-desc" [(ngModel)]="tf.description" maxlength="500" /></div>
  </div>
  <ng-container *ngIf="teamEditing">
    <h3 class="cs-h">Members</h3>
    <div class="cs-member" *ngFor="let m of teamEditing.members">
      <span>{{ m.name }} <span class="sf-pill" [ngClass]="m.role === 'LEADER' ? 'in' : ''">{{ label(m.role) }}</span></span>
      <p-button icon="pi pi-times" [text]="true" severity="secondary" (onClick)="removeMember(m.userId)" ariaLabel="Remove"></p-button>
    </div>
    <div class="cs-add">
      <p-dropdown [options]="userOptions" [(ngModel)]="newMember" optionLabel="label" optionValue="value" [filter]="true" filterBy="label" placeholder="Add a member"
                  appendTo="body" styleClass="w-full" data-testid="member-pick"></p-dropdown>
      <p-button label="Add" icon="pi pi-plus" [disabled]="!newMember" (onClick)="addMember()" data-testid="member-add"></p-button>
    </div>
  </ng-container>
  <ng-template pTemplate="footer">
    <p-button label="Close" [text]="true" severity="secondary" (onClick)="teamDialog = false"></p-button>
    <p-button label="Save" icon="pi pi-check" [loading]="saving" (onClick)="saveTeam()" data-testid="team-save"></p-button>
  </ng-template>
</p-dialog>

<!-- API key dialog -->
<p-dialog [(visible)]="keyDialog" [modal]="true" [draggable]="false" header="New API key" [style]="{ width: '34rem' }">
  <div class="sf-pill er cs-err" *ngIf="error">{{ error }}</div>
  <ng-container *ngIf="!newKey">
    <div class="sf-fields">
      <div class="sf-fld span-2"><label class="sf-req" for="k-name">Name</label><input pInputText id="k-name" [(ngModel)]="keyName" maxlength="100" placeholder="e.g. Website contact form" data-testid="key-name" /></div>
      <div class="sf-fld"><label for="k-limit">Per minute</label><p-inputnumber inputId="k-limit" [(ngModel)]="keyLimit" [min]="1" [max]="10000" styleClass="w-full"></p-inputnumber></div>
    </div>
  </ng-container>
  <div *ngIf="newKey" data-testid="key-shown">
    <p>Copy the key now — it is not shown again.</p>
    <div class="cs-key sf-mono">{{ newKey }}</div>
    <p-button label="Copy" icon="pi pi-copy" [text]="true" (onClick)="copyKey()"></p-button>
  </div>
  <ng-template pTemplate="footer">
    <p-button [label]="newKey ? 'Done' : 'Cancel'" [text]="!newKey" severity="secondary" (onClick)="keyDialog = false"></p-button>
    <p-button *ngIf="!newKey" label="Create key" icon="pi pi-key" [loading]="saving" (onClick)="createKey()" data-testid="key-save"></p-button>
  </ng-template>
</p-dialog>`,
  styles: [`
    .cs-check { display: inline-flex; align-items: center; gap: .4rem; }
    .cs-off { opacity: .55; }
    .cs-err { display: block; margin-bottom: .75rem; }
    .cs-h { font-size: .85rem; margin: 1rem 0 .4rem; }
    .cs-member { display: flex; justify-content: space-between; align-items: center; border-bottom: 1px solid var(--sms-border-soft); padding: .2rem 0; }
    .cs-add { display: flex; gap: .5rem; margin-top: .5rem; }
    .cs-key { padding: .6rem; background: var(--sms-bg); border-radius: var(--sms-radius-sm); word-break: break-all; margin-bottom: .4rem; }
    :host ::ng-deep .w-full { width: 100%; }
  `]
})
export class CrmSettingsComponent implements OnInit {
  readonly tabs = [
    { label: 'Lead sources', value: 'sources' as Tab }, { label: 'Pipeline stages', value: 'stages' as Tab },
    { label: 'Sales teams', value: 'teams' as Tab }, { label: 'API keys', value: 'keys' as Tab }
  ];
  readonly stageTypeOptions: Option[] = [{ value: 'LEAD', label: 'Lead' }, { value: 'OPPORTUNITY', label: 'Opportunity' }];
  readonly publicUrl = `${environment.apiUrl}/public/crm/leads`;
  readonly label = crmLabel;

  tab: Tab = 'sources';
  sources: LeadSource[] = [];
  stages: PipelineStage[] = [];
  teams: SalesTeam[] = [];
  keys: LeadApiKey[] = [];
  pipelineTeam = '';
  showInactive = false;
  pipelineOptions: Option[] = [{ value: '', label: 'Organization pipeline' }];
  userOptions: Option<number>[] = [];
  campaignOptions: Option[] = [];
  saving = false;
  error: string | null = null;

  sourceDialog = false; sourceEditing: LeadSource | null = null; sf: LeadSourceUpsert = {};
  stageDialog = false; stageEditing: PipelineStage | null = null; stf: PipelineStageUpsert = {}; stageTeam = '';
  teamDialog = false; teamEditing: SalesTeam | null = null; tf: SalesTeamUpsert = {}; newMember: number | null = null;
  keyDialog = false; keyName = ''; keyLimit = 100; newKey: string | null = null;

  constructor(private crm: CrmService, private lookups: CrmLookupsService, private messages: MessageService, private confirm: ConfirmationService) {}

  ngOnInit(): void {
    this.lookups.reset();
    this.lookups.assignableUsers().subscribe(u => this.userOptions = CrmLookupsService.userOptions(u));
    this.lookups.campaigns().subscribe(c => this.campaignOptions = c.map(x => ({ value: x.uuid, label: x.name })));
    this.load();
  }

  load(): void {
    this.error = null;
    switch (this.tab) {
      case 'sources': this.crm.getLeadSources(true).subscribe({ next: r => this.sources = r.result ?? [] }); break;
      case 'stages': this.loadTeams(); this.loadStages(); break;
      case 'teams': this.loadTeams(); break;
      case 'keys': this.crm.getApiKeys().subscribe({ next: r => this.keys = r.result ?? [] }); break;
    }
  }

  private loadTeams(): void {
    this.crm.getSalesTeams(true).subscribe({
      next: r => {
        this.teams = r.result ?? [];
        this.pipelineOptions = [{ value: '', label: 'Organization pipeline' }, ...this.teams.map(t => ({ value: t.uuid, label: `Team: ${t.name}` }))];
      }
    });
  }

  loadStages(): void {
    this.crm.getPipelineStages({ salesTeamUuid: this.pipelineTeam || undefined, includeInactive: this.showInactive }).subscribe({ next: r => this.stages = r.result ?? [] });
  }

  memberNames(t: SalesTeam): string { return t.members.map(m => m.name).join(', ') || '—'; }

  private done(msg: string, reload = true): void {
    this.saving = false;
    this.lookups.reset();
    this.messages.add({ severity: 'success', summary: msg });
    if (reload) this.load();
  }

  private fail(err: { error?: { message?: string } }, fallback: string): void { this.saving = false; this.error = err?.error?.message || fallback; }

  // lead sources
  editSource(s: LeadSource | null): void {
    this.error = null;
    this.sourceEditing = s;
    this.sf = s ? { code: s.code, name: s.name, description: s.description, isActive: s.isActive, sortOrder: s.sortOrder } : { isActive: true };
    this.sourceDialog = true;
  }

  saveSource(): void {
    this.saving = true;
    (this.sourceEditing ? this.crm.updateLeadSource(this.sourceEditing.uuid, this.sf) : this.crm.createLeadSource(this.sf)).subscribe({
      next: () => { this.sourceDialog = false; this.done('Lead source saved'); }, error: e => this.fail(e, 'The lead source could not be saved.')
    });
  }

  // stages
  editStage(s: PipelineStage | null): void {
    this.error = null;
    this.stageEditing = s;
    this.stageTeam = this.pipelineTeam;
    this.stf = s ? { code: s.code, name: s.name, stageType: s.stageType, sequence: s.sequence, winProbability: s.winProbability, isWon: s.isWon, isLost: s.isLost, description: s.description }
      : { stageType: 'LEAD', sequence: (this.stages.at(-1)?.sequence ?? 0) + 10, isWon: false, isLost: false };
    this.stageDialog = true;
  }

  saveStage(): void {
    this.saving = true;
    const req = { ...this.stf, salesTeamUuid: this.stageEditing ? undefined : (this.stageTeam || null) };
    (this.stageEditing ? this.crm.updatePipelineStage(this.stageEditing.uuid, req) : this.crm.createPipelineStage(req)).subscribe({
      next: () => { this.stageDialog = false; this.done('Stage saved'); }, error: e => this.fail(e, 'The stage could not be saved.')
    });
  }

  deactivateStage(s: PipelineStage): void {
    this.confirm.confirm({
      header: 'Deactivate stage', message: `Deactivate "${s.name}"? Records still in it must be moved first.`,
      accept: () => this.crm.deactivatePipelineStage(s.uuid).subscribe({
        next: () => this.done('Stage deactivated'),
        error: e => this.messages.add({ severity: 'error', summary: 'Refused', detail: e?.error?.message || 'The stage could not be deactivated.' })
      })
    });
  }

  // teams
  editTeam(t: SalesTeam | null): void {
    this.error = null;
    this.teamEditing = t;
    this.newMember = null;
    this.tf = t ? { code: t.code, name: t.name, description: t.description, teamLeaderUserId: t.teamLeaderUserId, isActive: t.isActive, defaultCampaignUuid: t.defaultCampaignUuid }
      : { isActive: true };
    this.teamDialog = true;
  }

  saveTeam(): void {
    this.saving = true;
    (this.teamEditing ? this.crm.updateSalesTeam(this.teamEditing.uuid, this.tf) : this.crm.createSalesTeam(this.tf)).subscribe({
      next: r => { this.teamEditing = r.result; this.done('Team saved'); }, error: e => this.fail(e, 'The team could not be saved.')
    });
  }

  addMember(): void {
    if (!this.teamEditing || !this.newMember) return;
    this.crm.addTeamMember(this.teamEditing.uuid, this.newMember).subscribe({
      next: r => { this.teamEditing = r.result; this.newMember = null; this.done('Member added'); }, error: e => this.fail(e, 'The member could not be added.')
    });
  }

  removeMember(userId: number): void {
    if (!this.teamEditing) return;
    this.crm.removeTeamMember(this.teamEditing.uuid, userId).subscribe({
      next: r => { this.teamEditing = r.result; this.done('Member removed'); }, error: e => this.fail(e, 'The member could not be removed.')
    });
  }

  // keys
  createKey(): void {
    this.error = null;
    if (!this.keyName.trim()) { this.error = 'Give the key a name.'; return; }
    this.saving = true;
    this.crm.createApiKey(this.keyName.trim(), this.keyLimit).subscribe({
      next: r => { this.newKey = r.result.key; this.done('API key created'); }, error: e => this.fail(e, 'The key could not be created.')
    });
  }

  copyKey(): void { if (this.newKey) navigator.clipboard?.writeText(this.newKey); }

  toggleKey(k: LeadApiKey): void {
    this.crm.updateApiKey(k.uuid, { isActive: !k.isActive }).subscribe({ next: () => this.done(k.isActive ? 'Key revoked' : 'Key restored') });
  }

  deleteKey(k: LeadApiKey): void {
    this.confirm.confirm({
      header: 'Delete API key', message: `Delete "${k.name}"? Forms using it stop creating leads.`,
      accept: () => this.crm.deleteApiKey(k.uuid).subscribe({ next: () => this.done('Key deleted') })
    });
  }
}
