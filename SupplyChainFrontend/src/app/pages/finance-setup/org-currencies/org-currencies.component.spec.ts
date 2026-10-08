import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { HttpErrorResponse } from '@angular/common/http';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { OrgCurrenciesComponent } from './org-currencies.component';
import { OrgCurrencyModel, OrgCurrencyService } from '../../../services/org-currency.service';
import { CurrenciesService } from '../../../services/currencies.service';
import { MoneyService } from '../../../services/money.service';
import { AuthService } from '../../service/auth.service';

function cur(o: Partial<OrgCurrencyModel>): OrgCurrencyModel {
  return {
    currencyId: 'id-' + (o.code ?? 'USD').toLowerCase(), code: 'USD', name: 'US Dollar', symbol: '$', decimalPlaces: 2, rounding: 0.01,
    symbolPosition: 'before', isActive: true, displayOrder: 2, baseFor: [], isRateCurrency: false, ...o
  };
}

const LIST: OrgCurrencyModel[] = [
  cur({ code: 'JPY', name: 'Japanese Yen', symbol: '¥', decimalPlaces: 0, rounding: 1, displayOrder: 8 }),
  cur({ code: 'USD', displayOrder: 2 }),
  cur({ code: 'PKR', name: 'Pakistani Rupee', symbol: '₨', displayOrder: 1, baseFor: ['SALE', 'PURCHASE', 'SERVICE'], isRateCurrency: true }),
  cur({ code: 'INR', name: 'Indian Rupee', symbol: '₹', displayOrder: 10, isActive: false })
];

