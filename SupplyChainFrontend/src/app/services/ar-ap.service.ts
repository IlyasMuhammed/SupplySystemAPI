import { HttpClient, HttpParams, HttpResponse } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable, map } from 'rxjs';
import { environment } from '../../environments/environment';

// A48 — AR & AP sub-ledgers (docs/ar-ap-subledgers/API-CONTRACT.md + the models as built in SMS.Modules.Finance.ArAp).
// Figures come from the SCM Finance documents (D-1), never from GL lines. Uuids only, dates yyyy-MM-dd, totals in the
// base currency (`baseCurrencyCode`, D-13); a document's own amounts carry `currencyCode`. Enum spellings §0.1,
// permission codes §0.2. Wire shapes are typed exactly (`*Wire` where a page reads a mapped view); the mappers below read
// that one shape — the defensive dual-shape reading of the first round is gone now the shapes are pinned (D-FE-3).

export const AR_ROUTE = '/portal/pages/finance/ar';
export const AP_ROUTE = '/portal/pages/finance/ap';
export const CREDIT_ROUTE = '/portal/pages/finance/ar/credit';
export const STATEMENT_ROUTE = '/portal/pages/finance/ar/statements';
export const CREDIT_MEMOS_ROUTE = '/portal/pages/finance/ar/credit-memos';
export const DUNNING_PROCEDURES_ROUTE = '/portal/pages/finance/ar/dunning/procedures';
export const DUNNING_RUNS_ROUTE = '/portal/pages/finance/ar/dunning/runs';
export const SCHEDULES_ROUTE = '/portal/pages/finance/ap/payment-schedules';
export const PAYMENT_RUNS_ROUTE = '/portal/pages/finance/ap/payment-runs';
export const MATCHING_ROUTE = '/portal/pages/finance/ap/matching';
export const AP_SETTINGS_ROUTE = '/portal/pages/finance/ap/settings';
export const VENDOR_STATEMENT_ROUTE = '/portal/pages/finance/ap/vendor-statement';
export const DEBIT_MEMOS_ROUTE = '/portal/pages/finance/ap/debit-memos';

/** Contract §0.2 (D-14). */
export const AR_AP_PERMS = {
  AR_VIEW: 'AR_VIEW',
  AR_STATEMENT_SEND: 'AR_STATEMENT_SEND',
  AR_CREDIT_MEMO: 'AR_CREDIT_MEMO',
  CREDIT_CHECK: 'CREDIT_CHECK',
  CREDIT_MANAGE: 'CREDIT_MANAGE',
  CREDIT_OVERRIDE: 'CREDIT_OVERRIDE',
  DUNNING_EXECUTE: 'DUNNING_EXECUTE',
  DUNNING_ADMIN: 'DUNNING_ADMIN',
  AP_VIEW: 'AP_VIEW',
  AP_EXECUTE: 'AP_EXECUTE',
  AP_DEBIT_MEMO: 'AP_DEBIT_MEMO',
  MATCH_EXECUTE: 'MATCH_EXECUTE',
  MATCH_OVERRIDE: 'MATCH_OVERRIDE',
  MATCH_APPROVE: 'MATCH_APPROVE',
  PAYMENT_RUN_PROPOSE: 'PAYMENT_RUN_PROPOSE',
  PAYMENT_RUN_EDIT: 'PAYMENT_RUN_EDIT',
  PAYMENT_RUN_APPROVE: 'PAYMENT_RUN_APPROVE',
  PAYMENT_RUN_EXECUTE: 'PAYMENT_RUN_EXECUTE',
  PAYMENT_RUN_ADMIN: 'PAYMENT_RUN_ADMIN',
  ADMIN: 'FINANCE_AR_AP_ADMIN'
} as const;

const P = AR_AP_PERMS;
const RUN_CODES = [P.PAYMENT_RUN_PROPOSE, P.PAYMENT_RUN_EDIT, P.PAYMENT_RUN_APPROVE, P.PAYMENT_RUN_EXECUTE, P.PAYMENT_RUN_ADMIN];
const MATCH_CODES = [P.MATCH_EXECUTE, P.MATCH_OVERRIDE, P.MATCH_APPROVE];

// Route / menu gates = the codes of the page's main read in the contract (any of them).
/** §5 — summary / aging / DSO. */
export const AR_READ: string[] = [P.AR_VIEW];
/** §2 — the list (`credit/exposures`) and the history take AR_VIEW or CREDIT_MANAGE (CREDIT_CHECK alone reads neither). */
export const CREDIT_READ: string[] = [P.AR_VIEW, P.CREDIT_MANAGE];
/** §5 — statement view (AR_VIEW) or the send / batch actions (AR_STATEMENT_SEND). */
export const STATEMENT_READ: string[] = [P.AR_VIEW, P.AR_STATEMENT_SEND];
/** §4 — open credits / memo / applications. */
export const CREDIT_MEMO_READ: string[] = [P.AR_VIEW, P.AR_CREDIT_MEMO];
/** §3 — procedures list. */
export const DUNNING_PROCEDURE_READ: string[] = [P.AR_VIEW, P.DUNNING_EXECUTE, P.DUNNING_ADMIN, P.ADMIN];
/** §3 — runs list. */
export const DUNNING_RUN_READ: string[] = [P.AR_VIEW, P.DUNNING_EXECUTE, P.DUNNING_ADMIN];
/** §5 — AP summary / aging / DPO. */
export const AP_READ: string[] = [P.AP_VIEW];
/** §7 — schedules. */
export const SCHEDULE_READ: string[] = [P.AP_VIEW, P.AP_EXECUTE, ...RUN_CODES];
/** §8 — runs. */
export const PAYMENT_RUN_READ: string[] = [P.AP_VIEW, ...RUN_CODES];
/** §6 — pending list and detail. */
export const MATCH_READ: string[] = [P.AP_VIEW, ...MATCH_CODES];
/** §5 — the vendor statement GET is AP_VIEW only (reconciling on the page also needs AP_EXECUTE). */
export const VENDOR_STATEMENT_READ: string[] = [P.AP_VIEW];
/** §9 — debit memo applications. */
export const DEBIT_MEMO_READ: string[] = [P.AP_VIEW, P.AP_DEBIT_MEMO];
/** §6 / §7 — tolerance rules (AP_VIEW, MATCH_*, admin) and early-payment terms (AP_VIEW, admin). */
export const AP_SETTINGS_READ: string[] = [P.AP_VIEW, ...MATCH_CODES, P.ADMIN];
export const TOLERANCE_READ: string[] = [P.AP_VIEW, ...MATCH_CODES, P.ADMIN];
export const TERMS_READ: string[] = [P.AP_VIEW, P.ADMIN];
/** §1.4 — the A48 settings GET takes any A48 code. */
export const ALL_AR_AP_CODES: string[] = Object.values(AR_AP_PERMS);
/** The AR & AP settings page: credit control mode (§1.4), tolerance rules (§6), early-payment terms (§7). */
export const ARAP_SETTINGS_READ: string[] = [P.AR_VIEW, P.CREDIT_MANAGE, ...AP_SETTINGS_READ];
/** §1.1 — the profile GET, per role. */
export const PROFILE_READ_CUSTOMER: string[] = [P.AR_VIEW, P.CREDIT_CHECK, P.CREDIT_MANAGE, P.ADMIN];
export const PROFILE_READ_VENDOR: string[] = [P.AP_VIEW, P.AP_EXECUTE, P.ADMIN];

// ── Enums (§0.1) ──────────────────────────────────────────────────────────────

export type PartnerRole = 'Customer' | 'Vendor';
export type RiskTier = 'Low' | 'Medium' | 'High';
export const RISK_TIERS: RiskTier[] = ['Low', 'Medium', 'High'];
export type PaymentPriority = 'High' | 'Normal' | 'Low';
export const PAYMENT_PRIORITIES: PaymentPriority[] = ['High', 'Normal', 'Low'];
export type CreditControlMode = 'Off' | 'Warn' | 'Block';
export const CREDIT_CONTROL_MODES: CreditControlMode[] = ['Off', 'Warn', 'Block'];
/** AP: the SupplierPayment codes. */
export const AP_PAYMENT_METHODS = ['BANK_TRANSFER', 'ONLINE_WIRE', 'CHEQUE', 'CASH'] as const;
/** AR: CustomerPaymentMethods.All. */
export const AR_PAYMENT_METHODS = ['CASH', 'CHEQUE', 'BANK_TRANSFER', 'CARD', 'ONLINE', 'MOBILE'] as const;
/** §9 — a DebitReason code (upper-cased by the server); the usual ones. */
export const DEBIT_MEMO_REASONS = ['PRICE_DIFFERENCE', 'SHORT_SUPPLY', 'QUALITY_FAIL', 'DAMAGED', 'REBATE', 'OTHER'] as const;
export type AgingBasis = 'DueDate' | 'DocumentDate';
export type StatementType = 'Activity' | 'OpenItems';
export type DunningActionType = 'Email' | 'Letter' | 'Manual';
export const DUNNING_ACTION_TYPES: DunningActionType[] = ['Email', 'Letter', 'Manual'];
export type MatchType = 'ThreeWay' | 'TwoWay';
export type LineMatchResult = 'Matched' | 'PartialMatch' | 'Mismatch';
export const LINE_MATCH_RESULTS: LineMatchResult[] = ['Matched', 'PartialMatch', 'Mismatch'];
export type PaymentRunStatus = 'Draft' | 'Proposed' | 'Reviewing' | 'Approved' | 'Executing' | 'Completed' | 'Cancelled';
export const PAYMENT_RUN_STATUSES: PaymentRunStatus[] = ['Draft', 'Proposed', 'Reviewing', 'Approved', 'Executing', 'Completed', 'Cancelled'];

