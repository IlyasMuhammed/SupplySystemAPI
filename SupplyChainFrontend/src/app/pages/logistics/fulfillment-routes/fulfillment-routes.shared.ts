import {
  ACTIVE_ROUTE_CATEGORIES, FULFILLMENT_STEP_CODES, FulfillmentRouteCategory, FulfillmentRouteModel, FulfillmentRouteStepRequest,
  FulfillmentStepCode, REQUIRED_FULFILLMENT_STEPS, routeCategoryOf, validateRouteSteps
} from '../../../services/fulfillment-routes.service';

// A33 C1 — the route editor's rules, kept out of the components so they can be tested on their own
// (docs/fulfillment-routes/API-CONTRACT.md §3 and §8). The server checks every rule again.

export const MAX_ROUTE_CODE = 30;
export const MAX_ROUTE_NAME = 100;
export const MAX_ROUTE_DESCRIPTION = 500;
export const MAX_STEP_DESCRIPTION = 200;
const CODE_PATTERN = /^[A-Z0-9_]+$/;

/** One step as the editor holds it. The steps are always kept in canonical order (L-2). */
export interface RouteStepDraft {
  stepCode: FulfillmentStepCode;
  isMandatory: boolean;
  description: string;
}

export interface RouteDraft {
  code: string;
  name: string;
  description: string;
  /** Null on a new route = after the last one. */
  displayOrder: number | null;
  steps: RouteStepDraft[];
  /** Make (or keep) it the default of its class: shipping orders when it has SHIP, self-pickup orders otherwise (L-1). */
  makeDefault: boolean;
  /** A34 C1. A MANUFACTURE route is never a default (D-6). */
  routeCategory: FulfillmentRouteCategory;
}

/** How the editor shows the category (D-8): read-only on a system route, disabled on a default route, else editable. */
export interface CategoryLock {
  readOnly: boolean;
  disabled: boolean;
  reason: string | null;
}

/** Short labels for the list's step column ("Pick → GI → Ship"). */
const STEP_ABBREVIATIONS: Record<FulfillmentStepCode, string> = {
  PICK: 'Pick', PACK: 'Pack', STAGE: 'Stage', APPROVAL: 'Appr', GOODS_ISSUE: 'GI', SHIP: 'Ship'
};

/** What each step tells the user it does, under the step in the editor. */
export const STEP_HINTS: Record<FulfillmentStepCode, string> = {
  PICK: 'Release, generate the pick list and pick.',
  PACK: 'Box the picked items into handling units.',
  STAGE: 'Move the packed goods to the dispatch area.',
  APPROVAL: 'A manager approves dispatch (Approve dispatch) before goods issue.',
  GOODS_ISSUE: 'Post the goods issue: stock leaves the warehouse.',
  SHIP: 'Consignment and carrier, until delivered.'
};

/** The statuses each step covers (contract §8). APPROVAL is an action on a STAGED delivery (D-7). */
const STATUSES_BY_STEP: Record<FulfillmentStepCode, readonly string[]> = {
  PICK: ['RELEASED', 'PICKING', 'PICKED'],
  PACK: ['PACKED'],
  STAGE: ['STAGED'],
  APPROVAL: ['STAGED'],
  GOODS_ISSUE: ['GOODS_ISSUED'],
  SHIP: ['IN_TRANSIT', 'DELIVERED']
};

/**
 * The delivery statuses a route is seen to pass through — the editor's live preview. Mirrors the server's
 * FulfillmentRouteStatusMap.StatusPath (the real statuses, D-2): steps left out are not listed (they are
 * auto-completed, D-3), and every route ends at DELIVERED — without SHIP, through "Record collection".
 */
export function routeStatusPath(steps: readonly string[]): string[] {
  const path = ['DRAFT'];
  for (const step of steps) {
    for (const status of STATUSES_BY_STEP[step as FulfillmentStepCode] ?? []) {
      if (!path.includes(status)) path.push(status);
    }
  }
  if (!path.includes('DELIVERED')) path.push('DELIVERED');
  return path;
}

export function abbreviatedSteps(steps: readonly string[]): string {
  return steps.map(s => STEP_ABBREVIATIONS[s as FulfillmentStepCode] ?? s).join(' → ');
}

/** The orders a default route serves (L-1). */
export function defaultClassLabel(requiresShipping: boolean): string {
  return requiresShipping ? 'shipping orders' : 'self-pickup orders';
}

export function isRequiredStep(code: FulfillmentStepCode): boolean {
  return REQUIRED_FULFILLMENT_STEPS.includes(code);
}

const rank = (code: FulfillmentStepCode) => FULFILLMENT_STEP_CODES.indexOf(code);

export function emptyDraft(): RouteDraft {
  return {
    code: '', name: '', description: '', displayOrder: null, makeDefault: false, routeCategory: 'STOCK',
    steps: REQUIRED_FULFILLMENT_STEPS.map(stepCode => ({ stepCode, isMandatory: true, description: '' }))
  };
}

/**
 * D-8 — what the model tells us. Usage (variants, open sale order lines, open production orders) is not on the model
 * (contract §3), so an in-use route stays editable here and the server's 409 explains it.
 */
