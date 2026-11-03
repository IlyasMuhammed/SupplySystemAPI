import { Field } from '../core/hrm-form.component';
import { RecordsConfig } from '../core/hrm-records.component';
import { ENUMS, fullYearOptions, hours, hrmLabel, today, weekDaysText } from '../core/hrm.shared';

// Contract §6 — shifts, holidays, attendance, overtime, leave types, leave queue (tasks 106, 107, 122, 131, 138, 158).

const employee: Field = { key: 'employeeUuid', label: 'Employee', type: 'lookup', lookup: 'employees', required: true };

export const SHIFTS: RecordsConfig = {
  id: 'shifts', crumbs: ['HR', 'Attendance', 'Shifts'], title: 'Shifts', sub: 'Working hours, grace, half-day and overtime thresholds. Weekly offs do not count as absences.',
  listPath: 'hrm/shifts', empty: 'No shifts yet.',
  columns: [
    { key: 'code', label: 'Code', kind: 'mono' }, { key: 'name', label: 'Name', sub: r => r.isDefault ? 'Default shift' : '' },
    { key: 'hours', label: 'Hours', value: r => `${(r.startTime ?? '').slice(0, 5)} – ${(r.endTime ?? '').slice(0, 5)}` },
    { key: 'breakMinutes', label: 'Break (min)', kind: 'num' }, { key: 'graceInMinutes', label: 'Grace in', kind: 'num' },
    { key: 'weeklyOffDays', label: 'Weekly off', value: r => weekDaysText(r.weeklyOffDays) },
    { key: 'isActive', label: 'Status', kind: 'pill', value: r => r.isActive ? 'Active' : 'Inactive' }
  ],
  form: { noun: 'Shift', createPerm: ['HRM_SHIFT_MANAGE'], editPerm: ['HRM_SHIFT_MANAGE'], wide: true, fields: [
    { key: 'code', label: 'Code', required: true, maxLength: 20 }, { key: 'name', label: 'Name', required: true, maxLength: 100 },
    { key: 'startTime', label: 'Start', type: 'time', required: true }, { key: 'endTime', label: 'End', type: 'time', required: true },
    { key: 'breakMinutes', label: 'Break (minutes)', type: 'number', min: 0, step: 1, def: 60 }, { key: 'graceInMinutes', label: 'Grace in (minutes)', type: 'number', min: 0, step: 1, def: 15 },
    { key: 'graceOutMinutes', label: 'Grace out (minutes)', type: 'number', min: 0, step: 1, def: 15 }, { key: 'halfDayMinutes', label: 'Half day below (minutes)', type: 'number', min: 0, step: 1, def: 240 },
    { key: 'overtimeThresholdMinutes', label: 'Overtime after (minutes)', type: 'number', min: 0, step: 1, def: 30 },
    { key: 'weeklyOffDays', label: 'Weekly off', type: 'weekdays', full: true, def: 1 }, { key: 'isDefault', label: 'Default shift', type: 'bool' }
  ] },
  active: { perm: ['HRM_SHIFT_MANAGE'] },
  actions: [{ id: 'assign', label: 'Assign', icon: 'pi pi-users', perm: ['HRM_SHIFT_MANAGE'], path: r => `hrm/shifts/${r.uuid}/assign`, done: 'The shift is assigned from that date.', fields: [
    { key: 'effectiveFrom', label: 'Effective from', type: 'date', required: true, def: today() },
    { key: 'employees', label: 'Employees', type: 'rows', required: true, rows: [{ ...employee }] }
  ], body: (_, v) => ({ effectiveFrom: v['effectiveFrom'], employeeUuids: (v['employees'] as any[]).map(e => e.employeeUuid) }) }]
};

