import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { ApiResponse, PaginatedResponse } from './inventory.service';
import { PosPaymentMethod } from './pos-admin.service';

// A39 (POS returns, exchanges & store credit) — the back-office side of docs/pos-returns/API-CONTRACT.md §1, §3 (display), §4.
// "Credit note" in the API = POS **store credit** (CN-…); the UI always says "Store credit" so it is never mistaken for the
// Finance credit note (SCN-…).

// ── Codes (contract §0) ──────────────────────────────────────────────────────
export type ReturnReasonAppliesTo = 'BOTH' | 'RETURN' | 'EXCHANGE';
export type StoreCreditStatus = 'ACTIVE' | 'FULLY_REDEEMED' | 'EXPIRED' | 'CANCELLED';
export type StoreCreditTxnType = 'ISSUE' | 'REDEEM' | 'EXPIRE' | 'CANCEL' | 'ADJUST';
export type ManagerNeed = 'REASON' | 'AMOUNT' | 'WINDOW' | 'CROSS_STORE';

export const APPLIES_TO_OPTIONS: { label: string; value: ReturnReasonAppliesTo }[] = [
  { label: 'Returns and exchanges', value: 'BOTH' },
  { label: 'Returns only', value: 'RETURN' },
  { label: 'Exchanges only', value: 'EXCHANGE' }
];
export const STORE_CREDIT_STATUSES: StoreCreditStatus[] = ['ACTIVE', 'FULLY_REDEEMED', 'EXPIRED', 'CANCELLED'];

/** Refund methods a return can use (contract §0; ORIGINAL is resolved by the client). */
export const POS_REFUND_METHODS: PosPaymentMethod[] = ['CASH', 'CARD', 'MOBILE', 'BANK_TRANSFER', 'CHEQUE', 'ON_ACCOUNT', 'STORE_CREDIT'];

export function appliesToLabel(v: string): string { return APPLIES_TO_OPTIONS.find(o => o.value === v)?.label ?? v; }

/** Why a manager is needed (`result.reasons[]` of "Manager authorization required."). */
export const MANAGER_NEED_LABELS: Record<ManagerNeed, string> = {
  REASON: 'the return reason needs a manager',
  AMOUNT: 'the refund is above what the policy allows without a manager',
  WINDOW: 'the return window has passed',
  CROSS_STORE: 'cross-store returns need a manager at this store'
};
export function managerNeedLabel(code: string): string { return (MANAGER_NEED_LABELS as Record<string, string>)[code] ?? code; }

// ── Reasons & policies (§1) ──────────────────────────────────────────────────
export interface ReturnReason {
  uuid: string;
  code: string;
  name: string;
  description?: string | null;
  requiresNote: boolean;
  requiresManager: boolean;
  appliesTo: ReturnReasonAppliesTo;
  /** % — wins over the policy's fee; 0 waives it; null = the policy's. */
  restockingFeeOverride?: number | null;
  sortOrder: number;
  isActive: boolean;
  modifiedAt?: string | null;
}

export type ReturnReasonUpsert = Omit<ReturnReason, 'uuid' | 'modifiedAt'>;

export function reasonUpsertFrom(r: ReturnReason): ReturnReasonUpsert {
  return {
    code: r.code, name: r.name, description: r.description ?? null, requiresNote: r.requiresNote, requiresManager: r.requiresManager,
    appliesTo: r.appliesTo, restockingFeeOverride: r.restockingFeeOverride ?? null, sortOrder: r.sortOrder, isActive: r.isActive
  };
}

/** code ≤ 20 of A–Z 0–9 _ (contract §1). */
export function isValidReasonCode(code: string | null | undefined): boolean { return /^[A-Z0-9_]{1,20}$/.test(code ?? ''); }

