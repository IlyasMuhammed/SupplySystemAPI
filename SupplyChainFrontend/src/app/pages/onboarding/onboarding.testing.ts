// A50 — test fixtures shared by the onboarding specs (not shipped code paths).

const leaf = (code: string, name: string, root: string, extra: any = {}) => ({ code, name, root_type: root, level: 4, is_group: false, children: [] as any[], ...extra });
const grp = (code: string, name: string, root: string, level: number, children: any[]) => ({ code, name, root_type: root, level, is_group: true, children });

/** A small valid A42 chart: 1 › 11 › 111 › 1111 (DEFAULT_CASH), 1112; 1 › 11 › 112; 2. Seven accounts. */
export function sampleCoa(): any[] {
  return [grp('1', 'Assets', 'Asset', 1, [grp('11', 'Current', 'Asset', 2, [grp('111', 'Cash', 'Asset', 3, [
    leaf('1111', 'Cash in hand', 'Asset', { default_account_key: 'DEFAULT_CASH', is_default_account: true }), leaf('1112', 'Bank', 'Asset')
  ]), grp('112', 'Receivables', 'Asset', 3, [])])]), grp('2', 'Liabilities', 'Liability', 1, [])];
}

export function sampleLeaf(code: string, name: string, root: string, extra: any = {}): any { return leaf(code, name, root, extra); }

/** Preview rows as POST setup/preview returns them (PK Trading, trimmed). */
export function samplePreviews(): any[] {
  const row = (category: string, payload: any, extra: any = {}) => ({
    category, displayName: `PK Trading — ${category}`, status: 'Ready', setupDefaultUuid: 'u-' + category, version: 1,
    resolvedFrom: { country: 'PK', industry: 'Trading' }, payload, verified: null,
    preview: { category, inPayload: 1, alreadyPresent: 0, warnings: [] }, requiresModule: null, moduleEnabled: true, hasSeeder: true, errors: [], ...extra
  });
  return [
    row('CURRENCY', { base: { code: 'PKR', name: 'Pakistani Rupee', symbol: 'Rs' }, additional: [{ code: 'USD', name: 'US Dollar', symbol: '$' }] }),
    row('COA', { template_code: 'PK_GENERAL', accounts: sampleCoa() }, { requiresModule: 'FEATURE_GENERAL_LEDGER' }),
    row('TAX', { verified: false, taxCodes: [{ code: 'GST18', name: 'Sales Tax 18%', rate: 18, usage: 'SALES', isDefault: true }] },
      { verified: false, requiresModule: 'MODULE_FINANCE' }),
    row('FISCAL', { startMonth: 7, periodType: 'Monthly' }),
    row('ROLES', { roles: [
      { code: 'ORG_ADMIN', name: 'Organization Admin', permissions: ['ALL'], requiredModules: [] },
      { code: 'SALES_MANAGER', name: 'Sales Manager', permissions: [], requiredModules: ['MODULE_DEMAND'] },
      { code: 'WAREHOUSE_OPERATOR', name: 'Warehouse Operator', permissions: [], requiredModules: ['MODULE_WAREHOUSE'] }
    ] }),
    row('WAREHOUSE', { zoneTypes: [{ code: 'RECEIVING', name: 'Receiving', default: true }, { code: 'STORAGE', name: 'Storage', default: true },
      { code: 'QUALITY_HOLD', name: 'Quality Hold', default: false }], defaultWarehouse: { code: 'WH-MAIN', name: 'Main Warehouse' } },
      { requiresModule: 'MODULE_INVENTORY' }),
    row('DOC_NUMBERING', { sequences: [] }, { status: 'ModuleDisabled', moduleEnabled: false, requiresModule: 'MODULE_LOGISTICS' })
  ];
}