export const HOLIDAYS: RecordsConfig = {
  id: 'holidays', crumbs: ['HR', 'Attendance', 'Holidays'], title: 'Holiday calendar', sub: 'Holidays never count as leave or absence.',
  notice: 'Dates marked Estimated depend on the moon. Confirm them when they are notified: editing a date clears the mark.',
  listPath: 'hrm/holidays', empty: 'No holidays in this year.',
  filters: [{ key: 'year', label: 'Year', type: 'select', options: fullYearOptions(), def: new Date().getFullYear(), required: true }, { key: 'branchUuid', label: 'Branch', type: 'lookup', lookup: 'branches' }],
  columns: [
    { key: 'date', label: 'Date', kind: 'date' }, { key: 'name', label: 'Name' }, { key: 'countryCode', label: 'Country' },
    { key: 'isEstimated', label: 'Date', kind: 'pill', value: r => r.isEstimated ? 'Estimated' : 'Confirmed' }
  ],
  form: { noun: 'Holiday', createPerm: ['HRM_SHIFT_MANAGE'], editPerm: ['HRM_SHIFT_MANAGE'], fields: [
    { key: 'date', label: 'Date', type: 'date', required: true }, { key: 'name', label: 'Name', required: true, maxLength: 150 },
    { key: 'countryCode', label: 'Country code', maxLength: 2, def: 'PK', required: true }, { key: 'branchUuid', label: 'Branch only', type: 'lookup', lookup: 'branches', hint: 'Empty applies to every branch.' }
  ] },
  remove: { perm: ['HRM_SHIFT_MANAGE'], confirm: r => `Delete ${r.name}?` }
};

export const ATTENDANCE: RecordsConfig = {
  id: 'attendance', crumbs: ['HR', 'Attendance', 'Daily'], title: 'Daily attendance', sub: 'One row per employee per day, computed from events and the shift. An override stays against recomputation.',
  listPath: 'hrm/attendance/days', empty: 'No attendance in this range.',
  chips: { key: 'status', options: ENUMS.dayStatus },
  filters: [
    { key: 'from', label: 'From', type: 'date', def: today() }, { key: 'to', label: 'To', type: 'date', def: today() },
    { key: 'employeeUuid', label: 'Employee', type: 'lookup', lookup: 'employees' }, { key: 'departmentUuid', label: 'Department', type: 'lookup', lookup: 'departments' }, { key: 'branchUuid', label: 'Branch', type: 'lookup', lookup: 'branches' }
  ],
  columns: [
    { key: 'employeeName', label: 'Employee', sub: r => r.shiftName }, { key: 'date', label: 'Date', kind: 'date' },
    { key: 'firstInUtc', label: 'In', kind: 'datetime' }, { key: 'lastOutUtc', label: 'Out', kind: 'datetime' },
    { key: 'workedMinutes', label: 'Worked', value: r => hours(r.workedMinutes) }, { key: 'lateMinutes', label: 'Late (min)', kind: 'num' }, { key: 'overtimeMinutes', label: 'OT (min)', kind: 'num' },
    { key: 'status', label: 'Status', kind: 'pill', sub: r => r.isManualOverride ? `Override: ${r.overrideNote ?? ''}` : '' }
  ],
  view: { title: () => 'Monthly summary', load: r => `hrm/attendance/summary?employeeUuid=${r.employeeUuid}&year=${r.date.slice(0, 4)}&month=${Number(r.date.slice(5, 7))}`, kv: (r: any): [string, unknown][] => [['Working days', r.workingDays], ['Present', r.presentDays], ['Late', r.lateDays], ['Half days', r.halfDays], ['Absent', r.absentDays], ['On leave', r.leaveDays], ['Paid leave', r.paidLeaveDays], ['Unpaid leave', r.unpaidLeaveDays], ['Holidays', r.holidays], ['Weekly offs', r.weeklyOffs], ['Worked', hours(r.workedMinutes)], ['Overtime', hours(r.overtimeMinutes)], ['Approved overtime', hours(r.approvedOvertimeMinutes)]] },
  actions: [{ id: 'summary', label: 'Month', icon: 'pi pi-chart-bar', view: true }, { id: 'override', label: 'Override', icon: 'pi pi-pencil', perm: ['HRM_ATTENDANCE_MANAGE'], method: 'PUT', path: r => `hrm/attendance/days/${r.uuid}`, done: 'The day is overridden.', fields: [
    { key: 'status', label: 'Status', type: 'select', options: ENUMS.dayStatus, required: true }, { key: 'overrideNote', label: 'Why', type: 'textarea', required: true, maxLength: 300 }
  ] }],
  commands: [
    { id: 'event', label: 'Add event', icon: 'pi pi-plus', perm: ['HRM_ATTENDANCE_MANAGE'], primary: true, path: 'hrm/attendance/events', done: r => r?.duplicate ? 'That event was already recorded.' : 'The event is recorded and the day recomputed.', fields: [
      employee, { key: 'eventTimeUtc', label: 'When', type: 'datetime', required: true, hint: 'Your local time.' }, { key: 'eventType', label: 'Type', type: 'select', options: ENUMS.eventType, required: true, def: 'IN' }, { key: 'note', label: 'Note', maxLength: 200 }
    ], body: v => ({ ...v, source: 'MANUAL', eventTimeUtc: new Date(v['eventTimeUtc']).toISOString() }) },
    { id: 'import', label: 'Import CSV', icon: 'pi pi-upload', perm: ['HRM_ATTENDANCE_MANAGE'], upload: true, accept: '.csv', path: 'hrm/attendance/events/import',
      done: r => `${r?.imported ?? 0} of ${r?.total ?? 0} imported, ${r?.duplicates ?? 0} duplicates${r?.errors?.length ? `, ${r.errors.length} refused (first: row ${r.errors[0].row}, ${r.errors[0].message})` : ''}.` },
    { id: 'recompute', label: 'Recompute', icon: 'pi pi-sync', perm: ['HRM_ATTENDANCE_MANAGE'], path: 'hrm/attendance/days/recompute', done: 'The days are recomputed.', fields: [
      { key: 'from', label: 'From', type: 'date', required: true, def: today() }, { key: 'to', label: 'To', type: 'date', required: true, def: today() }, { key: 'employeeUuid', label: 'Employee', type: 'lookup', lookup: 'employees', hint: 'Empty recomputes everyone in your scope.' }
    ] }
  ]
};

