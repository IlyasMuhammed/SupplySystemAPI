import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { RadioButtonModule } from 'primeng/radiobutton';
import { SelectModule } from 'primeng/select';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';

import { FLOW } from '../../../shared/flow';
import { ModuleAdminService, ModuleService } from '../../../services/module.service';
import { AuthService } from '../../service/auth.service';
import { HrmApi } from '../core/hrm.api';
import { Field, HrmFormComponent, formProblem } from '../core/hrm-form.component';
import { HRM_ROUTE, MODULE_HRM } from '../core/hrm.nav';
import { ENUMS, hrmError, hrmErrorCode, hrmLabel } from '../core/hrm.shared';

export interface SetupStatus { licensed: boolean; enabled: boolean; payrollCostCenter?: { uuid: string; code: string; name: string } | null; mode?: string | null; costCenters: { uuid: string; code: string; name: string; isActive?: boolean }[]; }

const NEW_CC_FIELDS: Field[] = [
  { key: 'code', label: 'Code', required: true, maxLength: 20 }, { key: 'name', label: 'Name', required: true, maxLength: 100 }
];

/**
 * H1P / E-3 (FD-9, contract §10.2) — switching on HRM. The enable route refuses until a payroll cost center is set, so this wizard
 * sets it first: pick an existing one or create one inline (no cost centers yet: straight to create), choose the posting mode, then
 * switch the module on. Its routes are gated by the licence, not the switch, so this page must not sit behind MODULE_HRM.
 */
@Component({
  selector: 'app-hrm-setup',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule, ButtonModule, RadioButtonModule, SelectModule, ToastModule, HrmFormComponent, ...FLOW],
  providers: [MessageService],
  styleUrls: ['../../finance/gl/gl.scss', '../core/hrm.scss'],
  template: `
<p-toast></p-toast>
<sf-page [crumbs]="['Settings', 'Modules', 'Set up HR & Payroll']">
  <header class="sf-hdr sf-compact"><h1>Set up HR &amp; Payroll</h1></header>
  <div class="sf-body">
    <div class="sf-card sf-empty" *ngIf="loading"><i class="pi pi-spin pi-spinner"></i> Loading…</div>
    <div class="gl-note er" *ngIf="error" role="alert" data-testid="error"><i class="pi pi-exclamation-triangle"></i><div>{{ error }}</div></div>

    <ng-container *ngIf="status && !loading">
      <div class="gl-note wn" *ngIf="!status.licensed" data-testid="not-licensed"><i class="pi pi-lock"></i><div>HR &amp; Payroll is not in your organization's plan. Your administrator's provider can add it.</div></div>

      <div class="gl-note ok" *ngIf="status.enabled" data-testid="already-on"><i class="pi pi-check"></i><div>HR &amp; Payroll is switched on.<ng-container *ngIf="status.payrollCostCenter"> Payroll cost center {{ status.payrollCostCenter.code }}.</ng-container> <a [routerLink]="settingsRoute">HR Settings</a></div></div>

      <ng-container *ngIf="status.licensed && !status.enabled">
        <section class="sf-card" data-testid="cc-step">
          <div class="sf-ch"><h2>1. Payroll cost center</h2></div>
          <p class="sf-mu" style="margin-top:0">Salary liabilities (tax, EOBI, net pay) post to this one cost center. It is required, and it cannot be deactivated while it is the payroll cost center.</p>
          <ng-container *ngIf="canSet; else noSet">
            <div class="sf-fields" style="margin-bottom:0.75rem" *ngIf="status.costCenters.length">
              <label class="hrm-reveal"><p-radioButton name="ccChoice" value="existing" [(ngModel)]="choice" inputId="cc-existing"></p-radioButton> Use an existing one</label>
              <label class="hrm-reveal" *ngIf="canCreate"><p-radioButton name="ccChoice" value="new" [(ngModel)]="choice" inputId="cc-new"></p-radioButton> Create a new one</label>
            </div>
            <div class="sf-fld" *ngIf="choice === 'existing'" style="max-width:28rem"><label for="cc-select" class="sf-req">Cost center</label>
              <p-select inputId="cc-select" [options]="ccOptions" optionLabel="label" optionValue="value" [(ngModel)]="selected" [filter]="true" appendTo="body" placeholder="Select" data-testid="cc-select" [style]="{ width: '100%' }"></p-select></div>
            <hrm-form *ngIf="choice === 'new'" [fields]="newFields" [values]="newCc" idPrefix="cc"></hrm-form>
            <div class="gl-note wn" *ngIf="!status.costCenters.length && !canCreate" data-testid="cannot-create"><i class="pi pi-lock"></i><div>There are no cost centers yet, and creating one needs the organization manage permission. Ask someone who holds it.</div></div>
          </ng-container>
          <ng-template #noSet>
            <div class="sf-kv" *ngIf="status.payrollCostCenter" data-testid="cc-set"><span>Set to</span><span>{{ status.payrollCostCenter.name }} ({{ status.payrollCostCenter.code }})</span></div>
            <div class="gl-note wn" *ngIf="!status.payrollCostCenter" data-testid="no-settings-perm"><i class="pi pi-lock"></i><div>Setting it needs the HR settings permission. Ask someone who holds it, then come back to switch the module on.</div></div>
          </ng-template>
        </section>

        <section class="sf-card" *ngIf="canSet" data-testid="mode-step">
          <div class="sf-ch"><h2>2. Posting mode</h2></div>
          <div class="sf-fields">
            <label class="hrm-reveal" style="flex-wrap:nowrap;align-items:flex-start" *ngFor="let m of modes"><p-radioButton name="ccMode" [value]="m.value" [(ngModel)]="mode" [inputId]="'mode-' + m.value"></p-radioButton>
              <span><b>{{ m.label }}</b><span class="sub sf-mu sf-sm" style="display:block">{{ m.value === 'CENTRAL' ? 'All salary expense goes to the payroll cost center.' : 'Expense goes to each person\\'s department cost center; liabilities stay on the payroll cost center.' }}</span></span></label>
          </div>
          <p class="sf-mu sf-sm">It can be changed later in HR Settings and applies only to future runs. Posted runs are never re-classified.</p>
        </section>

        <div class="sf-fbar">
          <span class="sf-end">
            <a class="p-button p-button-text" [routerLink]="modulesRoute">Cancel</a>
            <p-button label="Switch on HR &amp; Payroll" icon="pi pi-check" [loading]="busy" [disabled]="busy || !canSwitchOn" (onClick)="finish()" data-testid="finish"></p-button>
          </span>
        </div>
      </ng-container>
    </ng-container>
  </div>
</sf-page>`
})
export class HrmSetupComponent implements OnInit {
  private readonly api = inject(HrmApi);
  private readonly auth = inject(AuthService);
  private readonly admin = inject(ModuleAdminService);
  private readonly modules = inject(ModuleService);
  private readonly router = inject(Router);
  private readonly messages = inject(MessageService);

