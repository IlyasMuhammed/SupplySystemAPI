import { FormControl, FormGroup } from '@angular/forms';

import { DepartmentOptionModel, SaleOrderConfigModel } from '../../services/sale-order-config.service';
import {
  APPROVAL_MODE_OPTIONS, FULFILMENT_MODE_OPTIONS, SETTINGS, SUPPLIER_SELECTION_OPTIONS, SettingsValue,
  MAX_CC_LENGTH, departmentLabel, describeHours, diffSettings, displayAuditValue, displayValue,
  dropShipDefaultValidator, emailListValidator, fromModel, impactNotes, invalidEmails, isChanged,
  normalizeEmails, parseEmails, pickupDefaultValidator, settingLabel, toRequest
} from './sale-order-settings.shared';

const DEPARTMENTS: DepartmentOptionModel[] = [
  { departmentId: 1, name: 'Supply', code: 'SUP', hasHead: true },
  { departmentId: 2, name: 'Warehouse', code: null, hasHead: false }
];

function value(overrides: Partial<SettingsValue> = {}): SettingsValue {
  return {
    autoPoEnabled: true, supplierSelectionMode: 'BEST_MATCH', autoPoApprovalMode: 'REQUIRE_WORKFLOW',
    dropShipEnabled: false, selfPickupEnabled: true, defaultFulfillmentMode: 'IN_STOCK', reservationTtlHours: 72,
    partialFulfillmentAllowed: true, emailIntimationEnabled: true, intimationDepartmentId: null,
    intimationCcEmails: '', shipmentRequiredDefault: true,
    ...overrides
  };
}

describe('sale order settings: the choices', () => {
  it('use exactly the values the server understands, and nothing else', () => {
    expect(SUPPLIER_SELECTION_OPTIONS.map(o => o.value).sort()).toEqual(['BEST_MATCH', 'DEFAULT_SUPPLIER', 'MANUAL']);
    expect(APPROVAL_MODE_OPTIONS.map(o => o.value).sort()).toEqual(['AUTO_SEND', 'DRAFT_ONLY', 'REQUIRE_WORKFLOW']);
    expect(FULFILMENT_MODE_OPTIONS.map(o => o.value).sort()).toEqual(['BACK_TO_BACK', 'DROP_SHIP', 'IN_STOCK']);
  });

  it('recommend best match, and warn only about the one that takes a person out of the loop', () => {
    const all = [...SUPPLIER_SELECTION_OPTIONS, ...APPROVAL_MODE_OPTIONS, ...FULFILMENT_MODE_OPTIONS];

    expect(all.filter(o => o.recommended).map(o => o.value)).toEqual(['BEST_MATCH']);
    expect(all.filter(o => o.caution).map(o => o.value)).toEqual(['AUTO_SEND']);
  });

  it('describe every setting the form holds, under the property name the server records changes with', () => {
    const keys = Object.keys(value()) as (keyof SettingsValue)[];

    expect(SETTINGS.map(s => s.key).sort()).toEqual([...keys].sort());
    // The server records "AutoPoEnabled" for autoPoEnabled, and so on.
    for (const s of SETTINGS) expect(s.field.toLowerCase()).toBe(s.key.toLowerCase());
  });
});

describe('sale order settings: email lists', () => {
  it('split on commas, semicolons and spaces, trim, and drop repeats whatever their case', () => {
    expect(parseEmails(' a@x.com;b@x.com , A@X.com   c@x.com,, ')).toEqual(['a@x.com', 'b@x.com', 'c@x.com']);
    expect(normalizeEmails('a@x.com;b@x.com')).toBe('a@x.com, b@x.com');
  });

  it('are empty when nothing was typed', () => {
    expect(parseEmails(null)).toEqual([]);
    expect(parseEmails(undefined)).toEqual([]);
    expect(normalizeEmails('  ,; ')).toBe('');
  });

  it('name the addresses that do not look like one', () => {
    expect(invalidEmails('good@x.com, nope, also@bad, fine@y.org')).toEqual(['nope', 'also@bad']);
    expect(invalidEmails('')).toEqual([]);
  });

  it('are valid when empty or every address is fine', () => {
    expect(emailListValidator(new FormControl(''))).toBeNull();
    expect(emailListValidator(new FormControl(null))).toBeNull();
    expect(emailListValidator(new FormControl('a@x.com, b@y.org'))).toBeNull();
  });

  it('are refused, naming the addresses, when one is not valid', () => {
    expect(emailListValidator(new FormControl('a@x.com, nope'))).toEqual({ invalidEmails: ['nope'] });
  });

  it('are refused when the tidy list will not fit the column', () => {
    const many = Array.from({ length: 60 }, (_, i) => `person${i}@example.com`).join(', ');

    expect(normalizeEmails(many).length).toBeGreaterThan(MAX_CC_LENGTH);
    expect(emailListValidator(new FormControl(many))).toEqual({ tooLong: true });
  });
});

