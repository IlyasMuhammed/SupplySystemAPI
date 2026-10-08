import {
  MAKE_TO_ORDER_LABEL, buildDeliveryPreviewView, confirmBlockedSummary, confirmReadyTooltip, isMakeToOrderLine,
  lineRouteDisplay, previewHeadline, routeCategoryTag, sourcingLabel
} from './fulfillment-route-display';
import { ConfirmBlockerCode, DeliveryPreviewLineModel, SaleOrderDeliveryPreviewModel } from '../../../services/sale-order.service';

// A34 PD-08 / D-5 / R-15 — what the sale order pages say about make-to-order lines: the four new confirm blockers,
// the preview's production group, the Confirm tooltip, the route-category tag and the "Make to order" sourcing label.
// Contract: docs/route-classification/API-CONTRACT.md §1, §6.2, §6.3.

function pLine(overrides: Partial<DeliveryPreviewLineModel> = {}): DeliveryPreviewLineModel {
  return {
    lineNumber: 1, variantUuid: 'v1', itemDescription: 'Steel Pipes', quantity: 500,
    effectiveRouteUuid: 'r-ps', effectiveRouteCode: 'PICK_AND_SHIP', effectiveRouteName: 'Pick & Ship',
    effectiveRouteSteps: ['PICK', 'GOODS_ISSUE', 'SHIP'], routeSource: 'ORG_DEFAULT', routeBlocker: null,
    effectiveRouteCategory: 'STOCK',
    ...overrides
  };
}

const GEAR = pLine({
  lineNumber: 2, variantUuid: 'v2', itemDescription: 'Custom Gear Assy', quantity: 50, effectiveRouteUuid: 'r-mfg',
  effectiveRouteCode: 'MFG_PICK_PACK_SHIP', effectiveRouteName: 'Manufacture → Pick, Pack & Ship', routeSource: 'VARIANT',
  effectiveRouteCategory: 'MANUFACTURE'
});

function preview(overrides: Partial<SaleOrderDeliveryPreviewModel> = {}): SaleOrderDeliveryPreviewModel {
  return {
    routesEnabled: true, canConfirm: true, deliveryCount: 1,
    lines: [pLine(), GEAR],
    groups: [{ routeUuid: 'r-ps', routeCode: 'PICK_AND_SHIP', routeName: 'Pick & Ship', steps: ['PICK', 'GOODS_ISSUE', 'SHIP'],
               stepsText: 'Pick → Goods Issue → Ship', requiresShipping: true, deliveryMode: 'SHIP', lineNumbers: [1] }],
    blockers: [],
    productionLines: [{
      lineUuid: 'l2', lineNumber: 2, variantUuid: 'v2', itemDescription: 'Custom Gear Assy', quantity: 50, routeUuid: 'r-mfg',
      routeCode: 'MFG_PICK_PACK_SHIP', routeName: 'Manufacture → Pick, Pack & Ship', steps: ['PICK', 'PACK', 'GOODS_ISSUE', 'SHIP'],
      message: 'A production order will be created; its delivery follows when production completes.'
    }],
    ...overrides
  };
}

