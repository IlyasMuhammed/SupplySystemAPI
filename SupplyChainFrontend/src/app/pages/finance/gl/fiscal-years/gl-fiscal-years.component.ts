import { Component, EventEmitter, Input, OnInit, Output, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { DatePickerModule } from 'primeng/datepicker';
import { DialogModule } from 'primeng/dialog';
import { InputTextModule } from 'primeng/inputtext';
import { SelectModule } from 'primeng/select';
import { TableModule } from 'primeng/table';
import { ToastModule } from 'primeng/toast';
import { ConfirmationService, MessageService } from 'primeng/api';
import { catchError, of } from 'rxjs';

import { AccountingService, FiscalPeriod, FiscalYear, GL_PERMS, PeriodStatus } from '../../../../services/accounting.service';
import { OrgCurrencyService } from '../../../../services/org-currency.service';
import { AuthService } from '../../../service/auth.service';
import { FLOW } from '../../../../shared/flow';
import { fromIsoDate, toIsoDate } from '../../../finance-setup/finance-setup.shared';
import { glError, periodTone } from '../gl.shared';

export interface YearDraft { fiscalYearName: string; startDate: Date | null; endDate: Date | null; reportingCurrencyCode: string | null; }

/** What the server would refuse about a new/edited year; null when it would accept it. */
export function yearProblem(d: YearDraft, allowBlankName = false): string | null {
  if (!d.fiscalYearName?.trim() && !allowBlankName) return 'Give the fiscal year a name, e.g. FY 2026-27.';
  if ((d.fiscalYearName ?? '').trim().length > 100) return 'The name can be at most 100 characters.';
  if (!d.startDate || !d.endDate) return 'Choose the start and end dates.';
  if (d.endDate <= d.startDate) return 'The end date must be after the start date.';
  if (d.endDate > defaultYearEnd(d.startDate)) return 'A fiscal year can be at most 12 months long.';
  if (!d.reportingCurrencyCode) return 'Choose the reporting currency.';
  return null;
}

/** The period transitions D-18 allows: Open→Locked/Closed, Locked→Open/Closed; Closed is final. */
export function periodActions(s: PeriodStatus): ('Lock' | 'Reopen' | 'Close')[] {
  if (s === 'Open') return ['Lock', 'Close'];
  if (s === 'Locked') return ['Reopen', 'Close'];
  return [];
}

/** The 12 months starting on the 1st of `start`'s month — the default end of a new year. */
export function defaultYearEnd(start: Date): Date {
  return new Date(start.getFullYear() + 1, start.getMonth(), start.getDate() - 1);
}

/**
 * A42 (0381–0393) — `/gl/fiscal-years`: a card per year (name, dates, currency, Active/Closed) with its periods and their
 * status pills; New / Edit / Close year; Lock / Reopen / Close a period with a confirmation (Closed is final). Also step 3
 * of the wizard (`embedded`).
 */
@Component({
  selector: 'app-gl-fiscal-years',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, ConfirmDialogModule, DatePickerModule, DialogModule, InputTextModule, SelectModule,
    TableModule, ToastModule, ...FLOW],
  providers: [ConfirmationService],
  styleUrls: ['../gl.scss'],
  template: `
<p-toast *ngIf="!embedded"></p-toast>
<p-confirmDialog key="gl-fy"></p-confirmDialog>

<ng-template #actions>
  <p-button *ngIf="canManage" label="New fiscal year" icon="pi pi-plus" (onClick)="openNew()" data-testid="new-year"></p-button>
  <p-button icon="pi pi-refresh" [text]="true" (onClick)="load()" ariaLabel="Refresh"></p-button>
</ng-template>

<ng-template #content>
  <div class="sf-empty sf-card" *ngIf="loading"><i class="pi pi-spin pi-spinner"></i> Loading…</div>
  <div class="gl-note er" *ngIf="loadError" data-testid="load-error"><i class="pi pi-exclamation-triangle"></i><div>{{ loadError }}</div></div>
  <div class="sf-card sf-empty" *ngIf="!loading && !loadError && !years.length" data-testid="no-years">
    No fiscal year yet. Create the first one — usually the year your opening balance date falls in.
  </div>

  <section class="sf-card p0" *ngFor="let y of years" [attr.data-testid]="'year-' + y.fiscalYearName">
    <div class="sf-ch sf-pad gl-year-head">
      <div>
        <h2>{{ y.fiscalYearName }} <span class="sf-pill" [ngClass]="y.isClosed ? 'er' : 'ok'" data-testid="year-status">{{ y.isClosed ? 'Closed' : 'Active' }}</span></h2>
        <div class="sf-mu sf-sm">{{ y.startDate | date:'d MMM y' }} – {{ y.endDate | date:'d MMM y' }} · {{ y.reportingCurrencyCode }}
          · {{ y.periods.length }} periods<ng-container *ngIf="y.hasPostings"> · has postings</ng-container><ng-container *ngIf="y.closedAt"> · closed {{ y.closedAt | date:'d MMM y' }}</ng-container></div>
      </div>
      <span class="gl-actions" *ngIf="canManage && !y.isClosed">
        <p-button label="Edit" icon="pi pi-pencil" [text]="true" size="small" (onClick)="openEdit(y)" [attr.data-testid]="'edit-' + y.fiscalYearName"></p-button>
        <p-button label="Close year" icon="pi pi-lock" [text]="true" size="small" severity="danger" (onClick)="confirmCloseYear(y)"
                  [attr.data-testid]="'close-' + y.fiscalYearName"></p-button>
      </span>
    </div>
    <div class="gl-scroll">
      <p-table [value]="y.periods" styleClass="p-datatable-sm" [tableStyle]="{ 'min-width': '36rem' }">
        <ng-template pTemplate="header"><tr><th style="width:3rem">#</th><th>Period</th><th>From</th><th>To</th><th>Status</th><th></th></tr></ng-template>
        <ng-template pTemplate="body" let-p>
          <tr [attr.data-testid]="'period-' + p.periodNumber">
            <td class="sf-mu">{{ p.periodNumber }}</td>
            <td>{{ p.periodName }} <span class="sf-pill vi" *ngIf="p.isAdjustment || p.periodNumber === 13">adjustments</span></td>
            <td>{{ p.startDate | date:'d MMM y' }}</td>
            <td>{{ p.endDate | date:'d MMM y' }}</td>
            <td><span class="sf-pill" [ngClass]="tone(p.status)" data-testid="period-status">{{ p.status }}</span></td>
            <td class="text-right">
              <span class="gl-actions" *ngIf="canManage && !y.isClosed">
                <p-button *ngFor="let a of actionsFor(p)" [label]="a" [text]="true" size="small" [severity]="a === 'Close' ? 'danger' : undefined"
                          [disabled]="busyUuid === p.uuid" (onClick)="confirmPeriod(p, a)" [attr.data-testid]="a.toLowerCase() + '-' + p.periodNumber"></p-button>
              </span>
            </td>
          </tr>
        </ng-template>
      </p-table>
    </div>
  </section>
</ng-template>

<sf-page *ngIf="!embedded" [crumbs]="['Finance', 'General Ledger', 'Fiscal Years']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands"><ng-container *ngTemplateOutlet="actions"></ng-container></div>
  <div class="sf-body">
    <div class="sf-lr-head"><div><h1>Fiscal years <span class="sf-count">{{ years.length }}</span></h1>
      <div class="sf-sub">Every posting is dated into a period; lock or close periods to protect reported figures</div></div></div>
    <ng-container *ngTemplateOutlet="content"></ng-container>
  </div>
</sf-page>
<div *ngIf="embedded" class="gl-embedded">
  <div class="sf-cmd" role="toolbar" aria-label="Fiscal year commands"><ng-container *ngTemplateOutlet="actions"></ng-container></div>
  <ng-container *ngTemplateOutlet="content"></ng-container>
</div>

<p-dialog [(visible)]="dialogVisible" [modal]="true" [draggable]="false" [style]="{ width: '34rem' }" [breakpoints]="{ '640px': '96vw' }"
          [header]="editingYear ? 'Edit ' + editingYear.fiscalYearName : 'New fiscal year'">
  <div class="sf-fields">
    <div class="sf-fld" style="grid-column: 1 / -1">
      <label for="fy-name" [class.sf-req]="!!editingYear">Name</label>
      <input pInputText id="fy-name" [(ngModel)]="draft.fiscalYearName" maxlength="100" [placeholder]="editingYear ? 'FY 2026-27' : 'Leave blank for FY 2026-27'" data-testid="fy-name" />
    </div>
    <div class="sf-fld">
      <label for="fy-start" class="sf-req">Start date</label>
      <p-datepicker inputId="fy-start" [(ngModel)]="draft.startDate" dateFormat="dd M yy" [showIcon]="true" appendTo="body"
                    [disabled]="datesLocked" (ngModelChange)="onStartChange()" data-testid="fy-start"></p-datepicker>
    </div>
    <div class="sf-fld">
      <label for="fy-end" class="sf-req">End date</label>
      <p-datepicker inputId="fy-end" [(ngModel)]="draft.endDate" dateFormat="dd M yy" [showIcon]="true" appendTo="body"
                    [disabled]="datesLocked" data-testid="fy-end"></p-datepicker>
    </div>
    <div class="sf-fld">
      <label for="fy-cur" class="sf-req">Reporting currency</label>
      <p-select inputId="fy-cur" [options]="currencyOptions" [(ngModel)]="draft.reportingCurrencyCode" [filter]="true" appendTo="body"
                placeholder="Currency" data-testid="fy-currency"></p-select>
    </div>
  </div>
  <div class="sf-mu sf-sm" *ngIf="!editingYear" style="margin-top:.5rem">Creates 12 monthly periods and a 13th period for year-end adjustments.</div>
  <div class="sf-mu sf-sm" *ngIf="datesLocked" style="margin-top:.5rem">The dates cannot change: the year has postings.</div>
  <div class="sf-sm" style="color: var(--sms-danger); margin-top:.5rem" *ngIf="shownProblem" data-testid="fy-problem">{{ shownProblem }}</div>
  <ng-template pTemplate="footer">
    <p-button label="Cancel" [text]="true" (onClick)="dialogVisible = false"></p-button>
    <p-button [label]="editingYear ? 'Save' : 'Create year'" icon="pi pi-check" [loading]="saving" (onClick)="save()" data-testid="fy-save"></p-button>
  </ng-template>
</p-dialog>`,
  styles: [`.gl-embedded { display: flex; flex-direction: column; gap: .75rem; }`]
})
export class GlFiscalYearsComponent implements OnInit {
  private readonly api = inject(AccountingService);
  private readonly auth = inject(AuthService);
  private readonly orgCurrency = inject(OrgCurrencyService);
  private readonly confirm = inject(ConfirmationService);
  private readonly messages = inject(MessageService, { optional: true });

