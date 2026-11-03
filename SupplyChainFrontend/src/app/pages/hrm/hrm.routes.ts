import { CanActivateFn, Route, Routes } from '@angular/router';

import { permissionGuard } from '../gaurds/permission.guard';
import { hrmFeatureGuard } from './core/hrm.guards';
import { F, HRM_PAGES, HrmPage } from './core/hrm.nav';
import { RecordsConfig } from './core/hrm-records.component';
import * as ATT from './configs/att.config';
import * as LIFE from './configs/life.config';
import * as ORG from './configs/org.config';
import * as PAY from './configs/pay.config';
import * as SELF from './configs/self.config';
import * as TAL from './configs/tal.config';

// A49 — lazy: this file and everything it loads is one chunk, reached only through `hrm` in pages.routes.ts behind MODULE_HRM.
// Each menu page's guards are read from HRM_PAGES (hrm.nav.ts), so the route and its menu entry hold the same list by construction.

const records = () => import('./core/hrm-records.component').then(m => m.HrmRecordsComponent);

/** Pages made of a config. */
const CONFIGS: Record<string, RecordsConfig> = {
  'me/attendance': SELF.MY_ATTENDANCE, 'me/leave': SELF.MY_LEAVE, 'me/payslips': SELF.MY_PAYSLIPS, 'me/expenses': SELF.MY_EXPENSES, 'me/loans': SELF.MY_LOANS, 'me/reviews': SELF.MY_REVIEWS, 'me/assigned-reviews': SELF.ASSIGNED_REVIEWS, 'recruitment/my-interviews': SELF.MY_INTERVIEWS,
  'me/training': SELF.MY_TRAINING, 'me/letters': SELF.MY_LETTERS, 'me/tax-certificates': SELF.MY_TAX_CERTS,
  employees: SELF.EMPLOYEES,
  'org/branches': ORG.BRANCHES, 'org/cost-centers': ORG.COST_CENTERS, 'org/designations': ORG.DESIGNATIONS, 'org/grades': ORG.GRADES,
  attendance: ATT.ATTENDANCE, 'attendance/overtime': ATT.OVERTIME, 'attendance/shifts': ATT.SHIFTS, 'attendance/holidays': ATT.HOLIDAYS,
  'leave/approvals': ATT.LEAVE_APPROVALS, 'leave/types': ATT.LEAVE_TYPES,
  'payroll/runs': PAY.PAYROLL_RUNS, 'payroll/adjustments': PAY.ADJUSTMENTS, 'salary/components': PAY.COMPONENTS, 'salary/structures': SELF.STRUCTURES, 'salary/tax-tables': PAY.TAX_TABLES, 'salary/statutory-rates': PAY.STATUTORY_RATES,
  'recruitment/vacancies': TAL.VACANCIES, 'recruitment/candidates': TAL.CANDIDATES, 'performance/cycles': TAL.CYCLES, 'performance/reviews': TAL.REVIEWS, 'training/courses': TAL.COURSES, 'training/enrollments': TAL.ENROLLMENTS,
  'lifecycle/loans': LIFE.LOANS, 'lifecycle/settlements': LIFE.SETTLEMENTS, 'lifecycle/increments': LIFE.INCREMENTS, 'lifecycle/letters': LIFE.LETTERS, 'lifecycle/letter-templates': LIFE.LETTER_TEMPLATES, 'lifecycle/tax-certificates': LIFE.TAX_CERTIFICATES,
  'expenses/claims': LIFE.CLAIMS, 'expenses/categories': LIFE.CATEGORIES, 'expenses/remittances': LIFE.REMITTANCE_TRACKER,
  'settings/cost-center-mapping': SELF.COST_CENTER_MAP
};

