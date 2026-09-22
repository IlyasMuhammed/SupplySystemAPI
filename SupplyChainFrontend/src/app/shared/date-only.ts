/** A calendar date as yyyy-MM-dd in the reader's own time zone, which is the day they picked. */
export function toDateOnly(date: Date): string {
  const p = (n: number) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${p(date.getMonth() + 1)}-${p(date.getDate())}`;
}

/** "2026-09-30T00:00:00Z" is the 30th, wherever the reader is. */
export function fromDateOnly(iso: string): Date {
  const [y, m, d] = iso.slice(0, 10).split('-').map(Number);
  return new Date(y, m - 1, d);
}
