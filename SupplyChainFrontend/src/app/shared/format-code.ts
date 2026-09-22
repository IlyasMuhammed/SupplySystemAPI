/** Turns PARTIALLY_FULFILLED into "Partially Fulfilled". */
export function formatCode(code?: string | null): string {
  if (!code) return '';
  return code
    .split('_')
    .map(word => word.charAt(0) + word.slice(1).toLowerCase())
    .join(' ');
}
