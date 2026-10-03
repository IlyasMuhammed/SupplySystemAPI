import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { MappingsTabComponent, normalizeTaxCode, splitTaxRows, taxCodeUsageLabel, toSettingsRequest } from './mappings-tab.component';
import {
  IntegrationSettingsModel, QuickBooksIntegrationService, ReferenceDataModel, TaxCodeMappingModel, TermMappingModel
} from '../../../../services/quickbooks-integration.service';
import { PaymentTermsService } from '../../../../services/payment-terms.service';

const REFERENCE: ReferenceDataModel = {
  fetchedAt: '2026-09-30T08:00:00Z',
  accounts: [
    { id: '79', name: 'Sales of Product Income', type: 'Income', active: true },
    { id: '80', name: 'Cost of Goods Sold', type: 'Cost of Goods Sold', active: true },
    { id: '81', name: 'Freight and delivery', type: 'Expense', active: true },
    { id: '86', name: 'Discounts given', type: 'Income', active: true },
    { id: '35', name: 'Checking', type: 'Bank', active: true }
  ],
  taxCodes: [
    { id: '5', name: 'GST 17%', rate: 17, active: true },
    { id: '6', name: 'Exempt', rate: 0, active: true }
  ],
  terms: [
    { id: '3', name: 'Net 30', days: 30, active: true },
    { id: '2', name: 'Net 15', days: 15, active: true }
  ],
  currencies: []
};

function settings(overrides: Partial<IntegrationSettingsModel> = {}): IntegrationSettingsModel {
  return {
    mode: 'DryRun', autoPushCustomers: true, autoPushVendors: true, autoPushItems: true, autoPushSalesInvoices: true,
    autoPushBills: true, itemTypeDefault: 'NonInventory', partnerScope: 'OnlyWhenReferenced',
    defaultIncomeAccountId: null, defaultExpenseAccountId: null, freightExpenseAccountId: null, discountAccountId: null,
    defaultPurchaseTaxCodeId: null, documentStartDate: null, matchingConfirmedAt: null,
    ...overrides
  };
}

/** Percent rows only — what the server answered before tax codes, and still does with no code in use. */
const TAX: TaxCodeMappingModel[] = [
  { taxPercent: 17, qboTaxCodeId: '5', qboTaxCodeName: 'GST 17%', timesSeen: 12 },
  { taxPercent: 5, qboTaxCodeId: null, timesSeen: 2 },
  { taxPercent: 0, qboTaxCodeId: null, timesSeen: 1 }
];

/** SMS tax codes (S-11) as well: GST17 unmapped with a suggestion from the 17% row, EXEMPT mapped, VAT5 unmapped with none. */
const TAX_WITH_CODES: TaxCodeMappingModel[] = [
  { sourceTaxCode: 'GST17', taxPercent: 17, qboTaxCodeId: null, timesSeen: 8, suggestedQboTaxCodeId: '5', suggestedQboTaxCodeName: 'GST 17%' },
  { sourceTaxCode: 'EXEMPT', taxPercent: 0, qboTaxCodeId: '6', qboTaxCodeName: 'Exempt', timesSeen: 3 },
  { sourceTaxCode: 'VAT5', taxPercent: 5, qboTaxCodeId: null, timesSeen: 1 },
  ...TAX
];

/**
 * Every active SMS tax code is listed with its name and usage from Finance, used or not: GST17 mapped and in use,
 * IGST17 and ZERO set up but on no document yet (unmapped, each with a suggestion), OLD5 only on old documents
 * (Finance does not know it: no name, no usage).
 */