  readonly newFields = NEW_CC_FIELDS;
  readonly modes = ENUMS.costMode;
  readonly settingsRoute = `${HRM_ROUTE}/settings`;
  readonly modulesRoute = '/portal/pages/settings/modules';
  readonly hrmLabel = hrmLabel;

  status: SetupStatus | null = null;
  loading = true;
  error = '';
  busy = false;
  choice: 'existing' | 'new' = 'existing';
  selected: string | null = null;
  newCc: Record<string, any> = { code: 'PAYROLL', name: 'Payroll' };
  mode = 'CENTRAL';

  get canSet(): boolean { return this.auth.hasPermission('HRM_SETTINGS_MANAGE'); }
  get canCreate(): boolean { return this.auth.hasPermission('HRM_ORG_MANAGE'); }
  get canManageModules(): boolean { return this.auth.hasPermission('MODULES_MANAGE'); }
  get ccOptions(): { label: string; value: string }[] { return (this.status?.costCenters ?? []).filter(c => c.isActive !== false).map(c => ({ label: `${c.name} (${c.code})`, value: c.uuid })); }

  /** What must be true before the switch: someone can flip it, and a payroll cost center is or will be set. */
  get canSwitchOn(): boolean {
    if (!this.canManageModules) return false;
    if (!this.canSet) return !!this.status?.payrollCostCenter;
    return this.choice === 'existing' ? !!this.selected : (this.canCreate && !!this.status);
  }

  ngOnInit(): void { this.load(); }

  load(): void {
    this.loading = true;
    this.api.get<SetupStatus>('hrm/setup/status').subscribe({
      next: s => {
        this.loading = false;
        this.status = { ...s, costCenters: s?.costCenters ?? [] };
        this.selected = s?.payrollCostCenter?.uuid ?? null;
        this.mode = s?.mode || 'CENTRAL';
        // FD-9 — no cost centers yet: straight to "create new".
        this.choice = this.ccOptions.length ? 'existing' : 'new';
      },
      error: err => { this.loading = false; this.error = hrmError(err, 'The setup status could not be loaded.'); }
    });
  }

  finish(): void {
    this.error = '';
    if (!this.canSet) { this.enable(); return; }
    if (this.choice === 'new') {
      this.error = formProblem(NEW_CC_FIELDS, this.newCc) ?? '';
      if (this.error) return;
    }
    const body = this.choice === 'new'
      ? { newCostCenter: { code: String(this.newCc['code']).trim(), name: String(this.newCc['name']).trim() }, mode: this.mode }
      : { costCenterUuid: this.selected, mode: this.mode };
    this.busy = true;
    this.api.post('hrm/setup/payroll-cost-center', body).subscribe({
      next: () => this.enable(),
      error: err => { this.busy = false; this.error = hrmError(err, 'The payroll cost center could not be saved.'); }
    });
  }

  private enable(): void {
    this.busy = true;
    this.admin.enable(MODULE_HRM).subscribe({
      next: res => {
        this.busy = false;
        if (res?.success === false) { this.error = res.message || 'The module could not be switched on.'; return; }
        this.modules.refresh();
        this.messages.add({ severity: 'success', summary: 'Switched on', detail: 'HR & Payroll is on. Start with your branches and departments.', life: 4000 });
        this.router.navigate([`${HRM_ROUTE}/org/branches`]);
      },
      error: err => {
        this.busy = false;
        this.error = hrmErrorCode(err) === 'PAYROLL_COST_CENTER_REQUIRED'
          ? 'The server still has no payroll cost center for this organization. Choose one above and try again.'
          : hrmError(err, 'The module could not be switched on.');
      }
    });
  }
}
