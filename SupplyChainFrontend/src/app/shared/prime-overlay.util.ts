// PrimeNG overlay-style components (Dropdown/Select, Calendar, MultiSelect, etc.) render their
// floating panel outside the DOM subtree of whichever component opened them, so a naive "is this
// click inside my element?" containment check treats picking an option as an outside click. This
// checks for PrimeNG's own overlay markers so slide-over panels can tell the two apart.
const PRIME_OVERLAY_SELECTOR = '.p-select-overlay, .p-overlay, .p-datepicker, [role="listbox"], [role="dialog"], [role="option"]';

export function isPrimeOverlayClick(target: EventTarget | null): boolean {
  return !!(target instanceof HTMLElement) && !!target.closest(PRIME_OVERLAY_SELECTOR);
}
