import { signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { PosDb } from '../core/pos-db';
import { CatalogItem } from '../core/pos.models';
import { PosSync } from '../core/pos-sync.service';
import { PosProductGridComponent } from './pos-product-grid.component';

const item = (sku: string, extra: Partial<CatalogItem> = {}): CatalogItem => ({
    variantUuid: `v-${sku}`, productUuid: `p-${sku}`, sku, name: `Product ${sku}`, price: 10, isWeighable: false, isFavorite: false,
    sortOrder: 0, isActive: true, modifiedAt: '2026-10-17T00:00:00Z', ...extra
});

/** The tile grid flags items that are not available in stock — and only those. */
describe('PosProductGridComponent out-of-stock indication', () => {
    let fixture: ComponentFixture<PosProductGridComponent>;

    async function render(items: CatalogItem[], stock: { variantUuid: string; available: number }[]) {
        const db = {
            catalog: { toArray: async () => items }, categories: { toArray: async () => [] }, stock: { toArray: async () => stock }
        };
        TestBed.configureTestingModule({
            imports: [PosProductGridComponent],
            providers: [{ provide: PosDb, useValue: db }, { provide: PosSync, useValue: { catalogVersion: signal(0), stockVersion: signal(0) } }]
        });
        fixture = TestBed.createComponent(PosProductGridComponent);
        fixture.detectChanges();
        await fixture.whenStable();
        fixture.detectChanges();
        return fixture.nativeElement as HTMLElement;
    }

    const tile = (root: HTMLElement, sku: string) => root.querySelector(`[data-testid="pos-tile-${sku}"]`) as HTMLElement;

    it('marks zero, negative and never-stocked items out of stock, and leaves stocked and non-stockable items alone', async () => {
        const root = await render(
            [item('NOREC'), item('ZERO'), item('NEG'), item('OK'), item('FOOD', { isStockable: false })],
            [{ variantUuid: 'v-ZERO', available: 0 }, { variantUuid: 'v-NEG', available: -3 }, { variantUuid: 'v-OK', available: 5 },
             { variantUuid: 'v-FOOD', available: 0 }]
        );
        for (const sku of ['NOREC', 'ZERO', 'NEG']) {
            expect(tile(root, sku).classList).withContext(sku).toContain('out');
            expect(tile(root, sku).textContent).withContext(sku).toContain('Out of stock');
        }
        expect(tile(root, 'OK').classList).not.toContain('out');
        expect(tile(root, 'OK').textContent).toContain('in stock');
        expect(tile(root, 'FOOD').classList).not.toContain('out');
        expect(tile(root, 'FOOD').textContent).not.toContain('Out of stock');
    });

    it('shows low stock in amber, healthy stock plain, and labels items that are not stock-tracked', async () => {
        const root = await render(
            [item('LOW'), item('HIGH'), item('FOOD', { isStockable: false })],
            [{ variantUuid: 'v-LOW', available: 4 }, { variantUuid: 'v-HIGH', available: 60 }]
        );
        expect(tile(root, 'LOW').getAttribute('data-stock')).toBe('low');
        expect(tile(root, 'LOW').querySelector('.st')!.classList).toContain('low');
        expect(tile(root, 'HIGH').getAttribute('data-stock')).toBeNull();
        expect(tile(root, 'HIGH').querySelector('.st')!.classList).not.toContain('low');
        expect(tile(root, 'FOOD').textContent).toContain('Not stock-tracked');
    });

    it('gives every tile its full name as the tooltip and a category colour edge', async () => {
        const root = await render([item('A', { categoryId: 3 as any })], [{ variantUuid: 'v-A', available: 20 }]);
        expect(tile(root, 'A').getAttribute('title')).toContain('Product A');
        expect(tile(root, 'A').style.borderTopColor).not.toBe('');
    });

    it('search has a clear button that appears with text and empties it', async () => {
        const root = await render([item('A')], [{ variantUuid: 'v-A', available: 20 }]);
        expect(root.querySelector('[data-testid="pos-search-clear"]')).toBeNull();
        fixture.componentInstance.search.set('abc');
        fixture.detectChanges();
        const clear = root.querySelector('[data-testid="pos-search-clear"]') as HTMLButtonElement;
        expect(clear).not.toBeNull();
        clear.click();
        fixture.detectChanges();
        expect(fixture.componentInstance.search()).toBe('');
        expect(root.querySelector('[data-testid="pos-search-clear"]')).toBeNull();
    });

    it('the category strip lists the categories without counts', async () => {
        const root = await render([item('A', { categoryId: 7 as any })], [{ variantUuid: 'v-A', available: 20 }]);
        fixture.componentInstance.categories.set([{ id: 7 as any, name: 'Pain Relief', sortOrder: 0 } as any]);
        fixture.detectChanges();
        const chips = Array.from(root.querySelectorAll('.cats button')).map(b => (b.textContent ?? '').trim());
        expect(chips).toContain('Pain Relief');
        expect(chips.every(t => !/\d/.test(t))).toBeTrue();
    });
});
