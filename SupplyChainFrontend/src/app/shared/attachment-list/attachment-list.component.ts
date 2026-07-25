import { Component, Input, OnChanges, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ButtonModule } from 'primeng/button';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';
import { ToastModule } from 'primeng/toast';
import { AttachmentService, AttachmentModel } from '../../services/attachment.service';

@Component({
  selector: 'app-attachment-list',
  standalone: true,
  imports: [CommonModule, ButtonModule, TooltipModule, ToastModule],
  templateUrl: './attachment-list.component.html',
  styleUrls: ['./attachment-list.component.scss'],
  providers: [MessageService]
})
export class AttachmentListComponent implements OnChanges {
  // Document this attachment list belongs to. When documentId is empty, the list renders a
  // "save first" hint instead of an upload control — attachments need a real UUID to link to.
  @Input() interfaceCode!: string;
  @Input() documentId?: string | null;
  @Input() readOnly = false;
  @Input() compact = false;
  @Input() label = 'Attachments';

  attachments: AttachmentModel[] = [];
  isLoading = false;
  isUploading = false;

  constructor(private attachmentService: AttachmentService, private messageService: MessageService) {}

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
    const file = input.files?.[0];
    input.value = '';
    if (!file || !this.documentId) return;

    if (file.size > 20 * 1024 * 1024) {
      this.messageService.add({ severity: 'warn', summary: 'Too Large', detail: 'File must be under 20 MB.' });
      return;
    }

    this.isUploading = true;
    this.attachmentService.upload(file, this.interfaceCode, this.documentId).subscribe({
      next: (res) => {
        this.isUploading = false;
        if (res.success) {
          this.messageService.add({ severity: 'success', summary: 'Uploaded', detail: file.name });
          this.load();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message });
        }
      },
      error: (err) => {
        this.isUploading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message || 'Upload failed.' });
      }
    });
  }

  remove(att: AttachmentModel) {
    this.attachmentService.deleteAttachment(att.uuid).subscribe({
      next: () => {
        this.attachments = this.attachments.filter(a => a.uuid !== att.uuid);
      },
      error: (err) => {
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message || 'Failed to remove attachment.' });
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
}
