// A51 P3 - timezone choices for the Create Organization form. The server validates the value (IANA name, max 50 characters),
// so this list only has to be a sensible set of common zones; the country's own default is always offered first.

export const COMMON_TIMEZONES: readonly string[] = [
  'UTC',
  'Africa/Cairo', 'Africa/Casablanca', 'Africa/Johannesburg', 'Africa/Lagos', 'Africa/Nairobi',
  'America/Anchorage', 'America/Argentina/Buenos_Aires', 'America/Bogota', 'America/Chicago', 'America/Denver',
  'America/Halifax', 'America/Los_Angeles', 'America/Mexico_City', 'America/New_York', 'America/Sao_Paulo', 'America/Toronto',
  'Asia/Baghdad', 'Asia/Bangkok', 'Asia/Colombo', 'Asia/Dhaka', 'Asia/Dubai', 'Asia/Hong_Kong', 'Asia/Jakarta', 'Asia/Karachi',
  'Asia/Kathmandu', 'Asia/Kolkata', 'Asia/Kuala_Lumpur', 'Asia/Manila', 'Asia/Qatar', 'Asia/Riyadh', 'Asia/Seoul',
  'Asia/Shanghai', 'Asia/Singapore', 'Asia/Tashkent', 'Asia/Tehran', 'Asia/Tokyo',
  'Atlantic/Reykjavik',
  'Australia/Brisbane', 'Australia/Melbourne', 'Australia/Perth', 'Australia/Sydney',
  'Europe/Amsterdam', 'Europe/Berlin', 'Europe/Istanbul', 'Europe/London', 'Europe/Madrid', 'Europe/Moscow', 'Europe/Paris',
  'Europe/Rome', 'Europe/Zurich',
  'Pacific/Auckland', 'Pacific/Honolulu'
];

export interface TimezoneOption {
  label: string;
  value: string;
}

/**
 * The dropdown options: the country's default zone first (marked), then the common zones, then the value already chosen when
 * it is in neither list (so an unusual zone typed earlier is never silently dropped). No duplicates.
 */
export function timezoneOptions(countryDefault?: string | null, current?: string | null): TimezoneOption[] {
  const seen = new Set<string>();
  const out: TimezoneOption[] = [];
  const add = (value: string | null | undefined, label?: string) => {
    const v = (value ?? '').trim();
    if (!v || seen.has(v)) return;
    seen.add(v);
    out.push({ value: v, label: label ?? v });
  };
  const own = (countryDefault ?? '').trim();
  if (own) add(own, `${own} (country default)`);
  COMMON_TIMEZONES.forEach(z => add(z));
  add(current);
  return out;
}
