import { Field } from '../core/hrm-form.component';
import { Column, RecordsConfig, RowAction } from '../core/hrm-records.component';
import { ENUMS, today } from '../core/hrm.shared';
import { HRM_ROUTE } from '../core/hrm.nav';

// Contract §11, §12 — loans, settlement, increments, letters, tax certificates, expenses, remittances (tasks 354, 364, 369, 377, 378, 396, 397).

const employee: Field = { key: 'employeeUuid', label: 'Employee', type: 'lookup', lookup: 'employees', required: true };
const paidFrom: Field = { key: 'paidFromAccount', label: 'Paid from', type: 'select', options: ENUMS.paidFrom, required: true, def: 'Bank' };
const reference: Field = { key: 'reference', label: 'Reference', maxLength: 60 };
const payFields = (dateKey: string, dateLabel: string): Field[] => [{ key: dateKey, label: dateLabel, type: 'date', required: true, def: today() }, paidFrom, reference];
const postingState = (r: any) => r.postingState && r.postingState !== 'None' ? r.postingState : '';

const retry = (base: string, perm: string[]): RowAction => ({ id: 'retry', label: 'Retry posting', icon: 'pi pi-refresh', perm, when: r => r.postingState === 'Failed' || r.paymentPostingState === 'Failed', path: r => `${base}/${r.uuid}/retry-posting`, done: 'Posting retried.' });

const LOAN_LINES: Column[] = [
  { key: 'sequence', label: '#', kind: 'num' }, { key: 'dueMonth', label: 'Due', value: r => (r.dueMonth ?? '').slice(0, 7) }, { key: 'amount', label: 'Amount', kind: 'money' }, { key: 'status', label: 'Status', kind: 'pill' }, { key: 'runNumber', label: 'Run', kind: 'mono' }
];

const loanView = {
  title: (r: any) => `Loan ${r.loanNumber}`, load: (r: any) => `hrm/loans/${r.uuid}`, wide: true,
  kv: (r: any): [string, unknown][] => [['Employee', r.employeeName], ['Type', r.type], ['Principal', r.principal], ['Monthly', r.monthlyAmount], ['Outstanding', r.outstanding], ['Status', r.status], ['Reason', r.reason], ['Disbursed on', r.disbursedOn], ['Posting', r.postingNote ?? r.postingState]],
  table: { title: 'Repayment schedule', rows: (r: any) => r.installments ?? [], columns: LOAN_LINES, action: {
    id: 'waive', label: 'Waive', icon: 'pi pi-minus-circle', danger: true, perm: ['HRM_LOAN_APPROVE'], when: (i: any) => i.status === 'Pending', path: (i: any) => `hrm/loans/installments/${i.uuid}/waive`, done: 'Installment waived.',
    fields: [{ key: 'note', label: 'Why', type: 'textarea' as const, required: true, maxLength: 300 }]
  } }
};

const LOAN_COLS: Column[] = [
  { key: 'loanNumber', label: 'Loan', kind: 'mono' }, { key: 'employeeName', label: 'Employee' }, { key: 'type', label: 'Type' }, { key: 'principal', label: 'Principal', kind: 'money' }, { key: 'monthlyAmount', label: 'Monthly', kind: 'money', sub: r => `${r.installmentCount} installments` },
  { key: 'outstanding', label: 'Outstanding', kind: 'money' }, { key: 'status', label: 'Status', kind: 'pill', sub: r => postingState(r) ? `Posting: ${postingState(r)}` : '' }
];