describe('sale order settings: drop ship as the default', () => {
  const group = (mode: string, dropShip: boolean) =>
    new FormGroup({ defaultFulfillmentMode: new FormControl(mode), dropShipEnabled: new FormControl(dropShip) });

  it('is refused while drop shipping is off', () => {
    expect(dropShipDefaultValidator(group('DROP_SHIP', false))).toEqual({ dropShipDefault: true });
  });

  it('is fine once drop shipping is on, and other defaults never need it', () => {
    expect(dropShipDefaultValidator(group('DROP_SHIP', true))).toBeNull();
    expect(dropShipDefaultValidator(group('IN_STOCK', false))).toBeNull();
    expect(dropShipDefaultValidator(group('BACK_TO_BACK', false))).toBeNull();
  });
});

describe('sale order settings: customer pickup as the starting point', () => {
  const group = (shipmentRequiredDefault: boolean, selfPickupEnabled: boolean) =>
    new FormGroup({ shipmentRequiredDefault: new FormControl(shipmentRequiredDefault), selfPickupEnabled: new FormControl(selfPickupEnabled) });

  it('is refused while customer pickup is off', () => {
    expect(pickupDefaultValidator(group(false, false))).toEqual({ pickupDefault: true });
  });

  it('is fine when pickup is on, and shipping as the start never needs it', () => {
    expect(pickupDefaultValidator(group(false, true))).toBeNull();
    expect(pickupDefaultValidator(group(true, false))).toBeNull();
    expect(pickupDefaultValidator(group(true, true))).toBeNull();
  });
});

describe('sale order settings: converting', () => {
  const model: SaleOrderConfigModel = {
    uuid: 'cfg-1', autoPoEnabled: false, supplierSelectionMode: 'MANUAL', autoPoApprovalMode: 'DRAFT_ONLY',
    dropShipEnabled: true, selfPickupEnabled: false, defaultFulfillmentMode: 'DROP_SHIP', reservationTtlHours: 96,
    partialFulfillmentAllowed: false, emailIntimationEnabled: false, intimationDepartmentId: 1,
    intimationCcEmails: 'a@x.com;b@x.com', shipmentRequiredDefault: false, updatedBy: 4, updatedAt: '2026-09-20T09:00:00Z'
  };

  it('takes the server model into the form, tidying the copy list and leaving out what the form does not hold', () => {
    expect(fromModel(model)).toEqual({
      autoPoEnabled: false, supplierSelectionMode: 'MANUAL', autoPoApprovalMode: 'DRAFT_ONLY',
      dropShipEnabled: true, selfPickupEnabled: false, defaultFulfillmentMode: 'DROP_SHIP', reservationTtlHours: 96,
      partialFulfillmentAllowed: false, emailIntimationEnabled: false, intimationDepartmentId: 1,
      intimationCcEmails: 'a@x.com, b@x.com', shipmentRequiredDefault: false
    });
  });

  it('reads a missing department and copy list as none', () => {
    const v = fromModel({ ...model, intimationDepartmentId: undefined, intimationCcEmails: undefined });

    expect(v.intimationDepartmentId).toBeNull();
    expect(v.intimationCcEmails).toBe('');
  });

  it('sends every setting, and records "none" as null rather than an empty string', () => {
    const request = toRequest(value({ intimationDepartmentId: null, intimationCcEmails: '  ' }));

    expect(request).toEqual({
      autoPoEnabled: true, supplierSelectionMode: 'BEST_MATCH', autoPoApprovalMode: 'REQUIRE_WORKFLOW',
      dropShipEnabled: false, selfPickupEnabled: true, defaultFulfillmentMode: 'IN_STOCK', reservationTtlHours: 72,
      partialFulfillmentAllowed: true, emailIntimationEnabled: true, intimationDepartmentId: null,
      intimationCcEmails: null, shipmentRequiredDefault: true
    });
  });

  it('sends the copy list tidied', () => {
    expect(toRequest(value({ intimationCcEmails: 'a@x.com; b@x.com' })).intimationCcEmails).toBe('a@x.com, b@x.com');
  });
});