// ── Shared ────────────────────────────────────────────────────────────────────

export interface PartnerRef { uuid: string; code?: string | null; name: string; email?: string | null; }
export interface Page<T> { data: T[]; totalRecords: number; page: number; pageSize: number; totalPages?: number; }

// ── §1 Partner financial profile + A48 settings (PartnerFinancialProfileModel) ─

export interface PartnerFinancialProfile {
  uuid?: string | null;
  partnerUuid: string;
  role: PartnerRole | string;
  partnerCode?: string | null;
  partnerName?: string | null;
  partnerEmail?: string | null;
  partnerIsActive?: boolean;
  creditLimit?: number | null;
  temporaryCreditLimit?: number | null;
  temporaryCreditExpiry?: string | null;
  temporaryLimitActive?: boolean;
  effectiveCreditLimit?: number | null;
  creditHold: boolean;
  creditHoldReason?: string | null;
  creditHoldAt?: string | null;
  creditHoldByName?: string | null;
  riskTier: RiskTier | string;
  dunningProcedureUuid?: string | null;
  dunningProcedureCode?: string | null;
  currentDunningLevel: number;
  lastDunningDate?: string | null;
  blockSalesAtDunning?: boolean;
  excludeFromDunning?: boolean;
  defaultPaymentMethod?: string | null;
  paymentPriority?: PaymentPriority | string;
  preferredPaymentMethod?: string | null;
  withholdingTaxApplicable?: boolean;
  earlyPaymentTermUuid?: string | null;
  earlyPaymentTermCode?: string | null;
  paymentTermsDays?: number | null;
  lastPaymentDate?: string | null;
  averageDaysToPay?: number | null;
  totalOverdueAmount?: number | null;
  statisticsUpdatedAt?: string | null;
  notes?: string | null;
  createdAt?: string;
  modifiedAt?: string | null;
  modifiedByName?: string | null;
  rowVersion?: string | null;
}

/** §1.2 — every field optional (absent = unchanged). */
export interface PartnerFinancialProfileSave {
  riskTier?: RiskTier | string | null;
  dunningProcedureUuid?: string | null;
  clearDunningProcedure?: boolean;
  blockSalesAtDunning?: boolean | null;
  excludeFromDunning?: boolean | null;
  defaultPaymentMethod?: string | null;
  paymentPriority?: PaymentPriority | string | null;
  preferredPaymentMethod?: string | null;
  withholdingTaxApplicable?: boolean | null;
  earlyPaymentTermUuid?: string | null;
  clearEarlyPaymentTerm?: boolean;
  notes?: string | null;
  rowVersion?: string | null;
}

export interface ArApSettings { creditControlMode: CreditControlMode | string; }

// ── §2 Credit control (CreditExposureModel, CreditHistoryModel) ───────────────

export interface CreditExposure {
  customerUuid: string;
  customerName: string;
  customerCode?: string | null;
  baseCurrencyCode?: string | null;
  creditLimit?: number | null;
  temporaryCreditLimit?: number | null;
  temporaryCreditExpiry?: string | null;
  effectiveCreditLimit?: number | null;
  openOrdersAmount: number;
  openInvoicesAmount: number;
  totalExposure: number;
  availableCredit?: number | null;
  isOverLimit: boolean;
  isOnHold: boolean;
  riskTier: RiskTier | string;
  creditControlMode: CreditControlMode | string;
}

export interface CreditExposureQuery { overLimit?: boolean | null; onHold?: boolean | null; riskTier?: string | null; search?: string | null; page?: number; pageSize?: number; }

/** §10.2 CreditCheckResult. */
export interface CreditCheckResult {
  mode: string;
  approved: boolean;
  blockReason?: string | null;
  warning?: string | null;
  onHold: boolean;
  effectiveLimit?: number | null;
  openOrdersAmount: number;
  openInvoicesAmount: number;
  exposure: number;
  requestedAmount: number;
  projectedExposure: number;
  availableCredit?: number | null;
  baseCurrencyCode?: string | null;
}

export type CreditHistoryAction = 'Hold' | 'Release' | 'TempIncrease' | 'TempExpired' | 'RiskTier' | 'Override' | 'DunningHold' | 'DunningReset';

export interface CreditHistoryEntry {
  uuid: string;
  action: CreditHistoryAction | string;
  reason: string;
  previousValue?: string | null;
  newValue?: string | null;
  amount?: number | null;
  expiryDate?: string | null;
  sourceDocumentType?: string | null;
  sourceDocumentUuid?: string | null;
  sourceDocumentNumber?: string | null;
  requestedByName?: string | null;
  requestedAt: string;
  status: string;
}

export interface TemporaryLimitRequest { amount: number | null; expiryDate: string | null; reason: string; }

// ── §3 Dunning (DunningProcedureModel, DunningRunModel, DunningHistoryModel) ──

export interface DunningLevel {
  uuid?: string | null;
  levelNumber: number;
  daysAfterPrevious: number;
  subjectTemplate: string;
  bodyTemplate: string;
  includeInvoiceList: boolean;
  includeStatement: boolean;
  chargeAmount?: number | null;
  actionType: DunningActionType | string;
  escalationNote?: string | null;
}

export interface DunningProcedure {
  uuid: string;
  code: string;
  name: string;
  description?: string | null;
  isDefault: boolean;
  isActive: boolean;
  graceDays: number;
  minimumAmount: number;
  blockSalesAtLevel?: number | null;
  levels: DunningLevel[];
}

export type DunningProcedureSave = Omit<DunningProcedure, 'uuid'>;

export interface DunningRunItem {
  uuid: string;
  customerUuid: string;
  customerName: string;
  customerCode?: string | null;
  procedureCode?: string | null;
  dunningLevel: number;
  previousLevel: number;
  overdueAmount: number;
  currencySummary?: string | null;
  openItemCount: number;
  oldestDueDate: string;
  daysOverdue: number;
  actionType: DunningActionType | string;
  chargeAmount?: number | null;
  chargeDocumentUuid?: string | null;
  chargeDocumentNumber?: string | null;
  sentTo?: string | null;
  sentAt?: string | null;
  subject?: string | null;
  body?: string | null;
  invoiceUuids: string[];
  /** Pending / Sent / Skipped / Failed / Manual. */
  status: string;
  skipReason?: string | null;
  failureReason?: string | null;
}

export interface DunningRunSummary {
  uuid: string;
  runNumber: string;
  runDate: string;
  asOfDate: string;
  status: string;
  currencyCode?: string | null;
  totalCustomers: number;
  totalAmount: number;
  itemsProcessed: number;
  itemsSent: number;
  itemsSkipped: number;
  itemsFailed: number;
  createdAt?: string;
  createdByName?: string | null;
  executedAt?: string | null;
  executedByName?: string | null;
  completedAt?: string | null;
  cancelledAt?: string | null;
  cancelReason?: string | null;
  autoResetCount: number;
}

export interface DunningRunDetail extends DunningRunSummary { items: DunningRunItem[]; }

export interface DunningHistoryEvent {
  at: string;
  /** Notice | Reset | Hold. */
  kind: string;
  level?: number | null;
  runNumber?: string | null;
  runUuid?: string | null;
  status?: string | null;
  amount?: number | null;
  detail?: string | null;
  byName?: string | null;
}

export interface DunningHistory {
  customerUuid: string;
  customerName: string;
  currentDunningLevel: number;
  lastDunningDate?: string | null;
  procedureCode?: string | null;
  excludeFromDunning: boolean;
  events: DunningHistoryEvent[];
}

// ── §4 AR credit memos (CreditMemoModel) ──────────────────────────────────────

export interface CreditMemoLineRequest { description: string; amount: number; taxCodeUuid?: string | null; invoiceLineUuid?: string | null; }
export interface CreateCreditMemoRequest {
  customerUuid: string;
  originalInvoiceUuid?: string | null;
  date?: string | null;
  currencyCode?: string | null;
  reason: string;
  lines: CreditMemoLineRequest[];
  applyToOriginal?: boolean | null;
}
export interface CreditApplicationRequest { invoiceUuid: string; amount: number; }

