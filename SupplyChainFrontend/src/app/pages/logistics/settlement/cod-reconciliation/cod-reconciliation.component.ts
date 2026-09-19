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
import { DatePickerModule } from 'primeng/datepicker';
import { MessageService } from 'primeng/api';

import {
  LogisticsService,
  CodCollectionModel,
  CodSummaryModel,
  CarrierListItemModel
} from '../../../../services/logistics.service';

type Severity = 'success' | 'info' | 'warn' | 'danger' | 'secondary' | 'contrast';

type CodAction = 'collected' | 'remitted' | 'write-off';

/**
 * Cash a carrier collected on delivery, and whether it came back.
 *
 * **This is a receivable, not a cost.** A freight accrual is what we owe a carrier; this is money
 * the carrier is holding that belongs to us — already taken from a customer and not yet passed on.
 * The screen says so, because the two are easy to confuse and they move in opposite directions.
 */
@Component({
  selector: 'app-cod-reconciliation',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, TagModule, TableModule, TooltipModule, ToastModule, DialogModule,
    InputTextModule, InputNumberModule, TextareaModule, SelectModule, DatePickerModule
  ],
  templateUrl: './cod-reconciliation.component.html',
  styleUrls: ['./cod-reconciliation.component.scss'],
  providers: [MessageService]
})
export class CodReconciliationComponent implements OnInit {
  summary: CodSummaryModel | null = null;
  records: CodCollectionModel[] = [];
  carriers: CarrierListItemModel[] = [];

  total = 0;
  page = 1;
  pageSize = 20;

  isLoading = true;
  isSubmitting = false;

  filter = { carrierUuid: null as string | null, status: null as string | null };

  readonly statusOptions = [
    { label: 'Still owed to us', value: null },
    { label: 'Expected',         value: 'EXPECTED' },
    { label: 'Carrier has it',   value: 'COLLECTED' },
    { label: 'Settled',          value: 'SETTLED' },
    { label: 'Written off',      value: 'WRITTEN_OFF' }
  ];

  // ── Recording what happened ─────────────────────────────────────────────────

  dialogVisible = false;
  action: CodAction = 'collected';
  working: CodCollectionModel | null = null;

  form = {
    amount: null as number | null,
    when: new Date() as Date | null,
    reference: '',
    note: ''
  };

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
    this.logisticsService.getCodSummary().subscribe({
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

    this.logisticsService.getCodList({
      carrierUuid: this.filter.carrierUuid ?? undefined,
      status:      this.filter.status ?? undefined,
      page:        this.page,
      pageSize:    this.pageSize
    }).subscribe({
      next: (res) => {
        this.isLoading = false;
        this.records = res.result?.data ?? [];
        this.total   = res.result?.totalRecords ?? 0;
      },
      error: (err) => {
        this.isLoading = false;
        this.records = [];
        this.fail(err, 'The outstanding cash could not be loaded.');
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

  statusSeverity(status: string): Severity {
    switch (status) {
      case 'SETTLED':     return 'success';
      case 'COLLECTED':   return 'warn';
      case 'WRITTEN_OFF': return 'secondary';
      default:            return 'info';
    }
  }

  /** Said as what it means, not as a code. COLLECTED is the state worth chasing. */
  statusLabel(status: string): string {
    switch (status) {
      case 'EXPECTED':    return 'Not yet collected';
      case 'COLLECTED':   return 'Carrier holds it';
      case 'SETTLED':     return 'Settled';
      case 'WRITTEN_OFF': return 'Written off';
      default:            return status;
    }
  }

  canAct(record: CodCollectionModel): boolean {
    return record.status !== 'SETTLED' && record.status !== 'WRITTEN_OFF';
  }

  // ── The dialogs ─────────────────────────────────────────────────────────────

  open(record: CodCollectionModel, action: CodAction) {
    this.working = record;
    this.action = action;
    this.form = {
      // Pre-filled with what is still outstanding: the common case is the whole of it, and a
      // figure somebody has to work out is a figure somebody gets wrong.
      amount: action === 'write-off' ? null : record.outstandingAmount,
      when: new Date(),
      reference: '',
      note: ''
    };
    this.dialogVisible = true;
  }

  get dialogHeader(): string {
    switch (this.action) {
      case 'remitted':  return 'Record money reaching us';
      case 'write-off': return 'Write off the shortfall';
      default:          return 'Record what the carrier collected';
    }
  }

  get canSave(): boolean {
    if (this.isSubmitting) return false;

    // A write-off needs only its reason; the other two need an amount as well.
    if (this.action === 'write-off') return !!this.form.note.trim();

    return (this.form.amount ?? 0) > 0;
  }

  /** What the server will refuse, said before the round trip. */
  get validationError(): string | null {
    if (!this.working) return null;

    const amount = this.form.amount ?? 0;

    if (this.action === 'write-off')
      return this.form.note.trim() ? null : 'Say why this cash is being written off.';

    if (amount <= 0) return 'An amount of nothing is not a collection.';

    if (this.action === 'collected' && amount > this.working.expectedAmount)
      return `This consignment collects ${this.working.expectedAmount.toFixed(2)}. `
           + 'Correct the consignment if the carrier really took more.';

    if (this.action === 'remitted' && amount > this.working.outstandingAmount)
      return `Only ${this.working.outstandingAmount.toFixed(2)} is outstanding. `
           + 'Split the remittance across the consignments it covers.';

    return null;
  }

  confirm() {
    if (!this.canSave || !this.working || this.validationError) return;
    this.isSubmitting = true;

    const consignment = this.working.consignmentUuid;

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

    if (this.action === 'collected') {
      this.logisticsService.recordCodCollection(consignment, {
        amount:      this.form.amount!,
        collectedAt: this.form.when?.toISOString(),
        reference:   this.form.reference.trim() || undefined
      }).subscribe({ next: () => done('Collection recorded.'), error: failed });
    } else if (this.action === 'remitted') {
      this.logisticsService.recordCodRemittance(consignment, {
        amount:     this.form.amount!,
        receivedAt: this.form.when?.toISOString(),
        reference:  this.form.reference.trim() || undefined,
        note:       this.form.note.trim() || undefined
      }).subscribe({ next: () => done('Remittance recorded.'), error: failed });
    } else {
      this.logisticsService.writeOffCod(consignment, this.form.note.trim())
        .subscribe({ next: () => done('Shortfall written off.'), error: failed });
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
