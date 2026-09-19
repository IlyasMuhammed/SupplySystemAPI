import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TagModule } from 'primeng/tag';
import { TableModule, TableLazyLoadEvent } from 'primeng/table';
import { TooltipModule } from 'primeng/tooltip';
import { ToastModule } from 'primeng/toast';
import { DialogModule } from 'primeng/dialog';
import { InputTextModule } from 'primeng/inputtext';
import { InputNumberModule } from 'primeng/inputnumber';
import { TextareaModule } from 'primeng/textarea';
import { SelectModule } from 'primeng/select';
import { MessageService } from 'primeng/api';

import {
  LogisticsService,
  DeliveryExceptionModel,
  ExceptionSummaryModel,
  CarrierListItemModel
} from '../../../../services/logistics.service';

type Severity = 'success' | 'info' | 'warn' | 'danger' | 'secondary' | 'contrast';

type ExceptionAction = 'assign' | 'resolve' | 'withdraw';

/**
 * What has gone wrong with a movement, named and owned until it is settled.
 *
 * **Worst first, then oldest** — the order the server returns and the order this screen keeps. The
 * two figures that decide what gets worked are how critical it is and how long it has sat, so both
 * are on every row rather than behind a click.
 *
 * Resolving and withdrawing are deliberately separate buttons, not one "close" with a dropdown.
 * Counting a mistaken exception as one that was fixed would flatter every figure the carrier
 * scorecard produces.
 */
@Component({
  selector: 'app-exception-queue',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, TagModule, TableModule, TooltipModule, ToastModule, DialogModule,
    InputTextModule, InputNumberModule, TextareaModule, SelectModule
  ],
  templateUrl: './exception-queue.component.html',
  styleUrls: ['./exception-queue.component.scss'],
  providers: [MessageService]
})
export class ExceptionQueueComponent implements OnInit {
  summary: ExceptionSummaryModel | null = null;
  exceptions: DeliveryExceptionModel[] = [];
  carriers: CarrierListItemModel[] = [];

  total = 0;
  page = 1;
  pageSize = 20;

  isLoading = true;
  isSubmitting = false;

  filter = {
    carrierUuid:   null as string | null,
    exceptionType: null as string | null,
    severity:      null as string | null,
    status:        null as string | null,
    unassigned:    null as boolean | null
  };

  readonly typeOptions = [
    { label: 'Every kind',           value: null },
    { label: 'Address wrong',        value: 'ADDRESS_INVALID' },
    { label: 'Nobody reachable',     value: 'CONSIGNEE_UNREACHABLE' },
    { label: 'Refused',              value: 'REFUSED' },
    { label: 'Damaged',              value: 'DAMAGED' },
    { label: 'Held at customs',      value: 'CUSTOMS_HOLD' },
    { label: 'Lost',                 value: 'LOST' },
    { label: 'Running late',         value: 'DELAYED' },
    { label: 'Cash does not agree',  value: 'COD_MISMATCH' }
  ];

  readonly severityOptions = [
    { label: 'Any severity', value: null },
    { label: 'Critical',     value: 'CRITICAL' },
    { label: 'Normal',       value: 'NORMAL' },
    { label: 'Low',          value: 'LOW' }
  ];

  readonly statusOptions = [
    { label: 'Still needs work', value: null },
    { label: 'Open',             value: 'OPEN' },
    { label: 'Waiting on others', value: 'WAITING' },
    { label: 'Resolved',         value: 'RESOLVED' },
    { label: 'Withdrawn',        value: 'WITHDRAWN' }
  ];

  readonly ownerOptions = [
    { label: 'Owned or not', value: null },
    { label: 'Nobody owns it', value: true },
    { label: 'Somebody owns it', value: false }
  ];

  // ── Acting on one ───────────────────────────────────────────────────────────

  dialogVisible = false;
  action: ExceptionAction = 'assign';
  working: DeliveryExceptionModel | null = null;

  form = {
    assignToUserId: null as number | null,
    clearAssignee: false,
    severity: null as string | null,
    status: null as string | null,
    note: ''
  };

  readonly assignableSeverities = [
    { label: 'Critical', value: 'CRITICAL' },
    { label: 'Normal',   value: 'NORMAL' },
    { label: 'Low',      value: 'LOW' }
  ];

  readonly assignableStatuses = [
    { label: 'Open',              value: 'OPEN' },
    { label: 'Waiting on others', value: 'WAITING' }
  ];

  constructor(
    private logisticsService: LogisticsService,
    private messageService: MessageService
  ) {}

  ngOnInit() {
    this.loadSummary();
    this.load();
    this.loadCarriers();
  }

  private loadSummary() {
    this.logisticsService.getExceptionSummary().subscribe({
      next: (res) => this.summary = res.result ?? null,
      error: () => this.summary = null
    });
  }

