import {
  CONNECT_ERROR_MESSAGES, DraftDecision, accountOptions, buildDecisions, companyChecksPassed, connectErrorMessage,
  currentStep, linkAllExact, liveBlockers, mergeTermRows, prettyJson, setupComplete, setupSteps, sourceRoute,
  stateLabel, stateSeverity, stateTagClass, suggestTermsByDays, taxCodeOptions, unlockedTabs
} from './quickbooks.shared';
import {
  ConnectionStatusModel, IntegrationSettingsModel, MatchCandidateModel, PreflightResultModel, ReferenceItemModel,
  SyncSummaryModel
} from '../../../models/quickbooks-integration.models';

function connection(overrides: Partial<ConnectionStatusModel> = {}): ConnectionStatusModel {
  return {
    appConfigured: true, status: 'NeedsSetup', isConnected: true, environment: 'Sandbox', reconnectSoon: false, mode: 'DryRun',
    ...overrides
  };
}

function settings(overrides: Partial<IntegrationSettingsModel> = {}): IntegrationSettingsModel {
  return {
    mode: 'DryRun', autoPushCustomers: true, autoPushVendors: true, autoPushItems: true, autoPushSalesInvoices: true,
    autoPushBills: true, itemTypeDefault: 'NonInventory', partnerScope: 'OnlyWhenReferenced', ...overrides
  };
}

const PASSING: PreflightResultModel = {
  passed: true, canGoLive: false,
  checks: [{ code: 'HOME_CURRENCY', title: 'Home currency', status: 'Pass', message: 'PKR' }]
};

function summary(totals: Partial<Record<string, number>>): SyncSummaryModel {
  return {
    connectionStatus: 'NeedsSetup', mode: 'DryRun', queueDepth: 0,
    kinds: Object.entries(totals).map(([kind, total]) => ({ kind: kind as any, total: total!, countsByState: {} }))
  };
}

function candidate(overrides: Partial<MatchCandidateModel>): MatchCandidateModel {
  return {
    id: 'c', kind: 'Customer', externalId: 'e', sourceSystem: 'SCM', localLabel: 'Acme', confidence: 'None',
    decision: 'Pending', remoteId: null, ...overrides
  };
}

