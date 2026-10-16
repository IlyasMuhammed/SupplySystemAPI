import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { provideRouter } from '@angular/router';
import { MessageService } from 'primeng/api';
import { of, throwError } from 'rxjs';

import { AccountsTabComponent } from './accounts-tab.component';
import { AccountMappingsModel, QuickBooksIntegrationService } from '../../../../services/quickbooks-integration.service';

// GET accounts as CTRL answers it (PostingAdminModels.AccountMappingsModel).
function model(overrides: Partial<AccountMappingsModel> = {}): AccountMappingsModel {
  return {
    glAvailable: true, referenceFetchedAt: '2026-10-16T08:00:00Z', totalAccounts: 3, mapped: 1, unmapped: 2, invalid: 0,
    accounts: [
      { accountUuid: 'a-4100', code: '4100', name: 'Sales', accountType: 'Revenue', isActive: true, qboAccountId: '79',
        qboAccountName: 'Sales of Product Income', status: 'Mapped', defaultKeys: ['DEFAULT_REVENUE'] },
      { accountUuid: 'a-1110', code: '1110', name: 'Cash in hand', accountType: 'Cash', isActive: true, qboAccountId: null, status: 'Unmapped', defaultKeys: [] },
      { accountUuid: 'a-1200', code: '1200', name: 'Trade debtors', accountType: 'Receivable', isActive: true, qboAccountId: null, status: 'Unmapped', defaultKeys: [] }
    ],
    qboAccounts: [
      { id: '79', name: 'Sales of Product Income', type: 'Income', active: true, mappedCount: 1 },
      { id: '35', name: 'Checking', acctNum: '1010', type: 'Bank', active: true, mappedCount: 0 }
    ],
    defaultRoles: [],
    ...overrides
  };
}

