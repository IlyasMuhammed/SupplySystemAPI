import { Component, OnDestroy, OnInit, QueryList, ViewChildren } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { DomSanitizer, SafeResourceUrl } from '@angular/platform-browser';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { CheckboxModule } from 'primeng/checkbox';
import { DialogModule } from 'primeng/dialog';
import { ToastModule } from 'primeng/toast';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { ConfirmationService, MessageService } from 'primeng/api';
import { FLOW } from '../../../shared/flow';
import { AttachmentService } from '../../../services/attachment.service';
import {
  DocumentTemplateModel, DocumentTemplateRequest, OnboardingTemplatesService, TemplatePlaceholder,
  errorMessageOf, templateTypeLabel
} from '../../../services/onboarding-templates.service';
import { PlaceholderPanelComponent } from './placeholder-panel.component';
import { HtmlFieldComponent } from './html-field.component';
import { DOC_TEMPLATES_ROUTE } from './document-template-list.component';

export const DOC_TEMPLATE_LOGO = 'DOC_TEMPLATE_LOGO';
const HEX = /^#[0-9A-Fa-f]{6}$/;
const MAX_LOGO_BYTES = 2 * 1024 * 1024;

type HtmlKey = 'headerHtml' | 'bodyHtml' | 'footerHtml';

/**
 * A50 tasks 298–304 (D-25): edit one PDF template — header / body / footer HTML with a placeholder panel, the logo
 * (drag and drop, attachment interface DOC_TEMPLATE_LOGO), primary colour, bank details, terms, a PDF preview of the
 * unsaved edit, and Set as default / Reset to default / Clone.
 */
@Component({
  selector: 'app-document-template-editor',
  standalone: true,
  imports: [
    CommonModule, FormsModule, RouterModule, ButtonModule, InputTextModule, TextareaModule, CheckboxModule, DialogModule,
    ToastModule, ConfirmDialogModule, PlaceholderPanelComponent, HtmlFieldComponent, ...FLOW
  ],
  providers: [MessageService, ConfirmationService],
  templateUrl: './document-template-editor.component.html',
  styleUrls: ['./templates.scss']
})
export class DocumentTemplateEditorComponent implements OnInit, OnDestroy {
  @ViewChildren(HtmlFieldComponent) htmlFields!: QueryList<HtmlFieldComponent>;

  readonly listRoute = DOC_TEMPLATES_ROUTE;
  readonly sections: { key: HtmlKey; label: string; hint: string; min: number }[] = [
    { key: 'headerHtml', label: 'Header', hint: 'Printed at the top of every page, beside the logo.', min: 90 },
    { key: 'bodyHtml', label: 'Body', hint: 'Leave empty to keep the built-in layout with your branding. Write a body to replace it; block placeholders such as {LineItems} go on a line of their own.', min: 200 },
    { key: 'footerHtml', label: 'Footer', hint: 'Printed at the bottom of every page.', min: 70 }
  ];

  uuid = '';
  template: DocumentTemplateModel | null = null;
  loading = true;
  loadError: string | null = null;
  notFound = false;
  saving = false;
  busy = false;
  dirty = false;

  // Form
  name = '';
  headerHtml = '';
  bodyHtml = '';
  footerHtml = '';
  primaryColor = '#4A42CC';
  showBankDetails = false;
  bankDetailsText = '';
  termsAndConditions = '';
  isActive = true;
  logoAttachmentUuid: string | null = null;

  // Logo
  logoSrc: string | null = null;
  uploadingLogo = false;
  dragOver = false;
  private logoObjectUrl: string | null = null;

  // Placeholders
  placeholders: TemplatePlaceholder[] = [];
  placeholdersLoading = false;
  placeholdersError: string | null = null;
  target: HtmlKey = 'bodyHtml';

