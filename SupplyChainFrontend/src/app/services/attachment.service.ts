import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ApiResponse } from './demand.service';
import { environment } from '../../environments/environment';

const BASE = environment.apiOrigin;

export interface AttachmentModel {
  uuid: string;
  interfaceCode: string;
  documentId: string;
  fileName: string;
  fileUrl: string;
  fileSize?: number;
  contentType?: string;
  notes?: string;
  uploadedBy: number;
  uploadedByName: string;
  uploadedDate: string;
}

@Injectable({ providedIn: 'root' })
export class AttachmentService {
  constructor(private http: HttpClient) {}

  upload(file: File, interfaceCode: string, documentId: string, notes?: string): Observable<ApiResponse<string>> {
    const form = new FormData();
    form.append('file', file);
    form.append('interfaceCode', interfaceCode);
    form.append('documentId', documentId);
    if (notes) form.append('notes', notes);
    return this.http.post<ApiResponse<string>>(`${BASE}/api/attachments/upload`, form);
  }

  getAttachments(interfaceCode: string, documentId: string): Observable<ApiResponse<AttachmentModel[]>> {
    const params = new HttpParams().set('interface', interfaceCode).set('documentId', documentId);
    return this.http.get<ApiResponse<AttachmentModel[]>>(`${BASE}/api/attachments`, { params });
  }

  deleteAttachment(uuid: string): Observable<ApiResponse> {
    return this.http.delete<ApiResponse>(`${BASE}/api/attachments/${uuid}`);
  }

  resolveUrl(url: string): string {
    if (!url) return '';
    return url.startsWith('/') ? `${BASE}${url}` : url;
  }

  /**
   * A document the system generated and filed (an issued invoice's PDF, a gate pass) is served by the
   * API behind the caller's token, not as a public static file: its url is under /api/. A link cannot
   * carry the token, so it has to be fetched and opened as a blob (see download).
   */
  isApiUrl(url: string | undefined | null): boolean {
    return !!url && url.startsWith('/api/');
  }

  download(url: string): Observable<Blob> {
    return this.http.get(this.resolveUrl(url), { responseType: 'blob' });
  }
}
