import { Injectable, inject, signal } from '@angular/core';
import { PosApi } from './pos-api.service';
import { PosAuth } from './pos-auth.service';
import { PosConnectivity } from './pos-connectivity.service';
import { CFG, PosDb } from './pos-db';
import { PosHttpError, errorMessage, isNetworkError } from './pos-http.service';
import { PosHub } from './pos-hub.service';
import { MODULE_POS, TerminalConfig } from './pos.models';
import { PosShifts } from './pos-shift.service';
import { PosState } from './pos-state.service';
import { PosSync } from './pos-sync.service';
import { PosTokens } from './pos-tokens.service';

export type ModuleStatus = 'unknown' | 'enabled' | 'disabled';

/**
 * A38 — start-up of the POS: restore the device state from Dexie, check MODULE_POS (cached for offline), identify the
 * terminal by device (`by-device`), bind from the picker, then activate (sync engine, first full sync, hub, shift).
 */
@Injectable({ providedIn: 'root' })
export class PosBoot {
    private readonly db = inject(PosDb);
    private readonly api = inject(PosApi);
    private readonly state = inject(PosState);
    private readonly tokens = inject(PosTokens);
    private readonly sync = inject(PosSync);
    private readonly hub = inject(PosHub);
    private readonly shifts = inject(PosShifts);
    private readonly auth = inject(PosAuth);
    private readonly conn = inject(PosConnectivity);

    readonly moduleStatus = signal<ModuleStatus>('unknown');
    readonly activated = signal(false);
    readonly activating = signal(false);

    private initP: Promise<void> | null = null;

    constructor() {
        // POSA: unbinding in the back office pushes ForceLogout "UNBOUND" — forget the terminal (keeps the device sign-in).
        this.hub.unbound$.subscribe(() => {
            this.pendingWipe = this.wipeDevice(false);
        });
    }

    /** The wipe started by a ForceLogout UNBOUND (awaited by specs). */
    pendingWipe: Promise<void> | null = null;

    /** Loads the persisted state once (route guard of the /pos tree). */
    init(): Promise<void> {
        if (!this.initP) {
            this.initP = this.state.load().then(() => {
                const mods = this.state.enabledModules();
                if (mods) this.moduleStatus.set(mods.includes(MODULE_POS) ? 'enabled' : 'disabled');
            });
        }
        return this.initP;
    }

    /** MODULE_POS for the device's organization. Offline ⇒ the cached answer (unknown ⇒ allowed). */
    async checkModule(): Promise<ModuleStatus> {
        if (!this.tokens.device()) return this.moduleStatus();
        try {
            const res = await this.api.enabledModules();
            const codes = [...(res?.modules ?? []), ...(res?.features ?? [])];
            await this.state.setEnabledModules(codes);
            this.moduleStatus.set(codes.includes(MODULE_POS) ? 'enabled' : 'disabled');
        } catch (e) {
            if (e instanceof PosHttpError && e.status === 403) this.moduleStatus.set('disabled');
            // network / other: keep the cached status
        }
        return this.moduleStatus();
    }

    /** by-device: returns the bound terminal (cached when offline), or null when this device is not bound. */
    async identifyTerminal(): Promise<TerminalConfig | null> {
        if (!this.tokens.device()) return this.state.terminalConfig();
        try {
            const tc = await this.api.terminalByDevice(this.tokens.deviceUuid());
            await this.state.setTerminalConfig(tc);
            return tc;
        } catch (e) {
            if (e instanceof PosHttpError && e.status === 404) {
                // Unbound elsewhere (back office): forget everything device-specific, PIN hashes first.
                if (this.state.terminalConfig()) await this.wipeDevice(false);
                return null;
            }
            return this.state.terminalConfig();
        }
    }

    async bind(terminalUuid: string): Promise<TerminalConfig> {
        const tc = await this.api.bindTerminal(terminalUuid, this.tokens.deviceUuid());
        await this.state.setTerminalConfig(tc);
        return tc;
    }

    /** Starts sync (full on first activation), hub and shift for the bound terminal. */
    async activate(): Promise<void> {
        const terminal = this.state.terminal();
        if (!terminal || this.activating()) return;
        this.activating.set(true);
        try {
            await this.sync.start();
            if (this.conn.online()) {
                const lastFull = await this.db.getConfig<string>(CFG.lastFullSyncAt);
                if (!lastFull) await this.sync.initialSync();
                else void this.sync.sync('DELTA');
                void this.hub.connect(terminal.uuid);
                await this.shifts.refreshCurrent();
            }
            this.activated.set(true);
        } finally {
            this.activating.set(false);
        }
    }

    /**
     * Unbind (POS_TERMINAL_MANAGE, online) and wipe the device. Refused while orders/cash movements are still queued,
     * so no sale is lost.
     */
    async unbind(): Promise<void> {
        const terminal = this.state.terminal();
        await this.sync.pushAll();
        await this.sync.refreshCounts();
        if (this.sync.pendingCount() > 0 || this.sync.errorCount() > 0) {
            throw new Error('Some sales or cash movements have not reached the server yet. Sync them (or resolve the errors) before unbinding.');
        }
        if (terminal) {
            try {
                await this.api.unbindTerminal(terminal.uuid);
            } catch (e) {
                if (isNetworkError(e)) throw new Error('Unbinding needs a connection.');
                throw new Error(errorMessage(e));
            }
        }
        await this.wipeDevice(true);
    }

    /** Removes every device-local record: PIN hashes, catalog, customers, stock, config, queues (already pushed). */
    async wipeDevice(signOutDevice: boolean): Promise<void> {
        await this.hub.disconnect();
        this.sync.stop();
        this.auth.signOut();
        await this.db.staffPins.clear();
        await Promise.all([this.db.catalog.clear(), this.db.categories.clear(), this.db.stock.clear(), this.db.customers.clear(), this.db.config.clear()]);
        await this.db.sessions.where('syncStatus').equals('synced').delete();
        await this.db.orders.where('syncStatus').equals('synced').delete();
        await this.db.cashMovements.where('syncStatus').equals('synced').delete();
        // A39
        await Promise.all([this.db.returnReasons.clear(), this.db.storeCredits.clear()]);
        await this.db.returns.where('syncStatus').equals('synced').delete();
        await this.state.setReturnPolicy(null);
        await this.state.setTerminalConfig(null);
        await this.state.setSyncConfig(null);
        await this.state.setShift(null);
        this.activated.set(false);
        if (signOutDevice) this.tokens.setDevice(null, null);
        await this.db.log('UNBIND', true, 'Device data wiped.');
    }

    /** Sign the supervisor's device session out (keeps the terminal binding and data). */
    signOutDevice(): void {
        this.auth.signOut();
        this.tokens.setDevice(null, null);
    }
}
