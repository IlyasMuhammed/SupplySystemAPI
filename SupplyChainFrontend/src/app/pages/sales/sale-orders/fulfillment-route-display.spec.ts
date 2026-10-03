import {
  ROUTE_SOURCE_ICONS, lineRouteDisplay, confirmBlockedSummary, buildDeliveryPreviewView, splitServerMessage
} from './fulfillment-route-display';
import { SaleOrderDeliveryPreviewModel, DeliveryPreviewLineModel } from '../../../services/sale-order.service';

// A33 PC-07/08/09 — what the Route column, the delivery preview and the disabled Confirm button say
// (API-CONTRACT.md §5, spec §5.5).

function pLine(overrides: Partial<DeliveryPreviewLineModel> = {}): DeliveryPreviewLineModel {
  return {
    lineNumber: 1, variantUuid: 'v1', itemDescription: 'Steel Pipes', quantity: 500,
    effectiveRouteUuid: 'r-po', effectiveRouteCode: 'PICK_ONLY', effectiveRouteName: 'Pick Only',
    effectiveRouteSteps: ['PICK', 'GOODS_ISSUE'], routeSource: 'VARIANT', routeBlocker: null,
    ...overrides
  };
}

function preview(overrides: Partial<SaleOrderDeliveryPreviewModel> = {}): SaleOrderDeliveryPreviewModel {
  return {
    routesEnabled: true, canConfirm: false, deliveryCount: 2,
    lines: [
      pLine(),
      pLine({ lineNumber: 2, variantUuid: 'v2', itemDescription: 'Copper Wire', quantity: 600, effectiveRouteUuid: 'r-pps',
              effectiveRouteCode: 'PICK_PACK_SHIP', effectiveRouteName: 'Pick, Pack & Ship', routeSource: 'LINE_OVERRIDE' }),
      pLine({ lineNumber: 3, variantUuid: 'v3', itemDescription: 'Synth Hyd Oil', quantity: 100 }),
      pLine({ lineNumber: 4, variantUuid: 'v4', itemDescription: 'Custom Gasket', quantity: 50, effectiveRouteUuid: null,
              effectiveRouteCode: null, effectiveRouteName: null, effectiveRouteSteps: [], routeSource: 'NONE', routeBlocker: 'ROUTE_MISSING' })
    ],
    groups: [
      { routeUuid: 'r-po', routeCode: 'PICK_ONLY', routeName: 'Pick Only', steps: ['PICK', 'GOODS_ISSUE'],
        stepsText: 'Pick → Goods Issue', requiresShipping: false, deliveryMode: 'SELF_PICKUP', lineNumbers: [1, 3] },
      { routeUuid: 'r-pps', routeCode: 'PICK_PACK_SHIP', routeName: 'Pick, Pack & Ship', steps: ['PICK', 'PACK', 'GOODS_ISSUE', 'SHIP'],
        stepsText: 'Pick → Pack → Goods Issue → Ship', requiresShipping: true, deliveryMode: 'SHIP',
        warehouseUuid: 'wh-1', warehouseName: 'Main warehouse', lineNumbers: [2] }
    ],
    blockers: [
      { lineUuid: 'l4', lineNumber: 4, code: 'ROUTE_MISSING', message: 'Cannot confirm: lines 4 have no fulfillment route.' },
      { code: 'SHIPPING_ADDRESS_REQUIRED', message: 'Line 2 ships, so the order needs a shipping address.' }
    ],
    ...overrides
  };
}

