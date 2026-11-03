import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';

import { FLOW } from '../../../shared/flow';
import { HrmApi } from '../core/hrm.api';
import { Field, HrmFormComponent, cleanBody, formDefaults, formProblem, lookupsOf } from '../core/hrm-form.component';
import { HrmLookups } from '../core/hrm-lookups.service';
import { hrmError, today } from '../core/hrm.shared';

export const FORMULA_VARIABLES = ['BASIC', 'GROSS', 'TOTAL_EARNINGS', 'TAXABLE_INCOME', 'WORKING_DAYS', 'PAID_DAYS', 'OT_HOURS'];

/** The upper-case names a formula uses, without the functions: these are what sample values are needed for. */
export function formulaVariables(formula: string): string[] {
  const fns = new Set(['MIN', 'MAX', 'IF', 'ROUND', 'SLAB']);
  return [...new Set((formula.toUpperCase().match(/[A-Z_][A-Z0-9_]*/g) ?? []).filter(n => !fns.has(n)))];
}

/** Task 181 — type a formula, give the sample values, see the server's result. The server parses it: nothing here evaluates anything. */
@Component({
  selector: 'app-hrm-formula-tester',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, InputTextModule, TextareaModule, ...FLOW],
  styleUrls: ['../../finance/gl/gl.scss', '../core/hrm.scss'],
  styles: [`input, textarea { width: 100%; }`],
  template: `
<sf-page [crumbs]="['HR', 'Payroll', 'Formula tester']">
  <div class="sf-body">
    <div class="sf-lr-head"><div><h1>Formula tester</h1><div class="sf-sub">Numbers, + - * / ( ), MIN MAX IF ROUND SLAB, {{ variables.join(' ') }} and any component code earlier in the structure.</div></div></div>
    <section class="sf-card">
      <div class="sf-fld hrm-full"><label for="ff" class="sf-req">Formula</label><textarea pTextarea id="ff" rows="3" [(ngModel)]="formula" (ngModelChange)="sync()" maxlength="500" data-testid="formula" placeholder="ROUND(BASIC * 0.1, 2)"></textarea></div>
      <div class="sf-fields" style="margin-top:0.75rem" *ngIf="names.length" data-testid="samples">
        <div class="sf-fld" *ngFor="let n of names"><label [attr.for]="'var-' + n">{{ n }}</label><input pInputText type="number" [id]="'var-' + n" [(ngModel)]="values[n]" [attr.data-testid]="'var-' + n" /></div>
      </div>
      <div style="margin-top:0.75rem"><p-button label="Test" icon="pi pi-play" [loading]="busy" [disabled]="busy || !formula.trim()" (onClick)="test()" data-testid="test"></p-button></div>
    </section>
    <div class="gl-note er" *ngIf="error" role="alert" data-testid="error"><i class="pi pi-exclamation-triangle"></i><div>{{ error }}</div></div>
    <div class="gl-note ok" *ngIf="result !== null" data-testid="result"><i class="pi pi-check"></i><div>Result: <b>{{ result }}</b></div></div>
  </div>
</sf-page>`
})
export class FormulaTesterComponent {
  private readonly api = inject(HrmApi);

  readonly variables = FORMULA_VARIABLES;
  formula = '';
  names: string[] = [];
  values: Record<string, number | null> = {};
  busy = false;
  error = '';
  result: number | null = null;

  sync(): void { this.names = formulaVariables(this.formula); }

  test(): void {
    const variables: Record<string, number> = {};
    for (const n of this.names) variables[n] = Number(this.values[n] ?? 0);
    this.busy = true;
    this.error = '';
    this.result = null;
    this.api.post<any>('hrm/salary-formula/test', { formula: this.formula.trim(), variables }).subscribe({
      next: r => { this.busy = false; if (r?.valid) this.result = r.result; else this.error = r?.error || 'The formula is not valid.'; },
      error: err => { this.busy = false; this.error = hrmError(err, 'The formula could not be tested.'); }
    });
  }
}

