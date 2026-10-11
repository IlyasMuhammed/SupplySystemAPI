import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { ApiResponse, PaginatedResponse } from './inventory.service';

// A38 (Point of Sale) — the back-office side of api/pos/* (docs/pos/API-CONTRACT.md §2, §4–§6, §8).
// The /pos PWA has its own services; this one only serves the portal screens (stores, shifts, orders, settings).

// ── Codes (contract §0) ──────────────────────────────────────────────────────
export type ShiftStatus = 'OPEN' | 'CLOSING' | 'CLOSED' | 'RECONCILED' | 'DISPUTED';
export type PosRole = 'CASHIER' | 'SUPERVISOR';
/** A39 adds EXCHANGE (negative returning lines + positive new lines in one order). */
export type PosOrderType = 'SALE' | 'RETURN' | 'EXCHANGE';
export type PosOrderStatus = 'COMPLETED' | 'VOIDED' | 'RETURNED' | 'PARTIALLY_RETURNED';
export type PosPaymentStatus = 'PAID' | 'CREDIT_SALE' | 'PARTIAL_CREDIT' | 'REFUNDED';
export type PosDiscountType = 'NONE' | 'PERCENT' | 'AMOUNT';
/** A39 adds STORE_CREDIT (a CN-… store credit as tender / refund) and EXCHANGE (internal exchange tender pair). */
export type PosPaymentMethod = 'CASH' | 'CARD' | 'MOBILE' | 'BANK_TRANSFER' | 'CHEQUE' | 'ON_ACCOUNT' | 'STORE_CREDIT' | 'EXCHANGE';
export type PosSyncType = 'FULL' | 'DELTA';

export const SHIFT_STATUSES: ShiftStatus[] = ['OPEN', 'CLOSING', 'CLOSED', 'RECONCILED', 'DISPUTED'];
export const POS_ORDER_STATUSES: PosOrderStatus[] = ['COMPLETED', 'VOIDED', 'RETURNED', 'PARTIALLY_RETURNED'];
export const POS_PAYMENT_METHODS: PosPaymentMethod[] = ['CASH', 'CARD', 'MOBILE', 'BANK_TRANSFER', 'CHEQUE', 'ON_ACCOUNT'];
export const POS_ROLE_OPTIONS: { label: string; value: PosRole }[] = [
  { label: 'Cashier', value: 'CASHIER' },
  { label: 'Supervisor', value: 'SUPERVISOR' }
];

/** Order flags (contract §0) with a label and pill tone for the back office. */
export const POS_FLAGS: Record<string, { label: string; tone: 'er' | 'wn' | 'in' | 'vi' | ''; hint: string }> = {
  CREDIT_OVER_LIMIT:     { label: 'Over credit limit', tone: 'er', hint: 'Sold on credit offline beyond the customer\'s limit' },
  NEGATIVE_STOCK:        { label: 'Negative stock',    tone: 'wn', hint: 'Sold more than the store warehouse had on hand' },
  OFFLINE:               { label: 'Offline',           tone: 'in', hint: 'Rung up while the terminal was offline, synced later' },
  OFFLINE_CREDIT:        { label: 'Offline credit',    tone: 'wn', hint: 'Credit sale approved offline against the cached balance' },
  PRICE_CHANGED:         { label: 'Price changed',     tone: 'vi', hint: 'The cashier\'s price differs from the current catalog price' },
  DISCOUNT_UNAUTHORIZED: { label: 'Unauthorised discount', tone: 'er', hint: 'Discount given offline by a user without the discount permission' },
  // A39 (API-CONTRACT §0)
  CROSS_STORE:               { label: 'Cross-store',         tone: 'vi', hint: 'Returned at a different store than the one that made the sale' },
  AUTHORIZED_OFFLINE:        { label: 'Authorised offline',  tone: 'in', hint: 'A manager PIN was checked on the device while offline' },
  AUTHORIZATION_INVALID:     { label: 'Authorisation invalid', tone: 'er', hint: 'The manager who authorised this offline return does not hold the override permission' },
  POLICY_OVERRIDDEN_OFFLINE: { label: 'Policy overridden offline', tone: 'wn', hint: 'Done offline although the return policy would have refused it' }
};

