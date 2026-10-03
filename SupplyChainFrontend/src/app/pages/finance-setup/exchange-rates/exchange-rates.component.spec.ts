import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { HttpErrorResponse } from '@angular/common/http';
import { ConfirmationService, MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { ExchangeRatesComponent } from './exchange-rates.component';
import { ExchangeRateModel, FinanceSetupService } from '../../../services/finance-setup.service';
import { CurrenciesService } from '../../../services/currencies.service';
import { AuthService } from '../../service/auth.service';

function rate(overrides: Partial<ExchangeRateModel> = {}): ExchangeRateModel {
  return {
    uuid: 'r1', fromCurrencyCode: 'USD', toCurrencyCode: 'PKR', rate: 278.5, effectiveDate: '2026-10-01', source: 'MANUAL',
    notes: 'SBP closing', createdDate: '2026-10-01T08:00:00Z', ...overrides
  };
}

const RATES: ExchangeRateModel[] = [
  rate(),
  rate({ uuid: 'r2', fromCurrencyCode: 'EUR', rate: 301.25, effectiveDate: '2026-09-15', notes: null })
];

describe('ExchangeRatesComponent', () => {
  let fixture: ComponentFixture<ExchangeRatesComponent>;
  let component: ExchangeRatesComponent;
  let service: jasmine.SpyObj<FinanceSetupService>;
  let toasts: jasmine.Spy;
  let permissions: string[];

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  async function setup(canManage = true) {
    permissions = canManage ? ['FINANCE_SETUP_MANAGE'] : [];
    service = jasmine.createSpyObj<FinanceSetupService>('FinanceSetupService', [
      'getExchangeRates', 'createExchangeRate', 'updateExchangeRate', 'deleteExchangeRate', 'quoteExchangeRate'
    ]);
    service.getExchangeRates.and.returnValue(of({ success: true, message: '', result: RATES }));
    service.createExchangeRate.and.callFake(req => of({ success: true, message: 'Record created successfully.', result: rate({ ...req, uuid: 'new' }) }));
    service.updateExchangeRate.and.callFake((uuid, req) => of({ success: true, message: 'Exchange rate updated.', result: rate({ ...req, uuid }) }));
    service.deleteExchangeRate.and.returnValue(of({ success: true, message: 'Record deleted successfully.', result: null }));
    service.quoteExchangeRate.and.returnValue(of({
      success: true, message: 'The USD → PKR rate of 2026-10-01.',
      result: { fromCurrencyCode: 'USD', toCurrencyCode: 'PKR', rate: 278.5, effectiveDate: '2026-10-01', inverted: false }
    }));

    const currencies = jasmine.createSpyObj<CurrenciesService>('CurrenciesService', ['getAll']);
    currencies.getAll.and.returnValue(of({
      success: true, message: '',
      result: [
        { id: 'c1', name: 'US Dollar', code: 'USD', symbol: '$' },
        { id: 'c2', name: 'Pakistani Rupee', code: 'PKR', symbol: 'Rs' },
        { id: 'c3', name: 'Euro', code: 'eur', symbol: '€' },
        { id: 'c4', name: 'Nameless', code: null, symbol: null }
      ]
    }));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [ExchangeRatesComponent],
      providers: [
        provideNoopAnimations(),
        { provide: FinanceSetupService, useValue: service },
        { provide: CurrenciesService, useValue: currencies },
        { provide: AuthService, useValue: { hasPermission: (p: string) => permissions.includes(p) } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(ExchangeRatesComponent);
    component = fixture.componentInstance;
    toasts = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
    fixture.detectChanges();
  }

  afterEach(() => fixture?.destroy());

  it('lists the rates newest first as the server sends them, with the pair, rate, date and source', async () => {
    await setup();

    expect(service.getExchangeRates).toHaveBeenCalledOnceWith(undefined, undefined);
    const row = query('rate-r1')!;
    expect(row.textContent).toContain('USD → PKR');
    expect(row.textContent).toContain('278.5');
    expect(row.textContent).toContain('1 Oct 2026');
    expect(row.textContent).toContain('MANUAL');
    expect(row.textContent).toContain('SBP closing');
  });

  it('offers only catalog currencies that have a code, by code, upper-cased', async () => {
    await setup();
    expect(component.currencies.map(c => c.value)).toEqual(['EUR', 'PKR', 'USD']);
    expect(component.currencies[0].label).toBe('EUR — Euro');
  });

  it('asks the server again when a currency filter changes', async () => {
    await setup();
    component.filterFrom = 'USD';
    component.onFilterChange();
    expect(service.getExchangeRates).toHaveBeenCalledWith('USD', undefined);

    component.filterTo = 'PKR';
    component.onFilterChange();
    expect(service.getExchangeRates).toHaveBeenCalledWith('USD', 'PKR');

    component.clearFilters();
    expect(service.getExchangeRates).toHaveBeenCalledWith(undefined, undefined);
  });

  it('adds a rate with its date as yyyy-MM-dd from the local calendar day', async () => {
    await setup();
    component.openCreate();
    component.draft = {
      fromCurrencyCode: 'USD', toCurrencyCode: 'PKR', rate: 279.25, effectiveDate: new Date(2026, 9, 2, 23, 30), notes: '  morning  '
    };
    expect(component.problem).toBeNull();
    expect(component.draftReading).toContain('1 USD = 279.25 PKR');

    component.save();

    expect(service.createExchangeRate).toHaveBeenCalledOnceWith({
      fromCurrencyCode: 'USD', toCurrencyCode: 'PKR', rate: 279.25, effectiveDate: '2026-10-02', notes: 'morning'
    });
    expect(component.dialogVisible).toBeFalse();
    expect(service.getExchangeRates).toHaveBeenCalledTimes(2);
    expect(toasts).toHaveBeenCalledWith(jasmine.objectContaining({ severity: 'success' }));
  });

  it('does not send the same currency on both sides or a second rate for a pair and day', async () => {
    await setup();
    component.openCreate();
    component.draft = { fromCurrencyCode: 'USD', toCurrencyCode: 'USD', rate: 1, effectiveDate: new Date(2026, 9, 2), notes: '' };
    expect(component.problem).toContain('two different currencies');

    component.draft = { fromCurrencyCode: 'USD', toCurrencyCode: 'PKR', rate: 280, effectiveDate: new Date(2026, 9, 1), notes: '' };
    expect(component.problem).toContain('already a USD → PKR rate for this date');

    component.save();
    expect(service.createExchangeRate).not.toHaveBeenCalled();
  });

  it('shows the server’s conflict in the dialog', async () => {
    await setup();
    service.createExchangeRate.and.returnValue(throwError(() =>
      new HttpErrorResponse({ status: 409, error: { message: 'There is already a USD → PKR rate for 2026-10-05 (280).' } })));
    component.openCreate();
    component.draft = { fromCurrencyCode: 'USD', toCurrencyCode: 'PKR', rate: 281, effectiveDate: new Date(2026, 9, 5), notes: '' };

    component.save();

    expect(component.dialogVisible).toBeTrue();
    expect(component.saveError).toContain('already a USD → PKR rate for 2026-10-05');
  });

  it('edits a rate starting from its own values, keeping its day', async () => {
    await setup();
    component.openEdit(RATES[0]);
    expect(component.draft.effectiveDate!.getDate()).toBe(1);
    expect(component.problem).toBeNull();

    component.draft = { ...component.draft, rate: 278.75 };
    component.save();

    expect(service.updateExchangeRate).toHaveBeenCalledOnceWith('r1', {
      fromCurrencyCode: 'USD', toCurrencyCode: 'PKR', rate: 278.75, effectiveDate: '2026-10-01', notes: 'SBP closing'
    });
  });

  it('swaps the pair and turns the rate round', async () => {
    await setup();
    component.openCreate();
    component.draft = { fromCurrencyCode: 'USD', toCurrencyCode: 'PKR', rate: 250, effectiveDate: new Date(2026, 9, 1), notes: '' };

    component.swapDraft();

    expect(component.draft.fromCurrencyCode).toBe('PKR');
    expect(component.draft.toCurrencyCode).toBe('USD');
    expect(component.draft.rate).toBe(0.004);
  });

  it('asks before deleting, then removes the row', async () => {
    await setup();
    const confirm = spyOn(fixture.debugElement.injector.get(ConfirmationService), 'confirm');

    component.confirmDelete(RATES[0]);
    expect(service.deleteExchangeRate).not.toHaveBeenCalled();
    expect(confirm.calls.mostRecent().args[0].message).toContain('keep it');

    confirm.calls.mostRecent().args[0].accept!();
    expect(service.deleteExchangeRate).toHaveBeenCalledOnceWith('r1');
    expect(component.rates.map(r => r.uuid)).toEqual(['r2']);
  });

  it('checks the rate on a date and shows a direct rate', async () => {
    await setup();
    component.quoteFrom = 'USD';
    component.quoteTo = 'PKR';
    component.quoteDate = new Date(2026, 9, 15);
    component.checkQuote();
    fixture.detectChanges();

    expect(service.quoteExchangeRate).toHaveBeenCalledOnceWith('USD', 'PKR', '2026-10-15');
    const answer = query('quote-answer')!.textContent!;
    expect(answer).toContain('1 USD = 278.5 PKR');
    expect(answer).toContain('rate of 1 Oct 2026');
  });

  it('says when the quote was turned round from the opposite pair', async () => {
    await setup();
    service.quoteExchangeRate.and.returnValue(of({
      success: true, message: '',
      result: { fromCurrencyCode: 'PKR', toCurrencyCode: 'USD', rate: 0.00359066, effectiveDate: '2026-10-01', inverted: true }
    }));
    component.quoteFrom = 'PKR';
    component.quoteTo = 'USD';
    component.checkQuote();
    fixture.detectChanges();

    const answer = query('quote-answer')!.textContent!;
    expect(answer).toContain('1 PKR = 0.00359066 USD');
    expect(answer).toContain('Worked out from the USD → PKR rate');
  });

  it('says when no rate is on file, and forgets the answer when the question changes', async () => {
    await setup();
    service.quoteExchangeRate.and.returnValue(of({ success: true, message: 'No USD → EUR rate is on file for that date.', result: null }));
    component.quoteFrom = 'USD';
    component.quoteTo = 'EUR';
    component.checkQuote();
    fixture.detectChanges();

    expect(query('quote-answer')!.textContent).toContain('No USD → EUR rate is on file');

    component.quoteTo = 'PKR';
    component.onQuoteInputChange();
    fixture.detectChanges();
    expect(query('quote-answer')).toBeNull();
  });

  it('cannot check without both currencies', async () => {
    await setup();
    component.quoteFrom = 'USD';
    component.quoteTo = null;
    expect(component.canQuote).toBeFalse();
    component.checkQuote();
    expect(service.quoteExchangeRate).not.toHaveBeenCalled();
  });

  it('sends a date picked at local midnight as that same calendar day', async () => {
    await setup();
    const picked = new Date(2026, 9, 1); // what the date picker hands over: local midnight
    const localDay = `${picked.getFullYear()}-${String(picked.getMonth() + 1).padStart(2, '0')}-${String(picked.getDate()).padStart(2, '0')}`;
    component.openCreate();
    component.draft = { fromCurrencyCode: 'EUR', toCurrencyCode: 'PKR', rate: 302, effectiveDate: picked, notes: '' };

    component.save();

    expect(service.createExchangeRate.calls.mostRecent().args[0].effectiveDate).toBe(localDay);
    expect(service.createExchangeRate.calls.mostRecent().args[0].effectiveDate).toBe('2026-10-01');
  });

  it('accepts and sends a big rate with a decimal part', async () => {
    await setup();
    component.openCreate();
    component.draft = { fromCurrencyCode: 'USD', toCurrencyCode: 'EUR', rate: 9000.3, effectiveDate: new Date(2026, 9, 3), notes: '' };
    expect(component.problem).toBeNull();

    component.save();

    expect(service.createExchangeRate).toHaveBeenCalledOnceWith(jasmine.objectContaining({ rate: 9000.3 }));
  });

  it('sends the rate without float noise, refuses 0 and a ninth decimal', async () => {
    await setup();
    component.openCreate();
    component.draft = { fromCurrencyCode: 'USD', toCurrencyCode: 'PKR', rate: 0, effectiveDate: new Date(2026, 9, 3), notes: '' };
    expect(component.problem).toContain('greater than 0');
    component.draft = { ...component.draft, rate: 0.123456789 };
    expect(component.problem).toContain('8 decimals');

    // 0.57 + 1 is what the rate box's arrow key gives from 0.57: 1.5699999999999998, which the server refuses.
    component.draft = { ...component.draft, rate: 0.57 + 1 };
    expect(component.problem).toBeNull();
    component.save();

    expect(service.createExchangeRate.calls.mostRecent().args[0].rate).toBe(1.57);
  });

  it('shows the server’s 400 in the dialog', async () => {
    await setup();
    service.createExchangeRate.and.returnValue(throwError(() =>
      new HttpErrorResponse({ status: 400, error: { success: false, message: "'XYZ' is not a currency in the Lookups catalog." } })));
    component.openCreate();
    component.draft = { fromCurrencyCode: 'USD', toCurrencyCode: 'PKR', rate: 281, effectiveDate: new Date(2026, 9, 6), notes: '' };

    component.save();
    fixture.detectChanges();

    expect(query('save-error')!.textContent).toContain('is not a currency in the Lookups catalog');
  });

  it('reads the list again when a rate turns out to be deleted already', async () => {
    await setup();
    service.deleteExchangeRate.and.returnValue(throwError(() => new HttpErrorResponse({
      status: 404, error: { success: false, message: 'That exchange rate does not exist in this organization, or was deleted.' }
    })));
    service.getExchangeRates.and.returnValue(of({ success: true, message: '', result: [RATES[1]] }));

    component.delete(RATES[0]);

    expect(toasts).toHaveBeenCalledWith(jasmine.objectContaining({
      severity: 'error', detail: 'That exchange rate does not exist in this organization, or was deleted.'
    }));
    expect(component.rates.map(r => r.uuid)).toEqual(['r2']);
  });

  it('is read-only for a finance viewer: no write is reachable, the rates still load', async () => {
    await setup(false);

    expect(service.getExchangeRates).toHaveBeenCalledTimes(1);
    expect(component.rates.length).toBe(2);

    component.delete(RATES[0]);
    component.draft = { fromCurrencyCode: 'USD', toCurrencyCode: 'PKR', rate: 281, effectiveDate: new Date(2026, 9, 6), notes: '' };
    component.save();

    expect(service.deleteExchangeRate).not.toHaveBeenCalled();
    expect(service.createExchangeRate).not.toHaveBeenCalled();
    expect(service.updateExchangeRate).not.toHaveBeenCalled();
  });

  it('is read-only without the manage permission but can still check a rate', async () => {
    await setup(false);

    expect(query('read-only')).not.toBeNull();
    expect(query('new-rate')).toBeNull();
    expect(query('edit-r1')).toBeNull();
    expect(query('delete-r1')).toBeNull();
    expect(query('rate-r1')).not.toBeNull();
    expect(query('quote-check')).not.toBeNull();

    component.openCreate();
    component.openEdit(RATES[0]);
    component.confirmDelete(RATES[0]);
    expect(component.dialogVisible).toBeFalse();
  });
});
