import { of, throwError } from 'rxjs';

import { DunningRunsComponent } from './dunning-runs.component';
import { dunningItem, dunningRun, mount, page } from '../ar-ap.testing';
import { DunningRunDetail } from '../../../../services/ar-ap.service';

const draft = (over: Partial<DunningRunDetail> = {}): DunningRunDetail => dunningRun({
  totalCustomers: 2, totalAmount: 9000,
  items: [
    dunningItem('i1', { customerUuid: 'c1', customerName: 'Acme', dunningLevel: 2, previousLevel: 1, overdueAmount: 5000, openItemCount: 3, procedureCode: 'STANDARD' }),
    dunningItem('i2', { customerUuid: 'c2', customerName: 'Delta', dunningLevel: 4, previousLevel: 3, overdueAmount: 4000, openItemCount: 2, actionType: 'Manual',
      currencySummary: 'PKR 2,000.00; USD 7.00' })
  ], ...over
});

describe('DunningRunsComponent (A48 task 580)', () => {
  const setup = (perms: string[], prep: (api: any) => void = () => {}) => mount(DunningRunsComponent, { perms, prepare: api => {
    api.getDunningRuns.and.returnValue(page([dunningRun({ uuid: 'r0', runNumber: 'DUN-0006', runDate: '2026-10-18', status: 'Completed', totalCustomers: 3, totalAmount: 100, itemsSent: 3 })]));
    api.previewDunningRun.and.returnValue(of(draft()));
    api.executeDunningRun.and.returnValue(of(draft({ status: 'Completed', itemsSent: 1, itemsSkipped: 0, items: draft().items.map(i => ({ ...i, status: i.actionType === 'Email' ? 'Sent' : 'Manual' })) })));
    api.skipDunningItem.and.returnValue(of(draft({ items: [{ ...draft().items[0], status: 'Skipped', skipReason: 'Disputed' }, draft().items[1]] })));
    api.cancelDunningRun.and.returnValue(of(draft({ status: 'Cancelled' })));
    prep(api);
  } });

  it('history lists past runs; preview needs DUNNING_EXECUTE', async () => {
    const viewer = await setup(['AR_VIEW']);
    expect(viewer.text('run-r0')).toContain('DUN-0006');
    expect(viewer.q('preview')).toBeNull();
    const m = await setup(['DUNNING_EXECUTE']);
    expect(m.q('preview')).not.toBeNull();
  });

  it('preview → items with levels and markers → execute → results', async () => {
    const m = await setup(['DUNNING_EXECUTE', 'AR_VIEW']);
    m.c.asOfDate = '2026-10-25';
    m.c.preview();
    m.fixture.detectChanges();
    expect(m.api.previewDunningRun).toHaveBeenCalledWith('2026-10-25');
    expect(m.text('run-status')).toBe('Draft');
    expect(m.text('item-i1')).toContain('1 → 2');
    expect(m.text('item-i1')).toContain('STANDARD');
    expect(m.text('item-i2')).toContain('USD 7.00');
    expect(m.q('draft-note')).not.toBeNull();
    m.q('execute')!.querySelector('button')!.click();
    m.fixture.detectChanges();
    expect(m.confirm).toHaveBeenCalled();
    expect(m.api.executeDunningRun).toHaveBeenCalledWith('r1');
    expect(m.text('run-status')).toBe('Completed');
    expect(m.text('item-i1')).toContain('Sent');
  });

  it('skip an item with a reason; cancel a draft run', async () => {
    const m = await setup(['DUNNING_EXECUTE']);
    m.c.preview();
    m.c.openSkip(m.c.run!.items[0]);
    m.c.skip();
    expect(m.c.dialogError).toBe('A reason is required.');
    m.c.reason = 'Disputed';
    m.c.skip();
    expect(m.api.skipDunningItem).toHaveBeenCalledWith('r1', 'i1', 'Disputed');
    expect(m.c.run!.items[0].status).toBe('Skipped');
    m.c.confirmCancel();
    expect(m.api.cancelDunningRun).toHaveBeenCalledWith('r1');
    expect(m.c.run!.status).toBe('Cancelled');
  });

  it('REV-10: a run stuck in Processing ≥ 15 min can be resumed or cancelled; failed items show the sender\'s error', async () => {
    const old = new Date(Date.now() - 20 * 60 * 1000).toISOString();
    const stuck = draft({ status: 'Processing', executedAt: old,
      items: [dunningItem('i1', { status: 'Failed', failureReason: 'SMTP 550 mailbox unavailable' }), dunningItem('i2')] });
    const m = await setup(['DUNNING_EXECUTE'], api => {
      api.getDunningRun.and.returnValue(of(stuck));
      api.executeDunningRun.and.returnValue(of(draft({ status: 'Completed' })));
    });
    m.c.openRun({ ...stuck });
    m.fixture.detectChanges();
    expect(m.c.stuck).toBeTrue();
    expect(m.q('stuck-note')).not.toBeNull();
    expect(m.q('cancel-run')).not.toBeNull();
    expect(m.text('item-i1')).toContain('SMTP 550 mailbox unavailable');
    m.q('resume')!.querySelector('button')!.click();
    expect(m.api.executeDunningRun).toHaveBeenCalledWith('r1');
    const fresh = await setup(['DUNNING_EXECUTE'], api => api.getDunningRun.and.returnValue(of(draft({ status: 'Processing', executedAt: new Date().toISOString() }))));
    fresh.c.openRun(draft());
    fresh.fixture.detectChanges();
    expect(fresh.c.stuck).toBeFalse();
    expect(fresh.q('resume')).toBeNull();
  });

  it('shows the one-run-a-day refusal (BR-DN-010)', async () => {
    const m = await setup(['DUNNING_EXECUTE'], api =>
      api.previewDunningRun.and.returnValue(throwError(() => ({ status: 409, error: { message: 'A dunning run already exists for 2026-10-25 (DUN-0007).' } }))));
    m.c.preview();
    m.fixture.detectChanges();
    expect(m.text('error')).toContain('already exists');
  });
});
