import { Component, Input } from '@angular/core';
import { CommonModule } from '@angular/common';
import { TooltipModule } from 'primeng/tooltip';

export type FlowStageState = 'done' | 'on' | 'todo' | 'bad';

export interface FlowStage {
    label: string;
    /** Second line: a date, a percentage, "advance due"… */
    sub?: string | null;
    state: FlowStageState;
    /** Shown on hover, e.g. "Confirmed by Usman Khan on 2 Sep 2026, 14:05". */
    tooltip?: string | null;
}

/**
 * The process strip of the SMS Flow header: done stages green with ✓, the current one solid blue, the rest grey.
 * Build the array in the component from the document's status, e.g. via flowStagesFrom().
 */
@Component({
    selector: 'sf-stages',
    standalone: true,
    imports: [CommonModule, TooltipModule],
    template: `
        <div class="sf-stages" role="list" aria-label="Progress">
            <div *ngFor="let s of stages" class="sf-stg" [class.done]="s.state === 'done'" [class.on]="s.state === 'on'"
                 [class.bad]="s.state === 'bad'" role="listitem" [attr.aria-current]="s.state === 'on' ? 'step' : null"
                 [pTooltip]="s.tooltip || ''" [tooltipDisabled]="!s.tooltip" tooltipPosition="bottom"
                 [attr.aria-label]="s.tooltip ? s.label + ': ' + s.tooltip : null">
                <b>{{ s.state === 'done' ? '✓ ' : '' }}{{ s.label }}</b>
                <span *ngIf="s.sub">{{ s.sub }}</span>
            </div>
        </div>
    `
})
export class FlowStagesComponent {
    @Input() stages: FlowStage[] = [];
}

/**
 * Stages for a linear flow: everything before `current` is done, `current` is on, the rest to do.
 * `failed` (e.g. cancelled/rejected) marks the current stage red instead of blue.
 */
export function flowStagesFrom(labels: string[], current: number,
                               opts: { subs?: (string | null | undefined)[]; tooltips?: (string | null | undefined)[]; failed?: boolean } = {}): FlowStage[] {
    return labels.map((label, i) => ({
        label,
        sub: opts.subs?.[i] ?? null,
        tooltip: opts.tooltips?.[i] ?? null,
        state: i < current ? 'done' : i === current ? (opts.failed ? 'bad' : 'on') : 'todo'
    }));
}
