import { Component, Input, OnChanges, OnDestroy, inject } from '@angular/core';
import { FlowCrumbsService } from './flow-crumbs.service';

/**
 * SMS Flow page frame: full-bleed under the top bar, command bar on top, main column, optional <sf-panel> on the right.
 *
 *   <sf-page [crumbs]="['Finance', 'Customer invoices', inv.invoiceNumber]">
 *     <div sfCmd class="sf-cmd"> …primary action first… </div>
 *     <header class="sf-hdr"> … </header>
 *     <sf-anchors [sections]="…" />
 *     <div class="sf-body"> …sf-card sections… </div>
 *     <sf-panel [tabs]="…"> … </sf-panel>
 *   </sf-page>
 */
@Component({
    selector: 'sf-page',
    standalone: true,
    host: { class: 'sf-page' },
    template: `
        <ng-content select="[sfCmd]"></ng-content>
        <div class="sf-page-row">
            <div class="sf-page-main"><ng-content></ng-content></div>
            <ng-content select="sf-panel"></ng-content>
        </div>
    `
})
export class FlowPageComponent implements OnChanges, OnDestroy {
    private readonly crumbsService = inject(FlowCrumbsService);

    /** Top-bar breadcrumbs: area › page › record. */
    @Input() crumbs: (string | null | undefined)[] = [];

    ngOnChanges(): void {
        this.crumbsService.set(this.crumbs as string[]);
    }

    ngOnDestroy(): void {
        this.crumbsService.clear();
    }
}
