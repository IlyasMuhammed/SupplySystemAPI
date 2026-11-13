import { Component, OnDestroy, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { CheckboxModule } from 'primeng/checkbox';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { InputTextModule } from 'primeng/inputtext';
import { ToastModule } from 'primeng/toast';
import { ConfirmationService, MessageService } from 'primeng/api';
import { Observable, Subject, Subscription, debounceTime, timer } from 'rxjs';

import { FLOW } from '../../../shared/flow';
import { saveDownload } from '../../../services/import.service';
import { AuthService } from '../../service/auth.service';
import { HrmApi, Page } from '../core/hrm.api';
import { HrmDialogComponent } from '../core/hrm-dialog.component';
import { Field, HrmFormComponent, cleanBody, formDefaults, formProblem } from '../core/hrm-form.component';
import { HrmPdfViewerComponent } from '../core/hrm-pdf-viewer.component';
import { HRM_ROUTE } from '../core/hrm.nav';
import { ENUMS, hrmError, hrmErrorAsync, hrmErrorCode, hrmLabel, hrmTone, money, today } from '../core/hrm.shared';
import { NEW_RUN_FIELDS } from '../configs/pay.config';
import { Readiness, ReadinessPanelComponent } from './readiness-panel.component';

export const STEPS = ['Period', 'Calculate', 'Review', 'Approve', 'Finalize', 'Pay'];

/** Which step a status belongs on (1-based). */
export function stepFor(status: string | null | undefined): number {
  switch (status) {
    case 'Draft': return 2;
    case 'Calculated': return 3;
    case 'UnderReview': return 4;
    case 'Approved': return 5;
    case 'Finalized': case 'Posted': case 'Paid': return 6;
    case 'Cancelled': case 'Reversed': return 3;
    default: return 1;
  }
}

/** A posting that needs someone's attention (retry). */
export function postingProblem(postings: { status: string }[] | undefined): boolean {
  return (postings ?? []).some(p => p.status === 'Failed' || p.status === 'Pending' || p.status === 'Held');
}

/** `PayrollPostingModel.targetResultsJson`: `[{ target, status, internalId, externalId, error }]` as the run service writes it. */
export interface PostingTargetResult { target?: string; status?: string; internalId?: string | null; externalId?: string | null; error?: string | null }

export function postingTargets(p: { targetResultsJson?: string | null } | null | undefined): PostingTargetResult[] {
  if (!p?.targetResultsJson) return [];
  try {
    const parsed = JSON.parse(p.targetResultsJson);
    return Array.isArray(parsed) ? parsed.filter(x => x && typeof x === 'object') : [];
  } catch { return []; }
}

/** "QBO: Posted, GL: Posted", or '—'. */
export function postingTargetText(p: { targetResultsJson?: string | null } | null | undefined): string {
  const t = postingTargets(p).map(x => [x.target, x.status].filter(Boolean).join(': ')).filter(Boolean);
  return t.length ? t.join(', ') : '—';
}

/** The posting's `lastError`, else the first target error. */
export function postingNote(p: { lastError?: string | null; targetResultsJson?: string | null } | null | undefined): string | null {
  return p?.lastError || postingTargets(p).map(x => x.error).find(e => !!e) || null;
}

type Dlg = 'pay' | 'reverse' | 'adjust';

const PAY_FIELDS: Field[] = [
  { key: 'paidOn', label: 'Paid on', type: 'date', required: true, def: today() }, { key: 'reference', label: 'Bank reference', required: true, maxLength: 60 },
  { key: 'paidFromAccount', label: 'Paid from', type: 'select', options: ENUMS.paidFrom, required: true, def: 'Bank' }
];
const REVERSE_FIELDS: Field[] = [{ key: 'reason', label: 'Why', type: 'textarea', required: true, maxLength: 500 }];

/**
 * Tasks 279–284, 286, 333 — the payroll run, one screen per step: Period → Calculate → Review → Approve → Finalize → Pay. What can be
 * done comes from the server (`allowedActions`), narrowed by the caller's permissions, so a button never offers what will be refused.
 * The route is the run: `payroll/runs/new`, then `payroll/runs/{uuid}`.
 */
@Component({
  selector: 'app-hrm-payroll-wizard',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule, ButtonModule, CheckboxModule, ConfirmDialogModule, InputTextModule, ToastModule, HrmDialogComponent, HrmFormComponent, HrmPdfViewerComponent, ReadinessPanelComponent, ...FLOW],
  providers: [MessageService, ConfirmationService],
  styleUrls: ['../../finance/gl/gl.scss', '../core/hrm.scss'],
  templateUrl: './payroll-wizard.component.html'
})
export class PayrollWizardComponent implements OnInit, OnDestroy {
  private readonly api = inject(HrmApi);
  private readonly auth = inject(AuthService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly messages = inject(MessageService);
  private readonly confirmer = inject(ConfirmationService);

  readonly steps = STEPS;
  readonly listRoute = `${HRM_ROUTE}/payroll/runs`;
  readonly hrmLabel = hrmLabel;
  readonly hrmTone = hrmTone;
  readonly money = money;
  readonly newFields = NEW_RUN_FIELDS;
  readonly payFields = PAY_FIELDS;
  readonly reverseFields = REVERSE_FIELDS;

  run: any = null;
  step = 1;
  loading = false;
  loadError = '';
  actionError = '';
  actionBlocked: string[] = [];
  busy = false;

  // Step 1
  newValues = formDefaults(NEW_RUN_FIELDS);

  // Progress
  progress: { processed: number; total: number; running: boolean } | null = null;
  private poll?: Subscription;

  // Lines
  lines: any[] = [];
  lineTotal = 0;
  linePage = 1;
  linePages = 1;
  lineSearch = '';
  blockedOnly = false;
  lineError = '';
  private readonly search$ = new Subject<void>();
  private searchSub?: Subscription;
  lineOpen = false;
  lineRow: any = null;

  // Approve
  excludeBlocked = false;

  readiness: Readiness | null = null;
  journal: any = null;
  journalOpen = false;
  recon: any = null;
  reconOpen = false;

  // Dialogs
  dlg: Dlg | null = null;
  dlgValues: Record<string, any> = {};
  dlgError = '';
  correctionUuid: string | null = null;

  viewer = { open: false, path: '', name: '' };

  can(code: string): boolean { return this.auth.hasPermission(code); }
  allowed(action: string): boolean { return (this.run?.allowedActions ?? []).includes(action); }

  /** Server says the action is open, and this user holds its code. */
  get canCalculate(): boolean { return this.allowed('calculate') && this.can('HRM_PAYROLL_RUN'); }
  get canReview(): boolean { return this.allowed('review') && this.can('HRM_PAYROLL_APPROVE'); }
  get canApprove(): boolean { return this.allowed('approve') && this.can('HRM_PAYROLL_APPROVE'); }
  get canReopen(): boolean { return this.allowed('reopen') && this.can('HRM_PAYROLL_APPROVE'); }
  get canCancel(): boolean { return this.allowed('cancel') && this.can('HRM_PAYROLL_RUN'); }
  get canFinalize(): boolean { return this.allowed('finalize') && this.can('HRM_PAYROLL_FINALIZE'); }
  get canRetry(): boolean { return this.allowed('retry-posting') && this.can('HRM_PAYROLL_FINALIZE'); }
  get canBankFile(): boolean { return this.allowed('bank-file') && this.can('HRM_PAYROLL_PAY') && this.can('HRM_SCOPE_ALL'); }
  get canPay(): boolean { return this.allowed('pay') && this.can('HRM_PAYROLL_PAY'); }
  get canReverse(): boolean { return this.allowed('reverse') && this.can('HRM_PAYROLL_REVERSE'); }
  get canPayslips(): boolean { return this.allowed('payslips') && this.can('HRM_PAYROLL_RUN'); }

  get terminal(): boolean { return this.run?.status === 'Cancelled' || this.run?.status === 'Reversed'; }
  /** The furthest step this run has reached. */
  get reached(): number { return stepFor(this.run?.status); }
  get noTargetPosting(): boolean { return (this.run?.postings ?? []).some((p: any) => p.status === 'NoTarget'); }
  get postingNeedsRetry(): boolean { return postingProblem(this.run?.postings); }
  /** The run model has no note of its own: the first problem posting's error. */
  get problemNote(): string | null {
    const p = (this.run?.postings ?? []).find((x: any) => postingProblem([x]) && postingNote(x));
    return p ? postingNote(p) : null;
  }
  readonly postingTargetText = postingTargetText;
  readonly postingNote = postingNote;
  get progressPct(): number { const p = this.progress; return p && p.total ? Math.min(100, Math.round((p.processed / p.total) * 100)) : 0; }
  get warningTexts(): string[] { return (this.run?.warnings ?? []).map((x: any) => typeof x === 'string' ? x : (x?.message ?? '')).filter(Boolean); }
  get blockReasonCount(): number { return Number(this.run?.blockedEmployees ?? 0); }

  ngOnInit(): void {
    this.searchSub = this.search$.pipe(debounceTime(300)).subscribe(() => { this.linePage = 1; this.loadLines(); });
    const uuid = this.route.snapshot.paramMap.get('uuid');
    if (uuid) this.load(uuid);
  }

  ngOnDestroy(): void { this.poll?.unsubscribe(); this.searchSub?.unsubscribe(); }

  // ── Loading ────────────────────────────────────────────────────────────

  load(uuid: string, stay = false): void {
    this.loading = !this.run;
    this.api.get<any>(`hrm/payroll-runs/${uuid}`).subscribe({
      next: r => { this.loading = false; this.setRun(r, stay); },
      error: err => { this.loading = false; this.loadError = hrmError(err, 'The payroll run could not be loaded.'); }
    });
  }

  private setRun(r: any, stay = false): void {
    this.run = r;
    if (!stay) this.step = stepFor(r.status);
    this.loadStepData();
    // Task 286 — on a phone the steps scroll sideways: keep the current one in view.
    setTimeout(() => document.querySelector('.hrm-steps button.on')?.scrollIntoView?.({ inline: 'center', block: 'nearest' }));
  }

  goStep(n: number): void {
    if (!this.run || n > this.reached || n === 1) return;
    this.step = n;
    this.actionError = '';
    this.actionBlocked = [];
    this.loadStepData();
  }

  private loadStepData(): void {
    if (this.step >= 3 && this.run && this.run.status !== 'Draft') this.loadLines();
  }

  loadLines(): void {
    if (!this.run) return;
    this.lineError = '';
    this.api.get<Page<any>>(`hrm/payroll-runs/${this.run.uuid}/lines`, { search: this.lineSearch, blockedOnly: this.blockedOnly || undefined, page: this.linePage }).subscribe({
      next: p => { this.lines = p?.data ?? []; this.lineTotal = p?.totalRecords ?? this.lines.length; this.linePages = p?.totalPages ?? 1; },
      error: err => { this.lines = []; this.lineError = hrmError(err, 'The lines could not be loaded.'); }
    });
  }

  onLineSearch(): void { this.search$.next(); }
  setBlockedOnly(v: boolean): void { this.blockedOnly = v; this.linePage = 1; this.loadLines(); }
  goPage(d: number): void { this.linePage = Math.min(Math.max(1, this.linePage + d), this.linePages); this.loadLines(); }
  openLine(l: any): void { this.lineRow = l; this.lineOpen = true; }
  earnings(l: any): any[] { return (l?.components ?? []).filter((c: any) => c.type === 'Earning'); }
  deductions(l: any): any[] { return (l?.components ?? []).filter((c: any) => c.type === 'Deduction'); }
  employer(l: any): any[] { return (l?.components ?? []).filter((c: any) => c.type === 'EmployerContribution'); }

  // ── Step 1: create ─────────────────────────────────────────────────────

  create(): void {
    this.actionError = formProblem(NEW_RUN_FIELDS, this.newValues) ?? '';
    if (this.actionError) return;
    this.busy = true;
    this.api.post<any>('hrm/payroll-runs', cleanBody(NEW_RUN_FIELDS, this.newValues)).subscribe({
      next: r => {
        this.busy = false;
        this.router.navigate([this.listRoute, r.uuid], { replaceUrl: true });
        this.setRun(r);
      },
      error: err => { this.busy = false; this.actionError = hrmError(err, 'The run could not be created.'); }
    });
  }

  // ── Step 2: calculate ──────────────────────────────────────────────────

  calculate(): void {
    this.busy = true;
    this.actionError = '';
    this.api.postStatus<any>(`hrm/payroll-runs/${this.run.uuid}/calculate`).subscribe({
      next: ({ status, result }) => {
        if (status === 202 || result?.calculationQueued) { this.watch(); return; }
        this.busy = false;
        this.setRun(result?.uuid ? result : this.run);
        if (!result?.uuid) this.load(this.run.uuid);
      },
      error: err => { this.busy = false; this.actionError = hrmError(err, 'The calculation failed.'); }
    });
  }

  /** Over 200 employees the server works in the background (§9): the bar follows it, and the page reads the run when it is done. */
  private watch(): void {
    this.progress = { processed: 0, total: 0, running: true };
    this.poll?.unsubscribe();
    this.poll = timer(0, 2000).subscribe(() => {
      this.api.get<any>(`hrm/payroll-runs/${this.run.uuid}/progress`).subscribe({
        next: p => {
          this.progress = { processed: p?.processed ?? 0, total: p?.total ?? 0, running: !!p?.running };
          if (p?.running) return;
          this.poll?.unsubscribe();
          this.busy = false;
          this.progress = null;
          if (p?.error) this.actionError = p.error;
          this.load(this.run.uuid);
        },
        error: err => { this.poll?.unsubscribe(); this.busy = false; this.progress = null; this.actionError = hrmError(err, 'Progress could not be read.'); }
      });
    });
  }

  // ── Plain actions ──────────────────────────────────────────────────────

  private act(call: Observable<any>, fallback: string, done?: string, then?: (r: any) => void): void {
    this.busy = true;
    this.actionError = '';
    this.actionBlocked = [];
    call.subscribe({
      next: r => {
        this.busy = false;
        if (done) this.messages.add({ severity: 'success', summary: 'Done', detail: done, life: 3500 });
        then?.(r);
        if (r?.uuid) this.setRun(r); else this.load(this.run.uuid);
      },
      error: err => {
        this.busy = false;
        this.actionError = hrmError(err, fallback);
        this.actionBlocked = this.detailOf(err);
      }
    });
  }

  /** A 409 with detail (PAYROLL_STATUTORY_UNVERIFIED lists them; POSTING_READINESS carries the same model as the panel). */
  private detailOf(err: any): string[] {
    const r = err?.error?.result ?? err?.error?.errors ?? err?.error?.detail;
    if (!Array.isArray(r)) return [];
    return r.map((x: any) => typeof x === 'string' ? x : (x?.name ? `${x.kind ?? ''} ${x.name}`.trim() : x?.message ?? '')).filter(Boolean);
  }

  review(): void { this.act(this.api.post(`hrm/payroll-runs/${this.run.uuid}/review`), 'The run could not move to review.', 'The run is under review.'); }

  approve(): void {
    const excluded = this.excludeBlocked;
    this.ask('Approve run', `Approve ${this.run.runNumber} for ${money(this.run.totalNet)} ${this.run.currencyCode}?${excluded ? ` ${this.blockReasonCount} blocked employees are left out.` : ''}`, 'pi pi-check', () =>
      this.api.post(`hrm/payroll-runs/${this.run.uuid}/approve`, { excludeBlocked: excluded }).subscribe({
        next: (r: any) => { this.messages.add({ severity: 'success', summary: 'Approved', detail: 'The run is approved and locked.', life: 3500 }); if (r?.uuid) this.setRun(r); else this.load(this.run.uuid); },
        error: err => {
          this.actionError = hrmErrorCode(err) === 'PAYROLL_HAS_BLOCKED'
            ? `${hrmError(err, '')} Fix them, or tick "Leave out blocked employees".`.trim()
            : hrmError(err, 'The run could not be approved.');
        }
      }));
  }

  reopen(): void { this.ask('Reopen', `Reopen ${this.run.runNumber}? It goes back to Calculated.`, 'pi pi-undo', () => this.act(this.api.post(`hrm/payroll-runs/${this.run.uuid}/reopen`), 'The run could not be reopened.', 'The run is reopened.')); }
  cancelRun(): void { this.ask('Cancel run', `Cancel ${this.run.runNumber}? This cannot be undone.`, 'pi pi-ban', () => this.act(this.api.post(`hrm/payroll-runs/${this.run.uuid}/cancel`), 'The run could not be cancelled.', 'The run is cancelled.')); }

  finalize(): void {
    this.ask('Finalize', `Finalize ${this.run.runNumber}? It is posted to accounting, and from here it can only be reversed.`, 'pi pi-lock', () =>
      this.act(this.api.post(`hrm/payroll-runs/${this.run.uuid}/finalize`), 'The run could not be finalized.'));
  }

  retry(): void { this.act(this.api.post(`hrm/payroll-runs/${this.run.uuid}/retry-posting`), 'Posting failed again.', 'Posting retried.'); }
  payslips(): void {
    this.busy = true;
    this.api.post<any>(`hrm/payroll-runs/${this.run.uuid}/payslips`).subscribe({
      next: r => { this.busy = false; this.messages.add({ severity: 'success', summary: 'Payslips', detail: `${r?.generated ?? 0} generated, ${r?.alreadyGenerated ?? 0} already there.`, life: 4000 }); },
      error: err => { this.busy = false; this.actionError = hrmError(err, 'The payslips could not be generated.'); }
    });
  }

  onReadiness(r: Readiness | null): void { this.readiness = r; }

  // ── Downloads (a refusal comes as a Blob) ──────────────────────────────

  private download(path: string, name: string): void {
    this.api.blob(path).subscribe({
      next: res => saveDownload(res, name),
      error: async err => { this.actionError = await hrmErrorAsync(err, 'The file could not be downloaded.'); }
    });
  }
  bankFile(): void { this.download(`hrm/payroll-runs/${this.run.uuid}/bank-file`, `${this.run.runNumber}-bank.csv`); }
  exportJournal(kind: 'csv' | 'xlsx'): void { this.download(`hrm/payroll-runs/${this.run.uuid}/journal-export/${kind}`, `${this.run.runNumber}-journal.${kind}`); }
  viewPayslip(l: any): void { this.viewer = { open: true, path: `hrm/payroll-runs/${this.run.uuid}/lines/${l.uuid}/payslip`, name: `payslip-${l.employeeNumber}-${this.run.runNumber}.pdf` }; }

  openJournal(): void {
    this.api.get<any>(`hrm/payroll-runs/${this.run.uuid}/journal`).subscribe({
      next: j => { this.journal = j; this.journalOpen = true; },
      error: err => { this.actionError = hrmError(err, 'The journal could not be loaded.'); }
    });
  }

  openRecon(): void {
    this.api.get<any>(`hrm/payroll-runs/${this.run.uuid}/reconciliation`).subscribe({
      next: r => { this.recon = r; this.reconOpen = true; },
      error: err => { this.actionError = hrmError(err, 'The reconciliation could not be loaded.'); }
    });
  }

  // ── Pay / reverse ──────────────────────────────────────────────────────

  get dlgOpen(): boolean { return this.dlg !== null; }
  set dlgOpen(v: boolean) { if (!v) this.dlg = null; }

  openPay(): void { this.dlg = 'pay'; this.dlgValues = formDefaults(PAY_FIELDS); this.dlgError = ''; }
  openReverse(): void { this.dlg = 'reverse'; this.dlgValues = formDefaults(REVERSE_FIELDS); this.dlgError = ''; }

  saveDlg(): void {
    const fields = this.dlg === 'pay' ? PAY_FIELDS : REVERSE_FIELDS;
    this.dlgError = formProblem(fields, this.dlgValues) ?? '';
    if (this.dlgError) return;
    const path = `hrm/payroll-runs/${this.run.uuid}/${this.dlg === 'pay' ? 'pay' : 'reverse'}`;
    const reversing = this.dlg === 'reverse';
    this.busy = true;
    this.api.post<any>(path, cleanBody(fields, this.dlgValues)).subscribe({
      next: r => {
        this.busy = false;
        this.dlg = null;
        this.correctionUuid = reversing ? (r?.correctionRunUuid ?? null) : this.correctionUuid;
        this.messages.add({ severity: 'success', summary: 'Done', detail: reversing ? 'The run is reversed. A new draft takes its place.' : 'The run is paid.', life: 4000 });
        if (r?.uuid) this.setRun(r); else this.load(this.run.uuid);
      },
      error: err => { this.busy = false; this.dlgError = hrmError(err, 'It was refused.'); }
    });
  }

  private ask(header: string, message: string, icon: string, accept: () => void): void {
    this.confirmer.confirm({ key: 'hrm-pay', header, message, icon, acceptLabel: header, rejectLabel: 'Cancel', rejectButtonStyleClass: 'p-button-text', accept });
  }
}
