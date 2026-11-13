import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';
import { catchError, of } from 'rxjs';

import { FLOW } from '../../../shared/flow';
import { AuthService } from '../../service/auth.service';
import { HrmApi, rowsOf } from '../core/hrm.api';
import { HrmDialogComponent } from '../core/hrm-dialog.component';
import { Field, HrmFormComponent, cleanBody, formDefaults, formProblem } from '../core/hrm-form.component';
import { HRM_ROUTE } from '../core/hrm.nav';
import { hrmError, hrmErrorCode, hrmLabel, hrmTone } from '../core/hrm.shared';

/** §8 — overall = Σ(weight × managerRating) / Σ weight, shown before submit; the server's own figure replaces it on save. */
export function weightedRating(goals: { weight?: number | null; managerRating?: number | null; status?: string }[]): number | null {
  const rated = goals.filter(g => g.status !== 'Dropped');
  const totalWeight = rated.reduce((s, g) => s + Number(g.weight ?? 0), 0);
  if (!totalWeight || rated.some(g => g.managerRating === null || g.managerRating === undefined)) return null;
  return Math.round((rated.reduce((s, g) => s + Number(g.weight ?? 0) * Number(g.managerRating), 0) / totalWeight) * 100) / 100;
}

/**
 * Task 222 — one review: goals with self and manager ratings, comments, submit, acknowledge. The sides follow the review, not the route
 * (contract Changes, FE-3): employee = it is about me; manager side = not about me, and I am its reviewer or hold HRM_PERFORMANCE_MANAGE,
 * with a code the write routes accept. The server has the last word on HR scope (403 or 404 shows its sentence).
 */
@Component({
  selector: 'app-hrm-review-form',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule, ButtonModule, InputTextModule, TextareaModule, ToastModule, HrmDialogComponent, HrmFormComponent, ...FLOW],
  providers: [MessageService],
  styleUrls: ['../../finance/gl/gl.scss', '../core/hrm.scss'],
  styles: [`textarea { width: 100%; } input { width: 5.5rem; } .wide { width: 100%; }`],
  template: `
<p-toast></p-toast>
<sf-page [crumbs]="['HR', 'Performance', 'Review', r?.employeeName ?? '']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <ng-container *ngIf="r">
      <p-button *ngIf="canSave" label="Save" icon="pi pi-check" [loading]="busy" [disabled]="busy" (onClick)="save()" data-testid="save"></p-button>
      <p-button *ngIf="manager && r.status === 'Draft'" label="Submit" icon="pi pi-send" [text]="true" [disabled]="busy" (onClick)="submit()" data-testid="submit"></p-button>
      <p-button *ngIf="employee && r.status === 'Submitted'" label="Acknowledge" icon="pi pi-check-circle" [disabled]="busy" (onClick)="acknowledge()" data-testid="acknowledge"></p-button>
    </ng-container>
    <span class="sf-end"><a class="p-button p-button-text" [routerLink]="back"><i class="pi pi-arrow-left"></i>&nbsp;Back</a></span>
  </div>
  <div class="sf-body">
    <div class="gl-note er" *ngIf="error" role="alert" data-testid="error"><i class="pi pi-exclamation-triangle"></i><div>{{ error }}</div></div>
    <ng-container *ngIf="r">
      <div class="sf-lr-head"><div><h1>{{ r.employeeName }} <span class="sf-pill" [ngClass]="hrmTone(r.status)" data-testid="status">{{ hrmLabel(r.status) }}</span></h1></div>
        <div class="sf-tile"><span class="l">Overall rating</span><span class="v" data-testid="rating">{{ overall ?? '—' }}</span></div></div>
      <section class="sf-card p0"><div class="sf-ch sf-pad"><h2>Goals</h2><span class="sf-ch-actions" *ngIf="manager && draft"><p-button label="Add goal" icon="pi pi-plus" [text]="true" size="small" (onClick)="openGoal()" data-testid="add-goal"></p-button></span></div>
        <div class="gl-scroll"><table class="hrm-table hrm-stack" data-testid="goals">
          <thead><tr><th>Goal</th><th class="gl-num">Weight</th><th>Self rating</th><th>Manager rating</th><th>Status</th></tr></thead>
          <tbody>
            <tr *ngFor="let g of goals" [attr.data-testid]="'goal-' + g.uuid">
              <td data-label="Goal"><span>{{ g.title }}<span class="sub">{{ g.description }}</span></span></td><td data-label="Weight" class="gl-num">{{ g.weight }}</td>
              <td data-label="Self rating"><input pInputText type="number" min="0" [attr.aria-label]="'Self rating for ' + g.title" [(ngModel)]="g.selfRating" [disabled]="!(employee && draft)" [attr.data-testid]="'self-' + g.uuid" /></td>
              <td data-label="Manager rating"><input pInputText type="number" min="0" [attr.aria-label]="'Manager rating for ' + g.title" [(ngModel)]="g.managerRating" [disabled]="!(manager && draft)" [attr.data-testid]="'mgr-' + g.uuid" /></td>
              <td data-label="Status"><span class="sf-pill" [ngClass]="hrmTone(g.status)">{{ hrmLabel(g.status) }}</span></td>
            </tr>
            <tr *ngIf="!goals.length"><td class="hrm-empty sf-empty" colspan="5">No goals in this cycle.</td></tr>
          </tbody>
        </table></div></section>
      <section class="sf-card"><div class="sf-fields">
        <div class="sf-fld hrm-full"><label for="cm-mgr">Manager comments</label><textarea pTextarea id="cm-mgr" rows="3" [(ngModel)]="r.managerComments" [disabled]="!(manager && draft)" data-testid="mgr-comments"></textarea></div>
        <div class="sf-fld hrm-full"><label for="cm-emp">Employee comments</label><textarea pTextarea id="cm-emp" rows="3" [(ngModel)]="r.employeeComments" [disabled]="!canSave || manager" data-testid="emp-comments"></textarea></div>
      </div></section>
    </ng-container>
  </div>
</sf-page>
<hrm-dialog [(visible)]="goalOpen" title="Add goal" [subtitle]="r?.employeeName ?? null" icon="pi-flag" [busy]="busy" [error]="goalError" (save)="saveGoal()">
  <hrm-form *ngIf="goalOpen" [fields]="goalFields" [values]="goalValues" idPrefix="goal"></hrm-form>
</hrm-dialog>`
})
export class ReviewFormComponent implements OnInit {
  private readonly api = inject(HrmApi);
  private readonly auth = inject(AuthService);
  private readonly route = inject(ActivatedRoute);
  private readonly messages = inject(MessageService);

