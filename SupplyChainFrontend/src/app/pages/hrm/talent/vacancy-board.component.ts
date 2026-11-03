import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';

import { FLOW } from '../../../shared/flow';
import { AuthService } from '../../service/auth.service';
import { HrmApi, rowsOf } from '../core/hrm.api';
import { HrmDialogComponent } from '../core/hrm-dialog.component';
import { Field, HrmFormComponent, cleanBody, formDefaults, formProblem, lookupsOf } from '../core/hrm-form.component';
import { HrmLookups } from '../core/hrm-lookups.service';
import { HRM_ROUTE } from '../core/hrm.nav';
import { ENUMS, hrmError, hrmLabel, hrmTone, today } from '../core/hrm.shared';

export const STAGES = ['New', 'Screening', 'Interview', 'Offer', 'Hired', 'Rejected'];

const REJECT_FIELDS: Field[] = [{ key: 'rejectionReason', label: 'Why', type: 'textarea', required: true, maxLength: 300 }];
const APPLY_FIELDS: Field[] = [{ key: 'candidateUuid', label: 'Candidate', type: 'lookup', lookup: 'candidates', required: true }];
const CONVERT_FIELDS: Field[] = [
  { key: 'joinDate', label: 'Join date', type: 'date', required: true, def: today() }, { key: 'employmentType', label: 'Employment type', type: 'select', options: ENUMS.employmentType, def: 'Probation' },
  { key: 'designationUuid', label: 'Designation', type: 'lookup', lookup: 'designations', hint: 'Empty takes the vacancy\'s.' }, { key: 'departmentUuid', label: 'Department', type: 'lookup', lookup: 'departments' },
  { key: 'branchUuid', label: 'Branch', type: 'lookup', lookup: 'branches' }, { key: 'managerUuid', label: 'Manager', type: 'lookup', lookup: 'employees' }
];

/**
 * Task 209 — applications across the stages. Drag a card to another column, or pick the stage from the card's own list: the list
 * does everything dragging does, for a keyboard, a screen reader or a phone. Rejected asks why. Hired only goes through Convert,
 * which creates the employee (§8).
 */
