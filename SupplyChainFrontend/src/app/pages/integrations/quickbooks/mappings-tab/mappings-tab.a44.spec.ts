import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { MappingsTabComponent } from './mappings-tab.component';
import { IntegrationSettingsModel, QuickBooksIntegrationService } from '../../../../services/quickbooks-integration.service';
import { PaymentTermsService } from '../../../../services/payment-terms.service';

const SETTINGS: IntegrationSettingsModel = {
  mode: 'DryRun', autoPushCustomers: true, autoPushVendors: false, autoPushItems: true, autoPushSalesInvoices: true,
  autoPushBills: true, autoPushPosSales: false, itemTypeDefault: 'NonInventory', partnerScope: 'OnlyWhenReferenced',
  defaultIncomeAccountId: '79', defaultExpenseAccountId: '80'
};

/** A44 D-14 / D-18 on the Mappings tab: the switches move to Sync control; tax "Import & suggest". */
describe('MappingsTabComponent — A44', () => {
  let fixture: ComponentFixture<MappingsTabComponent>;
  let component: MappingsTabComponent;
  let service: jasmine.SpyObj<QuickBooksIntegrationService>;
  let toasts: jasmine.Spy;

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  async function setup(syncControl: boolean) {
    service = jasmine.createSpyObj<QuickBooksIntegrationService>('QuickBooksIntegrationService', [
      'getReference', 'refreshReference', 'getSettings', 'updateSettings', 'getTaxMappings', 'saveTaxMappings',
      'getTermMappings', 'saveTermMappings', 'getSyncControl', 'importTaxMappings'
    ]);
    service.getSyncControl.and.returnValue(syncControl ? of([]) : throwError(() => ({ status: 404 })));
    service.getReference.and.returnValue(of({ accounts: [], taxCodes: [{ id: '5', name: 'GST', rate: 17, active: true }], terms: [], currencies: [] }));
    service.getSettings.and.returnValue(of(SETTINGS));
    service.getTaxMappings.and.returnValue(of([{ sourceTaxCode: 'GST17', taxPercent: 17, qboTaxCodeId: null, timesSeen: 2 }]));
    service.getTermMappings.and.returnValue(of([]));
    service.updateSettings.and.callFake(body => of({ ...SETTINGS, ...body } as IntegrationSettingsModel));
    service.importTaxMappings.and.returnValue(of({
      saved: 1, alreadyMapped: 0,
      savedRows: [{ sourceTaxCode: 'GST17', taxPercent: 17, qboTaxCodeId: '5', qboTaxCodeName: 'GST', reason: 'Same rate' }],
      needsReview: [{ sourceTaxCode: 'EXEMPT', taxPercent: 0, reason: 'Two 0% codes match', candidates: [] }],
      mappings: [{ sourceTaxCode: 'GST17', taxPercent: 17, qboTaxCodeId: '5', qboTaxCodeName: 'GST', timesSeen: 2 }]
    }));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [MappingsTabComponent],
      providers: [
        provideNoopAnimations(), MessageService,
        { provide: QuickBooksIntegrationService, useValue: service },
        { provide: PaymentTermsService, useValue: { getAll: () => of({ success: true, message: '', result: [] }) } }
      ]
    }).compileComponents();

    fixture = TestBed.createComponent(MappingsTabComponent);
    component = fixture.componentInstance;
    component.canManage = true;
    toasts = spyOn(TestBed.inject(MessageService), 'add');
    fixture.detectChanges();
  }

  it('keeps the switches (and the payments note) while the server has no sync control', async () => {
    await setup(false);
    expect(component.syncControlAvailable).toBeFalse();
    expect(query('autoPushCustomers')).not.toBeNull();
    expect(query('pos-sales-row')).not.toBeNull();
    expect(query('payments-note')).not.toBeNull();
    expect(query('sync-control-moved')).toBeNull();
    expect(query('import-tax')).toBeNull();
  });

  it('replaces the switches with a pointer to Sync control once the server has it', async () => {
    await setup(true);
    expect(component.syncControlAvailable).toBeTrue();
    expect(query('autoPushCustomers')).toBeNull();
    expect(query('pos-sales-row')).toBeNull();
    expect(query('payments-note')).toBeNull();
    const opened: string[] = [];
    component.navigate.subscribe(t => opened.push(t));
    query('open-sync-control')!.querySelector('button')!.click();
    expect(opened).toEqual(['sync-control']);
  });

  it('saving the settings still sends the switch values the server holds (unchanged)', async () => {
    await setup(true);
    component.form.markAsDirty();
    component.saveSettings();
    const body = service.updateSettings.calls.mostRecent().args[0];
    expect(body.autoPushVendors).toBeFalse();
    expect(body.autoPushCustomers).toBeTrue();
  });

  it('"Import & suggest" saves the unambiguous codes and lists the rest', async () => {
    await setup(true);
    query('import-tax')!.querySelector('button')!.click();
    fixture.detectChanges();
    expect(service.importTaxMappings).toHaveBeenCalled();
    expect(component.taxCodeRows[0].qboTaxCodeId).toBe('5');
    expect(query('tax-ambiguous')!.textContent).toContain('EXEMPT: Two 0% codes match');
    expect(toasts.calls.mostRecent().args[0].detail).toContain('1 tax code was mapped. 1 need a decision below.');
  });

  it('reloads the mappings when the import answer carries none', async () => {
    await setup(true);
    service.importTaxMappings.and.returnValue(of({ saved: 0 }));
    service.getTaxMappings.calls.reset();
    component.importTax();
    expect(service.getTaxMappings).toHaveBeenCalledTimes(1);
  });
});
