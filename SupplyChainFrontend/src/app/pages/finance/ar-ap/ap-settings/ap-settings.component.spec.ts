import { of } from 'rxjs';

import { ApSettingsComponent } from './ap-settings.component';
import { mount } from '../ar-ap.testing';

describe('ApSettingsComponent (A48 C9 tolerances / C15 terms)', () => {
  const rule = { uuid: 't1', name: 'DEFAULT', vendorUuid: null, vendorName: null, priceTolerancePercent: 2, priceToleranceAmount: 0, quantityTolerancePercent: 0,
    totalTolerancePercent: 0, totalToleranceAmount: 0, autoApproveWithin: false, isDefault: true, isActive: true };
  const term = { uuid: 'e1', code: '2-10', name: '2/10 Net 30', discountPercent: 2, discountDays: 10, netDays: 30, isActive: true };
  const setup = (perms: string[]) => mount(ApSettingsComponent, { perms, prepare: api => {
    api.getToleranceRules.and.returnValue(of([rule]));
    api.getEarlyPaymentTerms.and.returnValue(of([term]));
    api.createToleranceRule.and.returnValue(of(rule));
    api.updateToleranceRule.and.returnValue(of(rule));
    api.createEarlyPaymentTerm.and.returnValue(of(term));
    api.getSettings.and.returnValue(of({ creditControlMode: 'Warn' }));
    api.updateSettings.and.returnValue(of({ creditControlMode: 'Block' }));
  } });

  it('credit control mode (§1.4): shown to any A48 reader, changed by FINANCE_AR_AP_ADMIN only', async () => {
    const ar = await setup(['AR_VIEW']);
    expect(ar.c.mode).toBe('Warn');
    expect(ar.text('mode-help')).toContain('warning only');
    expect(ar.q('rules')).toBeNull();
    expect(ar.api.getToleranceRules).not.toHaveBeenCalled();
    ar.c.mode = 'Block';
    ar.c.saveMode();
    expect(ar.api.updateSettings).not.toHaveBeenCalled();
    const admin = await setup(['FINANCE_AR_AP_ADMIN']);
    admin.c.mode = 'Block';
    admin.c.saveMode();
    expect(admin.api.updateSettings).toHaveBeenCalledWith({ creditControlMode: 'Block' });
    expect(admin.c.mode).toBe('Block');
  });

  it('lists rules and terms read-only for an AP viewer', async () => {
    const m = await setup(['AP_VIEW']);
    expect(m.text('rule-t1')).toContain('Every vendor');
    expect(m.text('rule-t1')).toContain('Default');
    expect(m.text('term-2-10')).toContain('2/10 Net 30');
    expect(m.q('new-rule')).toBeNull();
    expect(m.q('edit-term-2-10')).toBeNull();
  });

  it('FINANCE_AR_AP_ADMIN edits a rule (PUT) and creates a term; the rules are checked first', async () => {
    const m = await setup(['FINANCE_AR_AP_ADMIN']);
    m.c.openRule(m.c.rules[0]);
    m.c.rule!.priceTolerancePercent = -1;
    m.c.saveRule();
    expect(m.c.formError).toBe('Tolerances cannot be negative.');
    m.c.rule!.priceTolerancePercent = 3;
    m.c.saveRule();
    expect(m.api.updateToleranceRule).toHaveBeenCalledWith('t1', jasmine.objectContaining({ priceTolerancePercent: 3, vendorUuid: null }));

    m.c.openTerm(null);
    m.c.term!.code = '1-15';
    m.c.term!.name = '1/15 Net 15';
    m.c.term!.discountDays = 15;
    m.c.term!.netDays = 15;
    m.c.saveTerm();
    expect(m.c.formError).toBe('Discount days must be fewer than net days.');
    m.c.term!.netDays = 45;
    m.c.saveTerm();
    expect(m.api.createEarlyPaymentTerm).toHaveBeenCalledWith(jasmine.objectContaining({ code: '1-15', discountDays: 15, netDays: 45 }));
  });
});