@Component({
  selector: 'app-hrm-vacancy-board',
  standalone: true,
  imports: [CommonModule, RouterModule, ButtonModule, ToastModule, HrmDialogComponent, HrmFormComponent, ...FLOW],
  providers: [MessageService, HrmLookups],
  styleUrls: ['../../finance/gl/gl.scss', '../core/hrm.scss'],
  template: `
<p-toast></p-toast>
<sf-page [crumbs]="['HR', 'Recruitment', 'Vacancies', vacancy?.vacancyNumber ?? '']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button *ngIf="canManage" label="Add candidate" icon="pi pi-user-plus" (onClick)="openApply()" data-testid="apply"></p-button>
    <span class="sf-end"><a class="p-button p-button-text" [routerLink]="back"><i class="pi pi-arrow-left"></i>&nbsp;Back</a></span>
  </div>
  <div class="sf-body">
    <div class="sf-lr-head" *ngIf="vacancy"><div><h1>{{ vacancy.title }} <span class="sf-pill" [ngClass]="hrmTone(vacancy.status)">{{ hrmLabel(vacancy.status) }}</span></h1>
      <div class="sf-sub">{{ vacancy.designationName }} · {{ vacancy.departmentName }} · {{ vacancy.positions }} {{ vacancy.positions === 1 ? 'position' : 'positions' }}</div></div></div>
    <div class="gl-note er" *ngIf="error" role="alert" data-testid="error"><i class="pi pi-exclamation-triangle"></i><div>{{ error }}</div></div>
    <div class="hrm-board" data-testid="board">
      <div *ngFor="let col of columns" class="hrm-col" [class.drop]="over === col.stage" (dragover)="dragOver($event, col.stage)" (dragleave)="over = null" (drop)="drop($event, col.stage)" [attr.data-testid]="'col-' + col.stage" role="group" [attr.aria-label]="col.stage">
        <h3>{{ col.stage }} <span>{{ col.applications.length }}</span></h3>
        <div *ngFor="let a of col.applications" class="hrm-card" [draggable]="canManage" (dragstart)="dragStart($event, a)" (dragend)="dragEnd()" [attr.data-testid]="'card-' + a.uuid">
          <a [routerLink]="[candidateRoute, a.candidateUuid]">{{ a.candidateName }}</a>
          <div class="sf-mu sf-sm" *ngIf="a.rating !== null && a.rating !== undefined">Rating {{ a.rating }}</div>
          <div class="sf-mu sf-sm" *ngIf="a.rejectionReason">{{ a.rejectionReason }}</div>
          <ng-container *ngIf="canManage && col.stage !== 'Hired'">
            <select [attr.aria-label]="'Move ' + a.candidateName + ' to stage'" [value]="col.stage" (change)="pick(a, $any($event.target).value, $any($event.target), col.stage)" [attr.data-testid]="'stage-' + a.uuid">
              <option *ngFor="let s of movable(col.stage)" [value]="s">{{ s === col.stage ? 'Move to…' : s }}</option>
            </select>
            <p-button *ngIf="canConvert && col.stage === 'Offer'" label="Hire" icon="pi pi-check" [text]="true" size="small" (onClick)="openConvert(a)" [attr.data-testid]="'hire-' + a.uuid"></p-button>
          </ng-container>
          <a *ngIf="a.hiredEmployeeUuid" class="sf-sm" [routerLink]="[employeeRoute, a.hiredEmployeeUuid]">Open employee</a>
        </div>
      </div>
    </div>
  </div>
</sf-page>

<hrm-dialog [(visible)]="dlgOpen" [title]="dlgTitle" [subtitle]="dlgRow?.candidateName ?? null" [icon]="dlgKind === 'reject' ? 'pi-times' : dlgKind === 'convert' ? 'pi-user-plus' : 'pi-plus'" [tone]="dlgKind === 'reject' ? 'red' : 'blue'" [busy]="busy" [error]="dlgError" (save)="saveDlg()">
  <hrm-form *ngIf="dlgOpen" [fields]="dlgFields" [values]="dlgValues" [lookups]="lookups" idPrefix="brd"></hrm-form>
</hrm-dialog>`
})
export class VacancyBoardComponent implements OnInit {
  private readonly api = inject(HrmApi);
  private readonly auth = inject(AuthService);
  private readonly route = inject(ActivatedRoute);
  private readonly messages = inject(MessageService);
  readonly lookups = inject(HrmLookups);

  readonly back = `${HRM_ROUTE}/recruitment/vacancies`;
  readonly candidateRoute = `${HRM_ROUTE}/recruitment/candidates`;
  readonly employeeRoute = `${HRM_ROUTE}/employees`;
  readonly hrmLabel = hrmLabel;
  readonly hrmTone = hrmTone;

  uuid = '';
  vacancy: any = null;
  columns: { stage: string; applications: any[] }[] = STAGES.map(stage => ({ stage, applications: [] }));
  error = '';
  over: string | null = null;
  private dragged: any = null;

  dlgOpen = false;
  dlgKind: 'reject' | 'apply' | 'convert' = 'apply';
  dlgFields: Field[] = [];
  dlgValues: Record<string, any> = {};
  dlgRow: any = null;
  dlgError = '';
  busy = false;

  get canManage(): boolean { return this.auth.hasPermission('HRM_RECRUITMENT_MANAGE'); }
  get canConvert(): boolean { return this.canManage && this.auth.hasPermission('HRM_EMPLOYEE_EDIT'); }
  get dlgTitle(): string { return this.dlgKind === 'reject' ? 'Reject application' : this.dlgKind === 'convert' ? 'Hire: create the employee' : 'Add candidate to this vacancy'; }

  /** Hired is not offered: it only happens through Convert. */
  movable(current: string): string[] { return [current, ...STAGES.filter(s => s !== current && s !== 'Hired')]; }

