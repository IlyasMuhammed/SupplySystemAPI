import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TagModule } from 'primeng/tag';
import { TableModule } from 'primeng/table';
import { TooltipModule } from 'primeng/tooltip';
import { ToastModule } from 'primeng/toast';
import { DialogModule } from 'primeng/dialog';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { SelectModule } from 'primeng/select';
import { DatePickerModule } from 'primeng/datepicker';
import { MessageService } from 'primeng/api';

import {
  LogisticsService,
  ProofCoverageModel,
  ProofGapModel,
  DeliveryProofModel
} from '../../../../services/logistics.service';

type Severity = 'success' | 'info' | 'warn' | 'danger' | 'secondary' | 'contrast';

/**
 * Proof that goods reached somebody, and what has been delivered without any.
 *
 * **The screen leads with the gaps, not the successes.** A coverage figure of 94% tells nobody
 * which six deliveries cannot be demonstrated, and those are the only ones anybody can act on.
 *
 * Artefacts are uploaded, not linked. The whole finding this closes (F47) was a URL somebody typed
 * into a field, pointing somewhere nobody controls.
 */
@Component({
  selector: 'app-proof-of-delivery',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, TagModule, TableModule, TooltipModule, ToastModule, DialogModule,
    InputTextModule, TextareaModule, SelectModule, DatePickerModule
  ],
  templateUrl: './proof-of-delivery.component.html',
  styleUrls: ['./proof-of-delivery.component.scss'],
  providers: [MessageService]
})
export class ProofOfDeliveryComponent implements OnInit {
  /** Matches the server's cap. Checked here so a 40 MB photograph is refused before it is sent. */
  static readonly MaxFileBytes = 10 * 1024 * 1024;

  /** Matches the server's allowed set. Anything a browser renders as a page is deliberately absent. */
  static readonly AllowedTypes = ['image/png', 'image/jpeg', 'image/gif', 'application/pdf'];

  coverage: ProofCoverageModel | null = null;

  isLoading = true;
  isSubmitting = false;

  // ── Recording one ───────────────────────────────────────────────────────────

  captureVisible = false;
  capturing: ProofGapModel | null = null;

  form = {
    receivedBy: '',
    relationship: '',
    deliveredAt: new Date() as Date | null,
    location: '',
    notes: ''
  };

  // ── Looking at one ──────────────────────────────────────────────────────────

  detailVisible = false;
  proofs: DeliveryProofModel[] = [];
  isLoadingProofs = false;
  viewing: ProofGapModel | null = null;

  uploadKind = 'SIGNATURE';
  uploadError: string | null = null;

  readonly kindOptions = [
    { label: 'Signature',     value: 'SIGNATURE' },
    { label: 'Photograph',    value: 'PHOTO' },
    { label: 'Delivery note', value: 'DOCUMENT' }
  ];

  constructor(
    private logisticsService: LogisticsService,
    private messageService: MessageService
  ) {}

  ngOnInit() {
    this.load();
  }

  load() {
    this.isLoading = true;

    this.logisticsService.getProofCoverage().subscribe({
      next: (res) => {
        this.isLoading = false;
        this.coverage = res.result ?? null;
      },
      error: (err) => {
        this.isLoading = false;
        this.coverage = null;
        this.fail(err, 'Coverage could not be loaded.');
      }
    });
  }

  // ── Reading ─────────────────────────────────────────────────────────────────

  /** Of what was delivered, how much could actually be demonstrated. Null when nothing was. */
  get defensiblePercent(): number | null {
    if (!this.coverage || this.coverage.delivered === 0) return null;
    return Math.round(this.coverage.defensible * 1000 / this.coverage.delivered) / 10;
  }

  /** Nothing on file is worse than something thin, and the tag says which. */
  gapTag(gap: ProofGapModel): Severity {
    return gap.gap.startsWith('Delivered with no proof') ? 'danger' : 'warn';
  }

  gapLabel(gap: ProofGapModel): string {
    return gap.gap.startsWith('Delivered with no proof') ? 'Nothing at all' : 'Not enough';
  }

  /** A gap with nothing recorded needs a proof; one that is thin needs an artefact adding to it. */
  needsRecording(gap: ProofGapModel): boolean {
    return gap.gap.startsWith('Delivered with no proof');
  }

  kindLabel(kind: string): string {
    return this.kindOptions.find(o => o.value === kind)?.label ?? kind;
  }

  sourceLabel(source: string): string {
    return source === 'CARRIER' ? 'The carrier reported it' : 'Recorded here';
  }

  sizeLabel(bytes: number): string {
    return bytes < 1024 * 1024
      ? `${Math.round(bytes / 1024)} KB`
      : `${(bytes / 1024 / 1024).toFixed(1)} MB`;
  }

  // ── Recording a handover ────────────────────────────────────────────────────

  openCapture(gap: ProofGapModel) {
    this.capturing = gap;
    this.form = {
      receivedBy: '',
      relationship: '',
      // Pre-filled with when the system thinks it arrived, which is usually right and is always
      // better than whatever today happens to be.
      deliveredAt: gap.deliveredAt ? new Date(gap.deliveredAt) : new Date(),
      location: '',
      notes: ''
    };
    this.captureVisible = true;
  }

