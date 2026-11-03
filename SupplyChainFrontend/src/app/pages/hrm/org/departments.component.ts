import { Component, Input, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ButtonModule } from 'primeng/button';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';

import { FLOW } from '../../../shared/flow';
import { AuthService } from '../../service/auth.service';
import { HrmApi } from '../core/hrm.api';
import { HrmDialogComponent } from '../core/hrm-dialog.component';
import { Field, HrmFormComponent, cleanBody, formDefaults, formProblem, lookupsOf } from '../core/hrm-form.component';
import { HrmLookups } from '../core/hrm-lookups.service';
import { Opt, hrmError } from '../core/hrm.shared';

export interface DeptNode { uuid: string; code: string; name: string; parentUuid?: string | null; headEmployeeUuid?: string | null; headEmployeeName?: string | null; branchUuid?: string | null; branchName?: string | null; costCenterUuid?: string | null; costCenterCode?: string | null; isActive: boolean; employeeCount: number; children: DeptNode[]; }

/** All uuids under a node, itself included: a department cannot move under one of these (the server also answers DEPARTMENT_CYCLE). */
export function subtreeOf(n: DeptNode): Set<string> {
  const out = new Set<string>([n.uuid]);
  for (const c of n.children ?? []) subtreeOf(c).forEach(u => out.add(u));
  return out;
}

const FIELDS: Field[] = [
  { key: 'code', label: 'Code', required: true, maxLength: 20 }, { key: 'name', label: 'Name', required: true, maxLength: 150 },
  { key: 'parentUuid', label: 'Parent', type: 'lookup', lookup: 'departments' }, { key: 'headEmployeeUuid', label: 'Head', type: 'lookup', lookup: 'employees' },
  { key: 'branchUuid', label: 'Branch', type: 'lookup', lookup: 'branches' }, { key: 'costCenterUuid', label: 'Cost center', type: 'lookup', lookup: 'costCenters' }
];

@Component({
  selector: 'hrm-dept-branch',
  standalone: true,
  imports: [CommonModule, ButtonModule],
  styleUrls: ['../core/hrm.scss'],
  template: `
<ul class="hrm-tree" [attr.role]="root ? 'tree' : 'group'">
  <li *ngFor="let n of nodes" role="treeitem" [attr.aria-label]="n.name" [attr.data-testid]="'node-' + n.uuid">
    <div class="hrm-node" [class.off]="!n.isActive" [class.drop]="host.dropTarget === n.uuid" [draggable]="host.canManage"
         (dragstart)="host.dragStart($event, n)" (dragover)="host.dragOver($event, n)" (dragleave)="host.dropTarget = null" (drop)="host.drop($event, n)" (dragend)="host.dragEnd()">
      <i *ngIf="host.canManage" class="pi pi-bars grip" aria-hidden="true"></i>
      <span class="name">{{ n.name }}</span><span class="sf-mono sf-mu sf-sm">{{ n.code }}</span>
      <span class="sf-pill" *ngIf="!n.isActive">Inactive</span>
      <span class="sf-mu sf-sm">{{ n.employeeCount }} people<ng-container *ngIf="n.headEmployeeName"> · head {{ n.headEmployeeName }}</ng-container><ng-container *ngIf="n.costCenterCode"> · {{ n.costCenterCode }}</ng-container></span>
      <span class="end" *ngIf="host.canManage">
        <p-button label="Move" icon="pi pi-arrows-alt" [text]="true" size="small" (onClick)="host.openMove(n)" [attr.data-testid]="'move-' + n.uuid"></p-button>
        <p-button label="Edit" icon="pi pi-pencil" [text]="true" size="small" (onClick)="host.openEdit(n)" [attr.data-testid]="'edit-' + n.uuid"></p-button>
        <p-button [label]="n.isActive ? 'Deactivate' : 'Activate'" [icon]="n.isActive ? 'pi pi-ban' : 'pi pi-check'" [text]="true" size="small" [severity]="n.isActive ? 'danger' : undefined" (onClick)="host.toggleActive(n)" [attr.data-testid]="'active-' + n.uuid"></p-button>
      </span>
    </div>
    <hrm-dept-branch *ngIf="n.children?.length" [nodes]="n.children" [host]="host"></hrm-dept-branch>
  </li>
</ul>`
})
export class DeptBranchComponent {
  @Input({ required: true }) nodes: DeptNode[] = [];
  @Input({ required: true }) host!: DepartmentsComponent;
  @Input() root = false;
}

/**
 * Task 077 — the department hierarchy. Dragging a row onto another makes it a child; dropping on the top bar makes it top level.
 * The Move button does the same from a picker, so a keyboard, a screen reader or a phone can do everything dragging can.
 */
