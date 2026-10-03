// Pure helpers for the QuickBooks Integration page and the sync badge: labels, colours, the setup
// checklist, option lists and the small transformations the tabs share. No Angular, no I/O.

import {
  ConnectErrorReason, ConnectionStatusModel, IntegrationSettingsModel, MatchCandidateModel, MatchConfidence,
  MatchDecisionChoice, MatchDecisionItem, PreflightCheckModel, PreflightResultModel, ReferenceItemModel, SyncKind,
  SyncState, SyncSummaryModel, TermMappingModel
} from '../../../models/quickbooks-integration.models';
import { TagSeverity } from '../../../shared/components/qbo-sync-badge/qbo-sync-state';

// State colours and labels are shared with the sync badge, so they live beside it.
export * from '../../../shared/components/qbo-sync-badge/qbo-sync-state';

// ── Record kinds ─────────────────────────────────────────────────────────────────────────────────

export const KIND_PLURAL: Record<SyncKind, string> = {
  Customer: 'customers', Vendor: 'vendors', Item: 'items', SalesInvoice: 'sales invoices', Bill: 'bills'
};

export const KIND_ICON: Record<SyncKind, string> = {
  Customer: 'pi pi-users', Vendor: 'pi pi-truck', Item: 'pi pi-box', SalesInvoice: 'pi pi-file', Bill: 'pi pi-file-invoice'
};

/** The order states are listed in on the dashboard: problems first. */
export const STATE_DISPLAY_ORDER: SyncState[] = [
  'Failed', 'Blocked', 'NeedsResolution', 'WaitingOnDependency', 'Pending', 'InProgress', 'DryRunOk', 'Synced', 'NotSynced', 'Voided'
];

// ── Connection ───────────────────────────────────────────────────────────────────────────────────

export const CONNECTION_STATUS_LABEL: Record<string, string> = {
  NotConnected: 'Not connected',
  Connecting: 'Connecting',
  Connected: 'Connected',
  NeedsSetup: 'Connected, setup not finished',
  Live: 'Connected',
  Revoked: 'Access revoked',
  Expired: 'Connection expired'
};

/** Revoked or expired: there was a connection, and it needs signing in again. */
export function needsReconnect(connection: ConnectionStatusModel | null | undefined): boolean {
  return connection?.status === 'Revoked' || connection?.status === 'Expired';
}

/** The words for each ?reason= the OAuth callback can come back with. */
export const CONNECT_ERROR_MESSAGES: Record<ConnectErrorReason, string> = {
  state_invalid: 'The sign-in could not be matched to a connection started here. Start again with "Connect to QuickBooks".',
  state_expired: 'The sign-in took too long and expired. Start again with "Connect to QuickBooks".',
  state_used: 'That sign-in link had already been used. Start again with "Connect to QuickBooks" if you still need to connect.',
  access_denied: 'Access was not granted in QuickBooks, so nothing was connected.',
  realm_mismatch: 'You signed in to a different QuickBooks company from the one this organization is linked to. Sign in to the original company, or disconnect first to switch companies.',
  realm_in_use: 'That QuickBooks company is already connected to another organization. Each QuickBooks company can be connected to one organization only.',
  exchange_failed: 'QuickBooks did not complete the sign-in. Wait a few minutes and try again.',
  not_configured: 'QuickBooks is not set up on this server yet: it has no Intuit app keys. Ask your system administrator.'
};

export function connectErrorMessage(reason: string | null | undefined): string {
  return CONNECT_ERROR_MESSAGES[reason as ConnectErrorReason]
    ?? 'The connection to QuickBooks did not complete. Try again.';
}

// ── Preflight ────────────────────────────────────────────────────────────────────────────────────

/**
 * Checks that report setup progress rather than the QuickBooks company itself. They fail until the
 * later setup steps are done, so they do not count towards the "Preflight passed" step — otherwise
 * the checklist could never get past it.
 */
export const SETUP_PROGRESS_CHECKS = ['ACCOUNTS_MAPPED', 'MATCHING_CONFIRMED'];

export function isSetupProgressCheck(check: PreflightCheckModel): boolean {
  return SETUP_PROGRESS_CHECKS.includes((check.code || '').toUpperCase());
}

/** The company checks: none of them failed (warnings allowed). */
export function companyChecksPassed(preflight: PreflightResultModel | null | undefined): boolean {
  if (!preflight) return false;
  if (preflight.passed) return true;
  const checks = preflight.checks ?? [];
  return checks.length > 0 && !checks.some(c => c.status === 'Fail' && !isSetupProgressCheck(c));
}

