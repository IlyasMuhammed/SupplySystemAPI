import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TagModule } from 'primeng/tag';
import { TableModule } from 'primeng/table';
import { TooltipModule } from 'primeng/tooltip';
import { ToastModule } from 'primeng/toast';
import { DialogModule } from 'primeng/dialog';
import { TextareaModule } from 'primeng/textarea';
import { DatePickerModule } from 'primeng/datepicker';
import { MessageService } from 'primeng/api';

import {
  LogisticsService,
  FreightAccrualSummaryModel,
  UnaccruableConsignmentModel
} from '../../../../services/logistics.service';

/**
 * What is owed to carriers for movements that have already happened.
 *
 * **The balance is struck as at a date, not as at today.** An accrual released last week was still
 * a liability at month end, and a balance that forgets that cannot be reconciled to anything — so
 * the date is the first control on the screen rather than an afterthought.
 */
@Component({
  selector: 'app-freight-accruals',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, TagModule, TableModule, TooltipModule, ToastModule, DialogModule,
    TextareaModule, DatePickerModule
  ],
  templateUrl: './freight-accruals.component.html',
  styleUrls: ['./freight-accruals.component.scss'],
  providers: [MessageService]
})
export class FreightAccrualsComponent implements OnInit {
  summary: FreightAccrualSummaryModel | null = null;

  /** Null means today. Changing it restates the whole balance at that date. */
  asOf: Date | null = null;

  isLoading = true;
  isSubmitting = false;

  // ── Accruing what was missed ────────────────────────────────────────────────

  accrueDialogVisible = false;
  accruing: UnaccruableConsignmentModel | null = null;

  reverseDialogVisible = false;
  reverseConsignment: string | null = null;
  reverseReason = '';

  constructor(
    private logisticsService: LogisticsService,
    private messageService: MessageService
  ) {}

  ngOnInit() {
    this.load();
  }

  load() {
    this.isLoading = true;

    this.logisticsService.getAccrualSummary(this.asOf?.toISOString()).subscribe({
      next: (res) => {
        this.isLoading = false;
        this.summary = res.result ?? null;
      },
      error: (err) => {
        this.isLoading = false;
        this.summary = null;
        this.fail(err, 'The accrual balance could not be loaded.');
      }
    });
  }

  /** Back to today, which is the balance most people want most of the time. */
  clearDate() {
    this.asOf = null;
    this.load();
  }

  get isHistoric(): boolean {
    return !!this.asOf;
  }

  // ── Accruing one that was missed ────────────────────────────────────────────

  openAccrue(consignment: UnaccruableConsignmentModel) {
    this.accruing = consignment;
    this.accrueDialogVisible = true;
  }

  confirmAccrue() {
    if (!this.accruing || this.isSubmitting) return;
    this.isSubmitting = true;

    this.logisticsService.accrueConsignment(this.accruing.consignmentUuid).subscribe({
      next: () => {
        this.isSubmitting = false;
        this.accrueDialogVisible = false;
        this.ok('Accrued.');
        this.load();
      },
      error: (err) => {
        this.isSubmitting = false;
        // The usual refusal is that it was never priced, which is the whole reason it is on this
        // list — so the server's message says what to do about it.
        this.fail(err, 'It could not be accrued.');
      }
    });
  }

  // ── Writing one back ────────────────────────────────────────────────────────

  openReverse(consignmentUuid: string) {
    this.reverseConsignment = consignmentUuid;
    this.reverseReason = '';
    this.reverseDialogVisible = true;
  }

  get canReverse(): boolean {
    return !this.isSubmitting && !!this.reverseReason.trim();
  }

  confirmReverse() {
    if (!this.canReverse || !this.reverseConsignment) return;
    this.isSubmitting = true;

    this.logisticsService.reverseAccrual(this.reverseConsignment, this.reverseReason.trim()).subscribe({
      next: () => {
        this.isSubmitting = false;
        this.reverseDialogVisible = false;
        this.ok('Accrual written back.');
        this.load();
      },
      error: (err) => {
        this.isSubmitting = false;
        this.fail(err, 'The accrual could not be written back.');
      }
    });
  }

  private ok(detail: string) {
    this.messageService.add({ severity: 'success', summary: 'Done', detail });
  }

  private fail(err: any, fallback: string) {
    this.messageService.add({
      severity: 'error', summary: 'Not allowed',
      detail: err?.error?.message ?? fallback, life: 8000
    });
  }
}
