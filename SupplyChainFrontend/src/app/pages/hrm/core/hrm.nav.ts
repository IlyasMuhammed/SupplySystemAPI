// A49 — the HRM pages as data. The sidebar (app.menu.ts) and the routes (hrm.routes.ts) are both built from this one table,
// so a menu entry and its route guard cannot disagree. Each `perms` is the contract's read gate for what the page loads first
// (docs/hrm/API-CONTRACT.md), any one of them. No component is imported here: the menu stays free of the lazy HRM chunk.

export const HRM_ROUTE = '/portal/pages/hrm';
export const MODULE_HRM = 'MODULE_HRM';

export const F = {
  ATTENDANCE: 'FEATURE_HRM_ATTENDANCE', LEAVE: 'FEATURE_HRM_LEAVE', PAYROLL: 'FEATURE_HRM_PAYROLL', RECRUITMENT: 'FEATURE_HRM_RECRUITMENT',
  PERFORMANCE: 'FEATURE_HRM_PERFORMANCE', TRAINING: 'FEATURE_HRM_TRAINING', LOANS: 'FEATURE_HRM_LOANS', EXPENSES: 'FEATURE_HRM_EXPENSES'
} as const;

export interface HrmPage {
  /** Route segment under `hrm/`; also the key the route table loads by. */
  path: string;
  label: string;
  icon: string;
  perms: string[];
  feature?: string;
  /** Any one of these sub-features opens the page (the controller's `[RequiresFeature(a, b)]`). */
  anyFeature?: string[];
}

export interface HrmGroup { label: string; icon: string; feature?: string; anyFeature?: string[]; pages: HrmPage[]; }

const page = (path: string, label: string, icon: string, perms: string[], feature?: string, anyFeature?: string[]): HrmPage => ({ path, label, icon: `pi pi-fw ${icon}`, perms, feature, ...(anyFeature ? { anyFeature } : {}) });

/** REV49B-4: shifts and holidays open with attendance OR leave (AttControllers: leave counts working days). */
export const SHIFT_FEATURES = [F.ATTENDANCE, F.LEAVE];

export const EMPLOYEE_READ = ['HRM_EMPLOYEE_VIEW', 'HRM_EMPLOYEE_EDIT'];
export const ORG_READ = ['HRM_ORG_VIEW', 'HRM_ORG_MANAGE'];

