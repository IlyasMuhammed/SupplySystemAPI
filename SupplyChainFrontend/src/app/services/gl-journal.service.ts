import { HttpClient, HttpParams, HttpResponse } from '@angular/common/http';
import { Injectable } from '@angular/core';
import { Observable, map } from 'rxjs';
import { environment } from '../../environments/environment';

// A47 — General Ledger journal engine (docs/journal-engine/API-CONTRACT.md): journal types (§1), journal entries (§2),
// reversal (§3), line reconciliation (§4), trial balance + account ledger (§5), period close and year-end close (§6).
// Uuids only, dates yyyy-MM-dd, money as numbers in the base currency unless named otherwise. Every route needs
// FEATURE_GENERAL_LEDGER and a Complete A42 setup (403 FINANCE_SETUP_REQUIRED → glSetupRequiredInterceptor).

export const GL_JOURNAL_ROUTE = '/portal/pages/finance/gl/journal-entries';
export const GL_RECONCILIATION_ROUTE = '/portal/pages/finance/gl/reconciliation';

/** D-15 / contract §0 — the A47 permission codes (journal-type writes stay GL_SETUP_MANAGE). */
export const GL_JOURNAL_PERMS = {
  VIEW: 'GL_VIEW',
  ACCOUNT_MANAGE: 'GL_ACCOUNT_MANAGE',
  SETUP_MANAGE: 'GL_SETUP_MANAGE',
  CREATE: 'GL_JOURNAL_CREATE',
  EDIT: 'GL_JOURNAL_EDIT',
  DELETE: 'GL_JOURNAL_DELETE',
  POST: 'GL_JOURNAL_POST',
  REVERSE: 'GL_JOURNAL_REVERSE',
  RECONCILE_VIEW: 'GL_RECONCILE_VIEW',
  RECONCILE_EXECUTE: 'GL_RECONCILE_EXECUTE',
  REPORTS_VIEW: 'GL_REPORTS_VIEW',
  PERIOD_CLOSE_VIEW: 'GL_PERIOD_CLOSE_VIEW',
  PERIOD_CLOSE_EXECUTE: 'GL_PERIOD_CLOSE_EXECUTE',
  PERIOD_CLOSE_ADMIN: 'GL_PERIOD_CLOSE_ADMIN'
} as const;

/** Contract §0 "JE-READ" — any of these reads journal entries and journal types. */
export const JE_READ = [
  GL_JOURNAL_PERMS.VIEW, GL_JOURNAL_PERMS.ACCOUNT_MANAGE, GL_JOURNAL_PERMS.SETUP_MANAGE, GL_JOURNAL_PERMS.CREATE, GL_JOURNAL_PERMS.EDIT,
  GL_JOURNAL_PERMS.DELETE, GL_JOURNAL_PERMS.POST, GL_JOURNAL_PERMS.REVERSE
];
export const RECONCILE_READ = [GL_JOURNAL_PERMS.RECONCILE_VIEW, GL_JOURNAL_PERMS.RECONCILE_EXECUTE];
export const PERIOD_CLOSE_READ = [GL_JOURNAL_PERMS.PERIOD_CLOSE_VIEW, GL_JOURNAL_PERMS.PERIOD_CLOSE_EXECUTE, GL_JOURNAL_PERMS.PERIOD_CLOSE_ADMIN];

// ── Shared refs ───────────────────────────────────────────────────────────────

/** Contract C-2 — the shared A42 AccountRefModel. */
export interface GlAccountRef { uuid: string; accountCode: string; accountName: string; accountType?: string | null; rootType?: string | null; }
/** EntryRef. */
export interface JournalEntryRef { uuid: string; entryNumber: string; }

/** Contract §0 — the body of a JournalErrorFilter refusal (inside an HttpErrorResponse's `error`). */
export interface JournalErrorBody {
  success: false;
  message: string;
  result?: { errorCode?: string | null; errors?: { errorCode?: string | null; message: string; lineNumber?: number | null }[] } | null;
}

// ── §1 Journal types ──────────────────────────────────────────────────────────

export type JournalTypeKind = 'Sale' | 'Purchase' | 'Cash' | 'Bank' | 'Miscellaneous' | 'Opening' | 'Closing';
export const JOURNAL_TYPE_KINDS: JournalTypeKind[] = ['Sale', 'Purchase', 'Cash', 'Bank', 'Miscellaneous', 'Opening', 'Closing'];