describe('A33 fulfillment route display helpers', () => {

  describe('lineRouteDisplay — the Route column of one line', () => {
    it('marks each source with the legend icon and says where the route came from', () => {
      const variant = lineRouteDisplay({ routeSource: 'VARIANT', effectiveRouteName: 'Pick Only' });
      expect(variant).toEqual(jasmine.objectContaining({ kind: 'route', icon: 'ⓥ', name: 'Pick Only', source: 'VARIANT' }));
      expect(variant.hint).toContain('variant');

      const override = lineRouteDisplay({ routeSource: 'LINE_OVERRIDE', effectiveRouteName: 'Pick, Pack & Ship' });
      expect(override.icon).toBe('✎');
      expect(override.hint).toContain('overridden');

      const orgDefault = lineRouteDisplay({ routeSource: 'ORG_DEFAULT', effectiveRouteName: 'Pick & Ship' });
      expect(orgDefault.icon).toBe('⊙');
      expect(orgDefault.hint).toContain('organization default');

      expect(ROUTE_SOURCE_ICONS).toEqual({ VARIANT: 'ⓥ', LINE_OVERRIDE: '✎', ORG_DEFAULT: '⊙', NONE: '⚠' });
    });

    it('flags a line with no route as blocking', () => {
      const d = lineRouteDisplay({ routeSource: 'NONE', routeBlocker: 'ROUTE_MISSING' });
      expect(d.kind).toBe('blocked');
      expect(d.icon).toBe('⚠');
      expect(d.name).toBeNull();
      expect(d.hint).toContain('cannot be confirmed');
    });

    it('keeps the name of an override that has been deactivated, and says to choose another', () => {
      const d = lineRouteDisplay({ routeSource: 'LINE_OVERRIDE', routeBlocker: 'ROUTE_INACTIVE', effectiveRouteName: 'Old route' });
      expect(d.kind).toBe('blocked');
      expect(d.name).toBe('Old route');
      expect(d.hint).toContain('inactive');
    });

    it('shows no route for a drop-shipped line, which needs none (D-5)', () => {
      const d = lineRouteDisplay({ fulfillmentMode: 'DROP_SHIP', routeSource: 'NONE', routeBlocker: null });
      expect(d.kind).toBe('exempt');
      expect(d.icon).toBe('');
      expect(d.name).toBeNull();
      expect(d.hint).toContain('Drop');
    });

    it('falls back to the route code when the name is missing', () => {
      expect(lineRouteDisplay({ routeSource: 'VARIANT', effectiveRouteCode: 'PICK_ONLY' }).name).toBe('PICK_ONLY');
    });
  });

  describe('confirmBlockedSummary — the disabled Confirm button tooltip', () => {
    it('is empty when nothing blocks', () => {
      expect(confirmBlockedSummary([])).toBe('');
    });

    it('gives the count and the lines missing a route', () => {
      const text = confirmBlockedSummary([
        { lineNumber: 4, code: 'ROUTE_MISSING', message: 'x' },
        { lineNumber: 2, code: 'ROUTE_MISSING', message: 'x' }
      ]);
      expect(text).toContain('2 lines');
      expect(text).toContain('lines 2, 4');
    });

    it('says "1 line" for one, and adds the order-level reasons in the server words', () => {
      const text = confirmBlockedSummary([
        { lineNumber: 3, code: 'ROUTE_INACTIVE', message: "Line 3: fulfillment route 'X' is inactive." },
        { code: 'SHIPPING_ADDRESS_REQUIRED', message: 'The order needs a shipping address.' }
      ]);
      expect(text).toContain('1 line');
      expect(text).toContain('line 3');
      expect(text).toContain('The order needs a shipping address.');
    });
  });

  describe('splitServerMessage — the confirm 400 joins its blockers with \\n', () => {
    it('splits into trimmed, non-empty lines', () => {
      expect(splitServerMessage("Line 3: fulfillment route 'X' is inactive.\nCannot confirm: lines 2, 4 have no fulfillment route.\n"))
        .toEqual(["Line 3: fulfillment route 'X' is inactive.", 'Cannot confirm: lines 2, 4 have no fulfillment route.']);
      expect(splitServerMessage(undefined)).toEqual([]);
    });
  });

  describe('buildDeliveryPreviewView — the preview panel', () => {
    it('lists one delivery per group with its route, steps, mode, warehouse and lines', () => {
      const view = buildDeliveryPreviewView(preview());

      expect(view.deliveryCount).toBe(2);
      expect(view.groups.map(g => g.routeName)).toEqual(['Pick Only', 'Pick, Pack & Ship']);
      expect(view.groups[0].stepsText).toBe('Pick → Goods Issue');
      expect(view.groups[0].lines.map(l => `${l.number}:${l.description}`)).toEqual(['1:Steel Pipes', '3:Synth Hyd Oil']);
      expect(view.groups[0].warehouseName).toBeNull();
      expect(view.groups[1].warehouseName).toBe('Main warehouse');
      expect(view.groups[1].modeLabel).toContain('Ship');
      expect(view.groups[0].modeLabel).toContain('collect');
    });

    it('lists the unroutable lines apart, and the order-level blockers in the server words', () => {
      const view = buildDeliveryPreviewView(preview());

      expect(view.unroutable.map(u => u.number)).toEqual([4]);
      expect(view.unroutable[0].description).toBe('Custom Gasket');
      expect(view.unroutable[0].message).toContain('assign');
      expect(view.orderBlockers).toEqual(['Line 2 ships, so the order needs a shipping address.']);
    });

    it('renumbers lines the way the page numbers them', () => {
      // The form leaves out incomplete lines, so the server's line 2 can be the form's line 3.
      const view = buildDeliveryPreviewView(preview(), n => ({ number: n === 2 ? 3 : n, description: `Form line ${n}` }));
      expect(view.groups[1].lines.map(l => l.number)).toEqual([3]);
      expect(view.groups[1].lines[0].description).toBe('Form line 2');
    });

    it('counts the deliveries from the groups when the count is missing', () => {
      const view = buildDeliveryPreviewView(preview({ deliveryCount: undefined as unknown as number }));
      expect(view.deliveryCount).toBe(2);
    });
  });
});
