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
  status: 'SUBMITTED' | 'CONSUMED' | 'EXPIRED' | 'INVALID' | 'VALIDATION_ERROR';
  responseUuid?: string;
  quotationNumber?: string;
  supplierName?: string;
  submittedAt?: string;
  validationErrors?: string[];
}

interface UploadedFileRow {
  name: string;
  uploading: boolean;
  failed: boolean;
}

interface ApiResponse<T> {
  success: boolean;
  message: string;
  result: T;
}

// ── Editable row model ────────────────────────────────────────────────────────

interface RfqLineRow extends RfqPublicLine {
  // Wire format is still string (form inputs), but these are now validated before submit —
  // supersedes the earlier FSD §5.2 "no validation by design" decision.
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
  private readonly BASE = 'https://localhost:52800';
  private token = '';
  // Generated once per page load so attachments uploaded before submit can be linked
  // to the eventual VendorResponse (which is created with this same UUID on submit).
  private readonly responseUuid = crypto.randomUUID();

  loading    = true;
  submitting = false;

  status: 'VALID' | 'CONSUMED' | 'EXPIRED' | 'INVALID' | null = null;
  payload: RfqPublicPayload | null = null;
  rows: RfqLineRow[] = [];

  submitResult: RfqSubmitResult | null = null;
  clientValidationErrors: string[] = [];
  submitError: string | null = null;

  attachments: UploadedFileRow[] = [];

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

  // Client-side check mirrors the server's rule exactly (a line the vendor can't supply is a
  // deliberate decline, not something to validate) — this is UX-only; the server re-validates
  // and is the real enforcement.
  private validateRows(): string[] {
    const errors: string[] = [];
    for (const r of this.rows) {
      if (!r.canSupply) continue;
      const price = Number(r.unitPrice);
      const days  = Number(r.deliveryDays);
      if (!r.unitPrice.trim() || !Number.isFinite(price) || price <= 0) {
        errors.push(`Line ${r.lineNo} (${r.itemDescription}): unit price must be a positive number.`);
      }
      if (!r.deliveryDays.trim() || !Number.isInteger(days) || days <= 0) {
        errors.push(`Line ${r.lineNo} (${r.itemDescription}): delivery days must be a positive whole number.`);
      }
    }
    return errors;
  }

  submit(): void {
    if (this.submitting) return;

    this.clientValidationErrors = this.validateRows();
    if (this.clientValidationErrors.length) return;

    this.submitError = null;
    this.submitting = true;

    const body = {
      lines: this.rows.map(r => ({
        lineUuid:     r.lineUuid,
        unitPrice:    r.unitPrice,
        deliveryDays: r.deliveryDays,
        canSupply:    r.canSupply,
        remarks:      r.remarks
      })),
      notes: this.payload?.notes ?? null,
      responseUuid: this.responseUuid
    };

    this.http
      .post<ApiResponse<RfqSubmitResult>>(
        `${this.BASE}/api/public/rfq-portal/${this.token}/submit`,
        body
      )
      .subscribe({
        next: res => {
          this.submitting = false;

          if (res.result.status === 'VALIDATION_ERROR') {
            // Stay on the form — this is a fixable input problem, not a dead link.
            this.clientValidationErrors = res.result.validationErrors ?? ['Please check your entries and try again.'];
            return;
          }

          this.submitResult = res.result;
          // On genuine blocking codes (link already used/expired/invalid), update the status.
          if (res.result.status !== 'SUBMITTED') {
            this.status = res.result.status as any;
          }
        },
        error: (err) => {
          this.submitting = false;
          // A transport/server failure (network drop, 4xx/5xx, rate limit) is NOT the same thing
          // as "this link is invalid" — conflating the two hides the real cause. Surface it plainly
          // instead so it's diagnosable, and let the vendor retry rather than dead-ending them.
          // GlobalExceptionMiddleware includes the real .NET exception text in result.exceptionMessage
          // — show it too (this is an internal admin-facing diagnostic page, not customer-facing, so
          // there's no information-disclosure concern in exposing it here).
          const baseMessage = err?.error?.message
            || (err?.status ? `The server returned an error (HTTP ${err.status}).` : null)
            || 'Could not reach the server. Please check your connection and try again.';
          const detail = err?.error?.result?.exceptionMessage;
          this.submitError = detail ? `${baseMessage} (${detail})` : baseMessage;
        }
      });
  }

  onFileSelected(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file  = input.files?.[0];
    input.value = '';
    if (!file) return;

    if (file.size > 20 * 1024 * 1024) {
      this.attachments.push({ name: file.name, uploading: false, failed: true });
      return;
    }

    const row: UploadedFileRow = { name: file.name, uploading: true, failed: false };
    this.attachments.push(row);

    const form = new FormData();
    form.append('file', file);
    form.append('responseUuid', this.responseUuid);

    this.http
      .post<ApiResponse<string>>(
        `${this.BASE}/api/public/rfq-portal/${this.token}/attachments`,
        form
      )
      .subscribe({
        next: res => { row.uploading = false; row.failed = !res.success; },
        error: () => { row.uploading = false; row.failed = true; }
      });
  }

  formatDate(iso?: string | null): string {
    if (!iso) return '—';
    return new Date(iso).toLocaleDateString('en-GB', {
      day: '2-digit', month: 'short', year: 'numeric'
    });
  }
}
