import { Component, Input, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { CheckboxModule } from 'primeng/checkbox';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { DatePickerModule } from 'primeng/datepicker';
import { DialogModule } from 'primeng/dialog';
import { InputTextModule } from 'primeng/inputtext';
import { TableModule } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';
import { ConfirmationService, MessageService } from 'primeng/api';

import {
  API_SCOPES, ApiClientKeyModel, ApiClientModel, ApiScope, IssuedApiKeyModel, QuickBooksIntegrationService, qboErrorMessage
} from '../../../../services/quickbooks-integration.service';
import { TenantService } from '../../../service/tenant.service';
import { environment } from '../../../../../environments/environment';

export const SCOPE_DESCRIPTION: Record<ApiScope, string> = {
  'customers:write': 'Send customers',
  'vendors:write': 'Send vendors',
  'items:write': 'Send items',
  'invoices:write': 'Send and void sales invoices',
  'bills:write': 'Send bills',
  'status:read': 'Read the sync status of what it sent'
};

export const MAX_CLIENT_NAME = 100;

export type KeyStatus = 'Active' | 'Revoked' | 'Expired' | 'Inactive';

export function keyStatus(key: ApiClientKeyModel, now = Date.now()): KeyStatus {
  if (key.revokedAt) return 'Revoked';
  if (key.expiresAt && new Date(key.expiresAt).getTime() <= now) return 'Expired';
  return key.isActive ? 'Active' : 'Inactive';
}

/**
 * Keys for other systems (a POS, a web shop) that push records to QuickBooks through the gateway's
 * data endpoints. A key is shown exactly once, when it is issued.
 */
@Component({
  selector: 'app-qbo-api-clients-tab',
  standalone: true,
  imports: [
    CommonModule, FormsModule, ButtonModule, CheckboxModule, ConfirmDialogModule, DatePickerModule, DialogModule,
    InputTextModule, TableModule, TagModule, TooltipModule
  ],
  templateUrl: './api-clients-tab.component.html',
  styleUrls: ['../quickbooks-tab.scss', './api-clients-tab.component.scss'],
  providers: [ConfirmationService]
})
export class ApiClientsTabComponent implements OnInit {
  @Input() canManage = false;

  readonly scopes = API_SCOPES;
  readonly scopeDescription = SCOPE_DESCRIPTION;
  readonly maxName = MAX_CLIENT_NAME;
  readonly gatewayBaseUrl = `${environment.apiOrigin}/api/gateway/quickbooks/v1`;
  readonly keyStatus = keyStatus;

  clients: ApiClientModel[] = [];
  isLoading = false;
  loadFailed = false;
  busy: Record<string, boolean> = {};

  // Create
  createVisible = false;
  newName = '';
  newScopes: string[] = [];
  isCreating = false;

  // Issue
  issueVisible = false;
  issueFor: ApiClientModel | null = null;
  issueExpiresAt: Date | null = null;
  isIssuing = false;
  readonly minExpiry = new Date(Date.now() + 24 * 3600 * 1000);

  // The key, shown once
  keyVisible = false;
  issuedKey: IssuedApiKeyModel | null = null;
  issuedFor = '';
  copied: Record<string, boolean> = {};

  constructor(
    private service: QuickBooksIntegrationService,
    private messages: MessageService,
    private confirmation: ConfirmationService,
    private tenantService: TenantService
  ) {}

  ngOnInit(): void {
    if (this.canManage) this.load();
  }

  /** The value other systems send as X-Tenant-Id: the one the server returned with a key, else this organization's id. */
  get tenantId(): string {
    return this.issuedKey?.tenantId || this.tenantService.tenant()?.id || '<your organization id>';
  }

  load(): void {
    this.isLoading = true;
    this.loadFailed = false;
    this.service.getApiClients().subscribe({
      next: (list) => {
        this.isLoading = false;
        this.clients = list ?? [];
      },
      error: () => {
        this.isLoading = false;
        this.loadFailed = true;
      }
    });
  }

  private replace(updated: ApiClientModel): void {
    this.clients = this.clients.some(c => c.id === updated.id)
      ? this.clients.map(c => (c.id === updated.id ? updated : c))
      : [...this.clients, updated];
  }

  // ── Create ──────────────────────────────────────────────────────────────────

  openCreate(): void {
    if (!this.canManage) return;
    this.newName = '';
    this.newScopes = [];
    this.createVisible = true;
  }

  get createProblem(): string | null {
    const name = this.newName.trim();
    if (!name) return 'Give the client a name.';
    if (name.length > MAX_CLIENT_NAME) return `Keep the name within ${MAX_CLIENT_NAME} characters.`;
    if (this.clients.some(c => c.name.trim().toLowerCase() === name.toLowerCase())) return 'A client with this name already exists.';
    if (!this.newScopes.length) return 'Choose at least one thing it may do.';
    return null;
  }

  create(): void {
    if (!this.canManage || this.isCreating || this.createProblem) return;
    this.isCreating = true;
    const scopes = API_SCOPES.filter(s => this.newScopes.includes(s));

    this.service.createApiClient({ name: this.newName.trim(), scopes }).subscribe({
      next: (client) => {
        this.isCreating = false;
        this.createVisible = false;
        this.replace(client);
        this.messages.add({ severity: 'success', summary: 'Client created', detail: `${client.name} is created. Issue it a key next.` });
      },
      error: (err) => {
        this.isCreating = false;
        this.messages.add({ severity: 'error', summary: 'Not created', detail: qboErrorMessage(err, 'The client could not be created.') });
      }
    });
  }

  // ── Keys ────────────────────────────────────────────────────────────────────

  openIssue(client: ApiClientModel): void {
    if (!this.canManage || !client.isActive) return;
    this.issueFor = client;
    this.issueExpiresAt = null;
    this.issueVisible = true;
  }

  issue(): void {
    const client = this.issueFor;
    if (!client || !this.canManage || this.isIssuing) return;
    this.isIssuing = true;

    this.service.issueApiKey(client.id, { expiresAt: this.issueExpiresAt ? this.issueExpiresAt.toISOString() : null }).subscribe({
      next: (key) => {
        this.isIssuing = false;
        this.issueVisible = false;
        this.issuedKey = key;
        this.issuedFor = client.name;
        this.copied = {};
        this.keyVisible = true;
        this.load();
      },
      error: (err) => {
        this.isIssuing = false;
        this.messages.add({ severity: 'error', summary: 'No key issued', detail: qboErrorMessage(err, 'The key could not be issued.') });
      }
    });
  }

  /** The key leaves memory the moment its dialog closes, however it is closed. */
  closeKey(): void {
    this.keyVisible = false;
    this.issuedKey = null;
    this.issuedFor = '';
    this.copied = {};
  }

  onKeyDialogVisibleChange(visible: boolean): void {
    if (!visible) this.closeKey();
  }

  copy(what: 'key' | 'tenant' | 'headers', text: string): void {
    const done = () => {
      this.copied = { ...this.copied, [what]: true };
      this.messages.add({ severity: 'success', summary: 'Copied', detail: 'Copied to the clipboard.', life: 2000 });
    };
    const failed = () => this.messages.add({ severity: 'warn', summary: 'Not copied', detail: 'Select the text and copy it yourself.' });

    try {
      if (navigator?.clipboard?.writeText) {
        navigator.clipboard.writeText(text).then(done, failed);
      } else {
        failed();
      }
    } catch {
      failed();
    }
  }

  exampleHeaders(apiKey: string | null): string {
    return `X-Tenant-Id: ${this.tenantId}\nX-Api-Key: ${apiKey || '<the key>'}`;
  }

  confirmRevoke(client: ApiClientModel, key: ApiClientKeyModel): void {
    if (!this.canManage) return;
    this.confirmation.confirm({
      key: 'qbo-api-clients',
      header: 'Revoke this key?',
      icon: 'pi pi-exclamation-triangle',
      message: `Key ${key.keyPrefix}… stops working at once. Anything still using it is refused.`,
      acceptLabel: 'Revoke',
      rejectLabel: 'Keep it',
      acceptButtonStyleClass: 'p-button-danger',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => this.revoke(client, key)
    });
  }

  revoke(client: ApiClientModel, key: ApiClientKeyModel): void {
    if (this.busy[key.id]) return;
    this.busy = { ...this.busy, [key.id]: true };
    this.service.revokeApiKey(client.id, key.id).subscribe({
      next: (updated) => {
        this.busy = { ...this.busy, [key.id]: false };
        this.replace(updated);
        this.messages.add({ severity: 'success', summary: 'Key revoked', detail: `Key ${key.keyPrefix}… no longer works.` });
      },
      error: (err) => {
        this.busy = { ...this.busy, [key.id]: false };
        this.messages.add({ severity: 'error', summary: 'Not revoked', detail: qboErrorMessage(err, 'The key could not be revoked.') });
      }
    });
  }

  confirmDeactivate(client: ApiClientModel): void {
    if (!this.canManage || !client.isActive) return;
    this.confirmation.confirm({
      key: 'qbo-api-clients',
      header: `Deactivate ${client.name}?`,
      icon: 'pi pi-exclamation-triangle',
      message: 'All of its keys stop working. What it already sent to QuickBooks stays there.',
      acceptLabel: 'Deactivate',
      rejectLabel: 'Keep it',
      acceptButtonStyleClass: 'p-button-danger',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => this.deactivate(client)
    });
  }

  deactivate(client: ApiClientModel): void {
    if (this.busy[client.id]) return;
    this.busy = { ...this.busy, [client.id]: true };
    this.service.deactivateApiClient(client.id).subscribe({
      next: (updated) => {
        this.busy = { ...this.busy, [client.id]: false };
        this.replace(updated);
        this.messages.add({ severity: 'success', summary: 'Deactivated', detail: `${client.name} can no longer send anything.` });
      },
      error: (err) => {
        this.busy = { ...this.busy, [client.id]: false };
        this.messages.add({ severity: 'error', summary: 'Not deactivated', detail: qboErrorMessage(err, 'The client could not be deactivated.') });
      }
    });
  }

  scopeText(scope: string): string {
    return SCOPE_DESCRIPTION[scope as ApiScope] ?? scope;
  }

  keySeverity(status: KeyStatus): 'success' | 'danger' | 'warn' | 'secondary' {
    switch (status) {
      case 'Active': return 'success';
      case 'Revoked': return 'danger';
      case 'Expired': return 'warn';
      default: return 'secondary';
    }
  }
}
