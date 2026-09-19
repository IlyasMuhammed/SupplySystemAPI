import { Injectable } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';
import { ApiResponse } from './logistics.service';

/**
 * The URL is under `/api/public/` on purpose: the auth interceptor strips credentials from
 * everything under that prefix, so a consignee's page can never accidentally carry a staff
 * member's bearer token because somebody happened to be logged in on the same browser.
 */
const BASE = `${environment.apiUrl}/public/tracking`;

export interface PublicTrackingEventModel {
  /** Plain words: "Out for delivery", never OUT_FOR_DELIVERY. */
  status: string;
  occurredAt: string;
  /** The hub or city, where the carrier gave one. Never a street address. */
  location?: string;
}

export interface PublicTrackingModel {
  reference: string;
  status: string;
  carrier?: string;
  estimatedArrival?: string;
  deliveredAt?: string;
  /** Oldest first, as a timeline reads. */
  events: PublicTrackingEventModel[];
  lastUpdatedAt?: string;
}

/**
 * The one call this application makes without a session.
 *
 * Deliberately its own service rather than a method on `LogisticsService`: that one is the
 * authenticated portal's, and mixing the two would make it easy to reach for a staff-only endpoint
 * from a page that has no staff behind it.
 */
@Injectable({ providedIn: 'root' })
export class PublicTrackingService {
  constructor(private http: HttpClient) {}

  track(token: string): Observable<ApiResponse<PublicTrackingModel>> {
    return this.http.get<ApiResponse<PublicTrackingModel>>(
      `${BASE}/${encodeURIComponent(token)}`);
  }
}
