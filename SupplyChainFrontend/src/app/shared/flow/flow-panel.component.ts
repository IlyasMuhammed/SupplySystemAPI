import { Component, Input, OnInit, model, signal } from '@angular/core';
import { CommonModule } from '@angular/common';

const OPEN_KEY = 'sf.panel.open';

/**
 * The SMS Flow right-hand side panel: tabs on top, content below; collapses to a thin rail (remembered per browser).
 *
 *   <sf-panel #panel [tabs]="['Activity', 'Files']">
 *     @if (panel.active() === 'Activity') { <app-timeline-panel …/> }
 *     @if (panel.active() === 'Files') { <app-attachment-list …/> }
 *   </sf-panel>
 */
@Component({
    selector: 'sf-panel',
    standalone: true,
    imports: [CommonModule],
    host: { '[class.sf-panel]': 'open()', '[class.sf-panel-rail]': '!open()' },
    template: `
        <ng-container *ngIf="open(); else rail">
            <div class="sf-panel-tabs" role="tablist">
                <button *ngFor="let t of tabs" type="button" role="tab" [class.on]="active() === t" [attr.aria-selected]="active() === t"
                        (click)="active.set(t)" [attr.data-testid]="'panel-tab-' + t">{{ t }}</button>
                <button type="button" class="sf-panel-close" (click)="setOpen(false)" aria-label="Hide side panel" title="Hide panel">
                    <i class="pi pi-angle-double-right"></i>
                </button>
            </div>
            <div class="sf-panel-body"><ng-content></ng-content></div>
        </ng-container>
        <ng-template #rail>
            <button type="button" (click)="setOpen(true)" aria-label="Show side panel" title="Show panel"><i class="pi pi-angle-double-left"></i></button>
        </ng-template>
    `
})
export class FlowPanelComponent implements OnInit {
    @Input() tabs: string[] = [];
    readonly active = model<string>('');
    readonly open = signal(true);

    ngOnInit(): void {
        if (!this.active() && this.tabs.length) this.active.set(this.tabs[0]);
        try {
            this.open.set(localStorage.getItem(OPEN_KEY) !== 'false');
        } catch {
            /* storage blocked: panel open */
        }
    }

    setOpen(open: boolean): void {
        this.open.set(open);
        try {
            localStorage.setItem(OPEN_KEY, String(open));
        } catch {
            /* ignore */
        }
    }
}
