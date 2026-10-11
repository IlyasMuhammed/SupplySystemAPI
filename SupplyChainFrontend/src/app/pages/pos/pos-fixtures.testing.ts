import { of, throwError } from 'rxjs';
import {
  PosOrder, PosOrderLine, PosShift, PosShiftDetail, PosStaffMember, PosStore, PosTerminal
} from '../../services/pos-admin.service';
import { EffectivePolicy, ReturnPolicy, ReturnReason, StoreCredit, StoreCreditTxn } from '../../services/pos-returns-admin.service';

// ── A39 ──
export function policyFixture(over: Partial<ReturnPolicy> = {}): ReturnPolicy {
  return {
    uuid: 'pol-1', storeUuid: null, storeName: null, name: 'Org policy', returnWindowDays: 30, exchangeWindowDays: null,
    requireReceipt: true, allowNoReceiptExchange: false, allowCrossStoreReturn: false, crossStoreRequiresManager: true,
    restockingFeePct: 0, maxReturnNoAuth: null, refundToOriginalMethod: false, allowCashRefund: true, allowCreditNoteRefund: true,
    creditNoteExpiryDays: 180, isActive: true, rowVersion: 'prv', modifiedAt: null, ...over
  };
}

export function effectivePolicyFixture(over: Partial<EffectivePolicy> = {}): EffectivePolicy {
  return { ...policyFixture(), source: 'ORG_DEFAULT', ...over };
}

export function reasonFixture(over: Partial<ReturnReason> = {}): ReturnReason {
  return {
    uuid: 'rr-1', code: 'DEFECTIVE', name: 'Defective', description: null, requiresNote: false, requiresManager: false, appliesTo: 'BOTH',
    restockingFeeOverride: null, sortOrder: 10, isActive: true, modifiedAt: null, ...over
  };
}

export function storeCreditFixture(over: Partial<StoreCredit> = {}): StoreCredit {
  return {
    uuid: 'cn-1', number: 'CN-S1-20261010-0001', customerUuid: 'c-1', customerName: 'Acme', returnOrderUuid: 'r-1',
    returnOrderNumber: 'RET-S1-20261010-0001', originalAmount: 100, remainingBalance: 60, redeemedAmount: 40, currencyCode: 'PKR',
    status: 'ACTIVE', issuedAtStoreName: 'Mall store', issuedByName: 'Sara', issuedAt: '2026-10-10T10:00:00', expiresAt: null,
    lastUsedAt: null, lastUsedAtStoreName: null, note: null, modifiedAt: null, ...over
  };
}

export function storeCreditTxnFixture(over: Partial<StoreCreditTxn> = {}): StoreCreditTxn {
  return {
    uuid: 'tx-1', type: 'ISSUE', amount: 100, balanceAfter: 100, orderUuid: 'r-1', orderNumber: 'RET-S1-20261010-0001',
    storeName: 'Mall store', performedByName: 'Sara', performedAt: '2026-10-10T10:00:00', note: null, ...over
  };
}

/** A38 — test fixtures for the POS back-office specs. */

export const ok = (result: unknown) => of({ success: true, message: '', result } as any);
export const fail400 = (message: string) => throwError(() => ({ status: 400, error: { message } }));

export function shiftFixture(over: Partial<PosShift> = {}): PosShift {
  return {
    uuid: 'sh-1', shiftNumber: 'SH-S1-20261010-01', storeUuid: 's-1', storeName: 'Mall', terminalUuid: 't-1', terminalName: 'Till 1',
    status: 'CLOSED', openedByName: 'Ali', openedAt: '2026-10-10T09:00:00', openingFloat: 1000,
    totalCashSales: 500, totalCardSales: 200, totalMobileSales: 50, totalOtherSales: 0, totalCreditSales: 100,
    totalCashRefunds: 20, totalOtherRefunds: 0, totalCashIn: 0, totalCashOut: 0, expectedCashClosing: 1480,
    actualCashClosing: 1480, cashVariance: 0, varianceThreshold: 100, orderCount: 10, refundCount: 1, grossTotal: 850, netTotal: 830,
    ...over
  };
}

export function shiftDetailFixture(over: Partial<PosShiftDetail> = {}): PosShiftDetail {
  return {
    ...shiftFixture(),
    sessions: [{ uuid: 'ss-1', shiftUuid: 'sh-1', terminalUuid: 't-1', userId: 7, userName: 'Sara', startedAt: '2026-10-10T09:00:00',
      endedAt: null, isActive: true, cashSales: 500, cardSales: 200, mobileSales: 50, creditSales: 100, refundTotal: 20, orderCount: 10 }],
    cashMovements: [{ uuid: 'cm-1', direction: 'OUT', amount: 200, reason: 'SAFE_DROP', note: 'Noon drop', performedByName: 'Sara',
      performedAt: '2026-10-10T12:00:00' }],
    ...over
  };
}