export function posFlagLabel(flag: string): string { return POS_FLAGS[flag]?.label ?? flag; }
export function posFlagTone(flag: string): string { return POS_FLAGS[flag]?.tone ?? ''; }

/** Contract §2 — a PIN is 4 to 6 digits. */
export function isValidPin(pin: string | null | undefined): boolean { return /^\d{4,6}$/.test(pin ?? ''); }

/** #RRGGBB (contract §6). */
export function isHexColor(value: string | null | undefined): boolean { return /^#[0-9A-Fa-f]{6}$/.test(value ?? ''); }

/** A client id for idempotent POSTs (return requests). */
export function newLocalId(): string {
  const c: any = (globalThis as any).crypto;
  if (c?.randomUUID) return c.randomUUID();
  return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, ch => {
    const r = (Math.random() * 16) | 0;
    return (ch === 'x' ? r : (r & 0x3) | 0x8).toString(16);
  });
}

// ── Stores, terminals, staff (§2) ────────────────────────────────────────────
export interface PosStore {
  uuid: string;
  code: string;
  name: string;
  warehouseUuid: string;
  warehouseName: string;
  addressLine1?: string | null;
  addressLine2?: string | null;
  city?: string | null;
  stateProvince?: string | null;
  postalCode?: string | null;
  country?: string | null;
  phone?: string | null;
  email?: string | null;
  timezone: string;
  /** Org sale base currency — read-only. */
  currencyCode: string;
  taxInclusivePricing: boolean;
  defaultTaxCodeUuid?: string | null;
  defaultTaxCode?: string | null;
  defaultTaxPercent: number;
  receiptHeader?: string | null;
  receiptFooter?: string | null;
  offlineCreditTolerancePct: number;
  autoCloseShiftHours: number;
  varianceThreshold: number;
  isActive: boolean;
  terminalCount: number;
  activeShiftCount: number;
  createdAt: string;
  modifiedAt?: string | null;
  rowVersion?: string | null;
}

export interface PosStoreUpsert {
  code: string;
  name: string;
  warehouseUuid: string;
  addressLine1?: string | null;
  addressLine2?: string | null;
  city?: string | null;
  stateProvince?: string | null;
  postalCode?: string | null;
  country?: string | null;
  phone?: string | null;
  email?: string | null;
  timezone: string;
  taxInclusivePricing: boolean;
  defaultTaxCodeUuid?: string | null;
  receiptHeader?: string | null;
  receiptFooter?: string | null;
  offlineCreditTolerancePct: number;
  autoCloseShiftHours: number;
  varianceThreshold: number;
  isActive: boolean;
  rowVersion?: string | null;
}

/** The editable part of a store, from a loaded one (for PUT). */
export function storeUpsertFrom(s: PosStore): PosStoreUpsert {
  return {
    code: s.code, name: s.name, warehouseUuid: s.warehouseUuid,
    addressLine1: s.addressLine1 ?? null, addressLine2: s.addressLine2 ?? null, city: s.city ?? null,
    stateProvince: s.stateProvince ?? null, postalCode: s.postalCode ?? null, country: s.country ?? null,
    phone: s.phone ?? null, email: s.email ?? null, timezone: s.timezone,
    taxInclusivePricing: s.taxInclusivePricing, defaultTaxCodeUuid: s.defaultTaxCodeUuid ?? null,
    receiptHeader: s.receiptHeader ?? null, receiptFooter: s.receiptFooter ?? null,
    offlineCreditTolerancePct: s.offlineCreditTolerancePct, autoCloseShiftHours: s.autoCloseShiftHours,
    varianceThreshold: s.varianceThreshold, isActive: s.isActive, rowVersion: s.rowVersion ?? null
  };
}