  @Input() embedded = false;
  @Output() changed = new EventEmitter<void>();

  years: FiscalYear[] = [];
  loading = false;
  loadError = '';
  busyUuid: string | null = null;

  dialogVisible = false;
  editingYear: FiscalYear | null = null;
  draft: YearDraft = { fiscalYearName: '', startDate: null, endDate: null, reportingCurrencyCode: null };
  tried = false;
  saving = false;
  serverError = '';
  currencyOptions: string[] = [];
  baseCurrency: string | null = null;

  readonly tone = periodTone;

  get canManage(): boolean { return this.auth.hasPermission(GL_PERMS.SETUP_MANAGE); }
  get datesLocked(): boolean { return !!this.editingYear?.hasPostings; }
  /** A new year's name may be left blank: the server names it FY 2026 / FY 2026-27 (BE2). */
  get problem(): string | null { return yearProblem(this.draft, !this.editingYear); }
  get shownProblem(): string | null { return this.serverError || (this.tried ? this.problem : null); }

  ngOnInit(): void {
    this.load();
    // D-13 — the reporting currency defaults to the org's base currency.
    this.orgCurrency.getCurrencies(false).pipe(catchError(() => of(null))).subscribe(r => {
      this.currencyOptions = (r?.result ?? []).filter(c => c.isActive).map(c => c.code);
    });
    this.orgCurrency.getSettings().pipe(catchError(() => of(null))).subscribe(s => {
      this.baseCurrency = s?.result?.saleBaseCurrencyCode ?? null;
      if (this.baseCurrency && !this.currencyOptions.includes(this.baseCurrency)) this.currencyOptions = [this.baseCurrency, ...this.currencyOptions];
    });
  }

