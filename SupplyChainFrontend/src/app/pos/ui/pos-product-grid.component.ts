import { Component, ElementRef, ViewChild, computed, effect, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TooltipModule } from 'primeng/tooltip';
import { itemLabel } from '../core/pos-cart.service';
import { PosDb } from '../core/pos-db';
import { CatalogCategory, CatalogItem } from '../core/pos.models';
import { PosSync } from '../core/pos-sync.service';
import { PosMoneyPipe, PosQtyPipe } from './pos-money.pipe';

type CatKey = 'fav' | 'all' | number;

/** Max tiles rendered at once (search narrows further). */
const GRID_LIMIT = 240;

/** At or below this many units a tile's stock chip turns amber. */
const LOW_STOCK = 10;

/** Tile top-edge colours by category — distinct enough to tell neighbouring categories apart at a glance. */
const CATEGORY_PALETTE = ['#2563eb', '#0d9488', '#d97706', '#7c3aed', '#db2777', '#16a34a', '#0891b2', '#ea580c'];

/**
 * A38 §17.2 product grid (search by name/SKU/barcode, categories incl. favourites, tiles with stock badges), shared by the
 * selling screen and the A39 exchange screen. Emits the tapped item; Enter with no single match emits the typed code so
 * the host can look it up as a barcode/SKU.
 */
