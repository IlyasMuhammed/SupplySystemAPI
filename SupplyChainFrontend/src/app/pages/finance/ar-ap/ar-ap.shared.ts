import {
  AgingBasis, AgingReport, CreditCheckResult, CreditExposure, DunningLevel, DunningProcedureSave, EarlyPaymentTermSave, PaymentRunDetail, PaymentRunItem,
  PaymentRunVendorGroup, ToleranceRuleSave
} from '../../../services/ar-ap.service';
import { toDateOnly } from '../../../shared/date-only';
import { formatCode } from '../../../shared/format-code';
import { glError, money, round2 } from '../gl/gl.shared';

// A48 — what the AR / AP sub-ledger pages share: bucket labels, tones, the rules the server enforces (checked here
// first so the user sees them before a round trip), and run arithmetic. Server: SMS.Modules.Finance — keep in step.

export { money, round2 };

/** The server's own words when it sent some, else a plain fallback. */
export function arApError(err: unknown, fallback: string): string {
  const body = (err as { error?: any } | null)?.error;
  // JournalErrorFilter-style envelopes put the list under result.errors.
  const first = body?.result?.errors?.[0]?.message;
  if (typeof body?.message !== 'string' && typeof first === 'string' && first.trim()) return first;
  return glError(err, fallback);
}

/**
 * Review fix — a failed download (`responseType: 'blob'`) carries its JSON refusal as a Blob: read it so the user sees
 * the server's words (e.g. the export's 409), else as `arApError`.
 */
export async function arApErrorAsync(err: unknown, fallback: string): Promise<string> {
  const body = (err as { error?: unknown } | null)?.error;
  if (typeof Blob !== 'undefined' && body instanceof Blob) {
    try {
      const text = await body.text();
      const parsed = JSON.parse(text);
      return arApError({ ...(err as object), error: parsed }, fallback);
    } catch {
      return arApError({ ...(err as object), error: null }, fallback);
    }
  }
  return arApError(err, fallback);
}

export function today(): string { return toDateOnly(new Date()); }

export function addDays(iso: string, days: number): string {
  const [y, m, d] = iso.slice(0, 10).split('-').map(Number);
  return toDateOnly(new Date(y, m - 1, d + days));
}

/** Whole days from a to b (b − a), both yyyy-MM-dd. */
export function daysBetween(a: string, b: string): number {
  const [ya, ma, da] = a.slice(0, 10).split('-').map(Number);
  const [yb, mb, db] = b.slice(0, 10).split('-').map(Number);
  return Math.round((Date.UTC(yb, mb - 1, db) - Date.UTC(ya, ma - 1, da)) / 86400000);
}

// ── Aging buckets (D-12, BR-AG-005/006) ───────────────────────────────────────

export const BASIS_OPTIONS: { label: string; value: AgingBasis }[] = [
  { label: 'Due date', value: 'DueDate' }, { label: 'Document date', value: 'DocumentDate' }
];

/** Upper bounds of the overdue buckets after Current: Current / 1-30 / 31-60 / 61-90 / 91-120 / 120+. */
export const DEFAULT_BUCKETS = [30, 60, 90, 120];

/** ["Current", "1-30", "31-60", …, "120+"] for the given upper bounds. */
export function bucketLabels(bounds: number[]): string[] {
  const out = ['Current'];
  let lo = 1;
  for (const b of bounds) { out.push(`${lo}-${b}`); lo = b + 1; }
  out.push(`${bounds.length ? bounds[bounds.length - 1] : 0}+`);
  return out;
}

/** "30, 60, 90" → [30, 60, 90]; null when not a list of numbers. */
export function parseBuckets(text: string): number[] | null {
  const parts = (text ?? '').split(/[,\s]+/).map(s => s.trim()).filter(Boolean);
  if (!parts.length) return null;
  const nums = parts.map(Number);
  return nums.every(n => Number.isFinite(n)) ? nums : null;
}

/** BR-AG-006 / contract §5 — at least 2 boundaries (3 buckets), whole positive days, ascending. */
export function bucketsProblem(bounds: number[] | null): string | null {
  if (!bounds || bounds.length < 2) return 'At least 3 buckets are needed (2 boundaries).';
  if (bounds.some(b => !Number.isInteger(b) || b <= 0)) return 'Bucket boundaries must be positive and ascending.';
  for (let i = 1; i < bounds.length; i++) if (bounds[i] <= bounds[i - 1]) return 'Bucket boundaries must be positive and ascending.';
  return null;
}

