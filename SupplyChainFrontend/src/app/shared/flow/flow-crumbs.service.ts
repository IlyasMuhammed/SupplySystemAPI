import { Injectable, signal } from '@angular/core';

/** Breadcrumbs shown in the top bar ("Finance › Customer invoices › INV-…"), set by the page through <sf-page [crumbs]>. */
@Injectable({ providedIn: 'root' })
export class FlowCrumbsService {
    readonly crumbs = signal<string[]>([]);

    set(crumbs: string[] | null | undefined): void {
        this.crumbs.set((crumbs ?? []).filter((c) => !!c));
    }

    clear(): void {
        this.crumbs.set([]);
    }
}