  /** What the server will refuse, said before the round trip. */
  get captureError(): string | null {
    if (!this.form.receivedBy.trim())
      return 'Say who took the goods. A proof naming nobody is a proof of nothing.';

    if (this.form.deliveredAt && this.form.deliveredAt.getTime() > Date.now() + 60_000)
      return 'A delivery cannot have happened in the future.';

    return null;
  }

  get canCapture(): boolean {
    return !this.isSubmitting && !this.captureError;
  }

  capture() {
    if (!this.canCapture || !this.capturing) return;
    this.isSubmitting = true;

    const consignment = this.capturing.consignmentUuid;

    this.logisticsService.recordProof(consignment, {
      receivedBy:   this.form.receivedBy.trim(),
      relationship: this.form.relationship.trim() || undefined,
      deliveredAt:  this.form.deliveredAt?.toISOString(),
      location:     this.form.location.trim() || undefined,
      notes:        this.form.notes.trim() || undefined
    }).subscribe({
      next: () => {
        this.isSubmitting = false;
        this.captureVisible = false;
        this.ok('Proof recorded. Attach the signature or a photograph to make it stand up.');
        this.load();
        // Straight into the artefact step: a name on its own is recorded and is not evidence.
        if (this.capturing) this.openDetail(this.capturing);
      },
      error: (err) => {
        this.isSubmitting = false;
        this.fail(err, 'That could not be recorded.');
      }
    });
  }

  // ── The artefacts ───────────────────────────────────────────────────────────

  openDetail(gap: ProofGapModel) {
    this.viewing = gap;
    this.proofs = [];
    this.uploadError = null;
    this.detailVisible = true;
    this.loadProofs(gap.consignmentUuid);
  }

  private loadProofs(consignmentUuid: string) {
    this.isLoadingProofs = true;

    this.logisticsService.getProofsForConsignment(consignmentUuid).subscribe({
      next: (res) => {
        this.isLoadingProofs = false;
        this.proofs = res.result ?? [];
      },
      error: (err) => {
        this.isLoadingProofs = false;
        this.proofs = [];
        this.fail(err, 'The proofs could not be loaded.');
      }
    });
  }

  onFileSelected(event: Event, proof: DeliveryProofModel) {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];

    if (!file) return;

    this.uploadError = this.rejectionReason(file);

    // Cleared either way, so choosing the same file again after a fix still fires a change.
    input.value = '';

    if (this.uploadError) return;

    this.upload(proof, file);
  }

  /**
   * Why this file cannot be stored, or null. The server checks the bytes against the claimed type
   * as well — this only saves a round trip on the obvious cases.
   */
  private rejectionReason(file: File): string | null {
    if (file.size === 0) return 'That file is empty.';

    if (file.size > ProofOfDeliveryComponent.MaxFileBytes)
      return `That file is ${this.sizeLabel(file.size)}. A signature is kilobytes and a `
           + 'doorstep photograph a couple of megabytes; the limit is 10 MB.';

    if (!ProofOfDeliveryComponent.AllowedTypes.includes(file.type))
      return `${file.type || 'That kind of file'} cannot be stored as proof. `
           + 'Use a PNG, JPEG, GIF or PDF.';

    return null;
  }

  private upload(proof: DeliveryProofModel, file: File) {
    this.isSubmitting = true;

    this.logisticsService.attachProofFile(proof.uuid, this.uploadKind, file).subscribe({
      next: () => {
        this.isSubmitting = false;
        this.ok('Attached.');
        if (this.viewing) this.loadProofs(this.viewing.consignmentUuid);
        this.load();
      },
      error: (err) => {
        this.isSubmitting = false;
        this.uploadError = err?.error?.message ?? 'That file could not be attached.';
      }
    });
  }

  removeFile(fileUuid: string) {
    this.isSubmitting = true;

    this.logisticsService.removeProofFile(fileUuid).subscribe({
      next: () => {
        this.isSubmitting = false;
        this.ok('Removed.');
        if (this.viewing) this.loadProofs(this.viewing.consignmentUuid);
        this.load();
      },
      error: (err) => {
        this.isSubmitting = false;
        this.fail(err, 'That could not be removed.');
      }
    });
  }

  /**
   * Fetched with the session's credentials and handed to the browser as a blob. A bare href would
   * be an unauthenticated request for evidence.
   */
  openFile(fileUuid: string, fileName: string) {
    this.logisticsService.downloadProofFile(fileUuid).subscribe({
      next: (blob) => {
        const url = URL.createObjectURL(blob);
        const link = document.createElement('a');
        link.href = url;
        link.download = fileName;
        link.click();
        URL.revokeObjectURL(url);
      },
      error: (err) => this.fail(err, 'That file could not be opened.')
    });
  }

  private ok(detail: string) {
    this.messageService.add({ severity: 'success', summary: 'Done', detail });
  }

  private fail(err: any, fallback: string) {
    this.messageService.add({
      severity: 'error', summary: 'Not allowed',
      detail: err?.error?.message ?? fallback, life: 8000
    });
  }
}
