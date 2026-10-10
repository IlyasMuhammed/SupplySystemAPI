/**
 * The server's password rule (AuthController.IsStrongPassword): at least 8 characters, an upper-case letter,
 * a lower-case letter, a digit and one of !@#$%^&*. Used by every screen where an admin types a password for
 * someone else (Organizations > admin dialog, Users > Set password), so the live checklist and the server agree.
 */
export const PASSWORD_SPECIALS = '!@#$%^&*';

export interface PasswordRule {
  id: 'length' | 'upper' | 'lower' | 'digit' | 'special';
  label: string;
  test: (password: string) => boolean;
}

export const PASSWORD_RULES: readonly PasswordRule[] = [
  { id: 'length',  label: 'At least 8 characters',                 test: p => p.length >= 8 },
  { id: 'upper',   label: 'An upper-case letter',                  test: p => /\p{Lu}/u.test(p) },
  { id: 'lower',   label: 'A lower-case letter',                   test: p => /\p{Ll}/u.test(p) },
  { id: 'digit',   label: 'A digit',                               test: p => /\p{Nd}/u.test(p) },
  { id: 'special', label: `One of ${PASSWORD_SPECIALS}`,           test: p => /[!@#$%^&*]/.test(p) }
];

export interface PasswordCheck {
  id: PasswordRule['id'];
  label: string;
  ok: boolean;
}

/** Each rule with whether the password meets it — for a live checklist. */
export function passwordChecks(password: string | null | undefined): PasswordCheck[] {
  const p = password ?? '';
  return PASSWORD_RULES.map(r => ({ id: r.id, label: r.label, ok: r.test(p) }));
}

/** True when the password meets every rule the server enforces. */
export function isStrongPassword(password: string | null | undefined): boolean {
  const p = password ?? '';
  return PASSWORD_RULES.every(r => r.test(p));
}
