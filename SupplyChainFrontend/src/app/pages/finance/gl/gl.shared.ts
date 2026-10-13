import {
  Account, AccountTreeNode, AccountType, OpeningRow, PeriodStatus, RootType, SetupStatus, SetupStatusCode, SyncDirection, SystemType, UploadError
} from '../../../services/accounting.service';
import { setupErrorMessage } from '../../finance-setup/finance-setup.shared';

// A42 — what the General Ledger pages share: codes, the rules the server enforces (checked here first), tree flattening,
// opening-balance arithmetic and the wizard's help text. Server: SMS.Modules.Accounting — keep the two in step.

// ── Codes & labels ────────────────────────────────────────────────────────────

/** D-8 — Level 1 codes are fixed and every descendant starts with its root's digit. */
export const ROOT_DIGIT: Record<RootType, string> = { Asset: '1', Liability: '2', Equity: '3', Revenue: '4', Expense: '5' };

/** D-9 — the opening-balance plug account. */
export const OPENING_BALANCE_EQUITY_CODE = '3900';

/** Appendix A / D-23 — the account types a leaf may have under each root type (Tax on Asset and Liability). */
export const ACCOUNT_TYPES_BY_ROOT: Record<RootType, AccountType[]> = {
  Asset: ['Cash', 'Bank', 'Receivable', 'OtherCurrentAsset', 'FixedAsset', 'AccumulatedDepreciation', 'Tax'],
  Liability: ['Payable', 'OtherCurrentLiability', 'LongTermLiability', 'Tax'],
  Equity: ['Equity'],
  Revenue: ['Revenue', 'OtherIncome'],
  Expense: ['CostOfGoodsSold', 'Expense', 'OtherExpense', 'Depreciation']
};

const TYPE_LABELS: Partial<Record<AccountType, string>> = {
  OtherCurrentAsset: 'Other current asset', FixedAsset: 'Fixed asset', AccumulatedDepreciation: 'Accumulated depreciation',
  OtherCurrentLiability: 'Other current liability', LongTermLiability: 'Long-term liability', OtherIncome: 'Other income',
  CostOfGoodsSold: 'Cost of goods sold', OtherExpense: 'Other expense'
};

export function accountTypeLabel(t: AccountType | string | null | undefined): string {
  if (!t) return '';
  return TYPE_LABELS[t as AccountType] ?? t;
}

export function accountTypeOptions(root: RootType | null | undefined): { label: string; value: AccountType }[] {
  return (root ? ACCOUNT_TYPES_BY_ROOT[root] : []).map(t => ({ label: accountTypeLabel(t), value: t }));
}

/** Debit-normal roots show their balance as is; credit-normal ones are shown positive too (sign = side). */
export const DEBIT_NORMAL: RootType[] = ['Asset', 'Expense'];

export const SETUP_STATUS_ORDER: SetupStatusCode[] = ['NotStarted', 'AccountsCreated', 'FiscalYearSet', 'DefaultsSet', 'BalancesEntered', 'Complete'];

export function setupStatusLabel(s: SetupStatusCode | string | null | undefined): string {
  switch (s) {
    case 'NotStarted': return 'Not started';
    case 'AccountsCreated': return 'Accounts created';
    case 'FiscalYearSet': return 'Fiscal year set';
    case 'DefaultsSet': return 'Defaults set';
    case 'BalancesEntered': return 'Balances entered';
    case 'Complete': return 'Complete';
    default: return s ?? '';
  }
}

export function setupStatusTone(s: SetupStatusCode | string | null | undefined): string {
  return s === 'Complete' ? 'ok' : s === 'NotStarted' ? '' : 'in';
}

export function periodTone(s: PeriodStatus | string): string {
  return s === 'Open' ? 'ok' : s === 'Locked' ? 'wn' : 'er';
}

export const SYSTEM_TYPES: { value: SystemType; label: string }[] = [
  { value: 'QUICKBOOKS_ONLINE', label: 'QuickBooks Online' }, { value: 'QUICKBOOKS_DESKTOP', label: 'QuickBooks Desktop' },
  { value: 'XERO', label: 'Xero' }, { value: 'ERPNEXT', label: 'ERPNext' }, { value: 'TALLY', label: 'Tally' },
  { value: 'SAGE', label: 'Sage' }, { value: 'SAP_B1', label: 'SAP Business One' }, { value: 'DYNAMICS_BC', label: 'Dynamics 365 Business Central' },
  { value: 'ZOHO_BOOKS', label: 'Zoho Books' }, { value: 'WAVE', label: 'Wave' }, { value: 'ODOO', label: 'Odoo' }, { value: 'CUSTOM', label: 'Custom' }
];