/** Why the Live switch is off, one sentence per missing precondition. */
export function liveBlockers(preflight: PreflightResultModel | null | undefined,
                             settings: IntegrationSettingsModel | null | undefined): string[] {
  if (preflight?.canGoLive) return [];
  const reasons: string[] = [];
  if (!preflight) return ['The preflight checks could not be read.'];
  for (const c of preflight.checks ?? []) {
    if (c.status === 'Fail') reasons.push(`${c.title}: ${c.message}`);
  }
  if (!settings?.matchingConfirmedAt && !reasons.some(r => /match/i.test(r))) {
    reasons.push('Matching has not been marked complete on the Match existing tab.');
  }
  if (!reasons.length) reasons.push('The preflight checks have not passed yet.');
  return reasons;
}

// ── Setup checklist and tabs ─────────────────────────────────────────────────────────────────────

export type QboTabKey = 'connection' | 'preflight' | 'mappings' | 'initial-sync' | 'match' | 'sync' | 'api-clients';

export type SetupStepKey = 'connected' | 'preflight' | 'mappings' | 'initialSync' | 'matching' | 'live';

export interface SetupStep {
  key: SetupStepKey;
  label: string;
  tab: QboTabKey;
  done: boolean;
}

export interface SetupInput {
  connection: ConnectionStatusModel | null;
  preflight: PreflightResultModel | null;
  settings: IntegrationSettingsModel | null;
  summary: SyncSummaryModel | null;
}

/** Customers, vendors and items held by the gateway: the initial dry-run load has happened. */
export function masterDataLoaded(summary: SyncSummaryModel | null | undefined): boolean {
  return (summary?.kinds ?? [])
    .filter(k => k.kind === 'Customer' || k.kind === 'Vendor' || k.kind === 'Item')
    .some(k => (k.total ?? 0) > 0);
}

export function mappingsSaved(settings: IntegrationSettingsModel | null | undefined): boolean {
  return !!settings?.defaultIncomeAccountId && !!settings?.defaultExpenseAccountId;
}

export function setupSteps(input: SetupInput): SetupStep[] {
  const mode = input.settings?.mode ?? input.connection?.mode;
  return [
    { key: 'connected',   label: 'Connected',            tab: 'connection',   done: !!input.connection?.isConnected },
    { key: 'preflight',   label: 'Preflight passed',     tab: 'preflight',    done: companyChecksPassed(input.preflight) },
    { key: 'mappings',    label: 'Mappings saved',       tab: 'mappings',     done: mappingsSaved(input.settings) },
    { key: 'initialSync', label: 'Initial sync loaded',  tab: 'initial-sync', done: masterDataLoaded(input.summary) },
    { key: 'matching',    label: 'Matching confirmed',   tab: 'match',        done: !!input.settings?.matchingConfirmedAt },
    { key: 'live',        label: 'Live',                 tab: 'sync',         done: mode === 'Live' }
  ];
}

/** Everything is set up: the checklist is hidden and every tab is open. */
export function setupComplete(steps: SetupStep[]): boolean {
  return steps.find(s => s.key === 'live')?.done === true;
}

/** The step to work on next, or null when all are done. */
export function currentStep(steps: SetupStep[]): SetupStep | null {
  return steps.find(s => !s.done) ?? null;
}

/**
 * Tabs open in setup order: a tab opens once every step before it is done. A tab stays open once
 * a later step has been reached, so a check that starts failing afterwards does not lock the
 * admin out of work already done. The API clients tab is not part of setup.
 */
export function unlockedTabs(steps: SetupStep[], canManage: boolean): Set<QboTabKey> {
  const open = new Set<QboTabKey>();
  if (setupComplete(steps)) {
    steps.forEach(s => open.add(s.tab));
  } else {
    const firstIncomplete = steps.findIndex(s => !s.done);
    let highestDone = -1;
    steps.forEach((s, i) => { if (s.done) highestDone = i; });
    const reach = Math.max(firstIncomplete, highestDone + 1);
    steps.forEach((s, i) => { if (i <= reach) open.add(s.tab); });
  }
  open.add('connection');
  if (canManage) open.add('api-clients');
  return open;
}

// ── Reference data → dropdown options ───────────────────────────────────────────────────────────

export interface Option<T = string> {
  label: string;
  value: T;
  disabled?: boolean;
}

