import { HttpResponse } from '@angular/common/http';
import { Router } from '@angular/router';
import { TestBed, discardPeriodicTasks, fakeAsync, tick } from '@angular/core/testing';
import { of, throwError } from 'rxjs';

import { PaymentRunWizardComponent } from './payment-run-wizard.component';
import { mount, run, runWire } from '../ar-ap.testing';
import { PaymentRunDetail } from '../../../../services/ar-ap.service';

const ALL = ['AP_VIEW', 'PAYMENT_RUN_PROPOSE', 'PAYMENT_RUN_EDIT', 'PAYMENT_RUN_APPROVE', 'PAYMENT_RUN_EXECUTE', 'PAYMENT_RUN_ADMIN'];
const exec = (r: PaymentRunDetail, accepted = false) => of({ run: r, accepted, message: null });

function withBatches(status: string, batch: (string | null)[], extra: Partial<PaymentRunDetail> = {}): PaymentRunDetail {
  const r = run(status);
  r.vendorGroups = r.vendorGroups.map((g, i) => ({ ...g, batchStatus: batch[i] ?? 'Pending',
    paymentUuid: batch[i] === 'Paid' ? 'sp' + i : null, paymentNumber: batch[i] === 'Paid' ? 'SP-010' + i : null,
    failureReason: batch[i] === 'Failed' ? 'No bank account' : null }));
  return { ...r, ...extra };
}

