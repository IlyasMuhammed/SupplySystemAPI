import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { DefaultsTabComponent } from './defaults-tab.component';
import { ProviderDefaultsModel, QuickBooksIntegrationService, ReferenceDataModel } from '../../../../services/quickbooks-integration.service';

function reference(overrides: Partial<ReferenceDataModel> = {}): ReferenceDataModel {
  return {
    fetchedAt: '2026-10-16T08:00:00Z',
    accounts: [
      { id: '35', name: 'Checking', type: 'Bank', active: true },
      { id: '4', name: 'Undeposited Funds', type: 'Other Current Asset', active: true },
      { id: '79', name: 'Sales', type: 'Income', active: true }
    ],
    taxCodes: [], currencies: [],
    terms: [{ id: '3', name: 'Net 30', days: 30, active: true }],
    classes: [{ id: '5000', name: 'Retail', active: true }],
    departments: [{ id: '1', name: 'Karachi', active: true }],
    paymentMethods: [{ id: '1', name: 'Cash', active: true }, { id: '2', name: 'Visa', active: true }],
    tracking: { classTrackingPerTransaction: true, classTrackingPerLine: false, departmentTracking: false, departmentTerminology: 'Location' },
    ...overrides
  };
}

// GET provider-defaults as CTRL answers it (PostingAdminModels.ProviderDefaultsModel).
function defaultsModel(overrides: Partial<ProviderDefaultsModel> = {}): ProviderDefaultsModel {
  return {
    classTrackingEnabled: true, departmentTrackingEnabled: false, departmentTerminology: 'Location',
    defaults: [
      { settingKey: 'DepositToAccountRef.CASH', baseKey: 'DepositToAccountRef', method: 'CASH', settingValue: '4', valueName: 'Undeposited Funds' },
      { settingKey: 'ClassRef', baseKey: 'ClassRef', settingValue: '5000', warning: null },
      { settingKey: 'BankAccountRef.CARD', baseKey: 'BankAccountRef', method: 'CARD', settingValue: '99', warning: 'The account is no longer active in QuickBooks.' }
    ],
    documentTypes: ['SalesInvoice', 'Payment', 'POSSale'],
    ...overrides
  };
}

