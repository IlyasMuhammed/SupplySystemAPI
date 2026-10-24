import { Component, ElementRef, HostListener, OnDestroy, ViewChild, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { MessageService } from 'primeng/api';
import { DialogModule } from 'primeng/dialog';
import { TooltipModule } from 'primeng/tooltip';
import { PosAuth } from '../core/pos-auth.service';
import { CartLine, PosCart, itemLabel } from '../core/pos-cart.service';
import { PosDb } from '../core/pos-db';
import { POS_TOAST_KEY } from '../core/pos-hub.service';
import { isWalkIn } from '../core/pos-payment-rules';
import { KeyboardWedgeDetector, hasBarcodeDetector } from '../core/pos-scanner';
import { CatalogItem, DiscountType, POS_PERM, SyncCustomer } from '../core/pos.models';
import { PosState } from '../core/pos-state.service';
import { PosMoneyPipe, PosQtyPipe } from './pos-money.pipe';
import { PosProductGridComponent } from './pos-product-grid.component';

type MobileTab = 'products' | 'cart' | 'more';

/**
 * A38 FSD §17.2 — the selling screen: categories (incl. favourites), product tiles with stock badges, search by
 * name/SKU/barcode, keyboard-wedge + camera scanning, weighable quantities, cart with discounts (POS_ORDER_DISCOUNT),
 * customer, note and §1 totals. Phones (< 768 px) get a bottom nav: Products, Cart, Pay, More.
 */
@Component({
    selector: 'pos-sell',
    standalone: true,
    imports: [FormsModule, DialogModule, TooltipModule, PosMoneyPipe, PosQtyPipe, PosProductGridComponent],
    styleUrls: ['./pos-sell.component.scss'],
    templateUrl: './pos-sell.component.html'
})
export class PosSellComponent implements OnDestroy {
    readonly state = inject(PosState);
    readonly cart = inject(PosCart);
    readonly auth = inject(PosAuth);
    private readonly db = inject(PosDb);
    private readonly router = inject(Router);
    private readonly messages = inject(MessageService);

    @ViewChild('video') video?: ElementRef<HTMLVideoElement>;

    @ViewChild(PosProductGridComponent) grid?: PosProductGridComponent;

    readonly mobileTab = signal<MobileTab>('products');

    readonly canDiscount = computed(() => this.auth.has(POS_PERM.ORDER_DISCOUNT));
    /** A39: returns / exchanges start from the selling screen too. */
    readonly canReturn = computed(() => this.auth.has(POS_PERM.ORDER_RETURN) && this.state.features().returns);
    readonly cameraSupported = hasBarcodeDetector();
    readonly currency = this.state.currency;

    // dialogs
    readonly weighItem = signal<CatalogItem | null>(null);
    weighQty: number | null = null;
    readonly qtyLine = signal<CartLine | null>(null);
    qtyValue: number | null = null;
    readonly discountTarget = signal<CartLine | 'order' | null>(null);
    discountType: DiscountType = 'PERCENT';
    discountValue: number | null = null;
    readonly customerOpen = signal(false);
    customerQuery = '';
    readonly customerResults = signal<SyncCustomer[]>([]);
    readonly noteOpen = signal(false);
    noteText = '';
    readonly cameraOpen = signal(false);
    readonly cameraError = signal<string | null>(null);

    private readonly wedge = new KeyboardWedgeDetector();
    private stream: MediaStream | null = null;
    private scanTimer?: ReturnType<typeof setInterval>;

    ngOnDestroy(): void {
        this.stopCamera();
        clearTimeout(this.confirmTimer);
    }

    label = itemLabel;

    startReturn(): void {
        void this.router.navigate(['/pos/return']);
    }

    // ── adding items ────────────────────────────────────────────────────────

    tap(item: CatalogItem, barcode?: string | null): void {
        if (item.isWeighable) {
            this.weighItem.set(item);
            this.weighQty = null;
            return;
        }
        this.cart.add(item, 1, barcode);
    }

    confirmWeight(): void {
        const item = this.weighItem();
        if (item && this.weighQty && this.weighQty > 0) this.cart.add(item, this.weighQty);
        this.weighItem.set(null);
    }

    /** Barcode from the wedge scanner, the camera or Enter in the search box. Barcode first, then SKU. */
    async handleCode(code: string): Promise<boolean> {
        const c = code.trim();
        if (!c) return false;
        let item = await this.db.catalog.where('barcode').equals(c).first();
        if (!item) item = await this.db.catalog.where('sku').equalsIgnoreCase(c).first();
        if (!item) {
            this.toast('warn', 'Not found', `No product with barcode or SKU "${c}".`);
            return false;
        }
        if (!item.isActive) {
            this.toast('warn', 'Not available', `${itemLabel(item)} is not available for sale at the POS.`);
            return false;
        }
        this.tap(item, item.barcode === c ? c : null);
        return true;
    }

    /** Enter in the grid's search box without a single match: try it as a barcode / SKU. */
    async searchCode(q: string): Promise<void> {
        if (await this.handleCode(q)) this.grid?.clearSearch();
    }

    @HostListener('document:keydown', ['$event'])
    onKeydown(e: KeyboardEvent): void {
        if (this.anyDialogOpen()) return;
        // Shortcuts: F2 = search / scan, F8 = pay.
        if (e.key === 'F2') {
            e.preventDefault();
            this.grid?.searchBox?.nativeElement.focus();
            return;
        }
        if (e.key === 'F8') {
            e.preventDefault();
            this.pay();
            return;
        }
        const code = this.wedge.handle(e.key, e.timeStamp || performance.now());
        if (!code) return;
        e.preventDefault();
        // The scan may have typed into a focused input: take it back out.
        const el = document.activeElement as HTMLInputElement | null;
        if (el && el.tagName === 'INPUT' && typeof el.value === 'string' && el.value.endsWith(code)) {
            el.value = el.value.slice(0, -code.length);
            if (this.grid && el === this.grid.searchBox?.nativeElement) this.grid.search.set(el.value);
        }
        void this.handleCode(code);
    }

    private anyDialogOpen(): boolean {
        return !!(this.weighItem() || this.qtyLine() || this.discountTarget() || this.customerOpen() || this.noteOpen() || this.cameraOpen());
    }

    // ── cart lines ──────────────────────────────────────────────────────────

    lineTotal(index: number): number {
        return this.cart.totals().lines[index]?.lineTotal ?? 0;
    }

    lineDiscount(index: number): number {
        return this.cart.totals().lines[index]?.discountAmount ?? 0;
    }

    editQty(line: CartLine): void {
        this.qtyLine.set(line);
        this.qtyValue = line.quantity;
    }

    confirmQty(): void {
        const line = this.qtyLine();
        if (line && this.qtyValue !== null) this.cart.setQuantity(line.localLineId, this.qtyValue);
        this.qtyLine.set(null);
    }

    openDiscount(target: CartLine | 'order'): void {
        if (!this.canDiscount()) return;
        this.discountTarget.set(target);
        if (target === 'order') {
            this.discountType = this.cart.orderDiscountType() === 'AMOUNT' ? 'AMOUNT' : 'PERCENT';
            this.discountValue = this.cart.orderDiscountValue() || null;
        } else {
            this.discountType = target.discountType === 'AMOUNT' ? 'AMOUNT' : 'PERCENT';
            this.discountValue = target.discountValue || null;
        }
    }

    applyDiscount(remove = false): void {
        const t = this.discountTarget();
        const type: DiscountType = remove ? 'NONE' : this.discountType;
        const value = remove ? 0 : (this.discountValue ?? 0);
        if (t === 'order') this.cart.setOrderDiscount(type, value);
        else if (t) this.cart.setLineDiscount(t.localLineId, type, value);
        this.discountTarget.set(null);
    }

    // ── customer / note ─────────────────────────────────────────────────────

    async openCustomer(): Promise<void> {
        this.customerOpen.set(true);
        this.customerQuery = '';
        await this.searchCustomers();
    }

    async searchCustomers(): Promise<void> {
        const q = this.customerQuery.trim().toLowerCase();
        const all = await this.db.customers.toArray();
        const hits = all
            .filter(c => c.isActive && !isWalkIn(c))
            .filter(c => !q || c.name.toLowerCase().includes(q) || c.code.toLowerCase().includes(q) || (c.phone ?? '').includes(q) || (c.mobile ?? '').includes(q) || (c.email ?? '').toLowerCase().includes(q))
            .sort((a, b) => a.name.localeCompare(b.name))
            .slice(0, 40);
        this.customerResults.set(hits);
    }

    pickCustomer(c: SyncCustomer | null): void {
        this.cart.customer.set(c);
        this.customerOpen.set(false);
    }

    openNote(): void {
        this.noteText = this.cart.note();
        this.noteOpen.set(true);
    }

    saveNote(): void {
        this.cart.note.set(this.noteText.trim());
        this.noteOpen.set(false);
    }

    /** Units in the cart (a weighed item counts once). */
    readonly itemCount = computed(() => this.cart.lines().reduce((n, l) => n + (l.isWeighable ? 1 : l.quantity), 0));

    /** True for a few seconds after the first tap on Clear when the cart holds more than two lines: the second tap clears. */
    readonly confirmClear = signal(false);
    private confirmTimer?: ReturnType<typeof setTimeout>;

    clearCart(): void {
        if (this.cart.lines().length > 2 && !this.confirmClear()) {
            this.confirmClear.set(true);
            clearTimeout(this.confirmTimer);
            this.confirmTimer = setTimeout(() => this.confirmClear.set(false), 3000);
            return;
        }
        clearTimeout(this.confirmTimer);
        this.confirmClear.set(false);
        this.cart.clear();
    }

    pay(): void {
        if (this.cart.isEmpty()) return;
        void this.router.navigate(['/pos/payment']);
    }

    /** The one-tap buttons offered here: the store's own methods, limited to those that settle a whole total in one go. */
    readonly quickMethods = computed(() => this.state.paymentMethods().filter(m => m === 'CASH' || m === 'CARD'));

    /** Cash exact / Card: the payment page adds the whole total as that tender and completes the sale (every rule still runs there). */
    quickPay(method: 'CASH' | 'CARD'): void {
        if (this.cart.isEmpty()) return;
        void this.router.navigate(['/pos/payment'], { queryParams: { quick: method } });
    }

    // ── camera scanning (BarcodeDetector) ───────────────────────────────────

    async openCamera(): Promise<void> {
        this.cameraError.set(null);
        this.cameraOpen.set(true);
        try {
            this.stream = await navigator.mediaDevices.getUserMedia({ video: { facingMode: 'environment' } });
            setTimeout(() => this.startDetecting(), 0);
        } catch {
            this.cameraError.set('The camera could not be opened. Allow camera access in the browser.');
        }
    }

    private startDetecting(): void {
        const video = this.video?.nativeElement;
        if (!video || !this.stream) return;
        video.srcObject = this.stream;
        void video.play();
        const Detector = (window as unknown as { BarcodeDetector: new () => { detect(src: CanvasImageSource): Promise<{ rawValue: string }[]> } }).BarcodeDetector;
        const detector = new Detector();
        let busy = false;
        this.scanTimer = setInterval(async () => {
            if (busy || video.readyState < 2) return;
            busy = true;
            try {
                const codes = await detector.detect(video);
                if (codes.length && codes[0].rawValue) {
                    this.closeCamera();
                    await this.handleCode(codes[0].rawValue);
                }
            } catch {
                /* keep trying */
            } finally {
                busy = false;
            }
        }, 300);
    }

    closeCamera(): void {
        this.cameraOpen.set(false);
        this.stopCamera();
    }

    private stopCamera(): void {
        clearInterval(this.scanTimer);
        this.stream?.getTracks().forEach(t => t.stop());
        this.stream = null;
    }

    private toast(severity: 'warn' | 'info' | 'error', summary: string, detail: string): void {
        this.messages.add({ key: POS_TOAST_KEY, severity, summary, detail, life: 4000 });
    }
}
