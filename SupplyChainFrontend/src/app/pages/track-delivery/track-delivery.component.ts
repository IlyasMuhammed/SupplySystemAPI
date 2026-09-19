import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute } from '@angular/router';

import {
  PublicTrackingService,
  PublicTrackingModel
} from '../../services/public-tracking.service';

/**
 * Where a consignee looks up their own delivery. **The only page in this application with nobody
 * logged in behind it.**
 *
 * Built deliberately plainly: no PrimeNG, no layout shell, no navigation. Everything this page
 * imports is something a stranger's browser downloads, and everything it links to is somewhere a
 * stranger might follow. A consignee needs one sentence and a list of dates.
 *
 * It states one thing and one thing only — where the goods are. It cannot be used to find out
 * anything else about them, and there is nothing on it to click through to.
 */
@Component({
  selector: 'app-track-delivery',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './track-delivery.component.html',
  styleUrls: ['./track-delivery.component.scss']
})
export class TrackDeliveryComponent implements OnInit {
  tracking: PublicTrackingModel | null = null;

  isLoading = true;

  /**
   * One message for every kind of failure, matching the server. A page that distinguished
   * "no such link" from "that link was revoked" would confirm which, to somebody guessing.
   */
  notFound = false;

  /** Something went wrong at our end. Distinct from a bad link — this one is worth retrying. */
  failed = false;

  constructor(
    private route: ActivatedRoute,
    private tracker: PublicTrackingService
  ) {}

  ngOnInit() {
    const token = this.route.snapshot.paramMap.get('token') ?? '';

    if (!token) {
      this.isLoading = false;
      this.notFound = true;
      return;
    }

    this.tracker.track(token).subscribe({
      next: (res) => {
        this.isLoading = false;
        this.tracking = res.result ?? null;
        this.notFound = this.tracking === null;
      },
      error: (err) => {
        this.isLoading = false;

        // A 404 is the server saying the link matches nothing — for any of five reasons it
        // deliberately does not distinguish between. Anything else is our problem, not the link's.
        if (err?.status === 404) this.notFound = true;
        else                     this.failed = true;
      }
    });
  }

  /** Whether the goods have arrived, which is the only thing most people open this page to learn. */
  get isDelivered(): boolean {
    return this.tracking?.status === 'Delivered';
  }

  get isSettled(): boolean {
    return this.isDelivered
        || this.tracking?.status === 'Returned to sender'
        || this.tracking?.status === 'Cancelled';
  }

  /** The most recent step, which is what the headline restates. */
  get latest() {
    const events = this.tracking?.events ?? [];
    return events.length ? events[events.length - 1] : null;
  }
}
