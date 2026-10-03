import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { HttpErrorResponse } from '@angular/common/http';
import { ConfirmationService, MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { TaxCodesComponent } from './tax-codes.component';
import { FinanceSetupService, TaxCodeModel } from '../../../services/finance-setup.service';
import { AuthService } from '../../service/auth.service';

function code(overrides: Partial<TaxCodeModel> = {}): TaxCodeModel {
  return {
    uuid: 'u-' + (overrides.code ?? 'GST17'), code: 'GST17', name: 'GST 17%', description: null, ratePercent: 17, usage: 'SALES',
    isDefault: false, isActive: true, ...overrides
  };
}

const CODES: TaxCodeModel[] = [
  code({ code: 'GST17', usage: 'SALES', isDefault: true }),
  code({ code: 'WHT5', name: 'Withholding 5%', ratePercent: 5, usage: 'PURCHASE' }),
  code({ code: 'ZERO', name: 'Zero rated', ratePercent: 0, usage: 'BOTH', description: 'Exports' }),
  code({ code: 'OLD16', name: 'Old GST', ratePercent: 16, usage: 'SALES', isActive: false })
];

describe('TaxCodesComponent', () => {
  let fixture: ComponentFixture<TaxCodesComponent>;
  let component: TaxCodesComponent;
  let service: jasmine.SpyObj<FinanceSetupService>;
  let toasts: jasmine.Spy;
  let permissions: string[];

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  async function setup(canManage = true, codes: TaxCodeModel[] = CODES) {
    permissions = canManage ? ['FINANCE_SETUP_MANAGE'] : [];
    service = jasmine.createSpyObj<FinanceSetupService>('FinanceSetupService', [
      'getTaxCodes', 'createTaxCode', 'updateTaxCode', 'createTaxCodesFromRatesInUse'
    ]);
    service.getTaxCodes.and.returnValue(of({ success: true, message: '', result: codes }));
    service.createTaxCode.and.callFake(req => of({ success: true, message: `Tax code ${req.code} created.`, result: code({ ...req, uuid: 'new' }) }));
    service.updateTaxCode.and.callFake((uuid, req) => of({ success: true, message: `Tax code ${req.code} updated.`, result: code({ ...req, uuid }) }));
    service.createTaxCodesFromRatesInUse.and.returnValue(of({
      success: true, message: 'Created 1 sales tax code: TAX7_5 (7.5%). 1 rate already had a code.',
      result: { created: [code({ uuid: 't', code: 'TAX7_5', name: 'Tax 7.5%', ratePercent: 7.5 })], skippedRates: [17] }
    }));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [TaxCodesComponent],
      providers: [
        provideNoopAnimations(),
        { provide: FinanceSetupService, useValue: service },
        { provide: AuthService, useValue: { hasPermission: (p: string) => permissions.includes(p) } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(TaxCodesComponent);
    component = fixture.componentInstance;
    toasts = spyOn(fixture.debugElement.injector.get(MessageService), 'add');
    fixture.detectChanges();
  }

  afterEach(() => fixture?.destroy());

  it('loads every code once, inactive ones too, and shows the active ones default first', async () => {
    await setup();

    expect(service.getTaxCodes).toHaveBeenCalledOnceWith(undefined, true);
    expect(component.visibleCodes.map(c => c.code)).toEqual(['GST17', 'WHT5', 'ZERO']);
    const row = query('code-GST17')!;
    expect(row.textContent).toContain('GST 17%');
    expect(row.textContent).toContain('17%');
    expect(row.textContent).toContain('Sales');
    expect(row.textContent).toContain('Default for sales');
    expect(query('code-OLD16')).toBeNull();
  });

  it('filters by side (both-codes count for either), by text, and shows deactivated codes on request', async () => {
    await setup();

    component.sideFilter = 'PURCHASE';
    expect(component.visibleCodes.map(c => c.code)).toEqual(['WHT5', 'ZERO']);

    component.sideFilter = 'ALL';
    component.search = 'export';
    expect(component.visibleCodes.map(c => c.code)).toEqual(['ZERO']);

    component.search = '';
    component.showInactive = true;
    fixture.detectChanges();
    expect(component.visibleCodes.map(c => c.code)).toContain('OLD16');
    expect(query('reactivate-OLD16')).not.toBeNull();
  });

  it('creates a code, upper-cased and trimmed, and reads the list again for the defaults the server moved', async () => {
    await setup();
    component.openCreate();
    expect(component.problem).toBe('Give the tax code a code, e.g. GST17.');

    component.draft = { ...component.draft, code: ' gst18 ', name: ' GST 18% ', ratePercent: 18, usage: 'SALES', isDefault: true };
    expect(component.problem).toBeNull();
    expect(component.defaultTakeoverText).toBe('GST17 stops being the default.');

    component.save();

    expect(service.createTaxCode).toHaveBeenCalledOnceWith({
      code: 'GST18', name: 'GST 18%', description: null, ratePercent: 18, usage: 'SALES', isDefault: true, isActive: true
    });
    expect(component.dialogVisible).toBeFalse();
    expect(service.getTaxCodes).toHaveBeenCalledTimes(2);
    expect(toasts).toHaveBeenCalledWith(jasmine.objectContaining({ severity: 'success', detail: 'Tax code GST18 created.' }));
  });

  it('does not send a draft the server would refuse', async () => {
    await setup();
    component.openCreate();
    component.draft = { ...component.draft, code: 'gst17', name: 'Dup', ratePercent: 17 };
    expect(component.problem).toBe('There is already a tax code GST17.');

    component.save();
    expect(service.createTaxCode).not.toHaveBeenCalled();
  });

  it('shows the server’s refusal in the dialog and keeps it open', async () => {
    await setup();
    service.createTaxCode.and.returnValue(throwError(() =>
      new HttpErrorResponse({ status: 409, error: { success: false, message: 'There is already a tax code NEW1.' } })));
    component.openCreate();
    component.draft = { ...component.draft, code: 'NEW1', name: 'New', ratePercent: 1 };

    component.save();
    fixture.detectChanges();

    expect(component.dialogVisible).toBeTrue();
    expect(component.saveError).toBe('There is already a tax code NEW1.');
  });

  it('edits a code, warning that documents keep the old rate and that a used code cannot be renamed', async () => {
    await setup();
    component.openEdit(CODES[0]);
    expect(component.rateChangeNote).toBeNull();

    component.draft = { ...component.draft, ratePercent: 18 };
    expect(component.rateChangeNote).toBe('Documents already raised keep 17%; lines added from now on use 18%.');

    component.draft = { ...component.draft, code: 'GST-17' };
    expect(component.renameNote).toContain('cannot be renamed');

    component.draft = { ...component.draft, code: 'GST17' };
    component.save();
    expect(service.updateTaxCode).toHaveBeenCalledOnceWith('u-GST17', jasmine.objectContaining({ code: 'GST17', ratePercent: 18 }));
  });

  it('clears the default when the code is made inactive in the dialog', async () => {
    await setup();
    component.openEdit(CODES[0]);
    component.draft = { ...component.draft, isActive: false };
    component.onActiveChange(false);

    expect(component.draft.isDefault).toBeFalse();
    component.save();
    expect(service.updateTaxCode).toHaveBeenCalledWith('u-GST17', jasmine.objectContaining({ isActive: false, isDefault: false }));
  });

  it('asks before deactivating, then sends the code inactive and no longer default', async () => {
    await setup();
    const confirm = spyOn(fixture.debugElement.injector.get(ConfirmationService), 'confirm');

    component.confirmDeactivate(CODES[0]);
    expect(service.updateTaxCode).not.toHaveBeenCalled();
    expect(confirm.calls.mostRecent().args[0].message).toContain('default code');

    confirm.calls.mostRecent().args[0].accept!();
    expect(service.updateTaxCode).toHaveBeenCalledOnceWith('u-GST17', {
      code: 'GST17', name: 'GST 17%', description: null, ratePercent: 17, usage: 'SALES', isDefault: false, isActive: false
    });
  });

  it('reactivates a deactivated code', async () => {
    await setup();
    component.reactivate(CODES[3]);
    expect(service.updateTaxCode).toHaveBeenCalledOnceWith('u-OLD16', jasmine.objectContaining({ isActive: true }));
  });

  it('creates codes from the rates already used after asking, and shows what was created and skipped', async () => {
    await setup();
    const confirm = spyOn(fixture.debugElement.injector.get(ConfirmationService), 'confirm');

    component.confirmFromRates();
    expect(service.createTaxCodesFromRatesInUse).not.toHaveBeenCalled();
    confirm.calls.mostRecent().args[0].accept!();
    fixture.detectChanges();

    expect(service.createTaxCodesFromRatesInUse).toHaveBeenCalledTimes(1);
    expect(query('from-rates-result')!.textContent).toContain('Created 1 sales tax code');
    expect(query('from-rates-created')!.textContent).toContain('TAX7_5');
    expect(query('from-rates-created')!.textContent).toContain('7.5%');
    expect(query('from-rates-skipped')!.textContent).toContain('17%');
    expect(service.getTaxCodes).toHaveBeenCalledTimes(2);

    component.dismissFromRates();
    fixture.detectChanges();
    expect(query('from-rates-result')).toBeNull();
  });

  it('is read-only without the manage permission', async () => {
    await setup(false);

    expect(query('read-only')).not.toBeNull();
    expect(query('new-code')).toBeNull();
    expect(query('from-rates')).toBeNull();
    expect(query('edit-GST17')).toBeNull();
    expect(query('code-GST17')).not.toBeNull();

    component.openCreate();
    component.openEdit(CODES[0]);
    component.confirmFromRates();
    expect(component.dialogVisible).toBeFalse();
    expect(service.createTaxCodesFromRatesInUse).not.toHaveBeenCalled();
  });

  it('sends the rate the server will accept, not float noise from the rate box’s arrow keys', async () => {
    await setup();
    component.openCreate();
    // PrimeNG's InputNumber spins by adding the step to the parsed value: 0.57 + 1 = 1.5699999999999998.
    // The server refuses anything with more than two decimals (decimal.Round(rate, 2) != rate → 400).
    component.draft = { ...component.draft, code: 'LOW', name: 'Low rate', ratePercent: 0.57 + 1, usage: 'SALES' };
    expect(component.problem).toBeNull();

    component.save();

    expect(service.createTaxCode.calls.mostRecent().args[0].ratePercent).toBe(1.57);
  });

  it('accepts a 0% and a 100% code, and refuses three decimals before sending', async () => {
    await setup();
    component.openCreate();
    component.draft = { ...component.draft, code: 'EXEMPT', name: 'Exempt', ratePercent: 0, usage: 'BOTH' };
    expect(component.problem).toBeNull();
    component.draft = { ...component.draft, ratePercent: 100 };
    expect(component.problem).toBeNull();
    component.draft = { ...component.draft, ratePercent: 17.255 };
    expect(component.problem).toContain('two decimals');

    component.save();
    expect(service.createTaxCode).not.toHaveBeenCalled();
  });

  it('shows the server’s sentence about what changed after a save, and its 400 in the dialog', async () => {
    await setup();
    const sentence = 'Tax code VAT5 updated. It is now the default for sales. GST17 is no longer the default.';
    service.updateTaxCode.and.returnValue(of({ success: true, message: sentence, result: code({ code: 'WHT5' }) }));
    component.openEdit(CODES[1]);
    component.draft = { ...component.draft, isDefault: true };
    component.save();
    expect(toasts).toHaveBeenCalledWith(jasmine.objectContaining({ severity: 'success', detail: sentence }));

    service.updateTaxCode.and.returnValue(throwError(() => new HttpErrorResponse({
      status: 400, error: { success: false, message: "A tax rate can have at most two decimals, like 17.25; 5.555 has more." }
    })));
    component.openEdit(CODES[1]);
    component.save();
    fixture.detectChanges();
    expect(query('save-error')!.textContent).toContain('5.555 has more');
  });

  it('never makes an inactive code the default, even if the draft says so', async () => {
    await setup();
    component.openCreate();
    component.draft = { ...component.draft, code: 'NEWX', name: 'New', ratePercent: 3, usage: 'SALES', isActive: false, isDefault: true };
    expect(component.takesDefaultFrom).toEqual([]);

    component.save();
    expect(service.createTaxCode).toHaveBeenCalledWith(jasmine.objectContaining({ isActive: false, isDefault: false }));
  });

  it('is read-only for a finance viewer: no write is reachable, the codes still load', async () => {
    await setup(false);
    component.showInactive = true;
    fixture.detectChanges();

    expect(service.getTaxCodes).toHaveBeenCalledOnceWith(undefined, true);
    expect(query('code-OLD16')).not.toBeNull();
    expect(query('reactivate-OLD16')).toBeNull();
    expect(query('deactivate-GST17')).toBeNull();

    component.save();
    component.confirmDeactivate(CODES[0]);
    component.reactivate(CODES[3]);
    component.createFromRates();
    expect(service.createTaxCode).not.toHaveBeenCalled();
    expect(service.updateTaxCode).not.toHaveBeenCalled();
    expect(service.createTaxCodesFromRatesInUse).not.toHaveBeenCalled();
  });

  it('says when the list could not be loaded, and when there is nothing yet', async () => {
    await setup(true, []);
    expect(query('empty')!.textContent).toContain('No tax codes yet');

    service.getTaxCodes.and.returnValue(throwError(() => new HttpErrorResponse({ status: 500 })));
    component.load();
    fixture.detectChanges();
    expect(query('load-failed')).not.toBeNull();
  });
});
