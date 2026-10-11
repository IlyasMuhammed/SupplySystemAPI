import { Injectable, inject } from '@angular/core';
import { PosHttp } from './pos-http.service';
import {
    AuthorizeResult,
    EffectivePolicy,
    ExchangeRequest,
    OrderLookup,
    ReasonsPage,
    ReturnSyncRequest,
    StoreCredit,
    StoreCreditDetail,
    CashDirection,
    CashMovement,
    CashReason,
    CashierSession,
    CatalogPage,
    CustomerCredit,
    CustomerPage,
    Paginated,
    PinSignIn,
    PosOrder,
    PosOrderListItem,
    PosTerminal,
    ReturnRequest,
    Shift,
    ShiftDetail,
    ShiftSummary,
    StaffPin,
    StockSnapshot,
    SyncConfig,
    SyncOrder,
    SyncResult,
    TerminalConfig
} from './pos.models';

/** Typed calls of docs/pos/API-CONTRACT.md used by the PWA. Device-level calls carry the device token; business calls the cashier's. */
@Injectable({ providedIn: 'root' })
export class PosApi {
    private readonly http = inject(PosHttp);

    // ── module state (A37) ──
    enabledModules() {
        return this.http.get<{ modules: string[]; features: string[] }>('tenant/modules/enabled', { auth: 'device' });
    }

    // ── §2 terminals ──
    terminalByDevice(deviceUuid: string) {
        return this.http.get<TerminalConfig>(`pos/terminals/by-device/${encodeURIComponent(deviceUuid)}`, { auth: 'device' });
    }
    availableTerminals() {
        return this.http.get<PosTerminal[]>('pos/terminals/available', { auth: 'device' });
    }
    bindTerminal(terminalUuid: string, deviceUuid: string) {
        return this.http.post<TerminalConfig>(`pos/terminals/${terminalUuid}/bind`, { deviceUuid }, { auth: 'device' });
    }
    unbindTerminal(terminalUuid: string) {
        return this.http.post<PosTerminal>(`pos/terminals/${terminalUuid}/unbind`, {}, { auth: 'cashier' });
    }

    // ── §3 PIN + sessions ──
    pinSignIn(terminalUuid: string, pin: string, userId?: number) {
        return this.http.post<PinSignIn>('pos/auth/pin', { terminalUuid, pin, userId: userId ?? null }, { auth: 'device', timeoutMs: 12000 });
    }
    startSession(shiftUuid: string, localId?: string) {
        return this.http.post<CashierSession>('pos/sessions', { shiftUuid, localId: localId ?? null }, { auth: 'auto' });
    }
    endSession(uuid: string) {
        return this.http.patch<CashierSession>(`pos/sessions/${uuid}/end`, {}, { auth: 'auto' });
    }

    // ── §4 shifts ──
    openShift(terminalUuid: string, openingFloat: number, varianceThreshold?: number | null) {
        return this.http.post<Shift>(`pos/terminals/${terminalUuid}/shifts`, { openingFloat, varianceThreshold: varianceThreshold ?? null }, { auth: 'cashier' });
    }
    currentShift(terminalUuid: string, auth: 'cashier' | 'auto' = 'auto') {
        return this.http.get<Shift>(`pos/terminals/${terminalUuid}/shifts/current`, { auth });
    }
    shiftDetail(uuid: string) {
        return this.http.get<ShiftDetail>(`pos/shifts/${uuid}`, { auth: 'cashier' });
    }
    shiftSummary(uuid: string) {
        return this.http.get<ShiftSummary>(`pos/shifts/${uuid}/summary`, { auth: 'cashier' });
    }
    startClose(uuid: string) {
        return this.http.patch<Shift>(`pos/shifts/${uuid}/start-close`, {}, { auth: 'cashier' });
    }
    resumeShift(uuid: string) {
        return this.http.patch<Shift>(`pos/shifts/${uuid}/resume`, {}, { auth: 'cashier' });
    }
    closeShift(uuid: string, actualCashClosing: number, varianceNote?: string | null) {
        return this.http.patch<Shift>(`pos/shifts/${uuid}/close`, { actualCashClosing, varianceNote: varianceNote || null }, { auth: 'cashier' });
    }
    addCashMovement(shiftUuid: string, body: { direction: CashDirection; amount: number; reason: CashReason; note?: string | null; localId?: string }) {
        return this.http.post<CashMovement>(`pos/shifts/${shiftUuid}/cash-movements`, body, { auth: 'auto' });
    }

