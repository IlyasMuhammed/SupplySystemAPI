import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';
import { ToastModule } from 'primeng/toast';
import { DialogModule } from 'primeng/dialog';
import { SelectModule } from 'primeng/select';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { MessageService } from 'primeng/api';

import {
  LogisticsService,
  PickListModel,
  PickListLineModel,
  PICK_SHORT_REASONS
} from '../../../../services/logistics.service';
import { PICK_LIST_STATUS_SEVERITY } from '../pick-list-queue/pick-list-queue.component';

type Severity = 'success' | 'info' | 'warn' | 'danger' | 'secondary' | 'contrast';

/**
 * The walk: one instruction at a time, in the order the warehouse is laid out.
 *
 * Built around a single focused instruction rather than a table, because that is how the job is
 * actually done — a picker is standing at one bin, not reading a spreadsheet. The full list is
 * still there underneath, so a supervisor can see the whole walk and a picker can jump back to
 * correct a miscount.
 */
@Component({
  selector: 'app-pick-walk',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, TagModule, TooltipModule, ToastModule,
    DialogModule, SelectModule, InputNumberModule, InputTextModule, TextareaModule
  ],
  templateUrl: './pick-walk.component.html',
  styleUrls: ['./pick-walk.component.scss'],
  providers: [MessageService]
})
export class PickWalkComponent implements OnInit {
  uuid = '';
  pickList: PickListModel | null = null;
  isLoading = true;
  notFound = false;
  isSubmitting = false;

  /** The instruction on screen. Defaults to the first unanswered one. */
  current: PickListLineModel | null = null;

  qtyPicked: number | null = null;
  shortReasonCode: string | null = null;
  shortNote = '';

  shortReasons = PICK_SHORT_REASONS.map(r => ({ label: r.label, value: r.code }));

  cancelDialogVisible = false;
  cancelReason = '';

  constructor(
    private route: ActivatedRoute,
    private logisticsService: LogisticsService,
    private messageService: MessageService
  ) {}

  ngOnInit() {
    this.uuid = this.route.snapshot.paramMap.get('uuid') ?? '';
    this.load();
  }

