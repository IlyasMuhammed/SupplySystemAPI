import { Component, ElementRef, OnInit, ViewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { CheckboxModule } from 'primeng/checkbox';
import { DialogModule } from 'primeng/dialog';
import { ToastModule } from 'primeng/toast';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { ConfirmationService, MessageService } from 'primeng/api';
import { FLOW } from '../../../shared/flow';
import {
  EmailPreviewResult, EmailTemplateModel, OnboardingTemplatesService, TemplatePlaceholder, templateTypeLabel
} from '../../../services/onboarding-templates.service';
import { validEmailList } from '../../../shared/components/document-email-dialog/document-email-dialog.component';
import { PlaceholderPanelComponent } from './placeholder-panel.component';
import { HtmlFieldComponent } from './html-field.component';
import { EMAIL_TEMPLATES_ROUTE } from './email-template-list.component';

/**
 * A50 tasks 306–308 (D-26): edit one email template — subject and body HTML with the placeholder guide, a preview
 * with sample values, "Send test email", and Set as default / Reset to default / Clone.
 */
@Component({
  selector: 'app-email-template-editor',
  standalone: true,
  imports: [
    CommonModule, FormsModule, RouterModule, ButtonModule, InputTextModule, CheckboxModule, DialogModule, ToastModule,
    ConfirmDialogModule, PlaceholderPanelComponent, HtmlFieldComponent, ...FLOW
  ],
  providers: [MessageService, ConfirmationService],
  templateUrl: './email-template-editor.component.html',
  styleUrls: ['./templates.scss']
})
export class EmailTemplateEditorComponent implements OnInit {
  @ViewChild(HtmlFieldComponent) bodyField?: HtmlFieldComponent;
  @ViewChild('subjectInput') subjectInput?: ElementRef<HTMLInputElement>;

  readonly listRoute = EMAIL_TEMPLATES_ROUTE;

  uuid = '';
  template: EmailTemplateModel | null = null;
  loading = true;
  loadError: string | null = null;
  notFound = false;
  saving = false;
  busy = false;
  dirty = false;

  name = '';
  subjectTemplate = '';
  bodyHtml = '';
  isActive = true;

  placeholders: TemplatePlaceholder[] = [];
  placeholdersLoading = false;
  placeholdersError: string | null = null;
  target: 'subject' | 'body' = 'body';
  private subjectCaret: number | null = null;

  testEmail = '';
  sendingTest = false;
  testResult: { ok: boolean; text: string } | null = null;

  previewVisible = false;
  previewLoading = false;
  previewData: EmailPreviewResult | null = null;

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private api: OnboardingTemplatesService,
    private messages: MessageService,
    private confirm: ConfirmationService
  ) {}

  ngOnInit(): void {
    this.uuid = this.route.snapshot.paramMap.get('uuid') ?? '';
    this.load();
  }

  get typeLabel(): string { return templateTypeLabel(this.template?.templateType); }
  get subjectValid(): boolean { const s = this.subjectTemplate.trim(); return !!s && s.length <= 300 && !/[\r\n]/.test(this.subjectTemplate); }
  get bodyEmpty(): boolean { return !this.bodyHtml || /^(<p>|<\/p>|<br\s*\/?>|\s|&nbsp;)*$/i.test(this.bodyHtml); }
  get canSave(): boolean { return !!this.name.trim() && this.name.length <= 200 && this.subjectValid && !this.bodyEmpty && !this.saving; }
  get testValid(): boolean { return !!this.testEmail.trim() && validEmailList(this.testEmail, true) && !/[;,]/.test(this.testEmail); }

  load(): void {
    if (!this.uuid) { this.loading = false; this.notFound = true; return; }
    this.loading = true;
    this.loadError = null;
    this.api.getEmailTemplate(this.uuid).subscribe({
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

  private apply(t: EmailTemplateModel): void {
    this.template = t;
    this.name = t.name ?? '';
    this.subjectTemplate = t.subjectTemplate ?? '';
    this.bodyHtml = t.bodyHtml ?? '';
    this.isActive = t.isActive !== false;
    this.dirty = false;
  }

  private loadPlaceholders(type: string): void {
    this.placeholdersLoading = true;
    this.placeholdersError = null;
    this.api.getEmailPlaceholders(type).subscribe({
      next: res => { this.placeholdersLoading = false; this.placeholders = res?.result ?? []; },
      error: () => { this.placeholdersLoading = false; this.placeholdersError = 'The placeholders could not be loaded.'; }
    });
  }

  touch(): void { this.dirty = true; }
  setBody(v: string): void { this.bodyHtml = v; this.dirty = true; }

  rememberSubjectCaret(): void {
    const el = this.subjectInput?.nativeElement;
    this.subjectCaret = el ? el.selectionStart : null;
    this.target = 'subject';
  }

  insertPlaceholder(token: string): void {
    if (this.target === 'subject') {
      const s = this.subjectTemplate;
      const at = this.subjectCaret ?? s.length;
      this.subjectTemplate = s.slice(0, at) + token + s.slice(at);
      this.subjectCaret = at + token.length;
      this.dirty = true;
      const el = this.subjectInput?.nativeElement;
      if (el) setTimeout(() => { el.focus(); el.setSelectionRange(this.subjectCaret!, this.subjectCaret!); });
      return;
    }
    this.bodyField?.insertText(token);
  }

  save(): void {
    if (!this.canSave || !this.template) return;
    this.saving = true;
    this.api.updateEmailTemplate(this.uuid, {
      name: this.name.trim(), subjectTemplate: this.subjectTemplate.trim(), bodyHtml: this.bodyHtml, isActive: this.isActive
    }).subscribe({
      next: res => {
        this.saving = false;
        if (res?.result) this.apply(res.result); else this.dirty = false;
        this.messages.add({ severity: 'success', summary: 'Saved', detail: 'The email template has been saved.' });
      },
      error: err => {
        this.saving = false;
        this.messages.add({ severity: 'error', summary: 'Not saved', detail: err?.error?.message || 'The template could not be saved.' });
      }
    });
  }

  /** The server renders the saved template; say so when there are edits. */
  preview(): void {
    if (!this.template || this.previewLoading) return;
    this.previewLoading = true;
    this.previewVisible = true;
    this.previewData = null;
    this.api.previewEmailTemplate(this.uuid).subscribe({
      next: res => { this.previewLoading = false; this.previewData = res?.result ?? null; },
      error: err => {
        this.previewLoading = false;
        this.previewVisible = false;
        this.messages.add({ severity: 'error', summary: 'No preview', detail: err?.error?.message || 'The preview could not be generated.' });
      }
    });
  }

  sendTest(): void {
    if (!this.testValid || this.sendingTest || !this.template) return;
    this.sendingTest = true;
    this.testResult = null;
    this.api.sendTestEmail(this.uuid, this.testEmail.trim()).subscribe({
      next: res => {
        this.sendingTest = false;
        const r = res?.result;
        if (r?.sent) {
          this.testResult = { ok: true, text: `Test email sent to ${r.toEmail || this.testEmail.trim()}.` };
          this.messages.add({ severity: 'success', summary: 'Test sent', detail: this.testResult.text });
        } else {
          this.testResult = { ok: false, text: r?.error || res?.message || 'The test email could not be sent.' };
          this.messages.add({ severity: 'error', summary: 'Not sent', detail: this.testResult.text });
        }
      },
      error: err => {
        this.sendingTest = false;
        this.testResult = { ok: false, text: err?.error?.message || 'The test email could not be sent.' };
        this.messages.add({ severity: 'error', summary: 'Not sent', detail: this.testResult.text });
      }
    });
  }

  setDefault(): void {
    if (!this.template || this.busy) return;
    if (this.dirty) {
      this.messages.add({ severity: 'warn', summary: 'Unsaved changes', detail: 'Save the template first, then set it as the default.' });
      return;
    }
    this.busy = true;
    this.api.setDefaultEmailTemplate(this.uuid).subscribe({
      next: res => {
        this.busy = false;
        if (res?.result) this.apply(res.result);
        this.messages.add({ severity: 'success', summary: 'Default set', detail: `${this.typeLabel} emails now use this template.` });
      },
      error: err => this.fail(err, 'The default could not be changed.')
    });
  }

  confirmReset(): void {
    if (!this.template) return;
    this.confirm.confirm({
      header: 'Reset to default?',
      message: `The seeded "${this.typeLabel}" template gets its original subject and text back and becomes the default. Unsaved edits here are lost; your custom templates are kept.`,
      icon: 'pi pi-replay', acceptLabel: 'Reset', rejectLabel: 'Cancel',
      accept: () => this.reset()
    });
  }

  reset(): void {
    if (!this.template) return;
    this.busy = true;
    this.api.resetEmailTemplate(this.template.templateType).subscribe({
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
    this.api.createEmailTemplate({
      templateType: this.template.templateType, cloneFromUuid: this.uuid, isDefault: false,
      name: `Copy of ${this.name.trim() || this.template.name}`.slice(0, 200),
      subjectTemplate: this.subjectTemplate.trim() || null, bodyHtml: this.bodyEmpty ? null : this.bodyHtml
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
}
