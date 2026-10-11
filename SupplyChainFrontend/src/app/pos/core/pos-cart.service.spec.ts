import { TestBed } from '@angular/core/testing';
import { PosAuth } from './pos-auth.service';
import { PosCart } from './pos-cart.service';
import { PosDb } from './pos-db';
import { PosState } from './pos-state.service';
import { catalogItem, clearPosStorage, customer, dropDb, posTestProviders, syncConfig } from './pos-testing';

describe('A38 PosCart', () => {
    let cart: PosCart;
    let state: PosState;

    beforeEach(() => {
        clearPosStorage();
        TestBed.configureTestingModule({ providers: posTestProviders() });
        cart = TestBed.inject(PosCart);
        state = TestBed.inject(PosState);
        state.syncConfig.set(syncConfig({ defaultTaxPercent: 17, taxInclusivePricing: false }));
    });

    afterEach(async () => {
        await dropDb(TestBed.inject(PosDb));
        clearPosStorage();
    });

    it('adds items and merges plain lines of the same variant', () => {
        const tea = catalogItem();
        cart.add(tea);
        cart.add(tea);
        expect(cart.lines().length).toBe(1);
        expect(cart.lines()[0].quantity).toBe(2);
        expect(cart.itemCount()).toBe(2);
    });

    it('keeps weighable items as separate lines with 3-dp quantities', () => {
        const cheese = catalogItem({ variantUuid: 'v-2', sku: 'CHEESE', isWeighable: true, price: 3.99 });
        cart.add(cheese, 1.2345);
        cart.add(cheese, 0.5);
        expect(cart.lines().map(l => l.quantity)).toEqual([1.235, 0.5]);
    });

    it('uses posName on the till', () => {
        cart.add(catalogItem({ posName: 'Chai' }));
        expect(cart.lines()[0].name).toBe('Chai');
    });

    it('removes a line when its quantity goes to zero', () => {
        const line = cart.add(catalogItem());
        cart.increment(line.localLineId, -1);
        expect(cart.isEmpty()).toBeTrue();
    });

    it('computes totals with the store tax rate (§1)', () => {
        cart.add(catalogItem({ price: 100 }), 2);
        const l2 = cart.add(catalogItem({ variantUuid: 'v-2', sku: 'B', price: 50.25 }));
        cart.setLineDiscount(l2.localLineId, 'PERCENT', 10);
        const t = cart.totals();
        expect(t.subtotal).toBe(245.22);
        expect(t.taxTotal).toBe(41.69);
        expect(t.grandTotal).toBe(286.91);
        expect(cart.hasDiscount()).toBeTrue();
    });

    it('follows inclusive pricing', () => {
        state.syncConfig.set(syncConfig({ defaultTaxPercent: 17, taxInclusivePricing: true }));
        cart.add(catalogItem({ price: 117 }));
        expect(cart.totals().grandTotal).toBe(117);
        expect(cart.totals().taxTotal).toBe(17);
    });

    it('clamps a percent discount at 100 and drops non-positive discounts', () => {
        const l = cart.add(catalogItem());
        cart.setLineDiscount(l.localLineId, 'PERCENT', 150);
        expect(cart.lines()[0].discountValue).toBe(100);
        cart.setOrderDiscount('AMOUNT', -5);
        expect(cart.orderDiscountType()).toBe('NONE');
    });

    it('is cleared when the cashier changes (POS-CSH-05)', () => {
        cart.add(catalogItem());
        cart.customer.set(customer());
        cart.note.set('gift');
        TestBed.inject(PosAuth).cashierChanged$.next(null);
        expect(cart.isEmpty()).toBeTrue();
        expect(cart.customer()).toBeNull();
        expect(cart.note()).toBe('');
    });
});
