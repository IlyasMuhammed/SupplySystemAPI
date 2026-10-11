// A38 §17.2 / D-16 — keyboard-wedge barcode scanners "type" the code very fast and end with Enter.
// A burst of printable keys, each within `maxGapMs` of the previous one, at least `minLength` long and closed by Enter,
// is a scan; anything slower is a person typing.

export interface WedgeOptions {
    maxGapMs?: number;
    minLength?: number;
}

export class KeyboardWedgeDetector {
    private buffer = '';
    private last = 0;
    private readonly maxGap: number;
    private readonly minLength: number;

    constructor(opts: WedgeOptions = {}) {
        this.maxGap = opts.maxGapMs ?? 50;
        this.minLength = opts.minLength ?? 4;
    }

    /**
     * Feed one keydown (`key` as in KeyboardEvent.key, `time` in ms). Returns the scanned code when this Enter closes a
     * scan, otherwise null.
     */
    handle(key: string, time: number): string | null {
        const gap = time - this.last;
        if (key === 'Enter') {
            const code = this.buffer;
            const fast = this.last > 0 && gap <= this.maxGap * 3;
            this.reset();
            return fast && code.length >= this.minLength ? code : null;
        }
        if (key.length !== 1) {
            // Shift and other modifiers arrive between characters of a scan; they do not break the burst.
            if (key === 'Shift') return null;
            this.reset();
            return null;
        }
        if (this.buffer && gap > this.maxGap) this.buffer = '';
        this.buffer += key;
        this.last = time;
        return null;
    }

    /** Characters of a burst in progress (so a caller can tell a scan is under way). */
    get pending(): string {
        return this.buffer;
    }

    reset(): void {
        this.buffer = '';
        this.last = 0;
    }
}

/** The browser BarcodeDetector API (Chrome/Edge on Android, ChromeOS, macOS) — the camera button hides without it. */
export function hasBarcodeDetector(): boolean {
    return typeof window !== 'undefined' && 'BarcodeDetector' in window;
}
