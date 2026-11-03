import { Field } from '../core/hrm-form.component';
import { RecordsConfig } from '../core/hrm-records.component';
import { ENUMS, today } from '../core/hrm.shared';
import { HRM_ROUTE } from '../core/hrm.nav';

// Contract §8 — recruitment lists, review cycles, reviews, training (tasks 209, 221, 229). Board, candidate and review form are their own pages.

const REC = ['HRM_RECRUITMENT_MANAGE'];
const PERF = ['HRM_PERFORMANCE_MANAGE'];
const TRAIN = ['HRM_TRAINING_MANAGE'];
const employee: Field = { key: 'employeeUuid', label: 'Employee', type: 'lookup', lookup: 'employees', required: true };

export const VACANCIES: RecordsConfig = {
  id: 'vacancies', crumbs: ['HR', 'Recruitment', 'Vacancies'], title: 'Vacancies', sub: 'Open a vacancy to move its applications across the stages.',
  listPath: 'hrm/vacancies', empty: 'No vacancies here.',
  chips: { key: 'status', options: ENUMS.vacancyStatus, def: 'Open' },
  columns: [
    { key: 'vacancyNumber', label: 'Vacancy', kind: 'mono', link: r => [`${HRM_ROUTE}/recruitment/vacancies`, r.uuid] }, { key: 'title', label: 'Title', sub: r => r.designationName }, { key: 'departmentName', label: 'Department' },
    { key: 'positions', label: 'Positions', kind: 'num' }, { key: 'closingDate', label: 'Closes', kind: 'date' },
    { key: 'applications', label: 'Applications', kind: 'num', value: r => Object.values<number>(r.applicationCounts ?? {}).reduce((a, b) => a + Number(b), 0) }, { key: 'status', label: 'Status', kind: 'pill' }
  ],
  form: { noun: 'Vacancy', createPerm: REC, editPerm: REC, wide: true, fields: [
    { key: 'title', label: 'Title', required: true, maxLength: 150, full: true }, { key: 'designationUuid', label: 'Designation', type: 'lookup', lookup: 'designations', required: true }, { key: 'departmentUuid', label: 'Department', type: 'lookup', lookup: 'departments', required: true },
    { key: 'branchUuid', label: 'Branch', type: 'lookup', lookup: 'branches' }, { key: 'positions', label: 'Positions', type: 'number', required: true, min: 1, step: 1, def: 1 }, { key: 'closingDate', label: 'Closing date', type: 'date' }, { key: 'description', label: 'Description', type: 'textarea', maxLength: 2000 }
  ] },
  actions: [{ id: 'status', label: 'Set status', icon: 'pi pi-flag', perm: REC, method: 'PATCH', path: r => `hrm/vacancies/${r.uuid}/status`, done: 'Vacancy status changed.', fields: [{ key: 'status', label: 'Status', type: 'select', options: ENUMS.vacancyStatus, required: true }] }]
};

export const CANDIDATE_FIELDS: Field[] = [
  { key: 'firstName', label: 'First name', required: true, maxLength: 100 }, { key: 'lastName', label: 'Last name', maxLength: 100 }, { key: 'email', label: 'Email', maxLength: 200 }, { key: 'phone', label: 'Phone', maxLength: 30 },
  { key: 'source', label: 'Source', maxLength: 100 }, { key: 'notes', label: 'Notes', type: 'textarea', maxLength: 2000 },
  { key: 'force', label: 'Save even if the email or phone matches another candidate', type: 'bool', hint: 'Only after the duplicate warning.' }
];

export const CANDIDATES: RecordsConfig = {
  id: 'candidates', crumbs: ['HR', 'Recruitment', 'Candidates'], title: 'Candidates', sub: 'People who applied, with their resumes and applications.',
  listPath: 'hrm/candidates', empty: 'No candidates found.',
  filters: [{ key: 'search', label: 'Search name, email, phone', type: 'search' }],
  columns: [
    { key: 'name', label: 'Candidate', value: r => `${r.firstName} ${r.lastName ?? ''}`.trim(), link: r => [`${HRM_ROUTE}/recruitment/candidates`, r.uuid] }, { key: 'email', label: 'Email' }, { key: 'phone', label: 'Phone' }, { key: 'source', label: 'Source' },
    { key: 'applications', label: 'Applications', kind: 'num', value: r => (r.applications ?? []).length }
  ],
  form: { noun: 'Candidate', createPerm: REC, fields: CANDIDATE_FIELDS },
  remove: { perm: REC, confirm: r => `Delete ${r.firstName} ${r.lastName ?? ''}?` }
};

