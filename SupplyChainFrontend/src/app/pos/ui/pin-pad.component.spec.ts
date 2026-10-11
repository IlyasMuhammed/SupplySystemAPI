import { TestBed } from '@angular/core/testing';
import { PinPadComponent } from './pin-pad.component';

describe('A38 PIN pad', () => {
    it('masks digits as dots, submits 4–6 digits and clears', () => {
        const f = TestBed.createComponent(PinPadComponent);
        const pad = f.componentInstance;
        const got: string[] = [];
        pad.entered.subscribe(p => got.push(p));
        f.detectChanges();
        const el: HTMLElement = f.nativeElement;
        const click = (id: string) => (el.querySelector(`[data-testid="${id}"]`) as HTMLButtonElement).click();

        ['1', '2', '3'].forEach(d => click(`pin-key-${d}`));
        f.detectChanges();
        expect(el.querySelectorAll('.pos-dots span.f').length).toBe(3);
        expect((el.querySelector('[data-testid="pin-submit"]') as HTMLButtonElement).disabled).toBeTrue();
        expect(el.querySelector('[data-testid="pin-dots"]')!.textContent!.trim()).toBe(''); // never shows the digits

        ['4', '5', '6', '7'].forEach(d => click(`pin-key-${d}`)); // 7th digit ignored
        f.detectChanges();
        expect(pad.pin()).toBe('123456');
        click('pin-back');
        click('pin-submit');
        expect(got).toEqual(['12345']);
        expect(pad.pin()).toBe('');
    });

    it('accepts the physical keyboard', () => {
        const f = TestBed.createComponent(PinPadComponent);
        const got: string[] = [];
        f.componentInstance.entered.subscribe(p => got.push(p));
        f.detectChanges();
        for (const key of ['9', '8', '7', '6', 'Enter']) document.dispatchEvent(new KeyboardEvent('keydown', { key }));
        expect(got).toEqual(['9876']);
    });
});
