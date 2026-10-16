import {
  bankAccountOptions, canonicalKey, completeSyncControl, coverageLabel, coveragePercent, coverageRows, defaultsRequest, issueTab, methodKey,
  overridesFrom, overridesRequest, refOptions, releaseCount, syncControlStatus, tabForArea, valuesByKey
} from './a44.shared';
import { SYNC_CONTROL_DOCUMENT_TYPES } from '../../../models/quickbooks-integration.models';
import { isHeldCode } from './quickbooks.shared';

describe('A44 QuickBooks helpers', () => {
  describe('sync control', () => {
    it('On / Held / Off from the two switches', () => {
      expect(syncControlStatus({ isEnabled: true, holdWhenDisabled: true })).toBe('On');
      expect(syncControlStatus({ isEnabled: false, holdWhenDisabled: true })).toBe('Held');
      expect(syncControlStatus({ isEnabled: false, holdWhenDisabled: false })).toBe('Off');
    });

    it('lists every document type in order; a type without a row is enabled (BR-29)', () => {
      const rows = completeSyncControl([{ documentType: 'possale' as any, isEnabled: false, holdWhenDisabled: true, heldCount: 4 }]);
      expect(rows.map(r => r.documentType)).toEqual([...SYNC_CONTROL_DOCUMENT_TYPES]);
      const pos = rows.find(r => r.documentType === 'POSSale')!;
      expect(pos.isEnabled).toBeFalse();
      expect(pos.heldCount).toBe(4);
      expect(rows.find(r => r.documentType === 'Customer')!.isEnabled).toBeTrue();
    });

    it('turning a held type on releases its held count; anything else releases nothing', () => {
      const held = { documentType: 'POSSale' as const, isEnabled: false, holdWhenDisabled: true, heldCount: 7 };
      expect(releaseCount(held, { isEnabled: true })).toBe(7);
      expect(releaseCount(held, { isEnabled: false })).toBe(0);
      expect(releaseCount({ ...held, isEnabled: true }, { isEnabled: true })).toBe(0);
    });

    it('recognises held codes', () => {
      expect(isHeldCode('POS_SALES_HELD')).toBeTrue();
      expect(isHeldCode('SYNC_HELD')).toBeTrue();
      expect(isHeldCode('held')).toBeTrue();
      expect(isHeldCode('HOLDER')).toBeFalse();
      expect(isHeldCode(null)).toBeFalse();
    });
  });

  describe('provider defaults', () => {
    it('method keys are "{key}.{METHOD}"', () => {
      expect(methodKey('DepositToAccountRef', 'cash')).toBe('DepositToAccountRef.CASH');
    });

    it('reads saved values by canonical key, blanks dropped', () => {
      const v = valuesByKey([
        { settingKey: 'deposittoaccountref.card', settingValue: '35' },
        { settingKey: 'ClassRef', settingValue: '  ' },
        { settingKey: 'SalesTermRef', settingValue: '3' }
      ]);
      expect(v['DepositToAccountRef.CARD']).toBe('35');
      expect(v['ClassRef']).toBeNull();
      expect(v['SalesTermRef']).toBe('3');
      expect(canonicalKey('bankaccountref.bank_transfer')).toBe('BankAccountRef.BANK_TRANSFER');
    });

    it('saves every shown key, blanks as null', () => {
      expect(defaultsRequest(['ClassRef', 'SalesTermRef'], { ClassRef: ' 5 ', SalesTermRef: '' })).toEqual([
        { settingKey: 'ClassRef', settingValue: '5' }, { settingKey: 'SalesTermRef', settingValue: null }
      ]);
    });

    it('overrides: drops incomplete rows, keeps the last per type and key', () => {
      const rows = overridesFrom([
        { documentType: 'POSSale', settingKey: 'classref', settingValue: '1' },
        { documentType: 'POSSale', settingKey: 'ClassRef', settingValue: '2' },
        { documentType: 'Payment', settingKey: 'SalesTermRef', settingValue: '' }
      ]);
      expect(rows[0].settingKey).toBe('ClassRef');
      expect(overridesRequest(rows)).toEqual([{ documentType: 'POSSale', settingKey: 'ClassRef', settingValue: '2' }]);
    });

    it('options: active items sorted, a saved id QuickBooks no longer lists keeps a label', () => {
      const opts = refOptions([
        { id: '2', name: 'Retail', active: true }, { id: '1', name: 'Online', active: true }, { id: '3', name: 'Old', active: false }
      ], ['3', '9']);
      expect(opts.map(o => o.value)).toEqual(['9', '3', '1', '2']);
      expect(opts[0].label).toContain('not found');
      expect(opts[1].label).toContain('inactive');
    });

    it('deposit / bank accounts: Bank, Other Current Asset, Credit Card only', () => {
      const opts = bankAccountOptions([
        { id: '1', name: 'Checking', type: 'Bank', active: true },
        { id: '2', name: 'Undeposited Funds', type: 'OtherCurrentAsset', active: true },
        { id: '3', name: 'Sales', type: 'Income', active: true },
        { id: '4', name: 'Visa', type: 'Credit Card', active: true }
      ]);
      expect(opts.map(o => o.value).sort()).toEqual(['1', '2', '4']);
      const accounts = [
        { id: '1', name: 'Checking', type: 'Bank', active: true },
        { id: '2', name: 'Undeposited Funds', type: 'OtherCurrentAsset', active: true },
        { id: '4', name: 'Visa', type: 'CreditCard', active: true }
      ];
      expect(bankAccountOptions(accounts, [], 'deposit').map(o => o.value).sort()).toEqual(['1', '2']);
      expect(bankAccountOptions(accounts, [], 'bank').map(o => o.value).sort()).toEqual(['1', '4']);
    });
  });

  describe('coverage', () => {
    it('percent of mapped + skipped; nothing to map is complete', () => {
      expect(coveragePercent({ total: 4, mapped: 1, skipped: 1 })).toBe(50);
      expect(coveragePercent({ total: 0, mapped: 0 })).toBe(100);
      expect(coveragePercent({ total: 3, mapped: 3, skipped: 2 })).toBe(100);
    });

    it('labels and the tab that fixes an area', () => {
      expect(coverageLabel({ kind: 'TaxCode', total: 1, mapped: 0 })).toBe('Tax codes');
      expect(coverageLabel({ kind: 'Customers', total: 1, mapped: 0 })).toBe('Customers');
      expect(coverageLabel({ kind: 'X', label: 'Own', total: 1, mapped: 0 })).toBe('Own');
      expect(tabForArea('Customer')).toBe('match');
      expect(tabForArea('TAX_CODE')).toBe('mappings');
      expect(tabForArea('Account')).toBe('accounts');
      expect(tabForArea('ProviderDefault')).toBe('defaults');
      expect(tabForArea('SyncControl')).toBe('sync-control');
      expect(tabForArea('anything else')).toBe('preflight');
      expect(issueTab({ code: 'DEPOSIT_ACCOUNT_MISSING', message: '' })).toBe('defaults');
      expect(issueTab({ code: 'X', message: '', area: 'Vendor' })).toBe('match');
    });

    it('the server\'s tab wins: setup → Preflight, tax → Mappings', () => {
      expect(issueTab({ code: 'PREFLIGHT', message: '', area: 'Preflight', tab: 'setup' })).toBe('preflight');
      expect(issueTab({ code: 'TAX_UNMAPPED', message: '', area: 'Customer', tab: 'tax' })).toBe('mappings');
      expect(issueTab({ code: 'X', message: '', area: 'Tax', tab: 'unknown' })).toBe('mappings');
    });

    it('bars: entities, then tax codes, provider defaults and GL accounts when there is a ledger', () => {
      const rows = coverageRows({
        ready: false, entities: [{ kind: 'Vendor', total: 2, mapped: 1 }], blockingIssues: [], warnings: [],
        taxCodes: { total: 3, mapped: 3, unmapped: [] },
        providerDefaults: { required: 2, set: 1, missing: [] },
        accounts: { glAvailable: true, total: 10, mapped: 4, unmapped: 6, blocking: false }
      });
      expect(rows.map(r => r.kind)).toEqual(['Vendor', 'TaxCode', 'ProviderDefault', 'Account']);
      expect(rows[3].informational).toBeTrue();
      expect(coverageRows(null)).toEqual([]);
    });
  });
});
