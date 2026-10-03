import { Component, EventEmitter, Input, OnChanges, Output, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { CheckboxModule } from 'primeng/checkbox';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { TooltipModule } from 'primeng/tooltip';
import { Observable, of } from 'rxjs';
import { catchError, map, switchMap } from 'rxjs/operators';

import {
  FULFILLMENT_STEP_LABELS, FulfillmentRouteModel, FulfillmentRoutesService, FulfillmentStepCode
} from '../../../../services/fulfillment-routes.service';
import {
  MAX_ROUTE_CODE, MAX_ROUTE_DESCRIPTION, MAX_ROUTE_NAME, MAX_STEP_DESCRIPTION, RouteDraft, STEP_HINTS, addStep,
  availableSteps, defaultClassLabel, draftFromRoute, draftProblem, emptyDraft, hasShipStep, isRequiredStep, removeStep,
  routeStatusPath, stepRequests, stepsChanged
} from '../fulfillment-routes.shared';

/** What the editor reports once the route itself is saved. `warning`: the route saved but the default change didn't. */
export interface RouteSavedEvent {
  route: FulfillmentRouteModel;
  created: boolean;
  warning?: string;
}

/**
 * A33-PA-08 — create or edit one fulfillment route (spec §10.2, contract §3). Shown in a dialog by the routes list.
 *
 * - The code is typed once and never changes (L-4).
 * - Steps are kept in the canonical order PICK, PACK, STAGE, APPROVAL, GOODS_ISSUE, SHIP (L-2): optional steps are
 *   added or removed, never reordered; PICK and GOODS_ISSUE are always there and always mandatory.
 * - A system route's steps are locked (D-10): only name, description and order are sent.
 * - The preview shows the real delivery statuses (contract §8), not the spec's.
 * - "Set as default" is per class (L-1): calls set-default / clear-default after the route is saved.
 */
@Component({
  selector: 'app-fulfillment-route-editor',
  standalone: true,
  imports: [
    CommonModule, FormsModule, ButtonModule, CheckboxModule, InputNumberModule, InputTextModule, TextareaModule, TooltipModule
  ],
  templateUrl: './fulfillment-route-editor.component.html',
  styleUrls: ['./fulfillment-route-editor.component.scss']
})
export class FulfillmentRouteEditorComponent implements OnChanges {
  /** The route being edited; null for a new one. */
  @Input() route: FulfillmentRouteModel | null = null;
  /** No FULFILLMENT_ROUTE_MANAGE: shows the route, changes nothing. */
  @Input() readOnly = false;
  /** Every route of the organization, to name the default a new default would replace. */
  @Input() routes: FulfillmentRouteModel[] = [];

  @Output() saved = new EventEmitter<RouteSavedEvent>();
  @Output() cancelled = new EventEmitter<void>();

  readonly maxCode = MAX_ROUTE_CODE;
  readonly maxName = MAX_ROUTE_NAME;
  readonly maxDescription = MAX_ROUTE_DESCRIPTION;
  readonly maxStepDescription = MAX_STEP_DESCRIPTION;
  readonly stepLabels = FULFILLMENT_STEP_LABELS;
  readonly stepHints = STEP_HINTS;
  readonly isRequired = isRequiredStep;

  draft: RouteDraft = emptyDraft();
  submitted = false;
  isSaving = false;
  saveError = '';

  constructor(private service: FulfillmentRoutesService) {}

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['route']) this.reset();
  }

  reset(): void {
    this.draft = this.route ? draftFromRoute(this.route) : emptyDraft();
    this.submitted = false;
    this.saveError = '';
    this.isSaving = false;
  }

  get isNew(): boolean { return !this.route; }

  /** A system route's steps can't be changed (D-10), and nothing can be in read-only mode. */
  get stepsLocked(): boolean { return this.readOnly || !!this.route?.isSystem; }

  get addable(): FulfillmentStepCode[] { return this.stepsLocked ? [] : availableSteps(this.draft.steps); }

  get statusPath(): string[] { return routeStatusPath(this.draft.steps.map(s => s.stepCode)); }

  get shipsToCustomer(): boolean { return hasShipStep(this.draft.steps); }

  get defaultClass(): string { return defaultClassLabel(this.shipsToCustomer); }

  /** set-default on an inactive route is a 400; the box stays as it is. */
  get canChangeDefault(): boolean { return !this.readOnly && (this.isNew || !!this.route?.isActive); }

  /** The route that is the default of this class today (not this one), if any. */
  get currentDefault(): FulfillmentRouteModel | null {
    return this.routes.find(r => r.isDefault && r.requiresShipping === this.shipsToCustomer && r.uuid !== this.route?.uuid) ?? null;
  }

  get problem(): string | null { return draftProblem(this.draft, this.route); }

  addStep(code: FulfillmentStepCode): void {
    if (this.stepsLocked) return;
    this.draft.steps = addStep(this.draft.steps, code);
  }

  removeStep(code: FulfillmentStepCode): void {
    if (this.stepsLocked) return;
    this.draft.steps = removeStep(this.draft.steps, code);
  }

  stepNumber(index: number): string { return '①②③④⑤⑥'.charAt(index) || String(index + 1); }

  save(): void {
    this.submitted = true;
    if (this.readOnly || this.isSaving || this.problem) return;
    this.isSaving = true;
    this.saveError = '';

    const d = this.draft;
    const original = this.route;
    const description = d.description.trim() || null;
    const save$ = original
      ? this.service.updateRoute(original.uuid, {
          name: d.name.trim(),
          description,
          displayOrder: d.displayOrder!,
          // System routes: locked (D-10). Custom routes: only when changed, so a name edit never races a step edit.
          steps: original.isSystem || !stepsChanged(d.steps, original) ? null : stepRequests(d.steps)
        })
      : this.service.createRoute({
          code: d.code.trim().toUpperCase(),
          name: d.name.trim(),
          description,
          displayOrder: d.displayOrder,
          steps: stepRequests(d.steps)
        });

    save$.pipe(
      switchMap(res => this.applyDefault(res.result, d.makeDefault))
    ).subscribe({
      next: ({ route, warning }) => {
        this.isSaving = false;
        this.saved.emit({ route, created: !original, warning });
      },
      error: (err) => {
        this.isSaving = false;
        this.saveError = err?.error?.message || 'The route could not be saved.';
      }
    });
  }

  /** After the route itself is saved: set or clear the default as the box says. A refusal is reported, not fatal. */
  private applyDefault(saved: FulfillmentRouteModel, makeDefault: boolean): Observable<{ route: FulfillmentRouteModel; warning?: string }> {
    let change$: Observable<{ result: FulfillmentRouteModel }> | null = null;
    if (makeDefault && !saved.isDefault) change$ = this.service.setDefault(saved.uuid);
    if (!makeDefault && saved.isDefault) change$ = this.service.clearDefault(saved.uuid);
    if (!change$) return of({ route: saved });
    return change$.pipe(
      map(res => ({ route: res.result ?? saved })),
      catchError(err => of({
        route: saved,
        warning: `The route was saved, but the default was not changed: ${err?.error?.message || 'the request failed.'}`
      }))
    );
  }
}