export const LOANS: RecordsConfig = {
  id: 'loans', crumbs: ['HR', 'Loans', 'Loans & advances'], title: 'Loans & advances', sub: 'Application, approval, disbursement, repayment through payroll.',
  listPath: 'hrm/loans', empty: 'No loans here.',
  chips: { key: 'status', options: ENUMS.loanStatus },
  filters: [{ key: 'employeeUuid', label: 'Employee', type: 'lookup', lookup: 'employees' }],
  columns: LOAN_COLS,
  form: { noun: 'Loan', createPerm: ['HRM_LOAN_MANAGE'], editPerm: ['HRM_LOAN_MANAGE'], canEdit: r => r.status === 'Draft', fields: [
    employee, { key: 'type', label: 'Type', type: 'select', options: ENUMS.loanType, required: true, def: 'Advance' }, { key: 'principal', label: 'Principal', type: 'number', required: true, min: 1 },
    { key: 'installmentCount', label: 'Installments', type: 'number', required: true, min: 1, step: 1, def: 1 }, { key: 'startMonth', label: 'First deduction', type: 'month', required: true }, { key: 'reason', label: 'Reason', type: 'textarea', maxLength: 500 }
  ] },
  view: loanView,
  actions: [
    { id: 'view', label: 'Schedule', icon: 'pi pi-list', view: true },
    { id: 'approve', label: 'Approve', icon: 'pi pi-check', perm: ['HRM_LOAN_APPROVE'], when: r => r.status === 'Draft', path: r => `hrm/loans/${r.uuid}/approve`, confirm: r => `Approve ${r.loanNumber} for ${r.employeeName}?`, done: 'Loan approved.' },
    { id: 'reject', label: 'Reject', icon: 'pi pi-times', danger: true, perm: ['HRM_LOAN_APPROVE'], when: r => r.status === 'Draft', path: r => `hrm/loans/${r.uuid}/reject`, done: 'Loan rejected.', fields: [{ key: 'note', label: 'Why', type: 'textarea', required: true, maxLength: 300 }] },
    { id: 'disburse', label: 'Disburse', icon: 'pi pi-send', perm: ['HRM_LOAN_MANAGE'], when: r => r.status === 'Approved', path: r => `hrm/loans/${r.uuid}/disburse`, done: 'Loan disbursed.', fields: payFields('disbursedOn', 'Disbursed on') },
    retry('hrm/loans', ['HRM_LOAN_MANAGE'])
  ]
};

const SETTLEMENT_LINES: Column[] = [
  { key: 'code', label: 'Code', kind: 'mono' }, { key: 'description', label: 'Description', sub: r => r.basis }, { key: 'type', label: 'Type' }, { key: 'amount', label: 'Amount', kind: 'money' }
];

