import { Component, HostBinding, Input } from '@angular/core';
import { NavigationEnd, Router, RouterModule } from '@angular/router';
import { animate, state, style, transition, trigger } from '@angular/animations';
import { Subscription } from 'rxjs';
import { filter } from 'rxjs/operators';
import { CommonModule } from '@angular/common';
import { RippleModule } from 'primeng/ripple';
import { MenuItem } from 'primeng/api';
import { LayoutService } from '../service/layout.service';

@Component({
    // eslint-disable-next-line @angular-eslint/component-selector
    selector: '[app-menuitem]',
    imports: [CommonModule, RouterModule, RippleModule],
    template: `
        <ng-container>
            <div *ngIf="root && item.visible !== false"
                 class="layout-menuitem-root-text"
                 [attr.tabindex]="item.items ? 0 : null"
                 [attr.role]="item.items ? 'button' : null"
                 [attr.aria-expanded]="item.items ? active : null"
                 (click)="itemClick($event)"
                 (keydown.enter)="itemClick($event)"
                 (keydown.space)="$event.preventDefault(); itemClick($event)">
                <span class="layout-menuitem-root-label">{{ item.label }}</span>
                <i class="pi pi-fw pi-angle-down layout-submenu-toggler" *ngIf="item.items"></i>
            </div>
            <a *ngIf="!root && (!item.routerLink || item.items) && item.visible !== false" [attr.href]="item.url" (click)="itemClick($event)" [ngClass]="item.styleClass" [attr.target]="item.target" tabindex="0" pRipple>
                <i [ngClass]="item.icon" class="layout-menuitem-icon"></i>
                <span class="layout-menuitem-text">{{ item.label }}</span>
                <i class="pi pi-fw pi-angle-down layout-submenu-toggler" *ngIf="item.items"></i>
            </a>
            <a
                *ngIf="item.routerLink && !item.items && item.visible !== false"
                (click)="itemClick($event)"
                [ngClass]="item.styleClass"
                [routerLink]="item.routerLink"
                [class.active-route]="routeActive"
                [attr.aria-current]="routeActive ? 'page' : null"
                [fragment]="item.fragment"
                [queryParamsHandling]="item.queryParamsHandling"
                [preserveFragment]="item.preserveFragment"
                [skipLocationChange]="item.skipLocationChange"
                [replaceUrl]="item.replaceUrl"
                [state]="item.state"
                [queryParams]="item.queryParams"
                [attr.target]="item.target"
                tabindex="0"
                pRipple
            >
                <i [ngClass]="item.icon" class="layout-menuitem-icon"></i>
                <span class="layout-menuitem-text">{{ item.label }}</span>
                <i class="pi pi-fw pi-angle-down layout-submenu-toggler" *ngIf="item.items"></i>
            </a>

            <ul *ngIf="item.items && item.visible !== false" [@children]="submenuAnimation">
                <ng-template ngFor let-child let-i="index" [ngForOf]="item.items">
                    <li app-menuitem [item]="child" [index]="i" [parentKey]="key" [siblings]="item.items" [class]="child['badgeClass']"></li>
                </ng-template>
            </ul>
        </ng-container>
    `,
    animations: [
        trigger('children', [
            state(
                'collapsed',
                style({
                    height: '0'
                })
            ),
            state(
                'expanded',
                style({
                    height: '*'
                })
            ),
            transition('collapsed <=> expanded', animate('400ms cubic-bezier(0.86, 0, 0.07, 1)'))
        ])
    ],
})
export class AppMenuitem {
    @Input() item!: MenuItem;

    @Input() index!: number;

    @Input() @HostBinding('class.layout-root-menuitem') root!: boolean;

    @Input() parentKey!: string;

    /** The items at this item's own level, so a more specific sibling link can take the highlight. */
    @Input() siblings: MenuItem[] | undefined;

    active = false;

    private static readonly SUBSET = { paths: 'subset', queryParams: 'ignored', matrixParams: 'ignored', fragment: 'ignored' } as const;

    private static linkOf(item: MenuItem): string | null {
        const link = Array.isArray(item.routerLink) ? item.routerLink[0] : item.routerLink;
        return typeof link === 'string' && link ? link : null;
    }