    // ── §5 orders ──
    syncOrder(order: SyncOrder, timeoutMs?: number) {
        return this.http.post<SyncResult>('pos/orders/sync', order, { auth: 'auto', timeoutMs });
    }
    syncOrders(orders: SyncOrder[]) {
        return this.http.post<SyncResult[]>('pos/orders/sync/batch', { orders }, { auth: 'auto', timeoutMs: 60000 });
    }
    order(uuid: string) {
        return this.http.get<PosOrder>(`pos/orders/${uuid}`, { auth: 'cashier' });
    }
    /** Same read with cashier-else-device token (queue pushes made while the cashier signed in offline). */
    orderAuto(uuid: string) {
        return this.http.get<PosOrder>(`pos/orders/${uuid}`, { auth: 'auto' });
    }
    orders(params: { terminalUuid?: string; shiftUuid?: string; search?: string; orderType?: string; page?: number; pageSize?: number }) {
        return this.http.get<Paginated<PosOrderListItem>>('pos/orders', { auth: 'cashier', params });
    }
    voidOrder(uuid: string, reason: string) {
        return this.http.patch<PosOrder>(`pos/orders/${uuid}/void`, { reason }, { auth: 'cashier' });
    }
    returnOrder(uuid: string, req: ReturnRequest) {
        return this.http.post<PosOrder>(`pos/orders/${uuid}/return`, req, { auth: 'cashier', timeoutMs: 20000 });
    }

    // ── A39 §2–§5 returns, exchanges, store credit ──
    lookupOrder(receipt: string, storeUuid: string) {
        return this.http.get<OrderLookup>('pos/orders/lookup', { auth: 'cashier', params: { receipt, storeUuid }, timeoutMs: 10000 });
    }
    returnableLines(orderUuid: string, storeUuid: string) {
        return this.http.get<OrderLookup>(`pos/orders/${orderUuid}/returnable-lines`, { auth: 'cashier', params: { storeUuid }, timeoutMs: 10000 });
    }
    authorizeReturn(body: { terminalUuid: string; originalOrderUuid: string; amount: number; managerUserId?: number | null; pin: string }) {
        return this.http.post<AuthorizeResult>('pos/returns/authorize', body, { auth: 'cashier', timeoutMs: 12000 });
    }
    exchangeOrder(uuid: string, req: ExchangeRequest) {
        return this.http.post<PosOrder>(`pos/orders/${uuid}/exchange`, req, { auth: 'cashier', timeoutMs: 20000 });
    }
    syncReturn(req: ReturnSyncRequest) {
        return this.http.post<SyncResult>('pos/returns/sync', req, { auth: 'auto', timeoutMs: 20000 });
    }
    creditNoteByNumber(number: string) {
        return this.http.get<StoreCredit>('pos/credit-notes/lookup', { auth: 'cashier', params: { number }, timeoutMs: 8000 });
    }
    creditNote(uuid: string) {
        return this.http.get<StoreCreditDetail>(`pos/credit-notes/${uuid}`, { auth: 'cashier', timeoutMs: 8000 });
    }
    customerCreditNotes(customerUuid: string, status = 'ACTIVE') {
        return this.http.get<StoreCredit[]>(`pos/customers/${customerUuid}/credit-notes`, { auth: 'cashier', params: { status }, timeoutMs: 8000 });
    }
    syncReturnReasons(since: string | null) {
        return this.http.get<ReasonsPage>('pos/sync/return-reasons', { auth: 'device', params: { since } });
    }
    syncReturnPolicy(terminalUuid: string) {
        return this.http.get<EffectivePolicy>('pos/sync/return-policy', { auth: 'device', params: { terminalUuid } });
    }
    customerCredit(customerUuid: string) {
        return this.http.get<CustomerCredit>(`pos/customers/${customerUuid}/credit`, { auth: 'auto', timeoutMs: 8000 });
    }

    // ── §7 sync ──
    syncConfig(terminalUuid: string) {
        return this.http.get<SyncConfig>('pos/sync/config', { auth: 'device', params: { terminalUuid } });
    }
    syncCatalog(terminalUuid: string, since: string | null, limit = 500) {
        return this.http.get<CatalogPage>('pos/sync/catalog', { auth: 'device', params: { terminalUuid, since, limit }, timeoutMs: 60000 });
    }
    syncStock(terminalUuid: string) {
        return this.http.get<StockSnapshot>('pos/sync/stock', { auth: 'device', params: { terminalUuid }, timeoutMs: 60000 });
    }
    syncCustomers(terminalUuid: string, since: string | null, limit = 500) {
        return this.http.get<CustomerPage>('pos/sync/customers', { auth: 'device', params: { terminalUuid, since, limit }, timeoutMs: 60000 });
    }
    syncUsers(terminalUuid: string, deviceUuid: string) {
        return this.http.get<StaffPin[]>('pos/sync/users', { auth: 'device', params: { terminalUuid }, headers: { 'X-Pos-Device': deviceUuid } });
    }
}
