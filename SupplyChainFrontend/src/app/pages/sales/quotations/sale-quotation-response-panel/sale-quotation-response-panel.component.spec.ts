import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { Subject } from 'rxjs';

import { SaleQuotationResponsePanelComponent } from './sale-quotation-response-panel.component';
import { SalesPreorderService, SaleQuotation } from '../../../../services/sales-preorder.service';
import { AuthService } from '../../../service/auth.service';
import { ok, fail, quotation, qLine, wireframeLines } from '../sale-quotation.fixtures.spec';

const SENT_ACTIONS = ['RECORD_RESPONSE', 'ACCEPT', 'REJECT', 'COPY'] as const;

function sent(overrides: Partial<SaleQuotation> = {}): SaleQuotation {
  return quotation({ status: 'SENT', isEditable: false, sentAt: '2026-10-04T09:15:00Z', allowedActions: [...SENT_ACTIONS], ...overrides });
}

describe('SaleQuotationResponsePanelComponent (A32-PC-13)', () => {
  let fixture: ComponentFixture<SaleQuotationResponsePanelComponent>;
  let component: SaleQuotationResponsePanelComponent;
  let service: jasmine.SpyObj<SalesPreorderService>;
  let changed: jasmine.Spy;
  let permissions: string[];

  const auth = { hasPermission: (c: string) => permissions.includes(c) } as unknown as AuthService;

  async function setup(q: SaleQuotation = sent()) {
    service = jasmine.createSpyObj<SalesPreorderService>('SalesPreorderService', ['recordCustomerResponse', 'acceptQuotation', 'rejectQuotation']);
    service.recordCustomerResponse.and.returnValue(ok(null));
    service.acceptQuotation.and.returnValue(ok(null));
    service.rejectQuotation.and.returnValue(ok(null));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [SaleQuotationResponsePanelComponent],
      providers: [
        provideNoopAnimations(), MessageService,
        { provide: SalesPreorderService, useValue: service },
        { provide: AuthService, useValue: auth }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(SaleQuotationResponsePanelComponent);
    component = fixture.componentInstance;
    fixture.componentRef.setInput('quotation', q);
    changed = jasmine.createSpy('changed');
    component.changed.subscribe(changed);
    fixture.detectChanges();
  }

  const row = (uuid: string) => component.rows.find(r => r.line.uuid === uuid)!;
  const el = (testId: string): HTMLButtonElement | null => fixture.nativeElement.querySelector(`[data-testid="${testId}"] button, button[data-testid="${testId}"]`);

  beforeEach(() => { permissions = ['SALE_QUOTATION_VIEW', 'SALE_QUOTATION_EDIT']; });

  it('asks for a response on every line but the ones the seller rejected, labelled as in the lines table', async () => {
    await setup();
    expect(component.rows.map(r => r.label)).toEqual(['1', '2', '3a', '3b', '5']);
    expect(component.responseOptions.map(o => o.value)).toEqual(['ACCEPTED', 'REJECTED', 'COUNTER']);
    expect(fixture.nativeElement.querySelectorAll('[data-testid="response"]').length).toBe(5);
  });

  it('starts each line on the response already recorded', async () => {
    const lines = wireframeLines();
    lines[0].customerResponse = 'ACCEPTED';
    lines[4].customerResponse = 'COUNTER';
    lines[4].customerCounterPrice = 0.1;
    lines[4].customerResponseNotes = 'Bulk discount expected';
    await setup(sent({ lines }));
    expect(row('l-1').response).toBe('ACCEPTED');
    expect(row('l-5').response).toBe('COUNTER');
    expect(row('l-5').counterPrice).toBe(0.1);
    expect(row('l-5').notes).toBe('Bulk discount expected');
    expect(row('l-2').response).toBeNull();
    expect(component.dirtyRows.length).toBe(0);
  });

  // ── COUNTER ────────────────────────────────────────────────────────────────

  it('asks a COUNTER for the customer\'s price, above zero, and notes', async () => {
    await setup();
    row('l-5').response = 'COUNTER';
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[data-testid="counter-price"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="response-notes"]')).not.toBeNull();

    expect(component.rowError(row('l-5'))).toContain('counter price');
    row('l-5').counterPrice = 0;
    expect(component.rowError(row('l-5'))).toContain('counter price');
    expect(component.canSave).toBeFalse();

    component.saveResponses();
    expect(service.recordCustomerResponse).not.toHaveBeenCalled();

    row('l-5').counterPrice = 0.1;
    row('l-5').notes = 'Bulk discount expected for 10K+ qty';
    expect(component.rowError(row('l-5'))).toBeNull();
    component.saveResponses();
    expect(service.recordCustomerResponse).toHaveBeenCalledOnceWith('sq-1', 'l-5', jasmine.objectContaining({
      response: 'COUNTER', counterPrice: 0.1, notes: 'Bulk discount expected for 10K+ qty'
    }));
  });

  it('lets the seller take a countered price when the line is then accepted', async () => {
    const lines = wireframeLines();
    lines[4].customerResponse = 'COUNTER';
    lines[4].customerCounterPrice = 0.1;
    await setup(sent({ lines }));

    row('l-5').response = 'ACCEPTED';
    expect(component.offersCounterAcceptance(row('l-5'))).toBeTrue();
    row('l-5').acceptCounterPrice = true;
    component.saveResponses();
    expect(service.recordCustomerResponse).toHaveBeenCalledOnceWith('sq-1', 'l-5', jasmine.objectContaining({
      response: 'ACCEPTED', acceptCounterPrice: true
    }));
  });

  // ── Save Responses ─────────────────────────────────────────────────────────

  it('saves only the lines whose response changed, one after another, then reports the change', async () => {
    await setup();
    row('l-1').response = 'ACCEPTED';
    row('l-4').response = 'REJECTED';
    expect(component.dirtyRows.map(r => r.line.uuid)).toEqual(['l-1', 'l-4']);

    component.saveResponses();
    expect(service.recordCustomerResponse.calls.allArgs().map(a => [a[1], a[2].response])).toEqual([['l-1', 'ACCEPTED'], ['l-4', 'REJECTED']]);
    expect(changed).toHaveBeenCalledTimes(1);
  });

  it('sends the second response only after the first is in', async () => {
    await setup();
    const first = new Subject<any>();
    service.recordCustomerResponse.and.returnValues(first.asObservable(), ok(null));
    row('l-1').response = 'ACCEPTED';
    row('l-2').response = 'ACCEPTED';
    component.saveResponses();
    expect(service.recordCustomerResponse).toHaveBeenCalledTimes(1);
    first.next({ success: true, message: '', result: null });
    first.complete();
    expect(service.recordCustomerResponse).toHaveBeenCalledTimes(2);
  });

  it('stops at a refused line and says which and why', async () => {
    await setup();
    service.recordCustomerResponse.and.returnValues(fail(400, 'Counter price must be above zero.'), ok(null));
    row('l-1').response = 'ACCEPTED';
    row('l-2').response = 'ACCEPTED';
    component.saveResponses();
    expect(service.recordCustomerResponse).toHaveBeenCalledTimes(1);
    expect(component.error).toBe('Line 1: Counter price must be above zero.');
    expect(changed).not.toHaveBeenCalled();
  });

  // ── Mark accepted / rejected ───────────────────────────────────────────────

  it('enables "Mark Quotation Accepted" once at least one line is accepted', async () => {
    await setup();
    expect(component.canMarkAccepted).toBeFalse();
    row('l-4').response = 'ACCEPTED';
    expect(component.canMarkAccepted).toBeTrue();
    fixture.detectChanges();
    expect(el('mark-accepted')!.disabled).toBeFalse();
  });

  it('saves pending responses before accepting the quotation', async () => {
    await setup();
    row('l-1').response = 'ACCEPTED';
    component.markAccepted();
    expect(service.recordCustomerResponse).toHaveBeenCalledTimes(1);
    expect(service.acceptQuotation).toHaveBeenCalledOnceWith('sq-1');
    expect(changed).toHaveBeenCalledTimes(1);
  });

  it('enables "Mark Quotation Rejected" only when every line the seller offered is rejected', async () => {
    await setup();
    expect(component.canMarkRejected).toBeFalse();
    for (const id of ['l-1', 'l-2', 'l-4', 'l-6']) row(id).response = 'REJECTED';
    expect(component.canMarkRejected).withContext('line 5 still open').toBeFalse();
    row('l-5').response = 'REJECTED';
    expect(component.canMarkRejected).withContext('the seller\'s own REJECTED line 3 does not count').toBeTrue();
  });

  it('rejects the quotation with an optional reason', async () => {
    const lines = wireframeLines().map(l => l.lineType === 'REJECTED' ? l : { ...l, customerResponse: 'REJECTED' as const });
    await setup(sent({ lines }));
    component.openReject();
    expect(component.rejectDialogVisible).toBeTrue();
    component.rejectReason = '  Went with another supplier ';
    component.confirmReject();
    expect(service.recordCustomerResponse).not.toHaveBeenCalled();
    expect(service.rejectQuotation).toHaveBeenCalledOnceWith('sq-1', 'Went with another supplier');
    expect(changed).toHaveBeenCalledTimes(1);
  });

  it('offers accept and reject only as far as the server allows them', async () => {
    await setup(sent({ allowedActions: ['RECORD_RESPONSE'] }));
    expect(el('mark-accepted')).toBeNull();
    expect(el('mark-rejected')).toBeNull();
    expect(el('save-responses')).not.toBeNull();
  });

  it('is read-only for someone who may not edit', async () => {
    permissions = ['SALE_QUOTATION_VIEW'];
    await setup();
    expect(component.canRespond).toBeFalse();
    expect(el('save-responses')).toBeNull();
    row('l-1').response = 'ACCEPTED';
    component.saveResponses();
    component.markAccepted();
    expect(service.recordCustomerResponse).not.toHaveBeenCalled();
    expect(service.acceptQuotation).not.toHaveBeenCalled();
  });

  it('resets its rows when the quotation is reloaded', async () => {
    await setup();
    row('l-1').response = 'ACCEPTED';
    const reloaded = sent({ lines: [qLine({ customerResponse: 'ACCEPTED' })] });
    fixture.componentRef.setInput('quotation', reloaded);
    fixture.detectChanges();
    expect(component.rows.length).toBe(1);
    expect(component.dirtyRows.length).toBe(0);
  });
});
