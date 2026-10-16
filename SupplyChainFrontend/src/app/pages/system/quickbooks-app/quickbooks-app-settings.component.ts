import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { InputTextModule } from 'primeng/inputtext';
import { SelectButtonModule } from 'primeng/selectbutton';
import { ToastModule } from 'primeng/toast';
import { ConfirmationService, MessageService } from 'primeng/api';
import { HttpErrorResponse } from '@angular/common/http';

import { FLOW } from '../../../shared/flow';
import {
  PlatformAppKeysModel, QboEnvironment, QboApiError, QuickBooksIntegrationService, qboErrorMessage
} from '../../../services/quickbooks-integration.service';

/**
 * System Administration › QuickBooks App (docs/quickbooks/APP-CREDENTIALS.md D-7) — the platform's Intuit app keys,
 * used by every organization that does not have its own. Super admin only. The client secret is write-only (D-4):
 * it is never shown, and a blank secret on save keeps the stored one.
 */
@Component({
  selector: 'app-quickbooks-app-settings',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, ConfirmDialogModule, InputTextModule, SelectButtonModule, ToastModule, ...FLOW],
  providers: [MessageService, ConfirmationService],
  template: `
<p-toast></p-toast>
<p-confirmDialog key="qbo-app" [style]="{ width: '32rem', maxWidth: '96vw' }"></p-confirmDialog>
<sf-page [crumbs]="['System Administration', 'QuickBooks App']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button label="Save" icon="pi pi-save" [disabled]="!canSave" [loading]="saving" (onClick)="save(false)" data-testid="qbo-app-save"></p-button>
    <span class="sf-end">
      <p-button *ngIf="model?.savedInDatabase" label="Remove saved keys" icon="pi pi-trash" severity="danger" [outlined]="true"
                [loading]="removing" (onClick)="confirmRemove()" data-testid="qbo-app-remove"></p-button>
    </span>
  </div>

  <div class="sf-body">
    <div class="sf-lr-head">
      <div>
        <h1>QuickBooks App</h1>
        <div class="sf-sub">The Intuit developer app every organization connects QuickBooks through, unless it uses its own</div>
      </div>
    </div>

    <div class="sf-card qa-pad" *ngIf="loadError" data-testid="qbo-app-error">
      <span class="sf-pill er">{{ loadError }}</span>
      <p-button label="Try again" icon="pi pi-refresh" [text]="true" (onClick)="load()"></p-button>
    </div>

    <ng-container *ngIf="model">
      <div class="qa-status" data-testid="qbo-app-status">
        <span class="sf-pill" [ngClass]="model.isConfigured ? 'ok' : 'er'">
          {{ model.isConfigured ? (model.savedInDatabase ? 'Keys saved here' : 'Using the server configuration') : 'No keys — organizations cannot connect' }}
        </span>
        <span class="sf-mu">{{ model.connectionsUsingPlatformKeys }} connected organization(s) use these keys ·
          {{ model.organizationsWithOwnKeys }} organization(s) have their own app</span>
      </div>

      <div class="sf-card qa-pad">
        <div class="sf-fields">
          <div class="sf-fld span-2">
            <label class="sf-req" for="qa-client-id">Client ID</label>
            <input pInputText id="qa-client-id" [(ngModel)]="clientId" maxlength="200" autocomplete="off" spellcheck="false" data-testid="qbo-app-client-id" />
          </div>
          <div class="sf-fld">
            <label for="qa-env">Environment</label>
            <p-selectButton inputId="qa-env" [options]="environments" [(ngModel)]="environment" optionLabel="label" optionValue="value"
                            [allowEmpty]="false" data-testid="qbo-app-environment"></p-selectButton>
          </div>
          <div class="sf-fld span-3">
            <label [class.sf-req]="!model.savedInDatabase" for="qa-secret">Client secret</label>
            <input pInputText id="qa-secret" type="password" [(ngModel)]="clientSecret" maxlength="500" autocomplete="new-password"
                   [placeholder]="model.savedInDatabase && model.hasSecret ? 'Stored — leave blank to keep it' : ''" data-testid="qbo-app-secret" />
            <span class="sf-mu">Never shown again after saving.</span>
          </div>
          <div class="sf-fld span-3">
            <label class="sf-req" for="qa-redirect">Callback (redirect) URL</label>
            <input pInputText id="qa-redirect" [(ngModel)]="redirectUri" maxlength="500" placeholder="https://your-api/api/integrations/quickbooks/callback"
                   data-testid="qbo-app-redirect" />
            <span class="sf-mu">Register exactly this address on the Intuit app — and on every organization's own app.</span>
          </div>
          <div class="sf-fld span-3">
            <label for="qa-return">Return page URL</label>
            <input pInputText id="qa-return" [(ngModel)]="frontendReturnUrl" maxlength="500"
                   placeholder="https://your-app/portal/pages/integrations/quickbooks" data-testid="qbo-app-return" />
            <span class="sf-mu">Where the browser goes back to after signing in to Intuit.</span>
          </div>
        </div>
        <div class="sf-mu qa-foot" *ngIf="model.configurationAvailable">
          When nothing is saved here, the server configuration is used (Client ID {{ model.configurationClientId }}).
        </div>
      </div>
    </ng-container>
  </div>
</sf-page>`,
  styles: [`
    .qa-pad { padding: 16px 18px; }
    .qa-status { display: flex; gap: .75rem; align-items: center; flex-wrap: wrap; margin-bottom: .75rem; }
    .qa-foot { margin-top: .75rem; }
  `]
})
export class QuickBooksAppSettingsComponent implements OnInit {
  model: PlatformAppKeysModel | null = null;
  loadError = '';
  clientId = '';
  clientSecret = '';
  environment: QboEnvironment = 'Sandbox';
  redirectUri = '';
  frontendReturnUrl = '';
  saving = false;
  removing = false;