export function categoryLock(route: FulfillmentRouteModel | null): CategoryLock {
  if (route?.isSystem) {
    return { readOnly: true, disabled: true, reason: 'A system route: its category can\'t be changed. Create a custom route instead.' };
  }
  if (route?.isDefault) {
    return {
      readOnly: false, disabled: true,
      reason: `The default route for ${defaultClassLabel(route.requiresShipping)}: its category can't be changed. ` +
        'Make another route the default first, or clear the default.'
    };
  }
  return { readOnly: false, disabled: false, reason: null };
}

export function draftFromRoute(route: FulfillmentRouteModel): RouteDraft {
  return {
    code: route.code,
    name: route.name,
    description: route.description ?? '',
    displayOrder: route.displayOrder,
    makeDefault: route.isDefault,
    routeCategory: routeCategoryOf(route),
    steps: [...route.steps]
      .sort((a, b) => a.stepOrder - b.stepOrder)
      .map(s => ({ stepCode: s.stepCode, isMandatory: s.isMandatory, description: s.description ?? '' }))
  };
}

/** Adds the step at its canonical position (L-2); a step already there is left alone. */
export function addStep(steps: RouteStepDraft[], code: FulfillmentStepCode): RouteStepDraft[] {
  if (steps.some(s => s.stepCode === code)) return steps;
  return [...steps, { stepCode: code, isMandatory: true, description: '' }].sort((a, b) => rank(a.stepCode) - rank(b.stepCode));
}

/** PICK and GOODS_ISSUE can't be removed (BR-C1-03). */
export function removeStep(steps: RouteStepDraft[], code: FulfillmentStepCode): RouteStepDraft[] {
  if (isRequiredStep(code)) return steps;
  return steps.filter(s => s.stepCode !== code);
}

export function availableSteps(steps: readonly RouteStepDraft[]): FulfillmentStepCode[] {
  return FULFILLMENT_STEP_CODES.filter(c => !steps.some(s => s.stepCode === c));
}

export function hasShipStep(steps: readonly RouteStepDraft[]): boolean {
  return steps.some(s => s.stepCode === 'SHIP');
}

/** The steps as the API takes them: orders 1..n, PICK and GOODS_ISSUE always mandatory. */
export function stepRequests(steps: readonly RouteStepDraft[]): FulfillmentRouteStepRequest[] {
  return steps.map((s, i) => ({
    stepCode: s.stepCode,
    stepOrder: i + 1,
    isMandatory: isRequiredStep(s.stepCode) ? true : s.isMandatory,
    description: s.description.trim() || null
  }));
}

/** Whether the draft's steps differ from the saved route's (codes, order, mandatory flags or descriptions). */
export function stepsChanged(steps: readonly RouteStepDraft[], route: FulfillmentRouteModel): boolean {
  const before = stepRequests(draftFromRoute(route).steps);
  const after = stepRequests(steps);
  return JSON.stringify(before) !== JSON.stringify(after);
}

/**
 * The first thing stopping a save, in words, or null. Mirrors the server (contract §3): code format (new routes
 * only — the code never changes, L-4), name / description lengths, the step rules BR-C1-03/04/05 + L-2 (custom
 * routes only — a system route's steps are locked and not sent, D-10), and L-5 (a default route can't change class).
 */
export function draftProblem(draft: RouteDraft, original: FulfillmentRouteModel | null): string | null {
  if (!original) {
    const code = draft.code.trim().toUpperCase();
    if (!code || code.length > MAX_ROUTE_CODE || !CODE_PATTERN.test(code)) {
      return `Code: up to ${MAX_ROUTE_CODE} letters, digits or underscores.`;
    }
  }

  const name = draft.name.trim();
  if (!name) return 'Name is required.';
  if (name.length > MAX_ROUTE_NAME) return `Name: at most ${MAX_ROUTE_NAME} characters.`;
  if (draft.description.trim().length > MAX_ROUTE_DESCRIPTION) return `Description: at most ${MAX_ROUTE_DESCRIPTION} characters.`;

  if (original && draft.displayOrder == null) return 'Order is required.';
  if (draft.displayOrder != null && (!Number.isInteger(draft.displayOrder) || draft.displayOrder < 0)) {
    return 'Order is a whole number, 0 or more.';
  }

  // A34 C1 — D-7 reserved categories, D-8 locked category, D-6 never a default.
  if (!ACTIVE_ROUTE_CATEGORIES.includes(draft.routeCategory)) {
    return `Route category '${draft.routeCategory}' is not yet available. Use STOCK or MANUFACTURE.`;
  }
  const lock = categoryLock(original);
  if (original && lock.disabled && draft.routeCategory !== routeCategoryOf(original)) return lock.reason;
  if (draft.routeCategory === 'MANUFACTURE' && draft.makeDefault) {
    return 'A make-to-order (MANUFACTURE) route can\'t be a default: assign it to the variants that are made to order instead.';
  }

  if (original?.isSystem) return null;

  if (draft.steps.some(s => s.description.trim().length > MAX_STEP_DESCRIPTION)) {
    return `Step description: at most ${MAX_STEP_DESCRIPTION} characters.`;
  }
  const stepProblem = validateRouteSteps(stepRequests(draft.steps));
  if (stepProblem) return stepProblem;

  if (original?.isDefault && original.requiresShipping !== hasShipStep(draft.steps)) {
    return `'${original.code}' is the default route for ${defaultClassLabel(original.requiresShipping)}; ` +
      `${original.requiresShipping ? 'removing' : 'adding'} SHIP would change that — make another route the default first.`;
  }
  return null;
}