export const OVERTIME: RecordsConfig = {
  id: 'overtime', crumbs: ['HR', 'Attendance', 'Overtime'], title: 'Overtime', sub: 'Only approved overtime reaches payroll.',
  listPath: 'hrm/overtime', empty: 'No overtime here.',
  chips: { key: 'status', options: ENUMS.approval, def: 'Pending' },
  filters: [{ key: 'from', label: 'From', type: 'date' }, { key: 'to', label: 'To', type: 'date' }, { key: 'employeeUuid', label: 'Employee', type: 'lookup', lookup: 'employees' }],
  columns: [
    { key: 'employeeName', label: 'Employee' }, { key: 'date', label: 'Date', kind: 'date' }, { key: 'minutes', label: 'Time', value: r => hours(r.minutes) }, { key: 'multiplier', label: 'Rate ×', kind: 'num' },
    { key: 'reason', label: 'Reason' }, { key: 'status', label: 'Status', kind: 'pill', sub: r => r.decidedByName ? `by ${r.decidedByName}` : '' }, { key: 'paid', label: 'Paid', kind: 'bool' }
  ],
  actions: [
    { id: 'approve', label: 'Approve', icon: 'pi pi-check', perm: ['HRM_OVERTIME_APPROVE'], when: r => r.status === 'Pending', path: r => `hrm/overtime/${r.uuid}/approve`, done: 'Overtime approved.' },
    { id: 'reject', label: 'Reject', icon: 'pi pi-times', danger: true, perm: ['HRM_OVERTIME_APPROVE'], when: r => r.status === 'Pending', path: r => `hrm/overtime/${r.uuid}/reject`, done: 'Overtime rejected.', fields: [{ key: 'note', label: 'Why', type: 'textarea', required: true, maxLength: 300 }] }
  ],
  commands: [{ id: 'new', label: 'Record overtime', icon: 'pi pi-plus', primary: true, perm: ['HRM_ATTENDANCE_MANAGE'], path: 'hrm/overtime', done: 'Overtime recorded.', fields: [
    employee, { key: 'date', label: 'Date', type: 'date', required: true, def: today() }, { key: 'minutes', label: 'Minutes', type: 'number', min: 1, step: 1, hint: 'Empty takes the minutes from that day.' },
    { key: 'multiplier', label: 'Rate multiplier', type: 'number', min: 0 }, { key: 'reason', label: 'Reason', type: 'textarea', maxLength: 300 }
  ] }]
};

