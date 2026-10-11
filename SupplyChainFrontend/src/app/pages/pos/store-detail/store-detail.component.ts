import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { CalendarModule } from 'primeng/calendar';
import { CheckboxModule } from 'primeng/checkbox';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { DialogModule } from 'primeng/dialog';
import { DropdownModule } from 'primeng/dropdown';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { TableModule } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { TextareaModule } from 'primeng/textarea';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { ConfirmationService, MessageService } from 'primeng/api';
import { Observable } from 'rxjs';

import {
  POS_ROLE_OPTIONS, POS_ROUTE, PosAdminService, PosDailyTotals, PosRole, PosShift, PosStaffCandidate, PosStaffMember, PosStore,
  PosStoreUpsert, PosSyncType, PosTerminal, dailyTotals, isValidPin, posError, shiftVarianceExceeded, storeUpsertFrom
} from '../../../services/pos-admin.service';
import { FinanceSetupService } from '../../../services/finance-setup.service';
import { AuthService } from '../../service/auth.service';
import { FLOW, FlowSection, flowLabel, flowTone } from '../../../shared/flow';
import { StoreFormDialogComponent } from '../store-form-dialog/store-form-dialog.component';
import { PosStoreReturnsComponent } from '../store-returns/store-returns.component';
import { ymd } from '../../services/service-orders/service-order-shared';

/** Terminal row for the table — built in loadTerminals(), never in a getter. */
export interface TerminalRow extends PosTerminal {
  online: boolean;
  locked: boolean;
}

/** The online indicator: seen in the last 15 minutes (contract §2). */
export const ONLINE_WINDOW_MS = 15 * 60 * 1000;

export function terminalRow(t: PosTerminal, now = Date.now()): TerminalRow {
  const seen = t.lastSeenAt ? new Date(t.lastSeenAt).getTime() : NaN;
  const lockedUntil = t.pinLockedUntil ? new Date(t.pinLockedUntil).getTime() : NaN;
  return {
    ...t,
    online: !!t.isOnline || (!isNaN(seen) && now - seen < ONLINE_WINDOW_MS),
    locked: !isNaN(lockedUntil) && lockedUntil > now
  };
}

interface ShiftRow extends PosShift { varianceHigh: boolean; }

const TOTAL_KEYS: (keyof PosDailyTotals)[] = ['shifts', 'orders', 'refunds', 'cash', 'card', 'mobile', 'other', 'credit', 'cashRefunds', 'otherRefunds', 'gross', 'net', 'variance'];

/**
 * A38 FSD §17.7 — a POS store as an SMS Flow object page with tabs General, Configuration, Terminals, Staff, Shifts,
 * Reports (API-CONTRACT §2, §4, §8). Reports are per-day totals of this store's shifts in a date range (no endpoint of its own).
 */