export interface CreditApplication {
  uuid: string;
  creditNoteUuid: string;
  creditNoteNumber: string;
  invoiceUuid: string;
  invoiceNumber: string;
  amount: number;
  appliedAt: string;
  appliedByName?: string | null;
  reversedAt?: string | null;
}

export interface CreditMemoLine { lineNo: number; description: string; amount: number; taxAmount: number; taxPercent: number; taxCode?: string | null; }

export interface CreditMemo {
  uuid: string;
  creditNoteNumber: string;
  /** AR_CREDIT_MEMO, or the A38 POS / sales source of an older note. */
  sourceType: string;
  customerUuid: string;
  customerName: string;
  originalInvoiceUuid?: string | null;
  originalInvoiceNumber?: string | null;
  date: string;
  currencyCode: string;
  subtotal: number;
  taxAmount: number;
  total: number;
  applied: number;
  refunded: number;
  remaining: number;
  remainingBase?: number | null;
  notes?: string | null;
  lines: CreditMemoLine[];
  applications: CreditApplication[];
}

// ── §5 Inquiry, aging, statements, reconciliation (ArApReportingModels.cs) ────

export interface AgingQuery {
  asOfDate?: string | null;
  /** Boundaries in days, e.g. [30, 60, 90, 120]. */
  buckets?: number[] | null;
  basis?: AgingBasis | null;
  /** customerUuid (AR) / vendorUuid (AP). */
  partnerUuid?: string | null;
  currencyCode?: string | null;
  includeDetails?: boolean;
  sortBy?: 'Partner' | 'TotalOutstanding' | 'TotalOverdue' | 'OldestBucket' | 'OldestDate' | null;
  sortDirection?: 'Asc' | 'Desc' | null;
  page?: number;
  pageSize?: number;
}

export interface AgingDetailLine {
  documentType: string;
  documentUuid: string;
  documentNumber: string;
  reference?: string | null;
  partnerId?: string;
  partnerName?: string | null;
  documentDate: string;
  dueDate: string;
  currencyCode: string;
  originalAmount: number;
  openAmount: number;
  openAmountBase: number;
  exchangeRate?: number;
  daysOverdue: number;
  ageDays?: number;
  bucketIndex?: number;
  bucketLabel: string;
  status?: string | null;
  rateMissing?: boolean;
}

/** The wire line (`partnerId/partnerName/partnerCode`). */
export interface AgingPartnerLineWire {
  partnerId: string;
  partnerName?: string | null;
  partnerCode?: string | null;
  bucketAmounts: number[];
  totalOutstanding: number;
  totalOverdue: number;
  unappliedCredits: number;
  netBalance: number;
  openItemCount: number;
  oldestOpenDate?: string | null;
  daysBeyondTerms?: number | null;
  oldestBucket: number;
  details?: AgingDetailLine[] | null;
}

/** The page's line: the wire line plus its partner as a ref. */
export interface AgingPartnerLine extends Partial<Omit<AgingPartnerLineWire, 'bucketAmounts' | 'totalOutstanding'>> {
  partner: PartnerRef;
  bucketAmounts: number[];
  totalOutstanding: number;
  onHold?: boolean | null;
}

export interface AgingTotals {
  bucketTotals: number[]; grandTotal: number; totalPartners: number; totalOpenItems: number;
  totalOverdue?: number; unappliedCredits?: number | null; netBalance?: number | null;
}

export interface AgingCurrencyTotal { currencyCode: string; bucketAmounts: number[]; total: number; totalBase: number; openItemCount: number; }

export interface AgingReport {
  reportType?: string;
  asOfDate: string;
  basis?: string;
  bucketDays: number[];
  bucketLabels: string[];
  baseCurrencyCode?: string | null;
  lines: AgingPartnerLine[];
  totals: AgingTotals;
  byCurrency?: AgingCurrencyTotal[];
  dsoOrDpo?: number;
  totalOutstanding: number;
  page?: number;
  pageSize?: number;
  totalRecords?: number;
  totalPages?: number;
  generatedAt?: string;
}

export interface ArSummary {
  asOfDate: string;
  baseCurrencyCode: string;
  periodDays: number;
  totalOutstanding: number;
  totalOverdue: number;
  totalCurrent: number;
  unappliedCredits: number;
  netReceivable: number;
  totalCustomersWithBalance: number;
  totalCustomersOverdue: number;
  dso: number;
  daysBeyondTerms: number;
  collectionRate: number;
  invoicedInPeriod: number;
  collectedInPeriod: number;
  agingBuckets: number[];
  agingLabels: string[];
}

export interface ApSummary {
  asOfDate: string;
  baseCurrencyCode: string;
  periodDays: number;
  totalOutstanding: number;
  totalOverdue: number;
  totalCurrent: number;
  totalDueThisWeek: number;
  totalDueThisMonth: number;
  unappliedCredits: number;
  netPayable: number;
  totalVendorsWithBalance: number;
  totalVendorsOverdue: number;
  dpo: number;
  discountOpportunityAmount: number;
  agingBuckets: number[];
  agingLabels: string[];
}

export interface PartnerCurrencyBalance { currencyCode: string; totalOpen: number; totalCredits: number; netBalance: number; netBalanceBase: number; }

export interface PartnerBalance {
  partnerId: string;
  partnerName?: string | null;
  partnerRole: string;
  asOfDate: string;
  baseCurrencyCode: string;
  totalOpen: number;
  totalCredits: number;
  netBalance: number;
  totalOverdue: number;
  openItemCount: number;
  oldestOpenDate?: string | null;
  daysOldest?: number | null;
  byCurrency: PartnerCurrencyBalance[];
}

/** One open item (signed: credits negative). */
export interface PartnerOpenItem {
  documentType: string;
  documentUuid: string;
  documentNumber: string;
  reference?: string | null;
  documentDate: string;
  dueDate: string;
  currencyCode: string;
  originalAmount: number;
  openAmount: number;
  openAmountBase: number;
  daysOutstanding: number;
  daysOverdue: number;
  isOverdue: boolean;
  isCredit: boolean;
  status?: string | null;
  rateMissing?: boolean;
}

export interface StatementQuery { fromDate: string; toDate: string; type: StatementType; currencyCode?: string | null; includeAging?: boolean; }

export interface StatementLine {
  date: string;
  documentType: string;
  entryNumber: string;
  reference?: string | null;
  description: string;
  dueDate?: string | null;
  debit?: number | null;
  credit?: number | null;
  runningBalance: number;
  status?: string | null;
}

export interface StatementCurrencySection {
  currencyCode: string;
  openingBalance: number;
  totalDebits: number;
  totalCredits: number;
  closingBalance: number;
  lines: StatementLine[];
  /** Open items at ToDate per bucket (due basis), this currency; null when not asked. */
  aging?: number[] | null;
}

export interface StatementResult {
  statementId?: string | null;
  partnerType: string;
  partnerId: string;
  partnerName?: string | null;
  partnerCode?: string | null;
  partnerEmail?: string | null;
  partnerAddress?: string | null;
  companyName?: string | null;
  fromDate: string;
  toDate: string;
  type: StatementType | string;
  baseCurrencyCode: string;
  sections: StatementCurrencySection[];
  openingBalanceBase: number;
  closingBalanceBase: number;
  lineCount: number;
  agingLabels: string[];
  generatedAt: string;
}

export interface StatementEmailRequest { emailTo?: string | null; subject?: string | null; message?: string | null; }
export interface StatementEmailResult { statementId?: string | null; emailedTo: string; emailedAt: string; closingBalanceBase: number; }

export interface BatchStatementRequest { from: string; to: string; type: StatementType; minimumBalance?: number | null; sendEmail: boolean; includeAging?: boolean; partnerIds?: string[] | null; }

export interface BatchStatementItem {
  partnerId: string;
  partnerName?: string | null;
  statementId?: string | null;
  closingBalanceBase: number;
  emailed: boolean;
  emailedTo?: string | null;
  /** GENERATED / EMAILED / NO_EMAIL / FAILED. */
  outcome: string;
  error?: string | null;
}

export interface BatchStatementResult { considered: number; generated: number; emailed: number; skipped: number; noEmail: number; failed: number; items: BatchStatementItem[]; }

export interface StatementLogEntry {
  uuid: string;
  partnerUuid: string;
  partnerName?: string | null;
  partnerRole: string;
  statementType: StatementType | string;
  fromDate: string;
  toDate: string;
  currencyCode?: string | null;
  openingBalance: number;
  closingBalance: number;
  lineCount: number;
  format?: string | null;
  /** NotSent / Sent / Failed. */
  emailStatus: string;
  emailedTo?: string | null;
  emailedAt?: string | null;
  emailError?: string | null;
  batchUuid?: string | null;
  generatedAt: string;
  generatedByName?: string | null;
}