const TAX_WITH_FINANCE: TaxCodeMappingModel[] = [
  { sourceTaxCode: 'GST17', sourceTaxCodeName: 'GST 17%', sourceTaxCodeUsage: 'SALES', taxPercent: 17, qboTaxCodeId: '5', qboTaxCodeName: 'GST 17%', timesSeen: 8 },
  { sourceTaxCode: 'IGST17', sourceTaxCodeName: 'Input GST 17%', sourceTaxCodeUsage: 'PURCHASE', taxPercent: 17, qboTaxCodeId: null, timesSeen: 0,
    suggestedQboTaxCodeId: '5', suggestedQboTaxCodeName: 'GST 17%' },
  { sourceTaxCode: 'ZERO', sourceTaxCodeName: 'Zero rated', sourceTaxCodeUsage: 'BOTH', taxPercent: 0, qboTaxCodeId: null, timesSeen: 0,
    suggestedQboTaxCodeId: '6', suggestedQboTaxCodeName: 'Exempt' },
  { sourceTaxCode: 'OLD5', sourceTaxCodeName: null, sourceTaxCodeUsage: null, taxPercent: 5, qboTaxCodeId: null, timesSeen: 2 },
  { taxPercent: 0, qboTaxCodeId: '6', qboTaxCodeName: 'Exempt', timesSeen: 1 },
  { taxPercent: 17, qboTaxCodeId: '5', qboTaxCodeName: 'GST 17%', timesSeen: 12 }
];

const TERMS: TermMappingModel[] = [
  { paymentTermExternalId: 'AAAA-1', paymentTermName: 'Net 30', qboTermId: '3', qboTermName: 'Net 30' }
];