@Component({
  selector: 'app-pos-store-detail',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule, ButtonModule, CalendarModule, CheckboxModule, ConfirmDialogModule, DialogModule,
    DropdownModule, InputNumberModule, InputTextModule, TableModule, TagModule, TextareaModule, ToastModule, TooltipModule,
    StoreFormDialogComponent, PosStoreReturnsComponent, ...FLOW
  ],
  templateUrl: './store-detail.component.html',
  styles: [`
    .sd-dot { display: inline-block; width: .55rem; height: .55rem; border-radius: 50%; background: var(--sms-text-faint); margin-right: .35rem; }
    .sd-dot.on { background: var(--sms-ok); }
    .sd-actions { display: flex; flex-wrap: wrap; gap: .15rem; justify-content: flex-end; }
    .sd-err { display: block; margin-bottom: .75rem; white-space: pre-line; }
    .sd-wide { grid-column: 1 / -1; }
    .sd-check { flex-direction: row; align-items: center; gap: .5rem; }
    .sd-var { color: var(--sms-danger); font-weight: 600; }
    .sd-total td { font-weight: 600; }
    :host ::ng-deep .w-full { width: 100%; }
  `],
  providers: [MessageService, ConfirmationService]
})
export class PosStoreDetailComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly pos = inject(PosAdminService);
  private readonly finance = inject(FinanceSetupService);
  private readonly auth = inject(AuthService);
  private readonly messages = inject(MessageService);
  private readonly confirm = inject(ConfirmationService);

  readonly posRoute = POS_ROUTE;
  readonly roleOptions = POS_ROLE_OPTIONS;
  readonly tone = flowTone;
  readonly label = flowLabel;
  readonly syncTypes: { label: string; value: PosSyncType }[] = [
    { label: 'Changes only (delta)', value: 'DELTA' }, { label: 'Everything (full)', value: 'FULL' }
  ];

  uuid = '';
  store: PosStore | null = null;
  isLoading = true;
  loadError: string | null = null;
  sections: FlowSection[] = [];
  address = '';
  statusBusy = false;
  showEdit = false;

  // Configuration
  config: PosStoreUpsert | null = null;
  taxOptions: { label: string; value: string }[] = [];
  configSaving = false;
  configError: string | null = null;

  // Terminals
  terminals: TerminalRow[] = [];
  terminalsLoading = false;
  terminalsFailed = false;
  terminalDialog = false;
  terminalEditing: TerminalRow | null = null;
  terminalModel = { code: '', name: '', isActive: true };
  terminalError: string | null = null;
  terminalBusy = false;
  syncDialog = false;
  syncTarget: TerminalRow | null = null;
  syncType: PosSyncType = 'DELTA';
  logoutDialog = false;
  logoutTarget: TerminalRow | null = null;
  logoutReason = '';

  // Staff
  staff: PosStaffMember[] = [];
  staffLoading = false;
  staffFailed = false;
  staffDialog = false;
  /** null = adding a new staff member. */
  staffEditing: PosStaffMember | null = null;
  candidates: { label: string; value: number }[] = [];
  candidateSearch = '';
  staffModel: { userId: number | null; posRole: PosRole; pin: string; pinConfirm: string } = { userId: null, posRole: 'CASHIER', pin: '', pinConfirm: '' };
  staffError: string | null = null;
  staffBusy = false;

  // Shifts
  shifts: ShiftRow[] = [];
  shiftsTotal = 0;
  shiftsLoading = false;
  shiftsFailed = false;

  // Reports
  reportRange: Date[] | null = null;
  reportRows: PosDailyTotals[] = [];
  reportTotal: PosDailyTotals | null = null;
  reportLoading = false;
  reportFailed = false;
  reportTruncated = false;

  get canManage(): boolean { return this.auth.hasPermission('POS_STORE_MANAGE'); }
  get canViewTerminals(): boolean { return this.auth.hasAnyPermission('POS_TERMINAL_VIEW', 'POS_TERMINAL_MANAGE'); }
  get canManageTerminals(): boolean { return this.auth.hasPermission('POS_TERMINAL_MANAGE'); }
  get canUnlock(): boolean { return this.auth.hasPermission('POS_SHIFT_OPEN'); }
  get canViewShifts(): boolean { return this.auth.hasPermission('POS_SHIFT_VIEW'); }
  /** A39 — store return policy writes. */
  get canManagePolicy(): boolean { return this.auth.hasPermission('POS_RETURN_POLICY_MANAGE'); }

  ngOnInit(): void {
    this.uuid = this.route.snapshot.paramMap.get('uuid') ?? '';
    const to = new Date();
    const from = new Date(to.getFullYear(), to.getMonth(), to.getDate() - 6);
    this.reportRange = [from, to];
    this.load();
  }

  load(): void {
    this.isLoading = true;
    this.loadError = null;
    this.pos.getStore(this.uuid).subscribe({
      next: res => {
        this.isLoading = false;
        this.store = res.result ?? null;
        if (!this.store) { this.loadError = res.message || 'Store not found.'; return; }
        this.setStore(this.store);
        this.buildSections();
        this.loadTaxCodes();
        this.loadTerminals();
        this.loadStaff();
        this.loadShifts();
        this.loadReport();
      },
      error: err => {
        this.isLoading = false;
        this.store = null;
        this.loadError = posError(err, err?.status === 404 ? 'Store not found.' : 'Failed to load the store.');
      }
    });
  }

  private setStore(s: PosStore): void {
    this.store = s;
    this.config = storeUpsertFrom(s);
    this.address = [s.addressLine1, s.addressLine2, s.city, s.stateProvince, s.postalCode, s.country].filter(Boolean).join(', ');
  }

  private buildSections(): void {
    this.sections = [
      { id: 'sec-general', label: 'General' },
      { id: 'sec-config', label: 'Configuration' },
      { id: 'sec-terminals', label: 'Terminals', count: this.canViewTerminals ? this.terminals.length : null },
      { id: 'sec-staff', label: 'Staff', count: this.staff.length },
      { id: 'sec-shifts', label: 'Shifts', count: this.canViewShifts ? this.shiftsTotal : null },
      { id: 'sec-reports', label: 'Reports' },
      { id: 'sec-returns', label: 'Returns' }
    ];
  }

  private loadTaxCodes(): void {
    if (this.taxOptions.length) return;
    this.finance.getTaxCodes('SALES').subscribe({
      next: res => {
        this.taxOptions = (res.result ?? [])
          .filter(t => t.usage === 'SALES' || t.usage === 'BOTH')
          .map(t => ({ label: `${t.code} — ${t.name} (${t.ratePercent}%)`, value: t.uuid }));
      },
      error: () => { this.taxOptions = []; }
    });
  }

  // ── Store status / general ────────────────────────────────────────────────
  onEdited(store: PosStore): void {
    this.messages.add({ severity: 'success', summary: 'Saved', detail: 'Store updated.' });
    if (store) this.setStore(store); else this.load();
  }

  toggleStatus(): void {
    if (!this.store || this.statusBusy) return;
    if (!this.store.isActive) { this.applyStatus(true); return; }
    this.confirm.confirm({
      header: 'Deactivate store?',
      message: `${this.store.name} and all of its terminals stop selling. Every shift must be closed first.`,
      icon: 'pi pi-exclamation-triangle',
      acceptButtonStyleClass: 'p-button-danger',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => this.applyStatus(false)
    });
  }

  applyStatus(isActive: boolean): void {
    this.statusBusy = true;
    this.pos.setStoreStatus(this.uuid, isActive).subscribe({
      next: res => {
        this.statusBusy = false;
        if (res && res.success === false) { this.fail(res.message); return; }
        this.messages.add({ severity: 'success', summary: 'Done', detail: isActive ? 'Store activated.' : 'Store deactivated.' });
        this.load();
      },
      // e.g. "Close all shifts before deactivating the store." — shown as it comes.
      error: err => { this.statusBusy = false; this.fail(posError(err, 'The status was not changed.')); }
    });
  }

  // ── Configuration ─────────────────────────────────────────────────────────
  saveConfig(): void {
    if (!this.config || !this.store || this.configSaving) return;
    this.configSaving = true;
    this.configError = null;
    const trim = (v: string | null | undefined) => (v ?? '').trim() || null;
    const req: PosStoreUpsert = {
      ...this.config,
      receiptHeader: trim(this.config.receiptHeader), receiptFooter: trim(this.config.receiptFooter),
      offlineCreditTolerancePct: this.config.offlineCreditTolerancePct ?? 0,
      autoCloseShiftHours: this.config.autoCloseShiftHours ?? 0,
      varianceThreshold: this.config.varianceThreshold ?? 0
    };
    this.pos.updateStore(this.uuid, req).subscribe({
      next: res => {
        this.configSaving = false;
        if (res && res.success === false) { this.configError = res.message || 'The configuration was not saved.'; return; }
        this.messages.add({ severity: 'success', summary: 'Saved', detail: 'Configuration saved.' });
        if (res.result) this.setStore(res.result);
      },
      error: err => {
        this.configSaving = false;
        this.configError = posError(err, err?.status === 409 ? 'Someone else changed this store. Reload and try again.' : 'The configuration was not saved.');
      }
    });
  }

  resetConfig(): void {
    if (this.store) this.config = storeUpsertFrom(this.store);
    this.configError = null;
  }

  // ── Terminals ─────────────────────────────────────────────────────────────
  loadTerminals(): void {
    if (!this.canViewTerminals) return;
    this.terminalsLoading = true;
    this.terminalsFailed = false;
    this.pos.getTerminals(this.uuid).subscribe({
      next: res => {
        this.terminalsLoading = false;
        const now = Date.now();
        this.terminals = (res.result ?? []).map(t => terminalRow(t, now));
        this.buildSections();
      },
      error: () => { this.terminalsLoading = false; this.terminalsFailed = true; }
    });
  }

  openTerminal(t: TerminalRow | null): void {
    this.terminalEditing = t;
    this.terminalModel = t ? { code: t.code, name: t.name, isActive: t.isActive } : { code: '', name: '', isActive: true };
    this.terminalError = null;
    this.terminalDialog = true;
  }

  get terminalCanSave(): boolean {
    return !this.terminalBusy && !!this.terminalModel.code.trim() && !!this.terminalModel.name.trim();
  }

  saveTerminal(): void {
    if (!this.terminalCanSave) return;
    this.terminalBusy = true;
    this.terminalError = null;
    const code = this.terminalModel.code.trim().toUpperCase();
    const name = this.terminalModel.name.trim();
    const call = this.terminalEditing
      ? this.pos.updateTerminal(this.terminalEditing.uuid, { code, name, isActive: this.terminalModel.isActive, rowVersion: this.terminalEditing.rowVersion })
      : this.pos.createTerminal(this.uuid, { code, name });
    call.subscribe({
      next: res => {
        this.terminalBusy = false;
        if (res && res.success === false) { this.terminalError = res.message || 'The terminal was not saved.'; return; }
        this.terminalDialog = false;
        this.messages.add({ severity: 'success', summary: 'Saved', detail: this.terminalEditing ? 'Terminal updated.' : 'Terminal created.' });
        this.loadTerminals();
      },
      error: err => { this.terminalBusy = false; this.terminalError = posError(err, 'The terminal was not saved.'); }
    });
  }

  unbind(t: TerminalRow): void {
    this.confirm.confirm({
      header: 'Unbind device?',
      message: `${t.name} is released from device ${t.deviceUuid || ''}. That device can no longer sell until it is bound again; its cached cashier PINs are wiped on the next sync.`,
      icon: 'pi pi-exclamation-triangle',
      acceptButtonStyleClass: 'p-button-danger',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => this.runTerminal(this.pos.unbindTerminal(t.uuid), 'Device unbound.')
    });
  }

  unlock(t: TerminalRow): void {
    this.runTerminal(this.pos.unlockTerminal(t.uuid), 'PIN lockout cleared.');
  }

  openForceSync(t: TerminalRow): void { this.syncTarget = t; this.syncType = 'DELTA'; this.syncDialog = true; }

  confirmForceSync(): void {
    if (!this.syncTarget) return;
    this.syncDialog = false;
    this.runTerminal(this.pos.forceSync(this.syncTarget.uuid, this.syncType), 'Sync requested — the terminal syncs when it is online.', false);
  }

  openForceLogout(t: TerminalRow): void { this.logoutTarget = t; this.logoutReason = ''; this.logoutDialog = true; }

  confirmForceLogout(): void {
    if (!this.logoutTarget || !this.logoutReason.trim()) return;
    this.logoutDialog = false;
    this.runTerminal(this.pos.forceLogout(this.logoutTarget.uuid, this.logoutReason.trim()), 'Logout sent to the terminal.', false);
  }

  private runTerminal(call: Observable<{ success: boolean; message: string }>, done: string, reload = true): void {
    this.terminalBusy = true;
    call.subscribe({
      next: res => {
        this.terminalBusy = false;
        if (res && res.success === false) { this.fail(res.message); return; }
        this.messages.add({ severity: 'success', summary: 'Done', detail: done });
        if (reload) this.loadTerminals();
      },
      error: err => { this.terminalBusy = false; this.fail(posError(err, 'The action failed.')); }
    });
  }

  // ── Staff ─────────────────────────────────────────────────────────────────
  loadStaff(): void {
    this.staffLoading = true;
    this.staffFailed = false;
    this.pos.getStaff(this.uuid).subscribe({
      next: res => { this.staffLoading = false; this.staff = res.result ?? []; this.buildSections(); },
      error: () => { this.staffLoading = false; this.staffFailed = true; }
    });
  }

  openStaff(member: PosStaffMember | null): void {
    this.staffEditing = member;
    this.staffModel = { userId: member?.userId ?? null, posRole: member?.posRole ?? 'CASHIER', pin: '', pinConfirm: '' };
    this.staffError = null;
    this.staffDialog = true;
    if (!member) this.searchCandidates();
  }

  searchCandidates(): void {
    this.pos.getStaffCandidates(this.uuid, this.candidateSearch.trim() || null).subscribe({
      next: res => {
        this.candidates = (res.result ?? []).map((c: PosStaffCandidate) => ({
          label: `${c.name} — ${c.email}${c.roleName ? ' (' + c.roleName + ')' : ''}`, value: c.userId
        }));
      },
      error: err => { this.candidates = []; this.staffError = posError(err, 'The users could not be loaded.'); }
    });
  }

  /** PIN rules: optional when the member keeps the current one; otherwise 4–6 digits, typed twice. */
  get pinError(): string | null {
    const { pin, pinConfirm } = this.staffModel;
    if (!pin && !pinConfirm) return null;
    if (!isValidPin(pin)) return 'PIN must be 4 to 6 digits.';
    if (pin !== pinConfirm) return 'The two PINs do not match.';
    return null;
  }

  get staffCanSave(): boolean {
    return !this.staffBusy && this.staffModel.userId != null && !this.pinError;
  }

  /** Digits only, at most 6. */
  digitsOnly(value: string): string { return (value ?? '').replace(/\D/g, '').slice(0, 6); }

  saveStaff(): void {
    if (!this.staffCanSave || this.staffModel.userId == null) return;
    this.staffBusy = true;
    this.staffError = null;
    this.pos.saveStaff(this.uuid, this.staffModel.userId, { posRole: this.staffModel.posRole, pin: this.staffModel.pin || null }).subscribe({
      next: res => {
        this.staffBusy = false;
        if (res && res.success === false) { this.staffError = res.message || 'Not saved.'; return; }
        this.staffDialog = false;
        this.messages.add({ severity: 'success', summary: 'Saved',
          detail: !this.staffEditing ? 'Staff member added.' : this.staffEditing.isActive ? 'Staff member updated.' : 'Staff member reactivated.' });
        this.loadStaff();
      },
      // e.g. "This PIN is already used by another staff member of this store." — verbatim.
      error: err => { this.staffBusy = false; this.staffError = posError(err, 'Not saved.'); }
    });
  }

  removeStaff(member: PosStaffMember): void {
    this.confirm.confirm({
      header: 'Remove from store?',
      message: `${member.name} can no longer sign in at this store's terminals, and any till session they have open here is ended. They stay listed as inactive and can be reactivated.`,
      icon: 'pi pi-exclamation-triangle',
      acceptButtonStyleClass: 'p-button-danger',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => {
        this.pos.removeStaff(this.uuid, member.userId).subscribe({
          next: res => {
            if (res && res.success === false) { this.fail(res.message); return; }
            this.messages.add({ severity: 'success', summary: 'Removed', detail: `${member.name} removed.` });
            this.loadStaff();
          },
          error: err => this.fail(posError(err, 'Not removed.'))
        });
      }
    });
  }

  roleLabel(role: string): string { return POS_ROLE_OPTIONS.find(r => r.value === role)?.label ?? role; }

  // ── Shifts ────────────────────────────────────────────────────────────────
  loadShifts(): void {
    if (!this.canViewShifts) return;
    this.shiftsLoading = true;
    this.shiftsFailed = false;
    this.pos.getShifts({ storeUuid: this.uuid, page: 1, pageSize: 25 }).subscribe({
      next: res => {
        this.shiftsLoading = false;
        this.shifts = (res.result?.data ?? []).map(s => ({ ...s, varianceHigh: shiftVarianceExceeded(s) }));
        this.shiftsTotal = res.result?.totalRecords ?? this.shifts.length;
        this.buildSections();
      },
      error: () => { this.shiftsLoading = false; this.shiftsFailed = true; }
    });
  }

  // ── Reports ───────────────────────────────────────────────────────────────
  onReportRange(): void {
    if (this.reportRange && this.reportRange.length === 2 && !this.reportRange[1]) return;
    this.loadReport();
  }

  loadReport(): void {
    if (!this.canViewShifts) return;
    const [from, to] = this.reportRange ?? [];
    this.reportLoading = true;
    this.reportFailed = false;
    const pageSize = 500;
    this.pos.getShifts({ storeUuid: this.uuid, from: ymd(from) ?? null, to: ymd(to ?? from) ?? null, page: 1, pageSize }).subscribe({
      next: res => {
        this.reportLoading = false;
        const rows = res.result?.data ?? [];
        this.reportTruncated = (res.result?.totalRecords ?? rows.length) > rows.length;
        this.reportRows = dailyTotals(rows);
        this.reportTotal = this.reportRows.length ? this.sumRows(this.reportRows) : null;
      },
      error: () => { this.reportLoading = false; this.reportFailed = true; this.reportRows = []; this.reportTotal = null; }
    });
  }

  private sumRows(rows: PosDailyTotals[]): PosDailyTotals {
    const total: any = { date: 'Total' };
    for (const k of TOTAL_KEYS) total[k] = Math.round(rows.reduce((s, r) => s + (r[k] as number), 0) * 100) / 100;
    return total as PosDailyTotals;
  }

  private fail(message?: string | null): void {
    this.messages.add({ severity: 'error', summary: 'Not done', detail: message || 'The action failed.', life: 8000 });
  }

  back(): void { this.router.navigate([POS_ROUTE, 'stores']); }
}
