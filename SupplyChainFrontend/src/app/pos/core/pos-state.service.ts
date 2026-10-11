import { Injectable, computed, inject, signal } from '@angular/core';
import { CFG, PosDb } from './pos-db';
import {
    EffectivePolicy,
    FEATURE_POS_CREDIT_NOTES,
    FEATURE_POS_CROSS_STORE_RETURNS,
    PaymentMethod,
    PosStore,
    PosTerminal,
    Shift,
    SyncConfig,
    TerminalConfig
} from './pos.models';

/** A feature is on when the cached code list has it; an unknown list (never fetched) counts as on. */
export function featureOn(codes: string[] | null, code: string): boolean {
    return !codes || codes.includes(code);
}

/**
 * A38 — what this terminal is (bound terminal + store), how it is configured (§7 sync/config) and its current shift.
 * Everything is mirrored into Dexie `config` so a reload while offline restores it.
 */
@Injectable({ providedIn: 'root' })
export class PosState {
    private readonly db = inject(PosDb);

    readonly terminalConfig = signal<TerminalConfig | null>(null);
    readonly syncConfig = signal<SyncConfig | null>(null);
    readonly shift = signal<Shift | null>(null);
    /** Codes from GET tenant/modules/enabled (cached for offline). */
    readonly enabledModules = signal<string[] | null>(null);
    readonly loaded = signal(false);

    readonly terminal = computed<PosTerminal | null>(() => this.syncConfig()?.terminal ?? this.terminalConfig()?.terminal ?? null);
    readonly store = computed<PosStore | null>(() => this.syncConfig()?.store ?? this.terminalConfig()?.store ?? null);
    readonly currency = computed(() => this.syncConfig()?.currency?.code ?? this.store()?.currencyCode ?? '');
    readonly taxPercent = computed(() => this.syncConfig()?.taxCode?.percent ?? this.store()?.defaultTaxPercent ?? 0);
    readonly taxInclusive = computed(() => !!this.store()?.taxInclusivePricing);
    readonly features = computed(() => this.syncConfig()?.features ?? { returns: false, creditSales: false, barcode: true, multiStore: false });
    readonly paymentMethods = computed<PaymentMethod[]>(() => {
        const cfg = this.syncConfig();
        const list = (cfg?.paymentMethods?.length ? cfg.paymentMethods : ['CASH']) as PaymentMethod[];
        // A39: STORE_CREDIT is offered by the payment screen on its own rules; EXCHANGE is internal.
        return list.filter(m => (m !== 'ON_ACCOUNT' || this.features().creditSales) && m !== 'STORE_CREDIT' && m !== 'EXCHANGE');
    });
    /** Shift accepting sales: OPEN, or CLOSING (orders before start-close still sync). The UI sells only while OPEN. */
    readonly shiftOpen = computed(() => this.shift()?.status === 'OPEN');

    /** A39 §5 — the store's effective return policy (pulled with the config; null = none configured / not pulled yet). */
    readonly returnPolicy = signal<EffectivePolicy | null>(null);
    /** A39 — FEATURE_POS_CREDIT_NOTES from the cached module answer (unknown ⇒ allowed; the server re-checks). */
    readonly creditNotesEnabled = computed(() => this.syncConfig()?.features?.creditNotes ?? featureOn(this.enabledModules(), FEATURE_POS_CREDIT_NOTES));
    readonly crossStoreReturnsEnabled = computed(() => this.syncConfig()?.features?.crossStoreReturns ?? featureOn(this.enabledModules(), FEATURE_POS_CROSS_STORE_RETURNS));

    async load(): Promise<void> {
        const [tc, sc, sh, mods, rp] = await Promise.all([
            this.db.getConfig<TerminalConfig>(CFG.terminalConfig),
            this.db.getConfig<SyncConfig>(CFG.syncConfig),
            this.db.getConfig<Shift>(CFG.currentShift),
            this.db.getConfig<string[]>(CFG.enabledModules),
            this.db.getConfig<EffectivePolicy>(CFG.returnPolicy)
        ]);
        this.terminalConfig.set(tc ?? null);
        this.syncConfig.set(sc ?? null);
        this.shift.set(sh ?? null);
        this.enabledModules.set(mods ?? null);
        this.returnPolicy.set(rp ?? null);
        this.loaded.set(true);
    }

    async setReturnPolicy(p: EffectivePolicy | null): Promise<void> {
        this.returnPolicy.set(p);
        if (p) await this.db.setConfig(CFG.returnPolicy, p);
        else await this.db.removeConfig(CFG.returnPolicy);
    }

    async setTerminalConfig(tc: TerminalConfig | null): Promise<void> {
        this.terminalConfig.set(tc);
        if (tc) await this.db.setConfig(CFG.terminalConfig, tc);
        else await this.db.removeConfig(CFG.terminalConfig);
    }

    async setSyncConfig(sc: SyncConfig | null): Promise<void> {
        this.syncConfig.set(sc);
        if (sc) await this.db.setConfig(CFG.syncConfig, sc);
        else await this.db.removeConfig(CFG.syncConfig);
    }

    async setShift(shift: Shift | null): Promise<void> {
        this.shift.set(shift);
        if (shift) await this.db.setConfig(CFG.currentShift, shift);
        else await this.db.removeConfig(CFG.currentShift);
    }

    async setEnabledModules(codes: string[] | null): Promise<void> {
        this.enabledModules.set(codes);
        if (codes) await this.db.setConfig(CFG.enabledModules, codes);
    }
}
