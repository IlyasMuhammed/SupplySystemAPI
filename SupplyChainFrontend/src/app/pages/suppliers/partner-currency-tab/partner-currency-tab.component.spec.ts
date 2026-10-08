import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { HttpErrorResponse } from '@angular/common/http';
import { of, throwError } from 'rxjs';

import { PartnerCurrencyTabComponent } from './partner-currency-tab.component';
import { BusinessPartnerModel, BusinessPartnerService } from '../../../services/business-partner.service';
import { OrgCurrencyModel, OrgCurrencyService } from '../../../services/org-currency.service';
import { AuthService } from '../../service/auth.service';

function cur(code: string, o: Partial<OrgCurrencyModel> = {}): OrgCurrencyModel {
  return {
    currencyId: code.toLowerCase(), code, name: code + ' name', symbol: code, decimalPlaces: 2, rounding: 0.01, symbolPosition: 'before',
    isActive: true, displayOrder: 1, baseFor: [], ...o
  };
}

const PARTNER: BusinessPartnerModel = {
  uuid: 'p1', partnerCode: 'BP-1', companyName: 'Al Rashid Trading LLC', partnerType: 'CUSTOMER', isVendor: true, isCustomer: true,
  isCarrier: false, isServiceProvider: false, isActive: true,
  defaultSaleCurrencyId: 'aed', defaultSaleCurrencyCode: 'AED', defaultPurchaseCurrencyId: null, defaultPurchaseCurrencyCode: null
};

describe('PartnerCurrencyTabComponent (A35-P2-09, §11.6)', () => {
  let fixture: ComponentFixture<PartnerCurrencyTabComponent>;
  let component: PartnerCurrencyTabComponent;
  let partners: jasmine.SpyObj<BusinessPartnerService>;
  let permissions: string[];

  const q = (id: string) => fixture.nativeElement.querySelector(`[data-testid="${id}"]`) as HTMLElement | null;

  async function setup(canEdit = true, partner: BusinessPartnerModel = PARTNER) {
    permissions = canEdit ? ['SUPPLIER_EDIT'] : ['SUPPLIER_VIEW'];
    partners = jasmine.createSpyObj<BusinessPartnerService>('BusinessPartnerService', ['updatePartner']);
    partners.updatePartner.and.returnValue(of({ success: true, message: 'Record updated successfully.', result: null }));
    const currencies = jasmine.createSpyObj<OrgCurrencyService>('OrgCurrencyService', ['getCurrencies']);
    currencies.getCurrencies.and.returnValue(of({ success: true, message: '', result: [
      cur('PKR', { baseFor: ['SALE', 'SERVICE'] }), cur('USD', { baseFor: ['PURCHASE'] }), cur('AED')
    ] }));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [PartnerCurrencyTabComponent],
      providers: [
        provideNoopAnimations(),
        { provide: BusinessPartnerService, useValue: partners },
        { provide: OrgCurrencyService, useValue: currencies },
        { provide: AuthService, useValue: { hasAnyPermission: (...c: string[]) => c.some(x => permissions.includes(x)) } }
      ]
    }).compileComponents();
    fixture = TestBed.createComponent(PartnerCurrencyTabComponent);
    component = fixture.componentInstance;
    component.partner = { ...partner };
    fixture.detectChanges();
  }

  afterEach(() => fixture?.destroy());

  it('shows the partner defaults, active currencies, and the org base each blank falls back to', async () => {
    await setup();
    expect(component.saleCurrencyId).toBe('aed');
    expect(component.purchaseCurrencyId).toBeNull();
    expect(component.saleBlankLabel).toBe('Use org default: PKR');
    expect(component.purchaseBlankLabel).toBe('Use org default: USD');
    expect(q('sale-note')!.textContent).toContain('will default to AED');
    expect(q('purchase-note')!.textContent).toContain('Used when this partner is also a supplier');
  });

  it('the note names the org default when the sale currency is blank', async () => {
    await setup();
    component.saleCurrencyId = null;
    fixture.detectChanges();
    expect(q('sale-note')!.textContent).toContain('will default to PKR');
  });

  it('saves the whole partner with the new defaults', async () => {
    await setup();
    let saved = false;
    component.saved.subscribe(() => saved = true);
    component.purchaseCurrencyId = 'usd';
    component.save();
    const [uuid, body] = partners.updatePartner.calls.mostRecent().args;
    expect(uuid).toBe('p1');
    expect(body).toEqual(jasmine.objectContaining({
      companyName: 'Al Rashid Trading LLC', isCustomer: true, defaultSaleCurrencyId: 'aed', defaultPurchaseCurrencyId: 'usd',
      clearDefaultSaleCurrency: false, clearDefaultPurchaseCurrency: false
    }));
    expect(saved).toBeTrue();
  });

  it('a blank choice clears the default (null alone would mean "unchanged")', async () => {
    await setup();
    component.saleCurrencyId = null;
    component.save();
    const body = partners.updatePartner.calls.mostRecent().args[1];
    expect(body.defaultSaleCurrencyId).toBeNull();
    expect(body.clearDefaultSaleCurrency).toBeTrue();
    expect(body.clearDefaultPurchaseCurrency).toBeTrue();
  });

  it('shows the server refusal (BR-C4-01)', async () => {
    await setup();
    partners.updatePartner.and.returnValue(throwError(() => new HttpErrorResponse({ status: 400, error: { success: false, message: 'AED is not an active currency of this organization.' } })));
    component.save();
    fixture.detectChanges();
    expect(q('save-error')!.textContent).toContain('AED is not an active currency');
  });

  it('read-only without SUPPLIER_EDIT / SUPPLIER_MANAGE', async () => {
    await setup(false);
    expect(q('save')).toBeNull();
    component.save();
    expect(partners.updatePartner).not.toHaveBeenCalled();
  });

  it('nothing to save when nothing changed', async () => {
    await setup();
    expect(component.dirty).toBeFalse();
    component.saleCurrencyId = 'pkr';
    expect(component.dirty).toBeTrue();
  });
});
