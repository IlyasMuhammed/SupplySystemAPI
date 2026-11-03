import { Field } from '../core/hrm-form.component';
import { RecordsConfig } from '../core/hrm-records.component';

// Contract §2 — the org-structure masters (tasks 076, 078; cost centers FD-9).

const code: Field = { key: 'code', label: 'Code', required: true, maxLength: 20 };
const name: Field = { key: 'name', label: 'Name', required: true, maxLength: 150, full: true };
const MANAGE = ['HRM_ORG_MANAGE'];

export const BRANCHES: RecordsConfig = {
  id: 'branches', crumbs: ['HR', 'Organization', 'Branches'], title: 'Branches', sub: 'Where people work. A department and an employment record point at one.',
  listPath: 'hrm/branches', empty: 'No branches yet. Add the first one.',
  columns: [
    { key: 'code', label: 'Code', kind: 'mono' }, { key: 'name', label: 'Name' }, { key: 'city', label: 'City' }, { key: 'address', label: 'Address' },
    { key: 'isActive', label: 'Status', kind: 'pill', value: r => r.isActive ? 'Active' : 'Inactive' }
  ],
  form: { noun: 'Branch', createPerm: MANAGE, editPerm: MANAGE, fields: [
    code, name, { key: 'city', label: 'City', maxLength: 100 }, { key: 'address', label: 'Address', type: 'textarea', maxLength: 500 }
  ], toBody: v => ({ code: v['code'], name: v['name'], city: v['city'], address: v['address'], warehouseUuid: v['warehouseUuid'] ?? null }) },
  active: { perm: MANAGE }
};

export const COST_CENTERS: RecordsConfig = {
  id: 'cost-centers', crumbs: ['HR', 'Organization', 'Cost centers'], title: 'Cost centers', sub: 'What salary expense is charged to. The payroll cost center also carries the liabilities.',
  listPath: 'hrm/cost-centers', empty: 'No cost centers yet.',
  columns: [
    { key: 'code', label: 'Code', kind: 'mono' }, { key: 'name', label: 'Name', sub: r => r.isPayrollCostCenter ? 'Payroll cost center' : '' },
    { key: 'isActive', label: 'Status', kind: 'pill', value: r => r.isActive ? 'Active' : 'Inactive' }
  ],
  form: { noun: 'Cost center', createPerm: MANAGE, editPerm: MANAGE, fields: [
    code, name, { key: 'parentUuid', label: 'Parent', type: 'lookup', lookup: 'costCenters' }, { key: 'branchUuid', label: 'Branch', type: 'lookup', lookup: 'branches' }
  ], toBody: v => ({ code: v['code'], name: v['name'], parentUuid: v['parentUuid'], branchUuid: v['branchUuid'] }) },
  active: { perm: MANAGE }
};

export const DESIGNATIONS: RecordsConfig = {
  id: 'designations', crumbs: ['HR', 'Organization', 'Designations'], title: 'Designations', sub: 'Job titles, ranked by level.',
  listPath: 'hrm/designations', empty: 'No designations yet.',
  columns: [
    { key: 'code', label: 'Code', kind: 'mono' }, { key: 'name', label: 'Name' }, { key: 'level', label: 'Level', kind: 'num' },
    { key: 'isActive', label: 'Status', kind: 'pill', value: r => r.isActive ? 'Active' : 'Inactive' }
  ],
  form: { noun: 'Designation', createPerm: MANAGE, editPerm: MANAGE, fields: [
    code, name, { key: 'level', label: 'Level', type: 'number', required: true, min: 0, step: 1, def: 1 }
  ], toBody: v => ({ code: v['code'], name: v['name'], level: v['level'] }) },
  active: { perm: MANAGE }
};

export const GRADES: RecordsConfig = {
  id: 'grades', crumbs: ['HR', 'Organization', 'Grades'], title: 'Grades', sub: 'Pay bands. The band is shown only to holders of salary view; saving without it keeps the stored band.',
  listPath: 'hrm/grades', empty: 'No grades yet.',
  columns: [
    { key: 'code', label: 'Code', kind: 'mono' }, { key: 'name', label: 'Name' },
    { key: 'minSalary', label: 'Band from', kind: 'money', masked: r => r.salaryMasked }, { key: 'maxSalary', label: 'Band to', kind: 'money', masked: r => r.salaryMasked },
    { key: 'isActive', label: 'Status', kind: 'pill', value: r => r.isActive ? 'Active' : 'Inactive' }
  ],
  form: { noun: 'Grade', createPerm: MANAGE, editPerm: MANAGE, fields: [
    code, name,
    { key: 'minSalary', label: 'Band from', type: 'number', min: 0, visible: v => !v['salaryMasked'] },
    { key: 'maxSalary', label: 'Band to', type: 'number', min: 0, visible: v => !v['salaryMasked'], check: (max, v) => max !== null && max !== '' && v['minSalary'] !== null && v['minSalary'] !== '' && Number(max) < Number(v['minSalary']) ? 'Band to cannot be below band from.' : null }
  ], toBody: v => ({ code: v['code'], name: v['name'], minSalary: v['minSalary'], maxSalary: v['maxSalary'] }) },
  active: { perm: MANAGE }
};
