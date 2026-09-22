import { Component, EventEmitter, Input, OnChanges, OnDestroy, Output, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { DomSanitizer, SafeResourceUrl } from '@angular/platform-browser';
import { DialogModule } from 'primeng/dialog';
import { ButtonModule } from 'primeng/button';
import { Subscription } from 'rxjs';

import { SalesInvoiceService } from '../../../../services/sales-invoice.service';

/** An invoice's PDF, rendered as it stands now, in a dialog. The bytes are fetched with the caller's token, so a plain link cannot open it. */
@Component({
  selector: 'app-sales-invoice-pdf-dialog',
  standalone: true,
  imports: [CommonModule, DialogModule, ButtonModule],
  template: `
    <p-dialog [header]="'Invoice ' + invoiceNumber" [visible]="visible" (visibleChange)="onVisibleChange($event)"
              [modal]="true" [draggable]="false" [maximizable]="true"
              [style]="{width:'64rem', maxWidth:'96vw'}" data-testid="pdf-dialog">
      <div class="pdf-state" *ngIf="isLoading" data-testid="pdf-loading">
        <i class="pi pi-spin pi-spinner"></i> Preparing the invoice…
      </div>
      <div class="pdf-state" *ngIf="failed" data-testid="pdf-failed">
        <i class="pi pi-exclamation-triangle"></i> The invoice PDF could not be loaded.
      </div>
      <iframe *ngIf="url" [src]="url" class="pdf-frame" title="Invoice PDF" data-testid="pdf-frame"></iframe>

      <ng-template pTemplate="footer">
        <p-button label="Download" icon="pi pi-download" [text]="true" severity="secondary"
                  [disabled]="!url" (onClick)="download()" data-testid="pdf-download"></p-button>
        <p-button label="Close" (onClick)="close()" data-testid="pdf-close"></p-button>
      </ng-template>
    </p-dialog>
  `,
  styles: [`
    .pdf-state { text-align: center; padding: 3rem 1rem; color: var(--text-color-secondary); }
    .pdf-frame { width: 100%; height: 70vh; border: 1px solid var(--surface-200); border-radius: .375rem; background: var(--surface-0); }
  `]
})
export class SalesInvoicePdfDialogComponent implements OnChanges, OnDestroy {
  @Input() visible = false;
  @Output() visibleChange = new EventEmitter<boolean>();
  @Input() invoiceUuid: string | null = null;
  @Input() invoiceNumber = '';

  url: SafeResourceUrl | null = null;
  isLoading = false;
  failed = false;

  private objectUrl: string | null = null;
  private request: Subscription | null = null;

  constructor(private invoiceService: SalesInvoiceService, private sanitizer: DomSanitizer) {}

  ngOnChanges(changes: SimpleChanges) {
    if (!this.visible) { this.release(); return; }
    if ((changes['visible'] || changes['invoiceUuid']) && this.invoiceUuid) this.load(this.invoiceUuid);
  }

  ngOnDestroy() { this.release(); }

  private load(uuid: string) {
    this.release();
    this.isLoading = true;

    this.request = this.invoiceService.downloadPdf(uuid).subscribe({
      next: (blob) => {
        this.isLoading = false;
        this.objectUrl = URL.createObjectURL(blob);
        // The address is a blob this page just made, not something a user or the server supplied.
        this.url = this.sanitizer.bypassSecurityTrustResourceUrl(this.objectUrl);
      },
      error: () => {
        this.isLoading = false;
        this.failed = true;
      }
    });
  }

  /** Stops an answer that is still coming and lets go of the bytes already here. */
  private release() {
    this.request?.unsubscribe();
    this.request = null;
    if (this.objectUrl) URL.revokeObjectURL(this.objectUrl);
    this.objectUrl = null;
    this.url = null;
    this.isLoading = false;
    this.failed = false;
  }

  download() {
    if (!this.objectUrl) return;
    const link = document.createElement('a');
    link.href = this.objectUrl;
    link.download = `${this.invoiceNumber || 'invoice'}.pdf`;
    link.click();
  }

  onVisibleChange(visible: boolean) {
    this.visible = visible;
    this.visibleChange.emit(visible);
    if (!visible) this.release();
  }

  close() { this.onVisibleChange(false); }
}
