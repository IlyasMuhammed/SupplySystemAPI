import { Component, EventEmitter, Input, OnInit, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';
import { ConfirmationService, MessageService } from 'primeng/api';

import {
  ConnectionStatusModel, QuickBooksIntegrationService, TestConnectionResult, qboErrorMessage
} from '../../../../services/quickbooks-integration.service';
import { CONNECTION_STATUS_LABEL, connectErrorMessage, needsReconnect } from '../quickbooks.shared';

/**
 * Connect, test and disconnect. Also where Intuit's OAuth callback lands: it redirects back to this
 * page with ?result=connected or ?result=error&reason=…, which is turned into a toast and removed
 * from the address.
 */
@Component({
  selector: 'app-qbo-connection-tab',
  standalone: true,
  imports: [CommonModule, ButtonModule, ConfirmDialogModule, TagModule, TooltipModule],
  templateUrl: './connection-tab.component.html',
  styleUrls: ['../quickbooks-tab.scss', './connection-tab.component.scss'],
  providers: [ConfirmationService]
})
export class ConnectionTabComponent implements OnInit {
  @Input() connection: ConnectionStatusModel | null = null;
  @Input() canManage = false;
  @Output() connectionChange = new EventEmitter<ConnectionStatusModel>();

  isConnecting = false;
  isTesting = false;
  isDisconnecting = false;
  lastTest: TestConnectionResult | null = null;

  readonly statusLabel = CONNECTION_STATUS_LABEL;

  constructor(
    private service: QuickBooksIntegrationService,
    private messages: MessageService,
    private confirmation: ConfirmationService,
    private route: ActivatedRoute,
    private router: Router
  ) {}

  ngOnInit(): void {
    this.handleCallbackResult();
  }

  // ── State ───────────────────────────────────────────────────────────────────

  get isConnected(): boolean { return !!this.connection?.isConnected; }
  get appConfigured(): boolean { return this.connection?.appConfigured !== false; }
  get mustReconnect(): boolean { return needsReconnect(this.connection); }
  get isLive(): boolean { return this.connection?.mode === 'Live'; }

  /** Connect is offered when there is no working connection. */
  get showConnect(): boolean { return !this.isConnected && !this.mustReconnect; }

  get canConnect(): boolean { return this.canManage && this.appConfigured && !this.isConnecting; }

  // ── Back from Intuit ────────────────────────────────────────────────────────

  /** Reads ?result / ?reason once, says what happened, and takes them off the address. */
  handleCallbackResult(): void {
    const params = this.route.snapshot.queryParamMap;
    const result = params.get('result');
    if (!result) return;

    if (result === 'connected') {
      this.messages.add({
        severity: 'success', summary: 'Connected to QuickBooks', life: 6000,
        detail: 'The connection is in dry run: nothing is sent until you switch to Live. Next, run the preflight checks.'
      });
    } else {
      this.messages.add({
        severity: 'error', summary: 'Not connected', life: 10000,
        detail: connectErrorMessage(params.get('reason'))
      });
    }

    this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { result: null, reason: null },
      queryParamsHandling: 'merge',
      replaceUrl: true
    });
  }

  // ── Actions ─────────────────────────────────────────────────────────────────

  /** Asks the server for Intuit's consent address and sends the browser there. Also used to reconnect. */
  connect(): void {
    if (!this.canConnect) return;
    this.isConnecting = true;

    this.service.connect().subscribe({
      next: (res) => {
        if (res?.consentUrl) {
          this.redirectTo(res.consentUrl);
          return;   // the page is leaving; keep the button busy
        }
        this.isConnecting = false;
        this.messages.add({ severity: 'error', summary: 'Not connected', detail: 'The server did not return a QuickBooks sign-in address.' });
      },
      error: (err) => {
        this.isConnecting = false;
        this.messages.add({ severity: 'error', summary: 'Not connected', detail: qboErrorMessage(err, 'The connection could not be started.') });
      }
    });
  }

  /** Separate so tests can stop the navigation. */
  redirectTo(url: string): void {
    window.location.href = url;
  }

  test(): void {
    if (this.isTesting) return;
    this.isTesting = true;

    this.service.testConnection().subscribe({
      next: (res) => {
        this.isTesting = false;
        this.lastTest = res;
        this.messages.add(res.ok
          ? { severity: 'success', summary: 'Connection works', detail: res.companyName ? `QuickBooks answered for ${res.companyName}.` : (res.message || 'QuickBooks answered.') }
          : { severity: 'error', summary: 'Connection failed', detail: res.message || 'QuickBooks did not answer.' });
      },
      error: (err) => {
        this.isTesting = false;
        this.lastTest = { ok: false, message: qboErrorMessage(err, 'The test could not be run.') };
        this.messages.add({ severity: 'error', summary: 'Connection failed', detail: this.lastTest.message! });
      }
    });
  }

  confirmDisconnect(): void {
    if (!this.canManage || this.isDisconnecting) return;
    this.confirmation.confirm({
      key: 'qbo-disconnect',
      header: 'Disconnect QuickBooks?',
      icon: 'pi pi-exclamation-triangle',
      message: 'Your QuickBooks data is not changed; links are kept so reconnecting the same company re-links. '
             + 'Nothing is sent to QuickBooks until you connect again.',
      acceptLabel: 'Disconnect',
      rejectLabel: 'Keep connected',
      acceptButtonStyleClass: 'p-button-danger',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => this.disconnect()
    });
  }

  disconnect(): void {
    if (!this.canManage || this.isDisconnecting) return;
    this.isDisconnecting = true;

    this.service.disconnect().subscribe({
      next: (status) => {
        this.isDisconnecting = false;
        this.lastTest = null;
        this.messages.add({ severity: 'success', summary: 'Disconnected', detail: 'QuickBooks is disconnected. Links to its records are kept.' });
        this.connectionChange.emit(status);
      },
      error: (err) => {
        this.isDisconnecting = false;
        this.messages.add({ severity: 'error', summary: 'Not disconnected', detail: qboErrorMessage(err, 'QuickBooks could not be disconnected.') });
      }
    });
  }
}