export const SETTLEMENTS: RecordsConfig = {
  id: 'settlements', crumbs: ['HR', 'Separation', 'Final settlements'], title: 'Final settlements', sub: 'Remaining salary, leave encashment and gratuity less loans and advances. Unverified gratuity blocks posting.',
  listPath: 'hrm/settlements', empty: 'No settlements here.',
  chips: { key: 'status', options: ENUMS.settlementStatus },
  columns: [
    { key: 'settlementNumber', label: 'Settlement', kind: 'mono' }, { key: 'employeeName', label: 'Employee' }, { key: 'lastWorkingDay', label: 'Last day', kind: 'date' }, { key: 'totalEarnings', label: 'Earnings', kind: 'money' }, { key: 'totalDeductions', label: 'Deductions', kind: 'money' }, { key: 'net', label: 'Net', kind: 'money' },
    { key: 'status', label: 'Status', kind: 'pill', sub: r => r.postingNote }
  ],
  view: { title: r => `Settlement ${r.settlementNumber}`, load: r => `hrm/settlements/${r.uuid}`, wide: true,
    kv: r => [['Employee', r.employeeName], ['Last working day', r.lastWorkingDay], ['Service (years)', r.serviceYears], ['Last drawn salary', r.lastDrawnSalary], ['Earnings', r.totalEarnings], ['Deductions', r.totalDeductions], ['Net', r.net], ['Status', r.status], ['Paid on', r.paidOn], ['Posting', r.postingNote ?? r.postingState]],
    table: { title: 'Lines', rows: r => r.lines ?? [], columns: SETTLEMENT_LINES } },
  actions: [
    { id: 'view', label: 'View', icon: 'pi pi-eye', view: true },
    { id: 'approve', label: 'Approve', icon: 'pi pi-check', perm: ['HRM_SETTLEMENT_MANAGE'], when: r => r.status === 'Draft', path: r => `hrm/settlements/${r.uuid}/approve`, confirm: r => `Approve ${r.settlementNumber} for ${r.employeeName}?`, done: 'Settlement approved.' },
    { id: 'post', label: 'Post', icon: 'pi pi-book', perm: ['HRM_SETTLEMENT_MANAGE'], when: r => r.status === 'Approved', path: r => `hrm/settlements/${r.uuid}/post`, confirm: r => `Post ${r.settlementNumber}?`, done: 'Settlement posted.' },
    { id: 'pay', label: 'Pay', icon: 'pi pi-wallet', perm: ['HRM_SETTLEMENT_MANAGE'], when: r => r.status === 'Posted', path: r => `hrm/settlements/${r.uuid}/pay`, done: 'Settlement paid.', fields: payFields('paidOn', 'Paid on') },
    { id: 'cancel', label: 'Cancel', icon: 'pi pi-ban', danger: true, perm: ['HRM_SETTLEMENT_MANAGE'], when: r => r.status === 'Draft' || r.status === 'Approved', path: r => `hrm/settlements/${r.uuid}/cancel`, confirm: r => `Cancel ${r.settlementNumber}?`, done: 'Settlement cancelled.' },
    retry('hrm/settlements', ['HRM_SETTLEMENT_MANAGE'])
  ],
  form: { noun: 'Settlement line edits', editPerm: ['HRM_SETTLEMENT_MANAGE'], canEdit: r => r.status === 'Draft', load: r => `hrm/settlements/${r.uuid}`, wide: true, fields: [
    { key: 'notes', label: 'Notes', type: 'textarea', maxLength: 1000 },
    { key: 'lines', label: 'Lines', type: 'rows', rows: [{ key: 'code', label: 'Code', required: true, maxLength: 20 }, { key: 'description', label: 'Description', required: true, maxLength: 200 }, { key: 'type', label: 'Type', type: 'select', options: [{ label: 'Earning', value: 'Earning' }, { label: 'Deduction', value: 'Deduction' }], required: true }, { key: 'amount', label: 'Amount', type: 'number', required: true, min: 0 }] }
  ] },
  commands: [{ id: 'compute', label: 'Compute settlement', icon: 'pi pi-calculator', primary: true, perm: ['HRM_SETTLEMENT_MANAGE'], path: 'hrm/settlements/compute', done: r => `Draft ${r?.settlementNumber ?? ''} computed.`, fields: [employee, { key: 'lastWorkingDay', label: 'Last working day', type: 'date', required: true, def: today() }] }]
};

