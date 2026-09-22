// What the receivables screens (sales invoices, customer payments, customer ledger) share.

export type Severity = 'success' | 'info' | 'warn' | 'danger' | 'secondary' | 'contrast';

export const INVOICE_STATUS_SEVERITY: Record<string, Severity> = {
  DRAFT:          'secondary',
  ISSUED:         'info',
  PARTIALLY_PAID: 'warn',
  PAID:           'success',
  OVERDUE:        'danger',
  CANCELLED:      'danger',
  CREDIT_NOTE:    'secondary'
};

export const INVOICE_STATUS_OPTIONS = [
  { label: 'All Statuses',   value: '' },
  { label: 'Draft',          value: 'DRAFT' },
  { label: 'Issued',         value: 'ISSUED' },
  { label: 'Partially Paid', value: 'PARTIALLY_PAID' },
  { label: 'Paid',           value: 'PAID' },
  { label: 'Overdue',        value: 'OVERDUE' },
  { label: 'Cancelled',      value: 'CANCELLED' },
  { label: 'Credit Note',    value: 'CREDIT_NOTE' }
];

/** A payment that stands is green; one the bank returned, or that was undone, is not. */
export const PAYMENT_STATUS_SEVERITY: Record<string, Severity> = {
  RECEIVED: 'success',
  BOUNCED:  'danger',
  REVERSED: 'danger'
};

export const PAYMENT_STATUS_OPTIONS = [
  { label: 'All Statuses', value: '' },
  { label: 'Received',     value: 'RECEIVED' },
  { label: 'Bounced',      value: 'BOUNCED' },
  { label: 'Reversed',     value: 'REVERSED' }
];

/** What kind of movement a customer ledger entry is: money owed is blue, money in is green. */
export const LEDGER_ENTRY_SEVERITY: Record<string, Severity> = {
  INVOICE:     'info',
  DEBIT_NOTE:  'info',
  REFUND:      'info',
  OPENING_BAL: 'secondary',
  PAYMENT:     'success',
  CREDIT_NOTE: 'success',
  ADVANCE:     'success'
};