export interface PosTerminal {
  uuid: string;
  storeUuid: string;
  storeName: string;
  code: string;
  name: string;
  deviceBound: boolean;
  deviceUuid?: string | null;
  lastSeenAt?: string | null;
  lastSyncAt?: string | null;
  /** Seen in the last 15 minutes (server-computed). */
  isOnline: boolean;
  isActive: boolean;
  activeShiftUuid?: string | null;
  activeShiftNumber?: string | null;
  pinLockedUntil?: string | null;
  rowVersion?: string | null;
}

export interface PosTerminalConfig { terminal: PosTerminal; store: PosStore; serverTime: string; }

export interface PosTerminalStatus {
  isOnline: boolean;
  lastSeenAt?: string | null;
  lastSyncAt?: string | null;
  deviceBound: boolean;
  activeShiftUuid?: string | null;
  pinLockedUntil?: string | null;
}

export interface PosStaffMember {
  userId: number;
  name: string;
  email: string;
  roleName: string;
  posRole: PosRole;
  hasPin: boolean;
  isActive: boolean;
}

export interface PosStaffCandidate { userId: number; name: string; email: string; roleName: string; }

// ── Shifts (§4) ──────────────────────────────────────────────────────────────
export interface PosCashierSession {
  uuid: string;
  shiftUuid: string;
  terminalUuid: string;
  userId: number;
  userName: string;
  startedAt: string;
  endedAt?: string | null;
  isActive: boolean;
  cashSales: number;
  cardSales: number;
  mobileSales: number;
  creditSales: number;
  refundTotal: number;
  orderCount: number;
}

export interface PosCashMovement {
  uuid: string;
  direction: 'IN' | 'OUT';
  amount: number;
  reason: string;
  note?: string | null;
  performedByName: string;
  performedAt: string;
  localId?: string | null;
}

export interface PosShift {
  uuid: string;
  shiftNumber: string;
  storeUuid: string;
  storeName: string;
  terminalUuid: string;
  terminalName: string;
  status: ShiftStatus;
  openedByName: string;
  openedAt: string;
  closingStartedAt?: string | null;
  closedByName?: string | null;
  closedAt?: string | null;
  reconciledByName?: string | null;
  reconciledAt?: string | null;
  openingFloat: number;
  totalCashSales: number;
  totalCardSales: number;
  totalMobileSales: number;
  totalOtherSales: number;
  totalCreditSales: number;
  totalCashRefunds: number;
  totalOtherRefunds: number;
  totalCashIn: number;
  totalCashOut: number;
  expectedCashClosing: number;
  actualCashClosing?: number | null;
  cashVariance?: number | null;
  varianceThreshold: number;
  /** The cashier's note at close. */
  varianceNote?: string | null;
  /** Append-only log of reconcile / supervisor notes, lines "[yyyy-MM-dd HH:mm UTC] name: text" (POSA). */
  reconcileNotes?: string | null;
  orderCount: number;
  refundCount: number;
  grossTotal: number;
  netTotal: number;
  /** A39 §7 — store credit issued (refunds as STORE_CREDIT + standalone) and redeemed (sales paid by store credit) in this shift. */
  totalStoreCreditIssued?: number | null;
  totalStoreCreditRedeemed?: number | null;
  rowVersion?: string | null;
}

export interface PosShiftDetail extends PosShift { sessions: PosCashierSession[]; cashMovements: PosCashMovement[]; }

export interface PosShiftSummary {
  shift: PosShift;
  byMethod: { method: string; sales: number; refunds: number; net: number }[];
  byCashier: PosCashierSession[];
}

