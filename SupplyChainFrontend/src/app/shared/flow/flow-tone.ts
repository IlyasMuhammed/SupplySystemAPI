/** Pill tone for a status code — the class to put next to `sf-pill`. '' is the neutral grey (drafts, unknown). */
export type FlowTone = '' | 'ok' | 'wn' | 'er' | 'in' | 'vi' | 'te';

const TONES: [RegExp, FlowTone][] = [
    [/^(CANCEL|REJECT|FAIL|OVERDUE|BLOCK|ERROR|LOST|VOID|EXPIRED|BLACKLIST|SUSPEND|DECLIN|INACTIVE|SHORT)/, 'er'],
    [/^(PARTIAL|PENDING|IN_APPROVAL|SUBMITTED|AWAIT|ON_HOLD|HOLD|WAIT|QC|IN_PROGRESS|PROCESSING|DUE|DELIVERING|PICKING|PACKING|STAGED|PLANNED|RELEASED|NEEDS|REVIEW|OPEN_BIDS)/, 'wn'],
    [/^(COMPLETE|RECEIVED|PAID|FULFIL|DELIVERED|CLOSED|DONE|POSTED|SETTLED|ACCEPTED|WON|RESOLVED|ACTIVE|SYNCED|PASS|AVAILABLE|CONVERTED|AWARDED)/, 'ok'],
    [/^(APPROVED|CONFIRMED|ISSUED|SENT|OPEN|QUOTED|BOOKED|DISPATCHED|SHIPPED|IN_TRANSIT|STARTED|RESERVED|ACKNOWLEDGED)/, 'in'],
    [/^(TO_INVOICE|INVOICED|RETURN)/, 'vi']
];

export function flowTone(status: string | null | undefined): FlowTone {
    const s = (status ?? '').toString().trim().toUpperCase().replace(/[\s-]+/g, '_');
    if (!s || s.startsWith('DRAFT') || s === 'NEW') return '';
    return TONES.find(([re]) => re.test(s))?.[1] ?? '';
}

/** "PARTIALLY_PAID" → "Partially paid". */
export function flowLabel(status: string | null | undefined): string {
    const s = (status ?? '').toString().replace(/_/g, ' ').toLowerCase().trim();
    return s ? s.charAt(0).toUpperCase() + s.slice(1) : '';
}