@Component({
  selector: 'app-hrm-departments',
  standalone: true,
  imports: [CommonModule, ButtonModule, ToastModule, DeptBranchComponent, HrmDialogComponent, HrmFormComponent, ...FLOW],
  providers: [MessageService, HrmLookups],
  styleUrls: ['../../finance/gl/gl.scss', '../core/hrm.scss'],
  template: `
<p-toast></p-toast>
<sf-page [crumbs]="['HR', 'Organization', 'Departments']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button *ngIf="canManage" label="New department" icon="pi pi-plus" (onClick)="openNew()" data-testid="new"></p-button>
    <span class="sf-end">
      <button type="button" class="sf-chip" [class.on]="includeInactive" (click)="includeInactive = !includeInactive; load()" data-testid="inactive">Show inactive</button>
      <p-button label="Refresh" icon="pi pi-refresh" [text]="true" (onClick)="load()"></p-button>
    </span>
  </div>
  <div class="sf-body">
    <div class="sf-lr-head"><div><h1>Departments <span class="sf-count">{{ count }}</span></h1>
      <div class="sf-sub" *ngIf="canManage">Drag a department onto another to move it under it, or use Move.</div></div></div>
    <div class="gl-note er" *ngIf="error" role="alert" data-testid="load-error"><i class="pi pi-exclamation-triangle"></i><div>{{ error }}</div></div>
    <div class="sf-card">
      <div class="sf-empty" *ngIf="loading"><i class="pi pi-spin pi-spinner"></i> Loading…</div>
      <div *ngIf="dragging" class="hrm-node" [class.drop]="dropTarget === 'root'" (dragover)="dragOverRoot($event)" (dragleave)="dropTarget = null" (drop)="dropRoot($event)" data-testid="drop-root"><i class="pi pi-arrow-up"></i> Drop here to make it top level</div>
      <hrm-dept-branch *ngIf="!loading && tree.length" [nodes]="tree" [host]="this" [root]="true"></hrm-dept-branch>
      <div class="sf-empty" *ngIf="!loading && !tree.length && !error" data-testid="empty">No departments yet.</div>
    </div>
  </div>
</sf-page>

<hrm-dialog [(visible)]="formOpen" [title]="editing ? 'Edit department' : 'New department'" [subtitle]="editing?.code ?? null" [icon]="editing ? 'pi-pencil' : 'pi-plus'" [busy]="busy" [error]="dialogError" (save)="saveForm()">
  <hrm-form *ngIf="formOpen" [fields]="fields" [values]="values" [editing]="!!editing" [lookups]="lookups" idPrefix="dept"></hrm-form>
</hrm-dialog>

<hrm-dialog [(visible)]="moveOpen" title="Move department" [subtitle]="moving?.name ?? null" icon="pi-arrows-alt" saveLabel="Move" [busy]="busy" [error]="dialogError" (save)="saveMove()">
  <hrm-form *ngIf="moveOpen" [fields]="moveFields" [values]="moveValues" idPrefix="mv"></hrm-form>
</hrm-dialog>`
})
export class DepartmentsComponent implements OnInit {
  private readonly api = inject(HrmApi);
  private readonly auth = inject(AuthService);
  private readonly messages = inject(MessageService);
  readonly lookups = inject(HrmLookups);

  readonly fields = FIELDS;
  tree: DeptNode[] = [];
  count = 0;
  loading = false;
  error = '';
  includeInactive = false;
  get canManage(): boolean { return this.auth.hasPermission('HRM_ORG_MANAGE'); }

  // Drag
  dragging: DeptNode | null = null;
  dropTarget: string | null = null;

  // Form
  formOpen = false;
  editing: DeptNode | null = null;
  values: Record<string, any> = {};
  busy = false;
  dialogError = '';

  // Move
  moveOpen = false;
  moving: DeptNode | null = null;
  moveFields: Field[] = [];
  moveValues: Record<string, any> = {};

  ngOnInit(): void { this.load(); }

  load(): void {
    this.loading = !this.tree.length;
    this.error = '';
    this.api.get<DeptNode[]>('hrm/departments/tree', { includeInactive: this.includeInactive || undefined }).subscribe({
      next: t => { this.loading = false; this.tree = t ?? []; this.count = this.flat(this.tree).length; },
      error: err => { this.loading = false; this.tree = []; this.error = hrmError(err, 'The departments could not be loaded.'); }
    });
  }

  private flat(list: DeptNode[], depth = 0, out: { n: DeptNode; depth: number }[] = []): { n: DeptNode; depth: number }[] {
    for (const n of list) { out.push({ n, depth }); this.flat(n.children ?? [], depth + 1, out); }
    return out;
  }

  // ── Drag and drop ──────────────────────────────────────────────────────

