import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router, RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';

import { FLOW } from '../../../shared/flow';
import { HrmApi } from '../core/hrm.api';
import { HrmFormComponent, cleanBody, formDefaults, formProblem, lookupsOf } from '../core/hrm-form.component';
import { HrmLookups } from '../core/hrm-lookups.service';
import { HRM_ROUTE } from '../core/hrm.nav';
import { hrmError } from '../core/hrm.shared';
import { HIRE_FIELDS, IDENTITY_FIELDS, PERSONAL_FIELDS, formToEmployee } from './employee.fields';

/** Task 080 (create half) — the personal data and the hire record in one go; bank and documents follow on the record once it exists. */
@Component({
  selector: 'app-hrm-employee-new',
  standalone: true,
  imports: [CommonModule, RouterModule, ButtonModule, HrmFormComponent, ...FLOW],
  providers: [HrmLookups],
  styleUrls: ['../../finance/gl/gl.scss', '../core/hrm.scss'],
  template: `
<sf-page [crumbs]="['HR', 'Employees', 'New employee']">
  <header class="sf-hdr sf-compact"><h1>New employee</h1></header>
  <div class="sf-body">
    <div class="gl-note er" *ngIf="error" role="alert" data-testid="error"><i class="pi pi-exclamation-triangle"></i><div>{{ error }}</div></div>
    <section class="sf-card"><div class="sf-ch"><h2>Personal</h2></div><hrm-form *ngIf="ready" [fields]="personal" [values]="values" [lookups]="lookups" idPrefix="p"></hrm-form></section>
    <section class="sf-card"><div class="sf-ch"><h2>Identity</h2></div><p class="sf-mu sf-sm">Stored encrypted. Afterwards it shows masked, and revealing it is logged.</p><hrm-form *ngIf="ready" [fields]="identity" [values]="values" idPrefix="i"></hrm-form></section>
    <section class="sf-card"><div class="sf-ch"><h2>Employment</h2></div><hrm-form *ngIf="ready" [fields]="hire" [values]="values" [lookups]="lookups" idPrefix="h"></hrm-form></section>
  </div>
  <div class="sf-fbar">The number is assigned when you save.
    <span class="sf-end">
      <a class="p-button p-button-text" [routerLink]="back">Cancel</a>
      <p-button label="Create employee" icon="pi pi-check" [loading]="busy" [disabled]="busy" (onClick)="save()" data-testid="save"></p-button>
    </span>
  </div>
</sf-page>`
})
export class EmployeeNewComponent implements OnInit {
  private readonly api = inject(HrmApi);
  private readonly router = inject(Router);
  readonly lookups = inject(HrmLookups);

  readonly personal = PERSONAL_FIELDS;
  readonly identity = IDENTITY_FIELDS;
  readonly hire = HIRE_FIELDS;
  readonly back = `${HRM_ROUTE}/employees`;
  values: Record<string, any> = formDefaults([...PERSONAL_FIELDS, ...IDENTITY_FIELDS, ...HIRE_FIELDS]);
  ready = false;
  busy = false;
  error = '';

  ngOnInit(): void { this.lookups.need(lookupsOf([...PERSONAL_FIELDS, ...HIRE_FIELDS])).subscribe(() => { this.ready = true; }); }

  save(): void {
    const all = [...PERSONAL_FIELDS, ...IDENTITY_FIELDS, ...HIRE_FIELDS];
    this.error = formProblem(all, this.values) ?? '';
    if (this.error) return;
    const v = cleanBody(all, this.values);
    const { status: _s, ...employment } = Object.fromEntries(HIRE_FIELDS.map(f => [f.key, v[f.key]]));
    const body = { ...formToEmployee(v), status: v['status'], employment: { ...employment, changeReason: 'Hire' } };
    this.busy = true;
    this.api.post<any>('hrm/employees', body).subscribe({
      next: e => { this.busy = false; this.router.navigate([this.back, e.uuid]); },
      error: err => { this.busy = false; this.error = hrmError(err, 'The employee could not be created.'); }
    });
  }
}
