import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router, RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { TableModule } from 'primeng/table';
import { DialogModule } from 'primeng/dialog';
import { SelectModule } from 'primeng/select';
import { InputTextModule } from 'primeng/inputtext';
import { ToastModule } from 'primeng/toast';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { TooltipModule } from 'primeng/tooltip';
import { ConfirmationService, MessageService } from 'primeng/api';
import { FLOW } from '../../../shared/flow';
import {
  DOCUMENT_TEMPLATE_TYPES, DocumentTemplateModel, OnboardingTemplatesService, templateTypeLabel
} from '../../../services/onboarding-templates.service';

export const DOC_TEMPLATES_ROUTE = '/portal/pages/onboarding/document-templates';

/** A50 task 297 (D-25): the org's PDF templates — one default per document type. DOC_TEMPLATE_MANAGE. */
@Component({
  selector: 'app-document-template-list',
  standalone: true,
  imports: [
    CommonModule, FormsModule, RouterModule, ButtonModule, TableModule, DialogModule, SelectModule, InputTextModule,
    ToastModule, ConfirmDialogModule, TooltipModule, ...FLOW
  ],
  providers: [MessageService, ConfirmationService],
  template: `
<p-toast></p-toast>
<p-confirmDialog [style]="{ width: '440px' }" [breakpoints]="{ '640px': '92vw' }"></p-confirmDialog>
<sf-page [crumbs]="['Settings', 'Organization', 'Document templates']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button label="New template" icon="pi pi-plus" (onClick)="openNew()" data-testid="new-template"></p-button>
    <p-button label="Refresh" icon="pi pi-refresh" [text]="true" [loading]="loading" (onClick)="load()"></p-button>
  </div>
  <div class="sf-body">
    <div class="sf-lr-head">
      <div>
        <h1>Document templates <span class="sf-count">{{ templates.length }}</span></h1>
        <div class="sf-sub">The logo, colour, header, footer, bank details and terms printed on your PDFs. Each document type uses its default template.</div>
      </div>
    </div>
    <div class="sf-filters">
      <button type="button" class="sf-chip" [class.on]="!typeFilter" (click)="typeFilter = null">All</button>
      @for (t of types; track t) {
        <button type="button" class="sf-chip" [class.on]="typeFilter === t" (click)="typeFilter = t" [attr.data-testid]="'chip-' + t">{{ label(t) }}</button>
      }
    </div>

    @if (loading && !templates.length) {
      <div class="sf-card sf-empty" data-testid="list-loading"><i class="pi pi-spin pi-spinner"></i> Loading templates…</div>
    } @else if (error) {
      <div class="sf-card sf-empty dtl-err" data-testid="list-error">
        <i class="pi pi-exclamation-circle"></i> {{ error }}
        <div><p-button label="Try again" icon="pi pi-refresh" [text]="true" (onClick)="load()"></p-button></div>
      </div>
    } @else if (!shown.length) {
      <div class="sf-card sf-empty" data-testid="list-empty">
        <i class="pi pi-file-pdf" style="font-size:1.6rem"></i>
        <p>{{ typeFilter ? 'No template for this document type yet.' : 'No document templates yet.' }}</p>
        <p class="sf-mu sf-sm">Create one, or reset a type to restore the template your setup pack ships with.</p>
        @if (typeFilter) {
          <p-button label="Reset to default" icon="pi pi-replay" [text]="true" (onClick)="confirmReset(typeFilter)"></p-button>
        }
      </div>
    } @else {
      <div class="sf-card p0">
        <p-table [value]="shown" dataKey="uuid" [scrollable]="true" styleClass="p-datatable-sm" data-testid="template-table">
          <ng-template pTemplate="header">
            <tr><th>Type</th><th>Name</th><th>Default</th><th>Source</th><th class="dtl-acts-h">Actions</th></tr>
          </ng-template>
          <ng-template pTemplate="body" let-t>
            <tr [attr.data-testid]="'row-' + t.uuid" [class.dtl-inactive]="t.isActive === false">
              <td>{{ label(t.templateType) }}</td>
              <td><a [routerLink]="[base, t.uuid]" class="dtl-link">{{ t.name }}</a>
                @if (t.isActive === false) { <span class="sf-pill">Inactive</span> }</td>
              <td>@if (t.isDefault) { <span class="sf-pill ok" data-testid="default-pill">Default</span> } @else { <span class="sf-mu">—</span> }</td>
              <td>@if (t.isSeeded) { <span class="sf-pill in">Seeded</span> } @else { <span class="sf-pill">Custom</span> }</td>
              <td class="dtl-acts">
                <p-button icon="pi pi-pencil" [text]="true" [rounded]="true" pTooltip="Edit" ariaLabel="Edit" [routerLink]="[base, t.uuid]"></p-button>
                @if (!t.isDefault && t.isActive !== false) {
                  <p-button icon="pi pi-star" [text]="true" [rounded]="true" pTooltip="Set as default" ariaLabel="Set as default"
                            [disabled]="busy" (onClick)="setDefault(t)" [attr.data-testid]="'set-default-' + t.uuid"></p-button>
                }
                <p-button icon="pi pi-copy" [text]="true" [rounded]="true" pTooltip="Clone" ariaLabel="Clone"
                          [disabled]="busy" (onClick)="clone(t)" [attr.data-testid]="'clone-' + t.uuid"></p-button>
                @if (t.isSeeded) {
                  <p-button icon="pi pi-replay" [text]="true" [rounded]="true" pTooltip="Reset to default" ariaLabel="Reset to default"
                            [disabled]="busy" (onClick)="confirmReset(t.templateType)" [attr.data-testid]="'reset-' + t.uuid"></p-button>
                }
              </td>
            </tr>
          </ng-template>
        </p-table>
      </div>
    }
  </div>
</sf-page>

<p-dialog [(visible)]="newVisible" [modal]="true" header="New document template" styleClass="sdlg"
          [style]="{ width: '440px' }" [breakpoints]="{ '640px': '94vw' }">
  <div class="sf-fields dtl-dlg">
    <div class="sf-fld">
      <label for="nt-type">Document type</label>
      <p-select inputId="nt-type" [options]="typeOptions" [(ngModel)]="newType" optionLabel="label" optionValue="value"
                appendTo="body" styleClass="w-full" data-testid="new-type"></p-select>
    </div>
    <div class="sf-fld">
      <label for="nt-name">Name</label>
      <input pInputText id="nt-name" [(ngModel)]="newName" maxlength="200" class="w-full" data-testid="new-name" />
    </div>
  </div>
  <ng-template pTemplate="footer">
    <p-button label="Cancel" [text]="true" severity="secondary" (onClick)="newVisible = false"></p-button>
    <p-button label="Create" icon="pi pi-check" [loading]="busy" [disabled]="!newType || !newName.trim()" (onClick)="create()" data-testid="new-create"></p-button>
  </ng-template>
</p-dialog>
  `,
  styles: [`
    .dtl-link { color: var(--sms-primary-text); font-weight: 600; text-decoration: none; }
    .dtl-link:hover { text-decoration: underline; }
    .dtl-acts { white-space: nowrap; text-align: right; }
    .dtl-acts-h { text-align: right; }
    .dtl-inactive td { color: var(--sms-text-faint); }
    .dtl-err { color: var(--sms-danger); }
    .dtl-dlg { grid-template-columns: 1fr; }
    .sf-pill { margin-left: .25rem; }
  `]
})
export class DocumentTemplateListComponent implements OnInit {
  readonly base = DOC_TEMPLATES_ROUTE;
  readonly types = [...DOCUMENT_TEMPLATE_TYPES];
  readonly typeOptions = this.types.map(t => ({ label: templateTypeLabel(t), value: t }));