describe('sale order settings: what changed', () => {
  it('is nothing when nothing was touched', () => {
    expect(diffSettings(value(), value())).toEqual([]);
    expect(isChanged(value(), value())).toBeFalse();
  });

  it('lists each change in page order, in words', () => {
    const changes = diffSettings(
      value(),
      value({ reservationTtlHours: 96, autoPoApprovalMode: 'AUTO_SEND', dropShipEnabled: true, autoPoEnabled: false }));

    expect(changes).toEqual([
      { key: 'autoPoEnabled',      label: 'Automatic purchase orders', from: 'On',               to: 'Off' },
      { key: 'autoPoApprovalMode', label: 'Purchase order approval',   from: 'Send for approval', to: 'Approve automatically' },
      { key: 'dropShipEnabled',    label: 'Drop shipping',             from: 'Off',              to: 'On' },
      { key: 'reservationTtlHours', label: 'Reservation hold',         from: '3 days',           to: '4 days' }
    ]);
  });

  it('does not count a copy list that is only written differently', () => {
    expect(isChanged(value({ intimationCcEmails: 'a@x.com, b@x.com' }), value({ intimationCcEmails: 'a@x.com;b@x.com' }))).toBeFalse();
    expect(isChanged(value({ intimationCcEmails: 'a@x.com' }), value({ intimationCcEmails: 'a@x.com, b@x.com' }))).toBeTrue();
  });

  it('names a department rather than showing its number', () => {
    const [change] = diffSettings(value(), value({ intimationDepartmentId: 1 }), DEPARTMENTS);

    expect(change).toEqual({ key: 'intimationDepartmentId', label: 'Notification department', from: 'None', to: 'Supply (SUP)' });
  });
});

describe('sale order settings: wording', () => {
  it('puts hours the way a person would', () => {
    expect(describeHours(1)).toBe('1 hour');
    expect(describeHours(24)).toBe('24 hours');
    expect(describeHours(47)).toBe('47 hours');
    expect(describeHours(48)).toBe('2 days');
    expect(describeHours(72)).toBe('3 days');
    expect(describeHours(49)).toBe('2 days 1 hour');
    expect(describeHours(50)).toBe('2 days 2 hours');
    expect(describeHours(null)).toBe('');
  });

  it('labels a department with its code when it has one', () => {
    expect(departmentLabel(DEPARTMENTS[0])).toBe('Supply (SUP)');
    expect(departmentLabel(DEPARTMENTS[1])).toBe('Warehouse');
  });

  it('shows switches as On and Off, and choices by name', () => {
    expect(displayValue('autoPoEnabled', true)).toBe('On');
    expect(displayValue('dropShipEnabled', false)).toBe('Off');
    expect(displayValue('supplierSelectionMode', 'DEFAULT_SUPPLIER')).toBe('Default supplier');
    expect(displayValue('defaultFulfillmentMode', 'BACK_TO_BACK')).toBe('Buy to order');
    expect(displayValue('supplierSelectionMode', 'SOMETHING_NEW')).toBe('SOMETHING_NEW');
  });

  it('shows what is empty as words', () => {
    expect(displayValue('reservationTtlHours', null)).toBe('Not set');
    expect(displayValue('intimationDepartmentId', null)).toBe('None');
    expect(displayValue('intimationCcEmails', '')).toBe('None');
    expect(displayValue('supplierSelectionMode', null)).toBe('Not set');
  });

  it('shows a department that is no longer listed by its number', () => {
    expect(displayValue('intimationDepartmentId', 9, DEPARTMENTS)).toBe('Department 9');
    expect(displayValue('intimationDepartmentId', 2, DEPARTMENTS)).toBe('Warehouse');
  });

  it('words a value from the change history the way the page words it', () => {
    expect(displayAuditValue('AutoPoEnabled', 'True')).toBe('On');
    expect(displayAuditValue('DropShipEnabled', 'False')).toBe('Off');
    expect(displayAuditValue('SupplierSelectionMode', 'BEST_MATCH')).toBe('Best match');
    expect(displayAuditValue('ReservationTtlHours', '72')).toBe('3 days');
    expect(displayAuditValue('IntimationDepartmentId', '1', DEPARTMENTS)).toBe('Supply (SUP)');
    expect(displayAuditValue('IntimationDepartmentId', null, DEPARTMENTS)).toBe('None');
    expect(displayAuditValue('IntimationCcEmails', 'a@x.com;b@x.com')).toBe('a@x.com, b@x.com');
  });

  it('shows a setting this page does not know by its own name, and value', () => {
    expect(settingLabel('AutoPoEnabled')).toBe('Automatic purchase orders');
    expect(settingLabel('SomethingElse')).toBe('SomethingElse');
    expect(displayAuditValue('SomethingElse', 'x')).toBe('x');
  });
});

