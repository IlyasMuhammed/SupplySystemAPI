import { Component, DestroyRef, OnInit, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';
import { Subject, debounceTime } from 'rxjs';

import { FLOW } from '../../../shared/flow';
import { AuthService } from '../../service/auth.service';
import { HrmApi } from '../core/hrm.api';
import { HrmDialogComponent } from '../core/hrm-dialog.component';
import { Field, HrmFormComponent, cleanBody, formDefaults, formProblem, lookupsOf } from '../core/hrm-form.component';
import { HrmLookups } from '../core/hrm-lookups.service';
import { ENUMS, Opt, hrmError, hrmLabel } from '../core/hrm.shared';

const SETTINGS_FIELDS: Field[] = [
  { key: 'payrollCostCenterUuid', label: 'Payroll cost center', type: 'lookup', lookup: 'costCenters', required: true },
  { key: 'payrollCostCenterMode', label: 'Posting mode', type: 'select', options: ENUMS.costMode, required: true, hint: 'A change applies to future runs only.' },
  { key: 'postDatePolicy', label: 'Posting date', type: 'select', options: ENUMS.postDatePolicy, required: true },
  { key: 'employeeNumberPrefix', label: 'Employee number prefix', maxLength: 10, required: true },
  { key: 'countryCode', label: 'Country code', maxLength: 2, required: true },
  { key: 'workingDaysDivisor', label: 'Working-days divisor', type: 'number', min: 1, max: 31, step: 1, hint: 'Empty uses the calendar days of the month.' },
  { key: 'defaultExpenseSettlement', label: 'Expense reimbursement default', type: 'select', options: ENUMS.settlementMethod, required: true },
  { key: 'defaultReceiptThreshold', label: 'Receipt needed above', type: 'number', min: 0 }
];

/** Contract §2.1, §2.7 — organization-wide HR settings and the extra HR scopes per user. Read for the three codes, edit for HRM_SETTINGS_MANAGE. */
@Component({
  selector: 'app-hr-settings',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, InputTextModule, ToastModule, HrmFormComponent, HrmDialogComponent, ...FLOW],
  providers: [MessageService, HrmLookups],
  styleUrls: ['../../finance/gl/gl.scss', '../core/hrm.scss'],
  template: `
<p-toast></p-toast>
<sf-page [crumbs]="['HR', 'Settings']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button *ngIf="canEdit" label="Save" icon="pi pi-check" [loading]="busy" [disabled]="busy || !values" (onClick)="save()" data-testid="save"></p-button>
  </div>
  <div class="sf-body">
    <div class="sf-lr-head"><div><h1>HR settings</h1><div class="sf-sub">One organization is one employer and one payroll legal entity.</div></div></div>
    <div class="gl-note er" *ngIf="error" role="alert" data-testid="error"><i class="pi pi-exclamation-triangle"></i><div>{{ error }}</div></div>

    <section class="sf-card" *ngIf="values && canEdit"><hrm-form [fields]="fields" [values]="values" [lookups]="lookups" idPrefix="set"></hrm-form></section>
    <section class="sf-card" *ngIf="values && !canEdit" data-testid="readonly">
      <div class="sf-kv">
        <span>Payroll cost center</span><span>{{ values['payrollCostCenterName'] || '—' }} {{ values['payrollCostCenterCode'] ? '(' + values['payrollCostCenterCode'] + ')' : '' }}</span>
        <span>Posting mode</span><span>{{ hrmLabel(values['payrollCostCenterMode']) }}</span>
        <span>Posting date</span><span>{{ hrmLabel(values['postDatePolicy']) }}</span>
        <span>Employee number prefix</span><span>{{ values['employeeNumberPrefix'] }}</span>
      </div>
    </section>

    <section class="sf-card p0" *ngIf="canEdit" data-testid="scopes">
      <div class="sf-ch sf-pad"><h2>Extra HR scopes</h2><span class="sf-ch-actions"><p-button label="Add" icon="pi pi-plus" [text]="true" size="small" (onClick)="openScope()" data-testid="add-scope"></p-button></span></div>
      <div class="sf-pad sf-filters">
        <input pInputText class="sf-search" placeholder="Search name or email" aria-label="Search users" [(ngModel)]="userSearch" (ngModelChange)="search$.next()" data-testid="scope-search" />
        <select class="sf-search" [(ngModel)]="scopeUser" (ngModelChange)="loadScopes()" aria-label="User" data-testid="scope-user" style="max-width:24rem">
          <option [ngValue]="null">Choose a user…</option>
          <option *ngFor="let u of users" [ngValue]="u.value">{{ u.label }}</option>
        </select>
      </div>
      <div class="gl-scroll"><table class="hrm-table hrm-stack">
        <thead><tr><th>Scope</th><th>Branch or department</th><th></th></tr></thead>
        <tbody>
          <tr *ngFor="let s of scopes" [attr.data-testid]="'scope-' + s.uuid"><td data-label="Scope">{{ s.scopeType }}</td><td data-label="On">{{ s.branchName || s.departmentName }}</td>
            <td class="hrm-acts"><div class="hrm-actions"><p-button label="Remove" icon="pi pi-trash" [text]="true" size="small" severity="danger" (onClick)="removeScope(s)"></p-button></div></td></tr>
          <tr *ngIf="!scopes.length"><td class="hrm-empty sf-empty" colspan="3">{{ scopeUser ? 'No extra scopes for this user.' : 'Choose a user to see their extra scopes.' }}</td></tr>
        </tbody>
      </table></div>
    </section>
  </div>
</sf-page>

<hrm-dialog [(visible)]="scopeOpen" title="Add HR scope" subtitle="The user sees people in this branch or department." icon="pi-eye" [busy]="busy" [error]="scopeError" (save)="saveScope()">
  <hrm-form *ngIf="scopeOpen" [fields]="scopeFields" [values]="scopeValues" [lookups]="lookups" idPrefix="sc"></hrm-form>
</hrm-dialog>`
})
export class HrSettingsComponent implements OnInit {
  private readonly api = inject(HrmApi);
  private readonly auth = inject(AuthService);
  private readonly messages = inject(MessageService);
  private readonly destroyRef = inject(DestroyRef);
  readonly lookups = inject(HrmLookups);