export interface VendorClaimedItem { reference: string; date: string; amount: number; }
export interface VendorReconciliationRequest { asOfDate: string; vendorClaimedBalance: number; currencyCode?: string | null; vendorItems?: VendorClaimedItem[] | null; tolerance?: number | null; }
export interface ReconciliationDifference {
  /** InSMSNotVendor / InVendorNotSMS / AmountDifference. */
  type: string;
  documentType?: string | null;
  entryNumber?: string | null;
  reference?: string | null;
  date?: string | null;
  smsAmount?: number | null;
  vendorAmount?: number | null;
  difference?: number | null;
}
export interface VendorReconciliationResult {
  vendorId: string;
  vendorName?: string | null;
  asOfDate: string;
  currencyCode: string;
  smsBalance: number;
  vendorClaimedBalance: number;
  difference: number;
  matchedCount: number;
  differences: ReconciliationDifference[];
  isReconciled: boolean;
}

// ── §6 Matching (MatchDetailModel, MatchSummaryModel) ─────────────────────────

export interface MatchLine {
  lineNo: number;
  itemDescription: string;
  poLineUuid?: string | null;
  grnLineUuid?: string | null;
  poQuantity?: number | null;
  poUnitPrice?: number | null;
  poLineTotal?: number | null;
  grnQuantity?: number | null;
  invoiceQuantity: number;
  invoiceUnitPrice: number;
  invoiceLineTotal: number;
  quantityVariance?: number | null;
  priceVariance?: number | null;
  priceVariancePercent?: number | null;
  totalVariance?: number | null;
  /** Pending / Matched / QuantityMismatch / PriceMismatch / Unmatched / Overridden. */
  lineStatus: string;
}

export interface MatchDetail {
  invoiceUuid: string;
  invoiceNumber: string;
  supplierInvoiceNo?: string | null;
  vendorUuid: string;
  vendorName: string;
  poUuid?: string | null;
  poNumber?: string | null;
  grnUuid?: string | null;
  grnNumber?: string | null;
  /** The existing invoice MatchStatus: Pending / Matched / Variance / Approved / Rejected / Reversed. */
  matchStatus: string;
  matchType?: MatchType | string | null;
  lineMatchResult?: LineMatchResult | string | null;
  withinTolerance?: boolean | null;
  variancePercent?: number | null;
  varianceAmount: number;
  toleranceRule?: { uuid: string; name: string } | null;
  autoApproved: boolean;
  matchedAt?: string | null;
  matchedByName?: string | null;
  overrideReason?: string | null;
  overriddenAt?: string | null;
  overriddenByName?: string | null;
  currencyCode: string;
  subtotal: number;
  totalAmount: number;
  lines: MatchLine[];
  /** BR-MT-005 — an auto-approval was refused; the match stands. */
  autoApproveMessage?: string | null;
}

export interface MatchSummary {
  invoiceUuid: string;
  invoiceNumber: string;
  supplierInvoiceNo?: string | null;
  vendorUuid: string;
  vendorName: string;
  poUuid?: string | null;
  poNumber?: string | null;
  matchStatus: string;
  matchType?: string | null;
  lineMatchResult?: string | null;
  withinTolerance?: boolean | null;
  variancePercent?: number | null;
  varianceAmount: number;
  overridden: boolean;
  currencyCode: string;
  totalAmount: number;
  invoiceDate: string;
  matchedAt?: string | null;
}

export interface MatchFilter { result?: string | null; vendorUuid?: string | null; search?: string | null; page?: number; pageSize?: number; }

export interface ToleranceRule {
  uuid: string;
  name: string;
  vendorUuid?: string | null;
  vendorName?: string | null;
  priceTolerancePercent: number;
  priceToleranceAmount: number;
  quantityTolerancePercent: number;
  totalTolerancePercent: number;
  totalToleranceAmount: number;
  autoApproveWithin: boolean;
  isDefault: boolean;
  isActive: boolean;
}

export type ToleranceRuleSave = Omit<ToleranceRule, 'uuid' | 'vendorName'>;

// ── §7 Schedules + early-payment terms (PaymentScheduleModel, CashFlowForecastModel) ─

export interface PaymentSchedule {
  uuid: string;
  invoiceUuid: string;
  invoiceNumber: string;
  supplierInvoiceNo?: string | null;
  vendorUuid: string;
  vendorName: string;
  currencyCode: string;
  invoiceDate: string;
  dueDate: string;
  daysUntilDue: number;
  originalAmount: number;
  amountRemaining: number;
  paymentPriority: PaymentPriority | string;
  paymentMethod?: string | null;
  paymentBlock: boolean;
  blockReason?: string | null;
  blockedByName?: string | null;
  blockedAt?: string | null;
  earlyPaymentTermUuid?: string | null;
  earlyPaymentTermCode?: string | null;
  discountDate?: string | null;
  discountPercent?: number | null;
  discountAmount?: number | null;
  reservedByRunUuid?: string | null;
  reservedByRunNumber?: string | null;
  paidInRunUuid?: string | null;
  paidInRunNumber?: string | null;
  status: string;
  rowVersion?: string;
}

export interface CashFlowWeek {
  week: number; weekStart: string; weekEnd: string; itemCount: number; dueAmount: number; blockedAmount: number;
  discountAvailable: number; netIfDiscountsTaken: number;
}
export interface CashFlowForecast {
  from: string; weeks: number; baseCurrencyCode?: string | null; overdueAmount: number; overdueCount: number; overdueBlockedAmount: number;
  weekRows: CashFlowWeek[]; laterAmount: number; totalDue: number; totalBlocked: number; totalDiscountAvailable: number;
}

export interface EarlyPaymentTerm { uuid: string; code: string; name: string; discountPercent: number; discountDays: number; netDays: number; isActive: boolean; }
export type EarlyPaymentTermSave = Omit<EarlyPaymentTerm, 'uuid'>;

// ── §8 Payment runs (PaymentRunDetailModel) ───────────────────────────────────

export interface PaymentRunProposalRequest {
  paymentDate: string;
  dueDateCutoff: string;
  paymentMethod?: string | null;
  vendorUuid?: string | null;
  currencyCode?: string | null;
  bankAccount?: string | null;
  includeDiscountItems: boolean;
  /** High = high only, Normal = high + normal, null = all. */
  minPriority?: PaymentPriority | null;
}

export interface PaymentRunItemWire {
  itemUuid: string; scheduleUuid: string; invoiceUuid: string; invoiceNumber: string; currencyCode: string; dueDate: string;
  originalAmount: number; discountAmount: number; netPaymentAmount: number; daysUntilDue: number; hasDiscount: boolean;
  discountDate?: string | null; paymentMethod?: string | null; status: string; removedReason?: string | null;
}

export interface PaymentRunBatchWire {
  batchUuid: string; vendorUuid: string; vendorName: string; currencyCode: string; paymentMethod: string; bankDetailsMasked?: string | null;
  itemCount: number; totalAmount: number; totalDiscount: number; netAmount: number;
  supplierPaymentUuid?: string | null; supplierPaymentNumber?: string | null; status: string; errorMessage?: string | null;
  items: PaymentRunItemWire[];
}

export interface BlockedScheduleWire {
  scheduleUuid: string; invoiceUuid: string; invoiceNumber: string; vendorUuid: string; vendorName: string; currencyCode: string;
  dueDate: string; amountRemaining: number; blockReason?: string | null;
}

export interface PaymentRunSummary {
  uuid: string;
  runNumber: string;
  runDate: string;
  paymentDate: string;
  dueDateCutoff?: string | null;
  status: PaymentRunStatus | string;
  paymentMethod?: string | null;
  baseCurrencyCode?: string | null;
  totalVendors: number;
  totalItems: number;
  totalAmount: number;
  totalDiscount: number;
  netPaymentAmount: number;
  failedBatchCount?: number;
  proposedByName?: string | null;
  proposedAt?: string | null;
  approvedByName?: string | null;
  approvedAt?: string | null;
  completedAt?: string | null;
}

export interface PaymentRunDetailWire {
  run: PaymentRunSummary;
  bankAccount?: string | null;
  includeDiscount: boolean;
  selectionCriteria?: string | null;
  executedByName?: string | null;
  executedAt?: string | null;
  cancelledByName?: string | null;
  cancelledAt?: string | null;
  cancelReason?: string | null;
  paymentFileName?: string | null;
  exportedAt?: string | null;
  rowVersion: string;
  vendorGroups: PaymentRunBatchWire[];
  removedItems: PaymentRunItemWire[];
  blockedItems: BlockedScheduleWire[];
  /** D-AP-12 — e.g. the discount account the GL needs is not assigned (approval is then refused). */
  warnings?: string[];
}

