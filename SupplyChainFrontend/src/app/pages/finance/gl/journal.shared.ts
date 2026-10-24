import { DOC_TYPE_LABEL, FinDocType, sourceDocRoute } from '../../../services/financial-posting.service';
import {
  ClosePeriodStatus, JournalStatus, JournalTypeKind, OpenItem, PartnerType, PeriodAction, TrialBalanceLine, TrialBalanceReport
} from '../../../services/gl-journal.service';
import { money, round2 } from './gl.shared';

// A47 — what the journal-engine pages share: the rules the server enforces (checked here first, server words win),
// totals, status tones and labels. Server: SMS.Modules.Accounting (JournalEntryService & co.) — keep the two in step.

/** BR-JE-001 — |Σ debit − Σ credit| must stay below this. */
export const BALANCE_TOLERANCE = 0.01;
export const MAX_LINES = 500;
export const MAX_TYPE_CODE = 10;
export const MAX_TYPE_PREFIX = 10;
export const MAX_TYPE_NAME = 100;
export const MAX_REVERSAL_REASON = 200;

// ── Labels & tones ────────────────────────────────────────────────────────────

/** Task 254 — Draft blue, Posted green, Reversed red (sf-pill tones). */
export function journalStatusTone(s: JournalStatus | string | null | undefined): 'in' | 'ok' | 'er' | '' {
  switch (s) {
    case 'Draft': return 'in';
    case 'Posted': return 'ok';
    case 'Reversed': return 'er';
    default: return '';
  }
}

export const KIND_LABEL: Record<JournalTypeKind, string> = {
  Sale: 'Sale', Purchase: 'Purchase', Cash: 'Cash', Bank: 'Bank', Miscellaneous: 'Miscellaneous', Opening: 'Opening', Closing: 'Closing'
};

export function kindLabel(k: string | null | undefined): string {
  return KIND_LABEL[k as JournalTypeKind] ?? k ?? '';
}

/** D-13 — the partner a line of this kind of journal is about (Sale → customer, Purchase → vendor); null = either. */
export function partnerTypeFor(kind: string | null | undefined): PartnerType | null {
  return kind === 'Sale' ? 'Customer' : kind === 'Purchase' ? 'Vendor' : null;
}

export function periodStatusLabel(s: ClosePeriodStatus | string | null | undefined): string {
  return s === 'AdjustmentOnly' ? 'Adjustments only' : (s ?? '');
}

export function periodStatusTone(s: ClosePeriodStatus | string | null | undefined): string {
  switch (s) {
    case 'Open': return 'ok';
    case 'AdjustmentOnly': return 'vi';
    case 'Locked': return 'wn';
    case 'Closed': return 'er';
    default: return '';
  }
}

/** "MANUAL_JOURNAL" → "Manual journal". */
export function entryKindLabel(t: string | null | undefined): string {
  const s = (t ?? '').replace(/_/g, ' ').trim().toLowerCase();
  return s ? s.charAt(0).toUpperCase() + s.slice(1) : '';
}

// ── Money ─────────────────────────────────────────────────────────────────────

/** Task 355 — 1234.5 → "1,234.50"; −24000 → "(24,000.00)". */
export function parens(n: number | null | undefined): string {
  const v = round2(n);
  return v < 0 ? `(${money(-v)})` : money(v);
}

// ── Source document (task 263) ────────────────────────────────────────────────

/** A44 EntryType (FinDocType in upper snake: SALES_INVOICE, POS_SALE) back to the FinDocType. */
export function finDocTypeOf(entryType: string | null | undefined): FinDocType | null {
  if (!entryType) return null;
  const snake = (t: string) => t.replace(/(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])/g, '_').toUpperCase();
  return (Object.keys(DOC_TYPE_LABEL) as FinDocType[]).find(t => snake(t) === entryType.toUpperCase()) ?? null;
}

/** The SCM page of a journal's source document (soft link), when it has one. */
export function entrySourceRoute(e: { entryType?: string | null; sourceDocumentUuid?: string | null }): string[] | null {
  const docType = finDocTypeOf(e.entryType);
  if (!docType || !e.sourceDocumentUuid) return null;
  return sourceDocRoute({ documentType: docType, sourceDocumentUuid: e.sourceDocumentUuid });
}

export function sourceLabel(e: { entryType?: string | null; isManual?: boolean | null; sourceModule?: string | null }): string {
  if (e.isManual || e.entryType === 'MANUAL_JOURNAL') return 'Manual entry';
  const t = finDocTypeOf(e.entryType);
  if (t) return DOC_TYPE_LABEL[t] ?? entryKindLabel(e.entryType);
  return entryKindLabel(e.entryType) || e.sourceModule || '';
}

// ── Entry form ────────────────────────────────────────────────────────────────

