import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { ApiResponse } from './demand.service';

const BASE = 'https://localhost:51800/api';

export interface TimelineEvent {
  eventType: string;
  interfaceCode: string;
  documentId: string;
  documentNumber?: string;
  occurredAt: string;
  performedBy?: number;
  performedByName?: string;
  notes?: string;
}

export interface TimelineDetail {
  traceId: string;
  chainRootType?: string;
  chainRootRef?: string;
  firstEventAt: string;
  lastEventAt: string;
  events: TimelineEvent[];
  totalEventCount: number;
}

@Injectable({ providedIn: 'root' })
export class TimelineService {
  constructor(private http: HttpClient) {}

  getByTraceId(traceId: string): Observable<ApiResponse<TimelineDetail>> {
    return this.http.get<ApiResponse<TimelineDetail>>(`${BASE}/timeline/${traceId}`);
  }

  getByDocument(interfaceCode: string, documentId: string): Observable<ApiResponse<TimelineDetail>> {
    const params = new HttpParams().set('interface', interfaceCode).set('documentId', documentId);
    return this.http.get<ApiResponse<TimelineDetail>>(`${BASE}/timeline/by-document`, { params });
  }
}