  readonly environments = [
    { label: 'Sandbox', value: 'Sandbox' as QboEnvironment },
    { label: 'Production', value: 'Production' as QboEnvironment }
  ];

  constructor(
    private service: QuickBooksIntegrationService,
    private messages: MessageService,
    private confirmation: ConfirmationService
  ) {}

  ngOnInit(): void { this.load(); }

  get canSave(): boolean {
    if (!this.model || this.saving) return false;
    const id = this.clientId.trim();
    if (!id || /\s/.test(id) || !this.redirectUri.trim()) return false;
    return (this.model.savedInDatabase && this.model.hasSecret) || this.clientSecret.trim().length > 0;
  }

  load(): void {
    this.loadError = '';
    this.service.getPlatformAppKeys().subscribe({
      next: (m) => this.apply(m),
      error: (err) => { this.loadError = qboErrorMessage(err, 'The QuickBooks app keys could not be read.'); }
    });
  }

  /** A 409 means live connections would have to reconnect: say so and let the administrator confirm. */
  save(confirmBreakingChange: boolean): void {
    if (!this.canSave) return;
    this.saving = true;
    this.service.savePlatformAppKeys({
      clientId: this.clientId.trim(),
      clientSecret: this.clientSecret.trim() || null,
      environment: this.environment,
      redirectUri: this.redirectUri.trim(),
      frontendReturnUrl: this.frontendReturnUrl.trim() || null,
      confirmBreakingChange
    }).subscribe({
      next: (m) => {
        this.saving = false;
        this.apply(m);
        this.messages.add({ severity: 'success', summary: 'Saved', detail: 'QuickBooks app keys saved.' });
      },
      error: (err) => {
        this.saving = false;
        if (isConflict(err) && !confirmBreakingChange) {
          this.confirmation.confirm({
            key: 'qbo-app',
            header: 'Organizations will have to reconnect',
            icon: 'pi pi-exclamation-triangle',
            message: qboErrorMessage(err, 'Connected organizations use the current keys.'),
            acceptLabel: 'Save anyway',
            rejectLabel: 'Cancel',
            acceptButtonStyleClass: 'p-button-danger',
            rejectButtonStyleClass: 'p-button-text',
            accept: () => this.save(true)
          });
          return;
        }
        this.messages.add({ severity: 'error', summary: 'Not saved', detail: qboErrorMessage(err, 'The keys could not be saved.') });
      }
    });
  }

  confirmRemove(): void {
    this.confirmation.confirm({
      key: 'qbo-app',
      header: 'Remove the saved keys?',
      icon: 'pi pi-exclamation-triangle',
      message: this.model?.configurationAvailable
        ? 'The server configuration is used again.'
        : 'No keys will be left: organizations without their own app cannot connect QuickBooks.',
      acceptLabel: 'Remove',
      rejectLabel: 'Keep',
      acceptButtonStyleClass: 'p-button-danger',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => this.remove(false)
    });
  }

  remove(confirmBreakingChange: boolean): void {
    this.removing = true;
    this.service.deletePlatformAppKeys(confirmBreakingChange).subscribe({
      next: (m) => {
        this.removing = false;
        this.apply(m);
        this.messages.add({ severity: 'success', summary: 'Removed', detail: 'The saved QuickBooks app keys were removed.' });
      },
      error: (err) => {
        this.removing = false;
        if (isConflict(err) && !confirmBreakingChange) {
          this.confirmation.confirm({
            key: 'qbo-app',
            header: 'Organizations will have to reconnect',
            icon: 'pi pi-exclamation-triangle',
            message: qboErrorMessage(err, 'Connected organizations use these keys.'),
            acceptLabel: 'Remove anyway',
            rejectLabel: 'Cancel',
            acceptButtonStyleClass: 'p-button-danger',
            rejectButtonStyleClass: 'p-button-text',
            accept: () => this.remove(true)
          });
          return;
        }
        this.messages.add({ severity: 'error', summary: 'Not removed', detail: qboErrorMessage(err, 'The keys could not be removed.') });
      }
    });
  }

  private apply(m: PlatformAppKeysModel): void {
    this.model = m;
    this.clientId = m.clientId ?? '';
    this.clientSecret = '';
    this.environment = m.environment ?? 'Sandbox';
    this.redirectUri = m.redirectUri ?? '';
    this.frontendReturnUrl = m.frontendReturnUrl ?? '';
  }
}

function isConflict(err: unknown): boolean {
  return !(err instanceof QboApiError) && (err as Partial<HttpErrorResponse> | null)?.status === 409;
}
