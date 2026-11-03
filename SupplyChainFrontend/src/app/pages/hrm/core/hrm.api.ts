import { HttpClient, HttpParams, HttpResponse } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';

import { environment } from '../../../../environments/environment';

export type Params = Record<string, string | number | boolean | null | undefined>;

export interface Page<T> { data: T[]; totalRecords: number; page: number; pageSize: number; totalPages: number; hasNext: boolean; hasPrevious: boolean; }

function toParams(p?: Params): HttpParams {
  let out = new HttpParams();
  for (const [k, v] of Object.entries(p ?? {})) {
    if (v !== null && v !== undefined && v !== '') out = out.set(k, String(v));
  }
  return out;
}

/** `{ success, message, result }` → result; `success: false` on a 2xx goes to the error channel so a screen shows the message. */
export function unwrap<T>(body: any): T {
  if (body && typeof body === 'object' && 'success' in body) {
    if (body.success === false) throw { error: { message: body.message } };
    return body.result as T;
  }
  return body as T;
}

/** A plain array, or the `data` of a page (contract §0: small lists are plain arrays). */
export function rowsOf<T = any>(x: any): T[] { return Array.isArray(x) ? x : (x?.data ?? []); }

/** The one door to the API for HRM pages. Paths are relative to `/api/`, as the contract writes them. */
@Injectable({ providedIn: 'root' })
export class HrmApi {
  private readonly http = inject(HttpClient);
  private url(path: string): string { return `${environment.apiUrl}/${path}`; }

  get<T = any>(path: string, params?: Params): Observable<T> {
    return this.http.get<any>(this.url(path), { params: toParams(params) }).pipe(map(b => unwrap<T>(b)));
  }
  post<T = any>(path: string, body: unknown = {}): Observable<T> { return this.http.post<any>(this.url(path), body).pipe(map(b => unwrap<T>(b))); }
  put<T = any>(path: string, body: unknown = {}): Observable<T> { return this.http.put<any>(this.url(path), body).pipe(map(b => unwrap<T>(b))); }
  patch<T = any>(path: string, body: unknown = {}): Observable<T> { return this.http.patch<any>(this.url(path), body).pipe(map(b => unwrap<T>(b))); }
  delete<T = any>(path: string): Observable<T> { return this.http.delete<any>(this.url(path)).pipe(map(b => unwrap<T>(b))); }

  send<T = any>(method: 'POST' | 'PUT' | 'PATCH' | 'DELETE', path: string, body?: unknown): Observable<T> {
    switch (method) {
      case 'PUT': return this.put<T>(path, body);
      case 'PATCH': return this.patch<T>(path, body);
      case 'DELETE': return this.delete<T>(path);
      default: return this.post<T>(path, body);
    }
  }

  /** POST that needs the status: a long calculation answers 202 (§9). */
  postStatus<T = any>(path: string, body: unknown = {}): Observable<{ status: number; result: T }> {
    return this.http.post<any>(this.url(path), body, { observe: 'response' }).pipe(map(r => ({ status: r.status, result: unwrap<T>(r.body) })));
  }

  upload<T = any>(path: string, file: File): Observable<T> {
    const form = new FormData();
    form.append('file', file);
    return this.http.post<any>(this.url(path), form).pipe(map(b => unwrap<T>(b)));
  }

  /** A file from the server. A refusal arrives as a Blob: read it with hrmErrorAsync. */
  blob(path: string, params?: Params): Observable<HttpResponse<Blob>> {
    return this.http.get(this.url(path), { params: toParams(params), responseType: 'blob', observe: 'response' });
  }

  /** §0 Files — uploads go to api/attachments, then the row keeps the attachment's uuid. Reads go through api/hrm/attachments. */
  attach(file: File, interfaceCode: string, documentId: string): Observable<string> {
    const form = new FormData();
    form.append('file', file);
    form.append('interfaceCode', interfaceCode);
    form.append('documentId', documentId);
    return this.http.post<any>(this.url('attachments/upload'), form).pipe(map(b => {
      const r = unwrap<any>(b);
      return typeof r === 'string' ? r : (r?.uuid as string);
    }));
  }

  attachmentBlob(attachmentUuid: string): Observable<HttpResponse<Blob>> { return this.blob(`hrm/attachments/${attachmentUuid}/content`); }

  /** A file chosen on a form and dropped before the save: the uploader only, while no row holds it (409 ATTACHMENT_IN_USE). */
  removeUnusedAttachment(attachmentUuid: string): Observable<boolean> { return this.delete<boolean>(`hrm/attachments/${attachmentUuid}`); }
}