export function systemTypeLabel(t: string | null | undefined): string {
  return SYSTEM_TYPES.find(s => s.value === t)?.label ?? t ?? '';
}

export const SYNC_DIRECTIONS: { value: SyncDirection; label: string }[] = [
  { value: 'None', label: 'None' }, { value: 'Inbound', label: 'Inbound' }, { value: 'Outbound', label: 'Outbound' },
  { value: 'Bidirectional', label: 'Bidirectional' }
];

/** Never / Success / Error / Pending — the server leaves it empty until a connector ran (none in A42). */
export function syncStatusLabel(s: string | null | undefined): 'Never' | 'Success' | 'Error' | 'Pending' {
  const v = (s ?? '').toLowerCase();
  if (v === 'success' || v === 'synced') return 'Success';
  if (v === 'error' || v === 'failed') return 'Error';
  if (v === 'pending') return 'Pending';
  return 'Never';
}

export function syncStatusTone(s: string | null | undefined): string {
  const l = syncStatusLabel(s);
  return l === 'Success' ? 'ok' : l === 'Error' ? 'er' : l === 'Pending' ? 'wn' : '';
}

// ── Numbers ───────────────────────────────────────────────────────────────────

/** 2 dp, rounded half away from zero like the server's display. */
export function round2(n: number | null | undefined): number {
  const v = Number(n ?? 0);
  if (!isFinite(v)) return 0;
  // Shifting through the decimal string avoids binary noise (1.005 * 100 = 100.49999…).
  const shifted = Number(Math.abs(v) + 'e2'); // NaN for exponent forms such as 1e-7
  const r = isNaN(shifted) ? Math.round(Math.abs(v) * 100) / 100 : Number(Math.round(shifted) + 'e-2');
  return r === 0 ? 0 : Math.sign(v) * r;
}