describe('sale order settings: what a change will mean', () => {
  it('warns when purchase orders will be approved without review', () => {
    const notes = impactNotes(value(), value({ autoPoApprovalMode: 'AUTO_SEND' }));

    expect(notes.length).toBe(1);
    expect(notes[0].severity).toBe('warn');
    expect(notes[0].text).toContain('no human review');
  });

  it('warns when automatic purchase orders come back on with that approval already chosen', () => {
    const notes = impactNotes(
      value({ autoPoEnabled: false, autoPoApprovalMode: 'AUTO_SEND' }),
      value({ autoPoEnabled: true, autoPoApprovalMode: 'AUTO_SEND' }));

    expect(notes.map(n => n.severity)).toEqual(['warn']);
  });

  it('does not warn about an approval that was already in force, or that will not be used', () => {
    expect(impactNotes(
      value({ autoPoApprovalMode: 'AUTO_SEND' }),
      value({ autoPoApprovalMode: 'AUTO_SEND', reservationTtlHours: 72 }))).toEqual([]);
    expect(impactNotes(value(), value({ autoPoEnabled: false, autoPoApprovalMode: 'AUTO_SEND' })).map(n => n.severity)).toEqual(['info']);
  });

  it('says shortfalls become manual when automatic purchase orders are turned off', () => {
    const notes = impactNotes(value(), value({ autoPoEnabled: false }));

    expect(notes).toEqual([{ severity: 'info', text: jasmine.stringContaining('handle each one by hand') } as any]);
  });

  it('warns that confirmed drop ship orders are stranded when drop shipping is turned off', () => {
    const notes = impactNotes(value({ dropShipEnabled: true }), value({ dropShipEnabled: false }));

    expect(notes.length).toBe(1);
    expect(notes[0].severity).toBe('warn');
    expect(notes[0].text).toContain('drop ship');
  });

  it('says a new reservation hold is not retroactive', () => {
    const notes = impactNotes(value(), value({ reservationTtlHours: 24 }));

    expect(notes.length).toBe(1);
    expect(notes[0].text).toContain('already exist keep their expiry');
  });

  it('has nothing to say about a change that carries no consequence', () => {
    expect(impactNotes(value(), value({
      intimationCcEmails: 'a@x.com', intimationDepartmentId: 1, supplierSelectionMode: 'MANUAL'
    }))).toEqual([]);
  });

  it('warns that every order has to ship when customer pickup is turned off', () => {
    const notes = impactNotes(value(), value({ selfPickupEnabled: false }));

    expect(notes.map(n => n.severity)).toEqual(['warn']);
    expect(notes[0].text).toContain('cannot be confirmed until they are changed to ship');
  });

  it('warns that an order goes out in one delivery when partial fulfilment is turned off', () => {
    const notes = impactNotes(value(), value({ partialFulfillmentAllowed: false }));

    expect(notes.map(n => n.severity)).toEqual(['warn']);
    expect(notes[0].text).toContain('one delivery');
  });

  it('warns that no email at all will be sent when email intimation is turned off', () => {
    const notes = impactNotes(value(), value({ emailIntimationEnabled: false }));

    expect(notes.map(n => n.severity)).toEqual(['warn']);
    expect(notes[0].text).toContain('No sale order email will be sent at all');
  });

  it('says what a new default fulfilment means for new orders, and that old orders are not touched', () => {
    const buy = impactNotes(value(), value({ defaultFulfillmentMode: 'BACK_TO_BACK' }));
    const drop = impactNotes(value(), value({ defaultFulfillmentMode: 'DROP_SHIP', dropShipEnabled: true }));
    const back = impactNotes(value({ defaultFulfillmentMode: 'BACK_TO_BACK' }), value());

    expect(buy[0].text).toContain('buy every line from a supplier');
    expect(buy[0].text).toContain('already taken are not changed');
    expect(drop[0].text).toContain('drop shipped');
    expect(back).withContext('back to letting stock decide needs no warning').toEqual([]);
  });

  it('says new orders will start as pickup when shipping is no longer the start', () => {
    const notes = impactNotes(value(), value({ shipmentRequiredDefault: false }));

    expect(notes).toEqual([{ severity: 'info', text: jasmine.stringContaining('start as customer pickup') } as any]);
  });

  it('has nothing to warn about when things are turned on', () => {
    expect(impactNotes(
      value({ selfPickupEnabled: false, partialFulfillmentAllowed: false, emailIntimationEnabled: false, shipmentRequiredDefault: false }),
      value())).toEqual([]);
  });
});
