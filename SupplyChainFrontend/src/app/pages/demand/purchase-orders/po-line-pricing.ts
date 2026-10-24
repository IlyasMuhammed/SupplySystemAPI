import { FormArray, FormControl, FormGroup } from '@angular/forms';
import { Router } from '@angular/router';
import { MessageService } from 'primeng/api';
import { Subject, Subscription, debounceTime, of, switchMap, map } from 'rxjs';
import {
  PriceSource, PRICE_SOURCE_LABELS, PurchasePricingService, ResolvePriceRequest, ResolvePriceResult,
  PriceConversionInfo, currencyMismatchText, samePrice
} from '../../../services/purchase-pricing.service';

/** What caused the lookup (decides how a result is applied; C5/C7). */
export type PriceLookupMode = 'initial' | 'force' | 'context' | 'qty' | 'date';
const MODE_RANK: Record<PriceLookupMode, number> = { initial: 5, force: 4, context: 3, qty: 2, date: 1 };

export const PRICE_TOAST_KEY = 'price-relookup';
export const RFQ_CREATE_ROUTE = ['/portal/pages/demand/quotations/create'];

/** Extra (non-validated) controls every PO line group carries for A45. */
export function addPricingControls(group: FormGroup, init?: {
  priceSource?: number | null; systemResolvedPrice?: number | null; supplierPricelistUuid?: string | null;
  priceOverrideReason?: string | null; pricingDateBasis?: string | null; conversion?: PriceConversionInfo | null;
}): FormGroup {
  group.addControl('priceSource', new FormControl<number>(init?.priceSource ?? PriceSource.Manual));
  group.addControl('systemResolvedPrice', new FormControl<number | null>(init?.systemResolvedPrice ?? null));
  group.addControl('supplierPricelistUuid', new FormControl<string | null>(init?.supplierPricelistUuid ?? null));
  group.addControl('priceOverrideReason', new FormControl<string>(init?.priceOverrideReason ?? ''));
  group.addControl('pricingDateBasis', new FormControl<string | null>(init?.pricingDateBasis ?? null));
  group.addControl('pricingWarning', new FormControl<string | null>(null));
  // A46 C10 — original price/currency/rate of a converted price (drives the info icon + tooltip).
  group.addControl('conversionInfo', new FormControl<PriceConversionInfo | null>(init?.conversion ?? null));
  return group;
}

export function ymd(d: Date | string | null | undefined): string | undefined {
  if (!d) return undefined;
  const dt = d instanceof Date ? d : new Date(d);
  if (isNaN(dt.getTime())) return undefined;
  const p = (n: number) => String(n).padStart(2, '0');
  return `${dt.getFullYear()}-${p(dt.getMonth() + 1)}-${p(dt.getDate())}`;
}

interface LineState { trigger$: Subject<void>; mode: PriceLookupMode | null; subs: Subscription; }

/**
 * A45 FE-B — PO line auto-pricing shared by po-create and po-edit. Owns the resolve-price calls (debounced),
 * the source/override bookkeeping, the C7 re-lookup rules and the order-date banner. Resolution failures never
 * block saving: they leave the line Manual.
 */
export class PoLinePricing {
  orderDate: Date = new Date();
  orderDateBanner: { date: Date; count: number } | null = null;
  /** True while a form is being hydrated: header patches must not trigger lookups. */
  suspended = false;

  private states = new Map<FormGroup, LineState>();
  private pending = new Map<string, { group: FormGroup; price: number; res: ResolvePriceResult; msg: any }>();
  private seq = 0;
  private headerSubs: Subscription[] = [];

  constructor(
    private form: FormGroup,
    private svc: PurchasePricingService,
    private toast: MessageService,
    private router: Router,
    private opts: {
      debounceMs?: number; focusPrice?: (index: number) => void; fmt?: (n: number) => string;
      /** A46 C10 — the PO currency's code (for the no-rate banner). */
      currencyCode?: () => string | null;
      /** A46 C2 — the PO number sent to RFQ create as sourcePO (null on an unsaved PO). */
      sourcePo?: () => string | null;
    } = {}
  ) {
    const watch = (name: string, mode: PriceLookupMode, gate?: (g: FormGroup) => boolean) => {
      const c = form.get(name);
      if (!c) return;
      const key = (v: any) => (v instanceof Date ? ymd(v) : (v ?? null));
      let last = key(c.value);
      this.headerSubs.push(c.valueChanges.subscribe(v => {
        if (key(v) === last) return;
        last = key(v);
        this.triggerAll(mode, gate);
      }));
    };
    // A46 C1 — the order-date control (when the form has one) drives setOrderDate(); resolve requests use it, not today.
    const od = form.get('orderDate');
    if (od) {
      if (od.value instanceof Date) this.orderDate = od.value;
      this.headerSubs.push(od.valueChanges.subscribe(v => {
        if (!(v instanceof Date) || ymd(v) === ymd(this.orderDate)) return;
        if (this.suspended) { this.orderDate = v; return; }
        this.setOrderDate(v);
      }));
    }
    watch('supplierId', 'context');
    watch('currencyId', 'context');
    // Header delivery date only matters to lines that don't carry their own required date (C7, BR-09).
    watch('deliveryDate', 'date', g => !g.get('requiredDate')?.value);
  }