describe('QuickBooks shared helpers', () => {

  describe('sync state display', () => {
    it('colours each state as specified', () => {
      expect(stateSeverity('Synced')).toBe('success');
      expect(stateSeverity('Pending')).toBe('info');
      expect(stateSeverity('InProgress')).toBe('info');
      expect(stateTagClass('DryRunOk')).toBe('qbo-tone-teal');
      expect(stateSeverity('WaitingOnDependency')).toBe('warn');
      for (const s of ['Blocked', 'Failed', 'NeedsResolution']) expect(stateSeverity(s)).toBe('danger');
      expect(stateSeverity('NotSynced')).toBe('secondary');
      expect(stateSeverity('Voided')).toBe('secondary');
      expect(stateSeverity('SomethingNew')).toBe('secondary');
    });

    it('labels states in words and keeps an unknown one as it came', () => {
      expect(stateLabel('DryRunOk')).toBe('Dry run OK');
      expect(stateLabel('NeedsResolution')).toBe('Needs resolution');
      expect(stateLabel('Brand new')).toBe('Brand new');
    });
  });

  describe('connect errors', () => {
    it('has a sentence for every reason the callback can return', () => {
      for (const reason of ['state_invalid', 'state_expired', 'state_used', 'access_denied', 'realm_mismatch', 'realm_in_use', 'exchange_failed', 'not_configured']) {
        expect(connectErrorMessage(reason)).toBe(CONNECT_ERROR_MESSAGES[reason as keyof typeof CONNECT_ERROR_MESSAGES]);
      }
      expect(connectErrorMessage('realm_mismatch')).toContain('different QuickBooks company');
      expect(connectErrorMessage('whatever')).toContain('did not complete');
      expect(connectErrorMessage(null)).toContain('did not complete');
    });
  });

  describe('preflight', () => {
    it('counts only company checks towards "Preflight passed"', () => {
      const result: PreflightResultModel = {
        passed: false, canGoLive: false,
        checks: [
          { code: 'HOME_CURRENCY', title: 'Home currency', status: 'Pass', message: '' },
          { code: 'TAX_CODES', title: 'Tax', status: 'Warn', message: '' },
          { code: 'ACCOUNTS_MAPPED', title: 'Accounts', status: 'Fail', message: '' },
          { code: 'MATCHING_CONFIRMED', title: 'Matching', status: 'Fail', message: '' }
        ]
      };
      expect(companyChecksPassed(result)).toBeTrue();

      result.checks.push({ code: 'COUNTRY', title: 'Country', status: 'Fail', message: 'US companies are not supported.' });
      expect(companyChecksPassed(result)).toBeFalse();
      expect(companyChecksPassed(null)).toBeFalse();
      expect(companyChecksPassed({ passed: false, canGoLive: false, checks: [] })).toBeFalse();
    });

    it('explains why Live is off', () => {
      const result: PreflightResultModel = {
        passed: false, canGoLive: false,
        checks: [{ code: 'CUSTOM_TXN_NUMBERS', title: 'Custom transaction numbers', status: 'Fail', message: 'Turn them on.' }]
      };
      const reasons = liveBlockers(result, settings());
      expect(reasons).toContain('Custom transaction numbers: Turn them on.');
      expect(reasons.some(r => r.includes('Matching has not been marked complete'))).toBeTrue();

      expect(liveBlockers({ ...PASSING, canGoLive: true }, settings())).toEqual([]);
      expect(liveBlockers(PASSING, settings({ matchingConfirmedAt: '2026-09-30' }))).toEqual(['The preflight checks have not passed yet.']);
    });
  });

  describe('setup checklist', () => {
    it('walks the six steps in order', () => {
      let steps = setupSteps({ connection: connection({ isConnected: false, status: 'NotConnected' }), preflight: null, settings: null, summary: null });
      expect(steps.map(s => s.key)).toEqual(['connected', 'preflight', 'mappings', 'initialSync', 'matching', 'live']);
      expect(steps.every(s => !s.done)).toBeTrue();
      expect(currentStep(steps)!.key).toBe('connected');

      steps = setupSteps({
        connection: connection(), preflight: PASSING,
        settings: settings({ defaultIncomeAccountId: '1', defaultExpenseAccountId: '2' }),
        summary: summary({ Customer: 4, SalesInvoice: 0 })
      });
      expect(steps.filter(s => s.done).map(s => s.key)).toEqual(['connected', 'preflight', 'mappings', 'initialSync']);
      expect(currentStep(steps)!.key).toBe('matching');
      expect(setupComplete(steps)).toBeFalse();
    });

    it('needs both default accounts for "Mappings saved", and customers, vendors or items for "Initial sync loaded"', () => {
      const steps = setupSteps({
        connection: connection(), preflight: PASSING,
        settings: settings({ defaultIncomeAccountId: '1' }), summary: summary({ SalesInvoice: 9 })
      });
      expect(steps.find(s => s.key === 'mappings')!.done).toBeFalse();
      expect(steps.find(s => s.key === 'initialSync')!.done).toBeFalse();
    });

    it('opens tabs in order, keeps reached tabs open, and opens all once Live', () => {
      const none = setupSteps({ connection: connection({ isConnected: false, status: 'NotConnected' }), preflight: null, settings: null, summary: null });
      expect([...unlockedTabs(none, false)]).toEqual(['connection']);
      expect(unlockedTabs(none, true).has('api-clients')).toBeTrue();

      const connected = setupSteps({ connection: connection(), preflight: null, settings: null, summary: null });
      expect([...unlockedTabs(connected, false)].sort()).toEqual(['connection', 'preflight']);

      // Preflight later fails, but mappings were saved: the tabs already reached stay open.
      const regressed = setupSteps({
        connection: connection(), preflight: { passed: false, canGoLive: false, checks: [{ code: 'COUNTRY', title: '', status: 'Fail', message: '' }] },
        settings: settings({ defaultIncomeAccountId: '1', defaultExpenseAccountId: '2' }), summary: null
      });
      const open = unlockedTabs(regressed, false);
      expect(open.has('mappings')).toBeTrue();
      expect(open.has('initial-sync')).toBeTrue();
      expect(open.has('match')).toBeFalse();

      const live = setupSteps({ connection: connection({ status: 'Revoked', isConnected: false }), preflight: null, settings: settings({ mode: 'Live' }), summary: null });
      expect(setupComplete(live)).toBeTrue();
      expect([...unlockedTabs(live, false)].sort()).toEqual(['connection', 'initial-sync', 'mappings', 'match', 'preflight', 'sync']);
    });
  });

  describe('dropdown options', () => {
    const accounts: ReferenceItemModel[] = [
      { id: '1', name: 'Sales', type: 'Income', active: true },
      { id: '2', name: 'Other income', type: 'Other Income', active: true },
      { id: '3', name: 'Purchases', type: 'Cost of Goods Sold', active: true },
      { id: '4', name: 'Freight', type: 'Expense', active: true },
      { id: '5', name: 'Bank', type: 'Bank', active: true },
      { id: '6', name: 'Old sales', type: 'Income', active: false },
      { id: '7', name: 'Materials', type: 'CostOfGoodsSold', active: true }
    ];

    it('offers only active accounts of the suitable type', () => {
      expect(accountOptions(accounts, 'income').map(o => o.value)).toEqual(['2', '1']);
      expect(accountOptions(accounts, 'expense').map(o => o.value).sort()).toEqual(['3', '4', '7']);
      expect(accountOptions(accounts, 'discount').map(o => o.value).sort()).toEqual(['1', '2', '4']);
    });

    it('keeps a saved account that no longer qualifies, so the box is not empty', () => {
      const opts = accountOptions(accounts, 'income', '6');
      expect(opts[0]).toEqual({ label: 'Old sales (inactive)', value: '6' });
      expect(accountOptions(accounts, 'income', '99')[0].label).toContain('not found');
    });

    it('labels tax codes with their rate', () => {
      const opts = taxCodeOptions([{ id: 't', name: 'GST', rate: 17, active: true }]);
      expect(opts[0].label).toBe('GST (17%)');
    });
  });

  describe('payment terms', () => {
    it('lists every SCM term with its mapping, and keeps mappings whose term was deleted', () => {
      const rows = mergeTermRows(
        [{ id: 'AAA', name: 'Net 30', days: 30 }, { id: 'bbb', name: 'Cash', days: 0 }],
        [
          { paymentTermExternalId: 'aaa', qboTermId: '3' },
          { paymentTermExternalId: 'zzz', paymentTermName: 'Net 90', qboTermId: '9' }
        ]);
      expect(rows.map(r => [r.paymentTermName, r.qboTermId, r.orphan])).toEqual([
        ['Cash', null, false], ['Net 30', '3', false], ['Net 90', '9', true]
      ]);
    });

    it('suggests a QuickBooks term only when exactly one has the same due days', () => {
      const rows = mergeTermRows([{ id: 'a', name: 'Net 30', days: 30 }, { id: 'b', name: 'Net 15', days: 15 }], []);
      const filled = suggestTermsByDays(rows, [
        { id: 'q30', name: 'Net 30', days: 30, active: true },
        { id: 'q15a', name: 'Net 15', days: 15, active: true },
        { id: 'q15b', name: '15 days', days: 15, active: true }
      ]);
      expect(filled).toBe(1);
      expect(rows.find(r => r.paymentTermExternalId === 'a')!.qboTermId).toBe('q30');
      expect(rows.find(r => r.paymentTermExternalId === 'b')!.qboTermId).toBeNull();
    });
  });

  describe('matching decisions', () => {
    const candidates = [
      candidate({ id: 'm1', confidence: 'Exact', remoteId: '58' }),
      candidate({ id: 'm2', confidence: 'Exact', remoteId: null }),
      candidate({ id: 'm3', confidence: 'Probable', remoteId: '61' }),
      candidate({ id: 'm4', confidence: 'Exact', remoteId: '70', decision: 'Link' }),
      candidate({ id: 'm5', confidence: 'None' })
    ];

    it('"Link all exact matches" sets only exact matches that have a QuickBooks record', () => {
      const drafts = new Map<string, DraftDecision>();
      const changed = linkAllExact(candidates, drafts);
      expect(changed).toBe(1);   // m4 already linked on the server
      expect([...drafts.keys()].sort()).toEqual(['m1', 'm4']);
      expect(drafts.get('m1')).toEqual({ decision: 'Link', remoteId: '58' });
      expect(buildDecisions(candidates, drafts)).toEqual([{ candidateId: 'm1', decision: 'Link', remoteId: '58' }]);
    });

    it('sends only changed decisions, with an id for Link and none otherwise', () => {
      const drafts = new Map<string, DraftDecision>([
        ['m3', { decision: 'CreateNew', remoteId: '61' }],
        ['m4', { decision: 'Link', remoteId: '70' }],     // unchanged
        ['m5', { decision: 'Skip', remoteId: null }],
        ['m2', { decision: 'Link', remoteId: null }]      // nothing to link to
      ]);
      expect(buildDecisions(candidates, drafts)).toEqual([
        { candidateId: 'm3', decision: 'CreateNew', remoteId: null },
        { candidateId: 'm5', decision: 'Skip', remoteId: null }
      ]);
    });
  });

  describe('links to SCM', () => {
    it('opens the record SCM sent', () => {
      expect(sourceRoute('Customer', 'p1', 'SCM')).toEqual(['/portal/pages/suppliers/partner-detail', 'p1']);
      expect(sourceRoute('Vendor', 'p1', 'scm')).toEqual(['/portal/pages/suppliers/partner-detail', 'p1']);
      expect(sourceRoute('SalesInvoice', 's1', 'SCM')).toEqual(['/portal/pages/finance/sales-invoices', 's1']);
      expect(sourceRoute('Bill', 'b1', 'SCM')).toEqual(['/portal/pages/finance/invoices', 'b1']);
      expect(sourceRoute('Item', 'v1', 'SCM')).toEqual(['/portal/pages/inventory/products']);
      expect(sourceRoute('Customer', 'x', 'Shop POS')).toBeNull();
    });
  });

  it('pretty-prints JSON and leaves anything else alone', () => {
    expect(prettyJson('{"a":1}')).toBe('{\n  "a": 1\n}');
    expect(prettyJson('not json')).toBe('not json');
    expect(prettyJson(null)).toBe('');
  });
});
