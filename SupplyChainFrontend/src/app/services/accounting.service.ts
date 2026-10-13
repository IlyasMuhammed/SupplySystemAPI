import { HttpClient, HttpParams, HttpResponse } from '@angular/common/http';
import { Injectable, signal } from '@angular/core';
import { Observable, map, tap } from 'rxjs';
import { environment } from '../../environments/environment';
import type { ApiResponse } from '../pages/service/auth.service';

// A42 — Chart of Accounts / GL setup (docs/general-ledger/API-CONTRACT.md). Every route needs FEATURE_GENERAL_LEDGER;
// reads GL_VIEW, CoA writes GL_ACCOUNT_MANAGE (or GL_SETUP_MANAGE), setup writes GL_SETUP_MANAGE.

// ── Codes ─────────────────────────────────────────────────────────────────────

export const GL_ROUTE = '/portal/pages/finance/gl';
export const GL_SETUP_ROUTE = `${GL_ROUTE}/setup`;
export const FEATURE_GENERAL_LEDGER = 'FEATURE_GENERAL_LEDGER';
export const GL_PERMS = {
  VIEW: 'GL_VIEW',
  ACCOUNT_MANAGE: 'GL_ACCOUNT_MANAGE',
  SETUP_MANAGE: 'GL_SETUP_MANAGE'
} as const;
/** 403 error code of the post-setup GL endpoints (D-4). */
export const FINANCE_SETUP_REQUIRED = 'FINANCE_SETUP_REQUIRED';

export type RootType = 'Asset' | 'Liability' | 'Equity' | 'Revenue' | 'Expense';
export const ROOT_TYPES: RootType[] = ['Asset', 'Liability', 'Equity', 'Revenue', 'Expense'];

export type AccountType =
  | 'Cash' | 'Bank' | 'Receivable' | 'OtherCurrentAsset' | 'FixedAsset' | 'AccumulatedDepreciation'
  | 'Payable' | 'OtherCurrentLiability' | 'LongTermLiability' | 'Equity' | 'Revenue' | 'OtherIncome'
  | 'CostOfGoodsSold' | 'Expense' | 'OtherExpense' | 'Tax' | 'Depreciation';

export type SetupStatusCode = 'NotStarted' | 'AccountsCreated' | 'FiscalYearSet' | 'DefaultsSet' | 'BalancesEntered' | 'Complete';
export type PeriodStatus = 'Open' | 'Locked' | 'Closed';
export type SyncDirection = 'None' | 'Inbound' | 'Outbound' | 'Bidirectional';
export type SystemType =
  | 'QUICKBOOKS_ONLINE' | 'QUICKBOOKS_DESKTOP' | 'XERO' | 'ERPNEXT' | 'TALLY' | 'SAGE' | 'SAP_B1' | 'DYNAMICS_BC'
  | 'ZOHO_BOOKS' | 'WAVE' | 'ODOO' | 'CUSTOM';
export type FileFormat = 'csv' | 'json';

// ── Templates ─────────────────────────────────────────────────────────────────

export interface CoaTemplate {
  uuid: string;
  templateCode: string;
  templateName: string;
  countryCode?: string | null;
  businessType?: string | null;
  currencyCode?: string | null;
  description?: string | null;
  accountCount: number;
  version: number | string;
  isSystem: boolean;
}

export interface TemplateFilters {
  countries: { code: string; name: string }[];
  businessTypes: string[];
}

export interface TemplateItem {
  accountCode: string;
  accountName: string;
  parentCode?: string | null;
  level: number;
  rootType: RootType;
  isGroup: boolean;
  accountType?: AccountType | null;
  description?: string | null;
  isDefaultAccount: boolean;
  defaultAccountKey?: string | null;
}

export interface AccountNode {
  code: string;
  name: string;
  rootType: RootType;
  level: number;
  isGroup: boolean;
  accountType?: AccountType | null;
  description?: string | null;
  children: AccountNode[];
}

export interface TemplateQuery { countryCode?: string | null; businessType?: string | null; search?: string | null; }

// ── Chart of accounts ─────────────────────────────────────────────────────────

