import { of } from 'rxjs';
import { AccountTreeNode, AccountingService, SetupStatus } from '../../../services/accounting.service';

// A42 — fixtures shared by the GL specs (not imported by app code).

export function acc(code: string, level: number, isGroup: boolean, children: AccountTreeNode[] = [], extra: Partial<AccountTreeNode> = {}): AccountTreeNode {
  return {
    uuid: 'u' + code, accountCode: code, accountName: 'Name ' + code, materializedPath: code, level, rootType: 'Asset', isGroup,
    isSystemAccount: false, isActive: true, hasTransactions: false, openingBalanceDebit: 0, openingBalanceCredit: 0, currentBalance: 0,
    sortOrder: 0, children, ...extra
  };
}

/** 1 Assets › 11 › 111 › 1111 Cash, 1112 Meezan Bank;  2 Liabilities › 21 › 211 › 2111 Trade creditors */
export function sampleTree(): AccountTreeNode[] {
  return [
    acc('1', 1, true, [acc('11', 2, true, [acc('111', 3, true, [
      acc('1112', 4, false, [], { accountName: 'Meezan Bank', currentBalance: 500, accountType: 'Bank' }),
      acc('1111', 4, false, [], { accountName: 'Cash in hand', currentBalance: 250.5, accountType: 'Cash', hasTransactions: true })
    ])])], { isSystemAccount: true, accountName: 'Assets' }),
    acc('2', 1, true, [acc('21', 2, true, [acc('211', 3, true, [acc('2111', 4, false, [], { rootType: 'Liability', accountName: 'Trade creditors', accountType: 'Payable' })],
      { rootType: 'Liability' })], { rootType: 'Liability' })],
      { rootType: 'Liability', isSystemAccount: true, accountName: 'Liabilities' })
  ];
}

export function status(over: Partial<SetupStatus> = {}): SetupStatus {
  return {
    status: 'NotStarted', accountsCreatedCount: 0, fiscalYearConfigured: false, defaultsConfigured: false, openingBalancesEntered: false,
    openingBalancesBalanced: false, nextStep: 1,
    steps: [1, 2, 3, 4, 5, 6].map(step => ({ step, title: 'Step ' + step, done: false })),
    ...over
  };
}

/** A status whose first `n` steps are done. */
export function statusDone(n: number, over: Partial<SetupStatus> = {}): SetupStatus {
  const codes: SetupStatus['status'][] = ['NotStarted', 'AccountsCreated', 'AccountsCreated', 'FiscalYearSet', 'DefaultsSet', 'BalancesEntered', 'Complete'];
  return status({
    status: codes[n], accountsCreatedCount: n >= 1 ? 60 : 0, nextStep: Math.min(n + 1, 6),
    steps: [1, 2, 3, 4, 5, 6].map(step => ({ step, title: 'Step ' + step, done: step <= n })),
    ...over
  });
}

export const okRes = <T>(result: T) => of({ success: true, message: '', result });

/** An AccountingService double whose every call answers with an empty, successful result (override per test). */
export function fakeApi(): jasmine.SpyObj<AccountingService> {
  const api = jasmine.createSpyObj<AccountingService>('AccountingService', [
    'getTemplates', 'getTemplateFilters', 'getTemplate', 'previewTemplate', 'downloadTemplate', 'downloadBlankTemplate',
    'getChart', 'getAccounts', 'getPostableAccounts', 'getAccount', 'suggestCode', 'createAccount', 'updateAccount', 'deleteAccount',
    'applyTemplate', 'startBlankChart', 'uploadChart', 'exportChart', 'getFiscalYears', 'createFiscalYear', 'updateFiscalYear', 'closeFiscalYear', 'getPeriods',
    'updatePeriod', 'getDefaults', 'setDefault', 'saveDefaults', 'validateDefaults', 'getOpeningBalances', 'saveOpeningBalances',
    'postOpeningBalances', 'getOpeningSummary', 'getSetupStatus', 'completeSetup', 'resetSetup', 'getSetupHistory', 'ensureSetupState',
    'getDashboard', 'getMappings', 'createMapping', 'updateMapping', 'deleteMapping', 'importMappings', 'exportMappings'
  ]);
  api.getTemplates.and.returnValue(okRes([]));
  api.getTemplateFilters.and.returnValue(okRes({ countries: [], businessTypes: [] }));
  api.getChart.and.returnValue(of({ roots: [], summary: { totalAccounts: 0, postableAccounts: 0, lastModifiedAt: null } }));
  api.getAccounts.and.returnValue(okRes([]));
  api.getPostableAccounts.and.returnValue(of([]));
  api.getFiscalYears.and.returnValue(okRes([]));
  api.getDefaults.and.returnValue(okRes({ categories: [], requiredCount: 0, assignedRequiredCount: 0 }));
  api.getOpeningBalances.and.returnValue(okRes({
    conversionDate: '2026-07-01', isPosted: false, groups: [],
    summary: { totalDebits: 0, totalCredits: 0, difference: 0, isBalanced: true },
    subledger: { customerOpeningTotal: 0, supplierOpeningTotal: 0, openingStockValue: 0, currencyCode: 'PKR' }
  }));
  api.getOpeningSummary.and.returnValue(okRes({ totalDebits: 0, totalCredits: 0, difference: 0, isBalanced: true }));
  api.getSetupStatus.and.returnValue(okRes(status()));
  api.getSetupHistory.and.returnValue(okRes([]));
  api.getMappings.and.returnValue(okRes([]));
  return api;
}

export function fakeAuth(perms: string[]) {
  return {
    perms,
    hasPermission(p: string) { return this.perms.includes(p); },
    hasAnyPermission(...p: string[]) { return p.some(x => this.perms.includes(x)); },
    isAuthenticated: () => true
  };
}