export interface JournalType {
  uuid: string;
  code: string;
  name: string;
  type: JournalTypeKind | string;
  sequencePrefix: string;
  defaultDebitAccount?: GlAccountRef | null;
  defaultCreditAccount?: GlAccountRef | null;
  requiresPartner: boolean;
  autoPost: boolean;
  isSystem: boolean;
  isActive: boolean;
  sortOrder?: number | null;
  /** Entries of the type exist (prefix fixed; a custom type cannot be deleted). */
  isUsed?: boolean | null;
  canDelete?: boolean | null;
  createdAt?: string | null;
  modifiedAt?: string | null;
}

/** CreateJournalType / UpdateJournalType (PUT is a full replacement of the editable fields). */
export interface JournalTypeSave {
  code: string;
  name: string;
  type: JournalTypeKind | string;
  sequencePrefix: string;
  defaultDebitAccountUuid?: string | null;
  defaultCreditAccountUuid?: string | null;
  requiresPartner: boolean;
  autoPost: boolean;
  sortOrder?: number | null;
}

// ── §2 Journal entries ────────────────────────────────────────────────────────

export type JournalStatus = 'Draft' | 'Posted' | 'Reversed';
export const JOURNAL_STATUSES: JournalStatus[] = ['Draft', 'Posted', 'Reversed'];
export type PartnerType = 'Customer' | 'Vendor';

export interface JournalTypeRef { uuid: string; code: string; name: string; type: JournalTypeKind | string; }

export interface JournalEntryListItem {
  uuid: string;
  entryNumber: string;
  journalType?: JournalTypeRef | null;
  entryDate: string;
  reference?: string | null;
  description?: string | null;
  entryType?: string | null;
  sourceModule?: string | null;
  status: JournalStatus | string;
  totalDebit: number;
  totalCredit: number;
  currencyCode?: string | null;
  lineCount?: number | null;
  isManual?: boolean | null;
  isOpeningEntry?: boolean | null;
  isClosingEntry?: boolean | null;
  isReversal?: boolean | null;
  createdAt?: string | null;
  postedAt?: string | null;
}

export interface JournalEntryLine {
  lineUuid: string;
  lineNumber: number;
  account: GlAccountRef;
  description?: string | null;
  /** Base currency. */
  debit: number;
  credit: number;
  currencyCode?: string | null;
  /** Signed (debit +), in currencyCode. */
  amountCurrency?: number | null;
  exchangeRate?: number | null;
  partnerUuid?: string | null;
  partnerType?: PartnerType | string | null;
  partnerName?: string | null;
  costCenter?: string | null;
  project?: string | null;
  taxCode?: string | null;
  taxAmount?: number | null;
  reconciled?: boolean | null;
  /** Signed base, debit +. */
  amountResidual?: number | null;
  reconcileGroupUuid?: string | null;
  reconcileGroupNumber?: string | null;
  clearingEntry?: JournalEntryRef | null;
  clearingDate?: string | null;
}

export interface JournalEntryDetail extends JournalEntryListItem {
  documentDate?: string | null;
  fiscalYear?: { uuid: string; fiscalYearName: string } | null;
  fiscalPeriod?: { uuid: string; periodName: string; periodNumber: number; status: string } | null;
  sourceDocumentUuid?: string | null;
  idempotencyKey?: string | null;
  exchangeRate?: number | null;
  reversesEntry?: JournalEntryRef | null;
  reversedByEntry?: JournalEntryRef | null;
  reversalReason?: string | null;
  createdByName?: string | null;
  postedByName?: string | null;
  modifiedAt?: string | null;
  modifiedByName?: string | null;
  /** State + the caller's permissions (contract §2). */
  canEdit?: boolean | null;
  canDelete?: boolean | null;
  canPost?: boolean | null;
  canReverse?: boolean | null;
  lines: JournalEntryLine[];
}

export interface JournalEntryQuery {
  journalTypeUuid?: string | null;
  fiscalYearUuid?: string | null;
  fiscalPeriodUuid?: string | null;
  status?: string | null;
  fromDate?: string | null;
  toDate?: string | null;
  accountUuid?: string | null;
  partnerUuid?: string | null;
  sourceModule?: string | null;
  search?: string | null;
  page?: number;
  pageSize?: number;
  sortBy?: 'entryDate' | 'entryNumber' | 'totalDebit' | 'createdAt' | 'postedAt' | null;
  sortDirection?: 'asc' | 'desc' | null;
}

