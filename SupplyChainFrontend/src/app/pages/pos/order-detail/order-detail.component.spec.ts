import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { of, throwError } from 'rxjs';

import {
  PosOrderDetailComponent, allowedRefundMethods, estimateReturnTotal, lineRefund, originalRefundMethod, r2, restockingFee
} from './order-detail.component';
import { PosAdminService, PosOrder } from '../../../services/pos-admin.service';
import { EffectivePolicy, PosReturnsAdminService, ReturnReason } from '../../../services/pos-returns-admin.service';
import { AuthService } from '../../service/auth.service';
import { ModuleService } from '../../../services/module.service';
import { effectivePolicyFixture, fail400, lineFixture, ok, orderFixture, reasonFixture } from '../pos-fixtures.testing';

const ALL = ['POS_ORDER_VIEW', 'POS_ORDER_VOID', 'POS_ORDER_RETURN', 'SALES_INVOICE_VIEW'];
const REASONS: ReturnReason[] = [
  reasonFixture(),
  reasonFixture({ uuid: 'rr-2', code: 'OTHER', name: 'Other', requiresNote: true, sortOrder: 90 }),
  reasonFixture({ uuid: 'rr-3', code: 'PRICE_MATCH', name: 'Price match', requiresNote: true, requiresManager: true, restockingFeeOverride: 0, sortOrder: 50 }),
  reasonFixture({ uuid: 'rr-4', code: 'SWAP', name: 'Swap only', appliesTo: 'EXCHANGE', sortOrder: 5 }),
  reasonFixture({ uuid: 'rr-5', code: 'OLD', name: 'Old', isActive: false, sortOrder: 1 })
];