describe('PaymentRunWizardComponent (A48 tasks 560–567)', () => {
  it('step 1: criteria are checked, then the proposal is made and the URL becomes the run (561)', async () => {
    const m = await mount(PaymentRunWizardComponent, { perms: ALL, prepare: api => api.proposePaymentRun.and.returnValue(of(run())) });
    const nav = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    expect(m.c.step).toBe(1);
    m.c.paymentDate = '2000-01-01';
    m.c.propose();
    expect(m.c.criteriaError).toBe('The payment date cannot be before today.');
    m.c.paymentDate = m.c.todayIso;
    m.c.cutoff = '2099-12-31';
    m.c.minPriority = 'Normal';
    m.c.method = 'BANK_TRANSFER';
    m.c.bankAccount = ' Operating ';
    m.c.propose();
    expect(m.api.proposePaymentRun).toHaveBeenCalledWith(jasmine.objectContaining({
      dueDateCutoff: '2099-12-31', minPriority: 'Normal', paymentMethod: 'BANK_TRANSFER', bankAccount: 'Operating', includeDiscountItems: true, vendorUuid: null
    }));
    expect(m.c.step).toBe(2);
    expect(nav).toHaveBeenCalledWith(['/portal/pages/finance/ap/payment-runs', 'r1'], { replaceUrl: true });
  });

  it('step 2: batches as vendor groups, discount / overdue markers, blocked schedules apart, live totals (562, 564)', async () => {
    const m = await mount(PaymentRunWizardComponent, { perms: ALL, params: { uuid: 'r1' }, prepare: api => api.getPaymentRun.and.returnValue(of(run())) });
    expect(m.c.step).toBe(2);
    expect(m.text('t-vendors')).toBe('2');
    expect(m.text('t-net')).toBe('17,898.00');
    expect(m.text('t-discount')).toBe('102.00');
    expect(m.text('group-v1')).toContain('Supplier A');
    expect(m.q('disc-a2')).not.toBeNull();
    expect(m.text('late-b1')).toContain('3d late');
    expect(m.text('blocked')).toContain('Supplier C');
    expect(m.text('blocked')).toContain('Disputed');
  });

  it('remove needs a reason; the removed item is listed apart and restored from there (563)', async () => {
    const removed = run('Proposed', {
      vendorGroups: runWire().vendorGroups.slice(0, 1),
      removedItems: [{ ...runWire().vendorGroups[1].items[0], status: 'Removed', removedReason: 'Pay next week' }]
    });
    const m = await mount(PaymentRunWizardComponent, { perms: ALL, params: { uuid: 'r1' }, prepare: api => {
      api.getPaymentRun.and.returnValue(of(run()));
      api.removePaymentRunItem.and.returnValue(of(removed));
      api.restorePaymentRunItem.and.returnValue(of(run()));
    } });
    m.c.openRemove(m.c.run!.vendorGroups[1].items[0]);
    m.c.submitReason();
    expect(m.c.dialogError).toBe('A removal needs a reason.');
    m.c.reason = 'Pay next week';
    m.c.submitReason();
    m.fixture.detectChanges();
    expect(m.api.removePaymentRunItem).toHaveBeenCalledWith('r1', 'b1', 'Pay next week');
    expect(m.c.totals.vendors).toBe(1);
    expect(m.text('removed-b1')).toContain('Pay next week');
    m.q('restore-b1')!.querySelector('button')!.click();
    expect(m.api.restorePaymentRunItem).toHaveBeenCalledWith('r1', 'b1');
    expect(m.c.totals.vendors).toBe(2);
    const viewer = await mount(PaymentRunWizardComponent, { perms: ['AP_VIEW'], params: { uuid: 'r1' }, prepare: api => api.getPaymentRun.and.returnValue(of(run())) });
    expect(viewer.q('remove-a1')).toBeNull();
    expect(viewer.q('export')).toBeNull();
  });

  it('the discount-account warning shows on the run (D-AP-12); the approval refusal is shown', async () => {
    const warned = run('Proposed', { warnings: ['The Discount received account is not assigned; assign it under GL Defaults before approving.'] });
    const m = await mount(PaymentRunWizardComponent, { perms: ALL, params: { uuid: 'r1' }, prepare: api => {
      api.getPaymentRun.and.returnValue(of(warned));
      api.approvePaymentRun.and.returnValue(throwError(() => ({ status: 409, error: { message: 'The Discount received account is not assigned.' } })));
    } });
    expect(m.text('warnings')).toContain('Discount received account');
    m.c.approve();
    m.fixture.detectChanges();
    expect(m.text('error')).toContain('not assigned');
  });

  it('step 3 approve → step 4 execute (200) → summary with the payments (565–566)', async () => {
    const m = await mount(PaymentRunWizardComponent, { perms: ALL, params: { uuid: 'r1' }, prepare: api => {
      api.getPaymentRun.and.returnValue(of(run()));
      api.approvePaymentRun.and.returnValue(of(run('Approved')));
      api.executePaymentRun.and.returnValue(exec(withBatches('Completed', ['Paid', 'Failed'])));
    } });
    m.c.go(3);
    m.fixture.detectChanges();
    m.c.confirmApprove();
    expect(m.api.approvePaymentRun).toHaveBeenCalledWith('r1');
    expect(m.c.step).toBe(4);
    m.c.confirmExecute();
    m.fixture.detectChanges();
    expect(m.api.executePaymentRun).toHaveBeenCalledWith('r1');
    expect(m.text('result')).toContain('1 payment made, 1 failed');
    expect(m.text('batches')).toContain('SP-0100');
    expect(m.text('batches')).toContain('No bank account');
  });

  it('REV: the completed run shows what was paid (server totals) and the items settled meanwhile', async () => {
    const done = withBatches('Completed', ['Paid', 'Paid']);
    done.netPaymentAmount = 13198;
    done.totalItems = 2;
    done.totalDiscount = 0;
    done.removedItems = [{ ...done.vendorGroups[1].items[0], status: 'Removed', removedReason: 'Settled before the run was executed', vendorName: '' }];
    const m = await mount(PaymentRunWizardComponent, { perms: ALL, params: { uuid: 'r1' }, prepare: api => api.getPaymentRun.and.returnValue(of(done)) });
    expect(m.text('paid-totals')).toContain('Paid 13,198.00 PKR for 2 invoices');
    expect(m.text('settled')).toContain('INV-b1');
    expect(m.text('settled')).toContain('Settled before the run was executed');
  });

  it('202: executes in the background — polls GET, shows batches as they finish, stops when Completed', fakeAsync(() => {
    let m: any;
    mount(PaymentRunWizardComponent, { perms: ALL, params: { uuid: 'r1' }, prepare: api => {
      api.getPaymentRun.and.returnValue(of(run('Approved')));
      api.executePaymentRun.and.returnValue(exec(withBatches('Executing', [null, null]), true));
    } }).then(x => m = x);
    tick();
    m.api.getPaymentRun.and.returnValues(of(withBatches('Executing', ['Paid', null])), of(withBatches('Completed', ['Paid', 'Paid'])));
    m.c.execute();
    m.fixture.detectChanges();
    expect(m.text('progress')).toContain('0 of 2 done');
    tick(3000);
    m.fixture.detectChanges();
    expect(m.text('progress')).toContain('1 of 2 done');
    expect(m.text('live-batches')).toContain('SP-0100');
    tick(3000);
    m.fixture.detectChanges();
    expect(m.c.run.status).toBe('Completed');
    expect(m.text('result')).toContain('2 payments made');
    const calls = m.api.getPaymentRun.calls.count();
    tick(6000);
    expect(m.api.getPaymentRun.calls.count()).withContext('polling stopped').toBe(calls);
    discardPeriodicTasks();
  }));

  it('a run left Executing: stuck help after unchanged polls, Resume (202 → poll), Cancel while nothing is paid', fakeAsync(() => {
    let m: any;
    const stuck = withBatches('Executing', [null, null]);
    mount(PaymentRunWizardComponent, { perms: ALL, params: { uuid: 'r1' }, prepare: api => {
      api.getPaymentRun.and.returnValue(of(stuck));
      api.resumePaymentRun.and.returnValue(exec(withBatches('Completed', ['Paid', 'Paid'])));
    } }).then(x => m = x);
    tick();
    m.fixture.detectChanges();
    expect(m.c.step).toBe(4);
    expect(m.q('resume')).not.toBeNull();
    expect(m.c.cancellable).withContext('nothing paid yet').toBeTrue();
    tick(12000);
    m.fixture.detectChanges();
    expect(m.text('stalled')).toContain('Resume carries on');
    m.c.confirmResume();
    m.fixture.detectChanges();
    expect(m.api.resumePaymentRun).toHaveBeenCalledWith('r1');
    expect(m.c.run.status).toBe('Completed');
    discardPeriodicTasks();
    m.c.ngOnDestroy();
  }));

  it('review: polling stops when the page goes (no restart from an in-flight read)', fakeAsync(() => {
    let m: any;
    mount(PaymentRunWizardComponent, { perms: ALL, params: { uuid: 'r1' }, prepare: api => api.getPaymentRun.and.returnValue(of(withBatches('Executing', [null, null]))) })
      .then(x => m = x);
    tick();
    tick(3000);
    const calls = m.api.getPaymentRun.calls.count();
    m.fixture.destroy();
    tick(12000);
    expect(m.api.getPaymentRun.calls.count()).toBe(calls);
  }));

  it('review: a failed poll stops polling and shows the error; completion is announced once', fakeAsync(() => {
    let m: any;
    mount(PaymentRunWizardComponent, { perms: ALL, params: { uuid: 'r1' }, prepare: api => api.getPaymentRun.and.returnValue(of(withBatches('Executing', [null, null]))) })
      .then(x => m = x);
    tick();
    m.api.getPaymentRun.and.returnValue(throwError(() => ({ status: 500 })));
    tick(3000);
    m.fixture.detectChanges();
    expect(m.c.error).toContain('could not be read');
    const calls = m.api.getPaymentRun.calls.count();
    tick(9000);
    expect(m.api.getPaymentRun.calls.count()).withContext('stopped on error').toBe(calls);

    // A new execution, completing by the poll: one toast.
    m.api.executePaymentRun.and.returnValue(exec(withBatches('Executing', [null, null]), true));
    m.api.getPaymentRun.and.returnValue(of(withBatches('Completed', ['Paid', 'Paid'])));
    m.c.run = withBatches('Approved', [null, null]);
    m.c.execute();
    tick(3000);
    tick(3000);
    const done = m.toast.calls.all().filter((c: any) => c.args[0].detail === 'PR-0042 is executed.');
    expect(done.length).toBe(1);
    discardPeriodicTasks();
  }));

  it('review: Export CSV only for Approved / Executing / Completed runs; a refused export shows the server text', async () => {
    const proposed = await mount(PaymentRunWizardComponent, { perms: ALL, params: { uuid: 'r1' }, prepare: api => api.getPaymentRun.and.returnValue(of(run('Proposed'))) });
    expect(proposed.q('export')).toBeNull();
    const m = await mount(PaymentRunWizardComponent, { perms: ALL, params: { uuid: 'r1' }, prepare: api => {
      api.getPaymentRun.and.returnValue(of(run('Approved')));
      api.exportPaymentRun.and.returnValue(throwError(() => ({ status: 409,
        error: new Blob([JSON.stringify({ success: false, message: 'Payment run PR-0042 has no payable batch.' })], { type: 'application/json' }) })));
    } });
    expect(m.q('export')).not.toBeNull();
    m.c.exportCsv();
    // Reading the refusal out of the Blob takes as long as it takes: poll, do not guess a delay.
    for (let i = 0; i < 200 && !m.toast.calls.count(); i++) await new Promise(r => setTimeout(r, 10));
    expect(m.toast).toHaveBeenCalledWith(jasmine.objectContaining({ severity: 'error', detail: 'Payment run PR-0042 has no payable batch.' }));
  });

  it('an Executing run with a paid batch cannot be cancelled', async () => {
    const m = await mount(PaymentRunWizardComponent, { perms: ALL, params: { uuid: 'r1' }, prepare: api => api.getPaymentRun.and.returnValue(of(withBatches('Executing', ['Paid', null]))) });
    expect(m.c.cancellable).toBeFalse();
    m.c.ngOnDestroy();
  });

  it('CSV export (567) and cancel with a reason (PAYMENT_RUN_ADMIN)', async () => {
    const m = await mount(PaymentRunWizardComponent, { perms: ALL, params: { uuid: 'r1' }, prepare: api => {
      api.getPaymentRun.and.returnValue(of(run()));
      api.exportPaymentRun.and.returnValue(of(new HttpResponse({ body: new Blob(['a,b']) })));
      api.cancelPaymentRun.and.returnValue(of(run('Cancelled', { cancelReason: 'Not needed' })));
    } });
    spyOn(URL, 'createObjectURL').and.returnValue('blob:x');
    spyOn(URL, 'revokeObjectURL');
    m.c.exportCsv();
    expect(m.api.exportPaymentRun).toHaveBeenCalledWith('r1');
    m.c.openCancel();
    m.c.reason = 'Not needed';
    m.c.submitReason();
    m.fixture.detectChanges();
    expect(m.api.cancelPaymentRun).toHaveBeenCalledWith('r1', 'Not needed');
    expect(m.text('cancelled')).toContain('Not needed');
  });

  it('an execute failure is shown and the run reloaded', async () => {
    const m = await mount(PaymentRunWizardComponent, { perms: ALL, params: { uuid: 'r1' }, prepare: api => {
      api.getPaymentRun.and.returnValue(of(run('Approved')));
      api.executePaymentRun.and.returnValue(throwError(() => ({ status: 409, error: { message: 'Only an approved payment run can be executed.' } })));
    } });
    expect(m.c.step).toBe(3);
    m.c.execute();
    expect(m.c.error).toContain('Only an approved');
    expect(m.api.getPaymentRun).toHaveBeenCalledTimes(2);
  });
});
