import { Field } from '../core/hrm-form.component';
import { RecordsConfig } from '../core/hrm-records.component';
import { ENUMS, today } from '../core/hrm.shared';
import { HRM_ROUTE } from '../core/hrm.nav';

// Contract §7, §9 — salary masters, statutory data, payroll runs list, adjustments (tasks 170, 199, 276, 278).

const CONFIG = ['HRM_PAYROLL_CONFIG'];
const VERIFY = ['HRM_STATUTORY_VERIFY'];
const employee: Field = { key: 'employeeUuid', label: 'Employee', type: 'lookup', lookup: 'employees', required: true };

export const UNVERIFIED_NOTICE = 'Seeded tax and statutory values are UNVERIFIED and need a qualified payroll consultant. Payroll cannot be finalized until someone with the verify permission has reviewed them.';

export const COMPONENTS: RecordsConfig = {
  id: 'components', crumbs: ['HR', 'Payroll', 'Salary components'], title: 'Salary components', sub: 'Earnings and deductions. A system component keeps its code and type.',
  listPath: 'hrm/salary-components', empty: 'No components yet.',
  columns: [
    { key: 'code', label: 'Code', kind: 'mono' }, { key: 'name', label: 'Name', sub: r => r.isSystem ? 'System' : '' }, { key: 'type', label: 'Type', kind: 'pill' }, { key: 'calcMethod', label: 'Method', sub: r => r.formula ?? (r.percentOf ? `of ${r.percentOf}` : '') },
    { key: 'defaultValue', label: 'Default', kind: 'num' }, { key: 'isTaxable', label: 'Taxable', kind: 'bool' }, { key: 'inGross', label: 'In gross', kind: 'bool' }, { key: 'postingRole', label: 'Account role' },
    { key: 'isActive', label: 'Status', kind: 'pill', value: r => r.isActive ? 'Active' : 'Inactive' }
  ],
  form: { noun: 'Component', createPerm: CONFIG, editPerm: CONFIG, wide: true, fields: [
    { key: 'code', label: 'Code', required: true, maxLength: 30, visible: v => !v['isSystem'] }, { key: 'name', label: 'Name', required: true, maxLength: 100 },
    { key: 'type', label: 'Type', type: 'select', options: ENUMS.componentType, required: true, def: 'Earning', visible: v => !v['isSystem'] },
    { key: 'calcMethod', label: 'Method', type: 'select', options: ENUMS.calcMethod, required: true, def: 'Fixed' },
    { key: 'defaultValue', label: 'Default value', type: 'number', visible: v => v['calcMethod'] !== 'Formula' },
    { key: 'percentOf', label: 'Percent of (code)', maxLength: 30, visible: v => v['calcMethod'] === 'Percent', required: true },
    { key: 'formula', label: 'Formula', type: 'textarea', maxLength: 500, visible: v => v['calcMethod'] === 'Formula', required: true, hint: 'Check it in Formula Tester: + - * / ( ) MIN MAX IF ROUND SLAB, BASIC GROSS TOTAL_EARNINGS TAXABLE_INCOME WORKING_DAYS PAID_DAYS OT_HOURS, earlier codes.' },
    { key: 'postingRole', label: 'Posting role', maxLength: 60 }, { key: 'expenseRole', label: 'Expense role', maxLength: 60 },
    { key: 'sortOrder', label: 'Sort order', type: 'number', step: 1, def: 100 }, { key: 'isTaxable', label: 'Taxable', type: 'bool' }, { key: 'inGross', label: 'Counts in gross', type: 'bool', def: true }
  ] },
  active: { perm: CONFIG }
};

const verifyActions = (base: string) => [
  { id: 'verify', label: 'Mark reviewed', icon: 'pi pi-verified', perm: VERIFY, when: (r: any) => !r.isVerified, path: (r: any) => `${base}/${r.uuid}/verify`, done: 'Marked as reviewed.', fields: [
    { key: 'note', label: 'Reviewed against', type: 'textarea' as const, required: true, maxLength: 500, hint: 'Say who reviewed it and against which source.' }
  ] },
  { id: 'unverify', label: 'Withdraw review', icon: 'pi pi-undo', danger: true, perm: VERIFY, when: (r: any) => r.isVerified, path: (r: any) => `${base}/${r.uuid}/unverify`, confirm: () => 'Withdraw the review? Payroll using this cannot be finalized until it is reviewed again.', done: 'Review withdrawn.' }
];

