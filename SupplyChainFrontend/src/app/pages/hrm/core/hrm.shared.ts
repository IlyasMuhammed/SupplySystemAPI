import { toDateOnly } from '../../../shared/date-only';

// A49 — what every HRM page shares: error words, labels, tones and the enum spellings of docs/hrm/API-CONTRACT.md §0.1.

/** `BRANCH_CODE_TAKEN: A branch …` — the code at the start of a coded error (contract §0), else null. */
export function hrmErrorCode(err: unknown): string | null {
  const m = rawMessage(err);
  const hit = m ? /^([A-Z][A-Z0-9_]+):\s/.exec(m) : null;
  return hit ? hit[1] : null;
}

function rawMessage(err: unknown): string | null {
  const e = err as { error?: any; message?: unknown } | null;
  const body = e?.error;
  if (typeof body === 'string' && body.trim() && !body.trim().startsWith('<')) return body.trim();
  if (typeof body?.message === 'string' && body.message.trim()) return body.message.trim();
  const first = body?.errors?.[0]?.message ?? body?.result?.errors?.[0]?.message;
  if (typeof first === 'string' && first.trim()) return first.trim();
  return null;
}

/** The server's own words (without the code prefix), else a plain fallback. */
export function hrmError(err: unknown, fallback: string): string {
  const code = hrmErrorCode(err);
  if (code === 'HRM_ATTACHMENT_INVALID') return 'This file can only be saved by the person who uploaded it on this form. Choose the file again, then save.';
  if (code === 'ATTACHMENT_IN_USE') return 'That file is already saved on a record, so it was kept.';
  const m = rawMessage(err);
  if (m) return m.replace(/^[A-Z][A-Z0-9_]+:\s+/, '');
  const status = (err as { status?: number } | null)?.status;
  if (status === 0) return 'The server could not be reached. Check the connection and try again.';
  if (status === 403) return 'You do not have permission to do this.';
  return fallback;
}

/** A failed download (`responseType: 'blob'`) carries its JSON refusal as a Blob: read it so the user sees the server's words. */
export async function hrmErrorAsync(err: unknown, fallback: string): Promise<string> {
  const body = (err as { error?: unknown } | null)?.error;
  if (typeof Blob !== 'undefined' && body instanceof Blob) {
    try {
      return hrmError({ ...(err as object), error: JSON.parse(await body.text()) }, fallback);
    } catch {
      return hrmError({ ...(err as object), error: null }, fallback);
    }
  }
  return hrmError(err, fallback);
}

export function today(): string { return toDateOnly(new Date()); }

export function money(n: number | null | undefined): string {
  if (n === null || n === undefined) return '—';
  return Number(n).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
}

export function hours(minutes: number | null | undefined): string {
  const m = Math.max(0, Number(minutes ?? 0));
  return `${Math.floor(m / 60)}h ${String(m % 60).padStart(2, '0')}m`;
}

const LABEL_FIX: Record<string, string> = {
  OnNotice: 'On notice', OnHold: 'On hold', OnLeave: 'On leave', HalfDay: 'Half day', WeeklyOff: 'Weekly off', UnderReview: 'Under review', NoTarget: 'No posting required',
  NoShow: 'No show', DailyWage: 'Daily wage', ContractEnd: 'Contract end', ContractRenewal: 'Contract renewal', PayDate: 'Pay date', PeriodEnd: 'Period end',
  PayDateWithFallback: 'Pay date (period end if closed)', OwnAccount: 'Own account', CompanyAccount: 'Company account', WithPayroll: 'With payroll', SeparatePayment: 'Separate payment',
  IncomeTax: 'Income tax', CENTRAL: 'Central', BY_DEPARTMENT: 'By department', EmployerContribution: 'Employer contribution', PendingApproval: 'Pending approval',
  OfferLetter: 'Offer letter', ExperienceCertificate: 'Experience certificate', SalaryCertificate: 'Salary certificate', PayrollRun: 'Payroll run', PayrollPayment: 'Payroll payment',
  M: 'Male', F: 'Female'
};

/** `UnderReview` → `Under review`. */
export function hrmLabel(s: string | null | undefined): string {
  if (s === null || s === undefined || s === '') return '';
  return LABEL_FIX[s] ?? s.replace(/([a-z])([A-Z])/g, (_, a, b) => `${a} ${b.toLowerCase()}`);
}

const TONES: Record<string, string> = {
  Mapped: 'ok', Reviewed: 'ok', Confirmed: 'ok', 'Not mapped': 'wn', 'Not reviewed': 'wn', Estimated: 'wn',
  Active: 'ok', Present: 'ok', Approved: 'ok', Posted: 'ok', Paid: 'ok', Hired: 'ok', Accepted: 'ok', Passed: 'ok', Completed: 'ok', Applied: 'ok', Acknowledged: 'ok', Achieved: 'ok', Deducted: 'ok', Closed: '',
  Probation: 'in', Open: 'in', Enrolled: 'in', Calculated: 'in', Finalized: 'in', Disbursed: 'in', Sent: 'in', Submitted: 'in', Interview: 'in', Offer: 'in', Screening: 'in', OnLeave: 'in', Holiday: 'in', WeeklyOff: '',
  Pending: 'wn', PendingApproval: 'wn', UnderReview: 'wn', OnNotice: 'wn', Late: 'wn', HalfDay: 'wn', Held: 'wn', OnHold: 'wn', Candidate: 'wn', Waived: 'wn',
  Rejected: 'er', Failed: 'er', Absent: 'er', Terminated: 'er', Suspended: 'er', Cancelled: 'er', Reversed: 'er', Declined: 'er', Missed: 'er', NoShow: 'er', Withdrawn: 'er', Superseded: '', Archived: '', Dropped: ''
};

