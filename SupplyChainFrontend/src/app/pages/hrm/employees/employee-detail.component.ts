import { Component, OnDestroy, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';
import { Observable } from 'rxjs';

import { FLOW } from '../../../shared/flow';
import { ModuleService } from '../../../services/module.service';
import { AuthService } from '../../service/auth.service';
import { HrmApi, rowsOf } from '../core/hrm.api';
import { HrmDialogComponent } from '../core/hrm-dialog.component';
import { Field, HrmFormComponent, cleanBody, formDefaults, formProblem, lookupsOf, markFilesSaved } from '../core/hrm-form.component';
import { HrmLookups } from '../core/hrm-lookups.service';
import { HrmPdfViewerComponent } from '../core/hrm-pdf-viewer.component';
import { F, HRM_ROUTE } from '../core/hrm.nav';
import { hrmError, hrmLabel, hrmTone, money, today } from '../core/hrm.shared';
import { BANK_FIELDS, DOCUMENT_FIELDS, IDENTITY_FIELDS, PERSONAL_FIELDS, PROMOTION_FIELDS, RECORD_FIELDS, STATUS_FIELDS, TRANSFER_FIELDS, employeeToForm, formToEmployee } from './employee.fields';

type Tab = 'personal' | 'employment' | 'bank' | 'documents' | 'salary' | 'payroll';
type Dlg = 'edit' | 'status' | 'record' | 'transfer' | 'promotion' | 'bank' | 'document' | 'salary' | 'rejectSalary';

/** How long a revealed value stays on screen. */
export const REVEAL_MS = 30000;

/**
 * Tasks 080, 191, 285 — one employee: Personal, Employment, Bank, Documents, Salary, Payroll history. S3 values show masked; Reveal
 * calls the audited `…/sensitive` route only when asked, holds the answer in memory only and hides it again after 30 seconds (D-23).
 * S2 amounts come back null without the permission, and the tab is gone.
 */
@Component({
  selector: 'app-hrm-employee-detail',
  standalone: true,
  imports: [CommonModule, RouterModule, ButtonModule, ToastModule, HrmDialogComponent, HrmFormComponent, HrmPdfViewerComponent, ...FLOW],
  providers: [MessageService, HrmLookups],
  styleUrls: ['../../finance/gl/gl.scss', '../core/hrm.scss'],
  templateUrl: './employee-detail.component.html'
})
export class EmployeeDetailComponent implements OnInit, OnDestroy {
  private readonly api = inject(HrmApi);
  private readonly auth = inject(AuthService);
  private readonly route = inject(ActivatedRoute);
  private readonly messages = inject(MessageService);
  private readonly modules = inject(ModuleService, { optional: true });
  readonly lookups = inject(HrmLookups);

  readonly back = `${HRM_ROUTE}/employees`;
  readonly runsRoute = `${HRM_ROUTE}/payroll/runs`;
  readonly hrmLabel = hrmLabel;
  readonly hrmTone = hrmTone;
  readonly money = money;

  uuid = '';
  e: any = null;
  loading = true;
  loadError = '';
  tab: Tab = 'personal';

  records: any[] = [];
  banks: any[] = [];
  docs: any[] = [];
  assignments: any[] = [];
  history: any[] = [];
  tabError = '';

  // S3 reveal
  identity: { cnic?: string | null; ntn?: string | null } | null = null;
  bankReveal: Record<string, { accountNumber?: string | null; iban?: string | null }> = {};
  private timers: ReturnType<typeof setTimeout>[] = [];

  // Dialogs
  dlg: Dlg | null = null;
  dlgFields: Field[] = [];
  dlgValues: Record<string, any> = {};
  dlgRow: any = null;
  dlgError = '';
  busy = false;

  viewer = { open: false, path: '', attachment: null as string | null, name: '' };

  can(...codes: string[]): boolean { return this.auth.hasAnyPermission(...codes); }
  private feature(code: string): boolean { return this.modules ? this.modules.isFeatureEnabled(code) : true; }

  get tabs(): { key: Tab; label: string }[] {
    const out: { key: Tab; label: string }[] = [{ key: 'personal', label: 'Personal' }];
    if (this.can('HRM_EMPLOYEE_VIEW', 'HRM_EMPLOYEE_EDIT', 'HRM_EMPLOYMENT_MANAGE')) out.push({ key: 'employment', label: 'Employment' });
    if (this.can('HRM_BANK_VIEW', 'HRM_BANK_EDIT')) out.push({ key: 'bank', label: 'Bank' });
    if (this.can('HRM_EMPLOYEE_EDIT', 'HRM_EMPLOYEE_SENSITIVE_VIEW')) out.push({ key: 'documents', label: 'Documents' });
    if (this.feature(F.PAYROLL) && this.can('HRM_SALARY_VIEW')) out.push({ key: 'salary', label: 'Salary' });
    if (this.feature(F.PAYROLL) && this.can('HRM_PAYROLL_VIEW')) out.push({ key: 'payroll', label: 'Payroll history' });
    return out;
  }

  ngOnInit(): void {
    this.uuid = this.route.snapshot.paramMap.get('uuid') ?? '';
    this.load();
  }

  ngOnDestroy(): void { this.timers.forEach(t => clearTimeout(t)); }

  load(): void {
    this.api.get<any>(`hrm/employees/${this.uuid}`).subscribe({
      next: e => { this.loading = false; this.e = e; this.loadTab(); },
      error: err => { this.loading = false; this.loadError = hrmError(err, 'The employee could not be loaded.'); }
    });
  }

  go(t: Tab): void { this.tab = t; this.tabError = ''; this.loadTab(); }

  private list(path: string, set: (rows: any[]) => void): void {
    this.api.get<any>(path).subscribe({ next: r => set(rowsOf(r)), error: err => { set([]); this.tabError = hrmError(err, 'This could not be loaded.'); } });
  }

  private loadTab(): void {
    const base = `hrm/employees/${this.uuid}`;
    switch (this.tab) {
      case 'employment': this.list(`${base}/employment`, r => this.records = r); break;
      case 'bank': this.list(`${base}/bank-accounts`, r => this.banks = r); break;
      case 'documents': this.list(`${base}/documents`, r => this.docs = r); break;
      case 'salary': this.list(`${base}/salary`, r => this.assignments = r); break;
      case 'payroll': this.list(`${base}/payroll-history`, r => this.history = r); break;
    }
  }

  // ── Reveal (S3) ────────────────────────────────────────────────────────

  private hideLater(fn: () => void): void { this.timers.push(setTimeout(fn, REVEAL_MS)); }

  revealIdentity(): void {
    this.api.get<any>(`hrm/employees/${this.uuid}/sensitive`).subscribe({
      next: v => { this.identity = v; this.hideLater(() => { this.identity = null; }); },
      error: err => this.toastError(hrmError(err, 'The value could not be revealed.'))
    });
  }
  hideIdentity(): void { this.identity = null; }

  revealBank(a: any): void {
    this.api.get<any>(`hrm/employees/${this.uuid}/bank-accounts/${a.uuid}/sensitive`).subscribe({
      next: v => { this.bankReveal[a.uuid] = v; this.hideLater(() => { delete this.bankReveal[a.uuid]; }); },
      error: err => this.toastError(hrmError(err, 'The value could not be revealed.'))
    });
  }
  hideBank(a: any): void { delete this.bankReveal[a.uuid]; }

  // ── Dialog plumbing ────────────────────────────────────────────────────

  get dlgTitle(): string {
    return { edit: 'Edit personal details', status: 'Change status', record: 'New employment record', transfer: 'Transfer', promotion: 'Promotion', bank: this.dlgRow ? 'Edit bank account' : 'Add bank account', document: 'Add document', salary: 'New salary assignment', rejectSalary: 'Reject salary' }[this.dlg ?? 'edit'];
  }

  private open(kind: Dlg, fields: Field[], values?: Record<string, any>, row: any = null): void {
    this.dlg = kind;
    this.dlgFields = fields;
    this.dlgValues = { ...formDefaults(fields), ...(values ?? {}) };
    this.dlgRow = row;
    this.dlgError = '';
    this.lookups.need(lookupsOf(fields)).subscribe();
  }

  get dlgOpen(): boolean { return this.dlg !== null; }
  set dlgOpen(v: boolean) { if (!v) this.dlg = null; }

  openEdit(): void { this.open('edit', [...PERSONAL_FIELDS, ...IDENTITY_FIELDS], employeeToForm(this.e)); }

  openStatus(): void {
    const opts = (this.e.allowedStatuses ?? []).map((s: string) => ({ label: hrmLabel(s), value: s }));
    this.open('status', STATUS_FIELDS.map(f => f.key === 'status' ? { ...f, options: opts } : f));
  }

  openRecord(): void { this.open('record', RECORD_FIELDS); }
  openTransfer(): void { this.open('transfer', TRANSFER_FIELDS); }
  openPromotion(): void { this.open('promotion', PROMOTION_FIELDS); }
  openBank(a?: any): void { this.open('bank', BANK_FIELDS, a ? { ...a, accountNumber: null, iban: null } : undefined, a ?? null); }

  openDoc(): void {
    const up: Field = { key: 'attachmentUuid', label: 'File', type: 'file', required: true, interfaceCode: 'HRM_EMPLOYEE', docId: () => this.uuid };
    this.open('document', [...DOCUMENT_FIELDS, up]);
  }

  /** Task 191 — employee → structure → component amounts. Picking the structure fills its components. */
  openSalary(): void {
    const fields: Field[] = [
      { key: 'structureUuid', label: 'Structure', type: 'lookup', lookup: 'structures', required: true },
      { key: 'effectiveFrom', label: 'Effective from', type: 'date', required: true, def: today() },
      { key: 'changeReason', label: 'Reason', maxLength: 200 },
      { key: 'lines', label: 'Component amounts', type: 'rows', required: true, rows: [{ key: 'componentUuid', label: 'Component', type: 'lookup', lookup: 'components', required: true }, { key: 'amount', label: 'Amount', type: 'number', required: true, min: 0 }] }
    ];
    this.open('salary', fields);
  }

  /** Fills the lines from the chosen structure, once, so the amounts can be typed in. */
  fillFromStructure(): void {
    const id = this.dlgValues['structureUuid'];
    if (!id) return;
    this.api.get<any>(`hrm/salary-structures/${id}`).subscribe({
      next: s => { this.dlgValues['lines'] = (s?.lines ?? []).map((l: any) => ({ componentUuid: l.componentUuid, amount: l.valueOverride ?? 0 })); },
      error: err => { this.dlgError = hrmError(err, 'The structure could not be read.'); }
    });
  }

  saveDlg(): void {
    this.dlgError = formProblem(this.dlgFields, this.dlgValues) ?? '';
    if (this.dlgError) return;
    const v = cleanBody(this.dlgFields, this.dlgValues);
    const base = `hrm/employees/${this.uuid}`;
    let call: Observable<any>;
    switch (this.dlg) {
      case 'edit': call = this.api.put(base, formToEmployee(v, this.e)); break;
      case 'status': call = this.api.patch(`${base}/status`, v); break;
      case 'record': call = this.api.post(`${base}/employment`, v); break;
      case 'transfer': call = this.api.post(`${base}/employment/transfer`, v); break;
      case 'promotion': call = this.api.post(`${base}/employment/promotion`, v); break;
      case 'bank': call = this.dlgRow ? this.api.put(`${base}/bank-accounts/${this.dlgRow.uuid}`, v) : this.api.post(`${base}/bank-accounts`, v); break;
      case 'document': call = this.api.post(`${base}/documents`, v); break;
      case 'salary': call = this.api.post(`${base}/salary`, v); break;
      case 'rejectSalary': call = this.api.post(`hrm/salary-assignments/${this.dlgRow.uuid}/reject`, v); break;
      default: return;
    }
    this.busy = true;
    call.subscribe({
      next: () => { markFilesSaved(this.dlgValues); this.busy = false; this.dlg = null; this.toastOk('Saved.'); this.load(); },
      error: err => { this.busy = false; this.dlgError = hrmError(err, 'It could not be saved.'); }
    });
  }

  // ── One-click row actions ──────────────────────────────────────────────

  private act(call: Observable<any>, done: string): void {
    call.subscribe({ next: () => { this.toastOk(done); this.load(); }, error: err => this.toastError(hrmError(err, 'It was refused.')) });
  }

  primary(a: any): void { this.act(this.api.post(`hrm/employees/${this.uuid}/bank-accounts/${a.uuid}/primary`), 'Primary account changed.'); }
  deactivateBank(a: any): void { this.act(this.api.delete(`hrm/employees/${this.uuid}/bank-accounts/${a.uuid}`), 'Account deactivated.'); }
  deleteDoc(d: any): void { this.act(this.api.delete(`hrm/employees/${this.uuid}/documents/${d.uuid}`), 'Document removed.'); }
  submitSalary(a: any): void { this.act(this.api.post(`hrm/salary-assignments/${a.uuid}/submit`), 'Submitted for approval.'); }
  approveSalary(a: any): void { this.act(this.api.post(`hrm/salary-assignments/${a.uuid}/approve`), 'Salary approved.'); }
  openReject(a: any): void { this.open('rejectSalary', [{ key: 'note', label: 'Why', type: 'textarea', required: true, maxLength: 300 }], undefined, a); }

  viewDoc(d: any): void { this.viewer = { open: true, path: '', attachment: d.attachmentUuid, name: d.title }; }

  /** FE-5 — neither link for a blocked line or a run before Finalized. */
  hasPayslip(h: any): boolean { return !h.isBlocked && ['Finalized', 'Posted', 'Paid'].includes(h.status); }
  viewPayslip(h: any): void {
    const path = h.payslipUuid ? `hrm/payslips/${h.payslipUuid}/pdf` : `hrm/payroll-runs/${h.runUuid}/lines/${h.lineUuid}/payslip`;
    this.viewer = { open: true, path, attachment: null, name: `payslip-${h.runNumber}.pdf` };
  }

  private toastOk(detail: string): void { this.messages.add({ severity: 'success', summary: 'Done', detail, life: 3000 }); }
  private toastError(detail: string): void { this.messages.add({ severity: 'error', summary: 'Not done', detail, life: 8000 }); }
}