export const INCREMENTS: RecordsConfig = {
  id: 'increments', crumbs: ['HR', 'Separation', 'Increments'], title: 'Increment proposals', sub: 'Linked from performance reviews. Amounts show only to holders of salary view.',
  listPath: 'hrm/increments', empty: 'No proposals here.',
  chips: { key: 'status', options: ENUMS.incrementStatus },
  filters: [{ key: 'cycleUuid', label: 'Review cycle', type: 'lookup', lookup: 'cycles' }],
  columns: [
    { key: 'employeeName', label: 'Employee', sub: r => r.reason }, { key: 'currentBasic', label: 'Current basic', kind: 'money', masked: () => true }, { key: 'newBasic', label: 'New basic', kind: 'money', masked: () => true }, { key: 'percent', label: '%', kind: 'num' },
    { key: 'effectiveDate', label: 'Effective', kind: 'date' }, { key: 'status', label: 'Status', kind: 'pill', sub: r => r.decidedByName }
  ],
  form: { noun: 'Proposal', createPerm: ['HRM_INCREMENT_MANAGE'], fields: [
    employee, { key: 'percent', label: 'Percent', type: 'number', min: 0, hint: 'Give a percent or a new basic.' }, { key: 'newBasic', label: 'New basic', type: 'number', min: 0, check: (nb, v) => (nb === null || nb === '') && (v['percent'] === null || v['percent'] === '') ? 'Give a percent or a new basic.' : null },
    { key: 'effectiveDate', label: 'Effective', type: 'date', required: true }, { key: 'reason', label: 'Reason', type: 'textarea', maxLength: 500 }
  ] },
  actions: [
    { id: 'approve', label: 'Approve', icon: 'pi pi-check', perm: ['HRM_INCREMENT_APPROVE'], when: r => r.status === 'Draft', path: r => `hrm/increments/${r.uuid}/approve`, done: 'Proposal approved.' },
    { id: 'reject', label: 'Reject', icon: 'pi pi-times', danger: true, perm: ['HRM_INCREMENT_APPROVE'], when: r => r.status === 'Draft', path: r => `hrm/increments/${r.uuid}/reject`, confirm: r => `Reject the proposal for ${r.employeeName}?`, done: 'Proposal rejected.' },
    { id: 'apply', label: 'Apply', icon: 'pi pi-play', perm: ['HRM_INCREMENT_APPROVE'], when: r => r.status === 'Approved', path: r => `hrm/increments/${r.uuid}/apply`, confirm: r => `Apply to ${r.employeeName}'s salary? The previous assignment is superseded.`, done: 'Applied to the salary.' }
  ],
  commands: [{ id: 'bulk', label: 'From review cycle', icon: 'pi pi-users', perm: ['HRM_INCREMENT_MANAGE'], path: 'hrm/increments/bulk', done: 'Proposals created.', fields: [
    { key: 'reviewCycleUuid', label: 'Review cycle', type: 'lookup', lookup: 'cycles', required: true },
    { key: 'bands', label: 'Percent by rating', type: 'rows', required: true, rows: [{ key: 'rating', label: 'Rating', required: true, maxLength: 10 }, { key: 'percent', label: 'Percent', type: 'number', required: true, min: 0 }] }
  ], body: v => ({ reviewCycleUuid: v['reviewCycleUuid'], percentByRating: Object.fromEntries((v['bands'] as any[]).map(b => [b.rating, b.percent])) }) }]
};

export const LETTER_TEMPLATES: RecordsConfig = {
  id: 'letter-templates', crumbs: ['HR', 'Letters', 'Templates'], title: 'Letter templates', sub: 'Placeholders: {EmployeeName} {EmployeeNumber} {Designation} {Department} {JoinDate} {LeaveDate} {Salary} {OrganizationName} {Date}. {Salary} needs salary view.',
  listPath: 'hrm/letter-templates', empty: 'No templates yet.',
  columns: [{ key: 'code', label: 'Code', kind: 'mono' }, { key: 'name', label: 'Name', sub: r => r.isSystem ? 'System' : '' }, { key: 'kind', label: 'Kind', kind: 'pill' }, { key: 'subject', label: 'Subject' }, { key: 'isActive', label: 'Status', kind: 'pill', value: r => r.isActive ? 'Active' : 'Inactive' }],
  form: { noun: 'Template', createPerm: ['HRM_LETTER_ISSUE'], editPerm: ['HRM_LETTER_ISSUE'], wide: true, fields: [
    { key: 'code', label: 'Code', required: true, maxLength: 30 }, { key: 'name', label: 'Name', required: true, maxLength: 100 }, { key: 'kind', label: 'Kind', type: 'select', options: ENUMS.letterKind, required: true, def: 'Custom' },
    { key: 'subject', label: 'Subject', required: true, maxLength: 200, full: true }, { key: 'body', label: 'Body', type: 'textarea', required: true, maxLength: 8000 }
  ] }
};

export const LETTERS: RecordsConfig = {
  id: 'letters', crumbs: ['HR', 'Letters', 'Issued'], title: 'Issued letters', listPath: 'hrm/letters', empty: 'No letters issued.',
  filters: [{ key: 'employeeUuid', label: 'Employee', type: 'lookup', lookup: 'employees' }],
  columns: [{ key: 'letterNumber', label: 'Letter', kind: 'mono' }, { key: 'employeeName', label: 'Employee' }, { key: 'kind', label: 'Kind', kind: 'pill' }, { key: 'issuedAt', label: 'Issued', kind: 'datetime', sub: r => r.issuedByName }],
  actions: [{ id: 'pdf', label: 'PDF', icon: 'pi pi-file-pdf', download: r => `hrm/letters/${r.uuid}/pdf`, fileName: r => `${r.letterNumber}.pdf` }],
  commands: [{ id: 'issue', label: 'Issue letter', icon: 'pi pi-plus', primary: true, perm: ['HRM_LETTER_ISSUE'], path: 'hrm/letters', done: r => `Letter ${r?.letterNumber ?? ''} issued.`, fields: [employee, { key: 'templateUuid', label: 'Template', type: 'lookup', lookup: 'templates', required: true }] }]
};