/** The report's own labels when it sent them, else built from its bucket days. */
export function reportLabels(r: AgingReport | null | undefined): string[] {
  if (!r) return bucketLabels(DEFAULT_BUCKETS);
  if (r.bucketLabels?.length) return r.bucketLabels;
  return bucketLabels((r.bucketDays ?? []).filter(d => d > 0));
}

/**
 * REV-24 — one CSV cell: a text cell starting with = + - @ TAB or CR is prefixed with ' so a spreadsheet does not run it
 * as a formula (the rule of the server exports and A47's csvCell); numbers (also negative, e.g. "-12.00") stay as they are.
 */
export function csvCell(v: string | number | null | undefined): string {
  let s = v === null || v === undefined ? '' : String(v);
  if (typeof v === 'string' && /^[=+\-@\t\r]/.test(s) && !/^-?\d+(\.\d+)?$/.test(s)) s = "'" + s;
  return /[",\r\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;
}

/** The aging table as CSV (partner, code, one column per bucket, total), amounts plain 2 dp. */
export function agingCsv(r: AgingReport, partnerLabel: string): string {
  const labels = reportLabels(r);
  const rows: (string | number)[][] = [[partnerLabel, 'Code', ...labels, 'Total']];
  for (const l of r.lines) {
    rows.push([l.partner.name, l.partner.code ?? '', ...labels.map((_, i) => round2(l.bucketAmounts?.[i] ?? 0).toFixed(2)), round2(l.totalOutstanding).toFixed(2)]);
  }
  rows.push(['TOTAL', '', ...labels.map((_, i) => round2(r.totals?.bucketTotals?.[i] ?? 0).toFixed(2)), round2(r.totals?.grandTotal ?? r.totalOutstanding).toFixed(2)]);
  return rows.map(row => row.map(csvCell).join(',')).join('\r\n') + '\r\n';
}


// ── Tones (sf-pill classes) ───────────────────────────────────────────────────

export function riskTone(t: string | null | undefined): string {
  return t === 'Low' ? 'ok' : t === 'High' ? 'er' : 'wn';
}

export function matchTone(s: string | null | undefined): string {
  switch (s) {
    case 'Matched': case 'Approved': return 'ok';
    case 'Overridden': return 'in';
    case 'PartialMatch': case 'Mismatch': case 'Variance': case 'QuantityMismatch': case 'PriceMismatch': case 'Unmatched': return 'wn';
    case 'Rejected': case 'Cancelled': case 'Reversed': return 'er';
    default: return '';
  }
}

export function matchLabel(s: string | null | undefined): string {
  switch (s) {
    case 'PartialMatch': return 'Partial match';
    case 'QuantityMismatch': return 'Quantity mismatch';
    case 'PriceMismatch': return 'Price mismatch';
    case 'Unmatched': return 'No PO line';
    case 'ThreeWay': return '3-way';
    case 'TwoWay': return '2-way (no goods receipt)';
    default: return s ?? '';
  }
}

type MatchLike = { matchStatus?: string | null; lineMatchResult?: string | null; overrideReason?: string | null; overridden?: boolean | null };

/**
 * Contract §6 — the invoice's A48 state: Approved / Rejected / Reversed from the existing MatchStatus, else Overridden when
 * a variance was overridden (detail: overrideReason; list: overridden), else the per-line result, else the existing status.
 */
export function matchState(m: MatchLike | null | undefined): string {
  if (!m) return '';
  if (m.matchStatus === 'Approved' || m.matchStatus === 'Rejected' || m.matchStatus === 'Reversed') return m.matchStatus;
  if (m.overrideReason || m.overridden) return 'Overridden';
  return m.lineMatchResult || m.matchStatus || 'Pending';
}

/** §6 — only an invoice with a match variance can be overridden. */
export function canOverrideMatch(m: { matchStatus?: string | null; lineMatchResult?: string | null; overrideReason?: string | null } | null | undefined): boolean {
  if (!m || m.overrideReason) return false;
  if (m.matchStatus === 'Approved' || m.matchStatus === 'Rejected' || m.matchStatus === 'Reversed') return false;
  return m.lineMatchResult === 'PartialMatch' || m.lineMatchResult === 'Mismatch' || m.matchStatus === 'Variance';
}

/** §6 — approvable when matched, or when its variance was overridden. */
export function canApproveMatch(m: { matchStatus?: string | null; lineMatchResult?: string | null; overrideReason?: string | null } | null | undefined): boolean {
  if (!m) return false;
  if (m.matchStatus === 'Approved' || m.matchStatus === 'Rejected' || m.matchStatus === 'Reversed') return false;
  if (m.overrideReason) return true;
  return m.lineMatchResult === 'Matched' || (!m.lineMatchResult && m.matchStatus === 'Matched');
}

export function scheduleTone(s: string | null | undefined): string {
  switch (s) {
    case 'Paid': return 'ok';
    case 'Blocked': case 'Cancelled': return 'er';
    case 'PartiallyPaid': case 'Scheduled': return 'in';
    default: return '';
  }
}

export function runTone(s: string | null | undefined): string {
  switch (s) {
    case 'Completed': case 'Paid': case 'Sent': return 'ok';
    case 'Approved': case 'Executing': case 'Processing': return 'in';
    case 'Proposed': case 'Reviewing': case 'Draft': case 'Pending': return 'wn';
    case 'Cancelled': case 'Failed': return 'er';
    default: return '';
  }
}

export function labelOf(s: string | null | undefined): string {
  if (!s) return '';
  if (/^[A-Z0-9_]+$/.test(s) && s.length > 1) return formatCode(s);
  return LABELS[s] ?? s;
}

const LABELS: Record<string, string> = {
  PartiallyPaid: 'Partially paid', TempIncrease: 'Temporary limit', TempExpired: 'Temporary limit expired', OpenItems: 'Open items',
  RiskTier: 'Risk tier', DunningHold: 'Dunning hold', DunningReset: 'Dunning reset', NotSent: 'Not sent', DueDate: 'Due date', DocumentDate: 'Document date'
};

// ── Credit (BR-CR-002/003) ────────────────────────────────────────────────────

/** BR-CR-002 — max(limit, temporary) while the temporary limit has not expired; null = no limit. */
export function effectiveLimit(limit: number | null | undefined, temp: number | null | undefined, expiry: string | null | undefined, on = today()): number | null {
  const base = limit && limit > 0 ? limit : null;
  const tempOk = temp && temp > 0 && (!expiry || expiry.slice(0, 10) >= on) ? temp : null;
  if (base === null && tempOk === null) return null;
  return Math.max(base ?? 0, tempOk ?? 0);
}

export function creditStatus(e: Pick<CreditExposure, 'isOnHold' | 'isOverLimit'> | null | undefined): { label: string; tone: string } {
  if (!e) return { label: '', tone: '' };
  if (e.isOnHold) return { label: 'On hold', tone: 'er' };
  if (e.isOverLimit) return { label: 'Over limit', tone: 'wn' };
  return { label: 'Active', tone: 'ok' };
}

/** §1.4 / D-5 — what each credit control mode does to a sale-order confirm. */
export function creditModeHelp(mode: string | null | undefined): string {
  return mode === 'Block' ? 'Sale orders over the limit or for a customer on hold are refused; CREDIT_OVERRIDE with a reason confirms them anyway.'
    : mode === 'Warn' ? 'A hold blocks sale orders; an exceeded limit is a warning only.'
    : 'Sale orders are not checked; holds and limits are recorded only.';
}

export interface CreditBlock { message: string; check: CreditCheckResult | null; }

/**
 * Contract §2 — a sale-order confirm refused by credit control (Block mode): 409 "Credit check failed: … Confirming
 * needs the CREDIT_OVERRIDE permission and an override reason." The check may come along in the body.
 */
export function creditBlockOf(err: unknown): CreditBlock | null {
  const e = err as { status?: number; error?: any } | null;
  const body = e?.error;
  const message: string | undefined = typeof body?.message === 'string' ? body.message : typeof body === 'string' ? body : undefined;
  if (!message || !/^credit check failed/i.test(message.trim())) return null;
  const check = body?.result?.creditCheck ?? body?.creditCheck ?? (body?.result && 'projectedExposure' in body.result ? body.result : null);
  return { message: message.trim(), check: check ?? null };
}

/**
 * Contract §2 — a sales invoice created over the credit check is a warning: the result's `creditWarning`, or (older
 * shape) a response message that speaks of credit. Null when there is none.
 */
export function creditWarningOf(res: { message?: string | null; result?: { creditWarning?: string | null } | null } | null | undefined): string | null {
  const w = res?.result?.creditWarning;
  if (w && w.trim()) return w.trim();
  const m = res?.message ?? '';
  return /credit (limit|hold|check)/i.test(m) ? m : null;
}

export const MAX_REASON_LENGTH = 500;

/** Contract §2 "A reason is required." (§6 / §8 pass their own text). */
export function reasonProblem(reason: string | null | undefined, missing = 'A reason is required.'): string | null {
  const r = (reason ?? '').trim();
  if (!r) return missing;
  if (r.length > MAX_REASON_LENGTH) return `The reason can be at most ${MAX_REASON_LENGTH} characters.`;
  return null;
}

/** Contract §2 — temporary limit: > 0, above a permanent limit that must exist, expiring after today, with a reason. */
export function tempLimitProblem(amount: number | null | undefined, expiry: string | null | undefined, reason: string | null | undefined,
                                 permanent: number | null | undefined, customerName: string, on = today()): string | null {
  if (amount === null || amount === undefined || !(amount > 0)) return 'The temporary limit must be greater than zero.';
  if (!permanent || permanent <= 0) return `${customerName} has no permanent credit limit; set one on the customer before granting a temporary one.`;
  if (amount <= permanent) return `The temporary limit must be above the permanent limit (${money(permanent)}).`;
  if (!expiry || expiry.slice(0, 10) <= on) return 'The expiry date must be after today.';
  return reasonProblem(reason);
}

// ── Dunning (BR-DN-*) ─────────────────────────────────────────────────────────

export const MAX_DUNNING_LEVELS = 4;
/** D-11 — the template placeholders the server fills in. */
export const DUNNING_PLACEHOLDERS = ['{CustomerName}', '{CustomerCode}', '{TotalOverdue}', '{OldestDueDate}', '{DunningLevel}', '{InvoiceList}', '{Amount}', '{DueDate}'];

export function blankLevel(n: number): DunningLevel {
  return {
    levelNumber: n, daysAfterPrevious: n === 1 ? 7 : 14, actionType: n >= MAX_DUNNING_LEVELS ? 'Manual' : 'Email',
    subjectTemplate: n === 1 ? 'Payment reminder — {TotalOverdue} overdue' : `Reminder ${n} — {TotalOverdue} overdue`,
    bodyTemplate: 'Dear {CustomerName},\n\nOur records show {TotalOverdue} overdue since {OldestDueDate}:\n\n{InvoiceList}\n\nPlease arrange payment.',
    includeInvoiceList: true, includeStatement: false, chargeAmount: null, escalationNote: null
  };
}

/** The first thing the server would refuse about a procedure; null when it would accept it. */
export function procedureProblem(d: DunningProcedureSave): string | null {
  const code = (d.code ?? '').trim();
  if (!code) return 'Enter the procedure code.';
  if (code.length > 20) return 'The code can be at most 20 characters.';
  if (!/^[A-Za-z0-9_-]+$/.test(code)) return 'The code may contain only letters, digits, - and _.';
  const name = (d.name ?? '').trim();
  if (!name) return 'Enter the procedure name.';
  if (name.length > 100) return 'The name can be at most 100 characters.';
  if (!Number.isInteger(d.graceDays) || d.graceDays < 0) return 'Grace days must be a whole number, 0 or more.';
  if (d.minimumAmount === null || d.minimumAmount === undefined || d.minimumAmount < 0) return 'The minimum amount must be 0 or more.';
  const levels = d.levels ?? [];
  if (!levels.length) return 'Add at least one level.';
  if (levels.length > MAX_DUNNING_LEVELS) return `At most ${MAX_DUNNING_LEVELS} levels.`;
  for (let i = 0; i < levels.length; i++) {
    const l = levels[i];
    const n = i + 1;
    if (l.levelNumber !== n) return `Dunning levels must be numbered 1 to ${levels.length} without gaps.`;
    if (!Number.isInteger(l.daysAfterPrevious) || l.daysAfterPrevious < (n === 1 ? 0 : 1)) {
      return n === 1 ? 'Level 1: days after the due date must be a whole number, 0 or more.' : `Level ${n}: days after the previous level must be at least 1.`;
    }
    if (!(l.subjectTemplate ?? '').trim()) return `Level ${n}: enter the subject.`;
    if ((l.subjectTemplate ?? '').length > 200) return `Level ${n}: the subject can be at most 200 characters.`;
    if (!(l.bodyTemplate ?? '').trim()) return `Level ${n}: enter the message.`;
    if (l.chargeAmount !== null && l.chargeAmount !== undefined && l.chargeAmount < 0) return `Level ${n}: the charge cannot be negative.`;
  }
  if (d.blockSalesAtLevel !== null && d.blockSalesAtLevel !== undefined && (d.blockSalesAtLevel < 1 || d.blockSalesAtLevel > levels.length)) {
    return `Block sales at level must be between 1 and ${levels.length}, or never.`;
  }
  return null;
}

/** Re-number after a removal / move. */
export function renumber(levels: DunningLevel[]): DunningLevel[] {
  return levels.map((l, i) => ({ ...l, levelNumber: i + 1 }));
}

// ── Tolerance rules / early-payment terms ─────────────────────────────────────

export function toleranceProblem(d: ToleranceRuleSave): string | null {
  const name = (d.name ?? '').trim();
  if (!name) return 'Enter the rule name.';
  if (name.length > 100) return 'The name can be at most 100 characters.';
  const all = [d.priceTolerancePercent, d.priceToleranceAmount, d.quantityTolerancePercent, d.totalTolerancePercent, d.totalToleranceAmount];
  if (all.some(v => v === null || v === undefined || !isFinite(v) || v < 0)) return 'Tolerances cannot be negative.';
  if ([d.priceTolerancePercent, d.quantityTolerancePercent, d.totalTolerancePercent].some(v => v > 100)) return 'A percentage cannot exceed 100.';
  return null;
}

/** BR-EP-004 — discount days < net days; "2/10 Net 30". */
export function termProblem(d: EarlyPaymentTermSave): string | null {
  const code = (d.code ?? '').trim();
  if (!code) return 'Enter the code.';
  if (code.length > 20) return 'The code can be at most 20 characters.';
  if (!(d.name ?? '').trim()) return 'Enter the name.';
  if (d.discountPercent === null || d.discountPercent === undefined || !(d.discountPercent > 0) || d.discountPercent >= 100) return 'The discount percent must be greater than 0 and less than 100.';
  if (!Number.isInteger(d.discountDays) || d.discountDays < 0) return 'Discount days must be a whole number, 0 or more.';
  if (!Number.isInteger(d.netDays) || d.netDays < 1) return 'Net days must be a whole number, 1 or more.';
  if (d.discountDays >= d.netDays) return 'Discount days must be fewer than net days.';
  return null;
}

/** "2/10 Net 30". */
export function termShort(t: { discountPercent: number; discountDays: number; netDays: number }): string {
  return `${round2(t.discountPercent)}/${t.discountDays} Net ${t.netDays}`;
}

// ── Payment runs (BR-PR-*) ────────────────────────────────────────────────────

export const RUN_STEPS = [
  { step: 1, title: 'Selection criteria', short: 'Criteria' },
  { step: 2, title: 'Review proposal', short: 'Review' },
  { step: 3, title: 'Approve', short: 'Approve' },
  { step: 4, title: 'Execute', short: 'Execute' }
];

/** The step a run's status belongs to. */
export function stepForStatus(s: string | null | undefined): number {
  switch (s) {
    case 'Draft': case 'Proposed': case 'Reviewing': return 2;
    case 'Approved': return 3;
    case 'Executing': case 'Completed': return 4;
    default: return 2;
  }
}

/** BR-PR-009/010 — editable while not yet approved; cancellable until executed. */
export function runEditable(s: string | null | undefined): boolean { return s === 'Draft' || s === 'Proposed' || s === 'Reviewing'; }
export function runCancellable(s: string | null | undefined): boolean { return runEditable(s) || s === 'Approved'; }

export function isRemoved(i: PaymentRunItem): boolean { return i.status === 'Removed'; }

export interface RunTotals { vendors: number; items: number; total: number; discount: number; net: number; }

/** The run's live totals over its batches (removed and blocked items are listed apart and never count). */
export function runTotals(r: PaymentRunDetail | null | undefined): RunTotals {
  const t: RunTotals = { vendors: 0, items: 0, total: 0, discount: 0, net: 0 };
  for (const g of r?.vendorGroups ?? []) {
    const live = g.items.filter(i => !isRemoved(i) && !i.isBlocked);
    if (!live.length) continue;
    t.vendors++;
    for (const i of live) {
      t.items++;
      t.total += Number(i.originalAmount || 0);
      t.discount += Number(i.discountAmount || 0);
      t.net += Number(i.netPaymentAmount || 0);
    }
  }
  t.total = round2(t.total); t.discount = round2(t.discount); t.net = round2(t.net);
  return t;
}

export function groupLive(g: PaymentRunVendorGroup): { items: number; net: number } {
  const live = g.items.filter(i => !isRemoved(i) && !i.isBlocked);
  return { items: live.length, net: round2(live.reduce((s, i) => s + Number(i.netPaymentAmount || 0), 0)) };
}

/** §8 — the blocked schedules that met the criteria (listed apart, BR-PR-002). */
export function blockedRows(r: PaymentRunDetail | null | undefined): { vendorName: string; item: PaymentRunItem }[] {
  return (r?.blockedItems ?? []).map(i => ({ vendorName: i.vendorName, item: i }));
}

/** Batches done (Paid / Failed) out of all — the progress of an executing run. */
export function runProgress(r: PaymentRunDetail | null | undefined): { done: number; paid: number; failed: number; total: number } {
  const groups = r?.vendorGroups ?? [];
  const paid = groups.filter(g => g.batchStatus === 'Paid').length;
  const failed = groups.filter(g => g.batchStatus === 'Failed').length;
  return { done: paid + failed, paid, failed, total: groups.length };
}

/** "3d late" / "due in 2d" / "due today". */
export function dueText(daysUntilDue: number | null | undefined): string {
  if (daysUntilDue === null || daysUntilDue === undefined) return '';
  if (daysUntilDue < 0) return `${-daysUntilDue}d late`;
  if (daysUntilDue === 0) return 'due today';
  return `due in ${daysUntilDue}d`;
}

/** Proposal criteria: a payment date not in the past and a due-date cutoff. */
export function criteriaProblem(paymentDate: string | null | undefined, cutoff: string | null | undefined, on = today()): string | null {
  if (!paymentDate) return 'Choose the payment date.';
  if (paymentDate < on) return 'The payment date cannot be before today.';
  if (!cutoff) return 'Choose the due-date cutoff.';
  return null;
}

// ── Credit / debit memo application (§4 / §9) ─────────────────────────────────

export interface ApplyRow { invoiceUuid: string; invoiceNumber: string; open: number; amount: number | null; }

/** The rows to send (amount > 0) and the first problem (§4 texts where the server has one). */
export function applicationProblem(rows: ApplyRow[], remaining: number, noteNumber: string): string | null {
  const used = rows.filter(r => r.amount && r.amount > 0);
  if (!used.length) return 'Enter an amount against at least one invoice.';
  for (const r of rows) {
    if (r.amount !== null && r.amount !== undefined && r.amount < 0) return `Invoice ${r.invoiceNumber}: the amount cannot be negative.`;
    if ((r.amount ?? 0) > round2(r.open) + 0.001) return `Invoice ${r.invoiceNumber} has only ${money(r.open)} open.`;
  }
  const total = round2(used.reduce((s, r) => s + Number(r.amount), 0));
  if (total > round2(remaining) + 0.001) return `${noteNumber} has only ${money(remaining)} left to apply.`;
  return null;
}

/** §4 — a memo: a reason and at least one line, each with a description and an amount > 0. */
export function creditMemoProblem(reason: string, lines: { description: string; amount: number | null }[]): string | null {
  if (!reason.trim()) return 'A reason is required.';
  if (!lines.length) return 'Add at least one line.';
  for (let i = 0; i < lines.length; i++) {
    if (!(lines[i].description ?? '').trim()) return `Line ${i + 1}: enter the description.`;
    if (!(Number(lines[i].amount) > 0)) return `Line ${i + 1}: the amount must be greater than zero.`;
  }
  return null;
}

// ── Matching ──────────────────────────────────────────────────────────────────

/** Price variance per unit and % of the PO price (BR-MT-003). */
export function priceVariance(poPrice: number, invPrice: number): { amount: number; percent: number | null } {
  const amount = Math.round((invPrice - poPrice) * 10000) / 10000;
  return { amount, percent: poPrice ? Math.round((amount / poPrice) * 10000) / 100 : null };
}

export function signedMoney(n: number | null | undefined): string {
  const v = round2(n ?? 0);
  return v > 0 ? '+' + money(v) : money(v);
}
