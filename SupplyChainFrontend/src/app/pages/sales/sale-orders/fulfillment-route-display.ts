import { FulfillmentRouteSource, routeStepsText } from '../../../services/fulfillment-routes.service';
import {
  ConfirmBlockerCode, ConfirmBlockerModel, RouteCategory, SaleOrderDeliveryPreviewModel
} from '../../../services/sale-order.service';
import { formatCode } from '../../../shared/format-code';

// A33 PC-07/08/09 — the words and marks the sale order pages use for a line's fulfillment route, the delivery
// preview and the disabled Confirm button (spec §5.5, API-CONTRACT.md §5). Pure functions, shared by the form and
// the detail page.

/** The legend of spec §5.5, plus ⊙ for the organization default. */
export const ROUTE_SOURCE_ICONS: Record<FulfillmentRouteSource, string> = {
  VARIANT: 'ⓥ',
  LINE_OVERRIDE: '✎',
  ORG_DEFAULT: '⊙',
  NONE: '⚠'
};

export const ROUTE_LEGEND: { icon: string; text: string }[] = [
  { icon: ROUTE_SOURCE_ICONS.VARIANT, text: 'inherited from the variant' },
  { icon: ROUTE_SOURCE_ICONS.LINE_OVERRIDE, text: 'overridden on the line' },
  { icon: ROUTE_SOURCE_ICONS.ORG_DEFAULT, text: 'organization default' },
  { icon: ROUTE_SOURCE_ICONS.NONE, text: 'no usable route (blocks confirmation)' }
];

/** The blockers that are about a line's own route; the others (address, pickup) are about the order. */
const ROUTE_BLOCKERS: readonly ConfirmBlockerCode[] = ['ROUTE_MISSING', 'ROUTE_INACTIVE', 'ROUTE_UNKNOWN'];

export function isRouteBlocker(code?: ConfirmBlockerCode | null): boolean {
  return !!code && ROUTE_BLOCKERS.includes(code);
}

/** A34 D-5 — the line's route is make-to-order, but the line can't be made to order. */
const MANUFACTURE_BLOCKERS: readonly ConfirmBlockerCode[] =
  ['MANUFACTURING_DISABLED', 'NOT_MANUFACTURED', 'BOM_MISSING', 'PRODUCTION_WAREHOUSE_MISSING'];

export function isManufactureBlocker(code?: ConfirmBlockerCode | null): boolean {
  return !!code && MANUFACTURE_BLOCKERS.includes(code);
}

/** A blocker about one line (its route, or making it to order), rather than about the order as a whole. */
export function isLineBlocker(code?: ConfirmBlockerCode | null): boolean {
  return isRouteBlocker(code) || isManufactureBlocker(code);
}

const BLOCKER_HINTS: Record<ConfirmBlockerCode, string> = {
  ROUTE_MISSING: 'No route defined — the order cannot be confirmed',
  ROUTE_INACTIVE: 'This route is inactive — choose another; the order cannot be confirmed',
  ROUTE_UNKNOWN: 'This route no longer exists — choose another; the order cannot be confirmed',
  SHIPPING_ADDRESS_REQUIRED: 'This route ships — the order needs a shipping address before it can be confirmed',
  SELF_PICKUP_DISABLED: 'Customer pickup is switched off — choose a route that ships',
  MANUFACTURING_DISABLED: 'Make-to-order route, but manufacturing is not enabled for your organization — choose a stock route; the order cannot be confirmed',
  NOT_MANUFACTURED: 'This is not a manufactured product, so it can\'t be made to order — choose a stock route; the order cannot be confirmed',
  BOM_MISSING: 'No active bill of materials, so it can\'t be made to order — activate a BOM or choose a stock route; the order cannot be confirmed',
  PRODUCTION_WAREHOUSE_MISSING: 'No default production warehouse on the product, so it can\'t be made to order — set one or choose a stock route; the order cannot be confirmed'
};

/**
 * A34 — the detail banner's words for a line that blocks confirmation, after "Line N · Item: ". The server's own
 * message is used for anything not listed.
 */