export interface Page<T> { data: T[]; totalRecords: number; page: number; pageSize: number; totalPages?: number; }

export interface JournalEntryLineInput {
  accountUuid: string;
  debit: number;
  credit: number;
  description?: string | null;
  partnerUuid?: string | null;
  partnerType?: PartnerType | null;
  currencyCode?: string | null;
  exchangeRate?: number | null;
  costCenter?: string | null;
  project?: string | null;
  taxCode?: string | null;
  taxAmount?: number | null;
}

export interface JournalEntryCreate {
  journalTypeUuid: string;
  entryDate: string;
  documentDate?: string | null;
  reference?: string | null;
  description?: string | null;
  currencyCode?: string | null;
  exchangeRate?: number | null;
  isOpeningEntry?: boolean;
  isClosingEntry?: boolean;
  /** Same id twice = the first entry back (idempotency key manual:{id}). */
  clientRequestId?: string | null;
  /** Create and post at once (GL_JOURNAL_POST too). */
  postImmediately?: boolean;
  lines: JournalEntryLineInput[];
}

export type JournalEntryUpdate = Omit<JournalEntryCreate, 'journalTypeUuid' | 'clientRequestId' | 'postImmediately'>;

// ── §3 Reverse ────────────────────────────────────────────────────────────────

export interface ReverseRequest { reason: string; reversalDate?: string | null; }

/** Contract C-1. */
export interface ReversalResult {
  reversal: JournalEntryDetail;
  original: JournalEntryRef;
  reconcileGroupNumbers: string[];
  /** Set for a source-document journal. */
  warning?: string | null;
}

// ── §4 Reconciliation ─────────────────────────────────────────────────────────

export interface OpenItem {
  lineUuid: string;
  entry: JournalEntryRef;
  entryDate: string;
  journalTypeCode?: string | null;
  reference?: string | null;
  description?: string | null;
  lineNumber: number;
  account?: GlAccountRef | null;
  partnerUuid?: string | null;
  partnerType?: string | null;
  partnerName?: string | null;
  debit: number;
  credit: number;
  /** Signed: what is still open (debit +). */
  amountResidual: number;
  /** Set on a partially reconciled line. */
  reconcileGroupUuid?: string | null;
  reconcileGroupNumber?: string | null;
}

export interface OpenItemsQuery {
  accountUuid: string;
  partnerUuid?: string | null;
  partnerType?: string | null;
  fromDate?: string | null;
  toDate?: string | null;
}

export interface ReconcileSuggestion {
  /** The opposite-side lines to add. */
  lineUuids: string[];
  /** Σ their residuals, signed. */
  total: number;
  /** Σ selected + suggested. */
  residualAfter: number;
  exact: boolean;
}

export interface ReconcileGroup {
  uuid: string;
  groupNumber: string;
  reconcileType: 'Full' | 'Partial' | string;
  account?: GlAccountRef | null;
  /** Matched, ≥ 0. */
  amount: number;
  currencyCode?: string | null;
  reconciledAt?: string | null;
  createdByName?: string | null;
  /** Signed residual left. */
  residual: number;
  lines: OpenItem[];
}

export interface UnreconcileResult { groupNumber: string; linesRestored: number; }

/** C-8 — a row of GET reconciliation/groups (newest first; reversal auto-groups included). */
export interface ReconcileGroupListItem {
  uuid: string;
  groupNumber: string;
  reconcileType: 'Full' | 'Partial' | string;
  account?: GlAccountRef | null;
  amount: number;
  currencyCode?: string | null;
  reconciledAt?: string | null;
  createdByName?: string | null;
  lineCount: number;
}

export interface ReconcileGroupQuery {
  accountUuid?: string | null;
  partnerUuid?: string | null;
  /** On the reconciliation date (reconciledAt, UTC). */
  fromDate?: string | null;
  toDate?: string | null;
  page?: number;
  pageSize?: number;
}

// ── §5 Trial balance & ledger ─────────────────────────────────────────────────

export interface TrialBalanceQuery {
  fromDate?: string | null;
  toDate?: string | null;
  fiscalYearUuid?: string | null;
  fiscalPeriodUuid?: string | null;
  includeOpeningBalances?: boolean;
  includeClosingEntries?: boolean;
  includeZeroBalances?: boolean;
}