/** A38 FSD §17.7.4 + A39 — a POS order: lines, payments, invoice link, flags; Void and Return dialogs gated by permission. */
describe('PosOrderDetailComponent (A38 + A39)', () => {
  let fixture: ComponentFixture<PosOrderDetailComponent>;
  let component: PosOrderDetailComponent;
  let el: HTMLElement;
  let pos: jasmine.SpyObj<PosAdminService>;
  let returnsApi: jasmine.SpyObj<PosReturnsAdminService>;

  async function setup(order: PosOrder, perms = ALL, features = ['FEATURE_POS_RETURNS'],
                       opts: { reasons?: ReturnReason[] | 'fail'; policy?: EffectivePolicy | 'fail' } = {}) {
    pos = jasmine.createSpyObj<PosAdminService>('PosAdminService', ['getOrder', 'voidOrder', 'returnOrder', 'getShifts']);
    pos.getOrder.and.returnValue(ok(order));
    returnsApi = jasmine.createSpyObj<PosReturnsAdminService>('PosReturnsAdminService', ['getReasons', 'getStorePolicy']);
    returnsApi.getReasons.and.returnValue(opts.reasons === 'fail' ? throwError(() => ({ status: 403, error: {} })) : ok(opts.reasons ?? REASONS));
    returnsApi.getStorePolicy.and.returnValue(opts.policy === 'fail'
      ? fail400('No return policy configured. Contact your administrator.') : ok(opts.policy ?? effectivePolicyFixture()));
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [PosOrderDetailComponent],
      providers: [
        provideNoopAnimations(), provideRouter([]),
        { provide: ActivatedRoute, useValue: { paramMap: of(convertToParamMap({ uuid: order.uuid })) } },
        { provide: PosAdminService, useValue: pos },
        { provide: PosReturnsAdminService, useValue: returnsApi },
        { provide: ModuleService, useValue: { isFeatureEnabled: (c: string) => features.includes(c) } },
        { provide: AuthService, useValue: {
          hasPermission: (c: string) => perms.includes(c),
          hasAnyPermission: (...c: string[]) => c.some(x => perms.includes(x))
        } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(PosOrderDetailComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    el = fixture.nativeElement;
  }

  const q = (id: string) => el.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  it('renders lines, payments, flags and the sales invoice link', async () => {
    await setup(orderFixture());
    expect(pos.getOrder).toHaveBeenCalledWith('o-1');
    expect(q('order-title')!.textContent).toContain('POS-S1-20261010-0001');
    expect(q('line-table')!.textContent).toContain('Tea');
    expect(q('line-table')!.textContent).toContain('234.00');
    expect(q('payment-table')!.textContent).toContain('Card');
    expect(q('order-flags')!.textContent).toContain('Price changed');
    expect(q('invoice-link')!.getAttribute('href')).toContain('/portal/pages/finance/sales-invoices/si-1');
    expect(q('posting-state')).toBeNull();
    expect(component.sections.map(s => s.label)).toEqual(['Lines', 'Payments', 'Returns', 'Details']);
  });

  it('posting pending and a posting error are shown; no invoice link without the invoice permission', async () => {
    await setup(orderFixture({ financePosted: false, postingError: 'Product ledger refused the OUT.', salesInvoiceUuid: null,
                                salesInvoiceNumber: null }), ['POS_ORDER_VIEW']);
    expect(q('posting-state')!.textContent).toContain('Finance pending');
    expect(q('posting-error')!.textContent).toContain('Product ledger refused the OUT.');
    expect(q('invoice-link')).toBeNull();
    expect(q('invoice-ref')!.textContent).toContain('not posted yet');
  });

  it('Void: with POS_ORDER_VOID on a completed sale; reason required; same-shift refusal verbatim', async () => {
    await setup(orderFixture());
    expect(q('void-order')).not.toBeNull();
    component.openVoid();
    component.confirmVoid();
    expect(pos.voidOrder).not.toHaveBeenCalled();
    component.voidReason = ' Rang twice ';
    pos.voidOrder.and.returnValue(fail400('Orders can only be voided within the same shift.'));
    component.confirmVoid();
    expect(pos.voidOrder).toHaveBeenCalledWith('o-1', 'Rang twice');
    expect(component.voidError).toBe('Orders can only be voided within the same shift.');

    pos.voidOrder.and.returnValue(ok(orderFixture({ status: 'VOIDED', voidReason: 'Rang twice' })));
    component.confirmVoid();
    expect(component.voidDialog).toBeFalse();
    expect(component.order!.status).toBe('VOIDED');
    fixture.detectChanges();
    expect(q('void-order')).toBeNull();
  });

  it('no Void / Return without the permission, on a return order, or with returns switched off', async () => {
    await setup(orderFixture(), ['POS_ORDER_VIEW']);
    expect(q('void-order')).toBeNull();
    expect(q('return-order')).toBeNull();
    await setup(orderFixture({ orderType: 'RETURN', originalOrderUuid: 'o-0', originalOrderNumber: 'POS-S1-0' }));
    expect(q('void-order')).toBeNull();
    expect(q('return-order')).toBeNull();
    expect(q('original-link')!.textContent).toContain('POS-S1-0');
    expect(component.sections.map(s => s.label)).toEqual(['Lines', 'Refunds', 'Details']);
    await setup(orderFixture(), ALL, []);
    expect(q('return-order')).toBeNull();
    expect(q('void-order')).not.toBeNull();
  });

  it('A39 Return: reason from the list (+ note when required), restocking fee preview, one refund method; body carries both shapes', async () => {
    await setup(orderFixture(), ALL, ['FEATURE_POS_RETURNS'], { policy: effectivePolicyFixture({ restockingFeePct: 10 }) });
    expect(q('return-order')).not.toBeNull();
    component.openReturn();
    fixture.detectChanges();
    expect(returnsApi.getStorePolicy).toHaveBeenCalledWith('s-1');
    expect(component.returnLines.length).toBe(1);                // Sugar has nothing left to return
    expect(component.reasonOptions.map(o => o.label)).toEqual(['Defective', 'Price match', 'Other']);   // active, not exchange-only
    expect(component.refundMethodOptions.map(o => o.value)).not.toContain('ON_ACCOUNT');   // not a credit sale
    expect(component.refundMethodOptions.map(o => o.value)).not.toContain('STORE_CREDIT'); // FEATURE_POS_CREDIT_NOTES off
    expect(component.refundMethod).toBe('CARD');                 // the original tender
    expect(component.returnProblem).toBe('Enter a quantity to return on at least one line.');

    component.returnLines[0].quantity = 1;
    component.onQuantityChange();
    expect(component.returnTotal).toBe(117);
    expect(component.restockingFee).toBe(11.7);
    expect(component.refundAmount).toBe(105.3);
    fixture.detectChanges();
    expect(q('restocking-fee-preview')!.textContent).toContain('11.70');
    expect(q('refund-total')!.textContent).toContain('105.30');
    expect(component.returnProblem).toBe('Pick a return reason.');
    component.reasonUuid = 'rr-2';
    component.onReasonChange();
    expect(component.returnProblem).toBe('A note is required for this reason.');
    component.returnNote = ' Box crushed ';
    expect(component.returnProblem).toBeNull();

    component.returnLines[0].quantity = 3;
    expect(component.returnProblem).toContain('At most 2');
    component.returnLines[0].quantity = 1;

    pos.returnOrder.and.returnValue(fail400('Return window expired (30 days).'));
    component.refundReference = ' slip-9 ';
    component.confirmReturn();
    const [uuid, req] = pos.returnOrder.calls.mostRecent().args;
    expect(uuid).toBe('o-1');
    expect(req.lines).toEqual([{ originalLineUuid: 'ln-1', quantity: 1 }]);
    expect(req.reasonUuid).toBe('rr-2');
    expect(req.note).toBe('Box crushed');
    expect(req.reason).toBe('Other: Box crushed');
    expect(req.refundMethod).toBe('CARD');
    expect(req.refundReference).toBe('slip-9');
    expect(req.refunds).toEqual([{ method: 'CARD', amount: 105.3, reference: 'slip-9' }]);
    expect(req.localId).toBeTruthy();
    expect(component.returnError).toBe('Return window expired (30 days).');

    const nav = spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    pos.returnOrder.and.returnValue(ok(orderFixture({ uuid: 'r-1', orderNumber: 'RET-S1-20261010-0001', orderType: 'RETURN' })));
    component.confirmReturn();
    expect(component.returnDialog).toBeFalse();
    expect(nav).toHaveBeenCalledWith(['/portal/pages/pos', 'orders', 'r-1']);
  });

  it('A39: a reason override of 0 waives the fee; manager needs are hinted, and the server\'s requiresManager answer is shown', async () => {
    const order = orderFixture({ createdAtLocal: new Date(Date.now() - 10 * 86400000).toISOString() });
    await setup(order, ALL, ['FEATURE_POS_RETURNS'],
      { policy: effectivePolicyFixture({ restockingFeePct: 10, maxReturnNoAuth: 100, returnWindowDays: 7 }) });
    component.openReturn();
    component.returnAll();
    component.reasonUuid = 'rr-3';
    component.onReasonChange();
    component.returnNote = 'Competitor ad';
    expect(component.restockingFee).toBe(0);
    expect(component.refundAmount).toBe(234);
    expect(component.managerHints.length).toBe(3);   // reason, amount > 100, window 7 days passed
    fixture.detectChanges();
    expect(q('manager-hints')!.textContent).toContain('Only a user with the return override permission');

    pos.returnOrder.and.returnValue(throwError(() => ({ status: 400, error: {
      message: 'Manager authorization required.', result: { requiresManager: true, reasons: ['REASON', 'AMOUNT'] } } })));
    component.confirmReturn();
    fixture.detectChanges();
    expect(component.returnError).toBe('Manager authorization required.');
    expect(q('manager-required')!.textContent).toContain('the return reason needs a manager');
    expect(q('manager-required')!.textContent).toContain('above what the policy allows');
  });

  it('A39: the policy shapes the refund methods; store credit needs the feature and the policy', async () => {
    await setup(orderFixture(), ALL, ['FEATURE_POS_RETURNS', 'FEATURE_POS_CREDIT_NOTES'],
      { policy: effectivePolicyFixture({ allowCashRefund: false }) });
    component.openReturn();
    expect(component.refundMethodOptions.map(o => o.value)).toEqual(['CARD', 'MOBILE', 'BANK_TRANSFER', 'CHEQUE', 'STORE_CREDIT']);
    component.refundMethod = 'STORE_CREDIT';
    fixture.detectChanges();
    expect(q('store-credit-hint')!.textContent).toContain('Acme');
    expect(q('store-credit-hint')!.textContent).toContain('180 days');

    await setup(orderFixture(), ALL, ['FEATURE_POS_RETURNS', 'FEATURE_POS_CREDIT_NOTES'],
      { policy: effectivePolicyFixture({ refundToOriginalMethod: true }) });
    component.openReturn();
    expect(component.refundMethodOptions.map(o => o.value)).toEqual(['CARD']);   // POSR: original only, store credit too is refused
    fixture.detectChanges();
    expect(q('original-method-hint')).not.toBeNull();

    // Original = cash but cash refunds are off: nothing is allowed — said before sending.
    await setup(orderFixture({ payments: [] }), ALL, ['FEATURE_POS_RETURNS'],
      { policy: effectivePolicyFixture({ refundToOriginalMethod: true, allowCashRefund: false }) });
    component.openReturn();
    component.returnAll();
    component.reasonUuid = 'rr-1';
    component.onReasonChange();
    expect(component.refundMethodOptions.length).toBe(0);
    expect(component.returnProblem).toBe('The store\'s return policy allows no refund method for this sale.');
  });

  it('A39: without the reason list the A38 free-text reason is asked; a policy refusal is shown as a note', async () => {
    await setup(orderFixture({ paymentStatus: 'CREDIT_SALE', payments: [] }), ALL, ['FEATURE_POS_RETURNS'], { reasons: 'fail', policy: 'fail' });
    component.openReturn();
    fixture.detectChanges();
    expect(component.reasonsLoaded).toBeFalse();
    expect(q('return-reason')).not.toBeNull();
    expect(q('policy-note')!.textContent).toContain('No return policy configured. Contact your administrator.');
    expect(component.refundMethodOptions.map(o => o.value)).toContain('ON_ACCOUNT');
    expect(component.refundMethod).toBe('CASH');
    component.returnAll();
    expect(component.returnTotal).toBe(234);
    expect(component.refundAmount).toBe(234);
    expect(component.returnProblem).toBe('A reason is required.');
    component.returnReason = 'Damaged';
    expect(component.returnProblem).toBeNull();
    pos.returnOrder.and.returnValue(ok(null));
    component.confirmReturn();
    const req = pos.returnOrder.calls.mostRecent().args[1];
    expect(req.reason).toBe('Damaged');
    expect(req.reasonUuid).toBeUndefined();
  });

  it('a credit note links to its sales-invoice page (SCN-…)', async () => {
    await setup(orderFixture({ orderType: 'RETURN', creditNoteUuid: 'scn-1', creditNoteNumber: 'SCN-2026-00001' }));
    expect(q('credit-note-link')!.getAttribute('href')).toContain('/portal/pages/finance/sales-invoices/scn-1');
    await setup(orderFixture({ orderType: 'RETURN', creditNoteUuid: 'scn-1', creditNoteNumber: 'SCN-2026-00001' }), ['POS_ORDER_VIEW']);
    expect(q('credit-note-link')).toBeNull();
    expect(q('credit-note-ref')!.textContent).toContain('SCN-2026-00001');
  });

  it('A39 return order: reason, authorised by, restocking fee, cross-store badge and the store credit it issued', async () => {
    const ret = orderFixture({
      uuid: 'r-1', orderType: 'RETURN', status: 'COMPLETED', originalOrderUuid: 'o-0', originalOrderNumber: 'POS-S2-0001',
      returnReasonName: 'Defective', returnReason: 'Defective: screen cracked', authorizedByName: 'Omar', authorizedAt: '2026-10-10T11:00:00',
      restockingFeeAmount: 11.7, isCrossStore: true, originalStoreName: 'Airport', flags: ['CROSS_STORE'],
      storeCredit: { uuid: 'cn-1', number: 'CN-S1-20261010-0001', amount: 105.3, balance: 105.3, expiresAt: '2027-04-08T00:00:00' }
    });
    await setup(ret, [...ALL, 'POS_CREDIT_NOTE_VIEW']);
    expect(q('cross-store-badge')!.textContent).toContain('Airport');
    expect(q('order-flags')!.textContent).toContain('Cross-store');
    expect(q('order-return-reason')!.textContent).toContain('Defective');
    expect(q('order-return-reason')!.textContent).toContain('screen cracked');
    expect(q('authorized-by')!.textContent).toContain('Omar');
    expect(q('restocking-fee')!.textContent).toContain('11.70');
    expect(q('kpi-fee')!.textContent).toContain('11.70');
    expect(q('original-store')!.textContent).toContain('Airport');
    expect(q('store-credit-link')!.getAttribute('href')).toContain('/portal/pages/pos/credit-notes/cn-1');
    expect(q('store-credit-ref')!.textContent).toContain('105.30 left');

    await setup(ret);
    expect(q('store-credit-link')).toBeNull();
    expect(q('store-credit-ref')!.textContent).toContain('CN-S1-20261010-0001');
  });

  it('A39 exchange: returning and new lines apart, net settlement, Settlement tab', async () => {
    const ex = orderFixture({
      uuid: 'x-1', orderType: 'EXCHANGE', orderNumber: 'EXC-1', originalOrderUuid: 'o-0', originalOrderNumber: 'POS-S1-0',
      lines: [lineFixture({ uuid: 'xl-1', quantity: -1, lineTotal: -117, originalLineUuid: 'ln-1', productName: 'Tea S' }),
              lineFixture({ uuid: 'xl-2', quantity: 1, lineTotal: 150, productName: 'Tea L' })],
      netAmount: 33, settlement: 'PAY', restockingFeeAmount: 0
    });
    await setup(ex);
    expect(q('line-table')).toBeNull();
    expect(q('exchange-returning')!.textContent).toContain('Tea S');
    expect(q('exchange-returning')!.textContent).not.toContain('Tea L');
    expect(q('exchange-new')!.textContent).toContain('Tea L');
    expect(q('kpi-net')!.textContent).toContain('33.00');
    expect(q('kpi-net')!.textContent).toContain('Customer paid the difference');
    expect(component.sections.map(s => s.label)).toEqual(['Lines', 'Settlement', 'Details']);
    expect(el.textContent).toContain('POS exchange');
    expect(q('return-order')).toBeNull();
  });

  it('estimateReturnTotal is pro-rata of the line total', () => {
    const line = lineFixture({ quantity: 3, lineTotal: 100, returnableQuantity: 3 });
    expect(estimateReturnTotal([{ line, quantity: 1 }])).toBe(33.33);
    expect(estimateReturnTotal([{ line, quantity: 3 }])).toBe(100);
    expect(estimateReturnTotal([{ line, quantity: 0 }])).toBe(0);
  });

  it('POSB remainder rule: 10.52 returned in three parts refunds 3.51 / 3.51 / 3.50 (rounding away from zero)', () => {
    const l = (returnable: number) => lineFixture({ quantity: 3, lineTotal: 10.52, returnableQuantity: returnable });
    expect(lineRefund({ line: l(3), quantity: 1, refundedBefore: 0 })).toBe(3.51);
    expect(lineRefund({ line: l(2), quantity: 1, refundedBefore: 3.51 })).toBe(3.51);
    expect(lineRefund({ line: l(1), quantity: 1, refundedBefore: 7.02 })).toBe(3.50);
    expect(lineRefund({ line: l(2), quantity: 2, refundedBefore: 3.51 })).toBe(7.01);   // the rest in one go
    expect(r2(0.125)).toBe(0.13);
    expect(r2(-0.125)).toBe(-0.13);
  });

  it('A39 helpers: fee rounding and override, original method, allowed methods', () => {
    expect(restockingFee(10.05, 15)).toBe(1.51);
    expect(restockingFee(11.7, 15)).toBe(1.76);   // POSR's example: refund 9.94
    expect(restockingFee(100, 15, 0)).toBe(0);
    expect(restockingFee(100, 0, 5)).toBe(5);
    expect(restockingFee(100, null)).toBe(0);
    const pay = (method: any, amount: number, isRefund = false) => ({ uuid: 'p', method, amount, changeGiven: 0, isRefund, createdAt: '' });
    expect(originalRefundMethod({ payments: [pay('CASH', 500), pay('CARD', 50), pay('MOBILE', 80)] })).toBe('MOBILE');
    expect(originalRefundMethod({ payments: [pay('CASH', 500)] })).toBe('CASH');
    expect(originalRefundMethod({ payments: [pay('CARD', 50, true)] })).toBe('CASH');
    expect(originalRefundMethod({ payments: [pay('CARD', 50), pay('CARD', 50), pay('MOBILE', 80)] })).toBe('CARD');   // per method
    const o = { payments: [pay('CARD', 100)], paymentStatus: 'PAID' as const };
    expect(allowedRefundMethods(o, null, false)).toEqual(['CASH', 'CARD', 'MOBILE', 'BANK_TRANSFER', 'CHEQUE']);
    expect(allowedRefundMethods(o, effectivePolicyFixture({ allowCreditNoteRefund: false }), true)).not.toContain('STORE_CREDIT');
    expect(allowedRefundMethods(o, effectivePolicyFixture({ refundToOriginalMethod: true }), false)).toEqual(['CARD']);
  });

  it('the last units use the amounts refunded by earlier returns', async () => {
    const sale = orderFixture({ status: 'PARTIALLY_RETURNED', returns: [{ uuid: 'r-1', orderNumber: 'RET-1', grandTotal: 3.51 },
                                                                       { uuid: 'r-2', orderNumber: 'RET-2', grandTotal: 3.51 }],
      lines: [lineFixture({ quantity: 3, lineTotal: 10.52, returnableQuantity: 1, returnedQuantity: 2 })] });
    await setup(sale);
    pos.getOrder.and.callFake((uuid: string) => ok(uuid === 'o-1' ? sale : orderFixture({
      uuid, orderType: 'RETURN', lines: [lineFixture({ uuid: 'rl-' + uuid, originalLineUuid: 'ln-1', quantity: 1, lineTotal: 3.51 })] })));
    component.openReturn();
    expect(pos.getOrder).toHaveBeenCalledWith('r-1');
    expect(pos.getOrder).toHaveBeenCalledWith('r-2');
    expect(component.returnLines[0].refundedBefore).toBe(7.02);
    component.returnAll();
    expect(component.returnTotal).toBe(3.5);
    expect(component.refundAmount).toBe(3.5);
  });

  it('the return is booked to an open shift of the store (default the original terminal\'s) with that shift\'s terminal', async () => {
    await setup(orderFixture(), [...ALL, 'POS_SHIFT_VIEW']);
    pos.getShifts.and.returnValue(ok({ data: [
      { uuid: 'sh-9', terminalUuid: 't-9', terminalName: 'Till 9', shiftNumber: 'SH-9' },
      { uuid: 'sh-1', terminalUuid: 't-1', terminalName: 'Till 1', shiftNumber: 'SH-1' }
    ], totalRecords: 2 }));
    component.openReturn();
    expect(pos.getShifts).toHaveBeenCalledWith(jasmine.objectContaining({ storeUuid: 's-1', status: 'OPEN' }));
    expect(component.returnShiftUuid).toBe('sh-1');
    component.returnShiftUuid = 'sh-9';
    component.returnLines[0].quantity = 1;
    component.onQuantityChange();
    component.reasonUuid = 'rr-1';
    component.onReasonChange();
    pos.returnOrder.and.returnValue(ok(null));
    component.confirmReturn();
    const req = pos.returnOrder.calls.mostRecent().args[1];
    expect(req.shiftUuid).toBe('sh-9');
    expect(req.terminalUuid).toBe('t-9');
  });
});