export const LINE_BLOCKER_REASONS: Partial<Record<ConfirmBlockerCode, string>> = {
  ROUTE_MISSING: 'no fulfillment route — assign one on the line, or set a default route on the product variant.',
  ROUTE_INACTIVE: 'its route is inactive — choose another.',
  ROUTE_UNKNOWN: 'its route no longer exists — choose another.',
  MANUFACTURING_DISABLED: 'its route is make-to-order, but manufacturing is not enabled for your organization — choose a stock route for this line.',
  NOT_MANUFACTURED: 'not a manufactured product, so it can\'t use a make-to-order route — choose a stock route, or set the product\'s supply method to MANUFACTURE.',
  BOM_MISSING: 'no active bill of materials, so it can\'t be made to order — activate a BOM, or choose a stock route for this line.',
  PRODUCTION_WAREHOUSE_MISSING: 'no default production warehouse, so it can\'t be made to order — set one on the product, or choose a stock route for this line.'
};

// ── A34 R-15: sourcing and route category ──────────────────────────────────────

/** fulfillmentMode MAKE_TO_ORDER, under "Sourcing". */
export const MAKE_TO_ORDER_LABEL = 'Make to order';

/** A line's sourcing (its fulfillment mode) in words. */
export function sourcingLabel(mode?: string | null): string {
  if (!mode) return '—';
  return mode === 'MAKE_TO_ORDER' ? MAKE_TO_ORDER_LABEL : formatCode(mode);
}

/** The route category as a tag ("Manufacture" / "Stock"); never shown as sourcing. */
export function routeCategoryTag(category?: RouteCategory | string | null): { label: string; severity: 'warn' | 'secondary' } | null {
  if (category === 'MANUFACTURE') return { label: 'Manufacture', severity: 'warn' };
  if (category === 'STOCK') return { label: 'Stock', severity: 'secondary' };
  return null;
}

/** D-1 — a line made to order: its sourcing says so (after confirm), or its route is a MANUFACTURE one (a draft). */
export function isMakeToOrderLine(line: { fulfillmentMode?: string | null; effectiveRouteCategory?: string | null }): boolean {
  return line.fulfillmentMode === 'MAKE_TO_ORDER' || line.effectiveRouteCategory === 'MANUFACTURE';
}

const SOURCE_HINTS: Record<FulfillmentRouteSource, string> = {
  VARIANT: 'Source: variant default',
  LINE_OVERRIDE: 'Source: overridden on this line',
  ORG_DEFAULT: 'Source: organization default',
  NONE: 'No route'
};

/** What a line's Route cell shows. */
export interface LineRouteDisplay {
  /** route: has a usable route · blocked: stops confirmation · exempt: drop-shipped, needs none (D-5) · none: no route, not blocking. */
  kind: 'route' | 'blocked' | 'exempt' | 'none';
  source: FulfillmentRouteSource | null;
  /** ⓥ ✎ ⊙ ⚠, or '' when there is nothing to mark. */
  icon: string;
  name: string | null;
  hint: string;
}

/** The route fields a sale order line (or a preview line) carries. */
export interface LineRouteFields {
  fulfillmentMode?: string | null;
  routeSource?: FulfillmentRouteSource | null;
  routeBlocker?: ConfirmBlockerCode | null;
  effectiveRouteName?: string | null;
  effectiveRouteCode?: string | null;
  /** A36 §4 — a service line: route-exempt like a drop-shipped one. */
  isService?: boolean | null;
}

export function lineRouteDisplay(line: LineRouteFields): LineRouteDisplay {
  const source = line.routeSource ?? null;
  const name = line.effectiveRouteName || line.effectiveRouteCode || null;

  if (line.isService && !line.routeBlocker) {
    return { kind: 'exempt', source, icon: '', name: null, hint: 'Service — no delivery; a service order carries it' };
  }
  if (line.fulfillmentMode === 'DROP_SHIP' && !line.routeBlocker) {
    return { kind: 'exempt', source, icon: '', name: null, hint: 'Drop-shipped by the supplier — no route needed' };
  }
  if (line.routeBlocker) {
    return { kind: 'blocked', source, icon: ROUTE_SOURCE_ICONS.NONE, name, hint: BLOCKER_HINTS[line.routeBlocker] ?? 'The order cannot be confirmed' };
  }
  if (!source || source === 'NONE' || !name) {
    return { kind: 'none', source, icon: '', name: null, hint: SOURCE_HINTS.NONE };
  }
  return { kind: 'route', source, icon: ROUTE_SOURCE_ICONS[source], name, hint: SOURCE_HINTS[source] };
}

