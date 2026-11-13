import { Component, computed, inject, input } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { of } from 'rxjs';
import { switchMap } from 'rxjs/operators';
import { DemoRefsStore } from './demo-refs.store';

/**
 * A50 task 312: a small "Demo" pill on a record created by Settings › Sample Data.
 *
 *   <app-demo-badge entityType="Customer" [entityRef]="row.uuid"></app-demo-badge>
 *
 * Renders nothing for a real record, and when the lookup fails. All badges of a type share one lookup (DemoRefsStore).
 */
@Component({
  selector: 'app-demo-badge',
  standalone: true,
  template: `
    @if (demo()) {
      <span class="sf-pill vi demo-badge" data-testid="demo-badge"
            title="Sample data: remove it under Settings › Organization › Sample Data">Demo</span>
    }
  `,
  styles: [`
    :host { display: inline-flex; vertical-align: middle; }
    .demo-badge { font-size: .68rem; padding: .05rem .45rem; margin-left: .35rem; letter-spacing: .02em; }
  `]
})
export class DemoBadgeComponent {
  readonly entityType = input.required<string>();
  readonly entityRef = input<string | number | null | undefined>(null);

  private readonly store = inject(DemoRefsStore);

  private readonly request = computed(() => ({ type: this.entityType(), ref: this.entityRef() }));

  readonly demo = toSignal(
    toObservable(this.request).pipe(
      switchMap(r => r.type && r.ref != null && r.ref !== '' ? this.store.isDemo(r.type, String(r.ref)) : of(false))
    ),
    { initialValue: false }
  );
}
