// How a QuickBooks sync state looks wherever it is shown — the badge on SCM pages and the
// QuickBooks Integration screens. No Angular, no I/O.

import { SyncKind, SyncState } from '../../../models/quickbooks-integration.models';

export type TagSeverity = 'success' | 'secondary' | 'info' | 'warn' | 'danger' | 'contrast';

// ── Permissions & feature ────────────────────────────────────────────────────────────────────────

export const QBO_FEATURE = 'MODULE_INTEGRATION';
export const PERM_VIEW = 'INTEGRATION_VIEW';
export const PERM_MANAGE = 'INTEGRATION_MANAGE';
export const PERM_SYNC = 'INTEGRATION_SYNC';

// ── Record kinds ─────────────────────────────────────────────────────────────────────────────────

export const KIND_LABEL: Record<SyncKind, string> = {
  Customer: 'Customer', Vendor: 'Vendor', Item: 'Item', SalesInvoice: 'Sales invoice', Bill: 'Bill'
};

// ── States ───────────────────────────────────────────────────────────────────────────────────────

/** Colour family of a state. */
export type StateTone = 'green' | 'blue' | 'teal' | 'amber' | 'red' | 'grey';

export const STATE_TONE: Record<SyncState, StateTone> = {
  Synced: 'green',
  Pending: 'blue',
  InProgress: 'blue',
  DryRunOk: 'teal',
  WaitingOnDependency: 'amber',
  Blocked: 'red',
  Failed: 'red',
  NeedsResolution: 'red',
  NotSynced: 'grey',
  Voided: 'grey'
};

const TONE_SEVERITY: Record<StateTone, TagSeverity> = {
  green: 'success', blue: 'info', teal: 'info', amber: 'warn', red: 'danger', grey: 'secondary'
};

export const STATE_LABEL: Record<SyncState, string> = {
  NotSynced: 'Not synced',
  Pending: 'Pending',
  InProgress: 'In progress',
  Synced: 'Synced',
  DryRunOk: 'Dry run OK',
  Failed: 'Failed',
  Blocked: 'Blocked',
  WaitingOnDependency: 'Waiting',
  NeedsResolution: 'Needs resolution',
  Voided: 'Voided'
};

export const STATE_DESCRIPTION: Record<SyncState, string> = {
  NotSynced: 'Not sent to QuickBooks yet.',
  Pending: 'Queued to be sent to QuickBooks.',
  InProgress: 'Being sent to QuickBooks now.',
  Synced: 'In QuickBooks and up to date.',
  DryRunOk: 'Checked in dry run: it would be accepted. Nothing was sent.',
  Failed: 'QuickBooks refused it, or the retries ran out.',
  Blocked: 'Refused by our own checks before anything was sent. Fix the record and it is sent again.',
  WaitingOnDependency: 'Waiting for a customer, vendor or item it uses to reach QuickBooks first.',
  NeedsResolution: 'The outcome of the last attempt is unknown. Someone has to check QuickBooks and resolve it.',
  Voided: 'Voided in QuickBooks.'
};

export function stateSeverity(state: SyncState | string | null | undefined): TagSeverity {
  const tone = STATE_TONE[state as SyncState];
  return tone ? TONE_SEVERITY[tone] : 'secondary';
}

/** Teal has no PrimeNG severity of its own, so every tag also carries its tone as a class. */
export function stateTagClass(state: SyncState | string | null | undefined): string {
  return `qbo-tone-${STATE_TONE[state as SyncState] ?? 'grey'}`;
}

export function stateLabel(state: SyncState | string | null | undefined): string {
  return STATE_LABEL[state as SyncState] ?? (state ? String(state) : 'Unknown');
}
