import { Injectable, inject } from '@angular/core';
import { Observable, catchError, forkJoin, map, of, shareReplay } from 'rxjs';

import { HrmApi, rowsOf } from './hrm.api';
import { Opt } from './hrm.shared';

export type LookupKey = 'branches' | 'departments' | 'costCenters' | 'designations' | 'grades' | 'shifts' | 'employees' | 'structures' | 'components' | 'componentCodes'
  | 'leaveTypes' | 'courses' | 'vacancies' | 'candidates' | 'cycles' | 'categories' | 'templates' | 'qboClasses' | 'qboDepartments';

const coded = (r: any): Opt => ({ label: `${r.name} (${r.code})`, value: r.uuid });
const employee = (r: any): Opt => ({ label: `${r.fullName} (${r.employeeNumber})`, value: r.uuid });

const QBO_REFERENCE = 'integrations/quickbooks/cost-center-mappings/reference';
const qboRef = (r: any): Opt => ({ label: r.name, value: r.id });

const SOURCES: Record<LookupKey, [string, (r: any) => Opt, Record<string, any>?, ((res: any) => any[])?]> = {
  qboClasses: [QBO_REFERENCE, qboRef, undefined, res => res?.classes ?? []],
  qboDepartments: [QBO_REFERENCE, qboRef, undefined, res => res?.departments ?? []],
  branches: ['hrm/branches', coded],
  departments: ['hrm/departments', coded],
  costCenters: ['hrm/cost-centers', coded],
  designations: ['hrm/designations', coded],
  grades: ['hrm/grades', coded],
  shifts: ['hrm/shifts', coded],
  employees: ['hrm/employees', employee, { pageSize: 200 }],
  structures: ['hrm/salary-structures', coded],
  components: ['hrm/salary-components', coded],
  componentCodes: ['hrm/salary-components', r => ({ label: `${r.name} (${r.code})`, value: r.code })],
  leaveTypes: ['hrm/leave/types', coded],
  courses: ['hrm/courses', coded],
  vacancies: ['hrm/vacancies', r => ({ label: `${r.title} (${r.vacancyNumber})`, value: r.uuid })],
  candidates: ['hrm/candidates', r => ({ label: `${r.firstName} ${r.lastName ?? ''}`.trim(), value: r.uuid })],
  cycles: ['hrm/review-cycles', r => ({ label: r.name, value: r.uuid })],
  categories: ['hrm/expense-categories', r => ({ label: r.name, value: r.uuid })],
  templates: ['hrm/letter-templates', r => ({ label: `${r.name} (${r.code})`, value: r.uuid })]
};

/**
 * Pickers. Provided per page, so a page sees rows it just created after `reset()`. A picker the caller may not read (403) is an
 * empty list, never an error on the page: the save itself answers with the server's words.
 */
@Injectable()
export class HrmLookups {
  private readonly api = inject(HrmApi);
  private cache = new Map<LookupKey, Observable<Opt[]>>();
  /** Filled by `need`; read by the form synchronously. */
  readonly options: Partial<Record<LookupKey, Opt[]>> = {};

  load(key: LookupKey): Observable<Opt[]> {
    let hit = this.cache.get(key);
    if (!hit) {
      const [path, toOpt, params, pick] = SOURCES[key];
      hit = this.api.get<any>(path, params).pipe(map(r => (pick ? pick(r) : rowsOf(r)).map(toOpt)), catchError(() => of([] as Opt[])), shareReplay(1));
      this.cache.set(key, hit);
    }
    return hit;
  }

  need(keys: LookupKey[]): Observable<void> {
    const unique = [...new Set(keys)];
    if (!unique.length) return of(undefined);
    return forkJoin(unique.map(k => this.load(k).pipe(map(list => { this.options[k] = list; })))).pipe(map(() => undefined));
  }

  /** Employee picker search beyond the first page (§3 `search`). */
  searchEmployees(term: string): Observable<Opt[]> {
    return this.api.get<any>('hrm/employees', { search: term, pageSize: 50 }).pipe(map(r => rowsOf(r).map(employee)), catchError(() => of([] as Opt[])));
  }

  reset(): void { this.cache.clear(); }
}