export function hrmTone(s: string | null | undefined): string { return (s && TONES[s]) || ''; }

export interface Opt { label: string; value: any; }

/** Options from enum spellings, labelled for people; the value is what the API takes. */
export function opts(list: string): Opt[] { return list.split(' ').map(v => ({ label: hrmLabel(v), value: v })); }

export const ENUMS = {
  employeeStatus: opts('Candidate Probation Active OnNotice Suspended Terminated Archived'),
  createStatus: opts('Candidate Probation Active'),
  gender: opts('M F Other'),
  marital: opts('Single Married Widowed Divorced Other'),
  departure: opts('Resignation Retirement Termination ContractEnd Death Other'),
  employmentType: opts('Permanent Contract Probation Intern DailyWage'),
  changeReason: opts('Hire Transfer Promotion Confirmation Demotion ContractRenewal Correction Other'),
  bankMethod: opts('Bank Cash Cheque'),
  costMode: opts('CENTRAL BY_DEPARTMENT'),
  postDatePolicy: opts('PayDate PeriodEnd PayDateWithFallback'),
  documentType: opts('CNIC CONTRACT CERTIFICATE RESUME PHOTO OTHER'),
  dayStatus: opts('Present Late HalfDay Absent OnLeave Holiday WeeklyOff'),
  eventType: opts('IN OUT UNKNOWN'),
  approval: opts('Pending Approved Rejected'),
  accrual: opts('Annual Monthly None'),
  leaveStatus: opts('Pending Approved Rejected Cancelled'),
  componentType: opts('Earning Deduction EmployerContribution Result'),
  calcMethod: opts('Fixed Percent Formula'),
  statutoryType: opts('EOBI PF SS GRATUITY'),
  vacancyStatus: opts('Open OnHold Closed'),
  stage: opts('New Screening Interview Offer Hired Rejected'),
  interviewResult: opts('Pending Passed Failed NoShow'),
  offerStatus: opts('Draft Sent Accepted Declined Withdrawn'),
  reviewPeriod: opts('Annual Quarterly'),
  cycleStatus: opts('Draft Open Closed'),
  reviewStatus: opts('Draft Submitted Acknowledged'),
  goalStatus: opts('Open Achieved Missed Dropped'),
  enrollmentStatus: opts('Enrolled Completed Cancelled'),
  runStatus: opts('Draft Calculated UnderReview Approved Finalized Posted Paid Cancelled Reversed'),
  loanType: opts('Advance Personal Housing'),
  loanStatus: opts('Draft Approved Disbursed Closed Rejected Cancelled'),
  settlementStatus: opts('Draft Approved Posted Paid Cancelled'),
  incrementStatus: opts('Draft Approved Applied Rejected'),
  letterKind: opts('OfferLetter ExperienceCertificate SalaryCertificate Custom'),
  claimStatus: opts('Draft Submitted Approved Paid Rejected'),
  paymentMode: opts('OwnAccount CompanyAccount'),
  settlementMethod: opts('WithPayroll SeparatePayment'),
  remittanceType: opts('IncomeTax EOBI PF SS'),
  paidFrom: opts('Bank Cash')
};

/** A CSV cell with no spreadsheet formula risk is not needed here: every HRM export is the server's. Weekly-off flags (§6.1): Sun=1 … Sat=64. */
export const WEEK_DAYS: Opt[] = [
  { label: 'Sun', value: 1 }, { label: 'Mon', value: 2 }, { label: 'Tue', value: 4 }, { label: 'Wed', value: 8 }, { label: 'Thu', value: 16 }, { label: 'Fri', value: 32 }, { label: 'Sat', value: 64 }
];

export function weekDaysText(flags: number | null | undefined): string {
  const f = Number(flags ?? 0);
  return WEEK_DAYS.filter(d => (f & d.value) !== 0).map(d => d.label).join(', ') || '—';
}

export function fullYearOptions(back = 3, forward = 1): Opt[] {
  const y = new Date().getFullYear();
  const out: Opt[] = [];
  for (let i = y + forward; i >= y - back; i--) out.push({ label: String(i), value: i });
  return out;
}

export function uuid(): string {
  const c = globalThis.crypto as Crypto | undefined;
  if (c?.randomUUID) return c.randomUUID();
  return 'xxxxxxxx-xxxx-4xxx-8xxx-xxxxxxxxxxxx'.replace(/x/g, () => Math.floor(Math.random() * 16).toString(16));
}
