import { Field } from '../core/hrm-form.component';
import { ENUMS, today } from '../core/hrm.shared';

// Contract §3–§5. PUT replaces the whole record, so everything here goes back on save; cnic and ntn: null = keep, "" = clear.

const addressFields = (prefix: string, title: string): Field[] => [
  { key: `${prefix}Line1`, label: `${title}: line 1`, maxLength: 200 }, { key: `${prefix}City`, label: 'City', maxLength: 100 }, { key: `${prefix}Province`, label: 'Province', maxLength: 100 }, { key: `${prefix}Country`, label: 'Country', maxLength: 60 }
];

export const PERSONAL_FIELDS: Field[] = [
  { key: 'firstName', label: 'First name', required: true, maxLength: 60 }, { key: 'lastName', label: 'Last name', required: true, maxLength: 60 }, { key: 'fatherOrHusbandName', label: 'Father / husband name', maxLength: 150 },
  { key: 'dateOfBirth', label: 'Date of birth', type: 'date' }, { key: 'gender', label: 'Gender', type: 'select', options: ENUMS.gender }, { key: 'maritalStatus', label: 'Marital status', type: 'select', options: ENUMS.marital }, { key: 'nationality', label: 'Nationality', maxLength: 60 },
  { key: 'workEmail', label: 'Work email', maxLength: 200 }, { key: 'personalEmail', label: 'Personal email', maxLength: 200 }, { key: 'phone', label: 'Phone', maxLength: 30 }, { key: 'mobile', label: 'Mobile', maxLength: 30 },
  { key: 'joinDate', label: 'Join date', type: 'date' },
  { key: 'userId', label: 'Login user', type: 'lookup', lookup: 'users', hint: 'Links this person to a login, so My HR (leave, payslips, attendance) works for that user. One login per employee.' },
  ...addressFields('current', 'Current address'), ...addressFields('permanent', 'Permanent address'),
  { key: 'emergencyContacts', label: 'Emergency contacts', type: 'rows', rows: [{ key: 'name', label: 'Name', required: true, maxLength: 150 }, { key: 'relation', label: 'Relation', maxLength: 50 }, { key: 'phone', label: 'Phone', maxLength: 30 }] },
  { key: 'notes', label: 'Notes', type: 'textarea', maxLength: 2000 }
];

/** New: typed plain. Edit: empty keeps what is stored; the masked value is shown beside the form. */
export const IDENTITY_FIELDS: Field[] = [
  { key: 'cnic', label: 'CNIC', maxLength: 15, placeholder: '#####-#######-#' }, { key: 'ntn', label: 'NTN', maxLength: 20 }
];

export const HIRE_FIELDS: Field[] = [
  { key: 'status', label: 'Status', type: 'select', options: ENUMS.createStatus, required: true, def: 'Probation' },
  { key: 'effectiveFrom', label: 'Effective from', type: 'date', required: true, def: today() },
  { key: 'employmentType', label: 'Employment type', type: 'select', options: ENUMS.employmentType, required: true, def: 'Permanent' },
  { key: 'departmentUuid', label: 'Department', type: 'lookup', lookup: 'departments', required: true }, { key: 'designationUuid', label: 'Designation', type: 'lookup', lookup: 'designations', required: true }, { key: 'branchUuid', label: 'Branch', type: 'lookup', lookup: 'branches', required: true },
  { key: 'gradeUuid', label: 'Grade', type: 'lookup', lookup: 'grades' }, { key: 'managerUuid', label: 'Manager', type: 'lookup', lookup: 'employees' }, { key: 'shiftUuid', label: 'Shift', type: 'lookup', lookup: 'shifts' }, { key: 'costCenterUuid', label: 'Cost center override', type: 'lookup', lookup: 'costCenters' },
  { key: 'probationEndDate', label: 'Probation ends', type: 'date' }, { key: 'contractEndDate', label: 'Contract ends', type: 'date' }, { key: 'noticePeriodDays', label: 'Notice (days)', type: 'number', min: 0, step: 1, def: 30 }, { key: 'payrollGroup', label: 'Payroll group', maxLength: 30 }
];

export const RECORD_FIELDS: Field[] = [
  ...HIRE_FIELDS.filter(f => f.key !== 'status'),
  { key: 'changeReason', label: 'Reason', type: 'select', options: ENUMS.changeReason, required: true, def: 'Other' }, { key: 'notes', label: 'Notes', type: 'textarea', maxLength: 500 },
  { key: 'costSplits', label: 'Cost split (must total 100%)', type: 'rows', rows: [{ key: 'costCenterUuid', label: 'Cost center', type: 'lookup', lookup: 'costCenters', required: true }, { key: 'percent', label: 'Percent', type: 'number', required: true, min: 0, max: 100 }],
    check: v => { const list = (v ?? []) as any[]; return list.length && Math.abs(list.reduce((s, r) => s + Number(r.percent || 0), 0) - 100) > 0.001 ? 'The cost split must total 100%.' : null; } }
];