@Component({
    selector: 'pos-product-grid',
    standalone: true,
    imports: [FormsModule, TooltipModule, PosMoneyPipe, PosQtyPipe],
    styles: [
        `
            :host { display: flex; flex-direction: column; gap: .6rem; min-height: 0; flex: 1 1 auto; }
            .searchbar { display: flex; gap: .5rem; }
            .searchwrap { position: relative; flex: 1 1 auto; min-width: 0; }
            .searchwrap .pos-input { padding-right: 2.6rem; }
            .searchwrap .clr { position: absolute; right: .25rem; top: 50%; transform: translateY(-50%); min-height: 36px; min-width: 36px; padding: 0; border-radius: 50%; }
            /* Category chips: ONE row that scrolls sideways (the wheel scrolls it too) instead of three wrapped rows that ate the
               product area; the fade on the right edge hints that there are more. Pill-shaped, never shrunk. */
            .cats {
                display: flex; flex-wrap: nowrap; gap: .4rem; flex: 0 0 auto; padding: .1rem .1rem .35rem;
                overflow-x: auto; overflow-y: hidden; scrollbar-width: thin; scroll-snap-type: x proximity;
                -webkit-mask-image: linear-gradient(to right, #000 calc(100% - 28px), transparent);
                mask-image: linear-gradient(to right, #000 calc(100% - 28px), transparent);
                .pos-btn {
                    flex: 0 0 auto; white-space: nowrap; min-height: 2.4rem; padding: .3rem .9rem; border-radius: 999px; font-size: .92rem;
                    display: inline-flex; align-items: center; gap: .35rem; scroll-snap-align: start;
                }
            }
            .grid { flex: 1 1 auto; min-height: 0; overflow: auto; display: grid; grid-template-columns: repeat(auto-fill, minmax(140px, 1fr)); gap: .6rem; align-content: start; }
            .tile {
                min-height: 128px; display: flex; flex-direction: column; gap: .25rem; text-align: left; padding: .6rem .7rem;
                border-radius: var(--sms-radius); border: 1px solid var(--sms-border); border-top: 4px solid var(--sms-primary-soft-border);
                background: var(--sms-surface); color: var(--sms-text); font: inherit; cursor: pointer; box-shadow: var(--sms-shadow);
                touch-action: manipulation;
                &:active { transform: scale(.98); }
                img { width: 100%; height: 56px; object-fit: contain; }
                // Names wrap to three lines (they used to be cut at two: "Amoxicillin/Clavu…") and the full name is the tooltip.
                .nm { font-weight: 650; line-height: 1.22; overflow: hidden; display: -webkit-box; -webkit-line-clamp: 3; -webkit-box-orient: vertical; font-size: .95rem; flex: 0 0 auto; }
                .pr { margin-top: auto; font-weight: 750; font-size: 1.05rem; font-variant-numeric: tabular-nums; }
                // Stock as a small coloured chip, same height on every tile: green = fine, amber = low, red = out, grey = not tracked.
                .st { align-self: flex-start; display: inline-flex; align-items: center; gap: .3rem; font-size: .78rem; font-weight: 650; padding: .1rem .5rem; border-radius: 999px;
                      background: var(--sms-ok-soft); color: var(--sms-ok); min-height: 1.4rem; }
                .st.low { background: var(--sms-warn-soft); color: var(--sms-warn); }
                .st.na { background: var(--sms-surface-2); color: var(--sms-text-muted); font-weight: 600; }
                // Not available in stock: red edge and tint plus a bold red label, so it cannot be missed at a glance.
                &.out { background: var(--sms-danger-soft); border-color: var(--sms-danger); }
                &.out .nm { color: var(--sms-danger); }
                .st.out { background: var(--sms-danger); color: #fff; font-size: .8rem; font-weight: 700; }
            }
            .empty { padding: 2rem 1rem; text-align: center; color: var(--sms-text-faint); }
            @media (max-width: 767.98px) { .grid { grid-template-columns: repeat(auto-fill, minmax(110px, 1fr)); } }
        `
    ],
    template: `
        <div class="searchbar">
            <div class="searchwrap">
                <input #searchBox class="pos-input" type="text" placeholder="Search name, SKU or scan a barcode  (F2)" aria-label="Search products" autocomplete="off"
                    [ngModel]="search()" (ngModelChange)="search.set($event)" (keyup.enter)="enter()" (keyup.escape)="clearSearch()" [attr.data-testid]="testIdPrefix() + 'search'" />
                @if (search()) {
                    <button class="pos-btn ghost clr" type="button" (click)="clearSearch(); searchBox.focus()" aria-label="Clear search" [attr.data-testid]="testIdPrefix() + 'search-clear'"><i class="pi pi-times"></i></button>
                }
            </div>
            @if (showCamera()) {
                <button class="pos-btn" type="button" (click)="camera.emit()" pTooltip="Scan with the camera" aria-label="Scan with the camera" [attr.data-testid]="testIdPrefix() + 'camera'"><i class="pi pi-camera"></i></button>
            }
        </div>
        <div class="cats" role="tablist" (wheel)="wheelCats($event)">
            <button class="pos-btn" type="button" role="tab" [class.on]="activeCat() === 'fav'" (click)="activeCat.set('fav'); search.set('')" [attr.data-testid]="testIdPrefix() + 'cat-fav'"><i class="pi pi-star"></i> Favourites</button>
            <button class="pos-btn" type="button" role="tab" [class.on]="activeCat() === 'all'" (click)="activeCat.set('all'); search.set('')">All</button>
            @for (c of categories(); track c.id) {
                @if (categoryCounts().get(c.id) || activeCat() === c.id) {
                    <button class="pos-btn" type="button" role="tab" [class.on]="activeCat() === c.id" (click)="activeCat.set(c.id); search.set('')" [attr.title]="c.name + ' · ' + (categoryCounts().get(c.id) ?? 0) + ' products'">{{ c.name }}</button>
                }
            }
        </div>
        <div class="grid" [attr.data-testid]="testIdPrefix() + 'grid'">
            @for (i of filtered(); track i.variantUuid) {
                <button class="tile" [class.out]="isOut(i)" type="button" (click)="picked.emit({ item: i, barcode: null })" [attr.title]="label(i)" [style.border-top-color]="isOut(i) ? 'var(--sms-danger)' : (i.color || catColor(i))" [attr.data-testid]="testIdPrefix() + 'tile-' + i.sku" [attr.data-stock]="isOut(i) ? 'out' : (isLow(i) ? 'low' : null)">
                    @if (i.imageUrl) { <img [src]="i.imageUrl" alt="" loading="lazy" /> }
                    <span class="nm">{{ label(i) }}</span>
                    <span class="pr">{{ i.price | posMoney }}@if (i.isWeighable) { <span class="faint"> / {{ i.uomCode || 'kg' }}</span> }</span>
                    @if (isOut(i)) {
                        <span class="st out" data-testid="pos-out-of-stock"><i class="pi pi-ban"></i> Out of stock</span>
                    } @else if (i.isStockable === false) {
                        <span class="st na" data-testid="pos-not-tracked">Not stock-tracked</span>
                    } @else {
                        @if (stockOf(i); as s) {
                            <span class="st" [class.low]="isLow(i)">{{ s | posQty }} in stock</span>
                        } @else {
                            <span class="st na">&nbsp;</span>
                        }
                    }
                </button>
            } @empty {
                <div class="empty">{{ search() ? 'Nothing matches "' + search() + '".' : 'No products here yet. They arrive with the next sync.' }}</div>
            }
        </div>
    `
})
export class PosProductGridComponent {
    private readonly db = inject(PosDb);
    private readonly sync = inject(PosSync);

    readonly showCamera = input(false);
    /** data-testid prefix: the selling screen keeps the A38 ids (`pos-`). */
    readonly testIdPrefix = input('pos-');
    readonly picked = output<{ item: CatalogItem; barcode: string | null }>();
    /** Enter in the search box without a single match: the host looks the text up as a barcode / SKU. */
    readonly code = output<string>();
    readonly camera = output<void>();