const BULK_FIELDS: Field[] = [
  { key: 'componentCode', label: 'Component', type: 'lookup', lookup: 'componentCodes', required: true, def: 'BASIC' },
  { key: 'percent', label: 'Raise by percent', type: 'number', min: 0, hint: 'A percent or an amount, not both.' },
  { key: 'amount', label: 'Raise by amount', type: 'number', min: 0, check: (a, v) => {
    const hasA = a !== null && a !== '', hasP = v['percent'] !== null && v['percent'] !== '';
    return hasA === hasP ? 'Give a percent or an amount, not both.' : null;
  } },
  { key: 'effectiveFrom', label: 'Effective from', type: 'date', required: true, def: today() },
  { key: 'reason', label: 'Reason', type: 'textarea', required: true, maxLength: 200 },
  { key: 'employees', label: 'Employees', type: 'rows', required: true, rows: [{ key: 'employeeUuid', label: 'Employee', type: 'lookup', lookup: 'employees', required: true }] }
];

/** Task 192 — drafts for many people at once. Each still goes through submit and approve on the employee's Salary tab. */
@Component({
  selector: 'app-hrm-bulk-revision',
  standalone: true,
  imports: [CommonModule, ButtonModule, HrmFormComponent, ...FLOW],
  providers: [HrmLookups],
  styleUrls: ['../../finance/gl/gl.scss', '../core/hrm.scss'],
  template: `
<sf-page [crumbs]="['HR', 'Payroll', 'Bulk salary revision']">
  <div class="sf-body">
    <div class="sf-lr-head"><div><h1>Bulk salary revision</h1><div class="sf-sub">Creates draft assignments. Nothing changes until each is submitted and approved.</div></div></div>
    <div class="gl-note er" *ngIf="error" role="alert" data-testid="error"><i class="pi pi-exclamation-triangle"></i><div>{{ error }}</div></div>
    <div class="gl-note ok" *ngIf="done !== null" data-testid="done"><i class="pi pi-check"></i><div>{{ done }} draft {{ done === 1 ? 'assignment' : 'assignments' }} created.</div></div>
    <section class="sf-card"><hrm-form *ngIf="ready" [fields]="fields" [values]="values" [lookups]="lookups" idPrefix="bulk"></hrm-form></section>
  </div>
  <div class="sf-fbar"><span class="sf-end"><p-button label="Create drafts" icon="pi pi-check" [loading]="busy" [disabled]="busy" (onClick)="save()" data-testid="save"></p-button></span></div>
</sf-page>`
})
export class BulkRevisionComponent implements OnInit {
  private readonly api = inject(HrmApi);
  readonly lookups = inject(HrmLookups);

  readonly fields = BULK_FIELDS;
  values: Record<string, any> = formDefaults(BULK_FIELDS);
  ready = false;
  busy = false;
  error = '';
  done: number | null = null;

  ngOnInit(): void { this.lookups.need(lookupsOf(BULK_FIELDS)).subscribe(() => { this.ready = true; }); }

  save(): void {
    this.done = null;
    this.error = formProblem(BULK_FIELDS, this.values) ?? '';
    if (this.error) return;
    const v = cleanBody(BULK_FIELDS, this.values);
    const body = { componentCode: v['componentCode'], percent: v['percent'], amount: v['amount'], effectiveFrom: v['effectiveFrom'], reason: v['reason'], employeeUuids: (v['employees'] as any[]).map(e => e.employeeUuid) };
    this.busy = true;
    this.api.post<any>('hrm/salary-assignments/bulk-revision', body).subscribe({
      next: r => { this.busy = false; this.done = Array.isArray(r) ? r.length : body.employeeUuids.length; this.values = formDefaults(BULK_FIELDS); },
      error: err => { this.busy = false; this.error = hrmError(err, 'The revision could not be created.'); }
    });
  }

}
