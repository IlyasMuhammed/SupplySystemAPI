import { Component, DestroyRef, Input, OnChanges, OnInit, SimpleChanges, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TooltipModule } from 'primeng/tooltip';
import { InputTextModule } from 'primeng/inputtext';
import { MessageService, ConfirmationService } from 'primeng/api';
import { ToastModule } from 'primeng/toast';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { AttachmentService, AttachmentModel } from '../../services/attachment.service';
import { AttachmentAccessRule, AttachmentPolicyService } from '../../services/attachment-policy.service';
import { AuthService } from '../../pages/service/auth.service';

/**
 * What a fetched file may be opened as in a new tab. A blob URL belongs to this app's origin, so a file
 * typed as a page or an SVG would run there; anything not on this list is handed over as a download instead.
 */
const OPENABLE_TYPES = new Set([
  'application/pdf', 'image/png', 'image/jpeg', 'image/gif', 'image/webp', 'image/bmp', 'text/plain'
]);

function escapeHtml(text: string): string {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
             .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
}

@Component({
  selector: 'app-attachment-list',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, TooltipModule, InputTextModule, ToastModule, ConfirmDialogModule],
  templateUrl: './attachment-list.component.html',
  styleUrls: ['./attachment-list.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class AttachmentListComponent implements OnInit, OnChanges {
  // Document this attachment list belongs to. When documentId is empty, the list renders a
  // "save first" hint instead of an upload control — attachments need a real UUID to link to.
  @Input() interfaceCode!: string;
  @Input() documentId?: string | null;
  // A page can always make the panel read-only (a cancelled payment, a sales invoice's filed copies). It
  // cannot do the opposite: adding and removing are offered only as far as the server's rules allow.
  @Input() readOnly = false;
  @Input() compact = false;
  // True single-line layout: no header, no notes field, files shown as small removable chips
  // next to the upload button — for forms where the attachment section shouldn't eat vertical
  // space (e.g. PR create's General Information card). Distinct from `compact`, which still keeps
  // the full stacked card-per-file layout, just smaller — existing `[compact]` usages are untouched.
  @Input() inline = false;
  @Input() label = 'Attachments';

  attachments: AttachmentModel[] = [];
  isLoading = false;
  isUploading = false;
  uploadingCount = 0;
  // Optional remark applied to the batch of files picked in a single upload action.
  pendingNotes = '';

  /**
   * This kind of document's rule from the server (GET api/attachments/policy). Null until it arrives, and
   * when it cannot be had — and while it is null the panel is read-only: it fails closed.
   */
  private rule: AttachmentAccessRule | null = null;
  private ruleRequestedFor?: string;

  private readonly destroyRef = inject(DestroyRef);

  constructor(
    private attachmentService: AttachmentService,
    private messageService: MessageService,
    private confirmationService: ConfirmationService,
    private policy: AttachmentPolicyService,
    private auth: AuthService
  ) {}

  ngOnInit() {
    this.loadRule();
  }

  ngOnChanges(changes: SimpleChanges) {
    if (changes['interfaceCode']) {
      this.loadRule();
    }
    if (changes['documentId'] && this.documentId) {
      this.load();
    }
  }

  /** Whether to offer adding files: never on a read-only page, and only to a holder of the rule's upload permission. */
  get canUpload(): boolean {
    // AuthService.hasPermission alone: it is all a page's test double provides.
    return !this.readOnly && !!this.rule && this.rule.upload.some(code => this.auth.hasPermission(code));
  }

  /**
   * Whether to offer removing this file: never on a read-only page or before the rule is known, never for a
   * document the system filed, and otherwise as the server said for this caller (it can turn on who uploaded it).
   */
  canRemove(att: AttachmentModel): boolean {
    return !this.readOnly && !!this.rule && !att.isGenerated && att.canRemove === true;
  }

  private loadRule() {
    if (!this.interfaceCode || this.ruleRequestedFor === this.interfaceCode) return;

    this.ruleRequestedFor = this.interfaceCode;
    this.rule = null;
    this.policy.ruleFor(this.interfaceCode)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(rule => this.rule = rule);
  }

  load() {
    if (!this.documentId) return;
    this.isLoading = true;
    this.attachmentService.getAttachments(this.interfaceCode, this.documentId).subscribe({
      next: (res) => {
        this.isLoading = false;
        this.attachments = res.success ? res.result : [];
      },
      error: () => { this.isLoading = false; }
    });
  }

  onFileSelected(event: Event) {
    const input = event.target as HTMLInputElement;
    const files = Array.from(input.files ?? []);
    input.value = '';
    if (!files.length || !this.documentId || !this.canUpload) return;

    const notes = this.pendingNotes.trim() || undefined;
    this.pendingNotes = '';

    const tooLarge = files.filter(f => f.size > 20 * 1024 * 1024);
    const toUpload = files.filter(f => f.size <= 20 * 1024 * 1024);
    tooLarge.forEach(f =>
      this.messageService.add({ severity: 'warn', summary: 'Too Large', detail: `${f.name} exceeds 20 MB.` }));

    if (!toUpload.length) return;

    this.isUploading = true;
    this.uploadingCount = toUpload.length;
    let remaining = toUpload.length;
    let anyFailed = false;

    toUpload.forEach(file => {
      this.attachmentService.upload(file, this.interfaceCode, this.documentId!, notes).subscribe({
        next: (res) => {
          if (!res.success) {
            anyFailed = true;
            this.messageService.add({ severity: 'error', summary: 'Error', detail: `${file.name}: ${res.message}` });
          }
          this.finishUpload(--remaining, anyFailed);
        },
        error: (err) => {
          anyFailed = true;
          // status 0 means the request never got a response from the server (network drop, CORS
          // rejection, timeout, …) — err.error is then the raw browser error, not a server message.
          const reason = err?.status === 0
            ? 'Network error — check your connection and try again.'
            : (err?.error?.message || 'Upload failed.');
          this.messageService.add({ severity: 'error', summary: 'Error', detail: `${file.name}: ${reason}` });
          this.finishUpload(--remaining, anyFailed);
        }
      });
    });
  }

  private finishUpload(remaining: number, anyFailed: boolean) {
    if (remaining > 0) return;
    this.isUploading = false;
    if (!anyFailed) {
      this.messageService.add({ severity: 'success', summary: 'Uploaded', detail: `${this.uploadingCount} file${this.uploadingCount > 1 ? 's' : ''} added.` });
    }
    this.load();
  }

  remove(att: AttachmentModel) {
    if (!this.canRemove(att)) return;

    this.confirmationService.confirm({
      // The dialog renders its message as HTML, and a file name is whatever the uploader called it.
      message: `Remove <strong>${escapeHtml(att.fileName)}</strong>? This action cannot be undone.`,
      header: 'Remove Attachment',
      icon: 'pi pi-exclamation-triangle',
      acceptLabel: 'Remove',
      acceptButtonStyleClass: 'p-button-danger',
      rejectLabel: 'Cancel',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => {
        this.attachmentService.deleteAttachment(att.uuid).subscribe({
          next: () => {
            this.attachments = this.attachments.filter(a => a.uuid !== att.uuid);
          },
          error: (err) => {
            this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message || 'Failed to remove attachment.' });
          }
        });
      }
    });
  }

  resolveUrl(url: string): string { return this.attachmentService.resolveUrl(url); }

  // An uploaded file is a public static url and opens as a plain link. A generated document is served
  // by the API behind the caller's token, which a link cannot carry: it has no href to fall back to
  // (a middle-click would only open an "unauthorized" page), and is fetched and shown here instead.
  hrefFor(att: AttachmentModel): string | null {
    return this.attachmentService.isApiUrl(att.fileUrl) ? null : this.resolveUrl(att.fileUrl);
  }

  open(att: AttachmentModel, event: Event) {
    if (!this.attachmentService.isApiUrl(att.fileUrl)) return;

    event.preventDefault();
    this.attachmentService.download(att.fileUrl).subscribe({
      next: (blob) => {
        const declared = (att.contentType || blob.type || '').split(';')[0].trim().toLowerCase();
        const type = OPENABLE_TYPES.has(declared) ? declared : 'application/octet-stream';
        const url = URL.createObjectURL(new Blob([blob], { type }));
        window.open(url, '_blank', 'noopener');
        // The new tab has the bytes by now; do not keep them alive for the life of this page.
        setTimeout(() => URL.revokeObjectURL(url), 60_000);
      },
      error: (err) => {
        const detail = err?.status === 403
          ? 'You do not have permission to open this document.'
          : err?.status === 404
            ? 'This document is no longer available.'
            : 'The document could not be opened. Try again.';
        this.messageService.add({ severity: 'error', summary: 'Error', detail });
      }
    });
  }

  formatSize(bytes?: number): string {
    if (!bytes) return '';
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  }

  formatDate(iso: string): string {
    return new Date(iso).toLocaleString('en-GB', {
      day: '2-digit', month: 'short', year: 'numeric', hour: '2-digit', minute: '2-digit'
    });
  }
}