  ngOnInit(): void {
    this.uuid = this.route.snapshot.paramMap.get('uuid') ?? '';
    this.api.get<any>(`hrm/vacancies/${this.uuid}`).subscribe({ next: v => { this.vacancy = v; }, error: err => { this.error = hrmError(err, 'The vacancy could not be loaded.'); } });
    this.load();
  }

  load(): void {
    this.api.get<any>(`hrm/vacancies/${this.uuid}/board`).subscribe({
      next: r => {
        const list = rowsOf<any>(r);
        this.columns = STAGES.map(stage => ({ stage, applications: list.find(c => c.stage === stage)?.applications ?? [] }));
      },
      error: err => { this.error = hrmError(err, 'The board could not be loaded.'); }
    });
  }

  dragStart(ev: DragEvent, a: any): void { this.dragged = a; ev.dataTransfer?.setData('text/plain', a.uuid); }
  dragEnd(): void { this.dragged = null; this.over = null; }
  dragOver(ev: DragEvent, stage: string): void { if (this.dragged && this.dragged.stage !== stage && stage !== 'Hired') { ev.preventDefault(); this.over = stage; } }

  drop(ev: DragEvent, stage: string): void {
    ev.preventDefault();
    const a = this.dragged;
    this.dragEnd();
    if (a && a.stage !== stage) this.moveTo(a, stage);
  }

  /** The same move as dragging. The list goes back to its column straight away; the reload shows where the card really is. */
  pick(a: any, stage: string, el: HTMLSelectElement, current: string): void {
    el.value = current;
    if (stage !== current) this.moveTo(a, stage);
  }

  moveTo(a: any, stage: string): void {
    if (stage === 'Hired') return;
    if (stage === 'Rejected') { this.open('reject', REJECT_FIELDS, a); return; }
    this.setStage(a, { stage });
  }

  private setStage(a: any, body: Record<string, any>, done?: () => void): void {
    this.api.patch(`hrm/applications/${a.uuid}/stage`, body).subscribe({
      next: () => { done?.(); this.load(); },
      error: err => {
        this.busy = false;
        const msg = hrmError(err, 'The application could not be moved.');
        if (this.dlgOpen) this.dlgError = msg; else this.messages.add({ severity: 'error', summary: 'Not moved', detail: msg, life: 8000 });
      }
    });
  }

  private open(kind: 'reject' | 'apply' | 'convert', fields: Field[], row: any = null): void {
    this.dlgKind = kind;
    this.dlgFields = fields;
    this.dlgRow = row;
    this.dlgValues = formDefaults(fields);
    this.dlgError = '';
    this.lookups.need(lookupsOf(fields)).subscribe(() => { this.dlgOpen = true; });
  }

  openApply(): void { this.open('apply', APPLY_FIELDS); }
  openConvert(a: any): void { this.open('convert', CONVERT_FIELDS, a); }

  saveDlg(): void {
    this.dlgError = formProblem(this.dlgFields, this.dlgValues) ?? '';
    if (this.dlgError) return;
    const v = cleanBody(this.dlgFields, this.dlgValues);
    const close = () => { this.busy = false; this.dlgOpen = false; };
    this.busy = true;
    if (this.dlgKind === 'reject') { this.setStage(this.dlgRow, { stage: 'Rejected', rejectionReason: v['rejectionReason'] }, close); return; }
    const call = this.dlgKind === 'apply'
      ? this.api.post('hrm/applications', { candidateUuid: v['candidateUuid'], vacancyUuid: this.uuid })
      : this.api.post(`hrm/applications/${this.dlgRow.uuid}/convert`, v);
    call.subscribe({
      next: () => {
        close();
        this.messages.add({ severity: 'success', summary: 'Done', detail: this.dlgKind === 'convert' ? `${this.dlgRow.candidateName} is now an employee.` : 'Added to this vacancy.', life: 4000 });
        this.load();
      },
      error: err => { this.busy = false; this.dlgError = hrmError(err, 'It was refused.'); }
    });
  }
}