  readonly fields = SETTINGS_FIELDS;
  readonly hrmLabel = hrmLabel;
  readonly scopeFields: Field[] = [
    { key: 'kind', label: 'Scope on', type: 'select', required: true, def: 'branch', options: [{ label: 'Branch', value: 'branch' }, { label: 'Department', value: 'department' }] },
    { key: 'branchUuid', label: 'Branch', type: 'lookup', lookup: 'branches', required: true, visible: v => v['kind'] === 'branch' },
    { key: 'departmentUuid', label: 'Department', type: 'lookup', lookup: 'departments', required: true, visible: v => v['kind'] === 'department' }
  ];

  values: Record<string, any> | null = null;
  error = '';
  busy = false;
  users: Opt[] = [];
  userSearch = '';
  readonly search$ = new Subject<void>();
  scopeUser: number | null = null;
  scopes: any[] = [];
  scopeOpen = false;
  scopeValues: Record<string, any> = {};
  scopeError = '';

  get canEdit(): boolean { return this.auth.hasPermission('HRM_SETTINGS_MANAGE'); }

  ngOnInit(): void {
    this.lookups.need(lookupsOf(SETTINGS_FIELDS)).subscribe();
    this.api.get<any>('hrm/settings').subscribe({
      next: s => { this.values = s ?? {}; },
      error: err => { this.error = hrmError(err, 'The settings could not be loaded.'); }
    });
    if (this.canEdit) this.loadUsers();
    this.search$.pipe(debounceTime(300), takeUntilDestroyed(this.destroyRef)).subscribe(() => this.loadUsers());
  }

  /** Contract Changes (FE-4): active users of this organization, at most 50 per search. */
  loadUsers(): void {
    this.api.get<any[]>('hrm/user-scopes/users', { search: this.userSearch.trim() }).subscribe({
      next: r => { this.users = (r ?? []).map(u => ({ label: `${u.name} (${u.email})`, value: u.userId })); },
      error: err => { this.users = []; this.error = hrmError(err, 'The users could not be loaded.'); }
    });
  }

  save(): void {
    if (!this.values) return;
    this.error = formProblem(SETTINGS_FIELDS, this.values) ?? '';
    if (this.error) return;
    const v = cleanBody(SETTINGS_FIELDS, this.values);
    this.busy = true;
    this.api.put<any>('hrm/settings', Object.fromEntries(SETTINGS_FIELDS.map(f => [f.key, v[f.key]]))).subscribe({
      next: s => { this.busy = false; if (s) this.values = s; this.messages.add({ severity: 'success', summary: 'Saved', detail: 'The settings are saved.', life: 3000 }); },
      error: err => { this.busy = false; this.error = hrmError(err, 'The settings could not be saved.'); }
    });
  }

  loadScopes(): void {
    if (!this.scopeUser) { this.scopes = []; return; }
    this.api.get<any[]>('hrm/user-scopes', { userId: this.scopeUser }).subscribe({
      next: r => { this.scopes = r ?? []; },
      error: err => { this.scopes = []; this.error = hrmError(err, 'The scopes could not be loaded.'); }
    });
  }

  openScope(): void {
    if (!this.scopeUser) { this.error = 'Choose a user first.'; return; }
    this.error = '';
    this.scopeError = '';
    this.scopeValues = formDefaults(this.scopeFields);
    this.lookups.need(lookupsOf(this.scopeFields)).subscribe(() => { this.scopeOpen = true; });
  }

  saveScope(): void {
    this.scopeError = formProblem(this.scopeFields, this.scopeValues) ?? '';
    if (this.scopeError) return;
    const v = this.scopeValues;
    const body = v['kind'] === 'branch' ? { userId: this.scopeUser, branchUuid: v['branchUuid'] } : { userId: this.scopeUser, departmentUuid: v['departmentUuid'] };
    this.busy = true;
    this.api.post('hrm/user-scopes', body).subscribe({
      next: () => { this.busy = false; this.scopeOpen = false; this.loadScopes(); },
      error: err => { this.busy = false; this.scopeError = hrmError(err, 'The scope could not be added.'); }
    });
  }

  removeScope(s: any): void {
    this.api.delete(`hrm/user-scopes/${s.uuid}`).subscribe({
      next: () => this.loadScopes(),
      error: err => this.messages.add({ severity: 'error', summary: 'Not removed', detail: hrmError(err, 'The scope could not be removed.'), life: 8000 })
    });
  }
}
