import { AbstractControl, ValidationErrors } from '@angular/forms';
import {
  DepartmentOptionModel, SaleOrderConfigModel, UpdateSaleOrderConfigRequest
} from '../../services/sale-order-config.service';

/** What the form holds. The server's model, with the two optional settings made easy to bind. */
export interface SettingsValue {
  autoPoEnabled: boolean;
  supplierSelectionMode: string;
  autoPoApprovalMode: string;
  dropShipEnabled: boolean;
  selfPickupEnabled: boolean;
  defaultFulfillmentMode: string;
  /** Null while the box is empty. */
  reservationTtlHours: number | null;
  partialFulfillmentAllowed: boolean;
  emailIntimationEnabled: boolean;
  intimationDepartmentId: number | null;
  /** Empty when there are none. */
  intimationCcEmails: string;
  shipmentRequiredDefault: boolean;
}

export type SettingKey = keyof SettingsValue;

export interface ChoiceOption {
  value: string;
  label: string;
  description: string;
  recommended?: boolean;
  /** Worth a second look before choosing: it takes a person out of the loop. */
  caution?: boolean;
}

export const MIN_RESERVATION_HOURS = 1;
export const MAX_RESERVATION_HOURS = 8760;
export const MAX_CC_LENGTH = 500;

export const SUPPLIER_SELECTION_OPTIONS: ChoiceOption[] = [
  {
    value: 'BEST_MATCH', label: 'Best match', recommended: true,
    description: 'Chooses the active vendor with the best mix of scorecard grade and price for the quantity. A tie goes to the shortest lead time.'
  },
  {
    value: 'DEFAULT_SUPPLIER', label: 'Default supplier',
    description: 'Uses the item\'s default supplier. If it has none, or no current rate, that line is left for the team.'
  },
  {
    value: 'MANUAL', label: 'Manual',
    description: 'No purchase order is raised. The notification department is told that a supplier has to be chosen.'
  }
];

export const APPROVAL_MODE_OPTIONS: ChoiceOption[] = [
  {
    value: 'REQUIRE_WORKFLOW', label: 'Send for approval',
    description: 'Submitted for approval straight away, through the normal approval flow. The team can edit it until it is approved.'
  },
  {
    value: 'DRAFT_ONLY', label: 'Keep as a draft',
    description: 'Left as a draft for the team to review and change, then submit or send themselves.'
  },
  {
    value: 'AUTO_SEND', label: 'Approve and send automatically', caution: true,
    description: 'Approved and sent to the supplier immediately, with no human review. Use it only when you trust the supplier selection above.'
  }
];

export const FULFILMENT_MODE_OPTIONS: ChoiceOption[] = [
  {
    value: 'IN_STOCK', label: 'From stock',
    description: 'Each line is checked against stock when the order is confirmed. What is on the shelf is reserved, and any shortfall is bought.'
  },
  {
    value: 'BACK_TO_BACK', label: 'Buy to order',
    description: 'Every line is bought from a supplier for the order, even when stock is on hand. Nothing is reserved from stock.'
  },
  {
    value: 'DROP_SHIP', label: 'Drop ship',
    description: 'The supplier ships every line straight to the customer. Needs drop shipping on, and applies to shipped orders only.'
  }
];

interface SettingMeta {
  key: SettingKey;
  /** The server's property name, which is what the change history records. */
  field: string;
  label: string;
}

/** In the order they are shown on the page and listed in a review. */
export const SETTINGS: SettingMeta[] = [
  { key: 'autoPoEnabled',             field: 'AutoPoEnabled',             label: 'Automatic purchase orders' },
  { key: 'supplierSelectionMode',     field: 'SupplierSelectionMode',     label: 'Supplier selection' },
  { key: 'autoPoApprovalMode',        field: 'AutoPoApprovalMode',        label: 'Purchase order approval' },
  { key: 'defaultFulfillmentMode',    field: 'DefaultFulfillmentMode',    label: 'Default fulfilment' },
  { key: 'dropShipEnabled',           field: 'DropShipEnabled',           label: 'Drop shipping' },
  { key: 'selfPickupEnabled',         field: 'SelfPickupEnabled',         label: 'Customer pickup' },
  { key: 'partialFulfillmentAllowed', field: 'PartialFulfillmentAllowed', label: 'Partial fulfilment' },
  { key: 'shipmentRequiredDefault',   field: 'ShipmentRequiredDefault',   label: 'Shipment required by default' },
  { key: 'reservationTtlHours',       field: 'ReservationTtlHours',       label: 'Reservation hold' },
  { key: 'emailIntimationEnabled',    field: 'EmailIntimationEnabled',    label: 'Sale order emails' },
  { key: 'intimationDepartmentId',    field: 'IntimationDepartmentId',    label: 'Notification department' },
  { key: 'intimationCcEmails',        field: 'IntimationCcEmails',        label: 'Copy emails to' }
];

const OPTIONS_BY_KEY: Partial<Record<SettingKey, ChoiceOption[]>> = {
  supplierSelectionMode:  SUPPLIER_SELECTION_OPTIONS,
  autoPoApprovalMode:     APPROVAL_MODE_OPTIONS,
  defaultFulfillmentMode: FULFILMENT_MODE_OPTIONS
};

