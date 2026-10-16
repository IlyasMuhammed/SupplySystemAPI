// A44 (docs/financial-posting) — pure helpers for the Sync control, Defaults, Accounts and Coverage tabs.
// No Angular, no I/O.

import {
  CoverageIssueModel, CoverageKindModel, CoverageModel, DocumentOverrideModel, ProviderDefaultModel,
  SYNC_CONTROL_DOCUMENT_TYPES, SyncControlDocumentType, SyncControlRowModel
} from '../../../models/quickbooks-integration.models';

/** What the option builders need of a reference item (ReferenceItemModel and QboAccountModel both fit). */
export interface RefLike {
  id: string;
  name: string;
  type?: string | null;
  active?: boolean | null;
  acctNum?: string | null;
  fullName?: string | null;
}
import { Option, QboTabKey } from './quickbooks.shared';

// ── Sync control (D-14) ──────────────────────────────────────────────────────────────────────────

export const SYNC_CONTROL_LABEL: Record<SyncControlDocumentType, string> = {
  Customer: 'Customers', Vendor: 'Vendors', Item: 'Items', SalesInvoice: 'Sales invoices', CreditNote: 'Credit notes',
  Payment: 'Customer payments', POSSale: 'POS sales', POSReturn: 'POS returns', PurchaseBill: 'Bills',
  BillPayment: 'Bill payments', VendorCredit: 'Vendor credits'
};

export const SYNC_CONTROL_HINT: Record<SyncControlDocumentType, string> = {
  Customer: 'When a customer is created or changed.',
  Vendor: 'When a vendor is created or changed.',
  Item: 'When a product or variant is changed.',
  SalesInvoice: 'When an invoice is issued, changed while issued, or cancelled.',
  CreditNote: 'Back-office credit notes (as credit memos or refund receipts).',
  Payment: 'Customer payments received, re-allocated, bounced or reversed.',
  POSSale: 'POS sales: a sales receipt when fully paid at the till, else an invoice and its payments.',
  POSReturn: 'POS returns: a refund receipt for a cash refund, else a credit memo.',
  PurchaseBill: 'When a supplier invoice is approved, or changed while approved.',
  BillPayment: 'Supplier payments when posted, cancelled or bounced.',
  VendorCredit: 'Supplier credit notes, and debit notes once issued.'
};

export type SyncControlStatus = 'On' | 'Held' | 'Off';

export function syncControlStatus(row: Pick<SyncControlRowModel, 'isEnabled' | 'holdWhenDisabled'>): SyncControlStatus {
  return row.isEnabled ? 'On' : row.holdWhenDisabled ? 'Held' : 'Off';
}

export const SYNC_CONTROL_STATUS_TONE: Record<SyncControlStatus, 'success' | 'warn' | 'secondary'> = {
  On: 'success', Held: 'warn', Off: 'secondary'
};

/** Every document type, in the fixed order, whether or not the server sent a row (no row = enabled, BR-29). */
export function completeSyncControl(rows: SyncControlRowModel[]): SyncControlRowModel[] {
  return SYNC_CONTROL_DOCUMENT_TYPES.map(type => {
    const found = rows.find(r => (r.documentType ?? '').toLowerCase() === type.toLowerCase());
    return found
      ? { ...found, documentType: type, heldCount: found.heldCount ?? 0 }
      : { documentType: type, isEnabled: true, holdWhenDisabled: false, heldCount: 0 };
  });
}

/** Turning a held type on releases what it held: the banner and the confirmation say how many. */
export function releaseCount(saved: SyncControlRowModel, draft: Pick<SyncControlRowModel, 'isEnabled'>): number {
  return !saved.isEnabled && draft.isEnabled ? (saved.heldCount ?? 0) : 0;
}

// ── Provider defaults (D-15) ─────────────────────────────────────────────────────────────────────

export const PAYMENT_METHODS = ['CASH', 'CHEQUE', 'BANK_TRANSFER', 'CARD', 'ONLINE', 'MOBILE'] as const;
export type PaymentMethodCode = typeof PAYMENT_METHODS[number];

export const PAYMENT_METHOD_LABEL: Record<PaymentMethodCode, string> = {
  CASH: 'Cash', CHEQUE: 'Cheque', BANK_TRANSFER: 'Bank transfer', CARD: 'Card', ONLINE: 'Online', MOBILE: 'Mobile wallet'
};