    /**
     * Highlighted when the current URL is this link or below it ('subset', so a record page keeps its list item lit) —
     * unless a sibling's link is longer, starts with this one and matches too: "New Order" (…/production-orders/new)
     * wins over "All Orders" (…/production-orders), so only one item at a level is ever highlighted.
     * <para>
     * Query parameters count too: "New Warehouse" (…/warehouses?action=create) and "All Warehouses" (…/warehouses)
     * share a path, so an item with queryParams is lit only while the URL carries them, and its plain sibling steps
     * aside while it is.
     * </para>
     */
    get routeActive(): boolean {
        const link = AppMenuitem.linkOf(this.item);
        if (!link) return false;
        const opts = this.item.routerLinkActiveOptions ?? AppMenuitem.SUBSET;
        if (!this.router.isActive(link, opts)) return false;
        if (this.item.queryParams && !this.queryParamsMatch(this.item.queryParams)) return false;
        return !(this.siblings ?? []).some((s) => {
            if (s === this.item || s.items || s.visible === false) return false;
            const other = AppMenuitem.linkOf(s);
            if (!other) return false;
            // Same path, but the sibling names query params the URL has (and this item doesn't): the sibling wins.
            if (other === link && s.queryParams && !this.item.queryParams && this.queryParamsMatch(s.queryParams)) return true;
            return other.length > link.length && other.startsWith(link + '/') && this.router.isActive(other, AppMenuitem.SUBSET);
        });
    }

    /** Every query param the item names is in the current URL with the same value. */
    private queryParamsMatch(wanted: Record<string, any>): boolean {
        const current = this.router.parseUrl(this.router.url).queryParams;
        return Object.entries(wanted).every(([k, v]) => v == null ? !(k in current) : String(current[k]) === String(v));
    }

    menuSourceSubscription: Subscription;

    key: string = '';

    constructor(
        public router: Router,
        private layoutService: LayoutService
    ) {
        // routeEvent: the item whose route just became active broadcasts its own key. Every item
        // in the tree — at every level, root included — opens if it IS that item or an ANCESTOR of
        // it (key is a prefix), and closes otherwise. A manual click broadcasts the same shape
        // without routeEvent: closes every item that is neither the clicked one nor one of its
        // ancestors, so opening a department closes its sibling departments (and whatever was open
        // under them), while opening a group inside an already-open department leaves that
        // department itself open — never resetting the whole tree the way a blunt "close
        // everything first" used to.
        this.menuSourceSubscription = this.layoutService.menuSource$.subscribe((value) => {
            Promise.resolve(null).then(() => {
                if (value.routeEvent) {
                    this.active = value.key === this.key || value.key.startsWith(this.key + '-') ? true : false;
                } else {
                    if (value.key !== this.key && !value.key.startsWith(this.key + '-')) {
                        this.active = false;
                    }
                }
            });
        });

        this.router.events.pipe(filter((event) => event instanceof NavigationEnd)).subscribe(() => {
            if (this.item.routerLink) {
                this.updateActiveStateFromRoute();
            }
        });
    }

    ngOnInit() {
        this.key = this.parentKey ? this.parentKey + '-' + this.index : String(this.index);

        if (this.item.routerLink) {
            this.updateActiveStateFromRoute();
        }
    }

    updateActiveStateFromRoute() {
        // 'subset', not 'exact' — matters far more now that a department only auto-opens when one
        // of its own links matches. A detail/edit/new sub-route (the overwhelming majority of real
        // navigation — a GRN, a production order, a sale order by id) shares every path segment
        // with its list page's own routerLink, just with more after it; 'exact' would never match
        // any of those, leaving every department collapsed on the very pages people spend the most
        // time on. Segment-aware, so it can't accidentally match an unrelated sibling route that
        // merely shares a text prefix (e.g. grn vs grn-returns).
        // A plain link defers to a more specific sibling (routeActive), so on ".../new" only "New Order" — not also
        // "All Orders" — claims the route and is marked open.
        let activeRoute = this.item.items
            ? this.router.isActive(this.item.routerLink[0], { paths: 'subset', queryParams: 'ignored', matrixParams: 'ignored', fragment: 'ignored' })
            : this.routeActive;

        if (activeRoute) {
            this.layoutService.onMenuStateChange({ key: this.key, routeEvent: true });
        }
    }

    itemClick(event: Event) {
        // avoid processing disabled items
        if (this.item.disabled) {
            event.preventDefault();
            return;
        }

        // execute command
        if (this.item.command) {
            this.item.command({ originalEvent: event, item: this.item });
        }

        // toggle active state — closing/opening siblings (never ancestors) happens entirely
        // through the onMenuStateChange broadcast below, handled in the constructor.
        if (this.item.items) {
            this.active = !this.active;
        }

        this.layoutService.onMenuStateChange({ key: this.key });
    }

    get submenuAnimation() {
        return this.active ? 'expanded' : 'collapsed';
    }

    @HostBinding('class.active-menuitem')
    get activeClass() {
        return this.active;
    }

    ngOnDestroy() {
        if (this.menuSourceSubscription) {
            this.menuSourceSubscription.unsubscribe();
        }
    }
}
