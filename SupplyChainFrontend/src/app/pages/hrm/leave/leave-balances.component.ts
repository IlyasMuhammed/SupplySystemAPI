import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { SelectModule } from 'primeng/select';

import { FLOW } from '../../../shared/flow';
import { HrmApi, rowsOf } from '../core/hrm.api';
import { HrmDialogComponent } from '../core/hrm-dialog.component';
import { HrmLookups } from '../core/hrm-lookups.service';
import { Opt, fullYearOptions, hrmError, hrmLabel } from '../core/hrm.shared';

/** Task 157 — one employee's balances for a year, and the ledger behind a line (allocations, consumption, carry-forward). */
@Component({
  selector: 'app-hrm-leave-balances',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, SelectModule, HrmDialogComponent, ...FLOW],
  providers: [HrmLookups],
  styleUrls: ['../../finance/gl/gl.scss', '../core/hrm.scss'],
  template: `
<sf-page [crumbs]="['HR', 'Leave', 'Balances']">
  <div class="sf-body">
    <div class="sf-lr-head"><div><h1>Leave balances</h1><div class="sf-sub">Choose an employee in your scope.</div></div></div>
    <div class="sf-filters">
      <p-select [options]="employees" optionLabel="label" optionValue="value" [(ngModel)]="employeeUuid" (ngModelChange)="load()" placeholder="Employee" ariaLabel="Employee" [filter]="true" appendTo="body" data-testid="employee"></p-select>
      <p-select [options]="years" optionLabel="label" optionValue="value" [(ngModel)]="year" (ngModelChange)="load()" ariaLabel="Year" appendTo="body" data-testid="year"></p-select>
    </div>
    <div class="gl-note er" *ngIf="error" role="alert" data-testid="error"><i class="pi pi-exclamation-triangle"></i><div>{{ error }}</div></div>
    <div class="sf-card p0"><div class="gl-scroll"><table class="hrm-table hrm-stack" data-testid="balances">
      <thead><tr><th>Type</th><th class="gl-num">Allocated</th><th class="gl-num">Carried</th><th class="gl-num">Adjusted</th><th class="gl-num">Used</th><th class="gl-num">Pending</th><th class="gl-num">Balance</th><th></th></tr></thead>
      <tbody>
        <tr *ngFor="let b of rows">
          <td data-label="Type">{{ b.leaveTypeName }}</td><td data-label="Allocated" class="gl-num">{{ b.allocated }}</td><td data-label="Carried" class="gl-num">{{ b.carried }}</td><td data-label="Adjusted" class="gl-num">{{ b.adjusted }}</td><td data-label="Used" class="gl-num">{{ b.used }}</td><td data-label="Pending" class="gl-num">{{ b.pending }}</td><td data-label="Balance" class="gl-num"><b>{{ b.balance }}</b></td>
          <td class="hrm-acts"><div class="hrm-actions"><p-button label="Ledger" icon="pi pi-list" [text]="true" size="small" (onClick)="openLedger(b)" [attr.data-testid]="'ledger-' + b.leaveTypeUuid"></p-button></div></td>
        </tr>
        <tr *ngIf="!rows.length"><td class="hrm-empty sf-empty" colspan="8">{{ employeeUuid ? 'No balances for this year.' : 'Choose an employee.' }}</td></tr>
      </tbody>
    </table></div></div>
  </div>
</sf-page>

<hrm-dialog [(visible)]="ledgerOpen" [title]="ledgerTitle + ' ledger'" icon="pi-list" [saveLabel]="null" width="44rem" [error]="ledgerError">
  <div class="gl-scroll"><table class="hrm-table hrm-stack">
    <thead><tr><th>Date</th><th>Reason</th><th class="gl-num">Days</th><th>Note</th></tr></thead>
    <tbody>
      <tr *ngFor="let l of ledger"><td data-label="Date">{{ l.date | date:'dd MMM yyyy' }}</td><td data-label="Reason">{{ hrmLabel(l.reason) }}</td><td data-label="Days" class="gl-num">{{ l.delta }}</td><td data-label="Note">{{ l.note || '—' }}</td></tr>
      <tr *ngIf="!ledger.length"><td class="hrm-empty sf-mu" colspan="4">No entries.</td></tr>
    </tbody>
  </table></div>
</hrm-dialog>`
})
export class LeaveBalancesComponent implements OnInit {
  private readonly api = inject(HrmApi);
  private readonly lookups = inject(HrmLookups);

  readonly years = fullYearOptions();
  readonly hrmLabel = hrmLabel;
  year = new Date().getFullYear();
  employees: Opt[] = [];
  employeeUuid: string | null = null;
  rows: any[] = [];
  error = '';
  ledgerOpen = false;
  ledgerTitle = '';
  ledger: any[] = [];
  ledgerError = '';

  ngOnInit(): void { this.lookups.load('employees').subscribe(l => { this.employees = l; }); }

  load(): void {
    if (!this.employeeUuid) { this.rows = []; return; }
    this.error = '';
    this.api.get<any>('hrm/leave/balances', { employeeUuid: this.employeeUuid, year: this.year }).subscribe({
      next: r => { this.rows = rowsOf(r); },
      error: err => { this.rows = []; this.error = hrmError(err, 'The balances could not be loaded.'); }
    });
  }

  openLedger(b: any): void {
    this.ledgerTitle = b.leaveTypeName;
    this.ledger = [];
    this.ledgerError = '';
    this.ledgerOpen = true;
    this.api.get<any>('hrm/leave/ledger', { employeeUuid: this.employeeUuid, leaveTypeUuid: b.leaveTypeUuid, year: this.year }).subscribe({
      next: r => { this.ledger = rowsOf(r); },
      error: err => { this.ledgerError = hrmError(err, 'The ledger could not be loaded.'); }
    });
  }
}