export interface ReturnPolicy {
  uuid: string;
  /** null = the organization default. */
  storeUuid?: string | null;
  storeName?: string | null;
  name: string;
  /** null = unlimited. */
  returnWindowDays?: number | null;
  /** null = same as the return window; 0 = no exchanges. */
  exchangeWindowDays?: number | null;
  requireReceipt: boolean;
  allowNoReceiptExchange: boolean;
  allowCrossStoreReturn: boolean;
  crossStoreRequiresManager: boolean;
  restockingFeePct: number;
  /** null = no limit. */
  maxReturnNoAuth?: number | null;
  refundToOriginalMethod: boolean;
  allowCashRefund: boolean;
  allowCreditNoteRefund: boolean;
  /** null = store credit never expires. */
  creditNoteExpiryDays?: number | null;
  isActive: boolean;
  rowVersion?: string | null;
  modifiedAt?: string | null;
}

export interface EffectivePolicy extends ReturnPolicy { source: 'STORE' | 'ORG_DEFAULT'; }

export interface ReturnPolicyUpsert {
  name: string;
  returnWindowDays: number | null;
  exchangeWindowDays: number | null;
  requireReceipt: boolean;
  allowNoReceiptExchange: boolean;
  allowCrossStoreReturn: boolean;
  crossStoreRequiresManager: boolean;
  restockingFeePct: number;
  maxReturnNoAuth: number | null;
  refundToOriginalMethod: boolean;
  allowCashRefund: boolean;
  allowCreditNoteRefund: boolean;
  creditNoteExpiryDays: number | null;
  isActive: boolean;
  rowVersion?: string | null;
}

export const DEFAULT_RETURN_POLICY: ReturnPolicyUpsert = {
  name: 'Return policy', returnWindowDays: 30, exchangeWindowDays: null, requireReceipt: true, allowNoReceiptExchange: false,
  allowCrossStoreReturn: false, crossStoreRequiresManager: true, restockingFeePct: 0, maxReturnNoAuth: null,
  refundToOriginalMethod: false, allowCashRefund: true, allowCreditNoteRefund: true, creditNoteExpiryDays: null, isActive: true
};

/** The editable part of a policy; `withRowVersion` false when the values are only a template (e.g. org default → new store policy). */
export function policyUpsertFrom(p: ReturnPolicy | null | undefined, withRowVersion = true): ReturnPolicyUpsert {
  if (!p) return { ...DEFAULT_RETURN_POLICY };
  return {
    name: p.name, returnWindowDays: p.returnWindowDays ?? null, exchangeWindowDays: p.exchangeWindowDays ?? null,
    requireReceipt: p.requireReceipt, allowNoReceiptExchange: p.allowNoReceiptExchange, allowCrossStoreReturn: p.allowCrossStoreReturn,
    crossStoreRequiresManager: p.crossStoreRequiresManager, restockingFeePct: p.restockingFeePct ?? 0, maxReturnNoAuth: p.maxReturnNoAuth ?? null,
    refundToOriginalMethod: p.refundToOriginalMethod, allowCashRefund: p.allowCashRefund, allowCreditNoteRefund: p.allowCreditNoteRefund,
    creditNoteExpiryDays: p.creditNoteExpiryDays ?? null, isActive: p.isActive,
    ...(withRowVersion ? { rowVersion: p.rowVersion ?? null } : {})
  };
}

/** Client-side rules of a policy form (contract §1: fee 0–100, days ≥ 0, name required); null when it can be saved. */
export function policyProblem(p: ReturnPolicyUpsert): string | null {
  if (!(p.name ?? '').trim()) return 'A name is required.';
  if ((p.name ?? '').trim().length > 100) return 'The name is at most 100 characters.';
  const fee = p.restockingFeePct ?? 0;
  if (fee < 0 || fee > 100) return 'The restocking fee must be between 0 and 100%.';
  for (const [v, l] of [[p.returnWindowDays, 'return window'], [p.exchangeWindowDays, 'exchange window'],
                        [p.creditNoteExpiryDays, 'store credit expiry']] as [number | null, string][]) {
    if (v != null && v < 0) return `The ${l} cannot be negative.`;
  }
  if (p.maxReturnNoAuth != null && p.maxReturnNoAuth < 0) return 'The amount without a manager cannot be negative.';
  return null;
}