// ── Emails ───────────────────────────────────────────────────────────────────

const EMAIL_PATTERN = /^[^\s@,;]+@[^\s@,;]+\.[^\s@,;]+$/;

/** The addresses in a typed list: split on commas, semicolons and spaces, trimmed, without repeats. */
export function parseEmails(text: string | null | undefined): string[] {
  const seen = new Set<string>();
  const result: string[] = [];
  for (const part of (text ?? '').split(/[,;\s]+/)) {
    const email = part.trim();
    if (!email || seen.has(email.toLowerCase())) continue;
    seen.add(email.toLowerCase());
    result.push(email);
  }
  return result;
}

/** How the list is stored: one tidy, comma separated line. */
export function normalizeEmails(text: string | null | undefined): string {
  return parseEmails(text).join(', ');
}

export function invalidEmails(text: string | null | undefined): string[] {
  return parseEmails(text).filter(e => !EMAIL_PATTERN.test(e));
}

// ── Validation ───────────────────────────────────────────────────────────────

/** Every address has to look like one, and the tidy list has to fit in the column. */
export function emailListValidator(control: AbstractControl): ValidationErrors | null {
  const text = control.value as string | null;
  const bad = invalidEmails(text);
  if (bad.length) return { invalidEmails: bad };
  if (normalizeEmails(text).length > MAX_CC_LENGTH) return { tooLong: true };
  return null;
}

/** Drop shipping is the default fulfilment, so it has to be switched on: a line cannot be drop shipped otherwise. */
export function dropShipDefaultValidator(group: AbstractControl): ValidationErrors | null {
  const v = group.getRawValue() as SettingsValue;
  return v.defaultFulfillmentMode === 'DROP_SHIP' && !v.dropShipEnabled ? { dropShipDefault: true } : null;
}

/** New orders start as customer pickup when a shipment is not required by default, so pickup has to be on. */
export function pickupDefaultValidator(group: AbstractControl): ValidationErrors | null {
  const v = group.getRawValue() as SettingsValue;
  return !v.shipmentRequiredDefault && !v.selfPickupEnabled ? { pickupDefault: true } : null;
}

// ── Converting ───────────────────────────────────────────────────────────────

export function fromModel(model: SaleOrderConfigModel): SettingsValue {
  return {
    autoPoEnabled: model.autoPoEnabled,
    supplierSelectionMode: model.supplierSelectionMode,
    autoPoApprovalMode: model.autoPoApprovalMode,
    dropShipEnabled: model.dropShipEnabled,
    selfPickupEnabled: model.selfPickupEnabled,
    defaultFulfillmentMode: model.defaultFulfillmentMode,
    reservationTtlHours: model.reservationTtlHours,
    partialFulfillmentAllowed: model.partialFulfillmentAllowed,
    emailIntimationEnabled: model.emailIntimationEnabled,
    intimationDepartmentId: model.intimationDepartmentId ?? null,
    intimationCcEmails: normalizeEmails(model.intimationCcEmails),
    shipmentRequiredDefault: model.shipmentRequiredDefault
  };
}

/** What a save sends. Nothing is sent as an empty string, so "none" is always recorded the same way. */
export function toRequest(value: SettingsValue): UpdateSaleOrderConfigRequest {
  return {
    autoPoEnabled: value.autoPoEnabled,
    supplierSelectionMode: value.supplierSelectionMode,
    autoPoApprovalMode: value.autoPoApprovalMode,
    dropShipEnabled: value.dropShipEnabled,
    selfPickupEnabled: value.selfPickupEnabled,
    defaultFulfillmentMode: value.defaultFulfillmentMode,
    reservationTtlHours: value.reservationTtlHours as number,
    partialFulfillmentAllowed: value.partialFulfillmentAllowed,
    emailIntimationEnabled: value.emailIntimationEnabled,
    intimationDepartmentId: value.intimationDepartmentId,
    intimationCcEmails: normalizeEmails(value.intimationCcEmails) || null,
    shipmentRequiredDefault: value.shipmentRequiredDefault
  };
}

// ── Changes ──────────────────────────────────────────────────────────────────

export interface SettingChange {
  key: SettingKey;
  label: string;
  from: string;
  to: string;
}

function comparable(key: SettingKey, value: unknown): unknown {
  return key === 'intimationCcEmails' ? normalizeEmails(value as string) : value ?? null;
}

/** The settings that differ, in page order, worded for a person. */
export function diffSettings(before: SettingsValue, after: SettingsValue, departments: DepartmentOptionModel[] = []): SettingChange[] {
  return SETTINGS
    .filter(s => comparable(s.key, before[s.key]) !== comparable(s.key, after[s.key]))
    .map(s => ({
      key: s.key,
      label: s.label,
      from: displayValue(s.key, before[s.key], departments),
      to: displayValue(s.key, after[s.key], departments)
    }));
}

export function isChanged(before: SettingsValue, after: SettingsValue): boolean {
  return diffSettings(before, after).length > 0;
}

// ── Wording ──────────────────────────────────────────────────────────────────

