import { TreeNode } from 'primeng/api';
import { setupErrorMessage } from '../finance-setup/finance-setup.shared';

// A50 — what the setup wizard, the Default Data dashboard and the super admin Setup Defaults pages share:
// permission codes, category labels / links, payload helpers (counts, the COA tree) and the wizard's steps.
// Payload shapes: docs/onboarding/PAYLOAD-SCHEMAS.md. Contract: docs/onboarding/API-CONTRACT.md (D-CORE).

export const ONBOARDING_ROUTE = '/portal/pages/onboarding';
export const SETUP_WIZARD_ROUTE = `${ONBOARDING_ROUTE}/setup`;
export const DEFAULT_DATA_ROUTE = `${ONBOARDING_ROUTE}/default-data`;
export const SETUP_DEFAULTS_ROUTE = '/portal/pages/system/setup-defaults';

/** D-8 — wizard, Default Data dashboard and resets. */
export const SETUP_WIZARD_RUN = 'SETUP_WIZARD_RUN';
export const PLATFORM_SUPER_ADMIN = 'PLATFORM_SUPER_ADMIN';

/** SetupCategories.ApplyOrder (D-10). */
export const SETUP_CATEGORIES = [
  'CURRENCY', 'COA', 'TAX', 'FISCAL', 'PAYMENT_TERMS', 'UOM', 'PRODUCT_CATEGORIES', 'DOC_NUMBERING', 'APPROVAL_RULES', 'ROLES',
  'WAREHOUSE', 'DOC_TEMPLATES', 'EMAIL_TEMPLATES', 'HRM'
] as const;

export interface CategoryInfo { label: string; viewRoute: string | null; viewLabel?: string; }

/** Label and the existing screen that maintains each category (task 149). */
export const CATEGORY_INFO: Record<string, CategoryInfo> = {
  CURRENCY:           { label: 'Currencies',          viewRoute: '/portal/pages/finance-setup/currencies' },
  COA:                { label: 'Chart of accounts',   viewRoute: '/portal/pages/finance/gl/accounts' },
  TAX:                { label: 'Tax codes',           viewRoute: '/portal/pages/finance-setup/tax-codes' },
  FISCAL:             { label: 'Fiscal year',         viewRoute: '/portal/pages/finance/gl/fiscal-years' },
  PAYMENT_TERMS:      { label: 'Payment terms',       viewRoute: '/portal/pages/finance/ap/settings', viewLabel: 'AR & AP settings' },
  UOM:                { label: 'Units of measure',    viewRoute: '/portal/pages/lookup-values/lookup-values-list', viewLabel: 'Lookup values' },
  PRODUCT_CATEGORIES: { label: 'Product categories',  viewRoute: '/portal/pages/inventory/categories' },
  DOC_NUMBERING:      { label: 'Document numbering',  viewRoute: null },
  APPROVAL_RULES:     { label: 'Approval rules',      viewRoute: '/portal/pages/workflow-admin/workflows', viewLabel: 'Workflows' },
  ROLES:              { label: 'Roles',               viewRoute: '/portal/pages/admin/roles' },
  WAREHOUSE:          { label: 'Warehouses & zones',  viewRoute: '/portal/pages/inventory/warehouses' },
  DOC_TEMPLATES:      { label: 'Document templates',  viewRoute: `${ONBOARDING_ROUTE}/document-templates` },
  EMAIL_TEMPLATES:    { label: 'Email templates',     viewRoute: `${ONBOARDING_ROUTE}/email-templates` },
  HRM:                { label: 'HR defaults',         viewRoute: '/portal/pages/hrm/salary/components', viewLabel: 'Salary components' }
};

export function categoryLabel(code: string | null | undefined): string {
  if (!code) return '';
  return CATEGORY_INFO[code]?.label ?? code.replace(/_/g, ' ').toLowerCase().replace(/^./, c => c.toUpperCase());
}

export const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December']
  .map((label, i) => ({ label, value: i + 1 }));

export function setupStatusLabel(s: string | null | undefined): string {
  switch (s) {
    case 'NotStarted': return 'Not started';
    case 'InProgress': return 'In progress';
    case 'Completed': return 'Completed';
    case 'Skipped': return 'Skipped';
    default: return s ?? '';
  }
}

export function setupStatusTone(s: string | null | undefined): string {
  return s === 'Completed' ? 'ok' : s === 'Skipped' ? 'wn' : s === 'InProgress' ? 'in' : '';
}