// The wizard's view of a run: the summary flattened, batches as vendor groups with a partner ref.
export interface PaymentRunItem {
  uuid: string;
  scheduleUuid?: string | null;
  invoiceUuid?: string | null;
  invoiceNumber: string;
  supplierInvoiceNo?: string | null;
  dueDate: string;
  currencyCode?: string | null;
  originalAmount: number;
  discountAmount: number;
  netPaymentAmount: number;
  daysUntilDue?: number | null;
  hasDiscount?: boolean | null;
  discountDate?: string | null;
  isBlocked?: boolean | null;
  blockReason?: string | null;
  paymentMethod?: string | null;
  status: string;
  removedReason?: string | null;
}

export interface PaymentRunVendorGroup {
  uuid?: string | null;
  vendor: PartnerRef;
  currencyCode?: string | null;
  paymentMethod?: string | null;
  itemCount: number;
  totalAmount: number;
  totalDiscount: number;
  netAmount: number;
  isBlocked?: boolean | null;
  batchStatus?: string | null;
  paymentUuid?: string | null;
  paymentNumber?: string | null;
  failureReason?: string | null;
  bankDetailsMasked?: string | null;
  items: PaymentRunItem[];
}

export interface PaymentRunDetail extends PaymentRunSummary {
  bankAccount?: string | null;
  includeDiscount?: boolean | null;
  currencyCode?: string | null;
  executedAt?: string | null;
  executedByName?: string | null;
  cancelReason?: string | null;
  rowVersion?: string | null;
  vendorGroups: PaymentRunVendorGroup[];
  /** Removed from the run (restore puts them back in their vendor's batch). */
  removedItems: (PaymentRunItem & { vendorName: string })[];
  /** BR-PR-002 — blocked schedules that met the criteria, shown apart. */
  blockedItems: (PaymentRunItem & { vendorName: string })[];
  warnings: string[];
}

/** Execute / resume: 202 while a background job carries on (poll GET until Completed). */
export interface PaymentRunExecution { run: PaymentRunDetail; accepted: boolean; message?: string | null; }

// ── §9 AP debit memos (ApDebitMemoModel) ──────────────────────────────────────

export interface CreateDebitMemoRequest {
  vendorUuid: string;
  originalInvoiceUuid?: string | null;
  date?: string | null;
  reason: string;
  reasonDetail?: string | null;
  amount: number;
  notes?: string | null;
}

export interface SupplierNoteApplication {
  uuid: string; noteUuid: string; noteNumber: string; invoiceUuid: string; invoiceNumber: string; amount: number;
  appliedAt: string; appliedByName?: string | null;
}

export interface DebitMemo {
  uuid: string;
  debitNoteNumber: string;
  /** SRO / MEMO. */
  origin: string;
  vendorUuid: string;
  vendorName: string;
  amount: number;
  debitReason: string;
  debitReasonDetail?: string | null;
  originalInvoiceUuid?: string | null;
  originalInvoiceNumber?: string | null;
  status: string;
  applicationStatus: string;
  remainingAmount: number;
  issuedAt?: string | null;
  /** REV (§9) — the note applies only to invoices in its own currency. */
  currencyCode?: string | null;
  applications: SupplierNoteApplication[];
}

// ── Mappers (one shape each) ──────────────────────────────────────────────────

function toAging(r: Omit<AgingReport, 'lines'> & { lines: AgingPartnerLineWire[] }): AgingReport {
  return {
    ...r,
    bucketLabels: r.bucketLabels ?? [],
    bucketDays: r.bucketDays ?? [],
    lines: (r.lines ?? []).map(l => ({ ...l, partner: { uuid: l.partnerId, name: l.partnerName ?? '', code: l.partnerCode ?? null } })),
    totals: r.totals ?? { bucketTotals: [], grandTotal: 0, totalPartners: 0, totalOpenItems: 0 }
  };
}

function toItem(i: PaymentRunItemWire): PaymentRunItem {
  const { itemUuid, ...rest } = i;
  return { ...rest, uuid: itemUuid };
}

/** The run as the wizard reads it (contract §8 / PaymentRunDetailModel). */
export function toRun(w: PaymentRunDetailWire): PaymentRunDetail {
  const vendorName = new Map<string, string>();
  for (const b of w.vendorGroups ?? []) vendorName.set(b.vendorUuid, b.vendorName);
  return {
    ...w.run,
    bankAccount: w.bankAccount, includeDiscount: w.includeDiscount, executedAt: w.executedAt, executedByName: w.executedByName,
    cancelReason: w.cancelReason, rowVersion: w.rowVersion, warnings: w.warnings ?? [],
    vendorGroups: (w.vendorGroups ?? []).map(b => ({
      uuid: b.batchUuid, vendor: { uuid: b.vendorUuid, name: b.vendorName }, currencyCode: b.currencyCode, paymentMethod: b.paymentMethod,
      itemCount: b.itemCount, totalAmount: b.totalAmount, totalDiscount: b.totalDiscount, netAmount: b.netAmount, batchStatus: b.status,
      paymentUuid: b.supplierPaymentUuid, paymentNumber: b.supplierPaymentNumber, failureReason: b.errorMessage, bankDetailsMasked: b.bankDetailsMasked,
      items: (b.items ?? []).map(toItem)
    })),
    removedItems: (w.removedItems ?? []).map(i => ({ ...toItem(i), vendorName: '' })),
    blockedItems: (w.blockedItems ?? []).map(s => ({
      uuid: s.scheduleUuid, scheduleUuid: s.scheduleUuid, invoiceUuid: s.invoiceUuid, invoiceNumber: s.invoiceNumber, currencyCode: s.currencyCode,
      dueDate: s.dueDate, originalAmount: s.amountRemaining, discountAmount: 0, netPaymentAmount: s.amountRemaining, isBlocked: true,
      blockReason: s.blockReason, status: 'Blocked', vendorName: s.vendorName
    }))
  };
}

// ── Plumbing ──────────────────────────────────────────────────────────────────

interface Envelope<T> { success?: boolean; message?: string; result: T; }

function unwrap<T>() {
  return map((r: Envelope<T> | T) => {
    if (r && typeof r === 'object' && 'result' in (r as object) && 'success' in (r as object)) {
      const e = r as Envelope<T>;
      if (e.success === false) throw Object.assign(new Error(e.message || 'The request did not succeed.'), { error: e });
      return e.result;
    }
    return r as T;
  });
}

function params(values: Record<string, string | number | boolean | null | undefined>): HttpParams {
  let p = new HttpParams();
  for (const [k, v] of Object.entries(values)) {
    if (v === null || v === undefined || v === '') continue;
    p = p.set(k, String(v));
  }
  return p;
}

function asPage<T>(r: Page<T> | null | undefined, page: number, pageSize: number): Page<T> {
  const data = r?.data ?? [];
  return { ...(r as Page<T>), data, totalRecords: r?.totalRecords ?? data.length, page: r?.page ?? page, pageSize: r?.pageSize ?? pageSize };
}

/** The contract caps pageSize at 200; the dashboards read one page of that size. */
export const MAX_PAGE_SIZE = 200;

function agingParams(q: AgingQuery, partnerKey: 'customerUuid' | 'vendorUuid', extra: Record<string, string | number | null> = {}): HttpParams {
  return params({
    asOfDate: q.asOfDate, basis: q.basis, buckets: q.buckets?.length ? q.buckets.join(',') : null, [partnerKey]: q.partnerUuid,
    currencyCode: q.currencyCode, includeDetails: q.includeDetails ? true : null, sortBy: q.sortBy, sortDirection: q.sortDirection,
    page: q.page ?? 1, pageSize: q.pageSize ?? MAX_PAGE_SIZE, ...extra
  });
}

function statementParams(q: StatementQuery, format: 'json' | 'pdf'): HttpParams {
  return params({ from: q.fromDate, to: q.toDate, type: q.type, currencyCode: q.currencyCode, includeAging: q.includeAging ?? true, format });
}

const id = encodeURIComponent;

@Injectable({ providedIn: 'root' })
export class ArApService {
  private readonly api = `${environment.apiUrl}/finance`;

  constructor(private http: HttpClient) {}

  // ── §1 Profiles + settings ──
  getProfile(partnerUuid: string, role: PartnerRole): Observable<PartnerFinancialProfile> {
    return this.http.get<Envelope<PartnerFinancialProfile>>(`${this.api}/partner-profiles/${id(partnerUuid)}`, { params: params({ role }) })
      .pipe(unwrap<PartnerFinancialProfile>());
  }

  updateProfile(partnerUuid: string, role: PartnerRole, body: PartnerFinancialProfileSave): Observable<PartnerFinancialProfile> {
    return this.http.put<Envelope<PartnerFinancialProfile>>(`${this.api}/partner-profiles/${id(partnerUuid)}`, body, { params: params({ role }) })
      .pipe(unwrap<PartnerFinancialProfile>());
  }

  getSettings(): Observable<ArApSettings> {
    return this.http.get<Envelope<ArApSettings>>(`${this.api}/ar-ap/settings`).pipe(unwrap<ArApSettings>());
  }

