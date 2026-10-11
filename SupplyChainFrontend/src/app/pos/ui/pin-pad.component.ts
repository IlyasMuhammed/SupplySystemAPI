import { Component, HostListener, input, output, signal } from '@angular/core';

/** 4–6 digit PIN entry: masked dots + numeric keypad (touch) + physical digits/Backspace/Enter. */
@Component({
    selector: 'pos-pin-pad',
    standalone: true,
    template: `
        <div class="stack">
            <div class="pos-dots" data-testid="pin-dots" aria-live="polite" [attr.aria-label]="pin().length + ' digits entered'">
                @for (i of slots; track i) {
                    <span [class.f]="i < pin().length"></span>
                }
            </div>
            <div class="pos-keys">
                @for (k of keys; track k) {
                    <button class="pos-btn" type="button" [disabled]="disabled()" (click)="press(k)" [attr.data-testid]="'pin-key-' + k">{{ k }}</button>
                }
                <button class="pos-btn" type="button" [disabled]="disabled()" (click)="clear()" aria-label="Clear" data-testid="pin-clear"><i class="pi pi-times"></i></button>
                <button class="pos-btn" type="button" [disabled]="disabled()" (click)="press('0')" data-testid="pin-key-0">0</button>
                <button class="pos-btn" type="button" [disabled]="disabled()" (click)="back()" aria-label="Backspace" data-testid="pin-back"><i class="pi pi-delete-left"></i></button>
            </div>
            <button class="pos-btn primary lg block" type="button" [disabled]="disabled() || pin().length < 4" (click)="submit()" data-testid="pin-submit">
                @if (busy()) {
                    <i class="pi pi-spin pi-spinner"></i>
                }
                {{ submitLabel() }}
            </button>
        </div>
    `
})
export class PinPadComponent {
    readonly disabled = input(false);
    readonly busy = input(false);
    readonly submitLabel = input('Sign in');
    readonly entered = output<string>();

    readonly pin = signal('');
    readonly slots = [0, 1, 2, 3, 4, 5];
    readonly keys = ['1', '2', '3', '4', '5', '6', '7', '8', '9'];

    press(d: string): void {
        if (this.disabled() || this.pin().length >= 6) return;
        this.pin.update(p => p + d);
    }

    back(): void {
        this.pin.update(p => p.slice(0, -1));
    }

    clear(): void {
        this.pin.set('');
    }

    submit(): void {
        const p = this.pin();
        if (this.disabled() || p.length < 4) return;
        this.pin.set('');
        this.entered.emit(p);
    }

    @HostListener('document:keydown', ['$event'])
    onKey(e: KeyboardEvent): void {
        const target = e.target as HTMLElement | null;
        if (target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA')) return;
        if (/^\d$/.test(e.key)) this.press(e.key);
        else if (e.key === 'Backspace') this.back();
        else if (e.key === 'Enter') this.submit();
        else if (e.key === 'Escape') this.clear();
    }
}