export const TAX_TABLES: RecordsConfig = {
  id: 'tax-tables', crumbs: ['HR', 'Payroll', 'Tax tables'], title: 'Tax tables', sub: 'Annual income slabs. Editing a reviewed table clears its review.', notice: UNVERIFIED_NOTICE,
  listPath: 'hrm/tax-tables', empty: 'No tax tables yet.',
  columns: [
    { key: 'code', label: 'Code', kind: 'mono' }, { key: 'name', label: 'Name', sub: r => r.sourceNote }, { key: 'countryCode', label: 'Country' }, { key: 'effectiveFrom', label: 'From', kind: 'date' }, { key: 'effectiveTo', label: 'To', kind: 'date' },
    { key: 'slabs', label: 'Slabs', value: r => (r.slabs ?? []).length }, { key: 'isVerified', label: 'Review', kind: 'pill', value: r => r.isVerified ? 'Reviewed' : 'Not reviewed', sub: r => r.isVerified ? (r.verifiedByName ? `by ${r.verifiedByName}` : '') : 'UNVERIFIED' }
  ],
  form: { noun: 'Tax table', createPerm: CONFIG, editPerm: CONFIG, wide: true, fields: [
    { key: 'code', label: 'Code', required: true, maxLength: 30 }, { key: 'name', label: 'Name', required: true, maxLength: 150 }, { key: 'countryCode', label: 'Country code', required: true, maxLength: 2, def: 'PK' },
    { key: 'effectiveFrom', label: 'Effective from', type: 'date', required: true }, { key: 'effectiveTo', label: 'Effective to', type: 'date' }, { key: 'sourceNote', label: 'Source', type: 'textarea', maxLength: 500 },
    { key: 'slabs', label: 'Slabs', type: 'rows', required: true, rows: [
      { key: 'fromAmount', label: 'From', type: 'number', required: true, min: 0 }, { key: 'toAmount', label: 'To', type: 'number', min: 0 }, { key: 'fixedAmount', label: 'Fixed', type: 'number', required: true, min: 0, def: 0 }, { key: 'ratePercent', label: 'Rate %', type: 'number', required: true, min: 0, max: 100, def: 0 }
    ] }
  ] },
  actions: verifyActions('hrm/tax-tables')
};

export const STATUTORY_RATES: RecordsConfig = {
  id: 'statutory-rates', crumbs: ['HR', 'Payroll', 'Statutory rates'], title: 'Statutory rates', sub: 'EOBI, provident fund, social security, gratuity. Editing a reviewed rate clears its review.', notice: UNVERIFIED_NOTICE,
  listPath: 'hrm/statutory-rates', empty: 'No rates yet.',
  columns: [
    { key: 'rateType', label: 'Type', kind: 'mono' }, { key: 'name', label: 'Name', sub: r => r.sourceNote }, { key: 'employeePercent', label: 'Employee %', kind: 'num' }, { key: 'employerPercent', label: 'Employer %', kind: 'num' },
    { key: 'wageCeiling', label: 'Wage ceiling', kind: 'money' }, { key: 'effectiveFrom', label: 'From', kind: 'date' }, { key: 'effectiveTo', label: 'To', kind: 'date' },
    { key: 'isVerified', label: 'Review', kind: 'pill', value: r => r.isVerified ? 'Reviewed' : 'Not reviewed', sub: r => r.isVerified ? '' : 'UNVERIFIED' }
  ],
  form: { noun: 'Statutory rate', createPerm: CONFIG, editPerm: CONFIG, wide: true, fields: [
    { key: 'rateType', label: 'Type', type: 'select', options: ENUMS.statutoryType, required: true }, { key: 'name', label: 'Name', required: true, maxLength: 150 }, { key: 'countryCode', label: 'Country code', required: true, maxLength: 2, def: 'PK' },
    { key: 'employeePercent', label: 'Employee %', type: 'number', required: true, min: 0, max: 100, def: 0 }, { key: 'employerPercent', label: 'Employer %', type: 'number', required: true, min: 0, max: 100, def: 0 },
    { key: 'wageCeiling', label: 'Wage ceiling', type: 'number', min: 0 }, { key: 'fixedBasis', label: 'Fixed basis', type: 'number', min: 0 },
    { key: 'effectiveFrom', label: 'Effective from', type: 'date', required: true }, { key: 'effectiveTo', label: 'Effective to', type: 'date' }, { key: 'sourceNote', label: 'Source', type: 'textarea', maxLength: 500 }
  ] },
  actions: verifyActions('hrm/statutory-rates')
};