export type AccountPurpose = 'income' | 'expense' | 'discount';

const ACCOUNT_TYPES: Record<AccountPurpose, string[]> = {
  income: ['income', 'other income'],
  expense: ['expense', 'other expense', 'cost of goods sold'],
  // QuickBooks posts a DiscountLineDetail to an income account ("Discounts given") by default; an
  // expense account is accepted too.
  discount: ['income', 'other income', 'expense', 'other expense']
};

/** Normalises "CostOfGoodsSold", "Cost of Goods Sold" and "cost_of_goods_sold" to one form. */
function normaliseType(type: string | null | undefined): string {
  return (type ?? '').replace(/_/g, ' ').replace(/([a-z])([A-Z])/g, '$1 $2').trim().toLowerCase();
}

/** Active accounts of the kinds that suit this setting, sorted by name. The saved one is kept even if it no longer qualifies. */
export function accountOptions(accounts: ReferenceItemModel[] | null | undefined, purpose: AccountPurpose,
                               keepId?: string | null): Option[] {
  const wanted = ACCOUNT_TYPES[purpose];
  const list = (accounts ?? [])
    .filter(a => a.active !== false && wanted.includes(normaliseType(a.type)))
    .sort((a, b) => a.name.localeCompare(b.name))
    .map(a => ({ label: a.type ? `${a.name} (${a.type})` : a.name, value: a.id }));
  return keepSaved(list, accounts, keepId);
}

export function taxCodeOptions(taxCodes: ReferenceItemModel[] | null | undefined, keepId?: string | null): Option[] {
  const list = (taxCodes ?? [])
    .filter(t => t.active !== false)
    .sort((a, b) => a.name.localeCompare(b.name))
    .map(t => ({ label: t.rate != null ? `${t.name} (${formatPercent(t.rate)})` : t.name, value: t.id }));
  return keepSaved(list, taxCodes, keepId);
}

export function termOptions(terms: ReferenceItemModel[] | null | undefined, keepId?: string | null): Option[] {
  const list = (terms ?? [])
    .filter(t => t.active !== false)
    .sort((a, b) => (a.days ?? 0) - (b.days ?? 0) || a.name.localeCompare(b.name))
    .map(t => ({ label: t.days != null ? `${t.name} (${t.days} days)` : t.name, value: t.id }));
  return keepSaved(list, terms, keepId);
}

/** A saved id that is not among the options (inactive, deleted) would show as an empty box. */
function keepSaved(list: Option[], all: ReferenceItemModel[] | null | undefined, keepId?: string | null): Option[] {
  if (!keepId || list.some(o => o.value === keepId)) return list;
  const found = (all ?? []).find(x => x.id === keepId);
  const label = found
    ? `${found.name} (${found.active === false ? 'inactive' : 'not suitable'})`
    : `QuickBooks id ${keepId} (not found in QuickBooks)`;
  return [{ label, value: keepId }, ...list];
}

export function formatPercent(n: number | null | undefined): string {
  if (n == null) return '';
  return `${Number(n.toFixed(4))}%`;
}

// ── Payment terms ────────────────────────────────────────────────────────────────────────────────

export interface ScmPaymentTerm {
  id: string;
  name: string;
  days: number | null;
}

export interface TermRow {
  paymentTermExternalId: string;
  paymentTermName: string;
  days: number | null;
  qboTermId: string | null;
  /** In the mappings but no longer among SCM's payment terms. */
  orphan: boolean;
}

export function sameId(a: string | null | undefined, b: string | null | undefined): boolean {
  return !!a && !!b && a.toLowerCase() === b.toLowerCase();
}

/** Every SCM payment term, with its saved mapping if any, plus saved mappings whose term has gone. */
export function mergeTermRows(scmTerms: ScmPaymentTerm[], mappings: TermMappingModel[]): TermRow[] {
  const rows: TermRow[] = scmTerms.map(t => {
    const m = mappings.find(x => sameId(x.paymentTermExternalId, t.id));
    return {
      paymentTermExternalId: t.id, paymentTermName: t.name, days: t.days ?? null,
      qboTermId: m?.qboTermId || null, orphan: false
    };
  });
  for (const m of mappings) {
    if (!scmTerms.some(t => sameId(t.id, m.paymentTermExternalId))) {
      rows.push({
        paymentTermExternalId: m.paymentTermExternalId, paymentTermName: m.paymentTermName || m.paymentTermExternalId,
        days: null, qboTermId: m.qboTermId || null, orphan: true
      });
    }
  }
  return rows.sort((a, b) => Number(a.orphan) - Number(b.orphan) || a.paymentTermName.localeCompare(b.paymentTermName));
}