export const TAX_CERTIFICATES: RecordsConfig = {
  id: 'tax-certificates', crumbs: ['HR', 'Letters', 'Tax certificates'], title: 'Tax certificates', sub: 'A certificate says it is unverified while any rate it used is.', listPath: 'hrm/tax-certificates', empty: 'No certificates for this year.',
  filters: [{ key: 'taxYear', label: 'Tax year', type: 'number', def: new Date().getFullYear() }],
  columns: [{ key: 'employeeName', label: 'Employee' }, { key: 'taxYear', label: 'Tax year' }, { key: 'totalIncome', label: 'Income', kind: 'money' }, { key: 'totalTax', label: 'Tax', kind: 'money' }, { key: 'generatedAt', label: 'Generated', kind: 'datetime', sub: r => r.isUnverified ? 'Uses unverified rates' : '' }],
  actions: [{ id: 'pdf', label: 'PDF', icon: 'pi pi-file-pdf', download: r => `hrm/tax-certificates/${r.uuid}/pdf`, fileName: r => `tax-certificate-${r.taxYear}-${r.employeeName}.pdf` }],
  commands: [{ id: 'generate', label: 'Generate', icon: 'pi pi-cog', primary: true, perm: ['HRM_TAX_CERT_VIEW'], path: 'hrm/tax-certificates/generate', done: 'Generated.', fields: [{ key: 'taxYear', label: 'Tax year', type: 'number', required: true, step: 1, def: new Date().getFullYear() }, { key: 'employeeUuid', label: 'Employee', type: 'lookup', lookup: 'employees', hint: 'Empty generates for everyone.' }] }]
};

const CLAIM_LINES: Column[] = [
  { key: 'categoryName', label: 'Category' }, { key: 'expenseDate', label: 'Date', kind: 'date' }, { key: 'description', label: 'Description' }, { key: 'amount', label: 'Amount', kind: 'money' }, { key: 'taxAmount', label: 'Tax', kind: 'money' }, { key: 'costCenterCode', label: 'Cost center', kind: 'mono' }
];

export const claimView = {
  title: (r: any) => `Claim ${r.claimNumber}`, load: (r: any) => `hrm/expense-claims/${r.uuid}`, wide: true,
  kv: (r: any): [string, unknown][] => [['Employee', r.employeeName], ['Claim date', r.claimDate], ['Status', r.status], ['Total', `${r.totalAmount} ${r.currencyCode ?? ''}`], ['Paid by', r.paymentMode], ['Settled', r.settlementMethod], ['Description', r.description], ['Decision', r.decisionNote], ['Paid', r.paidOn ?? r.paidInRunNumber], ['Posting', r.postingNote ?? r.postingState]],
  table: { title: 'Lines', rows: (r: any) => r.lines ?? [], columns: CLAIM_LINES }
};

