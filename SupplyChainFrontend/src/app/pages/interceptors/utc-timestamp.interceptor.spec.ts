import { markUtcTimestamps } from './utc-timestamp.interceptor';

describe('markUtcTimestamps', () => {
  it('marks a zone-less audit timestamp as UTC, so it reads as 15:48 in Karachi rather than 10:48', () => {
    const out = markUtcTimestamps({ approvedAt: '2026-11-02T10:48:14.8143943', createdDate: '2026-11-02T10:28:48.518' });
    expect(out.approvedAt).toBe('2026-11-02T10:48:14.8143943Z');
    expect(out.createdDate).toBe('2026-11-02T10:28:48.518Z');
    expect(new Date(out.approvedAt).getTime()).toBe(Date.UTC(2026, 10, 2, 10, 48, 14, 814));
  });

  it('leaves business dates alone, even with a time of day, so they never move to another day', () => {
    const body = { invoiceDate: '2026-11-02T00:00:00', dueDate: '2026-12-02T00:00:00', validTo: '2026-12-31T23:59:59' };
    expect(markUtcTimestamps(body)).toBe(body);
  });

  it('does not double-mark a timestamp that already has a zone', () => {
    const body = { sentAt: '2026-11-02T10:48:14Z', seenAt: '2026-11-02T10:48:14+05:00' };
    expect(markUtcTimestamps(body)).toBe(body);
  });

  it('reaches into nested objects and arrays, copying only what changed', () => {
    const untouched: { name?: string; receivedAt?: string } = { name: 'x' };
    const out = markUtcTimestamps({ result: { items: [{ receivedAt: '2026-11-02T09:00:00' } as typeof untouched, untouched] } });
    expect(out.result.items[0].receivedAt).toBe('2026-11-02T09:00:00Z');
    expect(out.result.items[1]).toBe(untouched);
  });

  it('passes through values that are not plain JSON (null, numbers, Blob)', () => {
    const blob = new Blob(['x']);
    expect(markUtcTimestamps(null)).toBeNull();
    expect(markUtcTimestamps(5)).toBe(5);
    expect(markUtcTimestamps(blob)).toBe(blob);
  });
});