export interface LineDraft {
  key: number;
  accountUuid: string | null;
  description: string;
  debit: number | null;
  credit: number | null;
  partnerUuid: string | null;
  partnerName: string | null;
  partnerType: PartnerType | null;
}

export interface EntryDraft {
  journalTypeUuid: string | null;
  entryDate: Date | null;
  documentDate: Date | null;
  reference: string;
  description: string;
  currencyCode: string | null;
  exchangeRate: number | null;
  lines: LineDraft[];
}

export interface EntryTotals { debit: number; credit: number; difference: number; balanced: boolean; }

/** Task 258 — Σ debit, Σ credit and debit − credit of what is typed now (2 dp). */
export function entryTotals(lines: Pick<LineDraft, 'debit' | 'credit'>[]): EntryTotals {
  let dr = 0, cr = 0;
  for (const l of lines) { dr += Number(l.debit || 0); cr += Number(l.credit || 0); }
  const debit = round2(dr), credit = round2(cr), difference = round2(dr - cr);
  return { debit, credit, difference, balanced: Math.abs(difference) < BALANCE_TOLERANCE && debit > 0 };
}

/**
 * REV-18 / REV-2 — a saved entry's base totals (2 dp) and whether they are exactly equal, as the server checks a posted
 * entry. (The form's own `entryTotals` keeps the 0.01 typing tolerance.)
 */
export function baseTotals(lines: { debit: number; credit: number }[] | null | undefined): { debit: number; credit: number; balanced: boolean } {
  let dr = 0, cr = 0;
  for (const l of lines ?? []) { dr += Number(l.debit || 0); cr += Number(l.credit || 0); }
  const debit = round2(dr), credit = round2(cr);
  return { debit, credit, balanced: debit === credit };
}

/** A line nothing was typed on (left out when saving). */
export function isBlankLine(l: LineDraft): boolean {
  return !l.accountUuid && !Number(l.debit || 0) && !Number(l.credit || 0) && !l.description.trim() && !l.partnerUuid;
}

/** BR-JE-003 / BR-JE-011 — the first thing wrong with one line, or null. */
export function lineProblem(l: LineDraft, requiresPartner: boolean): string | null {
  const dr = Number(l.debit || 0), cr = Number(l.credit || 0);
  if (!l.accountUuid) return 'Choose the account.';
  if (dr < 0 || cr < 0) return 'Amounts cannot be negative.';
  if (dr > 0 && cr > 0) return 'A line is either a debit or a credit, not both.';
  if (dr === 0 && cr === 0) return 'Enter a debit or a credit amount.';
  if (requiresPartner && !l.partnerUuid) return 'This journal type needs a partner on every line.';
  return null;
}

/**
 * The first thing the server would refuse about the entry (BR-JE-001/002/003/011/012), or null. `baseCurrency` null = the
 * entry is in the base currency (no rate needed).
 */
export function entryProblem(d: EntryDraft, requiresPartner: boolean, baseCurrency: string | null): string | null {
  if (!d.journalTypeUuid) return 'Choose the journal type.';
  if (!d.entryDate) return 'Choose the entry date.';
  if (d.currencyCode && baseCurrency && d.currencyCode !== baseCurrency && !(Number(d.exchangeRate) > 0)) {
    return `Enter the exchange rate (1 ${d.currencyCode} = ? ${baseCurrency}).`;
  }
  const lines = d.lines.filter(l => !isBlankLine(l));
  if (lines.length < 2) return 'An entry needs at least two lines.';
  if (lines.length > MAX_LINES) return `An entry can have at most ${MAX_LINES} lines.`;
  for (let i = 0; i < lines.length; i++) {
    const p = lineProblem(lines[i], requiresPartner);
    if (p) return `Line ${d.lines.indexOf(lines[i]) + 1}: ${p}`;
  }
  const t = entryTotals(lines);
  if (!t.balanced) return `Debits and credits differ by ${money(Math.abs(t.difference))}.`;
  return null;
}

/** Fills the empty side of the last line with what balances the entry (the "balance" shortcut). */
export function balancingAmount(lines: Pick<LineDraft, 'debit' | 'credit'>[], index: number): { debit: number | null; credit: number | null } {
  const others = lines.filter((_, i) => i !== index);
  const t = entryTotals(others);
  if (t.difference > 0) return { debit: null, credit: t.difference };
  if (t.difference < 0) return { debit: -t.difference, credit: null };
  return { debit: null, credit: null };
}

// ── Journal type form ─────────────────────────────────────────────────────────

export interface TypeDraft {
  code: string;
  name: string;
  type: JournalTypeKind | string | null;
  sequencePrefix: string;
  defaultDebitAccountUuid: string | null;
  defaultCreditAccountUuid: string | null;
  requiresPartner: boolean;
  autoPost: boolean;
}