/** Result row / preview status → pill tone. */
export function resultTone(status: string | null | undefined): string {
  switch (status) {
    case 'Applied': case 'Ready': return 'ok';
    case 'Failed': case 'Invalid': return 'er';
    case 'NotRun': case 'ModuleDisabled': case 'NoDefaults': case 'NoSeeder': return 'wn';
    default: return '';
  }
}

export function resultLabel(status: string | null | undefined): string {
  switch (status) {
    case 'ModuleDisabled': return 'Module off';
    case 'NoDefaults': return 'No defaults';
    case 'NoSeeder': return 'Not available';
    case 'NotRun': return 'Not run';
    default: return status ?? '';
  }
}

/** The server's own words (message), else a plain fallback per status. */
export function onboardingError(err: unknown, fallback: string): string {
  return setupErrorMessage(err, fallback);
}

/** The `result` of an error envelope (e.g. the 409 reference block, the import result). */
export function errorResult<T>(err: unknown): T | null {
  const body = (err as { error?: any } | null)?.error;
  return (body && typeof body === 'object' && body.result) ? body.result as T : null;
}

export function clone<T>(v: T): T {
  return v === undefined || v === null ? v : JSON.parse(JSON.stringify(v));
}

// ── Payload counts (Review, super admin list preview) ────────────────────────

function arr(v: any): any[] { return Array.isArray(v) ? v : []; }

function countTree(nodes: any[], childKey = 'children'): number {
  return arr(nodes).reduce((n, x) => n + 1 + countTree(x?.[childKey], childKey), 0);
}

/** How many records a payload holds, per category (FSD §5 / PAYLOAD-SCHEMAS). */
export function payloadCount(category: string, payload: any): number {
  if (!payload || typeof payload !== 'object') return 0;
  switch (category) {
    case 'COA': return countTree(payload.accounts);
    case 'TAX': return arr(payload.taxCodes).length;
    case 'CURRENCY': return (payload.base ? 1 : 0) + arr(payload.additional).length;
    case 'FISCAL': return payload.startMonth ? 1 : 0;
    case 'PAYMENT_TERMS': return arr(payload.terms).length;
    case 'UOM': return arr(payload.groups).reduce((n, g) => n + arr(g?.units).length, 0);
    case 'PRODUCT_CATEGORIES': return countTree(payload.categories);
    case 'DOC_NUMBERING': return arr(payload.sequences).length;
    case 'APPROVAL_RULES': return arr(payload.rules).reduce((n, r) => n + arr(r?.levels).length, 0);
    case 'ROLES': return arr(payload.roles).length;
    case 'WAREHOUSE': return arr(payload.zoneTypes).length;
    case 'DOC_TEMPLATES': case 'EMAIL_TEMPLATES': return arr(payload.templates).length;
    case 'HRM': return arr(payload.leaveTypes).length + arr(payload.salaryComponents).length + arr(payload.taxSlabTables).length;
    default:
      return Object.values(payload).filter(Array.isArray).reduce((n: number, a: any) => n + countTree(a), 0);
  }
}

// ── COA tree (A42 template format, snake_case; PAYLOAD-SCHEMAS "COA") ────────

export interface CoaAccount {
  code: string;
  name: string;
  root_type: string;
  level: number;
  is_group: boolean;
  account_type?: string | null;
  description?: string | null;
  is_default_account?: boolean;
  default_account_key?: string | null;
  children?: CoaAccount[];
  [k: string]: any;
}

export const COA_MAX_LEVEL = 4;
export const COA_ROOT_DIGIT: Record<string, string> = { Asset: '1', Liability: '2', Equity: '3', Revenue: '4', Expense: '5' };

/** Account payload → p-tree nodes (data = the account without its children; children carried by the node). */
export function coaToTree(accounts: CoaAccount[] | null | undefined, expandToLevel = 1): TreeNode<CoaAccount>[] {
  const build = (a: CoaAccount): TreeNode<CoaAccount> => {
    const { children, ...rest } = a;
    const node: TreeNode<CoaAccount> = {
      key: a.code,
      label: `${a.code} ${a.name}`,
      data: { ...rest } as CoaAccount,
      expanded: (a.level ?? 1) <= expandToLevel,
      draggable: (a.level ?? 1) > 1,
      droppable: !!a.is_group,
      leaf: !a.is_group,
      children: arr(children).map(build)
    };
    node.children!.forEach(c => (c.parent = node));
    return node;
  };
  return arr(accounts).map(build);
}