  readonly hrmLabel = hrmLabel;
  readonly hrmTone = hrmTone;

  uuid = '';
  r: any = null;
  goals: any[] = [];
  busy = false;
  error = '';
  /** My linked employee; null when I have none or may not read it. */
  me: string | null = null;
  private backPath = 'performance/reviews';

  private get holdsManage(): boolean { return this.auth.hasPermission('HRM_PERFORMANCE_MANAGE'); }
  private get canWrite(): boolean { return this.holdsManage || this.auth.hasPermission('HRM_SELF_SERVICE'); }
  get employee(): boolean { return !!this.r && !!this.me && this.r.employeeUuid === this.me && this.canWrite; }
  get manager(): boolean {
    if (!this.r || !this.canWrite || this.r.employeeUuid === this.me) return false;
    return this.holdsManage || (!!this.me && this.r.reviewerUuid === this.me);
  }
  get draft(): boolean { return this.r?.status === 'Draft'; }
  /** Manager side while Draft; the employee while Draft or Submitted. */
  get canSave(): boolean { return (this.manager && this.draft) || (this.employee && (this.draft || this.r?.status === 'Submitted')); }
  get overall(): number | null { return this.r?.overallRating ?? weightedRating(this.goals); }
  get back(): string { return `${HRM_ROUTE}/${this.backPath}`; }

  ngOnInit(): void {
    this.uuid = this.route.snapshot.paramMap.get('uuid') ?? '';
    this.backPath = this.route.snapshot.data['back'] ?? this.backPath;
    const me = this.auth.hasPermission('HRM_SELF_SERVICE') ? this.api.get<any>('hrm/employees/me').pipe(catchError(() => of(null))) : of(null);
    me.subscribe(e => { this.me = e?.uuid ?? null; this.load(); });
  }