export const HRM_GROUPS: HrmGroup[] = [
  { label: 'My HR', icon: 'pi pi-fw pi-user', pages: [
    page('me/profile', 'My Profile', 'pi-id-card', ['HRM_SELF_SERVICE']),
    page('me/attendance', 'My Attendance', 'pi-clock', ['HRM_SELF_SERVICE'], F.ATTENDANCE),
    page('me/leave', 'My Leave', 'pi-calendar-plus', ['HRM_SELF_SERVICE'], F.LEAVE),
    page('me/payslips', 'My Payslips', 'pi-money-bill', ['HRM_SELF_SERVICE'], F.PAYROLL),
    page('me/expenses', 'My Expenses', 'pi-receipt', ['HRM_SELF_SERVICE'], F.EXPENSES),
    page('me/loans', 'My Loans', 'pi-wallet', ['HRM_SELF_SERVICE'], F.LOANS),
    page('me/reviews', 'My Reviews', 'pi-star', ['HRM_SELF_SERVICE'], F.PERFORMANCE),
    page('me/assigned-reviews', 'Reviews Assigned to Me', 'pi-users', ['HRM_SELF_SERVICE'], F.PERFORMANCE),
    page('me/training', 'My Training', 'pi-book', ['HRM_SELF_SERVICE'], F.TRAINING),
    page('me/letters', 'My Letters', 'pi-envelope', ['HRM_SELF_SERVICE'], F.LOANS),
    page('me/tax-certificates', 'My Tax Certificates', 'pi-verified', ['HRM_SELF_SERVICE'], F.LOANS)
  ] },
  { label: 'People', icon: 'pi pi-fw pi-users', pages: [
    page('employees', 'Employees', 'pi-users', EMPLOYEE_READ)
  ] },
  { label: 'Organization', icon: 'pi pi-fw pi-sitemap', pages: [
    page('org/branches', 'Branches', 'pi-building', ORG_READ),
    page('org/departments', 'Departments', 'pi-sitemap', ORG_READ),
    page('org/cost-centers', 'Cost Centers', 'pi-chart-pie', ['HRM_ORG_VIEW', 'HRM_ORG_MANAGE', 'HRM_SETTINGS_MANAGE', 'HRM_ACCOUNT_MAPPING_VIEW']),
    page('org/designations', 'Designations', 'pi-briefcase', ORG_READ),
    page('org/grades', 'Grades', 'pi-sort-amount-up', ORG_READ)
  ] },
  { label: 'Attendance', icon: 'pi pi-fw pi-clock', anyFeature: SHIFT_FEATURES, pages: [
    page('attendance', 'Daily Attendance', 'pi-calendar', ['HRM_ATTENDANCE_VIEW', 'HRM_ATTENDANCE_MANAGE'], F.ATTENDANCE),
    page('attendance/overtime', 'Overtime', 'pi-stopwatch', ['HRM_ATTENDANCE_VIEW', 'HRM_ATTENDANCE_MANAGE', 'HRM_OVERTIME_APPROVE'], F.ATTENDANCE),
    page('attendance/shifts', 'Shifts', 'pi-sync', ['HRM_ATTENDANCE_VIEW', 'HRM_SHIFT_MANAGE'], undefined, SHIFT_FEATURES),
    page('attendance/holidays', 'Holidays', 'pi-sun', ['HRM_ATTENDANCE_VIEW', 'HRM_SHIFT_MANAGE', 'HRM_LEAVE_VIEW', 'HRM_LEAVE_MANAGE'], undefined, SHIFT_FEATURES)
  ] },
  { label: 'Leave', icon: 'pi pi-fw pi-calendar-plus', feature: F.LEAVE, pages: [
    page('leave', 'Team Calendar', 'pi-calendar', ['HRM_LEAVE_VIEW', 'HRM_LEAVE_APPROVE', 'HRM_LEAVE_MANAGE']),
    page('leave/approvals', 'Leave Approvals', 'pi-check-square', ['HRM_LEAVE_APPROVE', 'HRM_LEAVE_VIEW', 'HRM_LEAVE_MANAGE']),
    page('leave/balances', 'Leave Balances', 'pi-chart-bar', ['HRM_LEAVE_VIEW', 'HRM_LEAVE_APPROVE', 'HRM_LEAVE_MANAGE']),
    page('leave/types', 'Leave Types', 'pi-cog', ['HRM_LEAVE_VIEW', 'HRM_LEAVE_MANAGE'])
  ] },
  { label: 'Salary & Payroll', icon: 'pi pi-fw pi-money-bill', feature: F.PAYROLL, pages: [
    page('payroll/runs', 'Payroll Runs', 'pi-play', ['HRM_PAYROLL_VIEW']),
    page('payroll/adjustments', 'Adjustments', 'pi-plus-circle', ['HRM_PAYROLL_RUN', 'HRM_PAYROLL_VIEW']),
    page('salary/bulk-revision', 'Bulk Salary Revision', 'pi-percentage', ['HRM_SALARY_EDIT']),
    page('salary/components', 'Salary Components', 'pi-list', ['HRM_PAYROLL_CONFIG', 'HRM_PAYROLL_VIEW', 'HRM_SALARY_EDIT']),
    page('salary/structures', 'Salary Structures', 'pi-th-large', ['HRM_PAYROLL_CONFIG', 'HRM_SALARY_EDIT']),
    page('salary/formula-tester', 'Formula Tester', 'pi-calculator', ['HRM_PAYROLL_CONFIG']),
    page('salary/tax-tables', 'Tax Tables', 'pi-table', ['HRM_PAYROLL_CONFIG', 'HRM_STATUTORY_VERIFY']),
    page('salary/statutory-rates', 'Statutory Rates', 'pi-percentage', ['HRM_PAYROLL_CONFIG', 'HRM_STATUTORY_VERIFY'])
  ] },
  { label: 'Recruitment', icon: 'pi pi-fw pi-user-plus', feature: F.RECRUITMENT, pages: [
    page('recruitment/vacancies', 'Vacancies', 'pi-megaphone', ['HRM_RECRUITMENT_VIEW', 'HRM_RECRUITMENT_MANAGE']),
    page('recruitment/candidates', 'Candidates', 'pi-id-card', ['HRM_RECRUITMENT_VIEW', 'HRM_RECRUITMENT_MANAGE']),
    page('recruitment/my-interviews', 'My Interviews', 'pi-comments', ['HRM_RECRUITMENT_VIEW'])
  ] },
  { label: 'Performance', icon: 'pi pi-fw pi-star', feature: F.PERFORMANCE, pages: [
    page('performance/dashboard', 'Performance Dashboard', 'pi-chart-bar', ['HRM_PERFORMANCE_VIEW', 'HRM_PERFORMANCE_MANAGE']),
    page('performance/cycles', 'Review Cycles', 'pi-replay', ['HRM_PERFORMANCE_VIEW', 'HRM_PERFORMANCE_MANAGE']),
    page('performance/reviews', 'Reviews', 'pi-star', ['HRM_PERFORMANCE_VIEW', 'HRM_PERFORMANCE_MANAGE'])
  ] },
  { label: 'Training', icon: 'pi pi-fw pi-book', feature: F.TRAINING, pages: [
    page('training/courses', 'Course Catalog', 'pi-book', ['HRM_TRAINING_VIEW', 'HRM_TRAINING_MANAGE']),
    page('training/enrollments', 'Enrollments', 'pi-user-plus', ['HRM_TRAINING_VIEW', 'HRM_TRAINING_MANAGE'])
  ] },
  { label: 'Loans & Separation', icon: 'pi pi-fw pi-wallet', feature: F.LOANS, pages: [
    page('lifecycle/loans', 'Loans & Advances', 'pi-wallet', ['HRM_LOAN_VIEW']),
    page('lifecycle/settlements', 'Final Settlements', 'pi-sign-out', ['HRM_SETTLEMENT_VIEW']),
    page('lifecycle/increments', 'Increment Proposals', 'pi-arrow-up', ['HRM_INCREMENT_MANAGE', 'HRM_INCREMENT_APPROVE']),
    page('lifecycle/letters', 'Letters', 'pi-envelope', ['HRM_LETTER_ISSUE']),
    page('lifecycle/letter-templates', 'Letter Templates', 'pi-file-edit', ['HRM_LETTER_ISSUE']),
    page('lifecycle/tax-certificates', 'Tax Certificates', 'pi-verified', ['HRM_TAX_CERT_VIEW'])
  ] },
  { label: 'Expenses', icon: 'pi pi-fw pi-receipt', feature: F.EXPENSES, pages: [
    page('expenses/claims', 'Expense Claims', 'pi-check-square', ['HRM_EXPENSE_VIEW', 'HRM_EXPENSE_APPROVE', 'HRM_EXPENSE_PAY']),
    page('expenses/categories', 'Expense Categories', 'pi-tags', ['HRM_EXPENSE_VIEW', 'HRM_ACCOUNT_MAPPING_VIEW']),
    page('expenses/remittances', 'Statutory Remittances', 'pi-building-columns', ['HRM_REMITTANCE_VIEW'])
  ] },
  { label: 'HR Settings', icon: 'pi pi-fw pi-cog', pages: [
    page('settings', 'HR Settings', 'pi-cog', ['HRM_SETTINGS_MANAGE', 'HRM_ACCOUNT_MAPPING_VIEW', 'HRM_PAYROLL_VIEW']),
    page('settings/cost-center-mapping', 'Cost Center Mapping', 'pi-arrow-right-arrow-left', ['INTEGRATION_VIEW', 'HRM_ACCOUNT_MAPPING_VIEW'], 'MODULE_INTEGRATION')
  ] }
];

export const HRM_PAGES: HrmPage[] = HRM_GROUPS.flatMap(g => g.pages.map(p => {
  const anyFeature = p.anyFeature ?? (p.feature ? undefined : g.anyFeature);
  return { ...p, feature: p.feature ?? g.feature, ...(anyFeature ? { anyFeature } : {}) };
}));

/** The sidebar group: MODULE_HRM on the group, permission and sub-feature per entry, exactly as the routes guard them. */
export function hrmMenuGroup(): any {
  return {
    label: 'HR & Payroll',
    featureCode: MODULE_HRM,
    items: HRM_GROUPS.map(g => ({
      label: g.label,
      icon: g.icon,
      ...(g.feature ? { featureCode: g.feature } : {}),
      ...(g.anyFeature ? { anyFeature: g.anyFeature } : {}),
      items: g.pages.map(p => ({
        label: p.label,
        icon: p.icon,
        routerLink: [`${HRM_ROUTE}/${p.path}`],
        permRequired: p.perms,
        ...((p.feature ?? g.feature) ? { featureCode: p.feature ?? g.feature } : {}),
        ...(p.anyFeature ? { anyFeature: p.anyFeature } : {})
      }))
    }))
  };
}

