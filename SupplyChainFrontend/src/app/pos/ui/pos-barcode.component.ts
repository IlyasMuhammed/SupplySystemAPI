import { Component, computed, input } from '@angular/core';
import { code128Bars } from '../core/pos-code128';

/**
 * A39 — a scannable Code 128 barcode as inline SVG (no library). Always black bars on a white field, also in dark mode,
 * so scanners and printers read it.
 */
@Component({
    selector: 'pos-barcode',
    standalone: true,
    styles: [`:host { display: block; } svg { display: block; width: 100%; height: auto; max-width: 360px; margin: 0 auto; } .t { text-align: center; font-family: ui-monospace, SFMono-Regular, Consolas, monospace; letter-spacing: .08em; margin-top: .2rem; }`],
    template: `
        @if (bars(); as b) {
            <svg [attr.viewBox]="'0 0 ' + b.modules + ' ' + height()" preserveAspectRatio="none" role="img" [attr.aria-label]="'Barcode ' + value()" data-testid="pos-barcode"
                style="aspect-ratio: 5 / 1" shape-rendering="crispEdges">
                <rect x="0" y="0" [attr.width]="b.modules" [attr.height]="height()" fill="#ffffff"></rect>
                @for (r of b.bars; track $index) {
                    <rect [attr.x]="r.x" y="0" [attr.width]="r.width" [attr.height]="height()" fill="#000000"></rect>
                }
            </svg>
            @if (showText()) { <div class="t">{{ value() }}</div> }
        } @else {
            <div class="t">{{ value() }}</div>
        }
    `
})
export class PosBarcodeComponent {
    readonly value = input.required<string>();
    /** viewBox height (the drawn box is 5:1 whatever the length). */
    readonly height = input(60);
    readonly showText = input(true);

    readonly bars = computed(() => {
        try {
            return this.value() ? code128Bars(this.value()) : null;
        } catch {
            return null;
        }
    });
}