export interface TrialBalanceLine {
  account: GlAccountRef;
  rootType: string;
  accountType?: string | null;
  level?: number | null;
  openingDebit: number;
  openingCredit: number;
  periodDebit: number;
  periodCredit: number;
  closingDebit: number;
  closingCredit: number;
  /** closingDebit − closingCredit, + = debit. */
  balance: number;
  isActive?: boolean | null;
}

export interface TrialBalanceTotals {
  openingDebit: number; openingCredit: number; periodDebit: number; periodCredit: number; closingDebit: number; closingCredit: number;
}

export interface TrialBalanceReport {
  fromDate: string;
  toDate: string;
  fiscalYear?: { uuid: string; fiscalYearName: string } | null;
  generatedAt?: string | null;
  currencyCode?: string | null;
  includeOpeningBalances?: boolean | null;
  includeClosingEntries?: boolean | null;
  lines: TrialBalanceLine[];
  totals?: TrialBalanceTotals | null;
  /** = totals.closingDebit. */
  totalDebit: number;
  totalCredit: number;
  isBalanced?: boolean | null;
}

export interface LedgerQuery { fromDate?: string | null; toDate?: string | null; includeClosingEntries?: boolean; page?: number; pageSize?: number; }

export interface LedgerLine {
  lineUuid: string;
  entry: JournalEntryRef;
  entryDate: string;
  journalTypeCode?: string | null;
  status?: string | null;
  reference?: string | null;
  description?: string | null;
  lineNumber?: number | null;
  partnerName?: string | null;
  debit: number;
  credit: number;
  /** openingBalance + Σ to this line, debit − credit. */
  runningBalance: number;
  reconciled?: boolean | null;
  isClosingEntry?: boolean | null;
}

export interface AccountLedger {
  account?: GlAccountRef | null;
  fromDate?: string | null;
  toDate?: string | null;
  openingBalance: number;
  closingBalance: number;
  totalDebit?: number | null;
  totalCredit?: number | null;
  page: Page<LedgerLine>;
}

// ── §6 Period close & year-end close ──────────────────────────────────────────

export type ClosePeriodStatus = 'Open' | 'AdjustmentOnly' | 'Locked' | 'Closed';
export type PeriodAction = 'Lock' | 'AdjustmentOnly' | 'Reopen' | 'FinalClose';

export interface PeriodCloseFiscalYear {
  uuid: string; fiscalYearName: string; startDate: string; endDate: string; isClosed?: boolean | null; closedAt?: string | null;
}

export interface PeriodCloseStatus {
  uuid: string;
  periodNumber: number;
  periodName: string;
  startDate?: string | null;
  endDate?: string | null;
  status: ClosePeriodStatus | string;
  postedEntryCount: number;
  draftEntryCount: number;
  hasUnpostedDrafts?: boolean | null;
  /** By state and the caller's permissions. */
  allowedActions?: (PeriodAction | string)[] | null;
}

export interface PeriodCloseOverview {
  fiscalYear?: PeriodCloseFiscalYear | null;
  periods: PeriodCloseStatus[];
  closingEntry?: JournalEntryRef | null;
  canCloseYear?: boolean | null;
  /** The PC texts execute would answer. */
  yearCloseBlockers?: string[] | null;
  defaultRetainedEarningsAccount?: GlAccountRef | null;
}

export interface CloseResult { period: PeriodCloseStatus; oldStatus: string; newStatus: string; }

export interface YearEndPreviewLine {
  account: GlAccountRef;
  rootType: 'Revenue' | 'Expense' | 'Equity' | string;
  /** Natural: revenue credit − debit, expense debit − credit. */
  balance: number;
  closingDebit: number;
  closingCredit: number;
}

export interface YearEndPreview {
  fiscalYear?: PeriodCloseFiscalYear | null;
  retainedEarningsAccount?: GlAccountRef | null;
  entryDate?: string | null;
  /** 13 or 12. */
  periodNumber?: number | null;
  totalRevenue: number;
  totalExpenses: number;
  /** Revenue − expenses. */
  netIncome: number;
  lines: YearEndPreviewLine[];
  canExecute?: boolean | null;
  blockers?: string[] | null;
  existingClosingEntry?: JournalEntryRef | null;
}