/** The dropdown's placeholder when the line inherits: what it inherits, or a call to choose. */
export function inheritPlaceholder(line: LineRouteFields | null, hasOverride: boolean): string {
  if (!line || hasOverride || line.routeSource === 'LINE_OVERRIDE') return 'Inherit (variant or organization default)';
  if (line.routeBlocker === 'ROUTE_MISSING') return '— Select route —';
  const name = line.effectiveRouteName || line.effectiveRouteCode;
  if (!name) return 'Inherit (variant or organization default)';
  return `Inherit — ${name} (${line.routeSource === 'VARIANT' ? 'variant' : 'organization default'})`;
}

function plural(n: number, one: string, many: string): string {
  return n === 1 ? one : many;
}

/**
 * The tooltip of a disabled Confirm button: how many lines lack a usable route and which, then the order-level
 * reasons in the server's words. Empty when nothing blocks.
 */
export function confirmBlockedSummary(blockers: readonly ConfirmBlockerModel[]): string {
  if (!blockers.length) return '';
  const linesWith = (is: (code: ConfirmBlockerCode) => boolean) =>
    [...new Set(blockers.filter(b => is(b.code) && b.lineNumber != null).map(b => b.lineNumber as number))].sort((a, b) => a - b);
  const numbers = linesWith(isRouteBlocker);
  // A34 D-5 — make-to-order lines that can't be made: counted apart, never called unrouted.
  const unmakeable = linesWith(isManufactureBlocker);
  const parts: string[] = ['Confirm is disabled:'];
  if (numbers.length) {
    const n = numbers.length;
    parts.push(`${n} ${plural(n, 'line has', 'lines have')} no usable fulfillment route ` +
               `(${plural(n, 'line', 'lines')} ${numbers.join(', ')}). Assign a route on the line or a default route on the variant.`);
  }
  if (unmakeable.length) {
    const n = unmakeable.length;
    parts.push(`${n} ${plural(n, 'line', 'lines')} can't be made to order (${plural(n, 'line', 'lines')} ${unmakeable.join(', ')}). ` +
               `The line says why; choose a stock route or fix the product.`);
  }
  const others = [...new Set(blockers.filter(b => !isLineBlocker(b.code) || b.lineNumber == null).map(b => b.message))];
  parts.push(...others);
  return parts.join(' ');
}

/** A34 PD-08 — the Confirm button's tooltip when nothing blocks it: what confirming will create. Empty without a preview. */
export function confirmReadyTooltip(preview: SaleOrderDeliveryPreviewModel | null | undefined): string {
  if (!preview) return '';
  const n = preview.deliveryCount ?? (preview.groups ?? []).length;
  const m = (preview.productionLines ?? []).length;
  const deliveries = `${n} delivery order${n === 1 ? '' : 's'}`;
  return m > 0 ? `Will create ${deliveries} and ${m} production order${m === 1 ? '' : 's'}` : `Will create ${deliveries}`;
}

/** The confirm 400 joins its blockers with "\n": one readable line each. */
export function splitServerMessage(message?: string | null): string[] {
  return (message ?? '').split(/\r?\n/).map(s => s.trim()).filter(Boolean);
}

/** A line as the preview names it: the page's own number for it and what it is. */
export interface PreviewLineRef {
  number: number;
  description: string;
}

export interface DeliveryPreviewGroupView {
  routeName: string;
  routeCode: string;
  stepsText: string;
  /** "Ships to the customer" / "Customer collects" — from the route (D-4). */
  modeLabel: string;
  warehouseName: string | null;
  lines: (PreviewLineRef & { quantity: number | null })[];
}

export interface DeliveryPreviewView {
  deliveryCount: number;
  canConfirm: boolean;
  groups: DeliveryPreviewGroupView[];
  /** Lines with no usable route: they go on no delivery until one is assigned. */
  unroutable: (PreviewLineRef & { message: string })[];
  /** Lines that need no delivery (drop-shipped, D-5). */
  exempt: PreviewLineRef[];
  /** Reasons about the order as a whole (shipping address, pickup), in the server's words. */
  orderBlockers: string[];
  /** A34 §6.3 — make-to-order lines: a production order each at confirm; their delivery follows production. */
  production: (PreviewLineRef & { quantity: number; routeName: string })[];
}