export interface PosShiftFilter {
  storeUuid?: string | null;
  terminalUuid?: string | null;
  status?: ShiftStatus | '' | null;
  /** yyyy-MM-dd */
  from?: string | null;
  to?: string | null;
  page?: number;
  pageSize?: number;
}

/** |variance| above the shift's threshold. */
export function shiftVarianceExceeded(s: Pick<PosShift, 'cashVariance' | 'varianceThreshold'>): boolean {
  return s.cashVariance != null && Math.abs(s.cashVariance) > (s.varianceThreshold ?? 0);
}

// ── Orders (§5) ──────────────────────────────────────────────────────────────
export interface PosOrderLine {
  uuid: string;
  lineNumber: number;
  variantUuid: string;
  productName: string;
  variantName?: string | null;
  sku: string;
  barcode?: string | null;
  quantity: number;
  unitPrice: number;
  discountType: PosDiscountType;
  discountValue: number;
  /** Line + allocated order discount. */
  discountAmount: number;
  netAmount: number;
  taxPercent: number;
  taxAmount: number;
  lineTotal: number;
  originalLineUuid?: string | null;
  returnedQuantity: number;
  returnableQuantity: number;
  note?: string | null;
}

export interface PosPayment {
  uuid: string;
  method: PosPaymentMethod;
  amount: number;
  changeGiven: number;
  reference?: string | null;
  isRefund: boolean;
  customerPaymentUuid?: string | null;
  /** A39 — the store credit redeemed (sale) or issued (refund) by a STORE_CREDIT row. */
  creditNoteUuid?: string | null;
  createdAt: string;
}

export interface PosOrder {
  uuid: string;
  orderNumber: string;
  localId: string;
  offlineNumber?: string | null;
  orderType: PosOrderType;
  status: PosOrderStatus;
  storeUuid: string;
  storeName: string;
  terminalUuid: string;
  terminalName: string;
  shiftUuid: string;
  shiftNumber: string;
  sessionUuid?: string | null;
  cashierUserId: number;
  cashierName: string;
  customerUuid: string;
  customerCode: string;
  customerName: string;
  isWalkIn: boolean;
  lines: PosOrderLine[];
  payments: PosPayment[];
  subtotal: number;
  discountType: PosDiscountType;
  discountValue: number;
  discountAmount: number;
  taxTotal: number;
  grandTotal: number;
  paidAmount: number;
  changeAmount: number;
  balanceDue: number;
  paymentStatus: PosPaymentStatus;
  taxInclusive: boolean;
  taxCode?: string | null;
  taxPercent: number;
  originalOrderUuid?: string | null;
  originalOrderNumber?: string | null;
  returnReason?: string | null;
  voidReason?: string | null;
  note?: string | null;
  isOffline: boolean;
  flags: string[];
  stockPosted: boolean;
  financePosted: boolean;
  postingError?: string | null;
  salesInvoiceUuid?: string | null;
  salesInvoiceNumber?: string | null;
  creditNoteUuid?: string | null;
  creditNoteNumber?: string | null;
  createdAt: string;
  syncedAt: string;
  createdAtLocal: string;
  returns: { uuid: string; orderNumber: string; grandTotal: number; orderType?: PosOrderType }[];
  // ── A39 (API-CONTRACT §3) ──
  returnReasonUuid?: string | null;
  returnReasonName?: string | null;
  authorizedByName?: string | null;
  authorizedAt?: string | null;
  restockingFeeAmount?: number | null;
  isCrossStore?: boolean;
  originalStoreName?: string | null;
  /** The store credit issued by this return / exchange (CN-…). */
  storeCredit?: { uuid: string; number: string; amount: number; balance: number; expiresAt?: string | null } | null;
  /** Exchange: new total − (return total − fee). */
  netAmount?: number | null;
  settlement?: 'PAY' | 'EVEN' | 'REFUND' | null;
}