  load(): void {
    this.loading = true;
    this.loadError = '';
    this.api.getFiscalYears().subscribe({
      next: r => {
        this.loading = false;
        this.years = [...(r?.result ?? [])]
          .map(y => ({ ...y, periods: [...(y.periods ?? [])].sort((a, b) => a.periodNumber - b.periodNumber) }))
          .sort((a, b) => b.startDate.localeCompare(a.startDate));
      },
      error: err => { this.loading = false; this.loadError = glError(err, 'The fiscal years could not be loaded.'); }
    });
  }

  actionsFor(p: FiscalPeriod) { return periodActions(p.status); }

  openNew(): void {
    this.editingYear = null;
    const last = this.years[0];
    const start = last ? fromIsoDate(last.endDate) : null;
    if (start) start.setDate(start.getDate() + 1);
    this.draft = {
      fiscalYearName: '', startDate: start, endDate: start ? defaultYearEnd(start) : null,
      reportingCurrencyCode: this.baseCurrency ?? this.currencyOptions[0] ?? null
    };
    this.openDialog();
  }

  openEdit(y: FiscalYear): void {
    this.editingYear = y;
    this.draft = { fiscalYearName: y.fiscalYearName, startDate: fromIsoDate(y.startDate), endDate: fromIsoDate(y.endDate), reportingCurrencyCode: y.reportingCurrencyCode };
    if (y.reportingCurrencyCode && !this.currencyOptions.includes(y.reportingCurrencyCode)) this.currencyOptions = [...this.currencyOptions, y.reportingCurrencyCode];
    this.openDialog();
  }

