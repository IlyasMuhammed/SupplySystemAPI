import { of } from 'rxjs';

import { PartnerFinancialProfileComponent, profileChanges } from './partner-financial-profile.component';
import { mount, procedure, profile } from '../ar-ap.testing';

describe('PartnerFinancialProfileComponent (A48 C1 / contract §1)', () => {
  async function setup(role: 'Customer' | 'Vendor', perms: string[], over = {}) {
    const m = await mount(PartnerFinancialProfileComponent, { perms, prepare: api => {
      api.getProfile.and.returnValue(of(profile({ role, ...over })));
      api.getProcedures.and.returnValue(of([procedure()]));
      api.getEarlyPaymentTerms.and.returnValue(of([{ uuid: 'e1', code: '2-10', name: '2/10 Net 30', discountPercent: 2, discountDays: 10, netDays: 30, isActive: true }]));
      api.updateProfile.and.returnValue(of(profile({ role, paymentPriority: 'High' })));
    } });
    m.c.partnerUuid = 'c1';
    m.c.role = role;
    m.c.ngOnChanges();
    m.fixture.detectChanges();
    return m;
  }

  it('customer: limit from the master, hold, tier, dunning; read with role=Customer', async () => {
    const m = await setup('Customer', ['AR_VIEW'], { creditHold: true, creditHoldReason: 'Late', dunningProcedureCode: 'STANDARD', currentDunningLevel: 2 });
    expect(m.api.getProfile).toHaveBeenCalledWith('c1', 'Customer');
    expect(m.text('pf-limit')).toContain('100,000.00');
    expect(m.text('pf-hold')).toContain('On hold');
    expect(m.text('pf-dunning')).toContain('STANDARD · level 2');
    expect(m.q('pf-edit')).toBeNull();
    expect(m.q('pf-credit-link')).not.toBeNull();
  });

  it('vendor without an AP read code is not read', async () => {
    const m = await setup('Vendor', ['AR_VIEW']);
    expect(m.api.getProfile).not.toHaveBeenCalled();
    expect(m.q('profile-no-access')).not.toBeNull();
  });

  it('admin edits a vendor: only the changed fields go, with role and rowVersion', async () => {
    const m = await setup('Vendor', ['AP_VIEW', 'FINANCE_AR_AP_ADMIN']);
    m.c.edit();
    m.c.draft!.paymentPriority = 'High';
    m.c.save();
    expect(m.api.updateProfile).toHaveBeenCalledWith('c1', 'Vendor', { rowVersion: 'rv1', paymentPriority: 'High' });
    expect(m.c.editing).toBeFalse();
  });

  it('profileChanges: clearing a payment method sends \'\' (null would leave it unchanged on the server)', () => {
    const d = { dunningProcedureUuid: null, blockSalesAtDunning: false, excludeFromDunning: false, defaultPaymentMethod: null, paymentPriority: 'Normal',
      preferredPaymentMethod: null, withholdingTaxApplicable: false, earlyPaymentTermUuid: null, notes: '' };
    expect(profileChanges(profile({ defaultPaymentMethod: 'CASH' }), d, 'Customer')).toEqual({ rowVersion: 'rv1', defaultPaymentMethod: '' });
    expect(profileChanges(profile({ preferredPaymentMethod: 'CHEQUE' }), d, 'Vendor')).toEqual({ rowVersion: 'rv1', preferredPaymentMethod: '' });
    expect(profileChanges(profile({ preferredPaymentMethod: 'CHEQUE' }), { ...d, preferredPaymentMethod: 'CASH' }, 'Vendor'))
      .toEqual({ rowVersion: 'rv1', preferredPaymentMethod: 'CASH' });
  });

  it('profileChanges: a cleared procedure / term sends the clear flag', () => {
    const p = profile({ dunningProcedureUuid: 'p1', earlyPaymentTermUuid: 'e1' });
    const d = { dunningProcedureUuid: null, blockSalesAtDunning: false, excludeFromDunning: true, defaultPaymentMethod: null, paymentPriority: 'Normal',
      preferredPaymentMethod: null, withholdingTaxApplicable: false, earlyPaymentTermUuid: null, notes: '' };
    expect(profileChanges(p, d, 'Customer')).toEqual({ rowVersion: 'rv1', clearDunningProcedure: true, excludeFromDunning: true });
    expect(profileChanges(p, d, 'Vendor')).toEqual({ rowVersion: 'rv1', clearEarlyPaymentTerm: true });
  });
});