/** Pages with their own component. */
const LOADERS: Record<string, () => Promise<any>> = {
  'me/profile': () => import('./self/my-profile.component').then(m => m.MyProfileComponent),
  'org/departments': () => import('./org/departments.component').then(m => m.DepartmentsComponent),
  leave: () => import('./leave/leave-dashboard.component').then(m => m.LeaveDashboardComponent),
  'leave/balances': () => import('./leave/leave-balances.component').then(m => m.LeaveBalancesComponent),
  'salary/bulk-revision': () => import('./salary/salary-tools.component').then(m => m.BulkRevisionComponent),
  'salary/formula-tester': () => import('./salary/salary-tools.component').then(m => m.FormulaTesterComponent),
  'performance/dashboard': () => import('./talent/performance-dashboard.component').then(m => m.PerformanceDashboardComponent),
  settings: () => import('./setup/hr-settings.component').then(m => m.HrSettingsComponent)
};

function guards(perms: string[], feature?: string): CanActivateFn[] {
  return feature ? [hrmFeatureGuard(feature), permissionGuard(...perms)] : [permissionGuard(...perms)];
}

function pageRoute(p: HrmPage): Route {
  const config = CONFIGS[p.path];
  return config
    ? { path: p.path, loadComponent: records, data: { config }, canActivate: guards(p.perms, p.feature) }
    : { path: p.path, loadComponent: LOADERS[p.path], canActivate: guards(p.perms, p.feature) };
}

/** Routes that are no menu entry: new, detail, sub-lists. */
const DETAIL: Routes = [
  { path: 'employees/new', loadComponent: () => import('./employees/employee-new.component').then(m => m.EmployeeNewComponent), canActivate: guards(['HRM_EMPLOYEE_EDIT']) },
  { path: 'employees/:uuid', loadComponent: () => import('./employees/employee-detail.component').then(m => m.EmployeeDetailComponent), canActivate: guards(['HRM_EMPLOYEE_VIEW', 'HRM_EMPLOYEE_EDIT']) },
  { path: 'payroll/runs/new', loadComponent: () => import('./payroll/payroll-wizard.component').then(m => m.PayrollWizardComponent), canActivate: guards(['HRM_PAYROLL_RUN'], F.PAYROLL) },
  { path: 'payroll/runs/:uuid', loadComponent: () => import('./payroll/payroll-wizard.component').then(m => m.PayrollWizardComponent), canActivate: guards(['HRM_PAYROLL_VIEW'], F.PAYROLL) },
  { path: 'recruitment/vacancies/:uuid', loadComponent: () => import('./talent/vacancy-board.component').then(m => m.VacancyBoardComponent), canActivate: guards(['HRM_RECRUITMENT_VIEW', 'HRM_RECRUITMENT_MANAGE'], F.RECRUITMENT) },
  { path: 'recruitment/candidates/:uuid', loadComponent: () => import('./talent/candidate-detail.component').then(m => m.CandidateDetailComponent), canActivate: guards(['HRM_RECRUITMENT_VIEW', 'HRM_RECRUITMENT_MANAGE'], F.RECRUITMENT) },
  { path: 'performance/reviews/:uuid', loadComponent: () => import('./talent/review-form.component').then(m => m.ReviewFormComponent), canActivate: guards(['HRM_PERFORMANCE_VIEW', 'HRM_PERFORMANCE_MANAGE'], F.PERFORMANCE) },
  { path: 'me/reviews/:uuid', loadComponent: () => import('./talent/review-form.component').then(m => m.ReviewFormComponent), data: { back: 'me/reviews' }, canActivate: guards(['HRM_SELF_SERVICE'], F.PERFORMANCE) },
  { path: 'me/assigned-reviews/:uuid', loadComponent: () => import('./talent/review-form.component').then(m => m.ReviewFormComponent), data: { back: 'me/assigned-reviews' }, canActivate: guards(['HRM_SELF_SERVICE'], F.PERFORMANCE) },
  { path: 'expenses/remittances/payments', loadComponent: records, data: { config: LIFE.REMITTANCES }, canActivate: guards(['HRM_REMITTANCE_VIEW'], F.EXPENSES) }
];

const routes: Routes = [
  ...DETAIL,
  ...HRM_PAGES.map(pageRoute),
  { path: '', redirectTo: 'employees', pathMatch: 'full' }
];

export default routes;