/** The request body: trimmed name, blanks as null. */
export function cleanPolicy(p: ReturnPolicyUpsert): ReturnPolicyUpsert {
  const n = (v: number | null | undefined) => (v === undefined || v === null || (v as any) === '' ? null : v);
  return {
    ...p, name: (p.name ?? '').trim(), returnWindowDays: n(p.returnWindowDays), exchangeWindowDays: n(p.exchangeWindowDays),
    maxReturnNoAuth: n(p.maxReturnNoAuth), creditNoteExpiryDays: n(p.creditNoteExpiryDays), restockingFeePct: p.restockingFeePct ?? 0
  };
}

/** One-line plain summary of a policy, e.g. "30-day returns · exchanges 30 days · receipt required · no restocking fee". */
export function policySummary(p: ReturnPolicy | ReturnPolicyUpsert | null | undefined): string {
  if (!p) return '';
  const parts: string[] = [];
  parts.push(p.returnWindowDays == null ? 'returns any time' : p.returnWindowDays === 0 ? 'no returns' : `${p.returnWindowDays}-day returns`);
  const ex = p.exchangeWindowDays ?? p.returnWindowDays;
  parts.push(ex === 0 ? 'no exchanges' : ex == null ? 'exchanges any time' : `exchanges ${ex} days`);
  parts.push(p.requireReceipt ? 'receipt required' : 'no receipt needed');
  parts.push(p.restockingFeePct ? `${p.restockingFeePct}% restocking fee` : 'no restocking fee');
  if (p.allowCrossStoreReturn) parts.push('accepts other stores\' sales');
  return parts.join(' · ');
}

// ── Store credit (§4) ────────────────────────────────────────────────────────
export interface StoreCredit {
  uuid: string;
  /** CN-{store}-{yyyyMMdd}-{seq}. */
  number: string;
  /** CN-{store}-OFF-… when issued offline (POSR). */
  offlineNumber?: string | null;
  customerUuid?: string | null;
  customerName?: string | null;
  returnOrderUuid?: string | null;
  returnOrderNumber?: string | null;
  originalAmount: number;
  remainingBalance: number;
  redeemedAmount: number;
  currencyCode: string;
  status: StoreCreditStatus;
  issuedAtStoreName: string;
  issuedByName: string;
  issuedAt: string;
  expiresAt?: string | null;
  lastUsedAt?: string | null;
  lastUsedAtStoreName?: string | null;
  note?: string | null;
  modifiedAt?: string | null;
}

export interface StoreCreditTxn {
  uuid: string;
  type: StoreCreditTxnType;
  /** + issue, − redeem / expire / cancel. */
  amount: number;
  balanceAfter: number;
  orderUuid?: string | null;
  orderNumber?: string | null;
  storeName: string;
  performedByName: string;
  performedAt: string;
  note?: string | null;
}

export interface StoreCreditDetail extends StoreCredit { transactions: StoreCreditTxn[]; }

export interface StoreCreditFilter {
  status?: StoreCreditStatus | '' | null;
  customerUuid?: string | null;
  storeUuid?: string | null;
  search?: string | null;
  page?: number;
  pageSize?: number;
}

export interface StoreCreditIssueRequest {
  storeUuid: string;
  customerUuid?: string | null;
  amount: number;
  /** yyyy-MM-dd. */
  expiresAt?: string | null;
  note?: string | null;
  localId?: string | null;
}

/** "Manager authorization required." / "Return window expired (n days)." carry `result.requiresManager` (+ reasons). */
export function managerRequired(err: any): { requiresManager: boolean; reasons: string[] } {
  const r = err?.error?.result ?? err?.result;
  return { requiresManager: !!r?.requiresManager, reasons: Array.isArray(r?.reasons) ? r.reasons : [] };
}

function setIf(params: HttpParams, key: string, value: unknown): HttpParams {
  return value === undefined || value === null || value === '' ? params : params.set(key, String(value));
}

@Injectable({ providedIn: 'root' })
export class PosReturnsAdminService {
  private readonly base = `${environment.apiUrl}/pos`;

  constructor(private http: HttpClient) {}

  // ── Reasons ──
  getReasons(opts: { appliesTo?: ReturnReasonAppliesTo | null; includeInactive?: boolean } = {}): Observable<ApiResponse<ReturnReason[]>> {
    let params = setIf(new HttpParams(), 'appliesTo', opts.appliesTo);
    if (opts.includeInactive) params = params.set('includeInactive', 'true');
    return this.http.get<ApiResponse<ReturnReason[]>>(`${this.base}/return-reasons`, { params });
  }

