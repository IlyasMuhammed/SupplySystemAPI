import { Directive, Input, OnDestroy, OnInit, inject } from '@angular/core';
import { AutoComplete } from 'primeng/autocomplete';
import { Subscription } from 'rxjs';

/**
 * Opens a p-autoComplete's suggestion list when the field gets focus — except for the focus PrimeNG itself gives the
 * input right after an option is picked. Calling `ac.search(…)` straight from (onFocus) reopened the list on every
 * selection, so the panel never closed ("it opens again and again").
 *
 *   <p-autoComplete smsOpenOnFocus …>                 always open on focus
 *   <p-autoComplete [smsOpenOnFocus]="!selectedPo" …> only while nothing is chosen
 */
@Directive({
    selector: 'p-autoComplete[smsOpenOnFocus], p-autocomplete[smsOpenOnFocus]',
    standalone: true
})
export class AutoCompleteOpenOnFocusDirective implements OnInit, OnDestroy {
    private readonly ac = inject(AutoComplete, { self: true });
    private readonly subs = new Subscription();
    private justSelected = false;
    private resetTimer: ReturnType<typeof setTimeout> | null = null;

    /** '' (attribute only) or true = open on focus; false = don't. */
    @Input() smsOpenOnFocus: boolean | '' = true;

    ngOnInit(): void {
        this.subs.add(
            this.ac.onSelect.subscribe(() => {
                // The focus PrimeNG puts back on the input after a pick arrives right after onSelect.
                this.justSelected = true;
                if (this.resetTimer) clearTimeout(this.resetTimer);
                this.resetTimer = setTimeout(() => (this.justSelected = false), 400);
            })
        );
        this.subs.add(
            this.ac.onFocus.subscribe((event: Event) => {
                if (this.justSelected) {
                    this.justSelected = false;
                    return;
                }
                if (this.smsOpenOnFocus === false) return;
                this.ac.search(event, '', 'focus');
            })
        );
    }

    ngOnDestroy(): void {
        this.subs.unsubscribe();
        if (this.resetTimer) clearTimeout(this.resetTimer);
    }
}
