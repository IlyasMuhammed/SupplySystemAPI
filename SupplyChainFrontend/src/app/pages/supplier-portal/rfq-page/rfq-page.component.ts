import { Component, OnInit } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { HttpClient } from '@angular/common/http';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ProgressSpinnerModule } from 'primeng/progressspinner';
import { CheckboxModule } from 'primeng/checkbox';
import { ButtonModule } from 'primeng/button';

// ── API shapes ────────────────────────────────────────────────────────────────

interface RfqPublicLine {
  lineUuid: string;
  lineNo: number;
  itemDescription: string;
  specification?: string;
  unitOfMeasure?: string;
  quantity: number;
  requiredDate?: string;
}

interface RfqPublicPayload {
  quotationNumber: string;
  title: string;
  issueDate: string;
  dueDate?: string;
  notes?: string;
  supplierName?: string;
  expiresAt: string;
  firstOpenedAt?: string;
  lines: RfqPublicLine[];
}

interface RfqPortalResponse {
  status: 'VALID' | 'CONSUMED' | 'EXPIRED' | 'INVALID';
  payload?: RfqPublicPayload;
}

interface RfqSubmitResult {
  status: 'SUBMITTED' | 'CONSUMED' | 'EXPIRED' | 'INVALID';
  responseUuid?: string;
  quotationNumber?: string;
  supplierName?: string;
  submittedAt?: string;
}

interface ApiResponse<T> {
  success: boolean;
  message: string;
  result: T;
}

// ── Editable row model ────────────────────────────────────────────────────────

interface RfqLineRow extends RfqPublicLine {
  // string types intentional — no validation by design (FSD §5.2)
  unitPrice: string;
  deliveryDays: string;
  canSupply: boolean;
  remarks: string;
}

// ─────────────────────────────────────────────────────────────────────────────

@Component({
  selector: 'app-rfq-page',
  standalone: true,
  imports: [CommonModule, FormsModule, ProgressSpinnerModule, CheckboxModule, ButtonModule],
  templateUrl: './rfq-page.component.html',
  styleUrls: ['./rfq-page.component.scss']
})
export class RfqPageComponent implements OnInit {
  private readonly BASE = 'https://localhost:51800';
  private token = '';

  loading    = true;
  submitting = false;

  status: 'VALID' | 'CONSUMED' | 'EXPIRED' | 'INVALID' | null = null;
  payload: RfqPublicPayload | null = null;
  rows: RfqLineRow[] = [];

  submitResult: RfqSubmitResult | null = null;

  constructor(
    private route: ActivatedRoute,
    private http:  HttpClient
  ) {}

  ngOnInit(): void {
    this.token = this.route.snapshot.paramMap.get('token') ?? '';
    this.http
      .get<ApiResponse<RfqPortalResponse>>(
        `${this.BASE}/api/public/rfq-portal/${this.token}`
      )
      .subscribe({
        next: res => {
          this.status  = res.result?.status ?? 'INVALID';
          this.payload = res.result?.payload ?? null;
          if (this.payload) {
            this.rows = this.payload.lines.map(l => ({
              ...l,
              unitPrice:    '',
              deliveryDays: '',
              canSupply:    true,
              remarks:      ''
            }));
          }
          this.loading = false;
        },
        error: () => {
          this.status  = 'INVALID';
          this.loading = false;
        }
      });
  }

  submit(): void {
    if (this.submitting) return;
    this.submitting = true;

    const body = {
      lines: this.rows.map(r => ({
        lineUuid:     r.lineUuid,
        unitPrice:    r.unitPrice,
        deliveryDays: r.deliveryDays,
        canSupply:    r.canSupply,
        remarks:      r.remarks
      })),
      notes: this.payload?.notes ?? null
    };

    this.http
      .post<ApiResponse<RfqSubmitResult>>(
        `${this.BASE}/api/public/rfq-portal/${this.token}/submit`,
        body
      )
      .subscribe({
        next: res => {
          this.submitResult = res.result;
          // On rejection codes, update the blocking status
          if (res.result.status !== 'SUBMITTED') {
            this.status = res.result.status as any;
          }
          this.submitting = false;
        },
        error: () => {
          this.status     = 'INVALID';
          this.submitting = false;
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