export interface Account {
  uuid: string;
  accountCode: string;
  accountName: string;
  parentUuid?: string | null;
  parentCode?: string | null;
  materializedPath: string;
  level: number;
  rootType: RootType;
  isGroup: boolean;
  accountType?: AccountType | null;
  description?: string | null;
  externalCode?: string | null;
  isSystemAccount: boolean;
  isActive: boolean;
  hasTransactions: boolean;
  openingBalanceDebit: number;
  openingBalanceCredit: number;
  currentBalance: number;
  sortOrder: number;
}

export interface AccountTreeNode extends Account {
  children: AccountTreeNode[];
  /** When the server sends them on tree nodes (BE1), they win over the client-side reason. */
  canDelete?: boolean | null;
  deleteBlockedReason?: string | null;
}

export interface AccountDetail extends Account {
  parent?: { uuid: string; accountCode: string; accountName: string } | null;
  childrenCount: number;
  canDelete: boolean;
  deleteBlockedReason?: string | null;
  createdAt: string;
  modifiedAt?: string | null;
}

export interface CoaSummary { totalAccounts: number; postableAccounts: number; lastModifiedAt?: string | null; }
export interface CoaTree { roots: AccountTreeNode[]; summary: CoaSummary; }

export interface AccountQuery {
  level?: number | null; rootType?: RootType | null; isGroup?: boolean | null; isActive?: boolean | null;
  search?: string | null; accountType?: AccountType | null;
}

export interface AccountCreate {
  parentUuid: string;
  accountCode: string;
  accountName: string;
  accountType?: AccountType | null;
  description?: string | null;
  externalCode?: string | null;
  sortOrder?: number | null;
}

export interface AccountUpdate {
  accountName: string;
  accountType?: AccountType | null;
  description?: string | null;
  externalCode?: string | null;
  sortOrder?: number | null;
}

export interface ApplyResult { accountsCreated: number; defaultsAssigned: number; status: SetupStatusCode; }
/** CSV errors carry line (1 = header); JSON errors path (e.g. accounts[0].children[2]) and code. */
export interface UploadError { line?: number | null; code?: string | null; path?: string | null; rule: string; message: string; }

// ── Fiscal years ──────────────────────────────────────────────────────────────

export interface FiscalPeriod {
  uuid: string;
  periodNumber: number;
  periodName: string;
  startDate: string;
  endDate: string;
  status: PeriodStatus;
  /** Period 13 (year-end adjustments). */
  isAdjustment?: boolean;
}

export interface FiscalYear {
  uuid: string;
  fiscalYearName: string;
  startDate: string;
  endDate: string;
  isClosed: boolean;
  reportingCurrencyCode: string;
  periods: FiscalPeriod[];
  hasPostings: boolean;
  closedAt?: string | null;
}

export interface FiscalYearCreate { fiscalYearName: string; startDate: string; endDate: string; reportingCurrencyCode?: string | null; }
export interface FiscalYearUpdate { fiscalYearName: string; reportingCurrencyCode: string; startDate?: string | null; endDate?: string | null; }
export interface FiscalPeriodUpdate { status?: PeriodStatus; periodName?: string; startDate?: string; endDate?: string; }

// ── Defaults ──────────────────────────────────────────────────────────────────

export interface AccountRef { uuid: string; accountCode: string; accountName: string; accountType?: AccountType | null; }

export interface AccountingDefault {
  defaultKey: string;
  defaultLabel: string;
  category: string;
  isRequired: boolean;
  expectedAccountType?: AccountType | null;
  helpText?: string | null;
  isLocked: boolean;
  account?: AccountRef | null;
  /** Per-row result of the server's checks (type mismatch = warning; inactive/group = problem). */
  warning?: string | null;
  problem?: string | null;
}

export interface AccountingDefaults {
  categories: { category: string; items: AccountingDefault[] }[];
  requiredCount: number;
  assignedRequiredCount: number;
}

export interface DefaultsValidation {
  isValid: boolean;
  missingRequired: string[];
  problems: { defaultKey: string; message: string }[];
  warnings: { defaultKey: string; message: string }[];
}

