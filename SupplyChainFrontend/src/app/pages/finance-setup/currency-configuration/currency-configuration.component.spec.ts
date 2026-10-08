import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { HttpErrorResponse } from '@angular/common/http';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { CurrencyConfigurationComponent } from './currency-configuration.component';
import { OrgCurrencyModel, OrgCurrencyService, OrgCurrencySettingsModel } from '../../../services/org-currency.service';
import { MoneyService } from '../../../services/money.service';
import { AuthService } from '../../service/auth.service';

function cur(code: string, o: Partial<OrgCurrencyModel> = {}): OrgCurrencyModel {
  return {
    currencyId: code.toLowerCase(), code, name: code + ' name', symbol: code, decimalPlaces: 2, rounding: 0.01, symbolPosition: 'before',
    isActive: true, displayOrder: 1, ...o
  };
}

function settings(o: Partial<OrgCurrencySettingsModel> = {}): OrgCurrencySettingsModel {
  return {
    saleBaseCurrencyId: 'pkr', saleBaseCurrencyCode: 'PKR', purchaseBaseCurrencyId: 'pkr', purchaseBaseCurrencyCode: 'PKR',
    serviceBaseCurrencyId: 'pkr', serviceBaseCurrencyCode: 'PKR', rateCurrencyId: 'pkr', rateCurrencyCode: 'PKR',
    exchangeGainAccountCode: '7110', exchangeLossAccountCode: '7120', unrealizedGainAccountCode: null, unrealizedLossAccountCode: null,
    isStored: true,
    locks: {
      sale: { locked: true, reason: '12 confirmed sale orders' }, purchase: { locked: false, reason: null },
      service: { locked: false, reason: null }, rateCurrency: { locked: true, reason: '34 exchange rates' }
    },
    ...o
  };
}

describe('CurrencyConfigurationComponent (A35-P2-05, §11.1)', () => {
  let fixture: ComponentFixture<CurrencyConfigurationComponent>;
  let component: CurrencyConfigurationComponent;
  let service: jasmine.SpyObj<OrgCurrencyService>;
  let money: jasmine.SpyObj<MoneyService>;
  let toasts: jasmine.Spy;
  let permissions: string[];

  const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  async function setup(canManage = true, model = settings()) {
    permissions = canManage ? ['ORG_CURRENCY_SETTINGS_MANAGE'] : ['CURRENCY_VIEW'];
    service = jasmine.createSpyObj<OrgCurrencyService>('OrgCurrencyService', ['getCurrencies', 'getSettings', 'saveSettings']);
    service.getCurrencies.and.returnValue(of({ success: true, message: '', result: [cur('PKR'), cur('USD'), cur('EUR')] }));
    service.getSettings.and.returnValue(of({ success: true, message: '', result: model }));
    service.saveSettings.and.callFake(req => of({ success: true, message: '', result: settings({ ...req, rateCurrencyId: 'pkr' } as any) }));
    money = jasmine.createSpyObj<MoneyService>('MoneyService', ['reload']);

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [CurrencyConfigurationComponent],
      providers: [
        provideNoopAnimations(),
        { provide: OrgCurrencyService, useValue: service },
        { provide: MoneyService, useValue: money },
        { provide: AuthService, useValue: { hasPermission: (p: string) => permissions.includes(p) } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(CurrencyConfigurationComponent);
    component = fixture.componentInstance;
    toasts = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
    fixture.detectChanges();
  }

  afterEach(() => fixture?.destroy());

  it('shows the three bases from active currencies, the rate currency read-only, and the four account codes', async () => {
    await setup();
    expect(service.getCurrencies).toHaveBeenCalledWith(false);
    expect(component.options.map(o => o.label)).toEqual(['PKR - PKR name', 'USD - USD name', 'EUR - EUR name']);
    expect(component.form.saleBaseCurrencyId).toBe('pkr');
    expect(q('rate-currency')!.textContent).toContain('PKR');
    expect(component.form.exchangeGainAccountCode).toBe('7110');
    expect(component.form.unrealizedGainAccountCode).toBe('');
  });

  it('a base in use cannot be changed: disabled, with the reason as a tooltip', async () => {
    await setup();
    expect(component.isLocked('sale')).toBeTrue();
    expect(component.lockTooltip('sale')).toBe('Cannot change — transactions exist in this base currency (12 confirmed sale orders).');
    expect(component.isLocked('purchase')).toBeFalse();
    expect(component.lockTooltip('purchase')).toBeNull();
    expect(q('lock-sale')).not.toBeNull();
    expect(q('lock-purchase')).toBeNull();
  });

  it('warns when a base differs from the others', async () => {
    await setup();
    expect(component.warnings).toEqual([]);
    component.form.purchaseBaseCurrencyId = 'usd';
    expect(component.warnings).toEqual(['Purchase base set to USD — all purchase amounts will be converted to USD for accounting.']);
  });

  it('saves the bases and account codes (rate currency unchanged), then refreshes the money formatting', async () => {
    await setup();
    component.form.purchaseBaseCurrencyId = 'usd';
    component.form.unrealizedGainAccountCode = ' 7130 ';
    component.save();
    expect(service.saveSettings).toHaveBeenCalledWith({
      saleBaseCurrencyId: 'pkr', purchaseBaseCurrencyId: 'usd', serviceBaseCurrencyId: 'pkr', rateCurrencyId: null,
      exchangeGainAccountCode: '7110', exchangeLossAccountCode: '7120', unrealizedGainAccountCode: '7130', unrealizedLossAccountCode: null
    });
    expect(money.reload).toHaveBeenCalled();
    expect(toasts).toHaveBeenCalledWith(jasmine.objectContaining({ severity: 'success' }));
  });

  it('refuses an account code over 20 characters before saving', async () => {
    await setup();
    component.form.exchangeLossAccountCode = 'X'.repeat(21);
    expect(component.problem).toBe('Account codes can be at most 20 characters.');
    component.save();
    expect(service.saveSettings).not.toHaveBeenCalled();
  });

  it('shows the server refusal (409 base in use)', async () => {
    await setup(true, settings({ locks: null }));
    service.saveSettings.and.returnValue(throwError(() => new HttpErrorResponse({ status: 409, error: { success: false, message: 'Cannot change the sale base currency — 3 issued sales invoices.' } })));
    component.form.saleBaseCurrencyId = 'usd';
    component.save();
    expect(toasts).toHaveBeenCalledWith(jasmine.objectContaining({ severity: 'error', detail: 'Cannot change the sale base currency — 3 issued sales invoices.' }));
  });

  it('says when nothing is saved yet', async () => {
    await setup(true, settings({ isStored: false, locks: null }));
    expect(q('not-stored')).not.toBeNull();
  });

  it('read-only without ORG_CURRENCY_SETTINGS_MANAGE', async () => {
    await setup(false);
    expect(q('read-only')).not.toBeNull();
    expect(q('save')).toBeNull();
    component.save();
    expect(service.saveSettings).not.toHaveBeenCalled();
  });
});
