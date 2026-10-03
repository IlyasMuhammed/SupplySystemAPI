import { FulfillmentRouteModel } from '../../../services/fulfillment-routes.service';
import {
  RouteDraft, RouteStepDraft, abbreviatedSteps, addStep, availableSteps, defaultClassLabel, draftFromRoute, draftProblem,
  emptyDraft, hasShipStep, removeStep, routeStatusPath, stepRequests, stepsChanged
} from './fulfillment-routes.shared';

const step = (stepCode: RouteStepDraft['stepCode'], over: Partial<RouteStepDraft> = {}): RouteStepDraft =>
  ({ stepCode, isMandatory: true, description: '', ...over });

function route(over: Partial<FulfillmentRouteModel> = {}): FulfillmentRouteModel {
  return {
    uuid: 'r-1', code: 'PICK_PACK_SHIP', name: 'Pick, Pack & Ship', description: 'Full cycle', isDefault: false,
    isActive: true, isSystem: true, requiresPacking: true, requiresShipping: true, displayOrder: 30,
    steps: [
      { stepCode: 'PICK', label: 'Pick', stepOrder: 1, isMandatory: true, description: 'Generate pick list' },
      { stepCode: 'PACK', label: 'Pack', stepOrder: 2, isMandatory: true },
      { stepCode: 'GOODS_ISSUE', label: 'Goods Issue', stepOrder: 3, isMandatory: true },
      { stepCode: 'SHIP', label: 'Ship', stepOrder: 4, isMandatory: true }
    ],
    stepsText: 'Pick → Pack → Goods Issue → Ship', statusPath: [], createdDate: '', ...over
  };
}

function draft(over: Partial<RouteDraft> = {}): RouteDraft {
  return { ...emptyDraft(), code: 'HIGH_VALUE', name: 'High value', ...over };
}