export const DEFAULT_OPENING_BALANCE = 'DEFAULT_OPENING_BALANCE';

// ── Opening balances ──────────────────────────────────────────────────────────

export interface OpeningRow {
  accountUuid: string;
  accountCode: string;
  accountName: string;
  accountType?: AccountType | null;
  debitAmount: number;
  creditAmount: number;
  notes?: string | null;
  isPlug: boolean;
  warning?: string | null;
}

export interface OpeningSummary {
  totalDebits: number;
  totalCredits: number;
  difference: number;
  isBalanced: boolean;
  conversionDate?: string | null;
  plugSide?: 'Debit' | 'Credit' | null;
  /** Saved draft entries (BE2). */
  entryCount?: number | null;
}

export interface SubledgerReference {
  customerOpeningTotal: number;
  supplierOpeningTotal: number;
  openingStockValue: number;
  receivableAccountBalance?: number | null;
  payableAccountBalance?: number | null;
  inventoryAccountBalance?: number | null;
  currencyCode?: string | null;
  receivableAccountCode?: string | null;
  payableAccountCode?: string | null;
  inventoryAccountCode?: string | null;
}

export interface OpeningBalances {
  conversionDate?: string | null;
  isPosted: boolean;
  postedAt?: string | null;
  /** Posted with "Skip opening balances (new company)" — no journal. */
  isSkipped?: boolean;
  groups: { rootType: RootType; rows: OpeningRow[] }[];
  summary: OpeningSummary;
  subledger?: SubledgerReference | null;
}

export interface OpeningEntryInput { accountUuid: string; debitAmount: number; creditAmount: number; notes?: string | null; }
export interface PostOpeningResult { journalEntryNumber?: string | null; summary: OpeningSummary; }

// ── Setup status ──────────────────────────────────────────────────────────────

export interface SetupStep { step: number; title: string; done: boolean; }

export interface SetupStatus {
  status: SetupStatusCode;
  templateUuid?: string | null;
  templateName?: string | null;
  templateAppliedAt?: string | null;
  accountsCreatedCount: number;
  fiscalYearConfigured: boolean;
  defaultsConfigured: boolean;
  openingBalancesEntered: boolean;
  openingBalancesBalanced: boolean;
  setupCompletedAt?: string | null;
  setupCompletedByName?: string | null;
  conversionDate?: string | null;
  nextStep: number;
  steps: SetupStep[];
}

export interface SetupHistoryEntry { at: string; eventType: string; summary: string; userName?: string | null; }

export interface GlDashboard {
  totalAssets: number;
  totalLiabilities: number;
  totalEquity: number;
  totalRevenue: number;
  totalExpenses: number;
  currencyCode?: string | null;
  accountCount: number;
  fiscalYear?: { name: string; startDate: string; endDate: string } | null;
}

// ── External mappings ─────────────────────────────────────────────────────────

export interface ExternalMapping {
  uuid: string;
  systemType: SystemType;
  systemName: string;
  account: { uuid: string; accountCode: string; accountName: string };
  externalAccountCode: string;
  externalAccountName?: string | null;
  syncDirection: SyncDirection;
  isActive: boolean;
  lastSyncedAt?: string | null;
  syncStatus?: string | null;
  syncErrorMessage?: string | null;
}

export interface ExternalMappingCreate {
  systemType: SystemType;
  systemName: string;
  accountUuid: string;
  externalAccountCode: string;
  externalAccountName?: string | null;
  syncDirection?: SyncDirection;
  connectionConfigJson?: string | null;
}

export interface ExternalMappingUpdate {
  systemName?: string | null;
  externalAccountCode: string;
  externalAccountName?: string | null;
  syncDirection: SyncDirection;
  isActive: boolean;
}

export interface MappingImportResult { created: number; updated: number; errors: { line: number; message: string }[]; }

// ── Service ───────────────────────────────────────────────────────────────────

function params(values: Record<string, string | number | boolean | null | undefined>): HttpParams {
  let p = new HttpParams();
  for (const [k, v] of Object.entries(values)) {
    if (v === null || v === undefined || v === '') continue;
    p = p.set(k, String(v));
  }
  return p;
}

