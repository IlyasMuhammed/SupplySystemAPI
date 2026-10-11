import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { DialogModule } from 'primeng/dialog';
import { TableModule } from 'primeng/table';
import { TextareaModule } from 'primeng/textarea';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';

import {
  POS_ROUTE, PosAdminService, PosCashMovement, PosCashierSession, PosShiftDetail, PosShiftSummary, posError, shiftVarianceExceeded
} from '../../../services/pos-admin.service';
import { AuthService } from '../../service/auth.service';
import { FLOW, FlowSection, FlowStage, flowLabel, flowStagesFrom, flowTone } from '../../../shared/flow';

/** Stages of a shift (contract §0); DISPUTED is the red last stage. */
export function shiftStages(status: string): FlowStage[] {
  switch (status) {
    case 'OPEN':       return flowStagesFrom(['Open', 'Closing', 'Closed', 'Reconciled'], 0);
    case 'CLOSING':    return flowStagesFrom(['Open', 'Closing', 'Closed', 'Reconciled'], 1);
    case 'CLOSED':     return flowStagesFrom(['Open', 'Closing', 'Closed', 'Reconciled'], 2);
    case 'DISPUTED':   return flowStagesFrom(['Open', 'Closing', 'Closed', 'Disputed'], 3, { failed: true });
    case 'RECONCILED': return flowStagesFrom(['Open', 'Closing', 'Closed', 'Reconciled'], 4);
    default:           return flowStagesFrom(['Open', 'Closing', 'Closed', 'Reconciled'], 0);
  }
}

/**
 * A38 FSD §17.7.3 — one shift: totals, by payment method, by cashier, cash movements (API-CONTRACT §4).
 * DISPUTED → "Reconcile" (note required); RECONCILED → "Add note" (append-only, POS-SHF-10). Both need POS_SHIFT_RECONCILE.
 */
@Component({
  selector: 'app-pos-shift-detail',
  standalone: true,
  imports: [CommonModule, RouterModule, FormsModule, ButtonModule, DialogModule, TableModule, TextareaModule, ToastModule, TooltipModule, ...FLOW],
  templateUrl: './shift-detail.component.html',
  styles: [`
    .shd-var { color: var(--sms-danger); }
    .shd-note { white-space: pre-line; }
    .shd-err { display: block; margin-bottom: .75rem; white-space: pre-line; }
  `],
  providers: [MessageService]
})
export class PosShiftDetailComponent implements OnInit {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly pos = inject(PosAdminService);
  private readonly auth = inject(AuthService);
  private readonly messages = inject(MessageService);

  readonly posRoute = POS_ROUTE;
  readonly tone = flowTone;
  readonly label = flowLabel;

  uuid = '';
  shift: PosShiftDetail | null = null;
  summary: PosShiftSummary | null = null;
  byCashier: PosCashierSession[] = [];
  movements: PosCashMovement[] = [];
  isLoading = true;
  loadError: string | null = null;

  stages: FlowStage[] = [];
  sections: FlowSection[] = [];
  varianceHigh = false;

  /** 'reconcile' (DISPUTED) or 'note' (RECONCILED). */
  noteMode: 'reconcile' | 'note' | null = null;
  noteText = '';
  noteBusy = false;
  noteError: string | null = null;

  get canReconcile(): boolean { return this.auth.hasPermission('POS_SHIFT_RECONCILE'); }
  get canViewOrders(): boolean { return this.auth.hasPermission('POS_ORDER_VIEW'); }
  get showReconcile(): boolean { return this.canReconcile && this.shift?.status === 'DISPUTED'; }
  /** POSA: a note may be appended on RECONCILED and DISPUTED shifts. */
  get showAddNote(): boolean { return this.canReconcile && (this.shift?.status === 'RECONCILED' || this.shift?.status === 'DISPUTED'); }

  ngOnInit(): void {
    this.uuid = this.route.snapshot.paramMap.get('uuid') ?? '';
    this.load();
  }

  load(): void {
    this.isLoading = true;
    this.loadError = null;
    this.pos.getShift(this.uuid).subscribe({
      next: res => {
        this.isLoading = false;
        const s = res.result ?? null;
        if (!s) { this.shift = null; this.loadError = res.message || 'Shift not found.'; return; }
        this.setShift(s);
        this.loadSummary();
      },
      error: err => {
        this.isLoading = false;
        this.shift = null;
        this.loadError = posError(err, err?.status === 404 ? 'Shift not found.' : 'Failed to load the shift.');
      }
    });
  }

  private setShift(s: PosShiftDetail): void {
    this.shift = s;
    this.movements = s.cashMovements ?? [];
    this.byCashier = s.sessions ?? [];
    this.stages = shiftStages(s.status);
    this.varianceHigh = shiftVarianceExceeded(s);
    this.sections = [
      { id: 'sec-methods', label: 'By payment method' },
      { id: 'sec-cashiers', label: 'By cashier', count: this.byCashier.length },
      { id: 'sec-movements', label: 'Cash movements', count: this.movements.length },
      { id: 'sec-details', label: 'Details' }
    ];
  }

  private loadSummary(): void {
    this.pos.getShiftSummary(this.uuid).subscribe({
      next: res => {
        this.summary = res.result ?? null;
        if (this.summary?.byCashier?.length) {
          this.byCashier = this.summary.byCashier;
          this.sections = this.sections.map(s => s.id === 'sec-cashiers' ? { ...s, count: this.byCashier.length } : s);
        }
      },
      error: () => { this.summary = null; }
    });
  }

  openNote(mode: 'reconcile' | 'note'): void {
    this.noteMode = mode;
    this.noteText = '';
    this.noteError = null;
  }

  closeNote(): void { this.noteMode = null; }

  /** The note is required for both actions. */
  get noteValid(): boolean { return !!this.noteText.trim(); }

  submitNote(): void {
    if (!this.noteMode || !this.noteValid || this.noteBusy) return;
    const note = this.noteText.trim();
    const reconcile = this.noteMode === 'reconcile';
    this.noteBusy = true;
    this.noteError = null;
    const call = reconcile ? this.pos.reconcileShift(this.uuid, note) : this.pos.appendShiftNote(this.uuid, note);
    call.subscribe({
      next: res => {
        this.noteBusy = false;
        if (res && res.success === false) { this.noteError = res.message || 'Not saved.'; return; }
        this.noteMode = null;
        this.messages.add({ severity: 'success', summary: 'Done', detail: reconcile ? 'Shift reconciled.' : 'Note added.' });
        this.load();
      },
      error: err => { this.noteBusy = false; this.noteError = posError(err, 'Not saved.'); }
    });
  }

  methodLabel(m: string): string { return flowLabel(m); }

  back(): void { this.router.navigate([POS_ROUTE, 'shifts']); }
}