  // Preview
  previewVisible = false;
  previewLoading = false;
  previewUrl: SafeResourceUrl | null = null;
  private previewObjectUrl: string | null = null;

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private api: OnboardingTemplatesService,
    private attachments: AttachmentService,
    private sanitizer: DomSanitizer,
    private messages: MessageService,
    private confirm: ConfirmationService
  ) {}

  ngOnInit(): void {
    this.uuid = this.route.snapshot.paramMap.get('uuid') ?? '';
    this.load();
  }

  ngOnDestroy(): void {
    this.revoke('logo');
    this.revoke('preview');
  }

  get typeLabel(): string { return templateTypeLabel(this.template?.templateType); }
  get colorValid(): boolean { return !this.primaryColor || HEX.test(this.primaryColor); }
  get canSave(): boolean { return !!this.name.trim() && this.name.length <= 200 && this.colorValid && !this.saving
    && this.bankDetailsText.length <= 2000 && this.termsAndConditions.length <= 4000; }
  get targetLabel(): string { return this.sections.find(s => s.key === this.target)?.label.toLowerCase() ?? ''; }

  load(): void {
    if (!this.uuid) { this.loading = false; this.notFound = true; return; }
    this.loading = true;
    this.loadError = null;
    this.api.getDocumentTemplate(this.uuid).subscribe({
      next: res => {
        this.loading = false;
        if (!res?.result) { this.notFound = true; return; }
        this.apply(res.result);
        this.loadPlaceholders(res.result.templateType);
      },
      error: err => {
        this.loading = false;
        if (err?.status === 404) this.notFound = true;
        else this.loadError = err?.error?.message || 'The template could not be loaded.';
      }
    });
  }

  private apply(t: DocumentTemplateModel): void {
    this.template = t;
    this.name = t.name ?? '';
    this.headerHtml = t.headerHtml ?? '';
    this.bodyHtml = t.bodyHtml ?? '';
    this.footerHtml = t.footerHtml ?? '';
    this.primaryColor = t.primaryColor || '#4A42CC';
    this.showBankDetails = !!t.showBankDetails;
    this.bankDetailsText = t.bankDetailsText ?? '';
    this.termsAndConditions = t.termsAndConditions ?? '';
    this.isActive = t.isActive !== false;
    this.logoAttachmentUuid = t.logoAttachmentUuid ?? null;
    this.showLogo(t.logoUrl ?? null);
    this.dirty = false;
  }

  private loadPlaceholders(type: string): void {
    this.placeholdersLoading = true;
    this.placeholdersError = null;
    this.api.getDocumentPlaceholders(type).subscribe({
      next: res => { this.placeholdersLoading = false; this.placeholders = res?.result ?? []; },
      error: () => { this.placeholdersLoading = false; this.placeholdersError = 'The placeholders could not be loaded.'; }
    });
  }

  // ── Editing ───────────────────────────────────────────────────────────────────

  setHtml(key: HtmlKey, value: string): void { this[key] = value; this.dirty = true; }
  touch(): void { this.dirty = true; }

  onColorText(v: string): void {
    const s = (v ?? '').trim();
    this.primaryColor = s && !s.startsWith('#') ? `#${s}` : s;
    this.dirty = true;
  }

  insertPlaceholder(token: string): void {
    const idx = this.sections.findIndex(s => s.key === this.target);
    const field = this.htmlFields?.get(idx);
    if (field) field.insertText(token);
  }

  /** The request both save and preview send. */
  request(): DocumentTemplateRequest {
    const html = (v: string) => (v ?? '').trim() && !/^(<br\s*\/?>|\s|&nbsp;)*$/i.test(v) ? v : null;
    return {
      name: this.name.trim(),
      headerHtml: html(this.headerHtml),
      bodyHtml: html(this.bodyHtml),
      footerHtml: html(this.footerHtml),
      logoAttachmentUuid: this.logoAttachmentUuid,
      primaryColor: this.primaryColor ? this.primaryColor.toUpperCase() : null,
      showBankDetails: this.showBankDetails,
      bankDetailsText: this.bankDetailsText.trim() || null,
      termsAndConditions: this.termsAndConditions.trim() || null,
      isActive: this.isActive
    };
  }

  save(): void {
    if (!this.canSave || !this.template) return;
    this.saving = true;
    this.api.updateDocumentTemplate(this.uuid, this.request()).subscribe({
      next: res => {
        this.saving = false;
        if (res?.result) this.apply(res.result); else this.dirty = false;
        this.messages.add({ severity: 'success', summary: 'Saved', detail: 'The template has been saved.' });
      },
      error: err => {
        this.saving = false;
        this.messages.add({ severity: 'error', summary: 'Not saved', detail: err?.error?.message || 'The template could not be saved.' });
      }
    });
  }

  // ── Logo ──────────────────────────────────────────────────────────────────────

  onLogoInput(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (file) this.uploadLogo(file);
    input.value = '';
  }

  onDragOver(e: DragEvent): void { e.preventDefault(); this.dragOver = true; }
  onDragLeave(): void { this.dragOver = false; }
  onDrop(e: DragEvent): void {
    e.preventDefault();
    this.dragOver = false;
    const file = e.dataTransfer?.files?.[0];
    if (file) this.uploadLogo(file);
  }

  uploadLogo(file: File): void {
    if (!file.type.startsWith('image/')) {
      this.messages.add({ severity: 'warn', summary: 'Not a picture', detail: 'Choose a PNG, JPG or other image file.' });
      return;
    }
    if (file.size > MAX_LOGO_BYTES) {
      this.messages.add({ severity: 'warn', summary: 'Too large', detail: 'The logo must be 2 MB or smaller.' });
      return;
    }
    this.uploadingLogo = true;
    this.attachments.upload(file, DOC_TEMPLATE_LOGO, this.uuid).subscribe({
      next: res => {
        if (!res?.success || !res.result) {
          this.uploadingLogo = false;
          this.messages.add({ severity: 'error', summary: 'Not uploaded', detail: res?.message || 'The logo could not be uploaded.' });
          return;
        }
        const attUuid = String(res.result);
        this.logoAttachmentUuid = attUuid;
        this.dirty = true;
        // Show it at once from the file itself; the server copy replaces it when listed.
        this.showLocal(file);
        this.attachments.getAttachments(DOC_TEMPLATE_LOGO, this.uuid).subscribe({
          next: list => {
            this.uploadingLogo = false;
            const found = (list?.result ?? []).find(a => a.uuid?.toLowerCase() === attUuid.toLowerCase());
            this.showLogo(found?.fileUrl ?? null);
          },
          error: () => { this.uploadingLogo = false; }
        });
      },
      error: err => {
        this.uploadingLogo = false;
        this.messages.add({ severity: 'error', summary: 'Not uploaded', detail: err?.error?.message || 'The logo could not be uploaded.' });
      }
    });
  }

  removeLogo(): void {
    this.logoAttachmentUuid = null;
    this.revoke('logo');
    this.logoSrc = null;
    this.dirty = true;
  }

  private showLocal(file: File): void {
    try {
      this.revoke('logo');
      this.logoObjectUrl = URL.createObjectURL(file);
      this.logoSrc = this.logoObjectUrl;
    } catch { /* no preview */ }
  }

  /** A public /uploads URL is used as is; an /api/ URL needs the token, so it is fetched as a blob. */
  private showLogo(url: string | null): void {
    if (!url) { if (!this.logoObjectUrl) this.logoSrc = null; return; }
    if (this.attachments.isApiUrl(url)) {
      this.attachments.download(url).subscribe({
        next: blob => { this.revoke('logo'); this.logoObjectUrl = URL.createObjectURL(blob); this.logoSrc = this.logoObjectUrl; },
        error: () => {}
      });
      return;
    }
    this.revoke('logo');
    this.logoSrc = this.attachments.resolveUrl(url);
  }

  // ── Preview ───────────────────────────────────────────────────────────────────

  preview(): void {
    if (!this.template || this.previewLoading) return;
    this.previewLoading = true;
    this.previewVisible = true;
    this.revoke('preview');
    this.previewUrl = null;
    this.api.previewDocumentTemplate(this.uuid, this.request()).subscribe({
      next: blob => {
        this.previewLoading = false;
        this.previewObjectUrl = URL.createObjectURL(blob);
        this.previewUrl = this.sanitizer.bypassSecurityTrustResourceUrl(this.previewObjectUrl);
      },
      error: async err => {
        this.previewLoading = false;
        this.previewVisible = false;
        this.messages.add({ severity: 'error', summary: 'No preview', detail: await errorMessageOf(err, 'The preview could not be generated.') });
      }
    });
  }

  closePreview(): void {
    this.previewVisible = false;
    this.revoke('preview');
    this.previewUrl = null;
  }

  // ── Default / reset / clone ───────────────────────────────────────────────────

  setDefault(): void {
    if (!this.template || this.busy) return;
    if (this.dirty) {
      this.messages.add({ severity: 'warn', summary: 'Unsaved changes', detail: 'Save the template first, then set it as the default.' });
      return;
    }
    this.busy = true;
    this.api.setDefaultDocumentTemplate(this.uuid).subscribe({
      next: res => {
        this.busy = false;
        if (res?.result) this.apply(res.result);
        this.messages.add({ severity: 'success', summary: 'Default set', detail: `Every ${this.typeLabel.toLowerCase()} PDF now uses this template.` });
      },
      error: err => this.fail(err, 'The default could not be changed.')
    });
  }

  confirmReset(): void {
    if (!this.template) return;
    this.confirm.confirm({
      header: 'Reset to default?',
      message: `The seeded ${this.typeLabel.toLowerCase()} template gets its original values back and becomes the default. Unsaved edits here are lost; your custom templates are kept.`,
      icon: 'pi pi-replay', acceptLabel: 'Reset', rejectLabel: 'Cancel',
      accept: () => this.reset()
    });
  }

  reset(): void {
    if (!this.template) return;
    this.busy = true;
    this.api.resetDocumentTemplate(this.template.templateType).subscribe({
      next: res => {
        this.busy = false;
        this.messages.add({ severity: 'success', summary: 'Reset', detail: 'The default template has been restored.' });
        const t = res?.result;
        if (t && t.uuid !== this.uuid) this.router.navigate([this.listRoute, t.uuid]).then(() => { this.uuid = t.uuid; this.apply(t); });
        else if (t) this.apply(t);
        else this.load();
      },
      error: err => this.fail(err, 'The template could not be reset.')
    });
  }

  clone(): void {
    if (!this.template || this.busy) return;
    this.busy = true;
    this.api.createDocumentTemplate({
      ...this.request(), templateType: this.template.templateType, cloneFromUuid: this.uuid,
      name: `Copy of ${this.name.trim() || this.template.name}`.slice(0, 200), isDefault: false
    }).subscribe({
      next: res => {
        this.busy = false;
        const t = res?.result;
        this.messages.add({ severity: 'success', summary: 'Cloned', detail: `"${t?.name ?? 'Copy'}" was created.` });
        if (t?.uuid) this.router.navigate([this.listRoute, t.uuid]).then(() => { this.uuid = t.uuid; this.apply(t); });
      },
      error: err => this.fail(err, 'The template could not be cloned.')
    });
  }

  private fail(err: any, fallback: string): void {
    this.busy = false;
    this.messages.add({ severity: 'error', summary: 'Error', detail: err?.error?.message || fallback });
  }

  private revoke(which: 'logo' | 'preview'): void {
    const key = which === 'logo' ? 'logoObjectUrl' : 'previewObjectUrl';
    const url = this[key];
    if (url) { try { URL.revokeObjectURL(url); } catch { /* ignore */ } }
    this[key] = null;
  }
}
