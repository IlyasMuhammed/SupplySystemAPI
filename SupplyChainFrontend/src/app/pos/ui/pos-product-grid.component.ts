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
            .cats { display: flex; gap: .4rem; overflow-x: auto; padding-bottom: .2rem; flex: 0 0 auto; .pos-btn { white-space: nowrap; } }
            .grid { flex: 1 1 auto; min-height: 0; overflow: auto; display: grid; grid-template-columns: repeat(auto-fill, minmax(140px, 1fr)); gap: .6rem; align-content: start; }
            .tile {
                min-height: 110px; display: flex; flex-direction: column; gap: .25rem; text-align: left; padding: .6rem .7rem;
                border-radius: var(--sms-radius); border: 1px solid var(--sms-border); border-top: 4px solid var(--sms-primary-soft-border);
                background: var(--sms-surface); color: var(--sms-text); font: inherit; cursor: pointer; box-shadow: var(--sms-shadow);
                touch-action: manipulation;
                &:active { transform: scale(.98); }
                img { width: 100%; height: 56px; object-fit: contain; }
                .nm { font-weight: 650; line-height: 1.2; overflow: hidden; display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; }
                .pr { margin-top: auto; font-weight: 700; font-variant-numeric: tabular-nums; }
                .st { font-size: .75rem; color: var(--sms-text-faint); &.low { color: var(--sms-warn); font-weight: 650; } }
            }
            .empty { padding: 2rem 1rem; text-align: center; color: var(--sms-text-faint); }
            @media (max-width: 767.98px) { .grid { grid-template-columns: repeat(auto-fill, minmax(110px, 1fr)); } }
        `
    ],
    template: `
        <div class="searchbar">
            <input #searchBox class="pos-input" type="search" placeholder="Search name, SKU or scan a barcode" aria-label="Search products"
                [ngModel]="search()" (ngModelChange)="search.set($event)" (keyup.enter)="enter()" [attr.data-testid]="testIdPrefix() + 'search'" />
            @if (showCamera()) {
                <button class="pos-btn" type="button" (click)="camera.emit()" pTooltip="Scan with the camera" aria-label="Scan with the camera" [attr.data-testid]="testIdPrefix() + 'camera'"><i class="pi pi-camera"></i></button>
            }
        </div>
        <div class="cats" role="tablist">
            <button class="pos-btn" type="button" role="tab" [class.on]="activeCat() === 'fav'" (click)="activeCat.set('fav'); search.set('')" [attr.data-testid]="testIdPrefix() + 'cat-fav'"><i class="pi pi-star"></i> Favourites</button>
            <button class="pos-btn" type="button" role="tab" [class.on]="activeCat() === 'all'" (click)="activeCat.set('all'); search.set('')">All</button>
            @for (c of categories(); track c.id) {
                <button class="pos-btn" type="button" role="tab" [class.on]="activeCat() === c.id" (click)="activeCat.set(c.id); search.set('')">{{ c.name }}</button>
            }
        </div>
        <div class="grid" [attr.data-testid]="testIdPrefix() + 'grid'">
            @for (i of filtered(); track i.variantUuid) {
                <button class="tile" type="button" (click)="picked.emit({ item: i, barcode: null })" [style.border-top-color]="i.color || null" [attr.data-testid]="testIdPrefix() + 'tile-' + i.sku">
                    @if (i.imageUrl) { <img [src]="i.imageUrl" alt="" loading="lazy" /> }
                    <span class="nm">{{ label(i) }}</span>
                    <span class="pr">{{ i.price | posMoney }}@if (i.isWeighable) { <span class="faint"> / {{ i.uomCode || 'kg' }}</span> }</span>
                    @if (stockOf(i); as s) {
                        <span class="st" [class.low]="s <= 0">{{ s <= 0 ? '⚠ ' : '' }}{{ s | posQty }} in stock</span>
                    } @else if (stockOf(i) === 0) {
                        <span class="st low">⚠ 0 in stock</span>
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
    }

    stockOf(i: CatalogItem): number | null {
        const v = this.stock().get(i.variantUuid);
        return v === undefined ? null : v;
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