export interface YearEndCloseResult {
  fiscalYear?: PeriodCloseFiscalYear | null;
  /** Null when every P&L balance was 0 (no entry needed). */
  closingEntry?: JournalEntryRef | null;
  totalRevenueClosing: number;
  totalExpenseClosing: number;
  netIncomeTransferred: number;
  alreadyClosed?: boolean | null;
  retainedEarningsAccount?: GlAccountRef | null;
  /** C-7 — all closing entries (several only past 499 P&L accounts). */
  closingEntries?: JournalEntryRef[] | null;
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

/** A page `{ data, totalRecords }` or a bare list, read as a page. */
function asPage<T>(r: Page<T> | T[] | null | undefined, page: number, pageSize: number): Page<T> {
  if (Array.isArray(r)) return { data: r, totalRecords: r.length, page, pageSize };
  const data = r?.data ?? [];
  return { ...(r as Page<T>), data, totalRecords: r?.totalRecords ?? data.length, page: r?.page ?? page, pageSize: r?.pageSize ?? pageSize };
}

function asList<T>(r: T[] | { data?: T[] } | null | undefined): T[] {
  return Array.isArray(r) ? r : (r?.data ?? []);
}

const id = encodeURIComponent;

@Injectable({ providedIn: 'root' })
export class GlJournalService {
  private readonly api = `${environment.apiUrl}/finance`;

  constructor(private http: HttpClient) {}

  // ── §1 Journal types ──
  getJournalTypes(activeOnly = false): Observable<JournalType[]> {
    return this.http.get<Envelope<JournalType[]>>(`${this.api}/journal-types`, { params: params({ activeOnly }) })
      .pipe(unwrap<JournalType[]>(), map(r => asList(r)));
  }

  getJournalType(uuid: string): Observable<JournalType> {
    return this.http.get<Envelope<JournalType>>(`${this.api}/journal-types/${id(uuid)}`).pipe(unwrap<JournalType>());
  }

  createJournalType(body: JournalTypeSave): Observable<JournalType> {
    return this.http.post<Envelope<JournalType>>(`${this.api}/journal-types`, body).pipe(unwrap<JournalType>());
  }

  updateJournalType(uuid: string, body: JournalTypeSave): Observable<JournalType> {
    return this.http.put<Envelope<JournalType>>(`${this.api}/journal-types/${id(uuid)}`, body).pipe(unwrap<JournalType>());
  }

  deactivateJournalType(uuid: string): Observable<JournalType> {
    return this.http.patch<Envelope<JournalType>>(`${this.api}/journal-types/${id(uuid)}/deactivate`, {}).pipe(unwrap<JournalType>());
  }

  activateJournalType(uuid: string): Observable<JournalType> {
    return this.http.patch<Envelope<JournalType>>(`${this.api}/journal-types/${id(uuid)}/activate`, {}).pipe(unwrap<JournalType>());
  }

  deleteJournalType(uuid: string): Observable<{ deleted: boolean }> {
    return this.http.delete<Envelope<{ deleted: boolean }>>(`${this.api}/journal-types/${id(uuid)}`).pipe(unwrap<{ deleted: boolean }>());
  }

  // ── §2 Journal entries ──
  getEntries(q: JournalEntryQuery = {}): Observable<Page<JournalEntryListItem>> {
    const page = q.page ?? 1, pageSize = q.pageSize ?? 25;
    const p = params({
      journalTypeUuid: q.journalTypeUuid, fiscalYearUuid: q.fiscalYearUuid, fiscalPeriodUuid: q.fiscalPeriodUuid, status: q.status,
      fromDate: q.fromDate, toDate: q.toDate, accountUuid: q.accountUuid, partnerUuid: q.partnerUuid, sourceModule: q.sourceModule,
      search: q.search?.trim(), page, pageSize, sortBy: q.sortBy, sortDirection: q.sortDirection
    });
    return this.http.get<Envelope<Page<JournalEntryListItem> | JournalEntryListItem[]>>(`${this.api}/journal-entries`, { params: p })
      .pipe(unwrap<Page<JournalEntryListItem> | JournalEntryListItem[]>(), map(r => asPage(r, page, pageSize)));
  }

  getEntry(uuid: string): Observable<JournalEntryDetail> {
    return this.http.get<Envelope<JournalEntryDetail>>(`${this.api}/journal-entries/${id(uuid)}`).pipe(
      unwrap<JournalEntryDetail>(), map(d => ({ ...d, lines: d?.lines ?? [] })));
  }