export interface PosOrderListItem {
  uuid: string;
  orderNumber: string;
  orderType: PosOrderType;
  status: PosOrderStatus;
  storeName: string;
  terminalName: string;
  cashierName: string;
  customerName: string;
  grandTotal: number;
  paymentStatus: PosPaymentStatus;
  flags: string[];
  createdAt: string;
  isOffline: boolean;
  financePosted: boolean;
  stockPosted: boolean;
}

export interface PosOrderFilter {
  storeUuid?: string | null;
  terminalUuid?: string | null;
  shiftUuid?: string | null;
  customerUuid?: string | null;
  status?: PosOrderStatus | '' | null;
  orderType?: PosOrderType | '' | null;
  /** yyyy-MM-dd */
  from?: string | null;
  to?: string | null;
  search?: string | null;
  page?: number;
  pageSize?: number;
}

export interface PosReturnRequest {
  localId: string;
  /** POSB — the open shift the return is booked to; omitted ⇒ the server's default. */
  shiftUuid?: string | null;
  /** A39 — the terminal of that shift (contract §3 ReturnRequest). */
  terminalUuid?: string | null;
  /** A38 free-text reason; A39 sends the reason's name (+ note) here too so either server version reads it. */
  reason: string;
  lines: { originalLineUuid: string; quantity: number }[];
  /** A38 shape; A39 sends one row = the refund after the restocking fee (mirrors refundMethod / refundReference). */
  refunds: { method: PosPaymentMethod; amount: number; reference?: string | null }[];
  // ── A39 (API-CONTRACT §3) ──
  reasonUuid?: string | null;
  note?: string | null;
  authorizationToken?: string | null;
  refundMethod?: PosPaymentMethod | null;
  refundReference?: string | null;
  /** STORE_CREDIT only: the holder; null = bearer. Omitted ⇒ the original customer (walk-in ⇒ bearer). */
  storeCreditCustomerUuid?: string | null;
}

// ── POS catalog / customer settings (§6) ─────────────────────────────────────
export interface ProductPosSettings {
  posName?: string | null;
  /** Inventory product category id; null = the product's own category. */
  posCategoryId?: number | null;
  /** #RRGGBB */
  color?: string | null;
  imageUrl?: string | null;
  isWeighable: boolean;
  isFavorite: boolean;
  sortOrder: number;
  isPosActive: boolean;
  modifiedAt?: string | null;
}

export interface CustomerPosSettings {
  allowPosCredit: boolean;
  offlineCreditLimitOverride?: number | null;
  maxSingleCreditOrder?: number | null;
  requiresIdForCredit: boolean;
  notes?: string | null;
  modifiedAt?: string | null;
}

export const DEFAULT_PRODUCT_POS_SETTINGS: ProductPosSettings = {
  posName: null, posCategoryId: null, color: null, imageUrl: null,
  isWeighable: false, isFavorite: false, sortOrder: 0, isPosActive: true
};

export const DEFAULT_CUSTOMER_POS_SETTINGS: CustomerPosSettings = {
  allowPosCredit: false, offlineCreditLimitOverride: null, maxSingleCreditOrder: null, requiresIdForCredit: false, notes: null
};

// ── Store reports (computed client-side from the shifts list) ────────────────
export interface PosDailyTotals {
  /** yyyy-MM-dd (local date of the shift's opening). */
  date: string;
  shifts: number;
  orders: number;
  refunds: number;
  cash: number;
  card: number;
  mobile: number;
  other: number;
  credit: number;
  cashRefunds: number;
  otherRefunds: number;
  gross: number;
  net: number;
  variance: number;
}

function localYmd(iso: string): string {
  const d = new Date(iso);
  if (isNaN(d.getTime())) return (iso ?? '').substring(0, 10);
  const p = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
}

const r2 = (n: number) => Math.round((n + Number.EPSILON) * 100) / 100;

