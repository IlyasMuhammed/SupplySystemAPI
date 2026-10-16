import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideNoopAnimations } from '@angular/platform-browser/animations';
import { of, throwError } from 'rxjs';

import { CoverageTabComponent } from './coverage-tab.component';
import { CoverageModel, QuickBooksIntegrationService } from '../../../../services/quickbooks-integration.service';

// GET coverage as CTRL answers it (PostingAdminModels.CoverageModel).
const NOT_READY: CoverageModel = {
  ready: false, connectionStatus: 'Live', mode: 'Live', referenceFetchedAt: '2026-10-16T09:00:00Z',
  entities: [
    { kind: 'Customer', total: 10, mapped: 6, skipped: 2, pending: 1, failed: 0, remoteImported: 12, remoteUnlinked: 3, percentMapped: 60 },
    { kind: 'Item', total: 4, mapped: 1, skipped: 0, pending: 3 }
  ],
  taxCodes: { total: 3, mapped: 2, unmapped: ['GST17'] },
  accounts: { glAvailable: true, total: 20, mapped: 8, unmapped: 12, blocking: false },
  providerDefaults: { required: 4, set: 3, missing: [] },
  blockingIssues: [
    { code: 'TAX_UNMAPPED', area: 'Tax', message: 'GST17 has no QuickBooks tax code.', tab: 'tax' },
    { code: 'DEPOSIT_ACCOUNTS', area: 'Defaults', message: 'Customer payments are on but CARD has no deposit account.', tab: 'defaults' }
  ],
  warnings: [{ code: 'ACCOUNTS', area: 'Accounts', message: '12 GL accounts are not mapped.', tab: 'accounts' }]
};

describe('CoverageTabComponent', () => {
  let fixture: ComponentFixture<CoverageTabComponent>;
  let component: CoverageTabComponent;
  let service: jasmine.SpyObj<QuickBooksIntegrationService>;

  function query(testId: string): HTMLElement | null {
    return fixture.nativeElement.querySelector(`[data-testid="${testId}"]`);
  }

  async function setup(answer: CoverageModel | 'fail' = NOT_READY) {
    service = jasmine.createSpyObj<QuickBooksIntegrationService>('QuickBooksIntegrationService', ['getCoverage']);
    service.getCoverage.and.returnValue(answer === 'fail' ? throwError(() => ({ status: 500, error: { message: 'Down.' } })) : of(answer));
    await TestBed.resetTestingModule().configureTestingModule({
      imports: [CoverageTabComponent],
      providers: [provideNoopAnimations(), { provide: QuickBooksIntegrationService, useValue: service }]
    }).compileComponents();
    fixture = TestBed.createComponent(CoverageTabComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  }

  it('shows NOT READY with every blocking issue and the tab that fixes it', async () => {
    await setup();
    expect(query('readiness-tag')!.textContent).toContain('NOT READY');
    const issues = fixture.nativeElement.querySelectorAll('[data-testid="blocking-issue"]');
    expect(issues.length).toBe(2);
    expect(query('fix-TAX_UNMAPPED')!.textContent).toContain('Fix on Mappings');
    expect(query('fix-DEPOSIT_ACCOUNTS')!.textContent).toContain('Fix on Defaults');
    expect(query('warnings')!.textContent).toContain('12 GL accounts are not mapped.');
  });

  it('a fix button opens its tab', async () => {
    await setup();
    const opened: string[] = [];
    component.navigate.subscribe(t => opened.push(t));
    query('fix-TAX_UNMAPPED')!.querySelector('button')!.click();
    expect(opened).toEqual(['mappings']);
  });

  it('a bar per entity kind, tax codes, provider defaults and (information) GL accounts', async () => {
    await setup();
    expect(component.rows.map(r => r.kind)).toEqual(['Customer', 'Item', 'TaxCode', 'ProviderDefault', 'Account']);
    expect(query('pct-Customer')!.textContent).toContain('80%');
    expect(query('pct-Item')!.textContent).toContain('25%');
    expect(query('kind-Customer')!.textContent).toContain('6 of 10 mapped · 2 skipped · 1 pending');
    expect(query('remote-unlinked-Customer')!.textContent).toContain('3 imported QuickBooks records are not linked');
    expect(query('unmapped-tax')!.textContent).toContain('GST17');
    expect(query('kind-ProviderDefault')!.textContent).toContain('3 of 4 mapped');
    expect(query('kind-Account')!.textContent).toContain('information only');
    expect(component.tone(component.rows[0])).toBe('wn');
    expect(component.tone(component.rows[1])).toBe('er');
    expect(component.tone(component.rows[4])).toBe('info');
  });

  it('no GL accounts bar without the general ledger', async () => {
    await setup({ ...NOT_READY, accounts: { glAvailable: false, total: 0, mapped: 0, unmapped: 0, blocking: false } });
    expect(query('kind-Account')).toBeNull();
  });

  it('READY with nothing blocking; Refresh asks again', async () => {
    await setup({ ready: true, blockingIssues: [], warnings: [], entities: [{ kind: 'Vendor', total: 2, mapped: 2 }] });
    expect(query('readiness-tag')!.textContent!.trim()).toBe('READY');
    expect(query('blocking-issues')).toBeNull();
    query('refresh')!.querySelector('button')!.click();
    expect(service.getCoverage).toHaveBeenCalledTimes(2);
  });

  it('shows the error when coverage cannot be checked', async () => {
    await setup('fail');
    expect(query('load-failed')!.textContent).toContain('Down.');
  });
});
