import { Component, EventEmitter, Input, OnInit, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { SelectButtonModule } from 'primeng/selectbutton';
import { TagModule } from 'primeng/tag';
import { MessageService } from 'primeng/api';

import {
  QboEnvironment, QuickBooksIntegrationService, TenantAppKeysModel, qboErrorMessage
} from '../../../../services/quickbooks-integration.service';

/**
 * docs/quickbooks/APP-CREDENTIALS.md — "Use your own Intuit app". Shown only when the platform administrator has switched
 * on FEATURE_QBO_OWN_APP for the organization (or keys are still saved from before). The client secret is write-only:
 * it is never shown, and a blank secret on save keeps the stored one.
 */
@Component({
  selector: 'app-qbo-own-app-keys-card',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, InputTextModule, SelectButtonModule, TagModule],
  template: `
<section class="form-card" *ngIf="model && (model.featureEnabled || model.hasOwnKeys)" data-testid="own-app-card">
  <div class="card-head">
    <div class="card-icon"><i class="pi pi-key"></i></div>
    <div class="grow">
      <div class="card-title">Your own Intuit app</div>
      <div class="card-sub">
        Connect through your organization's Intuit developer app instead of the platform's. Leave this empty to use the platform's app.
      </div>
    </div>
    <p-tag *ngIf="model.newConnectionsUse === 'Tenant'" value="In use for new connections" severity="success" data-testid="own-app-in-use"></p-tag>
    <p-tag *ngIf="model.hasOwnKeys && !model.featureEnabled" value="Switched off by the administrator" severity="warn"></p-tag>
  </div>

  <div class="note info" *ngIf="model.redirectUriToRegister">
    <i class="pi pi-info-circle"></i>
    <div class="note-body">
      <div class="note-title">Register this redirect URI on your Intuit app</div>
      <div class="mono redirect" data-testid="redirect-uri">{{ model.redirectUriToRegister }}</div>
    </div>
  </div>

  <div class="note warn" *ngIf="model.connectedWithOwnKeys" data-testid="own-app-locked">
    <i class="pi pi-lock"></i>
    <div class="note-body">
      QuickBooks is connected through these keys. Disconnect first to change the Client ID or environment, or to remove them.
      You can replace the client secret while connected.
    </div>
  </div>

  <div class="field-grid">
    <div class="field">
      <label for="own-client-id">Client ID <span class="required">*</span></label>
      <input id="own-client-id" pInputText [(ngModel)]="clientId" [disabled]="!editable || model.connectedWithOwnKeys"
             autocomplete="off" spellcheck="false" maxlength="200" data-testid="own-client-id" />
    </div>
    <div class="field">
      <label for="own-client-secret">Client secret <span class="required" *ngIf="!model.hasSecret">*</span></label>
      <input id="own-client-secret" pInputText type="password" [(ngModel)]="clientSecret" [disabled]="!editable"
             autocomplete="new-password" maxlength="500"
             [placeholder]="model.hasSecret ? 'Stored — leave blank to keep it' : ''" data-testid="own-client-secret" />
    </div>
    <div class="field">
      <label>Environment</label>
      <p-selectButton [options]="environments" [(ngModel)]="environment" optionLabel="label" optionValue="value"
                      [disabled]="!editable || model.connectedWithOwnKeys" data-testid="own-environment"></p-selectButton>
      <span class="hint">Sandbox keys connect only to sandbox companies; Production keys need Intuit's app review.</span>
    </div>
  </div>

  <div class="action-bar" *ngIf="editable || (canManage && model.hasOwnKeys)">
    <p-button *ngIf="editable" label="Save keys" icon="pi pi-save" [disabled]="!canSave" [loading]="saving"
              (onClick)="save()" data-testid="own-app-save"></p-button>
    <span class="grow"></span>
    <p-button *ngIf="canManage && model.hasOwnKeys" label="Remove my keys" icon="pi pi-trash" severity="danger" [outlined]="true"
              [disabled]="model.connectedWithOwnKeys || removing" [loading]="removing" (onClick)="remove()" data-testid="own-app-remove"></p-button>
  </div>
  <div class="hint" *ngIf="!canManage">Only someone with the Manage integrations permission can change these keys.</div>
</section>`,
  styleUrls: ['../quickbooks-tab.scss'],
  styles: [`.redirect { word-break: break-all; margin-top: .25rem; }`]
})
export class OwnAppKeysCardComponent implements OnInit {
  @Input() canManage = false;
  /** Fired after a save or removal — which keys the next Connect uses may have changed. */
  @Output() keysChanged = new EventEmitter<TenantAppKeysModel>();

  model: TenantAppKeysModel | null = null;
  clientId = '';
  clientSecret = '';
  environment: QboEnvironment = 'Sandbox';
  saving = false;
  removing = false;

  readonly environments = [
    { label: 'Sandbox', value: 'Sandbox' as QboEnvironment },
    { label: 'Production', value: 'Production' as QboEnvironment }
  ];

  constructor(private service: QuickBooksIntegrationService, private messages: MessageService) {}

  ngOnInit(): void { this.load(); }

  /** Saving needs the feature on and the permission. */
  get editable(): boolean { return this.canManage && !!this.model?.featureEnabled; }

  get canSave(): boolean {
    if (!this.editable || this.saving || !this.model) return false;
    const id = this.clientId.trim();
    if (!id || /\s/.test(id)) return false;
    return this.model.hasSecret || this.clientSecret.trim().length > 0;
  }

  load(): void {
    this.service.getOwnAppKeys().subscribe({
      next: (m) => this.apply(m),
      // Not fatal for the page: the card simply stays hidden.
      error: () => { this.model = null; }
    });
  }

  save(): void {
    if (!this.canSave) return;
    this.saving = true;
    this.service.saveOwnAppKeys({
      clientId: this.clientId.trim(),
      clientSecret: this.clientSecret.trim() || null,
      environment: this.environment
    }).subscribe({
      next: (m) => {
        this.saving = false;
        this.apply(m);
        this.messages.add({ severity: 'success', summary: 'Keys saved', detail: 'They are used the next time QuickBooks is connected.' });
        this.keysChanged.emit(m);
      },
      error: (err) => {
        this.saving = false;
        this.messages.add({ severity: 'error', summary: 'Not saved', detail: qboErrorMessage(err, 'The keys could not be saved.') });
      }
    });
  }

  remove(): void {
    if (!this.canManage || this.removing || !this.model?.hasOwnKeys || this.model.connectedWithOwnKeys) return;
    this.removing = true;
    this.service.deleteOwnAppKeys().subscribe({
      next: (m) => {
        this.removing = false;
        this.apply(m);
        this.messages.add({ severity: 'success', summary: 'Keys removed', detail: "The platform's app is used for new connections." });
        this.keysChanged.emit(m);
      },
      error: (err) => {
        this.removing = false;
        this.messages.add({ severity: 'error', summary: 'Not removed', detail: qboErrorMessage(err, 'The keys could not be removed.') });
      }
    });
  }

  private apply(m: TenantAppKeysModel): void {
    this.model = m;
    this.clientId = m.clientId ?? '';
    this.clientSecret = '';
    this.environment = m.environment ?? 'Sandbox';
  }
}