  createEntry(body: JournalEntryCreate): Observable<JournalEntryDetail> {
    return this.http.post<Envelope<JournalEntryDetail>>(`${this.api}/journal-entries`, body).pipe(unwrap<JournalEntryDetail>());
  }

  updateEntry(uuid: string, body: JournalEntryUpdate): Observable<JournalEntryDetail> {
    return this.http.put<Envelope<JournalEntryDetail>>(`${this.api}/journal-entries/${id(uuid)}`, body).pipe(unwrap<JournalEntryDetail>());
  }

  deleteEntry(uuid: string): Observable<{ deleted: boolean }> {
    return this.http.delete<Envelope<{ deleted: boolean }>>(`${this.api}/journal-entries/${id(uuid)}`).pipe(unwrap<{ deleted: boolean }>());
  }

  postEntry(uuid: string): Observable<JournalEntryDetail> {
    return this.http.post<Envelope<JournalEntryDetail>>(`${this.api}/journal-entries/${id(uuid)}/post`, {}).pipe(unwrap<JournalEntryDetail>());
  }

  /** §3 / C-1 — the reversal result (the original is then Reversed). A bare detail is read as `{ reversal }`. */
  reverseEntry(uuid: string, body: ReverseRequest): Observable<ReversalResult> {
    const clean: ReverseRequest = { reason: body.reason.trim(), ...(body.reversalDate ? { reversalDate: body.reversalDate } : {}) };
    return this.http.post<Envelope<ReversalResult | JournalEntryDetail>>(`${this.api}/journal-entries/${id(uuid)}/reverse`, clean).pipe(
      unwrap<ReversalResult | JournalEntryDetail>(),
      map(r => r && 'reversal' in r ? { ...r, reconcileGroupNumbers: r.reconcileGroupNumbers ?? [] }
        : { reversal: r as JournalEntryDetail, original: { uuid, entryNumber: '' }, reconcileGroupNumbers: [] }));
  }

  // ── §4 Reconciliation ──
  getOpenItems(q: OpenItemsQuery): Observable<OpenItem[]> {
    return this.http.get<Envelope<OpenItem[]>>(`${this.api}/reconciliation/open-items`, {
      params: params({ accountUuid: q.accountUuid, partnerUuid: q.partnerUuid, partnerType: q.partnerType, fromDate: q.fromDate, toDate: q.toDate })
    }).pipe(unwrap<OpenItem[]>(), map(r => asList(r)));
  }

  suggestMatches(accountUuid: string, lineUuids: string[], maxResults = 10): Observable<ReconcileSuggestion[]> {
    return this.http.post<Envelope<ReconcileSuggestion[]>>(`${this.api}/reconciliation/suggest`, { accountUuid, lineUuids, maxResults })
      .pipe(unwrap<ReconcileSuggestion[]>(), map(r => asList(r)));
  }

  reconcile(lineUuids: string[]): Observable<ReconcileGroup> {
    return this.http.post<Envelope<ReconcileGroup>>(`${this.api}/reconciliation/reconcile`, { lineUuids }).pipe(
      unwrap<ReconcileGroup>(), map(g => ({ ...g, lines: g?.lines ?? [] })));
  }

  getReconcileGroup(groupUuid: string): Observable<ReconcileGroup> {
    return this.http.get<Envelope<ReconcileGroup>>(`${this.api}/reconciliation/${id(groupUuid)}`).pipe(
      unwrap<ReconcileGroup>(), map(g => ({ ...g, lines: g?.lines ?? [] })));
  }

  /** C-8 — paged, newest first. */
  getReconcileGroups(q: ReconcileGroupQuery = {}): Observable<Page<ReconcileGroupListItem>> {
    const page = q.page ?? 1, pageSize = q.pageSize ?? 25;
    return this.http.get<Envelope<Page<ReconcileGroupListItem>>>(`${this.api}/reconciliation/groups`, {
      params: params({ accountUuid: q.accountUuid, partnerUuid: q.partnerUuid, fromDate: q.fromDate, toDate: q.toDate, page, pageSize })
    }).pipe(unwrap<Page<ReconcileGroupListItem>>(), map(r => asPage(r, page, pageSize)));
  }

  unreconcile(groupUuid: string): Observable<UnreconcileResult> {
    return this.http.post<Envelope<UnreconcileResult>>(`${this.api}/reconciliation/${id(groupUuid)}/unreconcile`, {}).pipe(unwrap<UnreconcileResult>());
  }

