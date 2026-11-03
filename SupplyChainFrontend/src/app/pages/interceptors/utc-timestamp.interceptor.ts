import { HttpInterceptorFn, HttpResponse } from '@angular/common/http';
import { map } from 'rxjs';

/** A date-time with a time of day and no zone: what the API writes for a UTC `DateTime` (SQL Server drops the 'Z'). */
const NO_OFFSET = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?$/;

/**
 * The properties that are audit timestamps, stamped with `DateTime.UtcNow` on the server: approvedAt, createdAt,
 * reversedAt … and the createdDate / modifiedDate family. A business date (invoiceDate, dueDate, validTo) is left
 * alone: it has no zone, and shifting it would move it to another day west or east of UTC.
 */
const UTC_KEY = /(?:At|Utc|Timestamp)$|^(?:createdDate|modifiedDate|updatedDate|createDate|updateDate|lastUpdated)$/;

/**
 * Marks the API's zone-less audit timestamps as UTC, so the browser shows them in the user's own time zone. Without
 * the 'Z' `new Date('2026-11-02T10:48:14')` is read as local time, and a screen shows 10:48 where another that
 * converts shows 15:48 for the same moment. Returns the same object when nothing changed.
 */
export function markUtcTimestamps<T>(value: T, key?: string): T {
  if (typeof value === 'string') {
    return (key && UTC_KEY.test(key) && NO_OFFSET.test(value) ? value + 'Z' : value) as T;
  }
  if (Array.isArray(value)) {
    let changed = false;
    const next = value.map(item => {
      const mapped = markUtcTimestamps(item, key);
      if (mapped !== item) changed = true;
      return mapped;
    });
    return (changed ? next : value) as T;
  }
  if (value !== null && typeof value === 'object' && Object.getPrototypeOf(value) === Object.prototype) {
    let copy: Record<string, unknown> | null = null;
    for (const [k, v] of Object.entries(value as Record<string, unknown>)) {
      const mapped = markUtcTimestamps(v, k);
      if (mapped !== v) (copy ??= { ...(value as Record<string, unknown>) })[k] = mapped;
    }
    return (copy ?? value) as T;
  }
  return value;
}

export const utcTimestampInterceptor: HttpInterceptorFn = (req, next) =>
  next(req).pipe(
    map(event => event instanceof HttpResponse && event.body !== null && typeof event.body === 'object'
      ? event.clone({ body: markUtcTimestamps(event.body) })
      : event)
  );
