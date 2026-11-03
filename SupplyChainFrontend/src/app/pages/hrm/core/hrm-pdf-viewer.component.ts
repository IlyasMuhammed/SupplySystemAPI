import { Component, EventEmitter, Input, OnChanges, OnDestroy, Output, SimpleChanges, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { DomSanitizer, SafeResourceUrl } from '@angular/platform-browser';
import { HttpResponse } from '@angular/common/http';
import { ButtonModule } from 'primeng/button';
import { Subscription } from 'rxjs';

import { saveDownload } from '../../../services/import.service';
import { HrmApi } from './hrm.api';
import { HrmDialogComponent } from './hrm-dialog.component';
import { hrmErrorAsync } from './hrm.shared';

/**
 * Task 271 / 210 — a PDF from a token-guarded route, shown in the page. A link cannot carry the token: the bytes come through
 * HttpClient (a refusal is read out of its blob) and show as an object URL, released on close. Phones often will not show a PDF
 * in a frame, so Download is always there.
 */
@Component({
  selector: 'hrm-pdf-viewer',
  standalone: true,
  imports: [CommonModule, ButtonModule, HrmDialogComponent],
  styleUrls: ['./hrm.scss', '../../finance/gl/gl.scss'],
  template: `
<hrm-dialog [visible]="visible" (visibleChange)="visibleChange.emit($event)" [title]="fileName" icon="pi-file-pdf" [saveLabel]="null" width="60rem" [error]="error">
  <div class="sf-empty" *ngIf="loading"><i class="pi pi-spin pi-spinner"></i> Loading…</div>
  <ng-container *ngIf="url">
    <iframe class="hrm-viewer" [src]="url" [title]="fileName" data-testid="pdf-frame"></iframe>
    <p-button label="Download" icon="pi pi-download" [text]="true" (onClick)="download()" data-testid="pdf-download"></p-button>
  </ng-container>
</hrm-dialog>`
})
export class HrmPdfViewerComponent implements OnChanges, OnDestroy {
  private readonly api = inject(HrmApi);
  private readonly sanitizer = inject(DomSanitizer);

  @Input() visible = false;
  @Output() visibleChange = new EventEmitter<boolean>();
  /** API path of the PDF. */
  @Input() path = '';
  /** An attachment instead of a path (resume, certificate). */
  @Input() attachmentUuid: string | null = null;
  @Input() fileName = 'document.pdf';

  url: SafeResourceUrl | null = null;
  loading = false;
  error = '';
  private objectUrl: string | null = null;
  private res: HttpResponse<Blob> | null = null;
  private sub?: Subscription;

  ngOnChanges(ch: SimpleChanges): void {
    if (ch['visible']) { if (this.visible) this.fetch(); else this.release(); }
  }

  ngOnDestroy(): void { this.release(); }

  private fetch(): void {
    this.release();
    this.loading = true;
    this.error = '';
    const call = this.attachmentUuid ? this.api.attachmentBlob(this.attachmentUuid) : this.api.blob(this.path);
    this.sub = call.subscribe({
      next: res => {
        this.loading = false;
        this.res = res;
        if (!res.body) return;
        this.objectUrl = URL.createObjectURL(res.body);
        // A blob: URL this page just made from a server response, never a remote address.
        this.url = this.sanitizer.bypassSecurityTrustResourceUrl(this.objectUrl);
      },
      error: async err => { this.loading = false; this.error = await hrmErrorAsync(err, 'The document could not be opened.'); }
    });
  }

  download(): void { if (this.res) saveDownload(this.res, this.fileName); }

  private release(): void {
    this.sub?.unsubscribe();
    if (this.objectUrl) URL.revokeObjectURL(this.objectUrl);
    this.objectUrl = null;
    this.url = null;
    this.res = null;
  }
}
