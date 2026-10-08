import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { HttpErrorResponse } from '@angular/common/http';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { ExchangeRatesComponent } from './exchange-rates.component';
import { CurrencyRateModel, OrgCurrencyModel, OrgCurrencyService } from '../../../services/org-currency.service';
import { AuthService } from '../../service/auth.service';

function rate(o: Partial<CurrencyRateModel>): CurrencyRateModel {
  return {
    id: 'r', currencyId: 'usd', currencyCode: 'USD', currencyName: 'US Dollar', rate: 278.05, inverseRate: 0.0035964754,
    effectiveFrom: '2026-10-07', effectiveTo: '9999-12-31', isCurrent: true, source: 'MANUAL', notes: null,
    rateCurrencyId: 'pkr', rateCurrencyCode: 'PKR', ...o
  };
}

function cur(code: string, o: Partial<OrgCurrencyModel> = {}): OrgCurrencyModel {
  return {
    currencyId: code.toLowerCase(), code, name: code + ' name', symbol: code, decimalPlaces: 2, rounding: 0.01, symbolPosition: 'before',
    isActive: true, displayOrder: 1, baseFor: [], isRateCurrency: false, ...o
  };
}

const RATES: CurrencyRateModel[] = [
  rate({ id: 'eur1', currencyId: 'eur', currencyCode: 'EUR', currencyName: 'Euro', rate: 316.48 }),
  rate({ id: 'usd-old', rate: 277.92, effectiveFrom: '2026-10-06', effectiveTo: '2026-10-06', isCurrent: false }),
  rate({ id: 'usd-cur' }),
  rate({ id: 'pkr', currencyId: 'pkr', currencyCode: 'PKR', currencyName: 'Pakistani Rupee', rate: 1, inverseRate: 1, effectiveFrom: '2000-01-01', source: 'SYSTEM' })
];

const CURRENCIES = [cur('PKR', { isRateCurrency: true, baseFor: ['SALE'] }), cur('USD'), cur('EUR'), cur('INR', { isActive: false })];