export const DEFAULT_KEYS = {
  ClassRef: 'ClassRef',
  DepartmentRef: 'DepartmentRef',
  SalesTermRef: 'SalesTermRef',
  PaymentMethodRef: 'PaymentMethodRef',
  DepositToAccountRef: 'DepositToAccountRef',
  BankAccountRef: 'BankAccountRef',
  RefundFromAccountRef: 'RefundFromAccountRef',
  PosDefaultCustomerRef: 'PosDefaultCustomerRef'
} as const;

/** "DepositToAccountRef.CASH" — the server's ProviderDefaultKeys.ForMethod. */
export function methodKey(key: string, method: string): string {
  return `${key}.${method.trim().toUpperCase()}`;
}

/** Saved values by key (keys compared without regard to case, blank values dropped). */
export function valuesByKey(rows: ProviderDefaultModel[]): Record<string, string | null> {
  const out: Record<string, string | null> = {};
  for (const r of rows ?? []) {
    if (!r?.settingKey) continue;
    const v = (r.settingValue ?? '').trim();
    out[canonicalKey(r.settingKey)] = v || null;
  }
  return out;
}

/** The key as the page holds it: base keys as written in DEFAULT_KEYS, the method part upper-case. */
export function canonicalKey(key: string): string {
  const [base, method] = key.trim().split('.');
  const known = Object.values(DEFAULT_KEYS).find(k => k.toLowerCase() === (base ?? '').toLowerCase()) ?? base;
  return method ? `${known}.${method.toUpperCase()}` : known;
}

/** The defaults to save: every key the page shows, blanks as null. */
export function defaultsRequest(keys: string[], values: Record<string, string | null>) {
  return keys.map(k => ({ settingKey: k, settingValue: (values[k] ?? '').toString().trim() || null }));
}

/** The keys a per-document-type override may set (D-15: class, location, terms). */
export const OVERRIDE_KEYS: string[] = [DEFAULT_KEYS.ClassRef, DEFAULT_KEYS.DepartmentRef, DEFAULT_KEYS.SalesTermRef];

export interface OverrideDraft {
  documentType: string;
  settingKey: string;
  settingValue: string | null;
}

export function overridesFrom(rows: DocumentOverrideModel[]): OverrideDraft[] {
  return (rows ?? [])
    .filter(r => r?.documentType && r?.settingKey)
    .map(r => ({ documentType: r.documentType, settingKey: canonicalKey(r.settingKey), settingValue: (r.settingValue ?? '').trim() || null }));
}

/** Rows with a document type, a key and a value; one per (type, key) — the last one wins. */
export function overridesRequest(rows: OverrideDraft[]) {
  const map = new Map<string, OverrideDraft>();
  for (const r of rows) {
    const v = (r.settingValue ?? '').trim();
    if (!r.documentType || !r.settingKey || !v) continue;
    map.set(`${r.documentType}|${r.settingKey}`, { ...r, settingValue: v });
  }
  return [...map.values()].map(r => ({ documentType: r.documentType, settingKey: r.settingKey, settingValue: r.settingValue }));
}

/** Active reference items as options, sorted; a saved id QuickBooks no longer lists keeps a label. */
export function refOptions(items: RefLike[] | null | undefined, keep?: (string | null | undefined)[]): Option[] {
  const name = (i: RefLike) => i.fullName || i.name;
  const list: Option[] = (items ?? [])
    .filter(i => i.active !== false)
    .sort((a, b) => name(a).localeCompare(name(b)))
    .map(i => {
      const base = i.acctNum ? `${i.acctNum} · ${name(i)}` : name(i);
      return { label: i.type ? `${base} (${i.type})` : base, value: i.id };
    });
  for (const id of keep ?? []) {
    if (id && !list.some(o => o.value === id)) {
      const found = (items ?? []).find(i => i.id === id);
      list.unshift({ label: found ? `${name(found)} (inactive)` : `QuickBooks id ${id} (not found in QuickBooks)`, value: id });
    }
  }
  return list;
}

/** The QuickBooks account types each money account default accepts (CTRL Details, D-15; the server refuses others). */
export type MoneyAccountPurpose = 'deposit' | 'bank' | 'refund';

export const MONEY_ACCOUNT_TYPES: Record<MoneyAccountPurpose, string[]> = {
  deposit: ['bank', 'other current asset'],
  bank: ['bank', 'credit card'],
  refund: ['bank', 'other current asset', 'credit card']
};

