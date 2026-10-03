import { Component, EventEmitter, Input, OnChanges, Output, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { DropdownModule } from 'primeng/dropdown';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { CheckboxModule } from 'primeng/checkbox';
import { DialogModule } from 'primeng/dialog';
import { TooltipModule } from 'primeng/tooltip';
import { Observable, concat, defer, of } from 'rxjs';
import { catchError, last, map, switchMap } from 'rxjs/operators';

import {
  SalesPreorderService, SaleQuotation, SaleQuotationLine, CustomerResponse, RecordCustomerResponseRequest
} from '../../../../services/sales-preorder.service';
import { AuthService } from '../../../service/auth.service';
import { quotationLineRows } from '../sale-quotation.shared';

/** One line the customer answers for, with the answer being recorded. */
export interface ResponseRow {
  line: SaleQuotationLine;
  /** As in the lines table: "3a" for an alternative. */
  label: string;
  /** Null while the line is still PENDING and nothing has been chosen. */
  response: CustomerResponse | null;
  counterPrice: number | null;
  notes: string;
  /** On a COUNTER line now ACCEPTED: take the customer's price as the line's (§4.5). */
  acceptCounterPrice: boolean;
}

/** A response that failed to save, and on which line. */
class LineRefused extends Error {
  constructor(readonly label: string, readonly reason: string) { super(reason); }
}

/**
 * A32-PC-13 — records the customer's answer to a SENT quotation, line by line (§10.4), and marks the
 * quotation accepted (≥ 1 line ACCEPTED, BR-C2-07) or rejected (every offered line REJECTED, BR-C2-08).
 * The seller's own REJECTED lines are not answered for.
 */
@Component({
  selector: 'app-sale-quotation-response-panel',
  standalone: true,
  imports: [
    CommonModule, FormsModule, ButtonModule, DropdownModule, InputNumberModule, InputTextModule, TextareaModule,
    CheckboxModule, DialogModule, TooltipModule
  ],
  templateUrl: './sale-quotation-response-panel.component.html',
  styleUrls: ['./sale-quotation-response-panel.component.scss']
})
export class SaleQuotationResponsePanelComponent implements OnChanges {
  @Input({ required: true }) quotation!: SaleQuotation;
  /** Something was recorded: the page reloads the quotation. */
  @Output() changed = new EventEmitter<void>();

  rows: ResponseRow[] = [];
  readonly responseOptions: { label: string; value: CustomerResponse }[] = [
    { label: 'Accepted', value: 'ACCEPTED' },
    { label: 'Rejected', value: 'REJECTED' },
    { label: 'Counter',  value: 'COUNTER' }
  ];

  isBusy = false;
  error: string | null = null;

  rejectDialogVisible = false;
  rejectReason = '';

  constructor(
    private salesPreorderService: SalesPreorderService,
    private authService: AuthService
  ) {}

  ngOnChanges(changes: SimpleChanges) {
    if (changes['quotation'] && this.quotation) this.resetRows();
  }

  private resetRows() {
    this.error = null;
    this.rows = quotationLineRows(this.quotation.lines)
      .filter(r => r.line.lineType !== 'REJECTED')
      .map(r => ({
        line: r.line,
        label: r.label,
        response: r.line.customerResponse === 'PENDING' ? null : r.line.customerResponse,
        counterPrice: r.line.customerCounterPrice ?? null,
        notes: r.line.customerResponseNotes ?? '',
        acceptCounterPrice: false
      }));
  }

  private allows(action: 'RECORD_RESPONSE' | 'ACCEPT' | 'REJECT'): boolean {
    return this.quotation?.status === 'SENT' && (this.quotation.allowedActions ?? []).includes(action);
  }

  /** Responses are recorded by someone who may edit quotations, on a SENT one. */
  get canRespond(): boolean {
    return this.allows('RECORD_RESPONSE') && this.authService.hasPermission('SALE_QUOTATION_EDIT');
  }

  get showAccept(): boolean { return this.canRespond && this.allows('ACCEPT'); }
  get showReject(): boolean { return this.canRespond && this.allows('REJECT'); }

  // ── Rows ────────────────────────────────────────────────────────────────────

  /** A row whose answer differs from what is recorded. */
  isDirty(row: ResponseRow): boolean {
    const l = row.line;
    if (row.response === null) return false;   // nothing chosen: nothing to send
    if (row.response !== l.customerResponse) return true;
    if (row.acceptCounterPrice) return true;
    if ((row.notes ?? '').trim() !== (l.customerResponseNotes ?? '').trim()) return true;
    return row.response === 'COUNTER' && row.counterPrice !== (l.customerCounterPrice ?? null);
  }

  get dirtyRows(): ResponseRow[] { return this.rows.filter(r => this.isDirty(r)); }

  /** BR-C2-09: a counter needs the customer's price, above zero. */
  rowError(row: ResponseRow): string | null {
    if (row.response === 'COUNTER' && !(row.counterPrice != null && row.counterPrice > 0)) {
      return 'Enter the customer\'s counter price (above zero).';
    }
    return null;
  }

  /** A line the customer countered, now being accepted: the seller may take the customer's price. */
  offersCounterAcceptance(row: ResponseRow): boolean {
    return row.response === 'ACCEPTED' && row.line.customerResponse === 'COUNTER' && (row.line.customerCounterPrice ?? 0) > 0;
  }

  onResponseChange(row: ResponseRow) {
    this.error = null;
    if (row.response !== 'ACCEPTED') row.acceptCounterPrice = false;
  }

  get canSave(): boolean {
    return this.canRespond && !this.isBusy && this.dirtyRows.length > 0 && this.dirtyRows.every(r => !this.rowError(r));
  }

  /** BR-C2-07, on the answers as they now stand (unsaved ones are saved first). */
  get canMarkAccepted(): boolean {
    return this.rows.some(r => r.response === 'ACCEPTED') && this.rows.every(r => !this.rowError(r));
  }

  /** BR-C2-08: every line the seller offered is rejected by the customer. */
  get canMarkRejected(): boolean {
    return this.rows.length > 0 && this.rows.every(r => r.response === 'REJECTED');
  }

  // ── Saving ──────────────────────────────────────────────────────────────────

  private request(row: ResponseRow): RecordCustomerResponseRequest {
    const notes = row.notes?.trim() || null;
    return {
      response: row.response!,
      notes,
      counterPrice: row.response === 'COUNTER' ? row.counterPrice : null,
      ...(this.offersCounterAcceptance(row) && row.acceptCounterPrice ? { acceptCounterPrice: true } : {})
    };
  }

  /** The changed answers, one after another (each changes the same quotation), stopping at the first refusal. */
  private saveDirty(): Observable<void> {
    const rows = this.dirtyRows;
    if (rows.length === 0) return of(undefined);
    const uuid = this.quotation.uuid;
    return concat(...rows.map(row => defer(() =>
      this.salesPreorderService.recordCustomerResponse(uuid, row.line.uuid, this.request(row))).pipe(
        catchError(err => { throw new LineRefused(row.label, err?.error?.message ?? 'The response could not be saved.'); })
      )
    )).pipe(last(), map(() => undefined));
  }

  private run(work: Observable<unknown>) {
    this.isBusy = true;
    this.error = null;
    work.subscribe({
      next: () => { /* one value at the end */ },
      complete: () => { this.isBusy = false; this.changed.emit(); },
      error: (err) => {
        this.isBusy = false;
        this.error = err instanceof LineRefused
          ? `Line ${err.label}: ${err.reason}`
          : err?.error?.message ?? 'The quotation could not be updated.';
      }
    });
  }

  saveResponses() {
    if (!this.canSave) {
      const invalid = this.dirtyRows.find(r => this.rowError(r));
      if (invalid && this.canRespond) this.error = `Line ${invalid.label}: ${this.rowError(invalid)}`;
      return;
    }
    this.run(this.saveDirty());
  }

  markAccepted() {
    if (!this.showAccept || !this.canMarkAccepted || this.isBusy) return;
    const uuid = this.quotation.uuid;
    this.run(this.saveDirty().pipe(switchMap(() => this.salesPreorderService.acceptQuotation(uuid))));
  }

  openReject() {
    if (!this.showReject || !this.canMarkRejected) return;
    this.rejectReason = '';
    this.rejectDialogVisible = true;
  }

  confirmReject() {
    if (!this.showReject || !this.canMarkRejected || this.isBusy) return;
    const uuid = this.quotation.uuid;
    const reason = this.rejectReason.trim() || undefined;
    this.rejectDialogVisible = false;
    this.run(this.saveDirty().pipe(switchMap(() => this.salesPreorderService.rejectQuotation(uuid, reason))));
  }
}
