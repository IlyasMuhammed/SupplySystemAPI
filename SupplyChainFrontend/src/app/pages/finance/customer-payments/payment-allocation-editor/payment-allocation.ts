import { ManualPaymentAllocation } from '../../../../services/customer-payment.service';
import { SalesInvoiceListItemModel } from '../../../../services/sales-invoice.service';

/** How much of each invoice a payment is to pay, by invoice uuid. Empty or null means none. */
export type AllocationAmounts = Record<string, number | null>;

/** Money is kept to two decimal places, and adding cents in floating point is how a cent goes missing. */
export function roundMoney(n: number): number {
  return Math.round((n + Number.EPSILON) * 100) / 100;
}

/** Oldest invoice first, the order the server applies a payment in when it is left to decide. */
export function oldestFirst(invoices: SalesInvoiceListItemModel[]): SalesInvoiceListItemModel[] {
  return [...invoices].sort((a, b) => a.invoiceDate.localeCompare(b.invoiceDate) || a.invoiceNumber.localeCompare(b.invoiceNumber));
}

/** What paying the oldest invoices first would do with `available`: each paid down as far as the money goes. */
export function planOldestFirst(invoices: SalesInvoiceListItemModel[], available: number): AllocationAmounts {
  const plan: AllocationAmounts = {};
  let left = roundMoney(available);

  for (const invoice of oldestFirst(invoices)) {
    if (left <= 0) break;
    const applied = roundMoney(Math.min(left, invoice.balanceDue));
    if (applied <= 0) continue;
    plan[invoice.uuid] = applied;
    left = roundMoney(left - applied);
  }

  return plan;
}

/** What the amounts come to. Given the invoices, only amounts for those count: one for an invoice not on offer pays nothing. */
export function allocationTotal(amounts: AllocationAmounts, invoices?: SalesInvoiceListItemModel[]): number {
  const counted = invoices ? invoices.map(i => amounts[i.uuid]) : Object.values(amounts);
  return roundMoney(counted.reduce<number>((sum, a) => sum + (a ?? 0), 0));
}

/** The allocations to send: only invoices given more than nothing, in the order the invoices are listed. */
export function toAllocations(invoices: SalesInvoiceListItemModel[], amounts: AllocationAmounts): ManualPaymentAllocation[] {
  return invoices
    .filter(i => (amounts[i.uuid] ?? 0) > 0)
    .map(i => ({ invoiceUuid: i.uuid, amount: roundMoney(amounts[i.uuid]!) }));
}

/** The first thing wrong with a set of amounts, in words, or null if the server would take them. */
export function allocationProblem(
  invoices: SalesInvoiceListItemModel[], amounts: AllocationAmounts, available: number
): string | null {
  for (const invoice of invoices) {
    const amount = amounts[invoice.uuid] ?? 0;
    if (amount < 0) return `${invoice.invoiceNumber}: an amount cannot be negative.`;
    if (amount > invoice.balanceDue) {
      return `${invoice.invoiceNumber}: ${amount.toFixed(2)} is more than the ${invoice.balanceDue.toFixed(2)} still owing.`;
    }
  }

  const total = allocationTotal(amounts, invoices);
  if (total > roundMoney(available)) {
    return `The allocations come to ${total.toFixed(2)}, more than the ${roundMoney(available).toFixed(2)} available.`;
  }

  return null;
}
