import { Component, Input, OnChanges, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TooltipModule } from 'primeng/tooltip';
import { InputTextModule } from 'primeng/inputtext';
import { MessageService, ConfirmationService } from 'primeng/api';
import { ToastModule } from 'primeng/toast';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { AttachmentService, AttachmentModel } from '../../services/attachment.service';

@Component({
  selector: 'app-attachment-list',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, TooltipModule, InputTextModule, ToastModule, ConfirmDialogModule],
  templateUrl: './attachment-list.component.html',
  styleUrls: ['./attachment-list.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class AttachmentListComponent implements OnChanges {
  // Document this attachment list belongs to. When documentId is empty, the list renders a
  // "save first" hint instead of an upload control — attachments need a real UUID to link to.
  @Input() interfaceCode!: string;
  @Input() documentId?: string | null;
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

  constructor(
    private attachmentService: AttachmentService,
    private messageService: MessageService,
    private confirmationService: ConfirmationService
  ) {}

  ngOnChanges(changes: SimpleChanges) {
    if (changes['documentId'] && this.documentId) {
      this.load();
    }
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
    if (!files.length || !this.documentId) return;

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
    this.confirmationService.confirm({
      message: `Remove <strong>${att.fileName}</strong>? This action cannot be undone.`,
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