/** Sums the summary the contract allows the server to leave out (an array of roots instead of `{ roots, summary }`). */
export function summarize(roots: AccountTreeNode[]): CoaSummary {
  let total = 0, postable = 0;
  const walk = (nodes: AccountTreeNode[]) => nodes.forEach(n => {
    total++;
    if (!n.isGroup) postable++;
    walk(n.children ?? []);
  });
  walk(roots);
  return { totalAccounts: total, postableAccounts: postable, lastModifiedAt: null };
}

@Injectable({ providedIn: 'root' })
export class AccountingService {
  private readonly api = `${environment.apiUrl}/finance`;

  /** The org's GL setup status as last seen by any page (menu label, wizard, guard); null until first read. */
  readonly setupState = signal<SetupStatus | null>(null);
  private statusLoading = false;

  constructor(private http: HttpClient) {}

  // Templates
  getTemplates(q: TemplateQuery = {}): Observable<ApiResponse<CoaTemplate[]>> {
    return this.http.get<ApiResponse<CoaTemplate[]>>(`${this.api}/templates`,
      { params: params({ countryCode: q.countryCode, businessType: q.businessType, search: q.search }) });
  }

  getTemplateFilters(): Observable<ApiResponse<TemplateFilters>> {
    return this.http.get<ApiResponse<TemplateFilters>>(`${this.api}/templates/filters`);
  }

  getTemplate(uuid: string): Observable<ApiResponse<CoaTemplate & { accounts: AccountNode[] }>> {
    return this.http.get<ApiResponse<CoaTemplate & { accounts: AccountNode[] }>>(`${this.api}/templates/${uuid}`);
  }

  previewTemplate(uuid: string): Observable<ApiResponse<TemplateItem[]>> {
    return this.http.get<ApiResponse<TemplateItem[]>>(`${this.api}/templates/${uuid}/preview`);
  }

  downloadTemplate(uuid: string, format: FileFormat): Observable<HttpResponse<Blob>> {
    return this.http.get(`${this.api}/templates/${uuid}/download`, { params: params({ format }), responseType: 'blob', observe: 'response' });
  }

  downloadBlankTemplate(format: FileFormat): Observable<HttpResponse<Blob>> {
    return this.http.get(`${this.api}/templates/blank`, { params: params({ format }), responseType: 'blob', observe: 'response' });
  }

  // Chart of accounts
  /** The tree, normalised to `{ roots, summary }` whichever of the two shapes the contract allows comes back. */
  getChart(): Observable<CoaTree> {
    return this.http.get<ApiResponse<CoaTree | AccountTreeNode[]>>(`${this.api}/chart-of-accounts`).pipe(
      map(res => {
        const r = res?.result;
        if (Array.isArray(r)) return { roots: r, summary: summarize(r) };
        const roots = r?.roots ?? [];
        return { roots, summary: r?.summary ?? summarize(roots) };
      }));
  }

  getAccounts(q: AccountQuery = {}): Observable<ApiResponse<Account[]>> {
    return this.http.get<ApiResponse<Account[]>>(`${this.api}/chart-of-accounts/flat`, {
      params: params({ level: q.level, rootType: q.rootType, isGroup: q.isGroup, isActive: q.isActive, search: q.search, accountType: q.accountType })
    });
  }

  /** Active leaf (Level 4) accounts — what defaults, opening balances and mappings accept. */
  getPostableAccounts(): Observable<Account[]> {
    return this.getAccounts({ isGroup: false, isActive: true }).pipe(map(r => r?.result ?? []));
  }

  getAccount(uuid: string): Observable<ApiResponse<AccountDetail>> {
    return this.http.get<ApiResponse<AccountDetail>>(`${this.api}/chart-of-accounts/${uuid}`);
  }

  suggestCode(parentUuid: string): Observable<ApiResponse<{ code: string }>> {
    return this.http.get<ApiResponse<{ code: string }>>(`${this.api}/chart-of-accounts/suggest-code`, { params: params({ parentUuid }) });
  }