describe('ExchangeRatesComponent (A35-P1-11/12, §11.3/§11.4)', () => {
  let fixture: ComponentFixture<ExchangeRatesComponent>;
  let component: ExchangeRatesComponent;
  let service: jasmine.SpyObj<OrgCurrencyService>;
  let toasts: jasmine.Spy;
  let permissions: string[];

  const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  async function setup(canManage = true) {
    permissions = canManage ? ['CURRENCY_RATE_MANAGE'] : ['CURRENCY_RATE_VIEW'];
    service = jasmine.createSpyObj<OrgCurrencyService>('OrgCurrencyService', [
      'getCurrencies', 'getRates', 'getRateHistory', 'getRateOn', 'createRate', 'updateRate'
    ]);
    service.getCurrencies.and.returnValue(of({ success: true, message: '', result: CURRENCIES }));
    service.getRates.and.returnValue(of({ success: true, message: '', result: RATES }));
    service.getRateHistory.and.callFake(id => of({ success: true, message: '', result: RATES.filter(r => r.currencyId === id) }));
    service.getRateOn.and.returnValue(of({ success: true, message: '', result: RATES[1] }));
    service.createRate.and.callFake(req => of({ success: true, message: '', result: {
      rate: rate({ ...req, id: 'new', effectiveTo: '9999-12-31' } as any),
      closedPrevious: rate({ id: 'usd-cur', effectiveTo: '2026-10-07', isCurrent: false })
    } }));
    service.updateRate.and.callFake((id, req) => of({ success: true, message: '', result: rate({ ...req, id } as any) }));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [ExchangeRatesComponent],
      providers: [
        provideNoopAnimations(),
        { provide: OrgCurrencyService, useValue: service },
        { provide: AuthService, useValue: { hasPermission: (p: string) => permissions.includes(p) } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(ExchangeRatesComponent);
    component = fixture.componentInstance;
    toasts = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
    fixture.detectChanges();
  }

  afterEach(() => fixture?.destroy());

  it('lists rates by code, newest first, current ones marked, the rate column named after the rate currency', async () => {
    await setup();
    expect(component.rates.map(r => r.id)).toEqual(['eur1', 'pkr', 'usd-cur', 'usd-old']);
    expect(component.rateCurrencyCode).toBe('PKR');
    expect(q('rate-col')!.textContent).toContain('Rate (PKR)');
    expect(q('rate-usd-cur')!.textContent).toContain('278.0500');
    expect(q('current-usd-cur')).not.toBeNull();
    expect(q('current-usd-old')).toBeNull();
    expect(q('rate-usd-old')!.textContent).toContain('6 Oct 2026');
    // The rate currency's SYSTEM row cannot be edited (BR-C2-04).
    expect(q('edit-usd-cur')).not.toBeNull();
    expect(q('edit-pkr')).toBeNull();
  });

  it('filters by currency and by month (sent to the server as a date range)', async () => {
    await setup();
    component.filterCurrencyId = 'usd';
    component.filterMonth = new Date(2026, 9, 15);
    component.onFilterChange();
    expect(service.getRates).toHaveBeenCalledWith({ currencyId: 'usd', from: '2026-10-01', to: '2026-10-31' });
    component.clearFilters();
    expect(service.getRates).toHaveBeenCalledWith({ currencyId: null, from: null, to: null });
  });

  it('Add Rate: active currencies other than the rate currency; shows the inverse and which rate it closes', async () => {
    await setup();
    component.openCreate();
    expect(component.currencyOptions.map(o => o.value)).toEqual(['usd', 'eur']);
    component.draft.currencyId = 'usd';
    component.onDraftCurrencyChange();
    expect(service.getRateHistory).toHaveBeenCalledWith('usd');
    component.draft.rate = 278.12;
    component.draft.effectiveFrom = new Date(2026, 9, 8);
    expect(component.inverse).toBe('0.003596 USD/PKR');
    expect(component.notice).toBe('The previous active rate (278.05 from 7 Oct 2026) will be closed to 7 Oct 2026.');
    expect(component.problem).toBeNull();

    component.save();
    expect(service.createRate).toHaveBeenCalledWith({ currencyId: 'usd', rate: 278.12, effectiveFrom: '2026-10-08', effectiveTo: null, notes: null });
    expect(component.dialogVisible).toBeFalse();
    expect(toasts).toHaveBeenCalledWith(jasmine.objectContaining({ severity: 'success' }));
  });

  it('a fixed end date is sent as the day picked; overlaps are refused before saving', async () => {
    await setup();
    component.openCreate();
    component.draft.currencyId = 'usd';
    component.onDraftCurrencyChange();
    component.draft.rate = 277;
    component.draft.effectiveFrom = new Date(2026, 9, 1);
    component.draft.openEnded = false;
    component.draft.effectiveTo = new Date(2026, 9, 6);
    expect(component.problem).toContain('Rate already exists for this date');
    component.save();
    expect(service.createRate).not.toHaveBeenCalled();

    component.draft.effectiveTo = new Date(2026, 9, 5);
    expect(component.problem).toBeNull();
    component.save();
    expect(service.createRate).toHaveBeenCalledWith(jasmine.objectContaining({ effectiveFrom: '2026-10-01', effectiveTo: '2026-10-05' }));
  });

  it('Edit: the currency is fixed, the range and rate are sent with PUT', async () => {
    await setup();
    component.openEdit(RATES[1]);
    expect(component.editing?.id).toBe('usd-old');
    expect(component.draft.openEnded).toBeFalse();
    component.draft.rate = 277.95;
    component.draft.notes = 'corrected';
    component.save();
    expect(service.updateRate).toHaveBeenCalledWith('usd-old', { rate: 277.95, effectiveFrom: '2026-10-06', effectiveTo: '2026-10-06', notes: 'corrected' });
  });

  it('shows the server refusal in the dialog', async () => {
    await setup();
    service.createRate.and.returnValue(throwError(() => new HttpErrorResponse({ status: 409, error: { success: false, message: 'Rate already exists for this date (USD 278.05 from 2026-10-07 to 9999-12-31)' } })));
    component.openCreate();
    component.draft = { currencyId: 'usd', rate: 280, effectiveFrom: new Date(2026, 9, 9), openEnded: true, effectiveTo: null, notes: '' };
    component.history = RATES.filter(r => r.currencyId === 'usd');
    component.save();
    expect(component.dialogVisible).toBeTrue();
    expect(component.saveError).toContain('Rate already exists');
  });

  it('checks the rate on a date', async () => {
    await setup();
    component.quoteCurrencyId = 'usd';
    component.quoteDate = new Date(2026, 9, 6);
    component.checkQuote();
    expect(service.getRateOn).toHaveBeenCalledWith('usd', '2026-10-06');
    expect(component.quote?.rate).toBe(277.92);
  });

  it('read-only without CURRENCY_RATE_MANAGE', async () => {
    await setup(false);
    expect(q('read-only')).not.toBeNull();
    expect(q('add-rate')).toBeNull();
    expect(q('edit-usd-cur')).toBeNull();
    component.openCreate();
    component.openEdit(RATES[2]);
    component.save();
    expect(component.dialogVisible).toBeFalse();
    expect(service.createRate).not.toHaveBeenCalled();
    expect(service.updateRate).not.toHaveBeenCalled();
  });
});