export const LEAVE_TYPES: RecordsConfig = {
  id: 'leave-types', crumbs: ['HR', 'Leave', 'Leave types'], title: 'Leave types', sub: 'Types, carry-forward and entitlement policies. Year-end allocation and carry-forward are safe to repeat.',
  listPath: 'hrm/leave/types', empty: 'No leave types yet.',
  columns: [
    { key: 'code', label: 'Code', kind: 'mono' }, { key: 'name', label: 'Name' }, { key: 'isPaid', label: 'Paid', kind: 'bool' }, { key: 'carryForwardCap', label: 'Carry cap', kind: 'num' },
    { key: 'attachmentAfterDays', label: 'Attachment after (days)', kind: 'num' }, { key: 'genderRestriction', label: 'Only for', value: r => hrmLabel(r.genderRestriction) },
    { key: 'policies', label: 'Policies', value: r => (r.policies ?? []).map((p: any) => `${p.entitlement} ${hrmLabel(p.accrualMethod).toLowerCase()}`).join(', ') },
    { key: 'isActive', label: 'Status', kind: 'pill', value: r => r.isActive ? 'Active' : 'Inactive' }
  ],
  form: { noun: 'Leave type', createPerm: ['HRM_LEAVE_MANAGE'], editPerm: ['HRM_LEAVE_MANAGE'], createPath: 'hrm/leave/types', updatePath: r => `hrm/leave/types/${r.uuid}`, fields: [
    { key: 'code', label: 'Code', required: true, maxLength: 20 }, { key: 'name', label: 'Name', required: true, maxLength: 100 },
    { key: 'isPaid', label: 'Paid leave', type: 'bool', def: true }, { key: 'allowNegativeBalance', label: 'Balance may go negative', type: 'bool' },
    { key: 'carryForwardCap', label: 'Carry-forward cap (days)', type: 'number', min: 0 }, { key: 'attachmentAfterDays', label: 'Attachment needed after (days)', type: 'number', min: 0, step: 1 },
    { key: 'genderRestriction', label: 'Only for', type: 'select', options: ENUMS.gender }
  ] },
  active: { perm: ['HRM_LEAVE_MANAGE'], path: r => `hrm/leave/types/${r.uuid}/active` },
  actions: [{ id: 'policy', label: 'Add policy', icon: 'pi pi-plus', perm: ['HRM_LEAVE_MANAGE'], path: r => `hrm/leave/types/${r.uuid}/policies`, done: 'Policy added.', fields: [
    { key: 'gradeUuid', label: 'Grade', type: 'lookup', lookup: 'grades', hint: 'Empty applies to every grade.' }, { key: 'entitlement', label: 'Days per year', type: 'number', required: true, min: 0 },
    { key: 'accrualMethod', label: 'Accrual', type: 'select', options: ENUMS.accrual, required: true, def: 'Annual' }, { key: 'effectiveFrom', label: 'Effective from', type: 'date', required: true, def: today() }, { key: 'effectiveTo', label: 'Effective to', type: 'date' }
  ] }],
  commands: [
    { id: 'allocate', label: 'Allocate for all', icon: 'pi pi-users', perm: ['HRM_LEAVE_MANAGE'], path: 'hrm/leave/allocate/bulk', done: r => `${r?.allocated ?? 0} allocated, ${r?.skipped ?? 0} already had it.`, fields: [
      { key: 'leaveTypeUuid', label: 'Leave type', type: 'lookup', lookup: 'leaveTypes', required: true }, { key: 'year', label: 'Year', type: 'select', options: fullYearOptions(1, 1), required: true, def: new Date().getFullYear() }
    ] },
    { id: 'allocate-one', label: 'Allocate one', icon: 'pi pi-user-plus', perm: ['HRM_LEAVE_MANAGE'], path: 'hrm/leave/allocate', done: 'Days allocated.', fields: [
      employee, { key: 'leaveTypeUuid', label: 'Leave type', type: 'lookup', lookup: 'leaveTypes', required: true }, { key: 'days', label: 'Days', type: 'number', required: true }, { key: 'year', label: 'Year', type: 'select', options: fullYearOptions(1, 1), required: true, def: new Date().getFullYear() }, { key: 'adjustment', label: 'This is an adjustment (signed days, on top of an existing allocation)', type: 'bool' }, { key: 'note', label: 'Note', maxLength: 200 }
    ] },
    { id: 'carry', label: 'Carry forward', icon: 'pi pi-forward', perm: ['HRM_LEAVE_MANAGE'], path: 'hrm/leave/carry-forward', done: r => `${r?.processed ?? 0} processed, ${r?.carried ?? 0} days carried, ${r?.expired ?? 0} expired.`, fields: [
      { key: 'fromYear', label: 'From year', type: 'select', options: fullYearOptions(), required: true, def: new Date().getFullYear() - 1 }, { key: 'toYear', label: 'To year', type: 'select', options: fullYearOptions(), required: true, def: new Date().getFullYear() }
    ] }
  ]
};

