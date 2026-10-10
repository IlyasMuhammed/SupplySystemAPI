import { AfterViewChecked, AfterViewInit, Component, Input, NgZone, OnChanges, OnDestroy, inject, signal } from '@angular/core';
import { CommonModule } from '@angular/common';

export interface FlowSection {
    /** id of the element to scroll to — give that element class="sf-section" too. */
    id: string;
    label: string;
    count?: number | null;
}

/**
 * Section tabs under the header.
 * <ul>
 *   <li><b>tabs</b> (default): only the selected section is shown; the others are hidden (display:none) by id, so a
 *       page needs no changes. A page may also read {@link isActive} (template ref) to skip rendering hidden ones.</li>
 *   <li><b>scroll</b>: every section stays on the page; click scrolls to it, the one in view is highlighted.</li>
 * </ul>
 */
@Component({
    selector: 'sf-anchors',
    standalone: true,
    imports: [CommonModule],
    template: `
        <nav class="sf-anch" [attr.aria-label]="mode === 'tabs' ? 'Tabs' : 'Sections'" [attr.role]="mode === 'tabs' ? 'tablist' : null">
            <button *ngFor="let s of sections" type="button" [class.on]="active() === s.id" (click)="go(s.id)"
                    [attr.role]="mode === 'tabs' ? 'tab' : null"
                    [attr.aria-selected]="mode === 'tabs' ? active() === s.id : null"
                    [attr.aria-current]="mode !== 'tabs' && active() === s.id ? 'true' : null" [attr.data-testid]="'anchor-' + s.id">
                {{ s.label }}<span *ngIf="s.count != null" class="sf-count">{{ s.count }}</span>
            </button>
        </nav>
    `
})
export class FlowAnchorsComponent implements AfterViewInit, AfterViewChecked, OnChanges, OnDestroy {
    private readonly zone = inject(NgZone);
    private observer: IntersectionObserver | null = null;
    private visible = new Map<string, number>();

    @Input() sections: FlowSection[] = [];
    @Input() mode: 'scroll' | 'tabs' = 'tabs';
    readonly active = signal<string | null>(null);

    ngOnChanges(): void {
        // A section can disappear (e.g. Approval once a PO is approved): fall back to the first one.
        const current = this.active();
        if ((!current || !this.sections.some(s => s.id === current)) && this.sections.length) this.active.set(this.sections[0].id);
        this.observe();
    }

    ngAfterViewInit(): void {
        // Sections often render after data arrives; observe again on the next frames.
        setTimeout(() => this.observe(), 0);
        setTimeout(() => this.observe(), 600);
    }

    /**
     * Tabs mode: show the selected section, hide the other listed ones. Runs after every check because sections
     * appear later (data arrives, *ngIf flips); touching only style.display on a handful of ids keeps it cheap.
     * The selection is never changed here: a page that renders sections with *ngIf only creates the newly selected
     * one on the next check, so "not on the page yet" must not undo the user's click. (A tab removed from
     * `sections` is handled in ngOnChanges.)
     */
    ngAfterViewChecked(): void {
        if (this.mode !== 'tabs' || typeof document === 'undefined') return;
        const current = this.active();
        for (const s of this.sections) {
            const el = document.getElementById(s.id);
            if (!el) continue;
            const want = s.id === current ? '' : 'none';
            if (el.style.display !== want) el.style.display = want;
        }
    }

    /** Tabs mode: is this section the one to show? */
    isActive(id: string): boolean {
        return this.active() === id;
    }

    go(id: string): void {
        this.active.set(id);
        if (this.mode === 'tabs') { this.ngAfterViewChecked(); return; }
        document.getElementById(id)?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    }

    private observe(): void {
        if (this.mode === 'tabs') { this.observer?.disconnect(); return; }
        if (typeof IntersectionObserver === 'undefined') return;
        this.observer?.disconnect();
        this.visible.clear();
        this.zone.runOutsideAngular(() => {
            this.observer = new IntersectionObserver(
                (entries) => {
                    for (const e of entries) this.visible.set(e.target.id, e.isIntersecting ? e.intersectionRatio : 0);
                    const first = this.sections.find((s) => (this.visible.get(s.id) ?? 0) > 0);
                    if (first && first.id !== this.active()) this.zone.run(() => this.active.set(first.id));
                },
                { rootMargin: '-120px 0px -55% 0px', threshold: [0, 0.01] }
            );
            for (const s of this.sections) {
                const el = document.getElementById(s.id);
                if (el) this.observer.observe(el);
            }
        });
    }

    ngOnDestroy(): void {
        this.observer?.disconnect();
    }
}