  load() {
    if (!this.uuid) { this.isLoading = false; this.notFound = true; return; }

    this.isLoading = true;

    this.logisticsService.getPickListById(this.uuid).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success && res.result) {
          this.pickList = res.result;
          this.notFound = false;
          this.focusNext();
        } else {
          this.pickList = null;
          this.notFound = true;
        }
      },
      error: (err) => {
        this.isLoading = false;
        this.pickList = null;
        this.notFound = err?.status === 404;
        if (!this.notFound) this.fail(err, 'Failed to load the pick list.');
      }
    });
  }

  // ── Where the picker is ─────────────────────────────────────────────────────

  get lines(): PickListLineModel[] {
    return this.pickList?.lines ?? [];
  }

  get outstanding(): PickListLineModel[] {
    return this.lines.filter(l => !l.isConfirmed);
  }

  get confirmedCount(): number {
    return this.lines.filter(l => l.isConfirmed).length;
  }

  get isComplete(): boolean {
    return this.pickList?.status === 'COMPLETED';
  }

  get isLive(): boolean {
    return this.pickList?.status === 'OPEN' || this.pickList?.status === 'IN_PROGRESS';
  }

  get progressPercent(): number {
    if (!this.lines.length) return 0;
    return Math.round((this.confirmedCount / this.lines.length) * 100);
  }

  /** Moves to the first unanswered instruction, or clears the focus when there are none left. */
  private focusNext() {
    this.focus(this.outstanding[0] ?? null);
  }

  focus(line: PickListLineModel | null) {
    this.current = line;
    // Pre-filled with what the instruction asks for: taking exactly what was asked is the
    // overwhelmingly common case, and typing it again every time is friction for no gain.
    this.qtyPicked = line ? line.qtyToPick : null;
    this.shortReasonCode = null;
    this.shortNote = '';
  }

  // ── Confirming ──────────────────────────────────────────────────────────────

  get shortfall(): number {
    if (!this.current) return 0;
    return Math.max(0, this.current.qtyToPick - (this.qtyPicked ?? 0));
  }

  get isShort(): boolean {
    return this.shortfall > 0;
  }

  /** More than the instruction is not a short pick, it is somebody else's stock. */
  get isOverPicked(): boolean {
    return !!this.current && (this.qtyPicked ?? 0) > this.current.qtyToPick;
  }

  get canConfirm(): boolean {
    if (!this.current || this.isSubmitting || !this.isLive) return false;
    if (this.qtyPicked == null || this.qtyPicked < 0) return false;
    if (this.isOverPicked) return false;
    // The server requires a reason for anything short, and finding that out through a 400 after
    // the picker has walked away is a poor way to learn it.
    return !this.isShort || !!this.shortReasonCode;
  }

  confirm() {
    if (!this.canConfirm || !this.current) return;

    this.isSubmitting = true;

    this.logisticsService.confirmPick(this.uuid, {
      lines: [{
        lineUuid: this.current.uuid,
        qtyPicked: this.qtyPicked as number,
        shortReasonCode: this.isShort ? this.shortReasonCode ?? undefined : undefined,
        shortNote: this.isShort ? (this.shortNote.trim() || undefined) : undefined
      }]
    }).subscribe({
      next: (res) => {
        this.isSubmitting = false;
        const result = res.result;

        if (result?.completed) {
          this.messageService.add({
            severity: 'success',
            summary: 'Picking complete',
            detail: result.qtyReturnedToStock > 0
              // The returned quantity is the consequence a picker should see: those units stopped
              // being promised to this delivery the moment the walk closed.
              ? `${result.qtyPicked} picked. ${result.qtyReturnedToStock} was not found and is back in stock.`
              : `${result.qtyPicked} picked. Nothing short.`,
            life: 6000
          });
        } else {
          this.messageService.add({
            severity: 'success', summary: 'Confirmed',
            detail: `${result?.linesOutstanding ?? 0} left to pick.`
          });
        }

        // Reload rather than patching: the server decides whether that was the last line, and
        // guessing is how the screen and the pick list drift apart.
        this.load();
      },
      error: (err) => {
        this.isSubmitting = false;
        this.fail(err, 'The pick could not be confirmed.');
      }
    });
  }

  /** Answering a line already answered — a miscount corrected before the walk closes. */
  reopen(line: PickListLineModel) {
    if (!this.isLive) return;
    this.focus(line);
  }

  // ── Abandoning the walk ─────────────────────────────────────────────────────

  confirmCancel() {
    if (!this.cancelReason.trim() || this.isSubmitting) return;

    this.isSubmitting = true;

    this.logisticsService.cancelPickList(this.uuid, { reason: this.cancelReason.trim() })
      .subscribe({
        next: () => {
          this.isSubmitting = false;
          this.cancelDialogVisible = false;
          this.messageService.add({
            severity: 'success', summary: 'Cancelled',
            detail: 'Pick list cancelled. The stock stays reserved for the delivery.'
          });
          this.load();
        },
        error: (err) => {
          this.isSubmitting = false;
          this.fail(err, 'The pick list could not be cancelled.');
        }
      });
  }

  // ── Display ─────────────────────────────────────────────────────────────────

  getStatusSeverity(status: string): Severity {
    return PICK_LIST_STATUS_SEVERITY[status] ?? 'secondary';
  }

  formatStatus(status?: string): string {
    if (!status) return '';
    return status.split('_')
      .map(word => word.charAt(0) + word.slice(1).toLowerCase())
      .join(' ');
  }

  /** Where to walk, as one readable string. Stock not yet put away has no bin. */
  location(line: PickListLineModel): string {
    const parts = [line.zoneName, line.binCode].filter(Boolean);
    return parts.length ? parts.join(' · ') : 'Not put away';
  }

  reasonLabel(code?: string): string {
    return PICK_SHORT_REASONS.find(r => r.code === code)?.label ?? code ?? '';
  }

  private fail(err: any, fallback: string) {
    this.messageService.add({
      severity: 'error',
      summary: 'Not allowed',
      detail: err?.error?.message ?? fallback
    });
  }
}