describe('MappingsTabComponent', () => {
  let fixture: ComponentFixture<MappingsTabComponent>;
  let component: MappingsTabComponent;
  let service: jasmine.SpyObj<QuickBooksIntegrationService>;
  let paymentTerms: jasmine.SpyObj<PaymentTermsService>;
  let toasts: jasmine.Spy;

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  async function setup(opts: {
    settings?: IntegrationSettingsModel; canManage?: boolean; reference?: ReferenceDataModel | null; tax?: TaxCodeMappingModel[];
  } = {}) {
    service = jasmine.createSpyObj<QuickBooksIntegrationService>('QuickBooksIntegrationService', [
      'getReference', 'refreshReference', 'getSettings', 'updateSettings', 'getTaxMappings', 'saveTaxMappings',
      'getTermMappings', 'saveTermMappings'
    ]);
    service.getReference.and.returnValue(opts.reference === null ? throwError(() => ({ status: 404 })) : of(opts.reference ?? REFERENCE));
    service.getSettings.and.returnValue(of(opts.settings ?? settings()));
    service.getTaxMappings.and.returnValue(of(opts.tax ?? TAX));
    service.getTermMappings.and.returnValue(of(TERMS));
    service.updateSettings.and.callFake(body => of(settings({ ...body, documentStartDate: body.documentStartDate } as any)));
    service.saveTaxMappings.and.callFake(body => of(body.mappings.map(m => ({ ...m, timesSeen: 1 }))));
    service.saveTermMappings.and.callFake(body => of(body.mappings));

    paymentTerms = jasmine.createSpyObj<PaymentTermsService>('PaymentTermsService', ['getAll']);
    paymentTerms.getAll.and.returnValue(of({
      success: true, message: '',
      result: [{ id: 'aaaa-1', name: 'Net 30', days: 30 }, { id: 'bbbb-2', name: 'Net 15', days: 15 }]
    }));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [MappingsTabComponent],
      providers: [
        provideNoopAnimations(), MessageService,
        { provide: QuickBooksIntegrationService, useValue: service },
        { provide: PaymentTermsService, useValue: paymentTerms }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(MappingsTabComponent);
    component = fixture.componentInstance;
    component.canManage = opts.canManage ?? true;
    toasts = spyOn(TestBed.inject(MessageService), 'add');
    fixture.detectChanges();
  }

  // ── Loading ────────────────────────────────────────────────────────────────

  it('loads reference data, settings, tax and term mappings, and SCM payment terms', async () => {
    await setup({ settings: settings({ defaultIncomeAccountId: '79', documentStartDate: '2026-10-01T00:00:00Z' }) });
    expect(service.getReference).toHaveBeenCalledTimes(1);
    expect(service.getSettings).toHaveBeenCalledTimes(1);
    expect(service.getTaxMappings).toHaveBeenCalledTimes(1);
    expect(service.getTermMappings).toHaveBeenCalledTimes(1);
    expect(paymentTerms.getAll).toHaveBeenCalledTimes(1);

    expect(component.form.get('defaultIncomeAccountId')!.value).toBe('79');
    const start = component.form.get('documentStartDate')!.value as Date;
    expect([start.getFullYear(), start.getMonth(), start.getDate()]).toEqual([2026, 9, 1]);
    expect(component.form.pristine).toBeTrue();
  });

  it('offers each account setting only suitable account types', async () => {
    await setup();
    expect(component.incomeOptions.map(o => o.value).sort()).toEqual(['79', '86']);
    expect(component.expenseOptions.map(o => o.value).sort()).toEqual(['80', '81']);
    expect(component.incomeOptions.some(o => o.value === '35')).toBeFalse();
    expect(component.purchaseTaxOptions.map(o => o.value).sort()).toEqual(['5', '6']);
  });

  it('says payments are not synced (D-8)', async () => {
    await setup();
    expect(query('payments-note')!.textContent).toContain('Payments are not sent to QuickBooks');
  });

  it('asks to read QuickBooks when there is no reference data yet', async () => {
    await setup({ reference: null });
    expect(query('no-reference')).not.toBeNull();
    service.refreshReference.and.returnValue(of(REFERENCE));
    (query('refresh-reference')!.querySelector('button') as HTMLButtonElement).click();
    expect(service.refreshReference).toHaveBeenCalledTimes(1);
    fixture.detectChanges();
    expect(query('no-reference')).toBeNull();
  });

  // ── Validation ─────────────────────────────────────────────────────────────

  it('refuses to save without the income and expense accounts, and says which', async () => {
    await setup();
    component.saveSettings();
    fixture.detectChanges();

    expect(service.updateSettings).not.toHaveBeenCalled();
    expect(toasts.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({ severity: 'warn', detail: 'Choose the income account items are sold to.' }));
    expect(query('income-error')).not.toBeNull();
    expect(query('expense-error')).not.toBeNull();

    component.form.patchValue({ defaultIncomeAccountId: '79' });
    component.saveSettings();
    expect(service.updateSettings).not.toHaveBeenCalled();
    expect(toasts.calls.mostRecent().args[0].detail).toBe('Choose the expense account items are bought from.');
  });

  // ── Save payload ───────────────────────────────────────────────────────────

  it('saves every setting, blanks as null and the start date as yyyy-MM-dd', async () => {
    await setup();
    const emitted: IntegrationSettingsModel[] = [];
    component.settingsChange.subscribe(s => emitted.push(s));

    component.form.patchValue({
      defaultIncomeAccountId: '79', defaultExpenseAccountId: '80', freightExpenseAccountId: '', discountAccountId: '86',
      defaultPurchaseTaxCodeId: null, itemTypeDefault: 'Service', partnerScope: 'AllActive',
      documentStartDate: new Date(2026, 9, 1), autoPushVendors: false, autoPushBills: false
    });
    component.form.markAsDirty();
    fixture.detectChanges();
    expect(query('settings-actions')!.textContent).toContain('Unsaved changes');

    component.saveSettings();

    expect(service.updateSettings).toHaveBeenCalledOnceWith({
      autoPushCustomers: true, autoPushVendors: false, autoPushItems: true, autoPushSalesInvoices: true, autoPushBills: false,
      itemTypeDefault: 'Service', partnerScope: 'AllActive',
      defaultIncomeAccountId: '79', defaultExpenseAccountId: '80', freightExpenseAccountId: null, discountAccountId: '86',
      defaultPurchaseTaxCodeId: null, documentStartDate: '2026-10-01'
    });
    expect(emitted.length).toBe(1);
    expect(component.form.pristine).toBeTrue();
    expect(toasts.calls.mostRecent().args[0].severity).toBe('success');
  });

  it('keeps the form as it is when the server refuses', async () => {
    await setup();
    service.updateSettings.and.returnValue(throwError(() => ({ status: 400, error: { message: 'Account 80 is inactive.' } })));
    component.form.patchValue({ defaultIncomeAccountId: '79', defaultExpenseAccountId: '80' });
    component.form.markAsDirty();
    component.saveSettings();
    expect(toasts.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({ severity: 'error', detail: 'Account 80 is inactive.' }));
    expect(component.form.dirty).toBeTrue();
  });

  it('is read-only without the manage permission', async () => {
    await setup({ canManage: false });
    expect(component.form.disabled).toBeTrue();
    expect(query('settings-actions')).toBeNull();
    component.saveSettings();
    component.saveTax();
    component.saveTerms();
    expect(service.updateSettings).not.toHaveBeenCalled();
    expect(service.saveTaxMappings).not.toHaveBeenCalled();
    expect(service.saveTermMappings).not.toHaveBeenCalled();
  });

  // ── Tax rates ──────────────────────────────────────────────────────────────

  it('lists the rates seen, highlights the unmapped ones, and shows how often each is used', async () => {
    await setup();
    const rows = fixture.nativeElement.querySelectorAll('[data-testid="tax-row"]');
    expect(rows.length).toBe(3);
    expect(fixture.nativeElement.querySelectorAll('tr.unmapped[data-testid="tax-row"]').length).toBe(2);
    expect(query('unmapped-tax-count')!.textContent).toContain('2 unmapped');
    expect(rows[2].textContent).toContain('12 lines');
  });

  it('saves every rate with its code, null for the unmapped', async () => {
    await setup();
    component.taxRows.find(r => r.taxPercent === 5)!.qboTaxCodeId = '6';
    expect(component.taxDirty).toBeTrue();
    component.newRate = 8.5;
    component.addRate();
    component.saveTax();

    expect(service.saveTaxMappings).toHaveBeenCalledOnceWith({
      mappings: [
        { taxPercent: 0, qboTaxCodeId: null },
        { taxPercent: 5, qboTaxCodeId: '6' },
        { taxPercent: 8.5, qboTaxCodeId: null },
        { taxPercent: 17, qboTaxCodeId: '5' }
      ]
    });
    expect(component.taxDirty).toBeFalse();
  });

  it('does not add a rate twice', async () => {
    await setup();
    component.newRate = 17;
    component.addRate();
    expect(component.taxRows.length).toBe(3);
    expect(toasts.calls.mostRecent().args[0].summary).toBe('Already listed');
  });

  it('with no tax code in use, shows an empty codes table and the rates as before', async () => {
    await setup();
    expect(component.taxCodeRows).toEqual([]);
    expect(query('tax-code-empty')).not.toBeNull();
    expect(fixture.nativeElement.querySelectorAll('[data-testid="tax-row"]').length).toBe(3);
  });

  // ── SMS tax codes (S-11) ───────────────────────────────────────────────────

  it('lists SMS tax codes with rate and use, separately from the bare rates', async () => {
    await setup({ tax: TAX_WITH_CODES });

    const codeRows = fixture.nativeElement.querySelectorAll('[data-testid="tax-code-row"]');
    expect(codeRows.length).toBe(3);
    expect(component.taxCodeRows.map(r => r.sourceTaxCode)).toEqual(['EXEMPT', 'GST17', 'VAT5']);
    expect(codeRows[1].textContent).toContain('GST17');
    expect(codeRows[1].textContent).toContain('17%');
    expect(codeRows[1].textContent).toContain('8 lines');
    expect(codeRows[2].textContent).toContain('1 line');
    expect(fixture.nativeElement.querySelectorAll('tr.unmapped[data-testid="tax-code-row"]').length).toBe(2);

    expect(component.taxRows.map(r => r.taxPercent)).toEqual([0, 5, 17]);
    expect(fixture.nativeElement.querySelectorAll('[data-testid="tax-row"]').length).toBe(3);
    expect(query('unmapped-tax-count')!.textContent).toContain('4 unmapped');
  });

  it('offers the QuickBooks code the rate is mapped to, and uses it only when asked', async () => {
    await setup({ tax: TAX_WITH_CODES });

    const gst = component.taxCodeRows.find(r => r.sourceTaxCode === 'GST17')!;
    expect(gst.qboTaxCodeId).toBeNull();
    expect(query('suggestion-GST17')!.textContent).toContain('17% is mapped to GST 17%');
    expect(query('suggestion-VAT5')).toBeNull();
    expect(query('suggestion-EXEMPT')).toBeNull();
    expect(component.taxDirty).toBeFalse();

    (query('use-suggestion-GST17')!.querySelector('button') as HTMLButtonElement).click();
    fixture.detectChanges();

    expect(gst.qboTaxCodeId).toBe('5');
    expect(component.taxDirty).toBeTrue();
    expect(query('suggestion-GST17')).toBeNull();
  });

  it('"Use suggestions" fills every unmapped code that has one and says how many', async () => {
    await setup({ tax: TAX_WITH_CODES });
    expect(component.suggestableCount).toBe(1);

    component.useAllSuggestions();

    expect(component.taxCodeRows.map(r => [r.sourceTaxCode, r.qboTaxCodeId])).toEqual([['EXEMPT', '6'], ['GST17', '5'], ['VAT5', null]]);
    expect(toasts.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({ summary: 'Suggested', detail: jasmine.stringContaining('1 code was') }));

    component.useAllSuggestions();
    expect(toasts.calls.mostRecent().args[0].summary).toBe('Nothing to suggest');
  });

  it('saves both kinds: code rows with their code, percent rows without one, cleared ones as null', async () => {
    await setup({ tax: TAX_WITH_CODES });
    component.taxCodeRows.find(r => r.sourceTaxCode === 'VAT5')!.qboTaxCodeId = '5';
    component.taxCodeRows.find(r => r.sourceTaxCode === 'EXEMPT')!.qboTaxCodeId = null;   // delete

    component.saveTax();

    expect(service.saveTaxMappings).toHaveBeenCalledOnceWith({
      mappings: [
        { sourceTaxCode: 'EXEMPT', taxPercent: 0, qboTaxCodeId: null },
        { sourceTaxCode: 'GST17', taxPercent: 17, qboTaxCodeId: null },
        { sourceTaxCode: 'VAT5', taxPercent: 5, qboTaxCodeId: '5' },
        { taxPercent: 0, qboTaxCodeId: null },
        { taxPercent: 5, qboTaxCodeId: null },
        { taxPercent: 17, qboTaxCodeId: '5' }
      ]
    });
    expect(component.taxDirty).toBeFalse();
    expect(component.taxCodeRows.map(r => r.sourceTaxCode)).withContext('what the server answered').toEqual(['EXEMPT', 'GST17', 'VAT5']);
    expect(toasts.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({ severity: 'success', detail: 'The tax mappings are saved.' }));
  });

  it('keeps the unsaved mappings when the server refuses them', async () => {
    await setup({ tax: TAX_WITH_CODES });
    service.saveTaxMappings.and.returnValue(throwError(() => ({ status: 400, error: { message: 'Tax code GST17 appears more than once.' } })));
    component.taxCodeRows.find(r => r.sourceTaxCode === 'GST17')!.qboTaxCodeId = '5';

    component.saveTax();

    expect(toasts.calls.mostRecent().args[0]).toEqual(jasmine.objectContaining({ severity: 'error', detail: 'Tax code GST17 appears more than once.' }));
    expect(component.taxDirty).toBeTrue();
    expect(component.isSavingTax).toBeFalse();
  });

  it('adds a code no document has used yet, upper-cased, and refuses a duplicate or an over-long one', async () => {
    await setup({ tax: TAX_WITH_CODES });

    component.newCode = '  pst16 ';
    component.newCodeRate = 16;
    component.addCode();
    expect(component.taxCodeRows.map(r => r.sourceTaxCode)).toEqual(['EXEMPT', 'GST17', 'PST16', 'VAT5']);
    expect(component.taxCodeRows.find(r => r.sourceTaxCode === 'PST16')).toEqual(jasmine.objectContaining({ taxPercent: 16, qboTaxCodeId: null, timesSeen: 0 }));
    expect(component.newCode).toBe('');
    expect(component.taxDirty).toBeTrue();

    component.newCode = 'gst17';
    component.newCodeRate = 17;
    component.addCode();
    expect(toasts.calls.mostRecent().args[0].summary).toBe('Already listed');

    component.newCode = 'X'.repeat(21);
    component.addCode();
    expect(toasts.calls.mostRecent().args[0].summary).toBe('Too long');
    expect(component.taxCodeRows.length).toBe(4);
  });

  it('needs both a code and its rate to add one', async () => {
    await setup({ tax: TAX_WITH_CODES });
    component.newCode = 'PST16';
    component.newCodeRate = null;
    component.addCode();
    component.newCode = '   ';
    component.newCodeRate = 16;
    component.addCode();
    expect(component.taxCodeRows.length).toBe(3);
  });

  it('read-only users see codes and suggestions but cannot change them', async () => {
    await setup({ tax: TAX_WITH_CODES, canManage: false });
    expect(query('suggestion-GST17')).not.toBeNull();
    expect(query('use-suggestion-GST17')).toBeNull();
    expect(query('use-all-suggestions')).toBeNull();
    expect(query('add-code')).toBeNull();
    component.useAllSuggestions();
    component.useSuggestion(component.taxCodeRows.find(r => r.sourceTaxCode === 'GST17')!);
    expect(component.taxCodeRows.find(r => r.sourceTaxCode === 'GST17')!.qboTaxCodeId).toBeNull();
  });

  // ── SMS tax codes from Finance: name and usage ─────────────────────────────

  it('shows a Finance tax code\'s name and who may use it under the code, and nothing extra for any other code', async () => {
    await setup({ tax: TAX_WITH_FINANCE });
    expect(component.taxCodeRows.map(r => r.sourceTaxCode)).toEqual(['GST17', 'IGST17', 'OLD5', 'ZERO']);

    expect(query('tax-code-name-GST17')!.textContent!.trim()).toBe('GST 17%');
    expect(query('tax-code-name-GST17')!.classList).withContext('muted').toContain('hint');
    expect(query('tax-code-usage-GST17')!.textContent!.trim()).toBe('Sales');
    expect(query('tax-code-name-IGST17')!.textContent!.trim()).toBe('Input GST 17%');
    expect(query('tax-code-usage-IGST17')!.textContent!.trim()).toBe('Purchases');
    expect(query('tax-code-name-ZERO')!.textContent!.trim()).toBe('Zero rated');
    expect(query('tax-code-usage-ZERO')!.textContent!.trim()).toBe('Sales & purchases');

    // Only seen on old documents: the code alone, no stray text.
    expect(query('tax-code-name-OLD5')).toBeNull();
    expect(query('tax-code-usage-OLD5')).toBeNull();
    expect(query('tax-code-cell-OLD5')!.textContent!.trim()).toBe('OLD5');

    // Added by hand: no name or usage either.
    component.newCode = 'new1';
    component.newCodeRate = 3;
    component.addCode();
    fixture.detectChanges();
    expect(component.taxCodeRows.find(r => r.sourceTaxCode === 'NEW1'))
      .toEqual(jasmine.objectContaining({ sourceTaxCodeName: null, sourceTaxCodeUsage: null }));
    expect(query('tax-code-cell-NEW1')!.textContent!.trim()).toBe('NEW1');

    // The percent table is as it was.
    const rateTable = query('tax-table')!;
    expect(rateTable.querySelectorAll('[data-testid="tax-row"]').length).toBe(2);
    expect(rateTable.textContent).not.toContain('Zero rated');
    expect(rateTable.textContent).not.toContain('Sales');
  });

  it('never sends a code\'s name or usage, and they do not make the table dirty', async () => {
    await setup({ tax: TAX_WITH_FINANCE });
    expect(component.taxDirty).toBeFalse();
    component.taxCodeRows.find(r => r.sourceTaxCode === 'GST17')!.sourceTaxCodeName = 'Renamed';
    component.taxCodeRows.find(r => r.sourceTaxCode === 'GST17')!.sourceTaxCodeUsage = 'BOTH';
    expect(component.taxDirty).withContext('display only').toBeFalse();

    component.taxCodeRows.find(r => r.sourceTaxCode === 'OLD5')!.qboTaxCodeId = '5';
    component.saveTax();

    expect(service.saveTaxMappings).toHaveBeenCalledOnceWith({
      mappings: [
        { sourceTaxCode: 'GST17', taxPercent: 17, qboTaxCodeId: '5' },
        { sourceTaxCode: 'IGST17', taxPercent: 17, qboTaxCodeId: null },
        { sourceTaxCode: 'OLD5', taxPercent: 5, qboTaxCodeId: '5' },
        { sourceTaxCode: 'ZERO', taxPercent: 0, qboTaxCodeId: null },
        { taxPercent: 0, qboTaxCodeId: '6' },
        { taxPercent: 17, qboTaxCodeId: '5' }
      ]
    });
    const sent = service.saveTaxMappings.calls.mostRecent().args[0].mappings;
    expect(sent.flatMap(m => Object.keys(m)).filter(k => !['sourceTaxCode', 'taxPercent', 'qboTaxCodeId'].includes(k))).toEqual([]);
    expect(JSON.stringify(sent)).not.toContain('Zero rated');
  });

  it('lists a Finance code no document uses yet as unmapped, and never takes its suggestion by itself', async () => {
    await setup({ tax: TAX_WITH_FINANCE });
    await fixture.whenStable();
    fixture.detectChanges();

    const zero = () => component.taxCodeRows.find(r => r.sourceTaxCode === 'ZERO')!;
    const selectLabel = (code: string) => query('tax-code-map-' + code)!.querySelector('.p-select-label')!.textContent!.trim();

    // On load.
    expect(zero().qboTaxCodeId).toBeNull();
    expect(query('tax-code-cell-ZERO')!.closest('tr')!.classList).toContain('unmapped');
    expect(query('tax-code-cell-ZERO')!.closest('tr')!.textContent).toContain('Not used yet');
    expect(query('suggestion-ZERO')!.textContent).toContain('0% is mapped to Exempt');
    expect(selectLabel('GST17')).withContext('a mapped code shows its QuickBooks code').toBe('GST 17% (17%)');
    expect(selectLabel('ZERO')).toBe('Not mapped');
    expect(component.taxDirty).toBeFalse();

    // After reading QuickBooks again.
    service.refreshReference.and.returnValue(of(REFERENCE));
    component.refreshReference();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(zero().qboTaxCodeId).toBeNull();
    expect(selectLabel('ZERO')).toBe('Not mapped');
    expect(component.taxDirty).toBeFalse();

    // After saving something else: sent unmapped, and the server's answer still only suggests it.
    service.saveTaxMappings.and.returnValue(of(TAX_WITH_FINANCE.map(r =>
      r.sourceTaxCode === 'OLD5' ? { ...r, qboTaxCodeId: '5', qboTaxCodeName: 'GST 17%' } : r)));
    component.taxCodeRows.find(r => r.sourceTaxCode === 'OLD5')!.qboTaxCodeId = '5';
    component.saveTax();
    expect(service.saveTaxMappings.calls.mostRecent().args[0].mappings).toContain({ sourceTaxCode: 'ZERO', taxPercent: 0, qboTaxCodeId: null });
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(zero().qboTaxCodeId).toBeNull();
    expect(query('suggestion-ZERO')).not.toBeNull();
    expect(selectLabel('ZERO')).toBe('Not mapped');
    expect(selectLabel('OLD5')).toBe('GST 17% (17%)');
    expect(component.taxDirty).toBeFalse();
  });

  // ── Payment terms ──────────────────────────────────────────────────────────

  it('lists SCM payment terms with their saved QuickBooks term (ids compared without case)', async () => {
    await setup();
    expect(component.termRows.map(r => [r.paymentTermName, r.qboTermId])).toEqual([['Net 15', null], ['Net 30', '3']]);
    expect(component.unmappedTermCount).toBe(1);
  });

  it('suggests by due days, then saves every term with its name', async () => {
    await setup();
    component.suggestTerms();
    expect(component.termRows.find(r => r.paymentTermName === 'Net 15')!.qboTermId).toBe('2');
    component.saveTerms();

    expect(service.saveTermMappings).toHaveBeenCalledOnceWith({
      mappings: [
        { paymentTermExternalId: 'bbbb-2', paymentTermName: 'Net 15', qboTermId: '2' },
        { paymentTermExternalId: 'aaaa-1', paymentTermName: 'Net 30', qboTermId: '3' }
      ]
    });
    expect(component.termsDirty).toBeFalse();
  });
});

describe('tax rows', () => {
  it('normalizeTaxCode trims and upper-cases, and blank is no code', () => {
    expect(normalizeTaxCode(' gst17 ')).toBe('GST17');
    expect(normalizeTaxCode('   ')).toBeNull();
    expect(normalizeTaxCode(null)).toBeNull();
    expect(normalizeTaxCode(undefined)).toBeNull();
  });

  it('splitTaxRows separates code rows (by code) from percent rows (by rate)', () => {
    const { codes, rates } = splitTaxRows([
      { taxPercent: 17, timesSeen: 1 },
      { sourceTaxCode: 'zero', taxPercent: 0, timesSeen: 0, qboTaxCodeId: '' },
      { sourceTaxCode: 'EXEMPT', taxPercent: 0, timesSeen: 2, qboTaxCodeId: '6' },
      { sourceTaxCode: '  ', taxPercent: 5, timesSeen: 3 }
    ]);

    expect(codes.map(r => [r.sourceTaxCode, r.qboTaxCodeId])).toEqual([['EXEMPT', '6'], ['ZERO', null]]);
    expect(rates.map(r => [r.taxPercent, r.timesSeen])).toEqual([[5, 3], [17, 1]]);
  });

  it('splitTaxRows carries a code\'s name and usage from Finance; every other row has null for both', () => {
    const { codes, rates } = splitTaxRows([
      { sourceTaxCode: 'gst17', sourceTaxCodeName: ' GST 17% ', sourceTaxCodeUsage: 'SALES', taxPercent: 17, timesSeen: 0 },
      { sourceTaxCode: 'OLD5', taxPercent: 5, timesSeen: 2 },
      { sourceTaxCode: 'X1', sourceTaxCodeName: '  ', sourceTaxCodeUsage: '', taxPercent: 1, timesSeen: 0 },
      { taxPercent: 17, timesSeen: 1 }
    ]);

    expect(codes.map(r => [r.sourceTaxCode, r.sourceTaxCodeName, r.sourceTaxCodeUsage]))
      .toEqual([['GST17', 'GST 17%', 'SALES'], ['OLD5', null, null], ['X1', null, null]]);
    expect(rates.map(r => [r.sourceTaxCodeName, r.sourceTaxCodeUsage])).toEqual([[null, null]]);
  });

  it('taxCodeUsageLabel says who may use a code, and nothing for an unknown usage', () => {
    expect(taxCodeUsageLabel('SALES')).toBe('Sales');
    expect(taxCodeUsageLabel('PURCHASE')).toBe('Purchases');
    expect(taxCodeUsageLabel('BOTH')).toBe('Sales & purchases');
    expect(taxCodeUsageLabel(' both ')).toBe('Sales & purchases');
    expect(taxCodeUsageLabel('EXPORT')).toBeNull();
    expect(taxCodeUsageLabel('')).toBeNull();
    expect(taxCodeUsageLabel(null)).toBeNull();
    expect(taxCodeUsageLabel(undefined)).toBeNull();
  });
});

describe('toSettingsRequest', () => {
  it('trims blanks to null and has every field the server expects', () => {
    const req = toSettingsRequest({
      defaultIncomeAccountId: ' ', defaultExpenseAccountId: '80', freightExpenseAccountId: null, discountAccountId: undefined as any,
      defaultPurchaseTaxCodeId: '5', itemTypeDefault: 'NonInventory', partnerScope: 'OnlyWhenReferenced', documentStartDate: null,
      autoPushCustomers: true, autoPushVendors: true, autoPushItems: false, autoPushSalesInvoices: true, autoPushBills: true
    });
    expect(Object.keys(req).sort()).toEqual([
      'autoPushBills', 'autoPushCustomers', 'autoPushItems', 'autoPushSalesInvoices', 'autoPushVendors',
      'defaultExpenseAccountId', 'defaultIncomeAccountId', 'defaultPurchaseTaxCodeId', 'discountAccountId',
      'documentStartDate', 'freightExpenseAccountId', 'itemTypeDefault', 'partnerScope'
    ]);
    expect(req.defaultIncomeAccountId).toBeNull();
    expect(req.discountAccountId).toBeNull();
    expect(req.documentStartDate).toBeNull();
    expect(req.autoPushItems).toBeFalse();
  });
});