/** For each unmapped row, the QuickBooks term with the same number of due days, if exactly one has. */
export function suggestTermsByDays(rows: TermRow[], terms: ReferenceItemModel[]): number {
  let filled = 0;
  for (const row of rows) {
    if (row.qboTermId || row.days == null) continue;
    const same = terms.filter(t => t.active !== false && t.days === row.days);
    if (same.length === 1) { row.qboTermId = same[0].id; filled++; }
  }
  return filled;
}

// ── Matching ─────────────────────────────────────────────────────────────────────────────────────

export const CONFIDENCE_SEVERITY: Record<MatchConfidence, TagSeverity> = {
  Exact: 'success', Probable: 'warn', None: 'secondary'
};

export const DECISION_LABEL: Record<MatchDecisionChoice, string> = {
  Link: 'Link', CreateNew: 'Create new', Skip: 'Skip'
};

/** What the admin chose for one candidate on screen (not yet saved). */
export interface DraftDecision {
  decision: MatchDecisionChoice | null;
  remoteId: string | null;
}

/** The on-screen choice a candidate starts with: what was saved, or nothing. */
export function initialDraft(c: MatchCandidateModel): DraftDecision {
  return {
    decision: c.decision === 'Pending' ? null : c.decision,
    remoteId: c.remoteId ?? null
  };
}

/** "Link all exact matches": every exact match with a proposed QuickBooks record is set to Link. Returns how many changed. */
export function linkAllExact(candidates: MatchCandidateModel[], drafts: Map<string, DraftDecision>): number {
  let changed = 0;
  for (const c of candidates) {
    if (c.confidence !== 'Exact' || !c.remoteId) continue;
    const d = drafts.get(c.id) ?? initialDraft(c);
    if (d.decision !== 'Link' || d.remoteId !== c.remoteId) changed++;
    drafts.set(c.id, { decision: 'Link', remoteId: c.remoteId });
  }
  return changed;
}

/**
 * The decisions to send: every candidate whose on-screen choice differs from what the server has.
 * A Link always names the QuickBooks record; other decisions send no id.
 */
export function buildDecisions(candidates: MatchCandidateModel[], drafts: Map<string, DraftDecision>): MatchDecisionItem[] {
  const out: MatchDecisionItem[] = [];
  for (const c of candidates) {
    const d = drafts.get(c.id);
    if (!d?.decision) continue;
    const remoteId = d.decision === 'Link' ? (d.remoteId || c.remoteId || null) : null;
    if (d.decision === 'Link' && !remoteId) continue;
    const unchanged = d.decision === c.decision && (d.decision !== 'Link' || remoteId === (c.remoteId ?? null));
    if (unchanged) continue;
    out.push({ candidateId: c.id, decision: d.decision, remoteId });
  }
  return out;
}

// ── Links to the record in SCM ───────────────────────────────────────────────────────────────────

/**
 * Where a synced record lives in SCM, for records SCM itself sent. Items are keyed by variant, and
 * no SCM page opens a product by variant id, so they link to the product list.
 */
export function sourceRoute(kind: SyncKind, externalId: string, sourceSystem?: string | null): string[] | null {
  if (sourceSystem && sourceSystem.toUpperCase() !== 'SCM') return null;
  if (!externalId) return null;
  switch (kind) {
    case 'Customer':
    case 'Vendor':       return ['/portal/pages/suppliers/partner-detail', externalId];
    case 'SalesInvoice': return ['/portal/pages/finance/sales-invoices', externalId];
    case 'Bill':         return ['/portal/pages/finance/invoices', externalId];
    case 'Item':         return ['/portal/pages/inventory/products'];
    default:             return null;
  }
}

// ── Misc ─────────────────────────────────────────────────────────────────────────────────────────

/** Pretty-prints JSON for the attempt log; anything that is not JSON is shown as it is. */
export function prettyJson(text: string | null | undefined): string {
  if (!text) return '';
  try {
    return JSON.stringify(JSON.parse(text), null, 2);
  } catch {
    return text;
  }
}

export function sumCounts(counts: Partial<Record<SyncState, number>> | null | undefined, states: SyncState[]): number {
  return states.reduce((n, s) => n + (counts?.[s] ?? 0), 0);
}
