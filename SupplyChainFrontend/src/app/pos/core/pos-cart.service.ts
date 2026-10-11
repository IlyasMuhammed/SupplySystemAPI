import { Injectable, computed, inject, signal } from '@angular/core';
import { calculateOrder } from './pos-calculator';
import { PosAuth } from './pos-auth.service';
import { CatalogItem, DiscountType, SyncCustomer } from './pos.models';
import { PosState } from './pos-state.service';

export interface CartLine {
    localLineId: string;
    variantUuid: string;
    name: string;
    sku: string;
    barcode?: string | null;
    uomCode?: string | null;
    unitPrice: number;
    quantity: number;
    discountType: DiscountType;
    discountValue: number;
    note?: string | null;
    isWeighable: boolean;
}

export function newId(): string {
    return typeof crypto !== 'undefined' && 'randomUUID' in crypto
        ? crypto.randomUUID()
        : `${Date.now().toString(16)}-${Math.random().toString(16).slice(2)}-${Math.random().toString(16).slice(2)}`;
}

/** Display name of a catalog item on the till (posName wins). */
export function itemLabel(i: Pick<CatalogItem, 'posName' | 'name' | 'variantName'>): string {
    if (i.posName) return i.posName;
    return i.variantName ? `${i.name} · ${i.variantName}` : i.name;
}

/**
 * A38 §17.2 — the cart of the selling screen. Totals always come from the §1 calculator with the store's tax rate and
 * inclusive/exclusive mode. A cashier switch clears it (POS-CSH-05).
 */
@Injectable({ providedIn: 'root' })
export class PosCart {
    private readonly state = inject(PosState);

    readonly lines = signal<CartLine[]>([]);
    readonly orderDiscountType = signal<DiscountType>('NONE');
    readonly orderDiscountValue = signal(0);
    /** null ⇒ walk-in. */
    readonly customer = signal<SyncCustomer | null>(null);
    readonly note = signal('');

    readonly itemCount = computed(() => this.lines().reduce((s, l) => s + (l.isWeighable ? 1 : l.quantity), 0));
    readonly isEmpty = computed(() => this.lines().length === 0);
    readonly hasDiscount = computed(() => this.lines().some(l => l.discountType !== 'NONE' && l.discountValue > 0) || (this.orderDiscountType() !== 'NONE' && this.orderDiscountValue() > 0));

    readonly totals = computed(() =>
        calculateOrder({
            lines: this.lines(),
            orderDiscountType: this.orderDiscountType(),
            orderDiscountValue: this.orderDiscountValue(),
            taxPercent: this.state.taxPercent(),
            taxInclusive: this.state.taxInclusive()
        })
    );

    constructor() {
        inject(PosAuth).cashierChanged$.subscribe(() => this.clear());
    }

    /** Adds an item; a plain (non-weighable, undiscounted) line of the same variant is incremented instead. */
    add(item: CatalogItem, quantity = 1, barcode?: string | null): CartLine {
        const qty = item.isWeighable ? round3(quantity) : Math.max(1, Math.round(quantity));
        if (!item.isWeighable) {
            const existing = this.lines().find(l => l.variantUuid === item.variantUuid && !l.isWeighable && l.discountType === 'NONE' && l.unitPrice === item.price);
            if (existing) {
                this.setQuantity(existing.localLineId, existing.quantity + qty);
                return this.lines().find(l => l.localLineId === existing.localLineId)!;
            }
        }
        const line: CartLine = {
            localLineId: newId(),
            variantUuid: item.variantUuid,
            name: itemLabel(item),
            sku: item.sku,
            barcode: barcode ?? item.barcode ?? null,
            uomCode: item.uomCode ?? null,
            unitPrice: item.price,
            quantity: qty,
            discountType: 'NONE',
            discountValue: 0,
            note: null,
            isWeighable: item.isWeighable
        };
        this.lines.update(ls => [...ls, line]);
        return line;
    }

    setQuantity(localLineId: string, quantity: number): void {
        if (!(quantity > 0)) {
            this.remove(localLineId);
            return;
        }
        this.lines.update(ls => ls.map(l => (l.localLineId === localLineId ? { ...l, quantity: l.isWeighable ? round3(quantity) : Math.round(quantity) } : l)));
    }

    increment(localLineId: string, by = 1): void {
        const l = this.lines().find(x => x.localLineId === localLineId);
        if (l) this.setQuantity(localLineId, l.quantity + by);
    }

    remove(localLineId: string): void {
        this.lines.update(ls => ls.filter(l => l.localLineId !== localLineId));
    }

    setLineDiscount(localLineId: string, type: DiscountType, value: number): void {
        const v = type === 'NONE' ? 0 : clampDiscount(type, value);
        this.lines.update(ls => ls.map(l => (l.localLineId === localLineId ? { ...l, discountType: v > 0 ? type : 'NONE', discountValue: v } : l)));
    }

    setLineNote(localLineId: string, note: string | null): void {
        this.lines.update(ls => ls.map(l => (l.localLineId === localLineId ? { ...l, note: note?.trim() || null } : l)));
    }

    setOrderDiscount(type: DiscountType, value: number): void {
        const v = type === 'NONE' ? 0 : clampDiscount(type, value);
        this.orderDiscountType.set(v > 0 ? type : 'NONE');
        this.orderDiscountValue.set(v);
    }

    /** Drops every discount (used when the cashier lacks POS_ORDER_DISCOUNT after a switch-in of a restored cart). */
    clearDiscounts(): void {
        this.lines.update(ls => ls.map(l => ({ ...l, discountType: 'NONE' as DiscountType, discountValue: 0 })));
        this.setOrderDiscount('NONE', 0);
    }

    clear(): void {
        this.lines.set([]);
        this.orderDiscountType.set('NONE');
        this.orderDiscountValue.set(0);
        this.customer.set(null);
        this.note.set('');
    }
}

function round3(v: number): number {
    return Math.round(v * 1000) / 1000;
}

function clampDiscount(type: DiscountType, value: number): number {
    if (!(value > 0)) return 0;
    return type === 'PERCENT' ? Math.min(100, value) : value;
}