  get lines(): FormArray { return this.form.get('lines') as FormArray; }

  destroy() {
    this.headerSubs.forEach(s => s.unsubscribe());
    this.states.forEach(s => s.subs.unsubscribe());
    this.states.clear();
  }

  // ── Per-line wiring ──────────────────────────────────────────────────────

  /** Call for every line group (new or hydrated). Adds the A45 controls if the group lacks them. */
  attach(group: FormGroup): FormGroup {
    if (!group.get('priceSource')) addPricingControls(group);
    if (this.states.has(group)) return group;
    const trigger$ = new Subject<void>();
    const st: LineState = { trigger$, mode: null, subs: new Subscription() };
    this.states.set(group, st);

    st.subs.add(trigger$.pipe(
      debounceTime(this.opts.debounceMs ?? 400),
      switchMap(() => {
        const mode = st.mode ?? 'qty';
        st.mode = null;
        const req = this.buildRequest(group);
        if (!req) { this.setWarning(group, null); return of(null); }
        return this.svc.resolvePrice(req).pipe(map(res => ({ mode, res })));
      })
    ).subscribe(r => { if (r) this.handleResult(group, r.mode, r.res); }));

    let lastVariant = group.get('variantUuid')?.value ?? null;
    st.subs.add(group.get('variantUuid')!.valueChanges.subscribe(v => {
      if ((v ?? null) === lastVariant) return;
      lastVariant = v ?? null;
      this.resetMeta(group);
      if (v) this.schedule(group, 'initial'); else st.mode = null;
    }));
    st.subs.add(group.get('quantity')!.valueChanges.subscribe(() => this.requestLookup(group, 'qty')));
    st.subs.add(group.get('requiredDate')!.valueChanges.subscribe(() => this.requestLookup(group, 'date')));
    st.subs.add(group.get('unitPrice')!.valueChanges.subscribe(v => this.onPriceEdited(group, v)));
    return group;
  }

  detach(group: FormGroup) {
    const st = this.states.get(group);
    st?.subs.unsubscribe();
    this.states.delete(group);
  }

  private schedule(group: FormGroup, mode: PriceLookupMode) {
    const st = this.states.get(group);
    if (!st) return;
    if (!st.mode || MODE_RANK[mode] > MODE_RANK[st.mode]) st.mode = mode;
    st.trigger$.next();
  }

  /** Applies the C7 gates before scheduling a lookup. */
  private requestLookup(group: FormGroup, mode: PriceLookupMode) {
    if (!group.get('variantUuid')?.value) return;
    const src = this.sourceOf(group);
    if (src === PriceSource.Rfq) return;                       // BR-17
    if (mode === 'date' && group.get('pricingDateBasis')?.value !== 'DeliveryDate') return; // BR-09
    this.schedule(group, mode);
  }

  triggerAll(mode: PriceLookupMode, gate?: (g: FormGroup) => boolean) {
    if (this.suspended) return;
    for (const g of this.lines.controls as FormGroup[]) {
      if (gate && !gate(g)) continue;
      this.requestLookup(g, mode);
    }
  }

  // ── Request / result ─────────────────────────────────────────────────────

  buildRequest(group: FormGroup): ResolvePriceRequest | null {
    const variantUuid = group.get('variantUuid')?.value;
    const supplierUuid = this.form.get('supplierId')?.value;
    const quantity = Number(group.get('quantity')?.value);
    if (!variantUuid || !supplierUuid || !(quantity > 0)) return null;
    return {
      variantUuid,
      productUuid: group.get('productUuid')?.value || undefined,
      supplierUuid,
      quantity,
      orderDate: ymd(this.orderDate)!,
      expectedDeliveryDate: ymd(group.get('requiredDate')?.value) ?? ymd(this.form.get('deliveryDate')?.value),
      currencyId: this.form.get('currencyId')?.value || undefined,
      poCurrencyId: this.form.get('currencyId')?.value || undefined
    };
  }