const UNROUTABLE_MESSAGES: Partial<Record<ConfirmBlockerCode, string>> = {
  ROUTE_MISSING: 'unroutable — assign a route first',
  ROUTE_INACTIVE: 'its route is inactive — assign another route',
  ROUTE_UNKNOWN: 'its route no longer exists — assign another route',
  MANUFACTURING_DISABLED: 'can\'t be made to order — manufacturing is not enabled; choose a stock route',
  NOT_MANUFACTURED: 'can\'t be made to order — not a manufactured product; choose a stock route',
  BOM_MISSING: 'can\'t be made to order — no active bill of materials',
  PRODUCTION_WAREHOUSE_MISSING: 'can\'t be made to order — no default production warehouse on the product'
};

/** The preview panel's headline (A33 words for a stock-only order; A34 adds the production count). */
export function previewHeadline(v: DeliveryPreviewView): string {
  const n = v.deliveryCount;
  const m = v.production.length;
  if (n === 0 && m === 0) return 'No delivery order can be created yet.';
  const what = [
    ...(n > 0 ? [`${n} delivery order${n === 1 ? '' : 's'}`] : []),
    ...(m > 0 ? [`${m} production order${m === 1 ? '' : 's'}`] : [])
  ].join(' and ');
  return `On confirmation, ${what} will be created${v.canConfirm ? '' : ' once the problems below are fixed'}:`;
}

/**
 * The preview panel's content. `ref` renames the server's line numbers to the page's own (the form leaves
 * incomplete lines out of the request, so the server's line 2 can be the form's line 3).
 */
export function buildDeliveryPreviewView(
  preview: SaleOrderDeliveryPreviewModel,
  ref?: (lineNumber: number) => PreviewLineRef | null
): DeliveryPreviewView {
  const lines = preview.lines ?? [];
  const byNumber = new Map(lines.map(l => [l.lineNumber, l]));
  const name = (n: number): PreviewLineRef => ref?.(n) ?? { number: n, description: byNumber.get(n)?.itemDescription || `Line ${n}` };

  const groups = (preview.groups ?? []).map(g => ({
    routeName: g.routeName || g.routeCode,
    routeCode: g.routeCode,
    stepsText: g.stepsText || routeStepsText(g.steps ?? []),
    modeLabel: g.deliveryMode === 'SHIP' ? 'Ships to the customer' : 'Customer collects',
    warehouseName: g.warehouseName ?? null,
    lines: (g.lineNumbers ?? []).map(n => ({ ...name(n), quantity: byNumber.get(n)?.quantity ?? null }))
  }));

  const productionLines = preview.productionLines ?? [];
  const production = productionLines.map(p => ({
    ...(ref?.(p.lineNumber) ?? { number: p.lineNumber, description: p.itemDescription || `Line ${p.lineNumber}` }),
    quantity: p.quantity,
    routeName: p.routeName || p.routeCode
  }));

  const grouped = new Set([...(preview.groups ?? []).flatMap(g => g.lineNumbers ?? []), ...productionLines.map(p => p.lineNumber)]);
  const unroutable = lines
    .filter(l => isLineBlocker(l.routeBlocker))
    .map(l => ({ ...name(l.lineNumber), message: UNROUTABLE_MESSAGES[l.routeBlocker!] ?? 'assign a route first' }));
  const exempt = lines
    .filter(l => !grouped.has(l.lineNumber) && !l.routeBlocker && l.routeSource === 'NONE')
    .map(l => name(l.lineNumber));

  return {
    deliveryCount: preview.deliveryCount ?? groups.length,
    canConfirm: !!preview.canConfirm,
    groups,
    unroutable,
    exempt,
    // A line's own problem is listed with the line; only the order's own reasons are here.
    orderBlockers: [...new Set((preview.blockers ?? []).filter(b => !isLineBlocker(b.code)).map(b => b.message))],
    production
  };
}