/** "72 hours" is easier to judge as "3 days". */
export function describeHours(hours: number | null | undefined): string {
  if (hours === null || hours === undefined) return '';
  if (hours < 48) return `${hours} ${hours === 1 ? 'hour' : 'hours'}`;
  const days = Math.floor(hours / 24);
  const rest = hours % 24;
  const dayText = `${days} days`;
  return rest === 0 ? dayText : `${dayText} ${rest} ${rest === 1 ? 'hour' : 'hours'}`;
}

export function departmentLabel(d: DepartmentOptionModel): string {
  return d.code ? `${d.name} (${d.code})` : d.name;
}

export function displayValue(key: SettingKey, value: unknown, departments: DepartmentOptionModel[] = []): string {
  if (typeof value === 'boolean') return value ? 'On' : 'Off';

  switch (key) {
    case 'reservationTtlHours':
      return value === null || value === undefined || value === '' ? 'Not set' : describeHours(Number(value));
    case 'intimationDepartmentId': {
      if (value === null || value === undefined || value === '') return 'None';
      const found = departments.find(d => d.departmentId === Number(value));
      return found ? departmentLabel(found) : `Department ${value}`;
    }
    case 'intimationCcEmails':
      return normalizeEmails(value as string) || 'None';
    default: {
      const options = OPTIONS_BY_KEY[key];
      const text = value === null || value === undefined ? '' : String(value);
      return options?.find(o => o.value === text)?.label ?? (text || 'Not set');
    }
  }
}

export function settingLabel(field: string): string {
  return SETTINGS.find(s => s.field === field)?.label ?? field;
}

/** A value from the change history, which the server records as text ("True", "72", "12"), worded like the page words it. */
export function displayAuditValue(field: string, raw: string | null | undefined, departments: DepartmentOptionModel[] = []): string {
  const meta = SETTINGS.find(s => s.field === field);
  if (!meta) return raw ?? 'None';
  if (raw === null || raw === undefined || raw === '') return displayValue(meta.key, null, departments);
  if (raw === 'True') return displayValue(meta.key, true, departments);
  if (raw === 'False') return displayValue(meta.key, false, departments);
  return displayValue(meta.key, raw, departments);
}

// ── What a change will mean ──────────────────────────────────────────────────

export interface ImpactNote {
  severity: 'warn' | 'info';
  text: string;
}

/** Consequences worth reading before a save, for the review. Only what the change itself causes. */
export function impactNotes(before: SettingsValue, after: SettingsValue): ImpactNote[] {
  const notes: ImpactNote[] = [];

  if (after.autoPoEnabled && after.autoPoApprovalMode === 'AUTO_SEND'
      && (before.autoPoApprovalMode !== 'AUTO_SEND' || !before.autoPoEnabled)) {
    notes.push({
      severity: 'warn',
      text: 'Purchase orders raised for shortfalls will be approved and sent to the supplier immediately, with no human review.'
    });
  }
  if (before.autoPoEnabled && !after.autoPoEnabled) {
    notes.push({
      severity: 'info',
      text: 'Shortfalls will no longer raise purchase orders. The team has to handle each one by hand.'
    });
  }
  if (before.dropShipEnabled && !after.dropShipEnabled) {
    notes.push({
      severity: 'warn',
      text: 'Orders already confirmed with drop ship lines cannot have purchase orders raised for them until drop shipping is turned back on.'
    });
  }
  if (before.reservationTtlHours !== after.reservationTtlHours) {
    notes.push({
      severity: 'info',
      text: 'The new hold applies to reservations made from now on. Reservations that already exist keep their expiry.'
    });
  }
  if (before.selfPickupEnabled && !after.selfPickupEnabled) {
    notes.push({
      severity: 'warn',
      text: 'Every order will have to be shipped. Orders already taken for customer pickup cannot be confirmed until they are changed to ship.'
    });
  }
  if (before.partialFulfillmentAllowed && !after.partialFulfillmentAllowed) {
    notes.push({
      severity: 'warn',
      text: 'An order will only go out in one delivery, covering everything still owed. Orders that are part delivered can only be finished that way.'
    });
  }
  if (before.emailIntimationEnabled && !after.emailIntimationEnabled) {
    notes.push({
      severity: 'warn',
      text: 'No sale order email will be sent at all: not the confirmation, and not the notices to the people who took the orders.'
    });
  }
  if (before.defaultFulfillmentMode !== after.defaultFulfillmentMode) {
    if (after.defaultFulfillmentMode === 'BACK_TO_BACK') {
      notes.push({
        severity: 'info',
        text: 'New orders will buy every line from a supplier, even when stock is on hand. Orders already taken are not changed.'
      });
    } else if (after.defaultFulfillmentMode === 'DROP_SHIP') {
      notes.push({
        severity: 'info',
        text: 'New shipped orders will be drop shipped by the supplier. Orders already taken are not changed.'
      });
    }
  }
  if (before.shipmentRequiredDefault && !after.shipmentRequiredDefault) {
    notes.push({
      severity: 'info',
      text: 'New orders will start as customer pickup unless the order says otherwise.'
    });
  }

  return notes;
}