export const CYCLES: RecordsConfig = {
  id: 'cycles', crumbs: ['HR', 'Performance', 'Review cycles'], title: 'Review cycles', sub: 'Opening a cycle creates a review for everyone employed.',
  listPath: 'hrm/review-cycles', empty: 'No review cycles yet.',
  columns: [
    { key: 'name', label: 'Cycle' }, { key: 'period', label: 'Period' }, { key: 'startDate', label: 'From', kind: 'date' }, { key: 'endDate', label: 'To', kind: 'date' }, { key: 'done', label: 'Submitted', value: r => r.completion ? `${r.completion.submitted} of ${r.completion.total}` : '—', sub: r => r.completion ? `${r.completion.acknowledged} acknowledged` : '' },
    { key: 'status', label: 'Status', kind: 'pill' }
  ],
  form: { noun: 'Review cycle', createPerm: PERF, editPerm: PERF, canEdit: r => r.status === 'Draft', fields: [
    { key: 'name', label: 'Name', required: true, maxLength: 100, full: true }, { key: 'period', label: 'Period', type: 'select', options: ENUMS.reviewPeriod, required: true, def: 'Annual' },
    { key: 'startDate', label: 'Start', type: 'date', required: true }, { key: 'endDate', label: 'End', type: 'date', required: true }
  ] },
  actions: [
    { id: 'reviews', label: 'Reviews', icon: 'pi pi-list', nav: r => [`${HRM_ROUTE}/performance/reviews`], when: r => r.status !== 'Draft' },
    { id: 'open', label: 'Open', icon: 'pi pi-play', perm: PERF, when: r => r.status === 'Draft', path: r => `hrm/review-cycles/${r.uuid}/open`, confirm: r => `Open ${r.name}? A review is created for everyone employed.`, done: 'The cycle is open.' },
    { id: 'close', label: 'Close', icon: 'pi pi-lock', danger: true, perm: PERF, when: r => r.status === 'Open', path: r => `hrm/review-cycles/${r.uuid}/close`, confirm: r => `Close ${r.name}?`, done: 'The cycle is closed.' }
  ]
};

export const REVIEWS: RecordsConfig = {
  id: 'reviews', crumbs: ['HR', 'Performance', 'Reviews'], title: 'Reviews', sub: 'Goals, ratings and comments per employee.',
  listPath: 'hrm/reviews', empty: 'No reviews here.',
  chips: { key: 'status', options: ENUMS.reviewStatus },
  filters: [{ key: 'cycleUuid', label: 'Cycle', type: 'lookup', lookup: 'cycles' }],
  columns: [{ key: 'employeeName', label: 'Employee', link: r => [`${HRM_ROUTE}/performance/reviews`, r.uuid] }, { key: 'cycleName', label: 'Cycle' }, { key: 'reviewerName', label: 'Reviewer' }, { key: 'overallRating', label: 'Rating', kind: 'num' }, { key: 'status', label: 'Status', kind: 'pill' }]
};

export const COURSES: RecordsConfig = {
  id: 'courses', crumbs: ['HR', 'Training', 'Courses'], title: 'Course catalog', listPath: 'hrm/courses', empty: 'No courses yet.',
  columns: [
    { key: 'code', label: 'Code', kind: 'mono' }, { key: 'name', label: 'Course', sub: r => r.description }, { key: 'provider', label: 'Provider' }, { key: 'durationHours', label: 'Hours', kind: 'num' }, { key: 'cost', label: 'Cost', kind: 'money' },
    { key: 'isActive', label: 'Status', kind: 'pill', value: r => r.isActive ? 'Active' : 'Inactive' }
  ],
  form: { noun: 'Course', createPerm: TRAIN, editPerm: TRAIN, fields: [
    { key: 'code', label: 'Code', required: true, maxLength: 20 }, { key: 'name', label: 'Name', required: true, maxLength: 150 }, { key: 'provider', label: 'Provider', maxLength: 150 }, { key: 'durationHours', label: 'Hours', type: 'number', min: 0 }, { key: 'cost', label: 'Cost', type: 'number', min: 0 }, { key: 'description', label: 'Description', type: 'textarea', maxLength: 1000 }
  ] },
  actions: [{ id: 'enroll', label: 'Enroll', icon: 'pi pi-user-plus', perm: TRAIN, when: r => r.isActive !== false, path: () => 'hrm/enrollments', done: 'Enrolled.', fields: [employee, { key: 'enrolledDate', label: 'Enrolled on', type: 'date', required: true, def: today() }], body: (r, v) => ({ ...v, courseUuid: r.uuid }) }]
};

export const ENROLLMENTS: RecordsConfig = {
  id: 'enrollments', crumbs: ['HR', 'Training', 'Enrollments'], title: 'Enrollments', listPath: 'hrm/enrollments', empty: 'No enrollments.',
  filters: [{ key: 'courseUuid', label: 'Course', type: 'lookup', lookup: 'courses' }, { key: 'employeeUuid', label: 'Employee', type: 'lookup', lookup: 'employees' }],
  columns: [{ key: 'employeeName', label: 'Employee' }, { key: 'courseName', label: 'Course' }, { key: 'enrolledDate', label: 'Enrolled', kind: 'date' }, { key: 'completedDate', label: 'Completed', kind: 'date' }, { key: 'status', label: 'Status', kind: 'pill' }],
  form: { noun: 'Enrollment', createPerm: TRAIN, fields: [employee, { key: 'courseUuid', label: 'Course', type: 'lookup', lookup: 'courses', required: true }, { key: 'enrolledDate', label: 'Enrolled on', type: 'date', required: true, def: today() }] },
  actions: [
    { id: 'complete', label: 'Complete', icon: 'pi pi-check', perm: TRAIN, when: r => r.status === 'Enrolled', path: r => `hrm/enrollments/${r.uuid}/complete`, done: 'Marked complete.', fields: [
      { key: 'completedDate', label: 'Completed on', type: 'date', required: true, def: today() }, { key: 'certificateAttachmentUuid', label: 'Certificate', type: 'file', interfaceCode: 'HRM_ENROLLMENT' }
    ] },
    { id: 'cancel', label: 'Cancel', icon: 'pi pi-ban', danger: true, perm: TRAIN, when: r => r.status === 'Enrolled', path: r => `hrm/enrollments/${r.uuid}/cancel`, confirm: r => `Cancel ${r.employeeName} on ${r.courseName}?`, done: 'Enrollment cancelled.' }
  ]
};