describe('fulfillment routes — shared editor rules', () => {

  // ── Status preview: the contract's real statuses (§8), never the spec's fictional ones ──────────────────────

  describe('routeStatusPath (mirrors FulfillmentRouteStatusMap.StatusPath on the server)', () => {
    it('PICK_ONLY ends at DELIVERED through "Record collection", with no packing or shipping statuses', () => {
      expect(routeStatusPath(['PICK', 'GOODS_ISSUE']))
        .toEqual(['DRAFT', 'RELEASED', 'PICKING', 'PICKED', 'GOODS_ISSUED', 'DELIVERED']);
    });

    it('PICK_AND_SHIP passes IN_TRANSIT then DELIVERED', () => {
      expect(routeStatusPath(['PICK', 'GOODS_ISSUE', 'SHIP']))
        .toEqual(['DRAFT', 'RELEASED', 'PICKING', 'PICKED', 'GOODS_ISSUED', 'IN_TRANSIT', 'DELIVERED']);
    });

    it('PICK_PACK_SHIP adds PACKED', () => {
      expect(routeStatusPath(['PICK', 'PACK', 'GOODS_ISSUE', 'SHIP']))
        .toEqual(['DRAFT', 'RELEASED', 'PICKING', 'PICKED', 'PACKED', 'GOODS_ISSUED', 'IN_TRANSIT', 'DELIVERED']);
    });

    it('APPROVAL shows as STAGED (the approve action happens on a staged delivery) and is not repeated', () => {
      expect(routeStatusPath(['PICK', 'PACK', 'STAGE', 'APPROVAL', 'GOODS_ISSUE', 'SHIP']))
        .toEqual(['DRAFT', 'RELEASED', 'PICKING', 'PICKED', 'PACKED', 'STAGED', 'GOODS_ISSUED', 'IN_TRANSIT', 'DELIVERED']);
      expect(routeStatusPath(['PICK', 'APPROVAL', 'GOODS_ISSUE']))
        .toEqual(['DRAFT', 'RELEASED', 'PICKING', 'PICKED', 'STAGED', 'GOODS_ISSUED', 'DELIVERED']);
    });

    it('never uses the spec-only statuses COMPLETED, AT_HUB, OUT_FOR_DELIVERY or PARTIALLY_DELIVERED', () => {
      const all = routeStatusPath(['PICK', 'PACK', 'STAGE', 'APPROVAL', 'GOODS_ISSUE', 'SHIP']);
      for (const fictional of ['COMPLETED', 'AT_HUB', 'OUT_FOR_DELIVERY', 'DELIVERY_ATTEMPTED', 'PARTIALLY_DELIVERED']) {
        expect(all).not.toContain(fictional);
      }
    });
  });

  // ── List abbreviations ───────────────────────────────────────────────────────────────────────────────────

  it('abbreviates a step chain for the list ("Pick → GI → Ship")', () => {
    expect(abbreviatedSteps(['PICK', 'GOODS_ISSUE', 'SHIP'])).toBe('Pick → GI → Ship');
    expect(abbreviatedSteps(['PICK', 'PACK', 'STAGE', 'APPROVAL', 'GOODS_ISSUE', 'SHIP']))
      .toBe('Pick → Pack → Stage → Appr → GI → Ship');
  });

  it('names the default class: a route with SHIP is the default for shipping orders, one without for self-pickup (L-1)', () => {
    expect(defaultClassLabel(true)).toBe('shipping orders');
    expect(defaultClassLabel(false)).toBe('self-pickup orders');
  });

  // ── Steps stay in canonical order; only the allowed codes can be added or removed (L-2) ──────────────────

  describe('steps', () => {
    it('a new draft starts with the two required steps', () => {
      expect(emptyDraft().steps.map(s => s.stepCode)).toEqual(['PICK', 'GOODS_ISSUE']);
    });

    it('adds a step at its canonical position whatever order they are added in', () => {
      let steps = emptyDraft().steps;
      steps = addStep(steps, 'SHIP');
      steps = addStep(steps, 'STAGE');
      steps = addStep(steps, 'PACK');
      steps = addStep(steps, 'APPROVAL');
      expect(steps.map(s => s.stepCode)).toEqual(['PICK', 'PACK', 'STAGE', 'APPROVAL', 'GOODS_ISSUE', 'SHIP']);
    });

    it('does not add a step twice', () => {
      const steps = addStep(addStep(emptyDraft().steps, 'PACK'), 'PACK');
      expect(steps.map(s => s.stepCode)).toEqual(['PICK', 'PACK', 'GOODS_ISSUE']);
    });

    it('never removes PICK or GOODS_ISSUE', () => {
      const steps = addStep(emptyDraft().steps, 'SHIP');
      expect(removeStep(steps, 'PICK').map(s => s.stepCode)).toEqual(['PICK', 'GOODS_ISSUE', 'SHIP']);
      expect(removeStep(steps, 'GOODS_ISSUE').map(s => s.stepCode)).toEqual(['PICK', 'GOODS_ISSUE', 'SHIP']);
      expect(removeStep(steps, 'SHIP').map(s => s.stepCode)).toEqual(['PICK', 'GOODS_ISSUE']);
    });

    it('lists the steps that can still be added, in canonical order', () => {
      expect(availableSteps([step('PICK'), step('PACK'), step('GOODS_ISSUE'), step('SHIP')])).toEqual(['STAGE', 'APPROVAL']);
    });

    it('builds the request with step orders 1..n, trimmed descriptions, PICK and GOODS_ISSUE always mandatory', () => {
      const req = stepRequests([
        step('PICK', { isMandatory: false, description: '  Generate pick list ' }),
        step('STAGE', { isMandatory: false }),
        step('GOODS_ISSUE', { isMandatory: false }),
        step('SHIP')
      ]);
      expect(req).toEqual([
        { stepCode: 'PICK', stepOrder: 1, isMandatory: true, description: 'Generate pick list' },
        { stepCode: 'STAGE', stepOrder: 2, isMandatory: false, description: null },
        { stepCode: 'GOODS_ISSUE', stepOrder: 3, isMandatory: true, description: null },
        { stepCode: 'SHIP', stepOrder: 4, isMandatory: true, description: null }
      ]);
    });

    it('detects SHIP', () => {
      expect(hasShipStep([step('PICK'), step('GOODS_ISSUE')])).toBeFalse();
      expect(hasShipStep([step('PICK'), step('GOODS_ISSUE'), step('SHIP')])).toBeTrue();
    });
  });

  // ── Editing an existing route ─────────────────────────────────────────────────────────────────────────

  it('starts an edit draft from the route, steps in step order and the default box reflecting the route', () => {
    const d = draftFromRoute(route({ isDefault: true }));
    expect(d.code).toBe('PICK_PACK_SHIP');
    expect(d.name).toBe('Pick, Pack & Ship');
    expect(d.description).toBe('Full cycle');
    expect(d.displayOrder).toBe(30);
    expect(d.makeDefault).toBeTrue();
    expect(d.steps.map(s => s.stepCode)).toEqual(['PICK', 'PACK', 'GOODS_ISSUE', 'SHIP']);
    expect(d.steps[0].description).toBe('Generate pick list');
  });

  it('tells whether the steps were changed (codes, mandatory flags or descriptions)', () => {
    const r = route();
    const d = draftFromRoute(r);
    expect(stepsChanged(d.steps, r)).toBeFalse();
    expect(stepsChanged(removeStep(d.steps, 'PACK'), r)).toBeTrue();
    expect(stepsChanged(d.steps.map(s => s.stepCode === 'SHIP' ? { ...s, isMandatory: false } : s), r)).toBeTrue();
    expect(stepsChanged(d.steps.map(s => s.stepCode === 'PICK' ? { ...s, description: 'x' } : s), r)).toBeTrue();
  });

  // ── Client validation mirrors the server (BR-C1-01..05, field lengths, L-5) ─────────────────────────────

  describe('draftProblem', () => {
    it('accepts a valid new route', () => {
      expect(draftProblem(draft(), null)).toBeNull();
    });

    it('needs a code of up to 30 letters, digits or underscores on a new route (upper-cased first)', () => {
      expect(draftProblem(draft({ code: '' }), null)).toContain('Code');
      expect(draftProblem(draft({ code: 'HIGH VALUE' }), null)).toContain('Code');
      expect(draftProblem(draft({ code: 'X'.repeat(31) }), null)).toContain('Code');
      expect(draftProblem(draft({ code: ' high_value_2 ' }), null)).toBeNull();
    });

    it('ignores the code when editing (it never changes, L-4)', () => {
      expect(draftProblem(draftFromRoute(route()), route())).toBeNull();
    });

    it('needs a name of at most 100 characters and a description of at most 500', () => {
      expect(draftProblem(draft({ name: '  ' }), null)).toContain('Name');
      expect(draftProblem(draft({ name: 'N'.repeat(101) }), null)).toContain('Name');
      expect(draftProblem(draft({ description: 'D'.repeat(501) }), null)).toContain('Description');
    });

    it('needs a whole display order of 0 or more when editing', () => {
      expect(draftProblem({ ...draftFromRoute(route()), displayOrder: null }, route())).toContain('Order');
      expect(draftProblem({ ...draftFromRoute(route()), displayOrder: -1 }, route())).toContain('Order');
      expect(draftProblem(draft({ displayOrder: null }), null)).toBeNull();
    });

    it('limits a step description to 200 characters', () => {
      const steps = [step('PICK', { description: 'x'.repeat(201) }), step('GOODS_ISSUE')];
      expect(draftProblem(draft({ steps }), null)).toContain('200');
    });

    it('refuses steps without PICK or GOODS_ISSUE (BR-C1-03) or out of canonical order (BR-C1-05)', () => {
      expect(draftProblem(draft({ steps: [step('PICK')] }), null)).toContain('GOODS_ISSUE');
      expect(draftProblem(draft({ steps: [step('GOODS_ISSUE')] }), null)).toContain('PICK');
      expect(draftProblem(draft({ steps: [step('PICK'), step('SHIP'), step('GOODS_ISSUE')] }), null)).toContain('GOODS_ISSUE must come before SHIP');
    });

    it('does not check the steps of a system route — they are locked and not sent (D-10)', () => {
      const r = route({ isSystem: true });
      expect(draftProblem({ ...draftFromRoute(r), steps: [step('PICK')] }, r)).toBeNull();
    });

    it('refuses to move a default route to the other class by adding or removing SHIP (L-5)', () => {
      const r = route({ isSystem: false, isDefault: true, requiresShipping: true });
      const problem = draftProblem({ ...draftFromRoute(r), steps: removeStep(draftFromRoute(r).steps, 'SHIP') }, r);
      expect(problem).toContain('default route for shipping orders');
      expect(problem).toContain('make another route the default first');
      expect(draftProblem({ ...draftFromRoute(r), steps: removeStep(draftFromRoute(r).steps, 'PACK') }, r)).toBeNull();
    });
  });
});
