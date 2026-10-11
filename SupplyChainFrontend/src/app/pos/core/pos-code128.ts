// A39 — a tiny Code 128 (code set B) encoder for store-credit numbers (CN-…): printable ASCII 32–126, modulo-103 check
// symbol, quiet zones. Output: bar/space module widths, rendered as SVG rects by PosBarcodeComponent.

/** Bar/space widths (in modules) of symbol values 0–106; 106 = STOP (7 elements). Each symbol is 11 modules (STOP 13). */
export const CODE128_PATTERNS: readonly string[] = [
    '212222', '222122', '222221', '121223', '121322', '131222', '122213', '122312', '132212', '221213',
    '221312', '231212', '112232', '122132', '122231', '113222', '123122', '123221', '223211', '221132',
    '221231', '213212', '223112', '312131', '311222', '321122', '321221', '312212', '322112', '322211',
    '212123', '212321', '232121', '111323', '131123', '131321', '112313', '132113', '132311', '211313',
    '231113', '231311', '112133', '112331', '132131', '113123', '113321', '133121', '313121', '211331',
    '231131', '213113', '213311', '213131', '311123', '311321', '331121', '312113', '312311', '332111',
    '314111', '221411', '431111', '111224', '111422', '121124', '121421', '141122', '141221', '112214',
    '112412', '122114', '122411', '142112', '142211', '241211', '221114', '413111', '241112', '134111',
    '111242', '121142', '121241', '114212', '124112', '124211', '411212', '421112', '421211', '212141',
    '214121', '412121', '111143', '111341', '131141', '114113', '114311', '411113', '411311', '113141',
    '114131', '311141', '411131', '211412', '211214', '211232', '2331112'
];

export const START_B = 104;
export const STOP = 106;
export const QUIET_MODULES = 10;

/** Symbol values of `text` in code set B: [START B, data…, check, STOP]. Throws on characters outside ASCII 32–126. */
export function code128BValues(text: string): number[] {
    const values = [START_B];
    for (const ch of text) {
        const c = ch.charCodeAt(0);
        if (ch.length !== 1 || c < 32 || c > 126) throw new Error(`Code 128 B cannot encode "${ch}".`);
        values.push(c - 32);
    }
    let sum = START_B;
    for (let i = 1; i < values.length; i++) sum += values[i] * i;
    values.push(sum % 103);
    values.push(STOP);
    return values;
}

export interface BarcodeBar {
    /** Start, in modules from the left edge (quiet zone included). */
    x: number;
    width: number;
}

/** Bars of the barcode (spaces are the gaps) and its total width in modules, quiet zones included. */
export function code128Bars(text: string): { bars: BarcodeBar[]; modules: number } {
    const bars: BarcodeBar[] = [];
    let x = QUIET_MODULES;
    for (const v of code128BValues(text)) {
        const pattern = CODE128_PATTERNS[v];
        for (let i = 0; i < pattern.length; i++) {
            const w = Number(pattern[i]);
            if (i % 2 === 0) bars.push({ x, width: w });
            x += w;
        }
    }
    return { bars, modules: x + QUIET_MODULES };
}
