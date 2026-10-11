import { KeyboardWedgeDetector } from './pos-scanner';

function feed(d: KeyboardWedgeDetector, text: string, start: number, gap: number, enterGap = gap): string | null {
    let t = start;
    for (const ch of text) {
        d.handle(ch, t);
        t += gap;
    }
    return d.handle('Enter', t - gap + enterGap);
}

describe('A38 keyboard-wedge scanner detection', () => {
    it('detects a fast burst closed by Enter', () => {
        const d = new KeyboardWedgeDetector();
        expect(feed(d, '8901234567890', 1000, 8)).toBe('8901234567890');
    });

    it('ignores a person typing', () => {
        const d = new KeyboardWedgeDetector();
        expect(feed(d, '12345', 1000, 180)).toBeNull();
    });

    it('ignores bursts shorter than the minimum', () => {
        const d = new KeyboardWedgeDetector({ minLength: 4 });
        expect(feed(d, 'abc', 1000, 5)).toBeNull();
    });

    it('drops slow leading keys and keeps the fast tail', () => {
        const d = new KeyboardWedgeDetector();
        d.handle('x', 0);
        expect(feed(d, 'ABC123', 500, 10)).toBe('ABC123');
    });

    it('does not let Shift break a scan (upper-case codes)', () => {
        const d = new KeyboardWedgeDetector();
        d.handle('Shift', 100);
        d.handle('A', 105);
        d.handle('Shift', 108);
        d.handle('B', 110);
        d.handle('1', 115);
        d.handle('2', 120);
        expect(d.handle('Enter', 125)).toBe('AB12');
    });

    it('needs Enter soon after the burst', () => {
        const d = new KeyboardWedgeDetector();
        expect(feed(d, '1234567', 0, 5, 1000)).toBeNull();
    });

    it('resets on other keys', () => {
        const d = new KeyboardWedgeDetector();
        d.handle('1', 0);
        d.handle('2', 5);
        d.handle('ArrowDown', 8);
        expect(d.pending).toBe('');
    });
});
