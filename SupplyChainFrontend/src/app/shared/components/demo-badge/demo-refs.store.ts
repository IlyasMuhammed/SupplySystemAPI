import { Injectable, Injector, inject } from '@angular/core';
import { Observable, of } from 'rxjs';
import { catchError, map, shareReplay } from 'rxjs/operators';
import { OnboardingDemoService } from '../../../services/onboarding-demo.service';

interface Entry { at: number; refs$: Observable<ReadonlySet<string>>; }

/**
 * A50 D-27: which records are sample (demo) data, one GET onboarding/demo/refs per entity type, shared by every badge
 * on the page and reused for a while. Never fails a page: an error (or a server without the endpoint) means "none".
 */
@Injectable({ providedIn: 'root' })
export class DemoRefsStore {
  static readonly TTL_MS = 60_000;

  private readonly injector = inject(Injector);
  private readonly cache = new Map<string, Entry>();

  /** The demo refs of a type, lower-cased. */
  refs(entityType: string): Observable<ReadonlySet<string>> {
    const hit = this.cache.get(entityType);
    if (hit && Date.now() - hit.at < DemoRefsStore.TTL_MS) return hit.refs$;

    let api: OnboardingDemoService | null = null;
    try { api = this.injector.get(OnboardingDemoService, null); } catch { api = null; }
    if (!api) return of(new Set<string>());

    const refs$ = api.getDemoRefs(entityType).pipe(
      map(res => new Set((res?.result ?? []).map(r => String(r).toLowerCase())) as ReadonlySet<string>),
      catchError(() => of(new Set<string>() as ReadonlySet<string>)),
      shareReplay(1)
    );
    this.cache.set(entityType, { at: Date.now(), refs$ });
    return refs$;
  }

  isDemo(entityType: string, ref: string | null | undefined): Observable<boolean> {
    if (!ref) return of(false);
    const key = String(ref).toLowerCase();
    return this.refs(entityType).pipe(map(set => set.has(key)));
  }

  /** Forget everything (after sample data is loaded or deleted). */
  invalidate(): void { this.cache.clear(); }
}
