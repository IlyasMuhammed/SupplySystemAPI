import { Injectable } from '@angular/core';
import { Observable, catchError, map, of, shareReplay, switchMap } from 'rxjs';
import {
  Campaign, CrmMeta, CrmService, CrmUser, LeadSource, PipelineStage, SalesTeam, crmLabel
} from '../../../services/crm.service';
import { OrgCurrencyService } from '../../../services/org-currency.service';

export interface Option<T = string> { label: string; value: T; }

/**
 * A41 — the CRM pickers' data, loaded once per page visit (each call starts a fresh cached stream; `reset()` after a
 * settings change). A failing lookup yields an empty list so a page still opens (e.g. campaigns while campaign tracking is off).
 */
@Injectable({ providedIn: 'root' })
export class CrmLookupsService {
  private meta$?: Observable<CrmMeta | null>;
  private users$?: Observable<CrmUser[]>;
  private assignable$?: Observable<CrmUser[]>;
  private sources$?: Observable<LeadSource[]>;
  private teams$?: Observable<SalesTeam[]>;
  private campaigns$?: Observable<Campaign[]>;
  private currencies$?: Observable<{ codes: string[]; base: string | null }>;

  constructor(private crm: CrmService, private orgCurrency: OrgCurrencyService) {}

  reset(): void {
    this.meta$ = this.users$ = this.assignable$ = undefined;
    this.sources$ = undefined;
    this.teams$ = undefined;
    this.campaigns$ = undefined;
    this.currencies$ = undefined;
  }

  /** The organization's active currency codes and its sale base (the default for amounts). */
  currencies(): Observable<{ codes: string[]; base: string | null }> {
    return this.currencies$ ??= this.orgCurrency.getCurrencies(false).pipe(
      map(r => (r.result ?? []).filter(c => c.isActive).map(c => c.code)),
      catchError(() => of([] as string[])),
      switchMap(codes => this.orgCurrency.getSettings().pipe(
        map(s => ({ codes, base: s.result?.saleBaseCurrencyCode ?? codes[0] ?? null })),
        catchError(() => of({ codes, base: codes[0] ?? null })))),
      shareReplay(1));
  }

  meta(): Observable<CrmMeta | null> {
    return this.meta$ ??= this.crm.getMeta().pipe(map(r => r.result ?? null), catchError(() => of(null)), shareReplay(1));
  }

  users(): Observable<CrmUser[]> {
    return this.users$ ??= this.crm.getUsers(undefined, 100).pipe(map(r => r.result ?? []), catchError(() => of([])), shareReplay(1));
  }

  /** People who can own leads and opportunities (they hold a CRM view permission) — owner, assignee and team-member pickers. */
  assignableUsers(): Observable<CrmUser[]> {
    return this.assignable$ ??= this.crm.getUsers(undefined, 100, true).pipe(map(r => r.result ?? []), catchError(() => of([])), shareReplay(1));
  }

  sources(): Observable<LeadSource[]> {
    return this.sources$ ??= this.crm.getLeadSources().pipe(map(r => r.result ?? []), catchError(() => of([])), shareReplay(1));
  }

  teams(): Observable<SalesTeam[]> {
    return this.teams$ ??= this.crm.getSalesTeams().pipe(map(r => r.result ?? []), catchError(() => of([])), shareReplay(1));
  }

  /** Active and draft campaigns for pickers (the server refuses a deleted one). Empty while campaign tracking is off. */
  campaigns(): Observable<Campaign[]> {
    return this.campaigns$ ??= this.crm.getCampaigns({ pageSize: 100 }).pipe(
      map(r => (r.result?.data ?? []).filter(c => c.status !== 'CANCELLED' && c.status !== 'COMPLETED')),
      catchError(() => of([])), shareReplay(1));
  }

  stages(stageType: 'LEAD' | 'OPPORTUNITY', salesTeamUuid?: string | null): Observable<PipelineStage[]> {
    return this.crm.getPipelineStages({ stageType, salesTeamUuid: salesTeamUuid ?? undefined }).pipe(
      map(r => r.result ?? []), catchError(() => of([])));
  }

  static options(codes: readonly string[] | undefined | null): Option[] {
    return (codes ?? []).map(c => ({ value: c, label: crmLabel(c) }));
  }

  static userOptions(users: CrmUser[]): Option<number>[] {
    return users.filter(u => u.isActive).map(u => ({ value: u.userId, label: u.name || u.email }));
  }
}