  dragStart(ev: DragEvent, n: DeptNode): void {
    ev.stopPropagation();
    this.dragging = n;
    ev.dataTransfer?.setData('text/plain', n.uuid);
    if (ev.dataTransfer) ev.dataTransfer.effectAllowed = 'move';
  }
  dragEnd(): void { this.dragging = null; this.dropTarget = null; }

  allowed(target: DeptNode): boolean { return !!this.dragging && !subtreeOf(this.dragging).has(target.uuid); }

  dragOver(ev: DragEvent, n: DeptNode): void {
    if (!this.allowed(n)) return;
    ev.preventDefault();
    ev.stopPropagation();
    this.dropTarget = n.uuid;
  }

  drop(ev: DragEvent, n: DeptNode): void {
    ev.preventDefault();
    ev.stopPropagation();
    const d = this.dragging;
    this.dragEnd();
    if (d && d.parentUuid !== n.uuid && !subtreeOf(d).has(n.uuid)) this.move(d, n.uuid);
  }

  dragOverRoot(ev: DragEvent): void { ev.preventDefault(); this.dropTarget = 'root'; }
  dropRoot(ev: DragEvent): void {
    ev.preventDefault();
    const d = this.dragging;
    this.dragEnd();
    if (d && d.parentUuid) this.move(d, null);
  }

  private bodyOf(n: DeptNode, parentUuid: string | null | undefined): Record<string, any> {
    return { code: n.code, name: n.name, parentUuid: parentUuid ?? null, headEmployeeUuid: n.headEmployeeUuid ?? null, branchUuid: n.branchUuid ?? null, costCenterUuid: n.costCenterUuid ?? null };
  }

  private move(n: DeptNode, parent: string | null, done?: () => void): void {
    this.api.put(`hrm/departments/${n.uuid}`, this.bodyOf(n, parent)).subscribe({
      next: () => { done?.(); this.messages.add({ severity: 'success', summary: 'Moved', detail: `${n.name} is moved.`, life: 3000 }); this.load(); },
      error: err => {
        this.busy = false;
        const msg = hrmError(err, 'The department could not be moved.');
        if (this.moveOpen) this.dialogError = msg; else this.messages.add({ severity: 'error', summary: 'Not moved', detail: msg, life: 8000 });
      }
    });
  }

  // ── Move without dragging ──────────────────────────────────────────────

  /** Task 077 — the non-drag way: only places it can legally go, indented so the shape of the tree stays readable. */
  moveOptions(n: DeptNode): Opt[] {
    const barred = subtreeOf(n);
    return [{ label: 'Top level', value: '' }, ...this.flat(this.tree).filter(x => !barred.has(x.n.uuid)).map(x => ({ label: `${'  '.repeat(x.depth)}${x.n.name} (${x.n.code})`, value: x.n.uuid }))];
  }

  openMove(n: DeptNode): void {
    this.moving = n;
    this.moveFields = [{ key: 'parent', label: 'New parent', type: 'select', required: false, options: this.moveOptions(n) }];
    this.moveValues = { parent: n.parentUuid ?? '' };
    this.dialogError = '';
    this.moveOpen = true;
  }

  saveMove(): void {
    const n = this.moving!;
    const target = this.moveValues['parent'] || null;
    if (target === (n.parentUuid ?? null)) { this.moveOpen = false; return; }
    this.busy = true;
    this.move(n, target, () => { this.busy = false; this.moveOpen = false; });
  }

  // ── New / edit ─────────────────────────────────────────────────────────

  private open(): void { this.dialogError = ''; this.lookups.need(lookupsOf(FIELDS)).subscribe(() => { this.formOpen = true; }); }
  openNew(): void { this.editing = null; this.values = formDefaults(FIELDS); this.open(); }
  openEdit(n: DeptNode): void { this.editing = n; this.values = { ...formDefaults(FIELDS), ...this.bodyOf(n, n.parentUuid) }; this.open(); }

  saveForm(): void {
    this.dialogError = formProblem(FIELDS, this.values) ?? '';
    if (this.dialogError) return;
    const body = cleanBody(FIELDS, this.values);
    this.busy = true;
    (this.editing ? this.api.put(`hrm/departments/${this.editing.uuid}`, body) : this.api.post('hrm/departments', body)).subscribe({
      next: () => { this.busy = false; this.formOpen = false; this.lookups.reset(); this.messages.add({ severity: 'success', summary: 'Saved', detail: 'The department is saved.', life: 3000 }); this.load(); },
      error: err => { this.busy = false; this.dialogError = hrmError(err, 'The department could not be saved.'); }
    });
  }

  toggleActive(n: DeptNode): void {
    this.api.patch(`hrm/departments/${n.uuid}/active`, { isActive: !n.isActive }).subscribe({
      next: () => { this.lookups.reset(); this.load(); },
      error: err => this.messages.add({ severity: 'error', summary: 'Not changed', detail: hrmError(err, 'The change was refused.'), life: 8000 })
    });
  }
}