const decideLeave = [
  { id: 'approve', label: 'Approve', icon: 'pi pi-check', perm: ['HRM_LEAVE_APPROVE'], when: (r: any) => r.status === 'Pending', path: (r: any) => `hrm/leave/requests/${r.uuid}/approve`, done: 'Leave approved.' },
  { id: 'reject', label: 'Reject', icon: 'pi pi-times', danger: true, perm: ['HRM_LEAVE_APPROVE'], when: (r: any) => r.status === 'Pending', path: (r: any) => `hrm/leave/requests/${r.uuid}/reject`, done: 'Leave rejected.', fields: [{ key: 'note', label: 'Why', type: 'textarea' as const, required: true, maxLength: 300 }] }
];

export const LEAVE_APPROVALS: RecordsConfig = {
  id: 'leave-approvals', crumbs: ['HR', 'Leave', 'Approvals'], title: 'Leave approvals', sub: 'The same requests also reach My Inbox through the workflow.',
  listPath: 'hrm/leave/requests', empty: 'Nothing is waiting for you.',
  chips: { key: 'status', options: ENUMS.leaveStatus, def: 'Pending' },
  filters: [{ key: 'employeeUuid', label: 'Employee', type: 'lookup', lookup: 'employees' }, { key: 'from', label: 'From', type: 'date' }, { key: 'to', label: 'To', type: 'date' }],
  columns: [
    { key: 'requestNumber', label: 'Request', kind: 'mono' }, { key: 'employeeName', label: 'Employee' }, { key: 'leaveTypeName', label: 'Type' },
    { key: 'dates', label: 'Dates', value: r => r.fromDate === r.toDate ? r.fromDate : `${r.fromDate} to ${r.toDate}`, sub: r => r.halfDay ? 'Half day' : '' }, { key: 'days', label: 'Days', kind: 'num' }, { key: 'reason', label: 'Reason' },
    { key: 'status', label: 'Status', kind: 'pill', sub: r => r.decisionNote }
  ],
  actions: [
    ...decideLeave,
    { id: 'cancel', label: 'Cancel', icon: 'pi pi-ban', danger: true, perm: ['HRM_LEAVE_MANAGE'], when: r => r.status === 'Pending' || r.status === 'Approved', path: r => `hrm/leave/requests/${r.uuid}/cancel`, confirm: r => `Cancel ${r.requestNumber}? The days go back to the balance.`, done: 'Leave cancelled.' }
  ]
};
