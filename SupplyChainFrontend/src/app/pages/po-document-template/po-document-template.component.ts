import { Component, ElementRef, OnInit, ViewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormsModule, FormBuilder, FormGroup, Validators } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { CheckboxModule } from 'primeng/checkbox';
import { DropdownModule } from 'primeng/dropdown';
import { TooltipModule } from 'primeng/tooltip';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';
import {
  PoDocumentTemplateService, PoDocumentTokenModel
} from '../../services/po-document-template.service';
import { AttachmentService } from '../../services/attachment.service';

const DEFAULT_BODY_HTML =
  '<p><strong>SUBJECT: Purchase Order {{PoNumber}}</strong></p>' +
  '<p>Dear Sir/Madam,</p>' +
  '<p>Please find below our Purchase Order {{PoNumber}} dated {{PoDate}}, to be delivered by {{DeliveryDate}}. ' +
  'Kindly acknowledge receipt and confirm your acceptance of the order details below.</p>' +
  '{{LineItemsTable}}' +
  '{{SignatureBlock}}';

@Component({
  selector: 'app-po-document-template',
  standalone: true,
  imports: [
    CommonModule, ReactiveFormsModule, FormsModule,
    ButtonModule, InputTextModule, TextareaModule, CheckboxModule, DropdownModule, TooltipModule, ToastModule
  ],
  templateUrl: './po-document-template.component.html',
  styleUrls: ['./po-document-template.component.scss'],
  providers: [MessageService]
})
export class PoDocumentTemplateComponent implements OnInit {
  @ViewChild('editor') editorRef!: ElementRef<HTMLDivElement>;

  form!: FormGroup;
  isLoading = true;
  isSaving = false;
  isUploadingLogo = false;
  logoUrl: string | null = null;

  tokens: PoDocumentTokenModel[] = [];
  tokenOptions: { label: string; value: string }[] = [];
  selectedTokenToInsert: string | null = null;

  constructor(
    private fb: FormBuilder,
    private service: PoDocumentTemplateService,
    private attachmentService: AttachmentService,
    private messageService: MessageService
  ) {}

  ngOnInit() {
    this.form = this.fb.group({
      companyName: ['', [Validators.maxLength(200)]],
      companyAddress: ['', [Validators.maxLength(500)]],
      companyTaxId: ['', [Validators.maxLength(50)]],
      companyPhone: ['', [Validators.maxLength(50)]],
      companyEmail: ['', [Validators.maxLength(100)]],
      showSignatureBlock: [true],
      signatureDisclaimer: ['This is a system generated document and does not require a signature.', [Validators.maxLength(500)]],
      preparedByLabel: ['Prepared By', [Validators.maxLength(100)]],
      approvedByLabel: ['Approved By', [Validators.maxLength(100)]],
      authorizedSignatoryLabel: ['Authorized Signatory', [Validators.maxLength(100)]],
      footerText: ['This is a system generated document and does not require a signature.', [Validators.maxLength(500)]],
    });
    this.loadTokens();
    this.load();
  }

  private loadTokens() {
    this.service.getTokens().subscribe({
      next: (res) => {
        this.tokens = res.result ?? [];
        this.tokenOptions = this.tokens.map(t => ({ label: `${t.label} — ${t.token}`, value: t.token }));
      },
      error: () => {}
    });
  }

  load() {
    this.isLoading = true;
    this.service.get().subscribe({
      next: (res) => {
        this.isLoading = false;
        const t = res.result;
        this.logoUrl = t?.companyLogoUrl ?? null;
        this.form.patchValue({
          companyName: t?.companyName ?? '',
          companyAddress: t?.companyAddress ?? '',
          companyTaxId: t?.companyTaxId ?? '',
          companyPhone: t?.companyPhone ?? '',
          companyEmail: t?.companyEmail ?? '',
          showSignatureBlock: t?.showSignatureBlock ?? true,
          signatureDisclaimer: t?.signatureDisclaimer ?? 'This is a system generated document and does not require a signature.',
          preparedByLabel: t?.preparedByLabel ?? 'Prepared By',
          approvedByLabel: t?.approvedByLabel ?? 'Approved By',
          authorizedSignatoryLabel: t?.authorizedSignatoryLabel ?? 'Authorized Signatory',
          footerText: t?.footerText ?? 'This is a system generated document and does not require a signature.',
        });
        // contenteditable content is set imperatively once (not via Angular binding) so the
        // cursor position isn't reset on every change-detection pass while the user types.
        setTimeout(() => {
          if (this.editorRef) this.editorRef.nativeElement.innerHTML = t?.bodyHtml ?? DEFAULT_BODY_HTML;
        });
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load PO document template' });
      }
    });
  }

  // ── Rich-text toolbar ──────────────────────────────────────────────────────

  format(command: 'bold' | 'italic' | 'underline' | 'insertUnorderedList' | 'justifyLeft' | 'justifyCenter' | 'justifyRight') {
    this.editorRef.nativeElement.focus();
    document.execCommand(command, false);
  }

  insertToken() {
    if (!this.selectedTokenToInsert) return;
    this.editorRef.nativeElement.focus();
    document.execCommand('insertText', false, this.selectedTokenToInsert);
    this.selectedTokenToInsert = null;
  }

  // ── Logo upload ────────────────────────────────────────────────────────────

  // Fixed slot id — the template is a single global row, so the logo has no natural document id.
  private readonly LOGO_DOCUMENT_ID = '00000000-0000-0000-0000-000000000001';

  onLogoSelected(event: Event) {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;
    this.isUploadingLogo = true;
    this.attachmentService.upload(file, 'PO_TEMPLATE_LOGO', this.LOGO_DOCUMENT_ID).subscribe({
      next: (res) => {
        if (!res.success) {
          this.isUploadingLogo = false;
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Logo upload failed' });
          return;
        }
        this.attachmentService.getAttachments('PO_TEMPLATE_LOGO', this.LOGO_DOCUMENT_ID).subscribe({
          next: (listRes) => {
            this.isUploadingLogo = false;
            const latest = (listRes.result ?? []).sort((a, b) =>
              new Date(b.uploadedDate).getTime() - new Date(a.uploadedDate).getTime())[0];
            if (latest) {
              this.logoUrl = latest.fileUrl;
            }
          },
          error: () => { this.isUploadingLogo = false; }
        });
      },
      error: () => {
        this.isUploadingLogo = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Logo upload failed' });
      }
    });
    input.value = '';
  }

  resolveLogoUrl(url: string): string {
    return this.attachmentService.resolveUrl(url);
  }

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.isSaving = true;
    const payload = {
      ...this.form.value,
      companyLogoUrl: this.logoUrl ?? undefined,
      bodyHtml: this.editorRef.nativeElement.innerHTML,
    };
    this.service.upsert(payload).subscribe({
      next: (res) => {
        this.isSaving = false;
        if (res.success) {
          this.messageService.add({ severity: 'success', summary: 'Saved', detail: 'PO document template updated' });
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Save failed' });
        }
      },
      error: (err) => {
        this.isSaving = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Save failed' });
      }
    });
  }

  get f() { return this.form.controls; }
}