/** 1234.5 → "1,234.50". */
export function money(n: number | null | undefined): string {
  return round2(n).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

// ── Tree ──────────────────────────────────────────────────────────────────────

export interface TreeRow {
  node: AccountTreeNode;
  depth: number;
  hasChildren: boolean;
  expanded: boolean;
}

export interface TreeFilter {
  search?: string | null;
  /** Show levels 1..maxLevel only (null = all). */
  maxLevel?: number | null;
}

function sortNodes(nodes: AccountTreeNode[]): AccountTreeNode[] {
  return [...nodes].sort((a, b) => (a.sortOrder ?? 0) - (b.sortOrder ?? 0) || a.accountCode.localeCompare(b.accountCode));
}

/**
 * The visible rows of the collapsible tree, in order. Collapsed nodes hide their children. A search (code or name,
 * case-insensitive) shows every match with its ancestors, expanded, whatever was collapsed.
 */
export function flattenTree(roots: AccountTreeNode[], expanded: Set<string>, filter: TreeFilter = {}): TreeRow[] {
  const term = (filter.search ?? '').trim().toLowerCase();
  const maxLevel = filter.maxLevel ?? null;
  const rows: TreeRow[] = [];
  const matches = (n: AccountTreeNode) => n.accountCode.toLowerCase().includes(term) || n.accountName.toLowerCase().includes(term);
  const keep = new Set<string>();
  if (term) {
    const mark = (n: AccountTreeNode): boolean => {
      let any = matches(n);
      for (const c of n.children ?? []) if (mark(c)) any = true;
      if (any) keep.add(n.uuid);
      return any;
    };
    roots.forEach(mark);
  }
  const visit = (nodes: AccountTreeNode[], depth: number) => {
    for (const n of sortNodes(nodes)) {
      if (term && !keep.has(n.uuid)) continue;
      if (maxLevel !== null && n.level > maxLevel) continue;
      const kids = (n.children ?? []).filter(c => maxLevel === null || c.level <= maxLevel);
      const isOpen = term ? true : expanded.has(n.uuid);
      rows.push({ node: n, depth, hasChildren: kids.length > 0, expanded: isOpen });
      if (isOpen) visit(kids, depth + 1);
    }
  };
  visit(roots, 0);
  return rows;
}

/** Every node, depth-first (for expand-all and lookups). */
export function allNodes(roots: AccountTreeNode[]): AccountTreeNode[] {
  const out: AccountTreeNode[] = [];
  const walk = (ns: AccountTreeNode[]) => ns.forEach(n => { out.push(n); walk(n.children ?? []); });
  walk(roots);
  return out;
}

/** The sum of the leaf current balances below (or at) a node. */
export function nodeBalance(n: AccountTreeNode): number {
  if (!n.children?.length) return n.isGroup ? 0 : Number(n.currentBalance ?? 0);
  return n.children.reduce((s, c) => s + nodeBalance(c), 0);
}

// ── Account form ──────────────────────────────────────────────────────────────

export const MAX_CODE_LENGTH = 20;
export const MAX_NAME_LENGTH = 200;
export const MAX_DESCRIPTION_LENGTH = 500;
export const MAX_EXTERNAL_CODE_LENGTH = 50;
export const MAX_LEVEL = 4;

export interface AccountDraft {
  parent: Pick<Account, 'uuid' | 'accountCode' | 'level' | 'rootType' | 'isGroup'> | null;
  accountCode: string;
  accountName: string;
  accountType: AccountType | null;
  description: string;
  externalCode: string;
  sortOrder: number | null;
}

/**
 * The first thing the server would refuse about a new account (or, with `editing`, an edit); null when it would accept it.
 * Mirrors D-8 (code format and root digit), D-23 (type per root) and the uniqueness/parent rules.
 */
export function accountProblem(d: AccountDraft, existingCodes: Iterable<string>, editing = false): string | null {
  if (!editing) {
    if (!d.parent) return 'Choose the parent account.';
    if (!d.parent.isGroup || d.parent.level >= MAX_LEVEL) return 'The parent must be a group account (Level 1–3).';
    const code = (d.accountCode ?? '').trim();
    if (!code) return 'Enter the account code.';
    if (!/^[A-Za-z0-9]+$/.test(code)) return 'An account code may contain only letters and digits.';
    if (code.length > MAX_CODE_LENGTH) return `An account code can be at most ${MAX_CODE_LENGTH} characters.`;
    const digit = ROOT_DIGIT[d.parent.rootType];
    if (!code.startsWith(digit)) return `${d.parent.rootType} account codes must start with ${digit}.`;
    const lower = code.toLowerCase();
    for (const c of existingCodes) if (c.toLowerCase() === lower) return `Account code ${code} already exists.`;
  }
  const name = (d.accountName ?? '').trim();
  if (!name) return 'Enter the account name.';
  if (name.length > MAX_NAME_LENGTH) return `The name can be at most ${MAX_NAME_LENGTH} characters.`;
  const root = d.parent?.rootType;
  if (d.accountType && root && !ACCOUNT_TYPES_BY_ROOT[root].includes(d.accountType)) {
    return `Account type ${d.accountType} is not valid for ${root} accounts.`;
  }
  if ((d.description ?? '').length > MAX_DESCRIPTION_LENGTH) return `The description can be at most ${MAX_DESCRIPTION_LENGTH} characters.`;
  if ((d.externalCode ?? '').length > MAX_EXTERNAL_CODE_LENGTH) return `The external code can be at most ${MAX_EXTERNAL_CODE_LENGTH} characters.`;
  if (d.sortOrder !== null && d.sortOrder !== undefined && (d.sortOrder < 0 || !Number.isInteger(d.sortOrder))) return 'Sort order must be a whole number, 0 or more.';
  return null;
}

// ── Opening balances ──────────────────────────────────────────────────────────

export interface OpeningTotals {
  totalDebits: number;
  totalCredits: number;
  /** Debits − credits of the entered rows (the plug excluded). */
  difference: number;
  /** What goes to 3900 to balance: a credit when debits exceed credits, else a debit. */
  plugSide: 'Debit' | 'Credit' | null;
  plugAmount: number;
}

/** Totals of what is typed now (not what was saved): the plug row is never counted, it is the result. */
export function openingTotals(rows: Pick<OpeningRow, 'debitAmount' | 'creditAmount' | 'isPlug'>[]): OpeningTotals {
  let dr = 0, cr = 0;
  for (const r of rows) {
    if (r.isPlug) continue;
    dr += Number(r.debitAmount || 0);
    cr += Number(r.creditAmount || 0);
  }
  dr = round2(dr);
  cr = round2(cr);
  const diff = round2(dr - cr);
  return {
    totalDebits: dr, totalCredits: cr, difference: diff,
    plugSide: diff > 0 ? 'Credit' : diff < 0 ? 'Debit' : null,
    plugAmount: Math.abs(diff)
  };
}

/** The server's refusals for one row, in its words (D-19). */
export function openingRowProblem(r: Pick<OpeningRow, 'debitAmount' | 'creditAmount' | 'isPlug'>): string | null {
  if (r.isPlug) return null;
  const dr = Number(r.debitAmount || 0), cr = Number(r.creditAmount || 0);
  if (dr < 0 || cr < 0) return 'Amounts cannot be negative.';
  if (dr > 0 && cr > 0) return 'Cannot have both debit and credit amounts.';
  return null;
}

/** "differs" when both sides are known and not equal to the cent. */
export function differs(a: number | null | undefined, b: number | null | undefined): boolean {
  if (a === null || a === undefined || b === null || b === undefined) return false;
  return Math.abs(round2(a) - round2(b)) >= 0.005;
}

// ── Errors ────────────────────────────────────────────────────────────────────

/** The server's own words when it sent some, else a plain fallback. */
export function glError(err: unknown, fallback: string): string {
  return setupErrorMessage(err, fallback);
}

/** The upload's 400 list — top level `errors`, or inside the envelope's `result` (both accepted). */
export function uploadErrors(err: unknown): UploadError[] {
  const body = (err as { error?: any } | null)?.error;
  const list = body?.errors ?? body?.result?.errors;
  return Array.isArray(list) ? list : [];
}

/** A 409 "complete" refusal may list the unmet prerequisites (errors / result / unmet); the text is shown either way. */
export function unmetList(err: unknown): string[] {
  const body = (err as { error?: any } | null)?.error;
  const list = body?.unmet ?? body?.errors ?? body?.result?.unmet ?? body?.result;
  return Array.isArray(list) ? list.map((x: any) => typeof x === 'string' ? x : x?.message ?? String(x)) : [];
}

// ── Wizard ────────────────────────────────────────────────────────────────────

export interface StepHelp { what: string; why: string; tips: string[]; example: string; }

export const WIZARD_STEPS: { step: number; title: string; short: string }[] = [
  { step: 1, title: 'Choose a template', short: 'Template' },
  { step: 2, title: 'Review the chart of accounts', short: 'Accounts' },
  { step: 3, title: 'Set up the fiscal year', short: 'Fiscal year' },
  { step: 4, title: 'Assign default accounts', short: 'Defaults' },
  { step: 5, title: 'Enter opening balances', short: 'Opening balances' },
  { step: 6, title: 'Review and finish', short: 'Review' }
];

/** True when the step's own prerequisite is met, from the server's steps (fallback: the status flags). */
export function stepDone(s: SetupStatus | null | undefined, step: number): boolean {
  if (!s) return false;
  const fromServer = s.steps?.find(x => x.step === step);
  if (fromServer) return !!fromServer.done;
  switch (step) {
    case 1: case 2: return (s.accountsCreatedCount ?? 0) > 0;
    case 3: return !!s.fiscalYearConfigured;
    case 4: return !!s.defaultsConfigured;
    case 5: return !!s.openingBalancesEntered;
    case 6: return s.status === 'Complete';
    default: return false;
  }
}

export const STEP_HELP: Record<number, StepHelp> = {
  1: {
    what: 'A chart of accounts (CoA) is the list of every account your books record money in, grouped under the five root types: Assets, Liabilities, Equity, Revenue and Expenses. A template gives you a ready-made list for your country and type of business.',
    why: 'Every report — balance sheet, profit and loss, tax returns — is built by adding up these accounts. Starting from a sound template saves days of work and keeps the structure your accountant expects.',
    tips: [
      'Pick the template for your country first; it carries local tax and payroll accounts.',
      'Preview it before applying. You can add, rename or deactivate accounts afterwards.',
      'Coming from another system? Upload your own chart as CSV or JSON (download the blank template for the columns).',
      'The chart can only be replaced until the fiscal year is set.'
    ],
    example: 'A Pakistani trading company applies "Pakistan — General" and gets 1100 Cash, 1200 Accounts Receivable, 2100 Accounts Payable, 2200 Sales Tax Payable, 4100 Sales and 5100 Cost of Goods Sold, ready to use.'
  },
  2: {
    what: 'The tree of accounts now in your books. Levels 1–3 are groups that only total what is below them; Level 4 accounts are the postable ones that transactions and balances go to.',
    why: 'Reports roll up along this tree, so an account placed under the wrong group shows in the wrong line of the balance sheet or P&L.',
    tips: [
      'Add an account under the group it belongs to; the code is suggested for you.',
      'Codes start with the root digit: 1 Assets, 2 Liabilities, 3 Equity, 4 Revenue, 5 Expenses.',
      'Set the account type on postable accounts (Bank, Receivable, Payable…) — defaults and reports rely on it.',
      'System accounts (the five roots and 3900 Opening Balance Equity) cannot be deleted.'
    ],
    example: 'You keep two bank accounts: add "1112 Meezan Bank — Current" and "1113 HBL — Savings" under 1110 Bank Accounts, both with type Bank.'
  },
  3: {
    what: 'The fiscal (financial) year is the 12-month period your accounts are closed for. It is split into periods — normally one per month — plus a 13th period for year-end adjustments.',
    why: 'Every posting is dated into a period. Locking or closing periods stops anyone from changing figures that were already reported.',
    tips: [
      'Use the year your company reports for tax: July–June in Pakistan, often January–December elsewhere.',
      'The opening balance date must fall inside the first fiscal year.',
      'Lock a period to stop postings temporarily; close it when it is final — a closed period cannot be reopened.'
    ],
    example: 'FY 2026-27 runs 1 Jul 2026 – 30 Jun 2027 in PKR, with periods Jul 2026 … Jun 2027 and period 13 for audit adjustments.'
  },
  4: {
    what: 'Default accounts tell the system where automatic postings go: which account receives customer invoices, supplier bills, sales tax, inventory and so on.',
    why: 'When invoices and payments start posting to the ledger, each line needs a target account. Missing or wrong defaults mean wrong balances or failed postings.',
    tips: [
      'Assign every Required row; Optional ones can wait.',
      'Only postable (Level 4) active accounts can be chosen.',
      'A type mismatch (e.g. a Bank account as Receivable) is allowed but warned about — check it is intended.',
      'Opening Balance Equity is fixed to 3900 and cannot be changed.'
    ],
    example: 'Accounts Receivable → 1200 Trade Debtors, Accounts Payable → 2100 Trade Creditors, Sales Tax Payable → 2210 Output Sales Tax.'
  },
  5: {
    what: 'Opening balances are what each account held on the conversion date — the day your books move to this system — taken from your previous trial balance.',
    why: 'Without them the balance sheet starts at zero and never agrees with the bank, debtors or creditors. Total debits must equal total credits; any difference is parked in 3900 Opening Balance Equity.',
    tips: [
      'Copy the closing trial balance of the day before the conversion date.',
      'Enter each amount on its natural side: assets and expenses as debits, liabilities, equity and revenue as credits.',
      'Revenue and expense balances are only needed if you convert mid-year.',
      'Compare A/R, A/P and Inventory with the customer, supplier and stock totals you imported.',
      'A new company with no history can skip this step.'
    ],
    example: 'Cash 250,000 Dr, Trade Debtors 1,200,000 Dr, Trade Creditors 800,000 Cr, Share Capital 500,000 Cr → debits exceed credits by 150,000, which goes to 3900 as a credit.'
  },
  6: {
    what: 'A last look at what was set up: the accounts, the fiscal year, the defaults and the opening balances.',
    why: 'Finishing marks the general ledger ready. The root accounts and the conversion date are locked from then on, and GL reporting opens.',
    tips: [
      'Check the opening balances are balanced, or that the difference is intended.',
      'You can still add accounts and change defaults after finishing.',
      'Until journal entries are posted, Setup Status can reset everything and start again.'
    ],
    example: '72 accounts, FY 2026-27, 14 of 14 required defaults, opening balances posted as JE-2026-00001 → Finish.'
  }
};