  load(): void {
    this.api.get<any>(`hrm/reviews/${this.uuid}`).subscribe({
      next: r => {
        this.r = r;
        if (r?.goals?.length) { this.goals = r.goals; return; }
        this.api.get<any>('hrm/goals', { cycleUuid: r.cycleUuid, employeeUuid: r.employeeUuid }).subscribe({ next: g => { this.goals = rowsOf(g); }, error: () => { this.goals = []; } });
      },
      error: err => { this.error = hrmError(err, 'The review could not be loaded.'); }
    });
  }

  private saveAll(then?: () => void): void {
    this.busy = true;
    this.error = '';
    const saveGoals = this.draft ? this.goals.map(g => this.api.put(`hrm/goals/${g.uuid}`, g)) : [];
    let left = saveGoals.length + 1;
    const fail = (err: unknown) => { this.busy = false; this.error = hrmError(err, 'The review could not be saved.'); };
    const one = () => { left--; if (!left) { this.busy = false; then ? then() : this.saved(); } };
    this.api.put(`hrm/reviews/${this.uuid}`, { ...(this.holdsManage ? { reviewerUuid: this.r.reviewerUuid ?? null } : {}), managerComments: this.r.managerComments ?? null, employeeComments: this.r.employeeComments ?? null }).subscribe({ next: one, error: fail });
    saveGoals.forEach(c => c.subscribe({ next: one, error: fail }));
  }

  readonly goalFields: Field[] = [{ key: 'title', label: 'Goal', required: true, maxLength: 200, full: true }, { key: 'weight', label: 'Weight', type: 'number', required: true, min: 0, def: 1 }, { key: 'description', label: 'Description', type: 'textarea', maxLength: 1000 }];
  goalOpen = false;
  goalValues: Record<string, any> = {};
  goalError = '';

  openGoal(): void { this.goalValues = formDefaults(this.goalFields); this.goalError = ''; this.goalOpen = true; }

  saveGoal(): void {
    this.goalError = formProblem(this.goalFields, this.goalValues) ?? '';
    if (this.goalError) return;
    this.busy = true;
    this.api.post('hrm/goals', { ...cleanBody(this.goalFields, this.goalValues), employeeUuid: this.r.employeeUuid, cycleUuid: this.r.cycleUuid }).subscribe({
      next: () => { this.busy = false; this.goalOpen = false; this.load(); },
      error: err => { this.busy = false; this.goalError = hrmError(err, 'The goal could not be added.'); }
    });
  }

  private saved(): void { this.messages.add({ severity: 'success', summary: 'Saved', detail: 'The review is saved.', life: 3000 }); this.load(); }

  save(): void { this.saveAll(); }

  submit(): void {
    this.saveAll(() => this.api.post(`hrm/reviews/${this.uuid}/submit`).subscribe({
      next: () => { this.messages.add({ severity: 'success', summary: 'Submitted', detail: 'The review is submitted.', life: 3000 }); this.load(); },
      error: err => { this.error = hrmErrorCode(err) === 'REVIEW_GOALS_UNRATED' ? 'Every goal needs a manager rating before the review can be submitted.' : hrmError(err, 'The review could not be submitted.'); }
    }));
  }

  /**
   * REV49B-7: `acknowledge` takes no body, and an acknowledged review is locked. So the employee's comments go first through
   * `PUT reviews/{uuid}` (open to the employee while Submitted), then the acknowledge. A refused save stops before acknowledging.
   */
  acknowledge(): void {
    this.busy = true;
    this.error = '';
    const fail = (fallback: string) => (err: unknown) => { this.busy = false; this.error = hrmError(err, fallback); };
    this.api.put(`hrm/reviews/${this.uuid}`, { employeeComments: this.r.employeeComments ?? null }).subscribe({
      next: () => this.api.post(`hrm/reviews/${this.uuid}/acknowledge`).subscribe({
        next: () => { this.busy = false; this.messages.add({ severity: 'success', summary: 'Acknowledged', detail: 'The review is acknowledged.', life: 3000 }); this.load(); },
        error: fail('The review could not be acknowledged.')
      }),
      error: fail('Your comments could not be saved, so the review was not acknowledged.')
    });
  }
}