/** p-tree nodes → account payload, re-deriving each level from its depth (the tree is the source while editing). */
export function treeToCoa(nodes: TreeNode<CoaAccount>[], depth = 1): CoaAccount[] {
  return arr(nodes).map(n => ({
    ...(n.data as CoaAccount),
    level: depth,
    children: treeToCoa(n.children ?? [], depth + 1)
  }));
}

export function allCoa(accounts: CoaAccount[]): CoaAccount[] {
  const out: CoaAccount[] = [];
  const walk = (list: CoaAccount[]) => arr(list).forEach(a => { out.push(a); walk(a.children ?? []); });
  walk(accounts);
  return out;
}

/**
 * The first A42 rule the chart breaks (null = fine): levels 1–3 groups / 4 leaves, codes unique and starting with the
 * root digit, root type inherited from the root, default keys used once and only on leaves.
 */
export function coaProblem(accounts: CoaAccount[]): string | null {
  const seen = new Set<string>();
  const keys = new Set<string>();
  const check = (list: CoaAccount[], depth: number, root: CoaAccount | null): string | null => {
    for (const a of arr(list)) {
      const code = (a.code ?? '').trim();
      if (!code) return 'Every account needs a code.';
      if (!/^[A-Za-z0-9]{1,20}$/.test(code)) return `Account code ${code} may contain only letters and digits (at most 20).`;
      if (!(a.name ?? '').trim()) return `Account ${code} needs a name.`;
      if (seen.has(code.toLowerCase())) return `Account code ${code} is used twice.`;
      seen.add(code.toLowerCase());
      if (depth > COA_MAX_LEVEL) return `Account ${code} is deeper than level ${COA_MAX_LEVEL}.`;
      if (a.is_group && depth === COA_MAX_LEVEL) return `Level ${COA_MAX_LEVEL} accounts are postable — ${code} cannot be a group there.`;
      if (!a.is_group && depth !== COA_MAX_LEVEL) return `Postable account ${code} must sit at level ${COA_MAX_LEVEL} (under a level ${COA_MAX_LEVEL - 1} group).`;
      const r = root ?? a;
      const digit = COA_ROOT_DIGIT[r.root_type];
      if (digit && !code.startsWith(digit)) return `${r.root_type} account codes must start with ${digit} (${code}).`;
      if (root && a.root_type && a.root_type !== root.root_type) return `Account ${code} must be of type ${root.root_type}, like its root.`;
      const kids = arr(a.children);
      if (!a.is_group && kids.length) return `Account ${code} is a postable account and cannot hold other accounts.`;
      if (a.default_account_key) {
        if (a.is_group) return `Role ${a.default_account_key} must be on a postable account, not on group ${code}.`;
        if (keys.has(a.default_account_key)) return `Role ${a.default_account_key} is assigned twice.`;
        keys.add(a.default_account_key);
      }
      const p = check(kids, depth + 1, r);
      if (p) return p;
    }
    return null;
  };
  return check(accounts, 1, null);
}

/** Suggests the next free code under a parent: the last sibling code + 1, else the parent code + "1". */
export function suggestChildCode(parent: CoaAccount, siblings: CoaAccount[], existing: Iterable<string>): string {
  const taken = new Set([...existing].map(c => c.toLowerCase()));
  const numeric = arr(siblings).map(s => s.code).filter(c => /^\d+$/.test(c) && c.startsWith(parent.code));
  let candidate = numeric.length ? String(Math.max(...numeric.map(Number)) + 1) : `${parent.code}1`;
  let guard = 0;
  while (taken.has(candidate.toLowerCase()) && guard++ < 1000) candidate = /^\d+$/.test(candidate) ? String(Number(candidate) + 1) : candidate + '1';
  return candidate;
}

/** Every default role key in a subtree (to refuse removing an account that carries one). */
export function coaRoleKeys(node: TreeNode<CoaAccount>): string[] {
  const out: string[] = [];
  const walk = (n: TreeNode<CoaAccount>) => {
    if (n.data?.default_account_key) out.push(n.data.default_account_key);
    (n.children ?? []).forEach(walk);
  };
  walk(node);
  return out;
}

// ── Tax payload (PAYLOAD-SCHEMAS "TAX") ──────────────────────────────────────

