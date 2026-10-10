import { isStrongPassword, passwordChecks } from './password-rule';

describe('password rule', () => {
  it('accepts a password that meets every rule', () => {
    expect(isStrongPassword('Str0ng!pw')).toBeTrue();
    expect(isStrongPassword('Abcdef1&')).toBeTrue();
  });

  it('refuses each missing requirement, like the server does', () => {
    expect(isStrongPassword('Sh0rt!')).toBeFalse();          // too short
    expect(isStrongPassword('alllower1!')).toBeFalse();      // no upper case
    expect(isStrongPassword('ALLUPPER1!')).toBeFalse();      // no lower case
    expect(isStrongPassword('NoDigits!!')).toBeFalse();      // no digit
    expect(isStrongPassword('NoSpecial12')).toBeFalse();     // no special
    expect(isStrongPassword('Wrong1Special_')).toBeFalse();  // '_' is not one of !@#$%^&*
    expect(isStrongPassword('')).toBeFalse();
    expect(isStrongPassword(null)).toBeFalse();
    expect(isStrongPassword(undefined)).toBeFalse();
  });

  it('reports each rule separately for the live checklist', () => {
    const checks = passwordChecks('abc1');
    expect(checks.map(c => c.id)).toEqual(['length', 'upper', 'lower', 'digit', 'special']);
    expect(checks.find(c => c.id === 'length')!.ok).toBeFalse();
    expect(checks.find(c => c.id === 'upper')!.ok).toBeFalse();
    expect(checks.find(c => c.id === 'lower')!.ok).toBeTrue();
    expect(checks.find(c => c.id === 'digit')!.ok).toBeTrue();
    expect(checks.find(c => c.id === 'special')!.ok).toBeFalse();
    expect(passwordChecks(null).every(c => !c.ok)).toBeTrue();
  });
});
