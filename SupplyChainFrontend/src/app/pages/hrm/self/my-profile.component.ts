import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';

import { FLOW } from '../../../shared/flow';
import { HrmApi } from '../core/hrm.api';
import { hrmError, hrmErrorCode, hrmLabel, hrmTone } from '../core/hrm.shared';

/** Self-service — your own HR record, read-only. Identity numbers stay masked here too; changing anything goes through HR. */
@Component({
  selector: 'app-hrm-my-profile',
  standalone: true,
  imports: [CommonModule, ...FLOW],
  styleUrls: ['../../finance/gl/gl.scss', '../core/hrm.scss'],
  template: `
<sf-page [crumbs]="['HR', 'Me', 'Profile']">
  <div class="sf-body" *ngIf="noEmployee || error">
    <div class="sf-card sf-empty" *ngIf="noEmployee" data-testid="no-employee">Your login is not linked to an employee record yet. Ask HR to link it.</div>
    <div class="gl-note er" *ngIf="error" role="alert" data-testid="error"><i class="pi pi-exclamation-triangle"></i><div>{{ error }}</div></div>
  </div>
  <ng-container *ngIf="e">
    <header class="sf-hdr"><div class="sf-t"><div><div class="sf-kicker">Employee · <span class="sf-mono">{{ e.employeeNumber }}</span></div>
      <h1>{{ e.fullName }} <span class="sf-pill" [ngClass]="hrmTone(e.status)">{{ hrmLabel(e.status) }}</span></h1>
      <div class="sf-meta" *ngIf="e.currentEmployment as c">{{ c.designationName }} · {{ c.departmentName }} · {{ c.branchName }}<ng-container *ngIf="c.managerName"> · reports to {{ c.managerName }}</ng-container></div></div></div></header>
    <div class="sf-body"><section class="sf-card"><div class="sf-kv" data-testid="profile">
      <span>Joined</span><span>{{ e.joinDate ? (e.joinDate | date:'dd MMM yyyy') : '—' }}</span><span>CNIC</span><span class="hrm-masked">{{ e.cnicMasked || '—' }}</span><span>NTN</span><span class="hrm-masked">{{ e.ntnMasked || '—' }}</span>
      <span>Work email</span><span>{{ e.workEmail || '—' }}</span><span>Personal email</span><span>{{ e.personalEmail || '—' }}</span><span>Mobile</span><span>{{ e.mobile || '—' }}</span><span>Current address</span><span>{{ e.currentAddress?.line1 || '—' }} {{ e.currentAddress?.city || '' }}</span>
    </div></section></div>
  </ng-container>
</sf-page>`
})
export class MyProfileComponent implements OnInit {
  private readonly api = inject(HrmApi);
  readonly hrmLabel = hrmLabel;
  readonly hrmTone = hrmTone;
  e: any = null;
  error = '';
  noEmployee = false;

  ngOnInit(): void {
    this.api.get<any>('hrm/employees/me').subscribe({
      next: e => { this.e = e; },
      error: err => { if (hrmErrorCode(err) === 'HRM_NO_EMPLOYEE') this.noEmployee = true; else this.error = hrmError(err, 'Your record could not be loaded.'); }
    });
  }
}