  createAccount(body: AccountCreate): Observable<ApiResponse<AccountDetail>> {
    return this.http.post<ApiResponse<AccountDetail>>(`${this.api}/chart-of-accounts`, body);
  }

  updateAccount(uuid: string, body: AccountUpdate): Observable<ApiResponse<AccountDetail>> {
    return this.http.put<ApiResponse<AccountDetail>>(`${this.api}/chart-of-accounts/${uuid}`, body);
  }

  deleteAccount(uuid: string): Observable<ApiResponse<unknown>> {
    return this.http.delete<ApiResponse<unknown>>(`${this.api}/chart-of-accounts/${uuid}`);
  }

  applyTemplate(templateUuid: string): Observable<ApiResponse<ApplyResult>> {
    return this.http.post<ApiResponse<ApplyResult>>(`${this.api}/chart-of-accounts/apply-template`, { templateUuid });
  }

  /** Only the 5 roots + 39/390/3900 Opening Balance Equity; replaces the chart like apply/upload (409 once replacing is closed). */
  startBlankChart(): Observable<ApiResponse<ApplyResult>> {
    return this.http.post<ApiResponse<ApplyResult>>(`${this.api}/chart-of-accounts/start-blank`, {});
  }

  uploadChart(file: File): Observable<ApiResponse<ApplyResult>> {
    const form = new FormData();
    form.append('file', file, file.name);
    return this.http.post<ApiResponse<ApplyResult>>(`${this.api}/chart-of-accounts/upload`, form);
  }

  exportChart(format: FileFormat): Observable<HttpResponse<Blob>> {
    return this.http.get(`${this.api}/chart-of-accounts/export`, { params: params({ format }), responseType: 'blob', observe: 'response' });
  }

  // Fiscal years
  getFiscalYears(): Observable<ApiResponse<FiscalYear[]>> {
    return this.http.get<ApiResponse<FiscalYear[]>>(`${this.api}/fiscal-years`);
  }

  createFiscalYear(body: FiscalYearCreate): Observable<ApiResponse<FiscalYear>> {
    return this.http.post<ApiResponse<FiscalYear>>(`${this.api}/fiscal-years`, body);
  }

  updateFiscalYear(uuid: string, body: FiscalYearUpdate): Observable<ApiResponse<FiscalYear>> {
    return this.http.put<ApiResponse<FiscalYear>>(`${this.api}/fiscal-years/${uuid}`, body);
  }

  closeFiscalYear(uuid: string): Observable<ApiResponse<FiscalYear>> {
    return this.http.post<ApiResponse<FiscalYear>>(`${this.api}/fiscal-years/${uuid}/close`, {});
  }

  getPeriods(yearUuid: string): Observable<ApiResponse<FiscalPeriod[]>> {
    return this.http.get<ApiResponse<FiscalPeriod[]>>(`${this.api}/fiscal-years/${yearUuid}/periods`);
  }

  updatePeriod(uuid: string, body: FiscalPeriodUpdate): Observable<ApiResponse<FiscalPeriod>> {
    return this.http.put<ApiResponse<FiscalPeriod>>(`${this.api}/fiscal-periods/${uuid}`, body);
  }

  // Defaults
  getDefaults(): Observable<ApiResponse<AccountingDefaults>> {
    return this.http.get<ApiResponse<AccountingDefaults>>(`${this.api}/accounting-defaults`);
  }

  setDefault(defaultKey: string, accountUuid: string | null): Observable<ApiResponse<AccountingDefault>> {
    return this.http.put<ApiResponse<AccountingDefault>>(`${this.api}/accounting-defaults/${encodeURIComponent(defaultKey)}`, { accountUuid });
  }

  saveDefaults(items: { defaultKey: string; accountUuid: string | null }[]): Observable<ApiResponse<AccountingDefaults>> {
    return this.http.put<ApiResponse<AccountingDefaults>>(`${this.api}/accounting-defaults`, { items });
  }

  validateDefaults(): Observable<ApiResponse<DefaultsValidation>> {
    return this.http.post<ApiResponse<DefaultsValidation>>(`${this.api}/accounting-defaults/validate`, {});
  }