  updateSettings(body: ArApSettings): Observable<ArApSettings> {
    return this.http.put<Envelope<ArApSettings>>(`${this.api}/ar-ap/settings`, body).pipe(unwrap<ArApSettings>());
  }

  // ── §2 Credit ──
  getExposure(customerUuid: string): Observable<CreditExposure> {
    return this.http.get<Envelope<CreditExposure>>(`${this.api}/credit/${id(customerUuid)}/exposure`).pipe(unwrap<CreditExposure>());
  }

  getExposures(q: CreditExposureQuery = {}): Observable<Page<CreditExposure>> {
    const page = q.page ?? 1, pageSize = q.pageSize ?? 25;
    return this.http.get<Envelope<Page<CreditExposure>>>(`${this.api}/credit/exposures`, {
      params: params({ overLimit: q.overLimit || null, onHold: q.onHold || null, riskTier: q.riskTier, search: q.search?.trim(), page, pageSize })
    }).pipe(unwrap<Page<CreditExposure>>(), map(r => asPage(r, page, pageSize)));
  }

  checkCredit(customerUuid: string, amount: number, currencyCode?: string | null): Observable<CreditCheckResult> {
    return this.http.post<Envelope<CreditCheckResult>>(`${this.api}/credit/${id(customerUuid)}/check`, { amount, currencyCode: currencyCode ?? null })
      .pipe(unwrap<CreditCheckResult>());
  }

  placeHold(customerUuid: string, reason: string): Observable<CreditExposure> {
    return this.http.post<Envelope<CreditExposure>>(`${this.api}/credit/${id(customerUuid)}/hold`, { reason }).pipe(unwrap<CreditExposure>());
  }

  releaseHold(customerUuid: string, reason: string): Observable<CreditExposure> {
    return this.http.post<Envelope<CreditExposure>>(`${this.api}/credit/${id(customerUuid)}/release`, { reason }).pipe(unwrap<CreditExposure>());
  }

  /** `amount: null` clears the temporary limit. */
  setTemporaryLimit(customerUuid: string, body: TemporaryLimitRequest): Observable<CreditExposure> {
    return this.http.post<Envelope<CreditExposure>>(`${this.api}/credit/${id(customerUuid)}/temporary-limit`, body).pipe(unwrap<CreditExposure>());
  }

  setRiskTier(customerUuid: string, riskTier: RiskTier | string, reason: string): Observable<CreditExposure> {
    return this.http.put<Envelope<CreditExposure>>(`${this.api}/credit/${id(customerUuid)}/risk-tier`, { riskTier, reason }).pipe(unwrap<CreditExposure>());
  }

  getCreditHistory(customerUuid: string, page = 1, pageSize = 50): Observable<Page<CreditHistoryEntry>> {
    return this.http.get<Envelope<Page<CreditHistoryEntry>>>(`${this.api}/credit/${id(customerUuid)}/history`, { params: params({ page, pageSize }) })
      .pipe(unwrap<Page<CreditHistoryEntry>>(), map(r => asPage(r, page, pageSize)));
  }

  // ── §3 Dunning ──
  getProcedures(): Observable<DunningProcedure[]> {
    return this.http.get<Envelope<DunningProcedure[]>>(`${this.api}/dunning/procedures`)
      .pipe(unwrap<DunningProcedure[]>(), map(r => (r ?? []).map(p => ({ ...p, levels: [...(p.levels ?? [])].sort((a, b) => a.levelNumber - b.levelNumber) }))));
  }

  createProcedure(body: DunningProcedureSave): Observable<DunningProcedure> {
    return this.http.post<Envelope<DunningProcedure>>(`${this.api}/dunning/procedures`, body).pipe(unwrap<DunningProcedure>());
  }

  updateProcedure(uuid: string, body: DunningProcedureSave): Observable<DunningProcedure> {
    return this.http.put<Envelope<DunningProcedure>>(`${this.api}/dunning/procedures/${id(uuid)}`, body).pipe(unwrap<DunningProcedure>());
  }

  previewDunningRun(asOfDate: string): Observable<DunningRunDetail> {
    return this.http.post<Envelope<DunningRunDetail>>(`${this.api}/dunning/runs/preview`, { asOfDate }).pipe(unwrap<DunningRunDetail>());
  }

  skipDunningItem(runUuid: string, itemUuid: string, reason: string): Observable<DunningRunDetail> {
    return this.http.post<Envelope<DunningRunDetail>>(`${this.api}/dunning/runs/${id(runUuid)}/items/${id(itemUuid)}/skip`, { reason }).pipe(unwrap<DunningRunDetail>());
  }

  executeDunningRun(uuid: string): Observable<DunningRunDetail> {
    return this.http.post<Envelope<DunningRunDetail>>(`${this.api}/dunning/runs/${id(uuid)}/execute`, {}).pipe(unwrap<DunningRunDetail>());
  }

  cancelDunningRun(uuid: string, reason?: string | null): Observable<DunningRunDetail> {
    return this.http.post<Envelope<DunningRunDetail>>(`${this.api}/dunning/runs/${id(uuid)}/cancel`, { reason: reason ?? null }).pipe(unwrap<DunningRunDetail>());
  }

  getDunningRuns(page = 1, pageSize = 25): Observable<Page<DunningRunSummary>> {
    return this.http.get<Envelope<Page<DunningRunSummary>>>(`${this.api}/dunning/runs`, { params: params({ page, pageSize }) })
      .pipe(unwrap<Page<DunningRunSummary>>(), map(r => asPage(r, page, pageSize)));
  }

  getDunningRun(uuid: string): Observable<DunningRunDetail> {
    return this.http.get<Envelope<DunningRunDetail>>(`${this.api}/dunning/runs/${id(uuid)}`).pipe(unwrap<DunningRunDetail>());
  }

  getCustomerDunningHistory(customerUuid: string): Observable<DunningHistory> {
    return this.http.get<Envelope<DunningHistory>>(`${this.api}/dunning/customers/${id(customerUuid)}/history`).pipe(unwrap<DunningHistory>());
  }

  resetDunning(customerUuid: string, reason: string, level = 0): Observable<unknown> {
    return this.http.post<Envelope<unknown>>(`${this.api}/dunning/customers/${id(customerUuid)}/reset`, { reason, level }).pipe(unwrap<unknown>());
  }

  // ── §4 AR credit memos ──
  createCreditMemo(body: CreateCreditMemoRequest): Observable<CreditMemo> {
    return this.http.post<Envelope<CreditMemo>>(`${this.api}/ar/credit-memo`, body).pipe(unwrap<CreditMemo>());
  }

  applyCreditMemo(creditNoteUuid: string, applications: CreditApplicationRequest[]): Observable<CreditMemo> {
    return this.http.post<Envelope<CreditMemo>>(`${this.api}/ar/credit-memo/${id(creditNoteUuid)}/apply`, { applications }).pipe(unwrap<CreditMemo>());
  }

  getCreditMemo(creditNoteUuid: string): Observable<CreditMemo> {
    return this.http.get<Envelope<CreditMemo>>(`${this.api}/ar/credit-memo/${id(creditNoteUuid)}`).pipe(unwrap<CreditMemo>());
  }

  getCreditApplications(creditNoteUuid: string): Observable<CreditApplication[]> {
    return this.http.get<Envelope<CreditApplication[]>>(`${this.api}/ar/credit-memo/${id(creditNoteUuid)}/applications`).pipe(unwrap<CreditApplication[]>());
  }

  getOpenCredits(customerUuid: string): Observable<CreditMemo[]> {
    return this.http.get<Envelope<CreditMemo[]>>(`${this.api}/ar/customers/${id(customerUuid)}/open-credits`).pipe(unwrap<CreditMemo[]>());
  }

  // ── §5 AR inquiry ──
  getArSummary(asOfDate?: string | null): Observable<ArSummary> {
    return this.http.get<Envelope<ArSummary>>(`${this.api}/ar/summary`, { params: params({ asOfDate }) }).pipe(unwrap<ArSummary>());
  }

  getArAging(q: AgingQuery = {}): Observable<AgingReport> {
    return this.http.get<Envelope<any>>(`${this.api}/ar/aging`, { params: agingParams(q, 'customerUuid') }).pipe(unwrap<any>(), map(toAging));
  }

  exportArAging(q: AgingQuery, format: 'pdf' | 'xlsx'): Observable<HttpResponse<Blob>> {
    return this.http.get(`${this.api}/ar/aging/export`, { params: agingParams(q, 'customerUuid', { format }), responseType: 'blob', observe: 'response' });
  }

  getCustomerBalance(uuid: string, asOfDate?: string | null): Observable<PartnerBalance> {
    return this.http.get<Envelope<PartnerBalance>>(`${this.api}/ar/customers/${id(uuid)}/balance`, { params: params({ asOfDate }) }).pipe(unwrap<PartnerBalance>());
  }