describe('OrgCurrenciesComponent (A35-P1-05, §11.2)', () => {
  let fixture: ComponentFixture<OrgCurrenciesComponent>;
  let component: OrgCurrenciesComponent;
  let service: jasmine.SpyObj<OrgCurrencyService>;
  let money: jasmine.SpyObj<MoneyService>;
  let toasts: jasmine.Spy;
  let permissions: string[];

  const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  async function setup(canManage = true) {
    permissions = canManage ? ['CURRENCY_MANAGE'] : ['CURRENCY_VIEW'];
    service = jasmine.createSpyObj<OrgCurrencyService>('OrgCurrencyService', ['getCurrencies', 'createCurrency', 'updateCurrency']);
    service.getCurrencies.and.returnValue(of({ success: true, message: '', result: LIST.map(c => ({ ...c })) }));
    service.createCurrency.and.callFake(req => of({ success: true, message: '', result: cur({ ...req, currencyId: 'new' } as any) }));
    service.updateCurrency.and.callFake((id, req) => of({ success: true, message: '', result: cur({ ...req, currencyId: id } as any) }));
    money = jasmine.createSpyObj<MoneyService>('MoneyService', ['reload']);
    const catalog = jasmine.createSpyObj<CurrenciesService>('CurrenciesService', ['getAll']);
    catalog.getAll.and.returnValue(of({ success: true, message: '', result: [
      { id: 'g-eur', code: 'EUR', name: 'Euro', symbol: '€' }, { id: 'g-usd', code: 'USD', name: 'US Dollar', symbol: '$' }
    ] }));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [OrgCurrenciesComponent],
      providers: [
        provideNoopAnimations(),
        { provide: OrgCurrencyService, useValue: service },
        { provide: CurrenciesService, useValue: catalog },
        { provide: MoneyService, useValue: money },
        { provide: AuthService, useValue: { hasPermission: (p: string) => permissions.includes(p) } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(OrgCurrenciesComponent);
    component = fixture.componentInstance;
    toasts = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
    fixture.detectChanges();
  }

  afterEach(() => fixture?.destroy());

  it('lists every org currency, inactive ones too, by display order, with the lock on base currencies', async () => {
    await setup();
    expect(service.getCurrencies).toHaveBeenCalledOnceWith(true);
    expect(component.currencies.map(c => c.code)).toEqual(['PKR', 'USD', 'JPY', 'INR']);
    expect(q('cur-JPY')!.textContent).toContain('Japanese Yen');
    expect(q('cur-JPY')!.textContent).toContain('¥1,234');   // format example with 0 decimals (T-C1-08)
    expect(q('lock-PKR')).not.toBeNull();
    expect(q('lock-USD')).toBeNull();
    expect(component.lockText(component.currencies[0])).toContain('sale, purchase and service base currency');
  });

  it('adds a currency: upper-cases the code, takes name and symbol from the global catalog when it has the code', async () => {
    await setup();
    component.openCreate();
    expect(component.draft.displayOrder).toBe(11);
    component.draft.code = 'eur';
    component.onCodeChange();
    expect(component.draft.name).toBe('Euro');
    expect(component.draft.symbol).toBe('€');
    expect(component.needsNameAndSymbol).toBeFalse();
    expect(component.problem).toBeNull();
    component.save();
    expect(service.createCurrency).toHaveBeenCalledWith(jasmine.objectContaining({ code: 'EUR', name: 'Euro', symbol: '€', decimalPlaces: 2 }));
    expect(money.reload).toHaveBeenCalled();
    expect(component.dialogVisible).toBeFalse();
  });

  it('a code not in the catalog needs a name and a symbol; duplicates are refused before saving', async () => {
    await setup();
    component.openCreate();
    component.draft.code = 'MXN';
    component.onCodeChange();
    expect(component.needsNameAndSymbol).toBeTrue();
    expect(component.problem).toBe('Name is required (max 60).');
    component.draft.code = 'USD';
    expect(component.problem).toBe('Currency USD already exists.');
    component.save();
    expect(service.createCurrency).not.toHaveBeenCalled();
  });

  it('edits with the code fixed', async () => {
    await setup();
    component.openEdit(component.currencies[1]);
    expect(component.editing?.code).toBe('USD');
    component.draft.symbol = 'US$';
    component.draft.decimalPlaces = 3;
    component.draft.rounding = 0.001;
    component.save();
    expect(service.updateCurrency).toHaveBeenCalledWith('id-usd', jasmine.objectContaining({ code: 'USD', symbol: 'US$', decimalPlaces: 3 }));
  });

  it('changing decimals offers the matching rounding', async () => {
    await setup();
    component.openEdit(component.currencies[1]);
    component.draft.decimalPlaces = 3;
    component.onDecimalsChange();
    expect(component.draft.rounding).toBe(0.001);
    component.draft.decimalPlaces = 0;
    component.onDecimalsChange();
    expect(component.draft.rounding).toBe(1);
  });

  it('the Active toggle saves at once; a refusal is shown and the toggle goes back', async () => {
    await setup();
    const usd = component.currencies[1];
    component.toggleActive(usd, false);
    expect(service.updateCurrency).toHaveBeenCalledWith('id-usd', jasmine.objectContaining({ code: 'USD', isActive: false }));
    expect(usd.isActive).toBeFalse();

    service.updateCurrency.and.returnValue(throwError(() => new HttpErrorResponse({ status: 400, error: { success: false, message: 'Cannot deactivate — used as sale base currency' } })));
    const jpy = component.currencies[2];
    component.toggleActive(jpy, false);
    expect(jpy.isActive).toBeTrue();
    expect(toasts).toHaveBeenCalledWith(jasmine.objectContaining({ severity: 'error', detail: 'Cannot deactivate — used as sale base currency' }));
  });

  it('a base currency cannot be switched off from the list', async () => {
    await setup();
    expect(component.canToggle(component.currencies[0])).toBeFalse();
    component.toggleActive(component.currencies[0], false);
    expect(service.updateCurrency).not.toHaveBeenCalled();
  });

  it('read-only without CURRENCY_MANAGE', async () => {
    await setup(false);
    expect(q('read-only')).not.toBeNull();
    expect(q('add-currency')).toBeNull();
    expect(q('edit-USD')).toBeNull();
    component.openCreate();
    component.openEdit(component.currencies[1]);
    component.toggleActive(component.currencies[1], false);
    component.save();
    expect(component.dialogVisible).toBeFalse();
    expect(service.createCurrency).not.toHaveBeenCalled();
    expect(service.updateCurrency).not.toHaveBeenCalled();
  });
});