export const CLAIMS: RecordsConfig = {
  id: 'claims', crumbs: ['HR', 'Expenses', 'Claims'], title: 'Expense claims', sub: 'Approve, then pay separately or with the next payroll.', listPath: 'hrm/expense-claims', empty: 'No claims here.',
  chips: { key: 'status', options: ENUMS.claimStatus, def: 'Submitted' },
  filters: [{ key: 'employeeUuid', label: 'Employee', type: 'lookup', lookup: 'employees' }],
  columns: [
    { key: 'claimNumber', label: 'Claim', kind: 'mono' }, { key: 'employeeName', label: 'Employee' }, { key: 'claimDate', label: 'Date', kind: 'date' }, { key: 'settlementMethod', label: 'Settled', value: r => r.settlementMethod === 'WithPayroll' ? 'With payroll' : 'Separate' }, { key: 'totalAmount', label: 'Amount', kind: 'money', sub: r => r.currencyCode },
    { key: 'status', label: 'Status', kind: 'pill', sub: r => postingState(r) ? `Posting: ${postingState(r)}` : '' }
  ],
  view: claimView,
  actions: [
    { id: 'view', label: 'View', icon: 'pi pi-eye', view: true },
    { id: 'approve', label: 'Approve', icon: 'pi pi-check', perm: ['HRM_EXPENSE_APPROVE'], when: r => r.status === 'Submitted', path: r => `hrm/expense-claims/${r.uuid}/approve`, confirm: r => `Approve ${r.claimNumber}? It is posted now.`, done: 'Claim approved.' },
    { id: 'reject', label: 'Reject', icon: 'pi pi-times', danger: true, perm: ['HRM_EXPENSE_APPROVE'], when: r => r.status === 'Submitted', path: r => `hrm/expense-claims/${r.uuid}/reject`, done: 'Claim rejected.', fields: [{ key: 'note', label: 'Why', type: 'textarea', required: true, maxLength: 300 }] },
    { id: 'pay', label: 'Pay', icon: 'pi pi-wallet', perm: ['HRM_EXPENSE_PAY'], when: r => r.status === 'Approved' && r.settlementMethod === 'SeparatePayment' && r.paymentMode !== 'CompanyAccount', path: r => `hrm/expense-claims/${r.uuid}/pay`, done: 'Claim paid.', fields: payFields('paidOn', 'Paid on') },
    retry('hrm/expense-claims', ['HRM_EXPENSE_PAY', 'HRM_EXPENSE_APPROVE'])
  ]
};

const CAT_EDIT = ['HRM_ACCOUNT_MAPPING_EDIT', 'HRM_SETTINGS_MANAGE'];

export const CATEGORIES: RecordsConfig = {
  id: 'categories', crumbs: ['HR', 'Expenses', 'Categories'], title: 'Expense categories', sub: 'A category with no account cannot be approved. Seeds carry no account on purpose.', listPath: 'hrm/expense-categories', empty: 'No categories yet.',
  columns: [
    { key: 'code', label: 'Code', kind: 'mono' }, { key: 'name', label: 'Name' }, { key: 'accountCode', label: 'Account', kind: 'mono', sub: r => r.accountRole ?? (r.accountCode ? '' : 'Not mapped') }, { key: 'defaultTaxCode', label: 'Tax code' },
    { key: 'requiresReceipt', label: 'Receipt', value: r => r.requiresReceipt ? (r.receiptThreshold ? `Above ${r.receiptThreshold}` : 'Always') : 'No' }, { key: 'ratePerUnit', label: 'Rate / unit', value: r => r.ratePerUnit ? `${r.ratePerUnit} / ${r.unitName ?? 'unit'}` : '' },
    { key: 'isActive', label: 'Status', kind: 'pill', value: r => r.isActive ? 'Active' : 'Inactive' }
  ],
  form: { noun: 'Category', createPerm: CAT_EDIT, editPerm: CAT_EDIT, fields: [
    { key: 'code', label: 'Code', required: true, maxLength: 20 }, { key: 'name', label: 'Name', required: true, maxLength: 100 }, { key: 'accountCode', label: 'Account code', maxLength: 30 }, { key: 'accountRole', label: 'Account role', maxLength: 60 }, { key: 'defaultTaxCode', label: 'Default tax code', maxLength: 20 },
    { key: 'requiresReceipt', label: 'Needs a receipt', type: 'bool' }, { key: 'receiptThreshold', label: 'Receipt above', type: 'number', min: 0, visible: v => !!v['requiresReceipt'] }, { key: 'ratePerUnit', label: 'Rate per unit', type: 'number', min: 0 }, { key: 'unitName', label: 'Unit name', maxLength: 20 }
  ] },
  active: { perm: CAT_EDIT }
};