  templates: DocumentTemplateModel[] = [];
  loading = false;
  error: string | null = null;
  busy = false;
  typeFilter: string | null = null;

  newVisible = false;
  newType: string | null = null;
  newName = '';

  constructor(
    private api: OnboardingTemplatesService,
    private router: Router,
    private messages: MessageService,
    private confirm: ConfirmationService
  ) {}

  ngOnInit(): void { this.load(); }

  label(t: string): string { return templateTypeLabel(t); }

  get shown(): DocumentTemplateModel[] {
    return this.typeFilter ? this.templates.filter(t => t.templateType === this.typeFilter) : this.templates;
  }

  load(): void {
    this.loading = true;
    this.error = null;
    this.api.getDocumentTemplates().subscribe({
      next: res => { this.loading = false; this.templates = res?.result ?? []; },
      error: err => { this.loading = false; this.error = err?.error?.message || 'The templates could not be loaded.'; }
    });
  }

  openNew(): void {
    this.newType = this.typeFilter ?? 'SalesInvoice';
    this.newName = '';
    this.newVisible = true;
  }

  create(): void {
    if (!this.newType || !this.newName.trim() || this.busy) return;
    this.busy = true;
    this.api.createDocumentTemplate({ templateType: this.newType, name: this.newName.trim(), showBankDetails: false }).subscribe({
      next: res => {
        this.busy = false;
        this.newVisible = false;
        if (res?.result?.uuid) this.router.navigate([this.base, res.result.uuid]);
        else this.load();
      },
      error: err => this.fail(err, 'The template could not be created.')
    });
  }

  clone(t: DocumentTemplateModel): void {
    if (this.busy) return;
    this.busy = true;
    this.api.createDocumentTemplate({
      templateType: t.templateType, name: `Copy of ${t.name}`.slice(0, 200), cloneFromUuid: t.uuid, showBankDetails: t.showBankDetails
    }).subscribe({
      next: res => {
        this.busy = false;
        this.messages.add({ severity: 'success', summary: 'Cloned', detail: `"${res?.result?.name ?? 'Copy'}" was created.` });
        if (res?.result?.uuid) this.router.navigate([this.base, res.result.uuid]);
        else this.load();
      },
      error: err => this.fail(err, 'The template could not be cloned.')
    });
  }

  setDefault(t: DocumentTemplateModel): void {
    if (this.busy) return;
    this.busy = true;
    this.api.setDefaultDocumentTemplate(t.uuid).subscribe({
      next: () => {
        this.busy = false;
        this.messages.add({ severity: 'success', summary: 'Default set', detail: `"${t.name}" is now used for every ${this.label(t.templateType).toLowerCase()} PDF.` });
        this.load();
      },
      error: err => this.fail(err, 'The default could not be changed.')
    });
  }

  confirmReset(type: string): void {
    this.confirm.confirm({
      header: 'Reset to default?',
      message: `The seeded ${this.label(type).toLowerCase()} template gets its original values back and becomes the default. Your custom templates are kept.`,
      icon: 'pi pi-replay',
      acceptLabel: 'Reset', rejectLabel: 'Cancel',
      accept: () => this.reset(type)
    });
  }

  reset(type: string): void {
    this.busy = true;
    this.api.resetDocumentTemplate(type).subscribe({
      next: () => {
        this.busy = false;
        this.messages.add({ severity: 'success', summary: 'Reset', detail: `The ${this.label(type).toLowerCase()} template is back to its default.` });
        this.load();
      },
      error: err => this.fail(err, 'The template could not be reset.')
    });
  }

  private fail(err: any, fallback: string): void {
    this.busy = false;
    this.messages.add({ severity: 'error', summary: 'Error', detail: err?.error?.message || fallback });
  }
}