/** §4.2 constraints: code/prefix upper-case alphanumeric ≤ 10, name ≤ 100, one Opening and one Closing type per org. */
export function typeProblem(d: TypeDraft, others: { uuid?: string; code: string; type: string }[], editingUuid: string | null): string | null {
  const code = (d.code ?? '').trim();
  if (!code) return 'Enter the code.';
  if (!/^[A-Z0-9]+$/.test(code)) return 'The code may contain only upper-case letters and digits.';
  if (code.length > MAX_TYPE_CODE) return `The code can be at most ${MAX_TYPE_CODE} characters.`;
  if (others.some(o => o.uuid !== editingUuid && o.code.toUpperCase() === code)) return `Journal type ${code} already exists.`;
  const name = (d.name ?? '').trim();
  if (!name) return 'Enter the name.';
  if (name.length > MAX_TYPE_NAME) return `The name can be at most ${MAX_TYPE_NAME} characters.`;
  if (!d.type) return 'Choose the kind of journal.';
  if ((d.type === 'Opening' || d.type === 'Closing') && others.some(o => o.uuid !== editingUuid && o.type === d.type)) {
    return `There can be only one ${d.type.toLowerCase()} journal type.`;
  }
  const prefix = (d.sequencePrefix ?? '').trim();
  if (!prefix) return 'Enter the number prefix.';
  if (!/^[A-Z0-9]+$/.test(prefix)) return 'The prefix may contain only upper-case letters and digits.';
  if (prefix.length > MAX_TYPE_PREFIX) return `The prefix can be at most ${MAX_TYPE_PREFIX} characters.`;
  return null;
}

/** "SALE/2026/0001" — what the next number looks like (D-7). */
export function numberPreview(prefix: string | null | undefined, year = new Date().getFullYear()): string {
  const p = (prefix ?? '').trim().toUpperCase();
  return p ? `${p}/${year}/0001` : '';
}

// ── Reconciliation ────────────────────────────────────────────────────────────

export interface SelectionTotals { count: number; debit: number; credit: number; residual: number; full: boolean; }

/** Task 318 — of the selected open items: Σ open debit, Σ open credit, the residual (signed) and whether it nets to zero. */
export function selectionTotals(items: Pick<OpenItem, 'amountResidual'>[]): SelectionTotals {
  let dr = 0, cr = 0;
  for (const i of items) {
    const r = Number(i.amountResidual || 0);
    if (r > 0) dr += r; else cr += -r;
  }
  const residual = round2(dr - cr);
  return { count: items.length, debit: round2(dr), credit: round2(cr), residual, full: Math.abs(residual) < BALANCE_TOLERANCE };
}

/** BR-RC-003 — what stops the selection from being reconciled, or null. */
export function reconcileProblem(items: Pick<OpenItem, 'amountResidual' | 'reconcileGroupUuid'>[]): string | null {
  if (items.length < 2) return 'Select at least two lines.';
  if (items.some(i => !!i.reconcileGroupUuid)) return 'A partly reconciled line is selected: unreconcile its group first.';
  const t = selectionTotals(items);
  if (t.debit === 0 || t.credit === 0) return 'Select debit and credit lines to match against each other.';
  return null;
}

// ── Trial balance ─────────────────────────────────────────────────────────────

export function openingBalance(l: TrialBalanceLine): number { return round2(Number(l.openingDebit || 0) - Number(l.openingCredit || 0)); }
/** The server's `balance` (closingDebit − closingCredit) when sent. */
export function closingBalance(l: TrialBalanceLine): number {
  return round2(l.balance ?? (Number(l.closingDebit || 0) - Number(l.closingCredit || 0)));
}

export interface TbTotals { opening: number; debit: number; credit: number; balance: number; closingDebit: number; closingCredit: number; balanced: boolean; }

/** Task 352 — sums of the shown columns (the server's totals win); balanced from the server, else Σ debit = Σ credit. */
export function tbTotals(r: TrialBalanceReport | null | undefined): TbTotals {
  let opening = 0, debit = 0, credit = 0, balance = 0;
  for (const l of r?.lines ?? []) {
    opening += openingBalance(l);
    debit += Number(l.periodDebit || 0);
    credit += Number(l.periodCredit || 0);
    balance += closingBalance(l);
  }
  const t = r?.totals;
  if (t) {
    opening = Number(t.openingDebit || 0) - Number(t.openingCredit || 0);
    debit = Number(t.periodDebit || 0);
    credit = Number(t.periodCredit || 0);
    balance = Number(t.closingDebit || 0) - Number(t.closingCredit || 0);
  }
  const closingDebit = round2(r?.totalDebit ?? t?.closingDebit ?? 0), closingCredit = round2(r?.totalCredit ?? t?.closingCredit ?? 0);
  const balanced = r?.isBalanced ?? Math.abs(round2(closingDebit - closingCredit)) < BALANCE_TOLERANCE;
  return { opening: round2(opening), debit: round2(debit), credit: round2(credit), balance: round2(balance), closingDebit, closingCredit, balanced };
}

