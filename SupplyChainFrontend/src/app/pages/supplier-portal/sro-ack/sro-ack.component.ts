import { Component, OnInit } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { HttpClient } from '@angular/common/http';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ProgressSpinnerModule } from 'primeng/progressspinner';
import { ButtonModule } from 'primeng/button';
import { environment } from '../../../../environments/environment';

// ── API shapes ────────────────────────────────────────────────────────────────

interface SroAckLine {
  uuid: string;
  lineNo: number;
  itemDescription: string;
  unitOfMeasure?: string;
  qtyToReturn: number;
  condition?: string;
}

interface SroAckPublicPayload {
  sroNumber: string;
  supplierName: string;
  rmaNumber?: string;
  dispatchDate?: string;
  dispatchCarrier?: string;
  dispatchTrackingRef?: string;
  expiresAt: string;
  lines: SroAckLine[];
}

interface SroPortalResponse {
  status: 'VALID' | 'CONSUMED' | 'EXPIRED' | 'INVALID';
  payload?: SroAckPublicPayload;
}

interface SroAcknowledgeResult {
  status: 'ACKNOWLEDGED' | 'CONSUMED' | 'EXPIRED' | 'INVALID';
  sroNumber?: string;
  acknowledgedAt?: string;
}

interface ApiResponse<T> {
  success: boolean;
  message: string;
  result: T;
}

// ─────────────────────────────────────────────────────────────────────────────

@Component({
  selector: 'app-sro-ack',
  standalone: true,
  imports: [CommonModule, FormsModule, ProgressSpinnerModule, ButtonModule],
  templateUrl: './sro-ack.component.html',
  styleUrls: ['./sro-ack.component.scss']
})
export class SroAckComponent implements OnInit {
  private readonly BASE = environment.apiOrigin;
  private token = '';

  loading      = true;
  acknowledging = false;

  status: 'VALID' | 'CONSUMED' | 'EXPIRED' | 'INVALID' | null = null;
  payload: SroAckPublicPayload | null = null;

  remarks = '';
  receivedDate: string = new Date().toISOString().split('T')[0];

  ackResult: SroAcknowledgeResult | null = null;
  submitError: string | null = null;

  constructor(
    private route: ActivatedRoute,
    private http: HttpClient
  ) {}

  ngOnInit(): void {
    this.token = this.route.snapshot.paramMap.get('token') ?? '';
    this.http
      .get<ApiResponse<SroPortalResponse>>(`${this.BASE}/api/public/sro-portal/${this.token}`)
      .subscribe({
        next: res => {
          this.status  = res.result?.status ?? 'INVALID';
          this.payload = res.result?.payload ?? null;
          this.loading = false;
        },
        error: () => {
          this.status  = 'INVALID';
          this.loading = false;
        }
      });
  }

  acknowledge(): void {
    if (this.acknowledging) return;

    this.submitError   = null;
    this.acknowledging = true;

    const body = {
      remarks: this.remarks?.trim() || null,
      receivedDate: this.receivedDate || null
    };

    this.http
      .post<ApiResponse<SroAcknowledgeResult>>(
        `${this.BASE}/api/public/sro-portal/${this.token}/acknowledge`,
        body
      )
      .subscribe({
        next: res => {
          this.acknowledging = false;
          this.ackResult = res.result;
          if (res.result.status !== 'ACKNOWLEDGED') {
            this.status = res.result.status as any;
          }
        },
        error: (err) => {
          this.acknowledging = false;
          const baseMessage = err?.error?.message
            || (err?.status ? `The server returned an error (HTTP ${err.status}).` : null)
            || 'Could not reach the server. Please check your connection and try again.';
          this.submitError = baseMessage;
        }
      });
  }

  formatDate(iso?: string | null): string {
    if (!iso) return '—';
    return new Date(iso).toLocaleDateString('en-GB', {
      day: '2-digit', month: 'short', year: 'numeric'
    });
  }
}