  createReason(req: ReturnReasonUpsert): Observable<ApiResponse<ReturnReason>> {
    return this.http.post<ApiResponse<ReturnReason>>(`${this.base}/return-reasons`, req);
  }

  updateReason(uuid: string, req: ReturnReasonUpsert): Observable<ApiResponse<ReturnReason>> {
    return this.http.put<ApiResponse<ReturnReason>>(`${this.base}/return-reasons/${uuid}`, req);
  }

  setReasonStatus(uuid: string, isActive: boolean): Observable<ApiResponse<ReturnReason>> {
    return this.http.patch<ApiResponse<ReturnReason>>(`${this.base}/return-reasons/${uuid}/status`, { isActive });
  }

  // ── Policies ──
  getStorePolicy(storeUuid: string): Observable<ApiResponse<EffectivePolicy>> {
    return this.http.get<ApiResponse<EffectivePolicy>>(`${this.base}/stores/${storeUuid}/return-policy`);
  }

  saveStorePolicy(storeUuid: string, req: ReturnPolicyUpsert): Observable<ApiResponse<ReturnPolicy>> {
    return this.http.put<ApiResponse<ReturnPolicy>>(`${this.base}/stores/${storeUuid}/return-policy`, req);
  }

  deleteStorePolicy(storeUuid: string): Observable<ApiResponse<unknown>> {
    return this.http.delete<ApiResponse<unknown>>(`${this.base}/stores/${storeUuid}/return-policy`);
  }

  /** Org default first, then the store overrides. */
  getPolicies(): Observable<ApiResponse<ReturnPolicy[]>> {
    return this.http.get<ApiResponse<ReturnPolicy[]>>(`${this.base}/return-policies`);
  }

  saveDefaultPolicy(req: ReturnPolicyUpsert): Observable<ApiResponse<ReturnPolicy>> {
    return this.http.put<ApiResponse<ReturnPolicy>>(`${this.base}/return-policies/default`, req);
  }

  // ── Store credit ──
  getStoreCredits(filter: StoreCreditFilter = {}): Observable<ApiResponse<PaginatedResponse<StoreCredit>>> {
    let params = new HttpParams().set('page', String(filter.page ?? 1)).set('pageSize', String(filter.pageSize ?? 25));
    params = setIf(params, 'status', filter.status);
    params = setIf(params, 'customerUuid', filter.customerUuid);
    params = setIf(params, 'storeUuid', filter.storeUuid);
    params = setIf(params, 'search', filter.search);
    return this.http.get<ApiResponse<PaginatedResponse<StoreCredit>>>(`${this.base}/credit-notes`, { params });
  }

  getStoreCredit(uuid: string): Observable<ApiResponse<StoreCreditDetail>> {
    return this.http.get<ApiResponse<StoreCreditDetail>>(`${this.base}/credit-notes/${uuid}`);
  }

  getStoreCreditTransactions(uuid: string): Observable<ApiResponse<StoreCreditTxn[]>> {
    return this.http.get<ApiResponse<StoreCreditTxn[]>>(`${this.base}/credit-notes/${uuid}/transactions`);
  }

  getCustomerStoreCredits(customerUuid: string, status?: StoreCreditStatus | null): Observable<ApiResponse<StoreCredit[]>> {
    const params = setIf(new HttpParams(), 'status', status);
    return this.http.get<ApiResponse<StoreCredit[]>>(`${this.base}/customers/${customerUuid}/credit-notes`, { params });
  }

  issueStoreCredit(req: StoreCreditIssueRequest): Observable<ApiResponse<StoreCredit>> {
    return this.http.post<ApiResponse<StoreCredit>>(`${this.base}/credit-notes`, req);
  }

  cancelStoreCredit(uuid: string, note?: string | null): Observable<ApiResponse<StoreCredit>> {
    return this.http.patch<ApiResponse<StoreCredit>>(`${this.base}/credit-notes/${uuid}/cancel`, { note: note ?? null });
  }
}

export const STORE_CREDIT_ROUTE = '/portal/pages/pos/credit-notes';