/** Accounts money can be deposited to (Bank, Undeposited Funds), paid from (Bank, Credit Card), or refunded from. */
export function bankAccountOptions(accounts: RefLike[] | null | undefined, keep?: (string | null | undefined)[],
                                   purpose: MoneyAccountPurpose = 'refund'): Option[] {
  const norm = (t: string | null | undefined) => (t ?? '').replace(/_/g, ' ').replace(/([a-z])([A-Z])/g, '$1 $2').trim().toLowerCase();
  const wanted = MONEY_ACCOUNT_TYPES[purpose];
  return refOptions((accounts ?? []).filter(a => wanted.includes(norm(a.type))), keep);
}

// ── Coverage (D-27) ──────────────────────────────────────────────────────────────────────────────

/** Which tab fixes a coverage area / issue. */
export function tabForArea(area: string | null | undefined): QboTabKey {
  const a = (area ?? '').toLowerCase().replace(/[\s_-]/g, '');
  if (['customer', 'customers', 'vendor', 'vendors', 'item', 'items', 'entity', 'entities'].includes(a)) return 'match';
  if (a.startsWith('tax')) return 'mappings';
  if (a.startsWith('setup')) return 'preflight';
  if (a.startsWith('term')) return 'mappings';
  if (a.startsWith('account')) return 'accounts';
  if (a.startsWith('providerdefault') || a.startsWith('default') || a.startsWith('deposit') || a.startsWith('bank') || a.startsWith('class') || a.startsWith('department')) return 'defaults';
  if (a.startsWith('synccontrol')) return 'sync-control';
  if (a.startsWith('connection')) return 'connection';
  return 'preflight';
}

export const TAB_LABEL: Partial<Record<QboTabKey, string>> = {
  connection: 'Connection', preflight: 'Preflight', mappings: 'Mappings', accounts: 'Accounts', defaults: 'Defaults',
  'sync-control': 'Sync control', match: 'Match existing', sync: 'Sync', coverage: 'Coverage'
};

/** 0–100, whole numbers; a kind with nothing to map counts as complete. */
export function coveragePercent(k: Pick<CoverageKindModel, 'total' | 'mapped' | 'skipped'>): number {
  const total = Number(k.total) || 0;
  if (total <= 0) return 100;
  const done = (Number(k.mapped) || 0) + (Number(k.skipped) || 0);
  return Math.max(0, Math.min(100, Math.round((done / total) * 100)));
}

export function coverageLabel(k: CoverageKindModel): string {
  if (k.label) return k.label;
  const map: Record<string, string> = {
    customer: 'Customers', vendor: 'Vendors', item: 'Items', taxcode: 'Tax codes', account: 'Accounts',
    providerdefault: 'Provider defaults', term: 'Payment terms'
  };
  const key = (k.kind ?? '').toLowerCase().replace(/[\s_-]/g, '').replace(/s$/, '');
  return map[key] ?? k.kind;
}

/** The server's `tab` (connection | setup | sync-control | defaults | accounts | tax | match) as a page tab. */
const SERVER_TAB: Record<string, QboTabKey> = {
  connection: 'connection', setup: 'preflight', preflight: 'preflight', 'sync-control': 'sync-control', defaults: 'defaults',
  accounts: 'accounts', tax: 'mappings', mappings: 'mappings', match: 'match', coverage: 'coverage', sync: 'sync'
};

export function issueTab(i: CoverageIssueModel): QboTabKey {
  const fromServer = SERVER_TAB[(i.tab ?? '').trim().toLowerCase()];
  return fromServer ?? tabForArea(i.area || i.code);
}

/**
 * The bars on the Coverage tab: the server's entity rows (customers, vendors, items), then tax codes, provider
 * defaults and — for information, when the general ledger is there — GL accounts.
 */
export function coverageRows(c: CoverageModel | null | undefined): CoverageKindModel[] {
  if (!c) return [];
  const rows: CoverageKindModel[] = [...(c.entities ?? [])];
  if (c.taxCodes) rows.push({ kind: 'TaxCode', total: c.taxCodes.total, mapped: c.taxCodes.mapped });
  if (c.providerDefaults) rows.push({ kind: 'ProviderDefault', total: c.providerDefaults.required, mapped: c.providerDefaults.set });
  if (c.accounts?.glAvailable) {
    rows.push({ kind: 'Account', total: c.accounts.total, mapped: c.accounts.mapped, informational: !c.accounts.blocking });
  }
  return rows;
}