  private handleResult(group: FormGroup, mode: PriceLookupMode, res: ResolvePriceResult | null) {
    const src = this.sourceOf(group);
    const price = Number(group.get('unitPrice')?.value ?? 0);

    if (!res) { // failure -> never block; a fresh line just stays Manual
      if (mode === 'initial') this.markManual(group);
      return;
    }

    // A46 C10 — a price exists but no exchange rate converts it: never fill a guess, leave the line Manual.
    if (res.currencyMismatchWarning) {
      const warn = this.mismatchText(res);
      if (src === PriceSource.Pricelist || src === PriceSource.Override) {
        this.markManual(group);
        group.get('unitPrice')!.setValue(0, { emitEvent: false });
        group.get('conversionInfo')?.setValue(null, { emitEvent: false });
      } else if (mode === 'initial') this.markManual(group);
      this.setWarning(group, warn);
      return;
    }

    if (res.found && res.unitPrice != null) {
      const found = res.unitPrice;
      switch (mode) {
        case 'initial':
        case 'force':
          this.applyFound(group, res); return;
        case 'context':
          if (src === PriceSource.Pricelist || src === PriceSource.Override) { this.applyFound(group, res); return; }
          this.offer(group, res, 'Apply'); return;
        case 'qty':
        case 'date':
          if (src === PriceSource.Pricelist) { this.applyFound(group, res); return; }
          if (src === PriceSource.Override || src === PriceSource.Manual) {
            if (samePrice(found, price)) { if (src === PriceSource.Manual) return; this.applyFound(group, res); return; }
            this.offer(group, res, src === PriceSource.Override ? 'Update' : 'Apply');
          }
          return;
      }
    }

    // Not found
    const warn = res.warningMessage || this.defaultWarning(group);
    if (mode === 'initial' || mode === 'force' || mode === 'context') {
      if (src === PriceSource.Pricelist || src === PriceSource.Override || mode === 'initial') this.markManual(group);
      this.setWarning(group, warn);
    } else if (src === PriceSource.Pricelist) { // qty/date moved the line out of every price tier
      this.markManual(group);
      this.setWarning(group, warn);
    }
  }

  private mismatchText(res: ResolvePriceResult): string {
    const to = this.opts.currencyCode?.() || null;
    const from = res.originalCurrencyCode || res.currencyCode || null;
    return currencyMismatchText(from, to, ymd(this.orderDate)!);
  }

  private defaultWarning(group: FormGroup): string {
    const item = group.get('itemDescription')?.value || 'this item';
    const sup = this.form.get('supplierName')?.value || 'the selected supplier';
    return `No configured price found for ${item} from ${sup} effective ${ymd(this.orderDate)}.`;
  }

  private applyFound(group: FormGroup, res: ResolvePriceResult) {
    group.get('unitPrice')!.setValue(res.unitPrice, { emitEvent: false });
    group.patchValue({
      priceSource: PriceSource.Pricelist,
      systemResolvedPrice: res.unitPrice,
      supplierPricelistUuid: res.supplierPricelistUuid ?? null,
      pricingDateBasis: res.dateBasisUsed ?? null,
      priceOverrideReason: '',
      pricingWarning: null,
      conversionInfo: res.currencyConverted
        ? { code: res.originalCurrencyCode ?? null, price: res.originalPrice ?? null, rate: res.exchangeRateUsed ?? null, date: res.exchangeRateDate ?? null }
        : null
    }, { emitEvent: false });
  }

  private markManual(group: FormGroup) {
    group.patchValue({
      priceSource: PriceSource.Manual, systemResolvedPrice: null, supplierPricelistUuid: null,
      pricingDateBasis: null, priceOverrideReason: '', conversionInfo: null
    }, { emitEvent: false });
  }

  private resetMeta(group: FormGroup) {
    this.markManual(group);
    group.get('pricingWarning')?.setValue(null, { emitEvent: false });
  }

  private setWarning(group: FormGroup, text: string | null) {
    group.get('pricingWarning')?.setValue(text, { emitEvent: false });
  }

  // ── Price edits / override ───────────────────────────────────────────────

  private onPriceEdited(group: FormGroup, value: number | null) {
    const src = this.sourceOf(group);
    const sys = group.get('systemResolvedPrice')?.value as number | null;
    if (src === PriceSource.Pricelist && sys != null && value !== sys) {
      group.get('priceSource')!.setValue(PriceSource.Override, { emitEvent: false }); // BR-05; systemResolvedPrice kept (BR-06)
    } else if (src === PriceSource.Override && sys != null && value === sys) {
      group.patchValue({ priceSource: PriceSource.Pricelist, priceOverrideReason: '' }, { emitEvent: false });
    }
    if (sys == null || !samePrice(value, sys)) group.get('conversionInfo')?.setValue(null, { emitEvent: false }); // A46: no longer the converted price
    if (value != null && Number(value) > 0 && group.get('pricingWarning')?.value) this.setWarning(group, null); // A45-0190
  }