  // Opening balances
  getOpeningBalances(): Observable<ApiResponse<OpeningBalances>> {
    return this.http.get<ApiResponse<OpeningBalances>>(`${this.api}/opening-balances`);
  }

  saveOpeningBalances(entries: OpeningEntryInput[]): Observable<ApiResponse<OpeningBalances>> {
    return this.http.put<ApiResponse<OpeningBalances>>(`${this.api}/opening-balances/batch`, { entries });
  }

  postOpeningBalances(body: { acknowledgeDifference?: boolean; skip?: boolean }): Observable<ApiResponse<PostOpeningResult>> {
    return this.http.post<ApiResponse<PostOpeningResult>>(`${this.api}/opening-balances/post`, body);
  }

  getOpeningSummary(): Observable<ApiResponse<OpeningSummary>> {
    return this.http.get<ApiResponse<OpeningSummary>>(`${this.api}/opening-balances/summary`);
  }

  // Setup
  getSetupStatus(): Observable<ApiResponse<SetupStatus>> {
    return this.http.get<ApiResponse<SetupStatus>>(`${this.api}/setup/status`).pipe(tap(r => this.remember(r)));
  }

  completeSetup(): Observable<ApiResponse<SetupStatus>> {
    return this.http.post<ApiResponse<SetupStatus>>(`${this.api}/setup/complete`, {}).pipe(tap(r => this.remember(r)));
  }

  resetSetup(): Observable<ApiResponse<SetupStatus>> {
    return this.http.post<ApiResponse<SetupStatus>>(`${this.api}/setup/reset`, {}).pipe(tap(r => this.remember(r)));
  }

  getSetupHistory(): Observable<ApiResponse<SetupHistoryEntry[]>> {
    return this.http.get<ApiResponse<SetupHistoryEntry[]>>(`${this.api}/setup/history`);
  }

  /** Loads the status once for the menu label (fire-and-forget; pages refresh it as they read it). */
  ensureSetupState(): void {
    if (this.setupState() || this.statusLoading) return;
    this.statusLoading = true;
    // Deferred: the menu asks from inside a computed(), where signals must not be written synchronously.
    queueMicrotask(() => this.getSetupStatus().subscribe({
      next: () => this.statusLoading = false,
      error: () => this.statusLoading = false
    }));
  }

  private remember(r: ApiResponse<SetupStatus> | null | undefined): void {
    if (r?.result) this.setupState.set(r.result);
  }

  // Dashboard (gated, D-4)
  getDashboard(): Observable<ApiResponse<GlDashboard>> {
    return this.http.get<ApiResponse<GlDashboard>>(`${this.api}/gl/dashboard`);
  }

  // External mappings
  getMappings(systemType?: SystemType | null): Observable<ApiResponse<ExternalMapping[]>> {
    return this.http.get<ApiResponse<ExternalMapping[]>>(`${this.api}/external-mappings`, { params: params({ systemType }) });
  }

  createMapping(body: ExternalMappingCreate): Observable<ApiResponse<ExternalMapping>> {
    return this.http.post<ApiResponse<ExternalMapping>>(`${this.api}/external-mappings`, body);
  }

  updateMapping(uuid: string, body: ExternalMappingUpdate): Observable<ApiResponse<ExternalMapping>> {
    return this.http.put<ApiResponse<ExternalMapping>>(`${this.api}/external-mappings/${uuid}`, body);
  }

  deleteMapping(uuid: string): Observable<ApiResponse<unknown>> {
    return this.http.delete<ApiResponse<unknown>>(`${this.api}/external-mappings/${uuid}`);
  }

  importMappings(file: File, systemType: SystemType, systemName: string): Observable<ApiResponse<MappingImportResult>> {
    const form = new FormData();
    form.append('file', file, file.name);
    form.append('systemType', systemType);
    form.append('systemName', systemName);
    return this.http.post<ApiResponse<MappingImportResult>>(`${this.api}/external-mappings/import`, form);
  }

  exportMappings(systemType?: SystemType | null): Observable<HttpResponse<Blob>> {
    return this.http.get(`${this.api}/external-mappings/export`, { params: params({ systemType }), responseType: 'blob', observe: 'response' });
  }
}