export const PAYROLL_RUNS: RecordsConfig = {
  id: 'payroll-runs', crumbs: ['HR', 'Payroll', 'Runs'], title: 'Payroll runs', sub: 'Open a run to calculate, review, approve, finalize and pay it.',
  listPath: 'hrm/payroll-runs', empty: 'No payroll runs yet.',
  chips: { key: 'status', options: ENUMS.runStatus },
  filters: [{ key: 'from', label: 'Period from', type: 'date' }, { key: 'to', label: 'Period to', type: 'date' }],
  columns: [
    { key: 'runNumber', label: 'Run', kind: 'mono', link: r => [`${HRM_ROUTE}/payroll/runs`, r.uuid], sub: r => r.payrollGroup ? `Group ${r.payrollGroup}` : '' },
    { key: 'period', label: 'Period', value: r => `${r.periodStart} to ${r.periodEnd}` }, { key: 'payDate', label: 'Pay date', kind: 'date' },
    { key: 'totalEmployees', label: 'Employees', kind: 'num', sub: r => [r.blockedEmployees ? `${r.blockedEmployees} blocked` : '', r.untrackedAttendanceEmployees ? `${r.untrackedAttendanceEmployees} no attendance` : ''].filter(Boolean).join(', ') }, { key: 'totalGross', label: 'Gross', kind: 'money' }, { key: 'totalNet', label: 'Net', kind: 'money', sub: r => r.currencyCode },
    { key: 'status', label: 'Status', kind: 'pill' }
  ],
  commands: [{ id: 'new', label: 'New payroll run', icon: 'pi pi-plus', primary: true, perm: ['HRM_PAYROLL_RUN'], nav: [`${HRM_ROUTE}/payroll/runs/new`] }]
};

export const ADJUSTMENTS: RecordsConfig = {
  id: 'adjustments', crumbs: ['HR', 'Payroll', 'Adjustments'], title: 'Payroll adjustments', sub: 'One-off amounts the next run picks up. Cannot be deleted once applied.',
  listPath: 'hrm/payroll-adjustments', empty: 'No adjustments.',
  filters: [{ key: 'employeeUuid', label: 'Employee', type: 'lookup', lookup: 'employees' }, { key: 'pending', label: 'State', type: 'select', options: [{ label: 'Pending', value: true }, { label: 'Applied', value: false }] }],
  columns: [{ key: 'employeeName', label: 'Employee' }, { key: 'componentCode', label: 'Component', kind: 'mono' }, { key: 'amount', label: 'Amount', kind: 'money' }, { key: 'reason', label: 'Reason' }, { key: 'state', label: 'State', kind: 'pill', value: r => r.appliedInRunUuid ? 'Applied' : 'Pending' }],
  form: { noun: 'Adjustment', createPerm: ['HRM_PAYROLL_RUN'], fields: [
    employee, { key: 'componentCode', label: 'Component', type: 'lookup', lookup: 'componentCodes', required: true }, { key: 'amount', label: 'Amount', type: 'number', required: true }, { key: 'reason', label: 'Reason', type: 'textarea', required: true, maxLength: 300 }
  ] },
  remove: { perm: ['HRM_PAYROLL_RUN'], when: r => !r.appliedInRunUuid, confirm: r => `Delete this adjustment for ${r.employeeName}?` }
};

export const NEW_RUN_FIELDS: Field[] = [
  { key: 'periodStart', label: 'Period start', type: 'date', required: true },
  { key: 'periodEnd', label: 'Period end', type: 'date', required: true, check: (end, v) => end && v['periodStart'] && end < v['periodStart'] ? 'Period end cannot be before its start.' : null },
  { key: 'payDate', label: 'Pay date', type: 'date', required: true, def: today() },
  { key: 'payrollGroup', label: 'Payroll group', maxLength: 30, hint: 'Empty is the default group.' }
];