/** Per-day totals of a list of shifts (newest day first), plus a grand total row computed by the caller if needed. */
export function dailyTotals(shifts: readonly PosShift[]): PosDailyTotals[] {
  const days = new Map<string, PosDailyTotals>();
  for (const s of shifts) {
    const key = localYmd(s.openedAt);
    const d = days.get(key) ?? {
      date: key, shifts: 0, orders: 0, refunds: 0, cash: 0, card: 0, mobile: 0, other: 0, credit: 0,
      cashRefunds: 0, otherRefunds: 0, gross: 0, net: 0, variance: 0
    };
    d.shifts++;
    d.orders += s.orderCount ?? 0;
    d.refunds += s.refundCount ?? 0;
    d.cash = r2(d.cash + (s.totalCashSales ?? 0));
    d.card = r2(d.card + (s.totalCardSales ?? 0));
    d.mobile = r2(d.mobile + (s.totalMobileSales ?? 0));
    d.other = r2(d.other + (s.totalOtherSales ?? 0));
    d.credit = r2(d.credit + (s.totalCreditSales ?? 0));
    d.cashRefunds = r2(d.cashRefunds + (s.totalCashRefunds ?? 0));
    d.otherRefunds = r2(d.otherRefunds + (s.totalOtherRefunds ?? 0));
    d.gross = r2(d.gross + (s.grossTotal ?? 0));
    d.net = r2(d.net + (s.netTotal ?? 0));
    d.variance = r2(d.variance + (s.cashVariance ?? 0));
    days.set(key, d);
  }
  return [...days.values()].sort((a, b) => b.date.localeCompare(a.date));
}

function setIf(params: HttpParams, key: string, value: unknown): HttpParams {
  return value === undefined || value === null || value === '' ? params : params.set(key, String(value));
}

@Injectable({ providedIn: 'root' })
export class PosAdminService {
  private readonly base = `${environment.apiUrl}/pos`;

  constructor(private http: HttpClient) {}

  // ── Stores ──
  getStores(filter: { search?: string | null; isActive?: boolean | null; page?: number; pageSize?: number } = {}):
    Observable<ApiResponse<PaginatedResponse<PosStore>>> {
    let params = new HttpParams().set('page', String(filter.page ?? 1)).set('pageSize', String(filter.pageSize ?? 25));
    params = setIf(params, 'search', filter.search);
    params = setIf(params, 'isActive', filter.isActive);
    return this.http.get<ApiResponse<PaginatedResponse<PosStore>>>(`${this.base}/stores`, { params });
  }

  getStore(uuid: string): Observable<ApiResponse<PosStore>> {
    return this.http.get<ApiResponse<PosStore>>(`${this.base}/stores/${uuid}`);
  }

  createStore(req: PosStoreUpsert): Observable<ApiResponse<PosStore>> {
    return this.http.post<ApiResponse<PosStore>>(`${this.base}/stores`, req);
  }

  updateStore(uuid: string, req: PosStoreUpsert): Observable<ApiResponse<PosStore>> {
    return this.http.put<ApiResponse<PosStore>>(`${this.base}/stores/${uuid}`, req);
  }

  setStoreStatus(uuid: string, isActive: boolean): Observable<ApiResponse<PosStore>> {
    return this.http.patch<ApiResponse<PosStore>>(`${this.base}/stores/${uuid}/status`, { isActive });
  }

  // ── Terminals ──
  getTerminals(storeUuid: string): Observable<ApiResponse<PosTerminal[]>> {
    return this.http.get<ApiResponse<PosTerminal[]>>(`${this.base}/stores/${storeUuid}/terminals`);
  }

  createTerminal(storeUuid: string, req: { code: string; name: string }): Observable<ApiResponse<PosTerminal>> {
    return this.http.post<ApiResponse<PosTerminal>>(`${this.base}/stores/${storeUuid}/terminals`, req);
  }