  getCustomerOpenItems(uuid: string, q: { includeCredits?: boolean; overdueOnly?: boolean; currencyCode?: string | null; page?: number; pageSize?: number } = {}): Observable<Page<PartnerOpenItem>> {
    const page = q.page ?? 1, pageSize = q.pageSize ?? MAX_PAGE_SIZE;
    return this.http.get<Envelope<Page<PartnerOpenItem>>>(`${this.api}/ar/customers/${id(uuid)}/open-items`, {
      params: params({ includeCredits: q.includeCredits ?? true, overdueOnly: q.overdueOnly || null, currencyCode: q.currencyCode, page, pageSize })
    }).pipe(unwrap<Page<PartnerOpenItem>>(), map(r => asPage(r, page, pageSize)));
  }

  getCustomerStatement(uuid: string, q: StatementQuery): Observable<StatementResult> {
    return this.http.get<Envelope<StatementResult>>(`${this.api}/ar/customers/${id(uuid)}/statement`, { params: statementParams(q, 'json') })
      .pipe(unwrap<StatementResult>(), map(s => ({ ...s, sections: s.sections ?? [], agingLabels: s.agingLabels ?? [] })));
  }

  downloadCustomerStatementPdf(uuid: string, q: StatementQuery): Observable<HttpResponse<Blob>> {
    return this.http.get(`${this.api}/ar/customers/${id(uuid)}/statement`, { params: statementParams(q, 'pdf'), responseType: 'blob', observe: 'response' });
  }

  emailCustomerStatement(uuid: string, q: StatementQuery, email: StatementEmailRequest = {}): Observable<StatementEmailResult> {
    return this.http.post<Envelope<StatementEmailResult>>(`${this.api}/ar/customers/${id(uuid)}/statement/email`, {
      from: q.fromDate, to: q.toDate, type: q.type, currencyCode: q.currencyCode ?? null, includeAging: q.includeAging ?? true,
      emailTo: email.emailTo || null, subject: email.subject || null, message: email.message || null
    }).pipe(unwrap<StatementEmailResult>());
  }

  batchStatements(body: BatchStatementRequest): Observable<BatchStatementResult> {
    return this.http.post<Envelope<BatchStatementResult>>(`${this.api}/ar/statements/batch`, body).pipe(unwrap<BatchStatementResult>(),
      map(r => ({ ...r, items: r.items ?? [] })));
  }

  getStatementLog(role: PartnerRole, partnerUuid?: string | null, page = 1, pageSize = 10): Observable<Page<StatementLogEntry>> {
    return this.http.get<Envelope<Page<StatementLogEntry>>>(`${this.api}/statements/log`, { params: params({ role, partnerUuid, page, pageSize }) })
      .pipe(unwrap<Page<StatementLogEntry>>(), map(r => asPage(r, page, pageSize)));
  }

  // ── §5 AP inquiry ──
  getApSummary(asOfDate?: string | null): Observable<ApSummary> {
    return this.http.get<Envelope<ApSummary>>(`${this.api}/ap/summary`, { params: params({ asOfDate }) }).pipe(unwrap<ApSummary>());
  }

  getApAging(q: AgingQuery = {}): Observable<AgingReport> {
    return this.http.get<Envelope<any>>(`${this.api}/ap/aging`, { params: agingParams(q, 'vendorUuid') }).pipe(unwrap<any>(), map(toAging));
  }

  exportApAging(q: AgingQuery, format: 'pdf' | 'xlsx'): Observable<HttpResponse<Blob>> {
    return this.http.get(`${this.api}/ap/aging/export`, { params: agingParams(q, 'vendorUuid', { format }), responseType: 'blob', observe: 'response' });
  }

  getVendorBalance(uuid: string, asOfDate?: string | null): Observable<PartnerBalance> {
    return this.http.get<Envelope<PartnerBalance>>(`${this.api}/ap/vendors/${id(uuid)}/balance`, { params: params({ asOfDate }) }).pipe(unwrap<PartnerBalance>());
  }

  getVendorOpenItems(uuid: string, q: { includeCredits?: boolean; page?: number; pageSize?: number } = {}): Observable<Page<PartnerOpenItem>> {
    const page = q.page ?? 1, pageSize = q.pageSize ?? MAX_PAGE_SIZE;
    return this.http.get<Envelope<Page<PartnerOpenItem>>>(`${this.api}/ap/vendors/${id(uuid)}/open-items`, {
      params: params({ includeCredits: q.includeCredits ?? true, page, pageSize })
    }).pipe(unwrap<Page<PartnerOpenItem>>(), map(r => asPage(r, page, pageSize)));
  }

  getVendorStatement(uuid: string, q: StatementQuery): Observable<StatementResult> {
    return this.http.get<Envelope<StatementResult>>(`${this.api}/ap/vendors/${id(uuid)}/statement`, { params: statementParams(q, 'json') })
      .pipe(unwrap<StatementResult>(), map(s => ({ ...s, sections: s.sections ?? [], agingLabels: s.agingLabels ?? [] })));
  }

  downloadVendorStatementPdf(uuid: string, q: StatementQuery): Observable<HttpResponse<Blob>> {
    return this.http.get(`${this.api}/ap/vendors/${id(uuid)}/statement`, { params: statementParams(q, 'pdf'), responseType: 'blob', observe: 'response' });
  }

  reconcileVendor(uuid: string, body: VendorReconciliationRequest): Observable<VendorReconciliationResult> {
    return this.http.post<Envelope<VendorReconciliationResult>>(`${this.api}/ap/vendors/${id(uuid)}/reconciliation`, body)
      .pipe(unwrap<VendorReconciliationResult>(), map(r => ({ ...r, differences: r.differences ?? [] })));
  }

  // ── §6 Matching (keyed by the supplier invoice) ──
  match(invoiceUuid: string, matchType?: MatchType | null): Observable<MatchDetail> {
    return this.http.post<Envelope<MatchDetail>>(`${this.api}/matching/invoices/${id(invoiceUuid)}/match`, matchType ? { matchType } : {}).pipe(unwrap<MatchDetail>());
  }

  getMatch(invoiceUuid: string): Observable<MatchDetail> {
    return this.http.get<Envelope<MatchDetail>>(`${this.api}/matching/invoices/${id(invoiceUuid)}`).pipe(unwrap<MatchDetail>());
  }

  overrideMatch(invoiceUuid: string, reason: string): Observable<MatchDetail> {
    return this.http.post<Envelope<MatchDetail>>(`${this.api}/matching/invoices/${id(invoiceUuid)}/override`, { reason }).pipe(unwrap<MatchDetail>());
  }

  approveMatch(invoiceUuid: string): Observable<MatchDetail> {
    return this.http.post<Envelope<MatchDetail>>(`${this.api}/matching/invoices/${id(invoiceUuid)}/approve`, {}).pipe(unwrap<MatchDetail>());
  }

  /** FE-6 answered — MATCH_APPROVE, reason required. */
  rejectMatch(invoiceUuid: string, reason: string): Observable<MatchDetail> {
    return this.http.post<Envelope<MatchDetail>>(`${this.api}/matching/invoices/${id(invoiceUuid)}/reject`, { reason }).pipe(unwrap<MatchDetail>());
  }

  getPendingMatches(f: MatchFilter = {}): Observable<Page<MatchSummary>> {
    const page = f.page ?? 1, pageSize = f.pageSize ?? 25;
    return this.http.get<Envelope<Page<MatchSummary>>>(`${this.api}/matching/pending`, {
      params: params({ vendorUuid: f.vendorUuid, result: f.result, search: f.search?.trim(), page, pageSize })
    }).pipe(unwrap<Page<MatchSummary>>(), map(r => asPage(r, page, pageSize)));
  }

  getMatchesByPo(poUuid: string): Observable<MatchSummary[]> {
    return this.http.get<Envelope<MatchSummary[]>>(`${this.api}/matching/po/${id(poUuid)}`).pipe(unwrap<MatchSummary[]>());
  }

  getToleranceRules(): Observable<ToleranceRule[]> {
    return this.http.get<Envelope<ToleranceRule[]>>(`${this.api}/matching/tolerances`).pipe(unwrap<ToleranceRule[]>(), map(r => r ?? []));
  }

  createToleranceRule(body: ToleranceRuleSave): Observable<ToleranceRule> {
    return this.http.post<Envelope<ToleranceRule>>(`${this.api}/matching/tolerances`, body).pipe(unwrap<ToleranceRule>());
  }

  updateToleranceRule(uuid: string, body: ToleranceRuleSave): Observable<ToleranceRule> {
    return this.http.put<Envelope<ToleranceRule>>(`${this.api}/matching/tolerances/${id(uuid)}`, body).pipe(unwrap<ToleranceRule>());
  }

  // ── §7 Schedules + terms ──
  getUpcomingPayments(from?: string | null, to?: string | null): Observable<PaymentSchedule[]> {
    return this.http.get<Envelope<PaymentSchedule[]>>(`${this.api}/payment-schedules/upcoming`, { params: params({ from, to }) })
      .pipe(unwrap<PaymentSchedule[]>(), map(r => r ?? []));
  }