export const REMITTANCES: RecordsConfig = {
  id: 'remittances', crumbs: ['HR', 'Expenses', 'Remittances'], title: 'Payments to authorities', sub: 'What was paid to the tax office, EOBI, PF and social security.', listPath: 'hrm/remittances', empty: 'No payments recorded.',
  filters: [{ key: 'type', label: 'Type', type: 'select', options: ENUMS.remittanceType }, { key: 'from', label: 'From', type: 'date' }, { key: 'to', label: 'To', type: 'date' }],
  columns: [{ key: 'remittanceType', label: 'Type', kind: 'pill' }, { key: 'period', label: 'Period', value: r => `${r.periodStart} to ${r.periodEnd}` }, { key: 'amount', label: 'Amount', kind: 'money' }, { key: 'paidOn', label: 'Paid', kind: 'date', sub: r => r.reference }, { key: 'authorityName', label: 'Authority' }, { key: 'postingState', label: 'Posting', kind: 'pill' }],
  actions: [retry('hrm/remittances', ['HRM_REMITTANCE_MANAGE'])]
};

export const REMITTANCE_FIELDS: Field[] = [
  { key: 'remittanceType', label: 'Type', type: 'select', options: ENUMS.remittanceType, required: true }, { key: 'amount', label: 'Amount', type: 'number', required: true, min: 0.01 },
  { key: 'periodStart', label: 'Period start', type: 'date', required: true }, { key: 'periodEnd', label: 'Period end', type: 'date', required: true },
  { key: 'paidOn', label: 'Paid on', type: 'date', required: true, def: today() }, paidFrom, reference, { key: 'authorityName', label: 'Authority', maxLength: 150 }, { key: 'notes', label: 'Notes', type: 'textarea', maxLength: 500 },
  { key: 'force', label: 'Record even if this overpays the period', type: 'bool', hint: 'Only after the overpaid warning.' }
];
REMITTANCES.commands = [{ id: 'new', label: 'Record payment', icon: 'pi pi-plus', primary: true, perm: ['HRM_REMITTANCE_MANAGE'], path: 'hrm/remittances', done: 'Payment recorded.', fields: REMITTANCE_FIELDS }];

// Task 397 � calculated vs paid vs balance per period; the payments themselves are a second list behind it.
export const REMITTANCE_TRACKER: RecordsConfig = {
  id: 'remittance-tracker', crumbs: ['HR', 'Expenses', 'Remittances'], title: 'Statutory remittances', sub: 'Calculated by payroll, paid to the authority, and what is still owed.', listPath: 'hrm/remittances/tracker', empty: 'Nothing calculated for this year.',
  filters: [{ key: 'year', label: 'Year', type: 'number', def: new Date().getFullYear() }, { key: 'type', label: 'Type', type: 'select', options: ENUMS.remittanceType }],
  columns: [{ key: 'type', label: 'Type', kind: 'pill' }, { key: 'period', label: 'Period', value: r => `${r.periodStart} to ${r.periodEnd}` }, { key: 'calculated', label: 'Calculated', kind: 'money' }, { key: 'paid', label: 'Paid', kind: 'money' }, { key: 'balance', label: 'Balance', kind: 'money' }],
  tiles: rows => [
    { l: 'Calculated', v: rows.reduce((s, r) => s + Number(r.calculated || 0), 0).toLocaleString('en-US', { minimumFractionDigits: 2 }) },
    { l: 'Paid', v: rows.reduce((s, r) => s + Number(r.paid || 0), 0).toLocaleString('en-US', { minimumFractionDigits: 2 }) },
    { l: 'Balance', v: rows.reduce((s, r) => s + Number(r.balance || 0), 0).toLocaleString('en-US', { minimumFractionDigits: 2 }) }
  ],
  commands: [
    { id: 'new', label: 'Record payment', icon: 'pi pi-plus', primary: true, perm: ['HRM_REMITTANCE_MANAGE'], path: 'hrm/remittances', done: 'Payment recorded.', fields: REMITTANCE_FIELDS },
    { id: 'payments', label: 'Payments', icon: 'pi pi-list', nav: [`${HRM_ROUTE}/expenses/remittances/payments`] }
  ]
};