  updateTerminal(uuid: string, req: { code: string; name: string; isActive: boolean; rowVersion?: string | null }):
    Observable<ApiResponse<PosTerminal>> {
    return this.http.put<ApiResponse<PosTerminal>>(`${this.base}/terminals/${uuid}`, req);
  }

  bindTerminal(uuid: string, deviceUuid: string): Observable<ApiResponse<PosTerminalConfig>> {
    return this.http.post<ApiResponse<PosTerminalConfig>>(`${this.base}/terminals/${uuid}/bind`, { deviceUuid });
  }

  unbindTerminal(uuid: string): Observable<ApiResponse<PosTerminal>> {
    return this.http.post<ApiResponse<PosTerminal>>(`${this.base}/terminals/${uuid}/unbind`, {});
  }

  unlockTerminal(uuid: string): Observable<ApiResponse<PosTerminal>> {
    return this.http.post<ApiResponse<PosTerminal>>(`${this.base}/terminals/${uuid}/unlock`, {});
  }

  getTerminalStatus(uuid: string): Observable<ApiResponse<PosTerminalStatus>> {
    return this.http.get<ApiResponse<PosTerminalStatus>>(`${this.base}/terminals/${uuid}/status`);
  }

  forceSync(uuid: string, syncType: PosSyncType): Observable<ApiResponse<unknown>> {
    return this.http.post<ApiResponse<unknown>>(`${this.base}/terminals/${uuid}/force-sync`, { syncType });
  }

  forceLogout(uuid: string, reason: string): Observable<ApiResponse<unknown>> {
    return this.http.post<ApiResponse<unknown>>(`${this.base}/terminals/${uuid}/force-logout`, { reason });
  }

  // ── Staff ──
  getStaff(storeUuid: string): Observable<ApiResponse<PosStaffMember[]>> {
    return this.http.get<ApiResponse<PosStaffMember[]>>(`${this.base}/stores/${storeUuid}/staff`);
  }

  getStaffCandidates(storeUuid: string, search?: string | null): Observable<ApiResponse<PosStaffCandidate[]>> {
    const params = setIf(new HttpParams(), 'search', search);
    return this.http.get<ApiResponse<PosStaffCandidate[]>>(`${this.base}/stores/${storeUuid}/staff/candidates`, { params });
  }

  /** Adds or updates a staff member; `pin` only when it is being set or reset. */
  saveStaff(storeUuid: string, userId: number, req: { posRole: PosRole; pin?: string | null }): Observable<ApiResponse<PosStaffMember>> {
    const body: { posRole: PosRole; pin?: string } = { posRole: req.posRole };
    if (req.pin) body.pin = req.pin;
    return this.http.put<ApiResponse<PosStaffMember>>(`${this.base}/stores/${storeUuid}/staff/${userId}`, body);
  }

  removeStaff(storeUuid: string, userId: number): Observable<ApiResponse<unknown>> {
    return this.http.delete<ApiResponse<unknown>>(`${this.base}/stores/${storeUuid}/staff/${userId}`);
  }

  // ── Shifts ──
  getShifts(filter: PosShiftFilter = {}): Observable<ApiResponse<PaginatedResponse<PosShift>>> {
    let params = new HttpParams().set('page', String(filter.page ?? 1)).set('pageSize', String(filter.pageSize ?? 25));
    params = setIf(params, 'storeUuid', filter.storeUuid);
    params = setIf(params, 'terminalUuid', filter.terminalUuid);
    params = setIf(params, 'status', filter.status);
    params = setIf(params, 'from', filter.from);
    params = setIf(params, 'to', filter.to);
    return this.http.get<ApiResponse<PaginatedResponse<PosShift>>>(`${this.base}/shifts`, { params });
  }

  getShift(uuid: string): Observable<ApiResponse<PosShiftDetail>> {
    return this.http.get<ApiResponse<PosShiftDetail>>(`${this.base}/shifts/${uuid}`);
  }