  private openDialog(): void {
    this.tried = false;
    this.serverError = '';
    this.dialogVisible = true;
  }

  onStartChange(): void {
    if (!this.editingYear && this.draft.startDate && !this.draft.endDate) this.draft.endDate = defaultYearEnd(this.draft.startDate);
  }

  save(): void {
    this.tried = true;
    this.serverError = '';
    if (this.problem || this.saving) return;
    const d = this.draft;
    this.saving = true;
    const call = this.editingYear
      ? this.api.updateFiscalYear(this.editingYear.uuid, {
          fiscalYearName: d.fiscalYearName.trim(), reportingCurrencyCode: d.reportingCurrencyCode!,
          ...(this.datesLocked ? {} : { startDate: toIsoDate(d.startDate!), endDate: toIsoDate(d.endDate!) })
        })
      : this.api.createFiscalYear({ fiscalYearName: (d.fiscalYearName ?? '').trim(), startDate: toIsoDate(d.startDate!), endDate: toIsoDate(d.endDate!),
          reportingCurrencyCode: d.reportingCurrencyCode });
    call.subscribe({
      next: () => { this.saving = false; this.dialogVisible = false; this.toast('success', 'The fiscal year is saved.'); this.load(); this.changed.emit(); },
      error: err => { this.saving = false; this.serverError = glError(err, 'The fiscal year could not be saved.'); }
    });
  }

  confirmCloseYear(y: FiscalYear): void {
    this.confirm.confirm({
      key: 'gl-fy', header: 'Close fiscal year', icon: 'pi pi-lock', acceptButtonStyleClass: 'p-button-danger',
      message: `Close ${y.fiscalYearName}? All its periods must already be closed. A closed year cannot be reopened.`,
      acceptLabel: 'Close year', rejectLabel: 'Cancel',
      accept: () => this.api.closeFiscalYear(y.uuid).subscribe({
        next: () => { this.toast('success', `${y.fiscalYearName} is closed.`); this.load(); this.changed.emit(); },
        error: err => this.toast('error', glError(err, 'The year could not be closed.'))
      })
    });
  }

  confirmPeriod(p: FiscalPeriod, action: 'Lock' | 'Reopen' | 'Close'): void {
    const target: PeriodStatus = action === 'Lock' ? 'Locked' : action === 'Reopen' ? 'Open' : 'Closed';
    const text = action === 'Lock' ? `Lock ${p.periodName}? Nothing can be posted into it until it is reopened.`
      : action === 'Reopen' ? `Reopen ${p.periodName} for postings?`
      : `Close ${p.periodName}? This is final — a closed period cannot be reopened.`;
    this.confirm.confirm({
      key: 'gl-fy', header: `${action} period`, icon: action === 'Reopen' ? 'pi pi-lock-open' : 'pi pi-lock', message: text,
      acceptLabel: action, rejectLabel: 'Cancel', acceptButtonStyleClass: action === 'Close' ? 'p-button-danger' : undefined,
      accept: () => this.setPeriod(p, target)
    });
  }

  setPeriod(p: FiscalPeriod, status: PeriodStatus): void {
    this.busyUuid = p.uuid;
    this.api.updatePeriod(p.uuid, { status }).subscribe({
      next: r => {
        this.busyUuid = null;
        p.status = r?.result?.status ?? status;
        // D-51 — a period change can move its neighbour: reload the years (and their periods).
        this.load();
        this.changed.emit();
      },
      error: err => { this.busyUuid = null; this.toast('error', glError(err, 'The period could not be changed.')); }
    });
  }

  private toast(severity: 'success' | 'error', detail: string): void {
    this.messages?.add({ severity, summary: severity === 'success' ? 'Done' : 'Error', detail, life: severity === 'error' ? 8000 : 3000 });
  }
}