    @ViewChild('searchBox') searchBox?: ElementRef<HTMLInputElement>;

    readonly items = signal<CatalogItem[]>([]);
    readonly categories = signal<CatalogCategory[]>([]);
    readonly stock = signal<Map<string, number>>(new Map());
    readonly activeCat = signal<CatKey>('all');
    readonly search = signal('');

    /** Products per category, shown on the chips. */
    readonly categoryCounts = computed(() => {
        const counts = new Map<CatalogCategory['id'], number>();
        for (const i of this.items()) if (i.categoryId != null) counts.set(i.categoryId, (counts.get(i.categoryId) ?? 0) + 1);
        return counts;
    });

    readonly filtered = computed(() => {
        const q = this.search().trim().toLowerCase();
        const cat = this.activeCat();
        let list = this.items();
        if (q) {
            list = list.filter(
                i =>
                    i.name.toLowerCase().includes(q) ||
                    (i.posName ?? '').toLowerCase().includes(q) ||
                    (i.variantName ?? '').toLowerCase().includes(q) ||
                    i.sku.toLowerCase().includes(q) ||
                    (i.barcode ?? '').toLowerCase() === q
            );
        } else if (cat === 'fav') list = list.filter(i => i.isFavorite);
        else if (cat !== 'all') list = list.filter(i => i.categoryId === cat);
        return list.slice(0, GRID_LIMIT);
    });

    label = itemLabel;

    constructor() {
        effect(() => {
            this.sync.catalogVersion();
            void this.loadCatalog();
        });
        effect(() => {
            this.sync.stockVersion();
            void this.loadStock();
        });
    }

    private async loadCatalog(): Promise<void> {
        const [items, cats] = await Promise.all([this.db.catalog.toArray(), this.db.categories.toArray()]);
        items.sort((a, b) => (a.sortOrder ?? 0) - (b.sortOrder ?? 0) || itemLabel(a).localeCompare(itemLabel(b)));
        this.items.set(items.filter(i => i.isActive));
        this.categories.set(cats.sort((a, b) => (a.sortOrder ?? 0) - (b.sortOrder ?? 0) || a.name.localeCompare(b.name)));
        if (this.activeCat() === 'all' && items.some(i => i.isFavorite && i.isActive)) this.activeCat.set('fav');
    }

    private async loadStock(): Promise<void> {
        const rows = await this.db.stock.toArray();
        this.stock.set(new Map(rows.map(r => [r.variantUuid, r.available])));
        this.stockLoaded.set(true);
    }

    /** Set once the stock snapshot has been read, so nothing is flagged out of stock before the numbers are known. */
    private readonly stockLoaded = signal(false);

    stockOf(i: CatalogItem): number | null {
        const v = this.stock().get(i.variantUuid);
        return v === undefined ? null : v;
    }

    /**
     * Not available in stock: a stockable item whose available quantity is zero or below — or that has no stock record at
     * all (never received). Items that hold no stock (prepared food, services) are never flagged. Selling stays allowed:
     * the server books it as negative stock — this only tells the cashier.
     */
    isOut(i: CatalogItem): boolean {
        if (i.isStockable === false || !this.stockLoaded()) return false;
        return (this.stockOf(i) ?? 0) <= 0;
    }

    /** Still in stock but running low (1 .. LOW_STOCK): shown in amber so the cashier can warn the customer. */
    isLow(i: CatalogItem): boolean {
        if (i.isStockable === false || !this.stockLoaded()) return false;
        const s = this.stockOf(i);
        return s !== null && s > 0 && s <= LOW_STOCK;
    }

    /** Top-edge colour per category (a stable pick from a small palette), unless the product has its own colour. */
    catColor(i: CatalogItem): string | null {
        if (i.categoryId == null) return null;
        const n = typeof i.categoryId === 'number' ? i.categoryId : [...String(i.categoryId)].reduce((a, c) => a + c.charCodeAt(0), 0);
        return CATEGORY_PALETTE[Math.abs(n) % CATEGORY_PALETTE.length];
    }

    /** The mouse wheel scrolls the one-row category strip sideways. */
    wheelCats(e: WheelEvent): void {
        const el = e.currentTarget as HTMLElement;
        if (el.scrollWidth <= el.clientWidth || Math.abs(e.deltaX) > Math.abs(e.deltaY)) return;
        el.scrollLeft += e.deltaY;
        e.preventDefault();
    }

    enter(): void {
        const q = this.search().trim();
        if (!q) return;
        const list = this.filtered();
        if (list.length === 1) {
            this.picked.emit({ item: list[0], barcode: null });
            this.search.set('');
            return;
        }
        this.code.emit(q);
    }

    clearSearch(): void {
        this.search.set('');
    }
}
