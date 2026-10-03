import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, catchError, map, of, shareReplay, throwError } from 'rxjs';
import { ApiResponse } from './demand.service';
import { environment } from '../../environments/environment';

const BASE = environment.apiOrigin;

/**
 * Who may list, add and remove the files attached to one kind of document — the server's own rule
 * (AttachmentAccessPolicy in SMS.WorkflowEngine), as GET /api/attachments/policy hands it out. Each list is
 * "any one of": holding a single permission in it is enough. An empty list means nobody may.
 * Removing is decided per file by the server (AttachmentModel.canRemove), because `deleteOwn` only reaches
 * the files the caller uploaded.
 */
export interface AttachmentAccessRule {
  interfaceCode: string;
  view: string[];
  upload: string[];
  /** May remove anybody's file. */
  delete: string[];
  /** May remove only a file they uploaded. */
  deleteOwn: string[];
}

/**
 * The attachment rules, read once and shared by every attachment panel. They are the same for every
 * caller — which permission each needs, not what this user holds — so one fetch serves the whole session,
 * whoever signs in; the caller's own permissions are checked against them separately.
 */
@Injectable({ providedIn: 'root' })
export class AttachmentPolicyService {
  private rules$?: Observable<ReadonlyMap<string, AttachmentAccessRule>>;

  constructor(private http: HttpClient) {}

  /**
   * The rule for one kind of document, or null — no rule for that code, or the policy could not be loaded.
   * A panel treats null as read-only: it fails closed, offering nothing the server might then refuse.
   */
  ruleFor(interfaceCode: string): Observable<AttachmentAccessRule | null> {
    return this.rules().pipe(
      map(rules => rules.get(interfaceCode) ?? null),
      catchError(() => of(null)));
  }

  private rules(): Observable<ReadonlyMap<string, AttachmentAccessRule>> {
    this.rules$ ??= this.http.get<ApiResponse<AttachmentAccessRule[]>>(`${BASE}/api/attachments/policy`).pipe(
      map(res => {
        if (!res?.success || !Array.isArray(res.result)) throw new Error('The attachment policy could not be read.');
        return new Map(res.result.map(rule => [rule.interfaceCode, rule] as const));
      }),
      // A failure is not kept: forget it, so the next panel to ask tries again.
      catchError(err => { this.rules$ = undefined; return throwError(() => err); }),
      shareReplay(1));

    return this.rules$;
  }
}