describe('DefaultsTabComponent', () => {
  let fixture: ComponentFixture<DefaultsTabComponent>;
  let component: DefaultsTabComponent;
  let service: jasmine.SpyObj<QuickBooksIntegrationService>;
  let toasts: jasmine.Spy;

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  async function setup(opts: { reference?: ReferenceDataModel; model?: ProviderDefaultsModel; canManage?: boolean; fail?: boolean; customers?: boolean } = {}) {
    service = jasmine.createSpyObj<QuickBooksIntegrationService>('QuickBooksIntegrationService', [
      'getReference', 'refreshReference', 'getProviderDefaults', 'saveProviderDefaults', 'getDocumentOverrides',
      'saveDocumentOverrides', 'getRemote'
    ]);
    service.getReference.and.returnValue(of(opts.reference ?? reference()));
    service.refreshReference.and.returnValue(of(reference({
      tracking: { classTrackingPerTransaction: true, departmentTracking: true, departmentTerminology: 'Division' }
    })));
    service.getProviderDefaults.and.returnValue(opts.fail
      ? throwError(() => ({ status: 500, error: { message: 'Down.' } }))
      : of(opts.model ?? defaultsModel()));
    service.saveProviderDefaults.and.callFake(body => of(defaultsModel({ defaults: body.values.map(v => ({ ...v })) })));
    service.getDocumentOverrides.and.returnValue(of([{ documentType: 'POSSale', settingKey: 'ClassRef', settingValue: '5000' }]));
    service.saveDocumentOverrides.and.callFake(body => of(body.overrides));
    service.getRemote.and.returnValue(opts.customers === false ? throwError(() => ({ status: 404 })) : of([{ remoteId: '58', name: 'Walk-in' }]));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [DefaultsTabComponent],
      providers: [provideNoopAnimations(), MessageService, { provide: QuickBooksIntegrationService, useValue: service }]
    }).compileComponents();

    fixture = TestBed.createComponent(DefaultsTabComponent);
    component = fixture.componentInstance;
    component.canManage = opts.canManage ?? true;
    toasts = spyOn(TestBed.inject(MessageService), 'add');
    fixture.detectChanges();
  }

  it('shows class only when the company tracks it, location only when it tracks locations', async () => {
    await setup();
    expect(query('class-field')).not.toBeNull();
    expect(query('department-field')).toBeNull();
    expect(query('tracking-hint')!.textContent).toContain('Location tracking is off');
    expect(component.editedKeys).toContain('ClassRef');
    expect(component.editedKeys).not.toContain('DepartmentRef');
    expect(component.overrideKeyOpts.map(o => o.value)).toEqual(['ClassRef', 'SalesTermRef']);
  });

  it('asks for the reference to be read again while tracking is not known, then uses the fresh preferences', async () => {
    await setup({
      reference: reference({ classes: [], paymentMethods: [], tracking: {} }),
      model: defaultsModel({ classTrackingEnabled: null, departmentTrackingEnabled: null, departmentTerminology: null })
    });
    expect(query('reference-incomplete')).not.toBeNull();
    expect(query('class-field')).toBeNull();
    query('refresh-reference')!.querySelector('button')!.click();
    fixture.detectChanges();
    expect(service.refreshReference).toHaveBeenCalled();
    expect(query('reference-incomplete')).toBeNull();
    expect(query('department-field')!.textContent).toContain('Division');
  });

  it('falls back to the reference snapshot\'s tracking when the defaults answer does not say', async () => {
    await setup({ model: defaultsModel({ classTrackingEnabled: undefined, departmentTrackingEnabled: undefined }) });
    expect(component.tracksClasses).toBeTrue();
    expect(component.tracksDepartments).toBeFalse();
  });

  it('shows the server\'s warning on a stored value, and its override document types', async () => {
    await setup();
    expect(component.warningFor('BankAccountRef.CARD')).toContain('no longer active');
    expect(query('bill-CARD')!.textContent).toContain('no longer active');
    expect(component.documentTypeOpts.map(o => o.value)).toEqual(['SalesInvoice', 'Payment', 'POSSale']);
  });

  it('a payment method and deposit account per method; deposit and bank lists hold bank-type accounts only', async () => {
    await setup();
    expect(fixture.nativeElement.querySelectorAll('[data-testid^="pay-"]').length).toBe(6);
    expect(fixture.nativeElement.querySelectorAll('[data-testid^="bill-"]').length).toBe(6);
    // Deposit: Bank + Other Current Asset. Paid from: Bank (+ Credit Card), plus the saved CARD account QuickBooks no longer lists.
    expect(component.depositOpts.map(o => o.value).sort()).toEqual(['35', '4']);
    expect(component.bankOpts.map(o => o.value).sort()).toEqual(['35', '99']);
    expect(component.bankOpts.find(o => o.value === '99')!.label).toContain('not found');
    expect(component.refundOpts.map(o => o.value).sort()).toEqual(['35', '4']);
    expect(component.values['DepositToAccountRef.CASH']).toBe('4');
    expect(component.methodsWithoutDeposit).not.toContain('CASH');
  });

  it('saves every shown key, blanks as null', async () => {
    await setup();
    component.values['BankAccountRef.CHEQUE'] = '35';
    component.values['PosDefaultCustomerRef'] = '58';
    expect(component.dirty).toBeTrue();
    component.save();
    const sent = service.saveProviderDefaults.calls.mostRecent().args[0].values;
    expect(sent).toContain({ settingKey: 'BankAccountRef.CHEQUE', settingValue: '35' });
    expect(sent).toContain({ settingKey: 'PosDefaultCustomerRef', settingValue: '58' });
    expect(sent).toContain({ settingKey: 'ClassRef', settingValue: '5000' });
    expect(sent).toContain({ settingKey: 'RefundFromAccountRef', settingValue: null });
    expect(sent.some(d => d.settingKey === 'DepartmentRef')).toBeFalse();
    expect(component.dirty).toBeFalse();
    expect(toasts.calls.mostRecent().args[0].severity).toBe('success');
  });

  it('discard restores the saved values', async () => {
    await setup();
    component.values['ClassRef'] = null;
    expect(component.dirty).toBeTrue();
    component.discard();
    expect(component.values['ClassRef'] as string | null).toBe('5000');
    expect(component.dirty).toBeFalse();
  });

  it('picks the walk-in customer by name from imported customers, by id otherwise', async () => {
    await setup();
    expect(query('posCustomer')).not.toBeNull();
    expect(component.customerOpts).toEqual([{ label: 'Walk-in', value: '58' }]);
    await setup({ customers: false });
    expect(query('posCustomerId')).not.toBeNull();
  });

  it('adds, edits and saves per-document-type overrides', async () => {
    await setup();
    expect(fixture.nativeElement.querySelectorAll('[data-testid="override-row"]').length).toBe(1);
    component.addOverride();
    const row = component.overrides[1];
    row.documentType = 'Payment';
    row.settingKey = 'SalesTermRef';
    row.settingValue = '3';
    expect(component.overridesDirty).toBeTrue();
    component.saveOverrides();
    expect(service.saveDocumentOverrides).toHaveBeenCalledOnceWith({ overrides: [
      { documentType: 'POSSale', settingKey: 'ClassRef', settingValue: '5000' },
      { documentType: 'Payment', settingKey: 'SalesTermRef', settingValue: '3' }
    ] });
    expect(component.overridesDirty).toBeFalse();
  });

  it('flags two rows setting the same thing for one type', async () => {
    await setup();
    component.addOverride();
    component.overrides[1].documentType = 'POSSale';
    component.overrides[1].settingKey = 'ClassRef';
    expect(component.isDuplicate(component.overrides[0])).toBeTrue();
    component.removeOverride(1);
    expect(component.isDuplicate(component.overrides[0])).toBeFalse();
  });

  it('read-only without manage', async () => {
    await setup({ canManage: false });
    expect(query('defaults-actions')).toBeNull();
    expect(query('add-override')).toBeNull();
    component.save();
    expect(service.saveProviderDefaults).not.toHaveBeenCalled();
  });

  it('shows the error when the defaults cannot be loaded', async () => {
    await setup({ fail: true });
    expect(query('load-failed')!.textContent).toContain('Down.');
  });

  // ── D-35 — refund account per method ───────────────────────────────────────

  it('a refund account per method next to the base one, with the lookup order as a hint', async () => {
    await setup();
    expect(fixture.nativeElement.querySelectorAll('[data-testid^="refund-from-"]').length).toBe(6);
    expect(query('refund-lookup-hint')!.textContent).toContain("that method's refund account");
    expect(query('refund-lookup-hint')!.textContent).toContain('the refund account above');
    expect(query('refund-lookup-hint')!.textContent).toContain("the method's deposit account");
    for (const m of ['CASH', 'CHEQUE', 'BANK_TRANSFER', 'CARD', 'ONLINE', 'MOBILE']) {
      expect(component.editedKeys).withContext(m).toContain(`RefundFromAccountRef.${m}`);
    }
  });

  it('works out the account a refund comes from: method → base → deposit', async () => {
    await setup();
    // CASH has a deposit account (4) only.
    expect(component.effectiveRefund('CASH')).toEqual({ id: '4', from: 'deposit' });
    expect(component.refundPlaceholder('CASH')).toContain('Cash deposit account');
    expect(component.effectiveRefund('CARD')).toBeNull();
    expect(component.refundPlaceholder('CARD')).toBe('Cash / bank account mapping');
    component.values['RefundFromAccountRef'] = '35';
    expect(component.effectiveRefund('CASH')).toEqual({ id: '35', from: 'base' });
    expect(component.refundPlaceholder('CARD')).toContain('The refund account (Checking');
    component.values['RefundFromAccountRef.CASH'] = '4';
    expect(component.effectiveRefund('CASH')).toEqual({ id: '4', from: 'method' });
  });

  it('saves the per-method refund keys with the others; a stored one is read back', async () => {
    await setup({ model: defaultsModel({ defaults: [
      ...defaultsModel().defaults,
      { settingKey: 'RefundFromAccountRef.CARD', baseKey: 'RefundFromAccountRef', method: 'CARD', settingValue: '35' }
    ] }) });
    expect(component.values['RefundFromAccountRef.CARD']).toBe('35');
    component.values['RefundFromAccountRef.MOBILE'] = '4';
    component.save();
    const sent = service.saveProviderDefaults.calls.mostRecent().args[0].values;
    expect(sent).toContain({ settingKey: 'RefundFromAccountRef.CARD', settingValue: '35' });
    expect(sent).toContain({ settingKey: 'RefundFromAccountRef.MOBILE', settingValue: '4' });
    expect(sent).toContain({ settingKey: 'RefundFromAccountRef.CASH', settingValue: null });
  });
});