/**
 * REV-15 — a text cell starting with = + - @ TAB or CR is prefixed with ' so a spreadsheet does not run it as a formula
 * (the server export's rule). Numbers are written as they are.
 */
export function csvCell(v: string | number): string {
  let s = String(v ?? '');
  if (typeof v === 'string' && /^[=+\-@\t\r]/.test(s)) s = "'" + s;
  return /[",\r\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
}

/** The trial balance as CSV (fallback when the server export is not there). */
export function trialBalanceCsv(r: TrialBalanceReport): string {
  const rows: (string | number)[][] = [
    ['Account code', 'Account name', 'Root type', 'Opening debit', 'Opening credit', 'Period debit', 'Period credit', 'Closing debit', 'Closing credit'],
    ...r.lines.map(l => [l.account?.accountCode ?? '', l.account?.accountName ?? '', l.rootType, round2(l.openingDebit), round2(l.openingCredit), round2(l.periodDebit),
      round2(l.periodCredit), round2(l.closingDebit), round2(l.closingCredit)]),
    ['', 'Total', '', '', '', '', '', round2(r.totalDebit), round2(r.totalCredit)]
  ];
  return rows.map(row => row.map(csvCell).join(',')).join('\r\n');
}

// ── Period close (D-11) ───────────────────────────────────────────────────────

const ACTION_ORDER: PeriodAction[] = ['Lock', 'AdjustmentOnly', 'Reopen', 'FinalClose'];

/**
 * D-11 — Open → AdjustmentOnly / Locked; AdjustmentOnly → Open / Locked; Locked → Open / Closed (final); Closed: none.
 * The server's `allowedActions` (state + the caller's permissions) win when it sent them.
 */
export function periodCloseActions(s: ClosePeriodStatus | string, allowed?: (PeriodAction | string)[] | null): PeriodAction[] {
  if (Array.isArray(allowed)) return ACTION_ORDER.filter(a => allowed.includes(a));
  switch (s) {
    case 'Open': return ['Lock', 'AdjustmentOnly'];
    case 'AdjustmentOnly': return ['Lock', 'Reopen'];
    case 'Locked': return ['Reopen', 'FinalClose'];
    default: return [];
  }
}

export const PERIOD_ACTION_LABEL: Record<PeriodAction, string> = {
  Lock: 'Lock', AdjustmentOnly: 'Adjustments only', Reopen: 'Reopen', FinalClose: 'Final close'
};

/** BR-PC-001 / PC-001 — Lock and AdjustmentOnly are refused while drafts are dated in the period. */
export function actionBlockedByDrafts(a: PeriodAction, drafts: number): boolean {
  return (a === 'Lock' || a === 'AdjustmentOnly') && drafts > 0;
}

/** D-18 — why the year-end close cannot run, from the period table (the server re-checks). */
export function yearEndBlockers(periods: { periodNumber: number; periodName: string; status: string; draftEntryCount: number }[]): string[] {
  const out: string[] = [];
  const regular = periods.filter(p => p.periodNumber <= 12);
  const open = regular.filter(p => p.status !== 'Locked' && p.status !== 'Closed');
  if (open.length) out.push(`Lock periods first: ${open.map(p => p.periodName).join(', ')}.`);
  const drafts = periods.filter(p => p.draftEntryCount > 0);
  if (drafts.length) out.push(`Post or delete the drafts in ${drafts.map(p => p.periodName).join(', ')}.`);
  return out;
}

// ── Errors ────────────────────────────────────────────────────────────────────

/**
 * Contract §0 — every error of a JournalErrorFilter refusal (`result.errors[]`), line errors as "Line n: text" like the
 * first one in `message`. Also reads an ASP.NET `errors` map.
 */
export function journalErrors(err: unknown): string[] {
  const body = (err as { error?: any } | null)?.error;
  const list = body?.result?.errors ?? body?.errors;
  if (Array.isArray(list)) {
    return list.map((x: any) => typeof x === 'string' ? x
      : x?.lineNumber ? `Line ${x.lineNumber}: ${x.message}` : (x?.message ?? String(x)));
  }
  if (list && typeof list === 'object') return Object.values(list).flat().map(x => String(x));
  return [];
}

/** The JE-/RC-/PC- code of a refusal, when there is one. */
export function journalErrorCode(err: unknown): string | null {
  const body = (err as { error?: any } | null)?.error;
  return body?.result?.errorCode ?? null;
}