  // ── Warning banner actions ───────────────────────────────────────────────

  enterManually(group: FormGroup) {
    this.setWarning(group, null);
    group.get('priceSource')!.setValue(PriceSource.Manual, { emitEvent: false });
    this.opts.focusPrice?.(this.lines.controls.indexOf(group));
  }

  createRfq(group: FormGroup) {
    this.router.navigate(RFQ_CREATE_ROUTE, {
      queryParams: {
        supplierId: this.form.get('supplierId')?.value || null,
        productId: group.get('productUuid')?.value || null,
        variantUuid: group.get('variantUuid')?.value || null,
        qty: group.get('quantity')?.value || null,
        productName: group.get('itemDescription')?.value || null,
        sourcePO: this.opts.sourcePo?.() || null   // A46 C2
      }
    });
  }

  // ── C7 toast (Override/Manual) ───────────────────────────────────────────

  private offer(group: FormGroup, res: ResolvePriceResult, applyLabel: 'Update' | 'Apply') {
    const price = Number(group.get('unitPrice')?.value ?? 0);
    const qty = group.get('quantity')?.value;
    const f = (n: number) => this.opts.fmt ? this.opts.fmt(n) : String(n);
    if (applyLabel === 'Apply' && price === 0) { this.applyFound(group, res); return; } // nothing typed yet -> just fill
    // one open offer per line: a newer offer for the same line replaces the older one
    for (const [t, p] of [...this.pending]) if (p.group === group) this.pending.delete(t);
    const token = `p${++this.seq}`;
    const msg = {
      key: PRICE_TOAST_KEY, severity: 'info', sticky: true,
      summary: 'Pricelist price available',
      detail: applyLabel === 'Update'
        ? `System price for qty ${qty} is ${f(res.unitPrice!)}. Keep your price of ${f(price)}?`
        : `A configured price of ${f(res.unitPrice!)} is available for qty ${qty}.`,
      data: { token, applyLabel }
    };
    this.pending.set(token, { group, price, res, msg });
    this.renderOffers();
  }

  /** The toast service can only clear a whole key, so the stack is redrawn from the pending offers (act per toast, R-5). */
  private renderOffers() {
    this.toast.clear(PRICE_TOAST_KEY);
    const msgs = [...this.pending.values()].map(p => p.msg);
    if (msgs.length) this.toast.addAll(msgs);
  }

  /** [Update] / [Apply] — price taken, source becomes Pricelist (A45-0202). Only this toast goes; other offers stay. */
  acceptOffer(message: any) {
    const p = this.pending.get(message?.data?.token);
    this.pending.delete(message?.data?.token);
    if (p) this.applyFound(p.group, p.res);
    this.renderOffers();
  }

  /** [Keep] — price and source untouched (A45-0203). Only this toast goes. */
  keepOffer(message: any) {
    this.pending.delete(message?.data?.token);
    this.renderOffers();
  }

  // ── Order date (C7 header) ───────────────────────────────────────────────

  /** D-11: the PO has no order-date column; callers with an order-date control forward its changes here. */
  setOrderDate(d: Date) {
    this.orderDate = d;
    const count = (this.lines.controls as FormGroup[]).filter(g => this.isOrderDateLine(g)).length;
    this.orderDateBanner = count > 0 ? { date: d, count } : null;
  }

  private isOrderDateLine(g: FormGroup): boolean {
    return g.get('pricingDateBasis')?.value === 'OrderDate' && this.sourceOf(g) !== PriceSource.Rfq;
  }

  reLookupAll() {
    this.orderDateBanner = null;
    for (const g of this.lines.controls as FormGroup[]) if (this.isOrderDateLine(g)) this.schedule(g, 'force');
  }

  keepCurrentPrices() { this.orderDateBanner = null; }

  // ── Read helpers (templates / payloads) ──────────────────────────────────

  sourceOf(group: AbstractGroup): number {
    const v = group.get('priceSource')?.value;
    return v == null ? PriceSource.Manual : Number(v);
  }
  label(group: AbstractGroup): string { return PRICE_SOURCE_LABELS[this.sourceOf(group)] ?? 'Manual'; }
  warning(group: AbstractGroup): string | null { return group.get('pricingWarning')?.value ?? null; }

  /** Fields to merge into a create/update line payload. */
  static payload(l: any) {
    const src = l.priceSource == null ? PriceSource.Manual : Number(l.priceSource);
    return {
      priceSource: src,
      systemResolvedPrice: l.systemResolvedPrice ?? undefined,
      supplierPricelistUuid: l.supplierPricelistUuid || undefined,
      priceOverrideReason: src === PriceSource.Override ? (l.priceOverrideReason || undefined) : undefined
    };
  }
}

type AbstractGroup = { get(name: string): { value: any } | null };