  getShiftSummary(uuid: string): Observable<ApiResponse<PosShiftSummary>> {
    return this.http.get<ApiResponse<PosShiftSummary>>(`${this.base}/shifts/${uuid}/summary`);
  }

  getCashMovements(uuid: string): Observable<ApiResponse<PosCashMovement[]>> {
    return this.http.get<ApiResponse<PosCashMovement[]>>(`${this.base}/shifts/${uuid}/cash-movements`);
  }

  /** DISPUTED → RECONCILED; the note is required. */
  reconcileShift(uuid: string, note: string): Observable<ApiResponse<PosShift>> {
    return this.http.patch<ApiResponse<PosShift>>(`${this.base}/shifts/${uuid}/reconcile`, { note });
  }

  /** Append-only note on a RECONCILED or DISPUTED shift (goes into reconcileNotes). */
  appendShiftNote(uuid: string, note: string): Observable<ApiResponse<PosShift>> {
    return this.http.patch<ApiResponse<PosShift>>(`${this.base}/shifts/${uuid}/note`, { note });
  }

  // ── Orders ──
  getOrders(filter: PosOrderFilter = {}): Observable<ApiResponse<PaginatedResponse<PosOrderListItem>>> {
    let params = new HttpParams().set('page', String(filter.page ?? 1)).set('pageSize', String(filter.pageSize ?? 25));
    params = setIf(params, 'storeUuid', filter.storeUuid);
    params = setIf(params, 'terminalUuid', filter.terminalUuid);
    params = setIf(params, 'shiftUuid', filter.shiftUuid);
    params = setIf(params, 'customerUuid', filter.customerUuid);
    params = setIf(params, 'status', filter.status);
    params = setIf(params, 'orderType', filter.orderType);
    params = setIf(params, 'from', filter.from);
    params = setIf(params, 'to', filter.to);
    params = setIf(params, 'search', filter.search);
    return this.http.get<ApiResponse<PaginatedResponse<PosOrderListItem>>>(`${this.base}/orders`, { params });
  }

  getOrder(uuid: string): Observable<ApiResponse<PosOrder>> {
    return this.http.get<ApiResponse<PosOrder>>(`${this.base}/orders/${uuid}`);
  }

  voidOrder(uuid: string, reason: string): Observable<ApiResponse<PosOrder>> {
    return this.http.patch<ApiResponse<PosOrder>>(`${this.base}/orders/${uuid}/void`, { reason });
  }

  /** Returns the RETURN order. */
  returnOrder(uuid: string, req: PosReturnRequest): Observable<ApiResponse<PosOrder>> {
    return this.http.post<ApiResponse<PosOrder>>(`${this.base}/orders/${uuid}/return`, req);
  }

  // ── Settings ──
  getProductSettings(productUuid: string): Observable<ApiResponse<ProductPosSettings>> {
    return this.http.get<ApiResponse<ProductPosSettings>>(`${this.base}/products/${productUuid}/settings`);
  }

  saveProductSettings(productUuid: string, req: ProductPosSettings): Observable<ApiResponse<ProductPosSettings>> {
    return this.http.put<ApiResponse<ProductPosSettings>>(`${this.base}/products/${productUuid}/settings`, req);
  }

  getCustomerSettings(customerUuid: string): Observable<ApiResponse<CustomerPosSettings>> {
    return this.http.get<ApiResponse<CustomerPosSettings>>(`${this.base}/customers/${customerUuid}/settings`);
  }

  saveCustomerSettings(customerUuid: string, req: CustomerPosSettings): Observable<ApiResponse<CustomerPosSettings>> {
    return this.http.put<ApiResponse<CustomerPosSettings>>(`${this.base}/customers/${customerUuid}/settings`, req);
  }
}

/** The first user-facing message of a failed call (400 texts are shown verbatim). */
export function posError(err: any, fallback: string): string {
  return err?.error?.message || fallback;
}

export const POS_ROUTE = '/portal/pages/pos';
