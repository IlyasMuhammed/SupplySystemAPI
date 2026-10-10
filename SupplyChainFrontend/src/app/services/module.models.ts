// A37 — Module Registry & Enablement (docs/module-registry/API-CONTRACT.md §1). Shapes of api/tenant/modules.

/** GET /api/tenant/modules/enabled — codes usable right now. A module in its grace period is not listed (only in `grace`). */
export interface EnabledModules {
  modules: string[];
  features: string[];
  grace: { code: string; graceEndsAt: string }[];
}

export type ModuleStatus = 'ALWAYS_ON' | 'ACTIVE' | 'GRACE' | 'DISABLED' | 'NOT_LICENSED' | 'COMING_SOON';

export interface ModuleRef { code: string; name: string; isEnabled: boolean; }

export interface ModuleFeature {
  code: string;
  name: string;
  description?: string | null;
  isCore: boolean;
  isAvailable: boolean;
  isLicensed: boolean;
  isEnabled: boolean;
  requiresCode?: string | null;
  requiredBy: string[];
  /** BOM management, switched by the system with Manufacturing / Services (MOD-08). */
  autoManaged: boolean;
}

export interface ModuleCard {
  code: string;
  name: string;
  description?: string | null;
  icon?: string | null;
  isAlwaysOn: boolean;
  isAvailable: boolean;
  isLicensed: boolean;
  isEnabled: boolean;
  status: ModuleStatus;
  graceEndsAt?: string | null;
  disabledAt?: string | null;
  disabledByName?: string | null;
  enabledAt?: string | null;
  dependsOn: ModuleRef[];
  dependents: ModuleRef[];
  features: ModuleFeature[];
  featureCount: number;
  enabledFeatureCount: number;
  rowVersion: string;
}

export interface ModuleHistoryEntry {
  performedAt: string;
  action: string;
  featureCode?: string | null;
  performedByName: string;
  graceDays?: number | null;
  notes?: string | null;
}

export interface ModuleImpact {
  code: string;
  name: string;
  /** Enabled dependents — disable is refused while there are any. */
  dependents: { code: string; name: string }[];
  inProgress: { label: string; count: number }[];
  willBlock: string[];
  notAffected: string[];
  defaultGraceDays: number;
}

/** Body of a module 403 (API-CONTRACT §1.4). */
export const MODULE_NOT_LICENSED = 'MODULE_NOT_LICENSED';

/**
 * Display names of the catalog codes (tenant.FeatureDefinitions), for places that only have a code — the 403 toast and
 * the role editor's module groups. The Modules page itself shows the names the API sends.
 */
export const MODULE_NAMES: Record<string, string> = {
  MODULE_MASTER_DATA: 'Master Data',
  MODULE_WORKFLOW_ENGINE: 'Workflow Engine',
  MODULE_INVENTORY: 'Inventory',
  MODULE_FINANCE: 'Finance',
  MODULE_CUSTOMERS: 'Customers',
  MODULE_SUPPLIERS: 'Supplier Management',
  MODULE_DEMAND: 'Demand & Procurement',
  MODULE_PROCUREMENT: 'Procurement',
  MODULE_WAREHOUSE: 'Warehouse',
  MODULE_LOGISTICS: 'Logistics',
  MODULE_MANUFACTURING: 'Manufacturing',
  MODULE_SERVICES: 'Service Orders',
  MODULE_MIR: 'Material Issue & Projects',
  MODULE_REPORTS: 'Reports',
  MODULE_NOTIFICATIONS: 'Notifications',
  MODULE_INTEGRATION: 'QuickBooks Integration',
  FEATURE_BOM_MANAGEMENT: 'BOM Management',
  FEATURE_RFQ_MANAGEMENT: 'RFQ Management',
  FEATURE_PICK_LISTS: 'Pick Lists',
  FEATURE_QUALITY_INSPECTION: 'Quality Inspection',
  FEATURE_SHIPMENT_TRACKING: 'Shipment Tracking',
  FEATURE_PURCHASE_RETURNS: 'Purchase Returns',
  FEATURE_CREDIT_MANAGEMENT: 'Credit Management'
};

/** "MODULE_MANUFACTURING" → "Manufacturing"; unknown codes are humanised. */
export function moduleName(code: string | null | undefined): string {
  if (!code) return '';
  const known = MODULE_NAMES[code];
  if (known) return known;
  const s = code.replace(/^(MODULE|FEATURE|SCREEN)_/, '').replace(/_/g, ' ').toLowerCase();
  return s.charAt(0).toUpperCase() + s.slice(1);
}

/** Whole days from now until `iso` (never negative); null without a date. */
export function daysLeft(iso: string | null | undefined, now: Date = new Date()): number | null {
  if (!iso) return null;
  const ms = new Date(iso).getTime() - now.getTime();
  return Math.max(0, Math.ceil(ms / 86_400_000));
}