  load(event?: TableLazyLoadEvent) {
    if (event) {
      this.pageSize = event.rows ?? this.pageSize;
      this.page = Math.floor((event.first ?? 0) / this.pageSize) + 1;
    }

    this.isLoading = true;

    this.logisticsService.getExceptions({
      carrierUuid:   this.filter.carrierUuid   ?? undefined,
      exceptionType: this.filter.exceptionType ?? undefined,
      severity:      this.filter.severity      ?? undefined,
      status:        this.filter.status        ?? undefined,
      unassigned:    this.filter.unassigned    ?? undefined,
      page:          this.page,
      pageSize:      this.pageSize
    }).subscribe({
      next: (res) => {
        this.isLoading = false;
        this.exceptions = res.result?.data ?? [];
        this.total      = res.result?.totalRecords ?? 0;
      },
      error: (err) => {
        this.isLoading = false;
        this.exceptions = [];
        this.fail(err, 'The queue could not be loaded.');
      }
    });
  }

  search() {
    this.page = 1;
    this.load();
  }

  private loadCarriers() {
    this.logisticsService.getActiveCarriers().subscribe({
      next: (res) => this.carriers = res.result ?? [],
      error: () => this.carriers = []
    });
  }

  get carrierOptions() {
    return [
      { label: 'Every carrier', value: null },
      ...this.carriers.map(c => ({ label: c.name, value: c.uuid }))
    ];
  }

  // ── Reading ─────────────────────────────────────────────────────────────────

  severityTag(severity: string): Severity {
    switch (severity) {
      case 'CRITICAL': return 'danger';
      case 'LOW':      return 'secondary';
      default:         return 'warn';
    }
  }

  statusTag(status: string): Severity {
    switch (status) {
      case 'RESOLVED':  return 'success';
      case 'WITHDRAWN': return 'secondary';
      case 'WAITING':   return 'info';
      default:          return 'warn';
    }
  }

  /** Said as what it means, not as a code. */
  typeLabel(type: string): string {
    return this.typeOptions.find(o => o.value === type)?.label ?? type;
  }

  statusLabel(status: string): string {
    switch (status) {
      case 'OPEN':      return 'Open';
      case 'WAITING':   return 'Waiting on others';
      case 'RESOLVED':  return 'Resolved';
      case 'WITHDRAWN': return 'Withdrawn';
      default:          return status;
    }
  }

  /** Where it came from. A carrier's account and ours carry different weight in a dispute. */
  sourceLabel(source: string): string {
    switch (source) {
      case 'CARRIER': return 'The carrier reported it';
      case 'SYSTEM':  return 'It went quiet and nothing was reported';
      case 'MANUAL':  return 'Raised here';
      default:        return source;
    }
  }

  /** Days where it has been more than a day — "73 hours" is a number nobody feels. */
  age(exception: DeliveryExceptionModel): string {
    const hours = exception.openForHours;

    if (hours < 1)  return 'under an hour';
    if (hours < 48) return `${Math.round(hours)} hours`;

    return `${Math.round(hours / 24)} days`;
  }

  /** Past two days without an owner, it is being ignored rather than worked. */
  isStale(exception: DeliveryExceptionModel): boolean {
    return this.isLive(exception) && exception.openForHours >= 48;
  }

  isLive(exception: DeliveryExceptionModel): boolean {
    return exception.status === 'OPEN' || exception.status === 'WAITING';
  }

  // ── The dialog ──────────────────────────────────────────────────────────────

  open(exception: DeliveryExceptionModel, action: ExceptionAction) {
    this.working = exception;
    this.action = action;
    this.form = {
      assignToUserId: exception.assignedToUserId ?? null,
      clearAssignee: false,
      severity: exception.severity,
      status: exception.status,
      note: ''
    };
    this.dialogVisible = true;
  }

  get dialogHeader(): string {
    switch (this.action) {
      case 'resolve':  return 'Say how it was settled';
      case 'withdraw': return 'Say why it should not have been raised';
      default:         return 'Who has it, and how much it matters';
    }
  }

  /** What the server will refuse, said before the round trip. */
  get validationError(): string | null {
    if (this.action === 'resolve' && !this.form.note.trim())
      return 'An exception that closes without a reason teaches nobody anything, '
           + 'and the next one will be worked from scratch.';

    if (this.action === 'withdraw' && !this.form.note.trim())
      return 'Say why this should not have been raised.';

    return null;
  }

  get canSave(): boolean {
    return !this.isSubmitting && !this.validationError;
  }

  confirm() {
    if (!this.canSave || !this.working) return;
    this.isSubmitting = true;

    const uuid = this.working.uuid;

    const done = (message: string) => {
      this.isSubmitting = false;
      this.dialogVisible = false;
      this.ok(message);
      this.loadSummary();
      this.load();
    };

    const failed = (err: any) => {
      this.isSubmitting = false;
      this.fail(err, 'That could not be recorded.');
    };

    if (this.action === 'resolve') {
      this.logisticsService.resolveException(uuid, this.form.note.trim())
        .subscribe({ next: () => done('Exception resolved.'), error: failed });
    } else if (this.action === 'withdraw') {
      this.logisticsService.withdrawException(uuid, this.form.note.trim())
        .subscribe({ next: () => done('Exception withdrawn.'), error: failed });
    } else {
      this.logisticsService.patchException(uuid, {
        assignToUserId: this.form.clearAssignee ? undefined : this.form.assignToUserId ?? undefined,
        clearAssignee:  this.form.clearAssignee,
        severity:       this.form.severity ?? undefined,
        status:         this.form.status   ?? undefined
      }).subscribe({ next: () => done('Exception updated.'), error: failed });
    }
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