export const TRANSFER_FIELDS: Field[] = [
  { key: 'effectiveFrom', label: 'Effective from', type: 'date', required: true, def: today() }, { key: 'departmentUuid', label: 'Department', type: 'lookup', lookup: 'departments' }, { key: 'branchUuid', label: 'Branch', type: 'lookup', lookup: 'branches' },
  { key: 'managerUuid', label: 'Manager', type: 'lookup', lookup: 'employees' }, { key: 'costCenterUuid', label: 'Cost center', type: 'lookup', lookup: 'costCenters' }, { key: 'notes', label: 'Notes', type: 'textarea', maxLength: 500 }
];

export const PROMOTION_FIELDS: Field[] = [
  { key: 'effectiveFrom', label: 'Effective from', type: 'date', required: true, def: today() }, { key: 'designationUuid', label: 'Designation', type: 'lookup', lookup: 'designations' }, { key: 'gradeUuid', label: 'Grade', type: 'lookup', lookup: 'grades' },
  { key: 'managerUuid', label: 'Manager', type: 'lookup', lookup: 'employees' }, { key: 'notes', label: 'Notes', type: 'textarea', maxLength: 500 }
];

export const BANK_FIELDS: Field[] = [
  { key: 'bankName', label: 'Bank', required: true, maxLength: 100 }, { key: 'branchName', label: 'Branch', maxLength: 100 }, { key: 'accountTitle', label: 'Account title', maxLength: 150 },
  { key: 'accountNumber', label: 'Account number', maxLength: 40, hint: 'When editing, empty keeps what is stored.' }, { key: 'iban', label: 'IBAN', maxLength: 40 },
  { key: 'paymentMethod', label: 'Paid by', type: 'select', options: ENUMS.bankMethod, required: true, def: 'Bank' }, { key: 'isPrimary', label: 'Primary account', type: 'bool' }
];

export const DOCUMENT_FIELDS: Field[] = [
  { key: 'documentType', label: 'Type', type: 'select', options: ENUMS.documentType, required: true, def: 'OTHER' }, { key: 'title', label: 'Title', required: true, maxLength: 150 }, { key: 'expiryDate', label: 'Expires', type: 'date' }
];

export const STATUS_FIELDS: Field[] = [
  { key: 'status', label: 'New status', type: 'select', options: [], required: true },
  { key: 'effectiveDate', label: 'Effective', type: 'date', def: today(), visible: v => v['status'] === 'Terminated' || v['status'] === 'OnNotice', required: true },
  { key: 'departureReason', label: 'Reason', type: 'select', options: ENUMS.departure, visible: v => v['status'] === 'Terminated' || v['status'] === 'OnNotice', required: true }
];

/** The flat form values ↔ the API's nested addresses. */
export function employeeToForm(e: any): Record<string, any> {
  const a = (x: any, p: string) => ({ [`${p}Line1`]: x?.line1 ?? null, [`${p}City`]: x?.city ?? null, [`${p}Province`]: x?.province ?? null, [`${p}Country`]: x?.country ?? null });
  return { ...e, ...a(e.currentAddress, 'current'), ...a(e.permanentAddress, 'permanent'), emergencyContacts: e.emergencyContacts ?? [], cnic: null, ntn: null };
}

export function formToEmployee(v: Record<string, any>, previous?: any): Record<string, any> {
  const a = (p: string, old: any) => ({ ...(old ?? {}), line1: v[`${p}Line1`], city: v[`${p}City`], province: v[`${p}Province`], country: v[`${p}Country`] });
  return {
    firstName: v['firstName'], lastName: v['lastName'], fatherOrHusbandName: v['fatherOrHusbandName'], dateOfBirth: v['dateOfBirth'], gender: v['gender'], maritalStatus: v['maritalStatus'], nationality: v['nationality'],
    personalEmail: v['personalEmail'], workEmail: v['workEmail'], phone: v['phone'], mobile: v['mobile'], currentAddress: a('current', previous?.currentAddress), permanentAddress: a('permanent', previous?.permanentAddress),
    emergencyContacts: v['emergencyContacts'], photoAttachmentUuid: previous?.photoAttachmentUuid ?? null, userId: v['userId'] !== undefined ? (v['userId'] ?? null) : (previous?.userId ?? null), joinDate: v['joinDate'], leaveDate: previous?.leaveDate ?? null, departureReason: previous?.departureReason ?? null, notes: v['notes'],
    sourceCandidateUuid: previous?.sourceCandidateUuid ?? null,
    // §3 — null keeps the stored value; the form cannot show these, so empty never clears by accident.
    cnic: v['cnic'] || null, ntn: v['ntn'] || null
  };
}
