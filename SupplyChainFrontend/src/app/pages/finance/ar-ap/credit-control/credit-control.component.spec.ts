import { of, throwError } from 'rxjs';

import { CreditControlComponent, dunningView } from './credit-control.component';
import { dunningHistory, exposure, mount, page, procedure, profile } from '../ar-ap.testing';

describe('CreditControlComponent (A48 tasks 545–551)', () => {
  const detail = (perms: string[], prep: (api: any) => void = () => {}) => mount(CreditControlComponent, {
    perms, params: { uuid: 'c1' }, prepare: api => {
      api.getExposure.and.returnValue(of(exposure()));
      api.getProfile.and.returnValue(of(profile({ currentDunningLevel: 2, lastDunningDate: '2026-10-10', blockSalesAtDunning: true })));
      api.getProcedures.and.returnValue(of([procedure()]));
      api.getCreditHistory.and.returnValue(page([{ uuid: 'h1', action: 'Hold', reason: '60+ days overdue', previousValue: 'No hold', newValue: 'On hold',
        requestedAt: '2026-09-15T10:00:00Z', requestedByName: 'J. Smith', status: 'Completed' }]));
      api.getCustomerDunningHistory.and.returnValue(of(dunningHistory([
        { at: '2026-10-10T08:00:00Z', kind: 'Notice', level: 2, runNumber: 'DUN-0006', status: 'Sent', amount: 5000 },
        { at: '2026-09-01T08:00:00Z', kind: 'Notice', level: 1, runNumber: 'DUN-0002', status: 'Sent', amount: 3000 }])));
      api.placeHold.and.returnValue(of(exposure({ isOnHold: true })));
      api.setTemporaryLimit.and.returnValue(of(exposure()));
      api.resetDunning.and.returnValue(of(null));
      prep(api);
    }
  });

  it('limit, temporary limit, exposure breakdown, available and status (546)', async () => {
    const m = await detail(['AR_VIEW']);
    expect(m.text('title')).toContain('Acme Corp');
    expect(m.text('limit')).toContain('100,000.00');
    expect(m.text('breakdown')).toBe('Open orders 35,000.00 · Open invoices 52,500.00');
    expect(m.text('exposure')).toContain('87,500.00');
    expect(m.text('available')).toContain('12,500.00');
    expect(m.text('status')).toBe('Active');
  });

  it('dunning status from the profile and the procedure: level 2 of 4, next level date, block at 4 (547)', async () => {
    const m = await detail(['AR_VIEW']);
    expect(m.text('dunning-level')).toBe('2 of 4');
    expect(m.c.dunning?.nextDate).toBe('2026-10-24');
    expect(m.text('dunning-block')).toBe('At level 4');
    expect(dunningView(profile({ currentDunningLevel: 4 }), [procedure()])?.nextLevel).toBeNull();
    expect(dunningView(profile({ blockSalesAtDunning: false }), [procedure()])?.blockApplies).toBeFalse();
  });

  it('actions follow the codes: CREDIT_MANAGE holds / limits, DUNNING_ADMIN resets (548)', async () => {
    const viewer = await detail(['AR_VIEW']);
    expect(viewer.q('place-hold')).toBeNull();
    expect(viewer.q('reset-dunning')).toBeNull();
    expect(viewer.q('view-history')).not.toBeNull();
    const mgr = await detail(['AR_VIEW', 'CREDIT_MANAGE', 'DUNNING_ADMIN']);
    expect(mgr.q('place-hold')).not.toBeNull();
    expect(mgr.q('set-temp')).not.toBeNull();
    expect(mgr.q('reset-dunning')).not.toBeNull();
  });

  it('hold history table (549)', async () => {
    const m = await detail(['AR_VIEW']);
    expect(m.text('history-h1')).toContain('Hold');
    expect(m.text('history-h1')).toContain('60+ days overdue');
    expect(m.text('history-h1')).toContain('J. Smith');
    expect(m.text('history-h1')).toContain('No hold → On hold');
    // Dunning history events, newest first.
    expect(m.c.dunningHistory.map(e => e.runNumber)).toEqual(['DUN-0006', 'DUN-0002']);
    expect(m.text('dunning-history')).toContain('Notice');
  });

  it('place hold needs a reason, then posts it (550)', async () => {
    const m = await detail(['AR_VIEW', 'CREDIT_MANAGE']);
    m.c.open('hold');
    m.c.submit();
    expect(m.c.dialogError).toBe('A reason is required.');
    expect(m.api.placeHold).not.toHaveBeenCalled();
    m.c.reason = ' 60+ days overdue ';
    m.c.submit();
    expect(m.api.placeHold).toHaveBeenCalledWith('c1', '60+ days overdue');
    expect(m.c.modal).toBeNull();
    expect(m.toast).toHaveBeenCalled();
  });

  it('temporary limit: amount above the permanent, expiry after today, reason (551); clear sends null', async () => {
    const m = await detail(['AR_VIEW', 'CREDIT_MANAGE']);
    m.c.open('temp');
    m.c.tempAmount = 90000;
    m.c.tempExpiry = '2099-01-01';
    m.c.reason = 'Large order';
    m.c.submit();
    expect(m.c.dialogError).toBe('The temporary limit must be above the permanent limit (100,000.00).');
    m.c.tempAmount = 120000;
    m.c.submit();
    expect(m.api.setTemporaryLimit).toHaveBeenCalledWith('c1', { amount: 120000, expiryDate: '2099-01-01', reason: 'Large order' });
    m.c.open('clearTemp');
    m.c.reason = 'Order delivered';
    m.c.submit();
    expect(m.api.setTemporaryLimit).toHaveBeenCalledWith('c1', { amount: null, expiryDate: null, reason: 'Order delivered' });
  });

  it('a server refusal stays in the dialog', async () => {
    const m = await detail(['AR_VIEW', 'CREDIT_MANAGE'], api => api.placeHold.and.returnValue(throwError(() => ({ status: 409, error: { message: 'Acme Corp is already on credit hold.' } }))));
    m.c.open('hold');
    m.c.reason = 'x';
    m.c.submit();
    expect(m.c.dialogError).toBe('Acme Corp is already on credit hold.');
  });

  it('without a uuid: the exposures list with filters, and the mode switch for FINANCE_AR_AP_ADMIN (D-5)', async () => {
    const m = await mount(CreditControlComponent, { perms: ['AR_VIEW', 'FINANCE_AR_AP_ADMIN'], prepare: api => {
      api.getExposures.and.returnValue(of({ data: [exposure({ isOnHold: true })], totalRecords: 1, page: 1, pageSize: 25 }));
      api.getSettings.and.returnValue(of({ creditControlMode: 'Warn' }));
      api.updateSettings.and.returnValue(of({ creditControlMode: 'Block' }));
    } });
    m.c.onLazy({ first: 0, rows: 25 });
    m.fixture.detectChanges();
    expect(m.text('row-c1')).toContain('On hold');
    expect(m.c.mode).toBe('Warn');
    m.c.setFilter('hold');
    expect(m.api.getExposures).toHaveBeenCalledWith(jasmine.objectContaining({ onHold: true, overLimit: false, page: 1 }));
    m.c.mode = 'Block';
    m.c.saveMode();
    expect(m.api.updateSettings).toHaveBeenCalledWith({ creditControlMode: 'Block' });
    const viewer = await mount(CreditControlComponent, { perms: ['AR_VIEW'] });
    expect(viewer.q('mode')).toBeNull();
    expect(viewer.api.getSettings).not.toHaveBeenCalled();
  });
});
