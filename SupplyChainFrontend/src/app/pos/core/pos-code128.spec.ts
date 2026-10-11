import { CODE128_PATTERNS, QUIET_MODULES, STOP, code128Bars, code128BValues } from './pos-code128';

describe('A39 Code 128 encoder', () => {
    it('has 107 symbols of 11 modules (STOP 13), alternating bar/space widths 1–4', () => {
        expect(CODE128_PATTERNS.length).toBe(107);
        CODE128_PATTERNS.forEach((p, v) => {
            const sum = [...p].reduce((s, d) => s + Number(d), 0);
            expect(sum).withContext(`symbol ${v}`).toBe(v === STOP ? 13 : 11);
            expect(/^[1-4]+$/.test(p)).withContext(`symbol ${v}`).toBeTrue();
            // the bars (odd positions) always add up to an even number of modules (parity rule)
            const bars = [...p].filter((_, i) => i % 2 === 0).reduce((s, d) => s + Number(d), 0);
            expect(bars % 2).withContext(`symbol ${v}`).toBe(0);
        });
        expect(new Set(CODE128_PATTERNS).size).toBe(107);
    });

    it('computes the modulo-103 check symbol (PJJ123C ⇒ 55)', () => {
        const v = code128BValues('PJJ123C');
        expect(v[0]).toBe(104);
        expect(v.slice(1, 8)).toEqual([48, 42, 42, 17, 18, 19, 35]);
        expect(v[8]).toBe(55);
        expect(v[9]).toBe(STOP);
    });

    it('encodes a store-credit number into bars with quiet zones', () => {
        const text = 'CN-S1-20261011-0001';
        const { bars, modules } = code128Bars(text);
        // START + data + check = 11 modules each, STOP 13, plus 2 quiet zones
        expect(modules).toBe(QUIET_MODULES * 2 + (text.length + 2) * 11 + 13);
        // 3 bars per symbol, 4 for STOP
        expect(bars.length).toBe((text.length + 2) * 3 + 4);
        expect(bars[0]).toEqual({ x: QUIET_MODULES, width: 2 }); // START B = 211214
        const last = bars[bars.length - 1];
        expect(last.x + last.width).toBe(modules - QUIET_MODULES);
    });

    it('refuses characters outside code set B', () => {
        expect(() => code128BValues('CN-é')).toThrowError(/cannot encode/);
    });
});