  // ── §5 Trial balance & ledger ──
  private tbParams(q: TrialBalanceQuery): HttpParams {
    return params({
      fromDate: q.fromDate, toDate: q.toDate, fiscalYearUuid: q.fiscalYearUuid, fiscalPeriodUuid: q.fiscalPeriodUuid,
      includeOpeningBalances: q.includeOpeningBalances, includeClosingEntries: q.includeClosingEntries, includeZeroBalances: q.includeZeroBalances
    });
  }

  getTrialBalance(q: TrialBalanceQuery): Observable<TrialBalanceReport> {
    return this.http.get<Envelope<TrialBalanceReport>>(`${this.api}/trial-balance`, { params: this.tbParams(q) }).pipe(
      unwrap<TrialBalanceReport>(), map(r => ({ ...r, lines: r?.lines ?? [] })));
  }

  /** text/csv, `trial-balance-{from}-{to}.csv`. */
  exportTrialBalance(q: TrialBalanceQuery): Observable<HttpResponse<Blob>> {
    return this.http.get(`${this.api}/trial-balance/export`, { params: this.tbParams(q), responseType: 'blob', observe: 'response' });
  }

  /** `api/finance/accounts/{uuid}/ledger` (TrialBalanceController). */
  getAccountLedger(accountUuid: string, q: LedgerQuery = {}): Observable<AccountLedger> {
    const page = q.page ?? 1, pageSize = q.pageSize ?? 50;
    return this.http.get<Envelope<AccountLedger>>(`${this.api}/accounts/${id(accountUuid)}/ledger`, {
      params: params({ fromDate: q.fromDate, toDate: q.toDate, includeClosingEntries: q.includeClosingEntries, page, pageSize })
    }).pipe(unwrap<AccountLedger>(), map(l => ({
      ...l, openingBalance: l?.openingBalance ?? 0, closingBalance: l?.closingBalance ?? 0, page: asPage(l?.page, page, pageSize)
    })));
  }

  // ── §6 Period close ──
  getPeriodCloseStatus(fiscalYearUuid: string): Observable<PeriodCloseOverview> {
    return this.http.get<Envelope<PeriodCloseOverview>>(`${this.api}/period-close/${id(fiscalYearUuid)}/status`).pipe(
      unwrap<PeriodCloseOverview>(),
      map(r => ({ ...r, periods: [...(r?.periods ?? [])].sort((a, b) => a.periodNumber - b.periodNumber) })));
  }

  /** Open / AdjustmentOnly → Locked, Open → AdjustmentOnly. */
  closePeriod(periodUuid: string, targetStatus: 'Locked' | 'AdjustmentOnly'): Observable<CloseResult> {
    return this.http.post<Envelope<CloseResult>>(`${this.api}/period-close/${id(periodUuid)}/close`, { targetStatus }).pipe(unwrap<CloseResult>());
  }

  /** Locked / AdjustmentOnly → Open. */
  reopenPeriod(periodUuid: string): Observable<CloseResult> {
    return this.http.post<Envelope<CloseResult>>(`${this.api}/period-close/${id(periodUuid)}/reopen`, {}).pipe(unwrap<CloseResult>());
  }

  /** Locked → Closed, irreversible (GL_PERIOD_CLOSE_ADMIN). */
  finalClosePeriod(periodUuid: string): Observable<CloseResult> {
    return this.http.post<Envelope<CloseResult>>(`${this.api}/period-close/${id(periodUuid)}/final-close`, {}).pipe(unwrap<CloseResult>());
  }

  previewYearEnd(fiscalYearUuid: string, retainedEarningsAccountUuid?: string | null): Observable<YearEndPreview> {
    return this.http.get<Envelope<YearEndPreview>>(`${this.api}/year-end-close/${id(fiscalYearUuid)}/preview`,
      { params: params({ retainedEarningsAccountUuid }) }).pipe(unwrap<YearEndPreview>(), map(p => ({ ...p, lines: p?.lines ?? [] })));
  }

  executeYearEnd(fiscalYearUuid: string, retainedEarningsAccountUuid?: string | null): Observable<YearEndCloseResult> {
    return this.http.post<Envelope<YearEndCloseResult>>(`${this.api}/year-end-close/${id(fiscalYearUuid)}/execute`,
      retainedEarningsAccountUuid ? { retainedEarningsAccountUuid } : {}).pipe(unwrap<YearEndCloseResult>());
  }
}
