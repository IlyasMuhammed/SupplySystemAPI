import { FulfillmentRouteSource, routeStepsText } from '../../../services/fulfillment-routes.service';
import { ConfirmBlockerCode, ConfirmBlockerModel, SaleOrderDeliveryPreviewModel } from '../../../services/sale-order.service';

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

const BLOCKER_HINTS: Record<ConfirmBlockerCode, string> = {
  ROUTE_MISSING: 'No route defined — the order cannot be confirmed',
  ROUTE_INACTIVE: 'This route is inactive — choose another; the order cannot be confirmed',
  ROUTE_UNKNOWN: 'This route no longer exists — choose another; the order cannot be confirmed',
  SHIPPING_ADDRESS_REQUIRED: 'This route ships — the order needs a shipping address before it can be confirmed',
  SELF_PICKUP_DISABLED: 'Customer pickup is switched off — choose a route that ships'
};

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
}

export function lineRouteDisplay(line: LineRouteFields): LineRouteDisplay {
  const source = line.routeSource ?? null;
  const name = line.effectiveRouteName || line.effectiveRouteCode || null;

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
  const numbers = [...new Set(blockers.filter(b => isRouteBlocker(b.code) && b.lineNumber != null).map(b => b.lineNumber as number))]
    .sort((a, b) => a - b);
  const parts: string[] = [];
  if (numbers.length) {
    const n = numbers.length;
    parts.push(`Confirm is disabled: ${n} ${plural(n, 'line has', 'lines have')} no usable fulfillment route ` +
               `(${plural(n, 'line', 'lines')} ${numbers.join(', ')}). Assign a route on the line or a default route on the variant.`);
  }
  const others = [...new Set(blockers.filter(b => !isRouteBlocker(b.code) || b.lineNumber == null).map(b => b.message))];
  if (others.length) {
    if (!numbers.length) parts.push('Confirm is disabled:');
    parts.push(...others);
  }
  return parts.join(' ');
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
}

const UNROUTABLE_MESSAGES: Partial<Record<ConfirmBlockerCode, string>> = {
  ROUTE_MISSING: 'unroutable — assign a route first',
  ROUTE_INACTIVE: 'its route is inactive — assign another route',
  ROUTE_UNKNOWN: 'its route no longer exists — assign another route'
};

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

  const grouped = new Set((preview.groups ?? []).flatMap(g => g.lineNumbers ?? []));
  const unroutable = lines
    .filter(l => isRouteBlocker(l.routeBlocker))
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
    orderBlockers: [...new Set((preview.blockers ?? []).filter(b => !isRouteBlocker(b.code)).map(b => b.message))]
  };
}
