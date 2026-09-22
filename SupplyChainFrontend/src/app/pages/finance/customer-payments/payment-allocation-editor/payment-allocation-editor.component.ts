import { Component, EventEmitter, Input, OnChanges, Output, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TagModule } from 'primeng/tag';
import { InputNumberModule } from 'primeng/inputnumber';

import { SalesInvoiceListItemModel } from '../../../../services/sales-invoice.service';
import { formatCode } from '../../../../shared/format-code';
import { INVOICE_STATUS_SEVERITY, Severity } from '../../receivables/receivables.shared';
import {
  AllocationAmounts, allocationProblem, allocationTotal, oldestFirst, planOldestFirst, roundMoney
} from './payment-allocation';

/**
 * Where a payment goes: the customer's open invoices in the payment's currency, oldest first, each with a
 * box for how much of it this payment pays. The amounts belong to whoever uses the editor; it shows them,
 * lets the user change them, and says what is wrong with them.
 */
@Component({
  selector: 'app-payment-allocation-editor',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, TagModule, InputNumberModule],
  templateUrl: './payment-allocation-editor.component.html',
  styleUrls: ['./payment-allocation-editor.component.scss']
})
export class PaymentAllocationEditorComponent implements OnChanges {
  /** The invoices that can be paid: issued, owing, and in the payment's currency. */
  @Input() invoices: SalesInvoiceListItemModel[] = [];
  /** How much there is to apply. */
  @Input() available = 0;
  @Input() currencyCode = '';
  @Input() amounts: AllocationAmounts = {};
  @Output() amountsChange = new EventEmitter<AllocationAmounts>();

  rows: SalesInvoiceListItemModel[] = [];

  ngOnChanges(changes: SimpleChanges) {
    if (changes['invoices']) this.rows = oldestFirst(this.invoices);
  }

  get total(): number { return allocationTotal(this.amounts, this.invoices); }

  /** What is left of the money once these amounts are applied: it stays on the customer's account. */
  get remaining(): number { return roundMoney(this.available - this.total); }

  get problem(): string | null { return allocationProblem(this.invoices, this.amounts, this.available); }

  amountOf(invoice: SalesInvoiceListItemModel): number | null {
    return this.amounts[invoice.uuid] ?? null;
  }

  rowProblem(invoice: SalesInvoiceListItemModel): boolean {
    return (this.amounts[invoice.uuid] ?? 0) > invoice.balanceDue;
  }

  setAmount(invoice: SalesInvoiceListItemModel, value: number | null) {
    this.amountsChange.emit({ ...this.amounts, [invoice.uuid]: value });
  }

  /** As much of this invoice as the money left allows. */
  payInFull(invoice: SalesInvoiceListItemModel) {
    const own = this.amounts[invoice.uuid] ?? 0;
    const room = roundMoney(this.available - this.total + own);
    this.setAmount(invoice, Math.max(0, Math.min(invoice.balanceDue, room)));
  }

  fillOldestFirst() { this.amountsChange.emit(planOldestFirst(this.invoices, this.available)); }

  clear() { this.amountsChange.emit({}); }

  getStatusSeverity(status: string): Severity { return INVOICE_STATUS_SEVERITY[status] ?? 'secondary'; }

  formatStatus(code?: string | null): string { return formatCode(code); }
}