export function storeFixture(over: Partial<PosStore> = {}): PosStore {
  return {
    uuid: 's-1', code: 'S1', name: 'Mall store', warehouseUuid: 'w-1', warehouseName: 'Main WH', city: 'Lahore', timezone: 'Asia/Karachi',
    currencyCode: 'PKR', taxInclusivePricing: true, defaultTaxCodeUuid: 'tx-1', defaultTaxCode: 'GST17', defaultTaxPercent: 17,
    receiptHeader: 'Welcome', receiptFooter: 'Thanks', offlineCreditTolerancePct: 10, autoCloseShiftHours: 24, varianceThreshold: 100,
    isActive: true, terminalCount: 2, activeShiftCount: 1, createdAt: '2026-10-01T10:00:00Z', modifiedAt: null, rowVersion: 'rv1',
    ...over
  };
}

export function terminalFixture(over: Partial<PosTerminal> = {}): PosTerminal {
  return {
    uuid: 't-1', storeUuid: 's-1', storeName: 'Mall store', code: 'T1', name: 'Till 1', deviceBound: true, deviceUuid: 'dev-1',
    lastSeenAt: null, lastSyncAt: null, isOnline: false, isActive: true, activeShiftUuid: null, activeShiftNumber: null,
    pinLockedUntil: null, rowVersion: 'trv', ...over
  };
}

export function staffFixture(over: Partial<PosStaffMember> = {}): PosStaffMember {
  return { userId: 7, name: 'Sara', email: 'sara@x.pk', roleName: 'Cashier', posRole: 'CASHIER', hasPin: true, isActive: true, ...over };
}

export function lineFixture(over: Partial<PosOrderLine> = {}): PosOrderLine {
  return {
    uuid: 'ln-1', lineNumber: 1, variantUuid: 'v-1', productName: 'Tea', variantName: null, sku: 'TEA-1', barcode: '123',
    quantity: 2, unitPrice: 117, discountType: 'NONE', discountValue: 0, discountAmount: 0, netAmount: 200, taxPercent: 17,
    taxAmount: 34, lineTotal: 234, originalLineUuid: null, returnedQuantity: 0, returnableQuantity: 2, note: null, ...over
  };
}

export function orderFixture(over: Partial<PosOrder> = {}): PosOrder {
  return {
    uuid: 'o-1', orderNumber: 'POS-S1-20261010-0001', localId: 'loc-1', offlineNumber: null, orderType: 'SALE', status: 'COMPLETED',
    storeUuid: 's-1', storeName: 'Mall store', terminalUuid: 't-1', terminalName: 'Till 1', shiftUuid: 'sh-1', shiftNumber: 'SH-S1-20261010-01',
    sessionUuid: null, cashierUserId: 7, cashierName: 'Sara', customerUuid: 'c-1', customerCode: 'C-00001', customerName: 'Acme', isWalkIn: false,
    lines: [lineFixture(), lineFixture({ uuid: 'ln-2', lineNumber: 2, productName: 'Sugar', quantity: 1, unitPrice: 100, lineTotal: 100,
      returnableQuantity: 0, returnedQuantity: 1 })],
    payments: [{ uuid: 'pay-1', method: 'CARD', amount: 334, changeGiven: 0, reference: 'R1', isRefund: false, createdAt: '2026-10-10T10:00:00' }],
    subtotal: 334, discountType: 'NONE', discountValue: 0, discountAmount: 0, taxTotal: 34, grandTotal: 334, paidAmount: 334,
    changeAmount: 0, balanceDue: 0, paymentStatus: 'PAID', taxInclusive: false, taxCode: 'GST17', taxPercent: 17,
    originalOrderUuid: null, originalOrderNumber: null, returnReason: null, voidReason: null, note: null, isOffline: false,
    flags: ['PRICE_CHANGED'], stockPosted: true, financePosted: true, postingError: null,
    salesInvoiceUuid: 'si-1', salesInvoiceNumber: 'SI-2026-00001', creditNoteUuid: null, creditNoteNumber: null,
    createdAt: '2026-10-10T10:00:00', syncedAt: '2026-10-10T10:00:01', createdAtLocal: '2026-10-10T10:00:00', returns: [],
    ...over
  };
}