  getSchedules(q: { status?: string | null; vendorUuid?: string | null; dueFrom?: string | null; dueTo?: string | null; page?: number; pageSize?: number } = {}): Observable<Page<PaymentSchedule>> {
    const page = q.page ?? 1, pageSize = q.pageSize ?? MAX_PAGE_SIZE;
    return this.http.get<Envelope<Page<PaymentSchedule>>>(`${this.api}/payment-schedules`, {
      params: params({ status: q.status, vendorUuid: q.vendorUuid, dueFrom: q.dueFrom, dueTo: q.dueTo, page, pageSize })
    }).pipe(unwrap<Page<PaymentSchedule>>(), map(r => asPage(r, page, pageSize)));
  }

  getDiscountOpportunities(asOfDate?: string | null, lookAheadDays?: number | null): Observable<PaymentSchedule[]> {
    return this.http.get<Envelope<PaymentSchedule[]>>(`${this.api}/payment-schedules/discounts`, { params: params({ asOfDate, lookAheadDays }) })
      .pipe(unwrap<PaymentSchedule[]>(), map(r => r ?? []));
  }

  blockSchedule(uuid: string, reason: string): Observable<PaymentSchedule> {
    return this.http.post<Envelope<PaymentSchedule>>(`${this.api}/payment-schedules/${id(uuid)}/block`, { reason }).pipe(unwrap<PaymentSchedule>());
  }

  unblockSchedule(uuid: string): Observable<PaymentSchedule> {
    return this.http.post<Envelope<PaymentSchedule>>(`${this.api}/payment-schedules/${id(uuid)}/unblock`, {}).pipe(unwrap<PaymentSchedule>());
  }

  getCashFlow(from?: string | null, weeks = 8): Observable<CashFlowForecast> {
    return this.http.get<Envelope<CashFlowForecast>>(`${this.api}/payment-schedules/cash-flow`, { params: params({ from, weeks }) })
      .pipe(unwrap<CashFlowForecast>(), map(r => ({ ...r, weekRows: r.weekRows ?? [] })));
  }

  getEarlyPaymentTerms(): Observable<EarlyPaymentTerm[]> {
    return this.http.get<Envelope<EarlyPaymentTerm[]>>(`${this.api}/early-payment-terms`).pipe(unwrap<EarlyPaymentTerm[]>(), map(r => r ?? []));
  }

  createEarlyPaymentTerm(body: EarlyPaymentTermSave): Observable<EarlyPaymentTerm> {
    return this.http.post<Envelope<EarlyPaymentTerm>>(`${this.api}/early-payment-terms`, body).pipe(unwrap<EarlyPaymentTerm>());
  }

  updateEarlyPaymentTerm(uuid: string, body: EarlyPaymentTermSave): Observable<EarlyPaymentTerm> {
    return this.http.put<Envelope<EarlyPaymentTerm>>(`${this.api}/early-payment-terms/${id(uuid)}`, body).pipe(unwrap<EarlyPaymentTerm>());
  }

  // ── §8 Payment runs ──
  proposePaymentRun(body: PaymentRunProposalRequest): Observable<PaymentRunDetail> {
    return this.http.post<Envelope<PaymentRunDetailWire>>(`${this.api}/payment-runs/propose`, body).pipe(unwrap<PaymentRunDetailWire>(), map(toRun));
  }

  getPaymentRuns(status?: string | null, page = 1, pageSize = 50): Observable<Page<PaymentRunSummary>> {
    return this.http.get<Envelope<Page<PaymentRunSummary>>>(`${this.api}/payment-runs`, { params: params({ status, page, pageSize }) })
      .pipe(unwrap<Page<PaymentRunSummary>>(), map(r => asPage(r, page, pageSize)));
  }

  getPaymentRun(uuid: string): Observable<PaymentRunDetail> {
    return this.http.get<Envelope<PaymentRunDetailWire>>(`${this.api}/payment-runs/${id(uuid)}`).pipe(unwrap<PaymentRunDetailWire>(), map(toRun));
  }

  removePaymentRunItem(runUuid: string, itemUuid: string, reason: string): Observable<PaymentRunDetail> {
    return this.http.post<Envelope<PaymentRunDetailWire>>(`${this.api}/payment-runs/${id(runUuid)}/items/${id(itemUuid)}/remove`, { reason })
      .pipe(unwrap<PaymentRunDetailWire>(), map(toRun));
  }

  restorePaymentRunItem(runUuid: string, itemUuid: string): Observable<PaymentRunDetail> {
    return this.http.post<Envelope<PaymentRunDetailWire>>(`${this.api}/payment-runs/${id(runUuid)}/items/${id(itemUuid)}/restore`, {})
      .pipe(unwrap<PaymentRunDetailWire>(), map(toRun));
  }

  reviewPaymentRun(uuid: string): Observable<PaymentRunDetail> {
    return this.http.post<Envelope<PaymentRunDetailWire>>(`${this.api}/payment-runs/${id(uuid)}/review`, {}).pipe(unwrap<PaymentRunDetailWire>(), map(toRun));
  }

  approvePaymentRun(uuid: string): Observable<PaymentRunDetail> {
    return this.http.post<Envelope<PaymentRunDetailWire>>(`${this.api}/payment-runs/${id(uuid)}/approve`, {}).pipe(unwrap<PaymentRunDetailWire>(), map(toRun));
  }

  /** 200 = done in the request; 202 = executing in the background (poll GET while Executing). */
  executePaymentRun(uuid: string): Observable<PaymentRunExecution> {
    return this.execution(`${this.api}/payment-runs/${id(uuid)}/execute`);
  }

  /** D-AP-13 — a run stuck Executing carries on with its Pending batches. */
  resumePaymentRun(uuid: string): Observable<PaymentRunExecution> {
    return this.execution(`${this.api}/payment-runs/${id(uuid)}/resume`);
  }

  private execution(url: string): Observable<PaymentRunExecution> {
    return this.http.post<Envelope<PaymentRunDetailWire>>(url, {}, { observe: 'response' }).pipe(map(res => {
      const body = res.body;
      if (!body || body.success === false) throw Object.assign(new Error(body?.message || 'The request did not succeed.'), { error: body });
      return { run: toRun(body.result), accepted: res.status === 202, message: body.message ?? null };
    }));
  }

  exportPaymentRun(uuid: string): Observable<HttpResponse<Blob>> {
    return this.http.get(`${this.api}/payment-runs/${id(uuid)}/export`, { params: params({ format: 'csv' }), responseType: 'blob', observe: 'response' });
  }

  cancelPaymentRun(uuid: string, reason: string): Observable<PaymentRunDetail> {
    return this.http.post<Envelope<PaymentRunDetailWire>>(`${this.api}/payment-runs/${id(uuid)}/cancel`, { reason }).pipe(unwrap<PaymentRunDetailWire>(), map(toRun));
  }

  // ── §9 AP debit memos ──
  createDebitMemo(body: CreateDebitMemoRequest): Observable<DebitMemo> {
    return this.http.post<Envelope<DebitMemo>>(`${this.api}/ap/debit-memo`, body).pipe(unwrap<DebitMemo>());
  }

  applyDebitMemo(debitNoteUuid: string, applications: CreditApplicationRequest[]): Observable<DebitMemo> {
    return this.http.post<Envelope<DebitMemo>>(`${this.api}/ap/debit-memo/${id(debitNoteUuid)}/apply`, { applications }).pipe(unwrap<DebitMemo>());
  }

  /** FE-8 — a memo or SRO note with what is left on it and its applications. */
  getDebitMemo(debitNoteUuid: string): Observable<DebitMemo> {
    return this.http.get<Envelope<DebitMemo>>(`${this.api}/ap/debit-memo/${id(debitNoteUuid)}`).pipe(unwrap<DebitMemo>(), map(m => ({ ...m, applications: m.applications ?? [] })));
  }

  /** FE-8 — a vendor's debit notes / memos, newest first; `openOnly` = carried forward, live, something left to apply. */
  getVendorDebitMemos(vendorUuid: string, openOnly = true, page = 1, pageSize = MAX_PAGE_SIZE): Observable<Page<DebitMemo>> {
    return this.http.get<Envelope<Page<DebitMemo>>>(`${this.api}/ap/vendors/${id(vendorUuid)}/debit-memos`, { params: params({ openOnly, page, pageSize }) })
      .pipe(unwrap<Page<DebitMemo>>(), map(r => asPage(r, page, pageSize)));
  }

  getDebitApplications(debitNoteUuid: string): Observable<SupplierNoteApplication[]> {
    return this.http.get<Envelope<SupplierNoteApplication[]>>(`${this.api}/ap/debit-memo/${id(debitNoteUuid)}/applications`).pipe(unwrap<SupplierNoteApplication[]>(), map(r => r ?? []));
  }
}