describe('A34 fulfillment route display helpers — make to order', () => {

  describe('lineRouteDisplay — the four D-5 blockers', () => {
    const cases: [ConfirmBlockerCode, string][] = [
      ['MANUFACTURING_DISABLED', 'manufacturing is not enabled'],
      ['NOT_MANUFACTURED', 'not a manufactured product'],
      ['BOM_MISSING', 'bill of materials'],
      ['PRODUCTION_WAREHOUSE_MISSING', 'production warehouse']
    ];
    for (const [code, words] of cases) {
      it(`marks a ${code} line as blocking, keeps its route name and says why`, () => {
        const d = lineRouteDisplay({ routeSource: 'VARIANT', routeBlocker: code, effectiveRouteName: 'Manufacture → Pick & Ship' });
        expect(d.kind).toBe('blocked');
        expect(d.icon).toBe('⚠');
        expect(d.name).toBe('Manufacture → Pick & Ship');
        expect(d.hint).toContain(words);
        expect(d.hint).toContain('cannot be confirmed');
      });
    }
  });

  describe('confirmBlockedSummary — make-to-order lines', () => {
    it('counts the lines that cannot be made to order apart from the unrouted ones, and never calls them unrouted', () => {
      const text = confirmBlockedSummary([
        { lineNumber: 2, code: 'BOM_MISSING', message: 'Line 2: Custom Gear Assy has no active bill of materials, so it can\'t be made to order.' },
        { lineNumber: 5, code: 'PRODUCTION_WAREHOUSE_MISSING', message: 'Line 5: …' }
      ]);
      expect(text).toContain("2 lines can't be made to order (lines 2, 5)");
      expect(text).not.toContain('no usable fulfillment route');
      expect(text).not.toContain('Line 2: Custom Gear Assy');
    });

    it('gives both reasons when both kinds block', () => {
      const text = confirmBlockedSummary([
        { lineNumber: 1, code: 'ROUTE_MISSING', message: 'x' },
        { lineNumber: 2, code: 'MANUFACTURING_DISABLED', message: 'y' }
      ]);
      expect(text).toContain('1 line has no usable fulfillment route (line 1)');
      expect(text).toContain("1 line can't be made to order (line 2)");
    });
  });

  describe('buildDeliveryPreviewView — the production group', () => {
    it('lists the make-to-order lines as production, with their route, apart from the deliveries', () => {
      const view = buildDeliveryPreviewView(preview());

      expect(view.groups.length).toBe(1);
      expect(view.production.map(p => `${p.number}:${p.description}:${p.quantity}:${p.routeName}`))
        .toEqual(['2:Custom Gear Assy:50:Manufacture → Pick, Pack & Ship']);
      expect(view.exempt).toEqual([]);
      expect(view.unroutable).toEqual([]);
    });

    it('has no production group for an older server that sends no productionLines', () => {
      const view = buildDeliveryPreviewView(preview({ productionLines: undefined }));
      expect(view.production).toEqual([]);
    });

    it('lists a line that cannot be made to order with the reason, and keeps it out of the order-level reasons', () => {
      const view = buildDeliveryPreviewView(preview({
        canConfirm: false, productionLines: [],
        lines: [pLine(), { ...GEAR, routeBlocker: 'BOM_MISSING' }],
        blockers: [{ lineUuid: 'l2', lineNumber: 2, code: 'BOM_MISSING', message: 'Line 2: Custom Gear Assy has no active bill of materials…' }]
      }));

      expect(view.unroutable.map(u => u.number)).toEqual([2]);
      expect(view.unroutable[0].message).toContain('bill of materials');
      expect(view.orderBlockers).toEqual([]);
    });

    it('renumbers production lines the way the page numbers them', () => {
      const view = buildDeliveryPreviewView(preview(), n => ({ number: n + 1, description: `Form line ${n}` }));
      expect(view.production[0].number).toBe(3);
      expect(view.production[0].description).toBe('Form line 2');
    });
  });

  describe('previewHeadline', () => {
    it('names both counts on a mixed order', () => {
      expect(previewHeadline(buildDeliveryPreviewView(preview())))
        .toBe('On confirmation, 1 delivery order and 1 production order will be created:');
    });

    it('names only production when every line is made to order', () => {
      expect(previewHeadline(buildDeliveryPreviewView(preview({ deliveryCount: 0, groups: [] }))))
        .toBe('On confirmation, 1 production order will be created:');
    });

    it('keeps the A33 words for a stock-only order', () => {
      expect(previewHeadline(buildDeliveryPreviewView(preview({ productionLines: [], deliveryCount: 2 }))))
        .toBe('On confirmation, 2 delivery orders will be created:');
      expect(previewHeadline(buildDeliveryPreviewView(preview({ productionLines: [], deliveryCount: 0, groups: [] }))))
        .toBe('No delivery order can be created yet.');
    });

    it('says the problems come first when the order is blocked', () => {
      expect(previewHeadline(buildDeliveryPreviewView(preview({ canConfirm: false }))))
        .toContain('once the problems below are fixed');
    });
  });

  describe('confirmReadyTooltip — PD-08', () => {
    it('says what confirming will create', () => {
      expect(confirmReadyTooltip(preview({ deliveryCount: 2 }))).toBe('Will create 2 delivery orders and 1 production order');
      expect(confirmReadyTooltip(preview({ deliveryCount: 1, productionLines: [] }))).toBe('Will create 1 delivery order');
      expect(confirmReadyTooltip(null)).toBe('');
    });
  });

  describe('R-15 wording', () => {
    it('labels MAKE_TO_ORDER sourcing "Make to order"', () => {
      expect(MAKE_TO_ORDER_LABEL).toBe('Make to order');
      expect(sourcingLabel('MAKE_TO_ORDER')).toBe('Make to order');
      expect(sourcingLabel('BACK_TO_BACK')).toBe('Back To Back');
      expect(sourcingLabel(null)).toBe('—');
    });

    it('shows the route category as a tag, never as sourcing', () => {
      expect(routeCategoryTag('MANUFACTURE')).toEqual({ label: 'Manufacture', severity: 'warn' });
      expect(routeCategoryTag('STOCK')).toEqual({ label: 'Stock', severity: 'secondary' });
      expect(routeCategoryTag(null)).toBeNull();
    });

    it('knows a make-to-order line by its sourcing, or by its route category', () => {
      expect(isMakeToOrderLine({ fulfillmentMode: 'MAKE_TO_ORDER' })).toBeTrue();
      expect(isMakeToOrderLine({ effectiveRouteCategory: 'MANUFACTURE' })).toBeTrue();
      expect(isMakeToOrderLine({ fulfillmentMode: 'IN_STOCK', effectiveRouteCategory: 'STOCK' })).toBeFalse();
    });
  });
});