describe('AccountsTabComponent', () => {
  let fixture: ComponentFixture<AccountsTabComponent>;
  let component: AccountsTabComponent;
  let service: jasmine.SpyObj<QuickBooksIntegrationService>;
  let toasts: jasmine.Spy;

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  async function setup(opts: { model?: AccountMappingsModel; canManage?: boolean; fail?: boolean } = {}) {
    service = jasmine.createSpyObj<QuickBooksIntegrationService>('QuickBooksIntegrationService', [
      'getAccountMappings', 'getReference', 'refreshReference', 'autoMapAccounts', 'saveAccountMappings'
    ]);
    service.getAccountMappings.and.returnValue(opts.fail
      ? throwError(() => ({ status: 500, error: { message: 'Down.' } }))
      : of(JSON.parse(JSON.stringify(opts.model ?? model()))));
    service.getReference.and.returnValue(of({ accounts: [{ id: '1', name: 'From reference', type: 'Bank', active: true }], taxCodes: [], terms: [], currencies: [] }));
    service.refreshReference.and.returnValue(of({ accounts: [{ id: '35', name: 'Checking', type: 'Bank', active: true }], taxCodes: [], terms: [], currencies: [] }));
    const mapped = model({ accounts: model().accounts.map(a => a.code === '1110' ? { ...a, qboAccountId: '35', status: 'Mapped' } : a) });
    service.autoMapAccounts.and.returnValue(of({
      mapped: 1, unmatched: 1, accounts: mapped,
      matches: [{ accountUuid: 'a-1110', code: '1110', name: 'Cash in hand', qboAccountId: '35', qboAccountName: 'Checking', rule: 'AcctNum' }],
      ambiguous: [{ accountUuid: 'a-1200', code: '1200', name: 'Trade debtors', rule: 'NormalizedName', candidates: [
        { id: '84', name: 'Accounts Receivable', type: 'Accounts Receivable', active: true },
        { id: '85', name: 'Accounts Receivable (A/R)', acctNum: '1200', type: 'Accounts Receivable', active: true }
      ] }]
    }));
    service.saveAccountMappings.and.callFake(body => of(model({ accounts: model().accounts.map(a => {
      const m = body.mappings.find(x => x.accountUuid === a.accountUuid);
      return m ? { ...a, qboAccountId: m.qboAccountId, status: m.qboAccountId ? 'Mapped' : 'Unmapped' } : a;
    }) })));

    await TestBed.resetTestingModule().configureTestingModule({
      imports: [AccountsTabComponent],
      providers: [provideNoopAnimations(), provideRouter([]), MessageService, { provide: QuickBooksIntegrationService, useValue: service }]
    }).compileComponents();

    fixture = TestBed.createComponent(AccountsTabComponent);
    component = fixture.componentInstance;
    component.canManage = opts.canManage ?? true;
    toasts = spyOn(TestBed.inject(MessageService), 'add');
    fixture.detectChanges();
  }

  it('lists the GL leaves with their QuickBooks account and warns about unmapped ones', async () => {
    await setup();
    const rows = fixture.nativeElement.querySelectorAll('[data-testid="account-row"]');
    expect(rows.length).toBe(3);
    expect(rows[0].textContent).toContain('1110');
    expect(rows[2].textContent).toContain('Default for DEFAULT_REVENUE');
    expect(query('unmapped-warning')!.textContent).toContain('2 of 3 accounts are not mapped');
    expect(component.qboOpts.map(o => o.value)).toEqual(['35', '79']);
    expect(component.qboOpts[0].label).toContain('1010 · Checking');
  });

  it('marks an account mapped to an inactive QuickBooks account', async () => {
    const m = model();
    m.accounts[0].status = 'Invalid';
    await setup({ model: m });
    expect(query('invalid-4100')).not.toBeNull();
    expect(query('unmapped-warning')!.textContent).toContain('1 is mapped to an account QuickBooks no longer has active');
  });

  it('uses the reference snapshot when the answer carries no QuickBooks accounts', async () => {
    await setup({ model: model({ qboAccounts: [] }) });
    expect(component.qboOpts.map(o => o.value)).toContain('1');
  });

  it('links to the GL setup when the general ledger is not available', async () => {
    await setup({ model: model({ glAvailable: false, accounts: [], totalAccounts: 0 }) });
    expect(query('no-chart')).not.toBeNull();
    expect(query('gl-setup-link')!.getAttribute('href')).toContain('/portal/pages/finance/gl/setup');
    expect(query('accounts-table')).toBeNull();
  });

  it('filters to unmapped accounts and by code or name', async () => {
    await setup();
    component.unmappedOnly = true;
    component.applyFilter();
    expect(component.filtered.map(r => r.code)).toEqual(['1110', '1200']);
    component.unmappedOnly = false;
    component.search = 'debt';
    component.applyFilter();
    expect(component.filtered.map(r => r.code)).toEqual(['1200']);
  });

  it('saves only the changed rows, by account uuid', async () => {
    await setup();
    component.rows.find(r => r.code === '1110')!.draft = '35';
    expect(component.dirty).toBeTrue();
    component.save();
    expect(service.saveAccountMappings).toHaveBeenCalledOnceWith({ mappings: [{ accountUuid: 'a-1110', qboAccountId: '35' }] });
    expect(component.dirty).toBeFalse();
    expect(component.unmappedCount).toBe(1);
  });

  it('auto-maps unmapped accounts on the server, shows the result and the ambiguous ones', async () => {
    await setup();
    service.getAccountMappings.calls.reset();
    query('auto-map')!.querySelector('button')!.click();
    fixture.detectChanges();
    expect(service.autoMapAccounts).toHaveBeenCalledOnceWith({ overwrite: false });
    expect(service.getAccountMappings).not.toHaveBeenCalled();
    expect(component.rows.find(r => r.code === '1110')!.qboAccountId).toBe('35');
    expect(toasts.calls.mostRecent().args[0].detail).toContain('1 account was mapped');
    expect(toasts.calls.mostRecent().args[0].detail).toContain('1 matched more than one');
    expect(query('ambiguous')!.textContent).toContain('1200 Trade debtors: Accounts Receivable, 1200 Accounts Receivable (A/R)');
  });

  it('imports the QuickBooks accounts again', async () => {
    await setup();
    component.importAccounts();
    expect(service.refreshReference).toHaveBeenCalled();
    expect(component.qboAccounts.length).toBe(1);
  });

  it('read-only without manage', async () => {
    await setup({ canManage: false });
    expect(query('auto-map')).toBeNull();
    expect(query('save-accounts')).toBeNull();
  });

  it('shows the error when it cannot load', async () => {
    await setup({ fail: true });
    expect(query('load-failed')!.textContent).toContain('Down.');
  });
});