export interface TaxRow { code: string; name: string; rate: number; usage: 'SALES' | 'PURCHASE' | 'BOTH'; type?: string; isDefault?: boolean; description?: string; [k: string]: any; }

export const TAX_USAGES = [
  { label: 'Sales', value: 'SALES' }, { label: 'Purchases', value: 'PURCHASE' }, { label: 'Sales & purchases', value: 'BOTH' }
];
export const TAX_TYPES = ['Output', 'Input', 'Withholding', 'Excise', 'Exempt', 'ZeroRated', 'None'];

export function taxProblem(rows: TaxRow[]): string | null {
  const seen = new Set<string>();
  const defaults: Record<string, number> = {};
  for (const r of arr(rows)) {
    const code = (r.code ?? '').trim().toUpperCase();
    if (!code) return 'Every tax code needs a code.';
    if (code.length > 20) return `Tax code ${code} is longer than 20 characters.`;
    if (seen.has(code)) return `Tax code ${code} is used twice.`;
    seen.add(code);
    if (!(r.name ?? '').trim()) return `Tax code ${code} needs a name.`;
    const rate = Number(r.rate);
    if (r.rate === null || r.rate === undefined || isNaN(rate) || rate < 0 || rate > 100) return `The rate of ${code} must be between 0 and 100.`;
    if (!['SALES', 'PURCHASE', 'BOTH'].includes(r.usage)) return `Choose where ${code} is used.`;
    if (r.isDefault) {
      defaults[r.usage] = (defaults[r.usage] ?? 0) + 1;
      if (defaults[r.usage] > 1) return `Only one default tax code per usage (${r.usage}).`;
    }
  }
  return null;
}

// ── Wizard ───────────────────────────────────────────────────────────────────

export interface WizardStep { step: number; title: string; short: string; categories: string[]; }

export const WIZARD_STEPS: WizardStep[] = [
  { step: 1, title: 'Company and industry', short: 'Company', categories: [] },
  { step: 2, title: 'Modules', short: 'Modules', categories: [] },
  { step: 3, title: 'Chart of accounts', short: 'Accounts', categories: ['COA'] },
  { step: 4, title: 'Tax codes', short: 'Tax', categories: ['TAX'] },
  { step: 5, title: 'Currency and fiscal year', short: 'Currency', categories: ['CURRENCY', 'FISCAL'] },
  { step: 6, title: 'Warehouses', short: 'Warehouses', categories: ['WAREHOUSE'] },
  { step: 7, title: 'Users and roles', short: 'Team', categories: ['ROLES'] },
  { step: 8, title: 'Review and launch', short: 'Review', categories: [] }
];
export const LAST_STEP = WIZARD_STEPS.length;

export interface WarehouseInput { code: string; name: string; address?: string; city?: string; zones: string[]; }
export interface InviteRow { email: string; firstName?: string; lastName?: string; roleRef: string; }

const EMAIL_RE = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

export function warehouseProblem(rows: WarehouseInput[]): string | null {
  const seen = new Set<string>();
  for (const w of arr(rows)) {
    const code = (w.code ?? '').trim().toUpperCase();
    if (!code) return 'Every warehouse needs a code.';
    if (!/^[A-Za-z0-9_-]{1,20}$/.test(code)) return `Warehouse code ${code} may contain letters, digits, - and _ (at most 20).`;
    if (seen.has(code)) return `Warehouse code ${code} is used twice.`;
    seen.add(code);
    if (!(w.name ?? '').trim()) return `Warehouse ${code} needs a name.`;
  }
  return null;
}

export function inviteProblem(rows: InviteRow[]): string | null {
  const seen = new Set<string>();
  for (const r of arr(rows)) {
    const email = (r.email ?? '').trim().toLowerCase();
    if (!email) return 'Enter an email address on every invite row (or remove the row).';
    if (!EMAIL_RE.test(email)) return `${r.email} is not a valid email address.`;
    if (seen.has(email)) return `${r.email} is invited twice.`;
    seen.add(email);
    if (!r.roleRef) return `Choose a role for ${r.email}.`;
  }
  return null;
}

/** A pack role is offered when every module it requires is enabled (task 136). */
export function roleAvailable(role: { requiredModules?: string[] | null }, isEnabled: (code: string) => boolean): boolean {
  return arr(role?.requiredModules).every((m: string) => isEnabled(m));
}

/** Triggers a browser download of a Blob. */
export function saveBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = fileName;
  a.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
