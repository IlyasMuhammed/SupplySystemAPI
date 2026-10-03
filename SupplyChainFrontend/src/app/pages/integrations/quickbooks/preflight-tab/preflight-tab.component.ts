import { Component, EventEmitter, Input, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ButtonModule } from 'primeng/button';
import { MessageService } from 'primeng/api';
import { switchMap } from 'rxjs';

import {
  PreflightCheckModel, PreflightResultModel, QuickBooksIntegrationService, qboErrorMessage
} from '../../../../services/quickbooks-integration.service';
import { companyChecksPassed, isSetupProgressCheck } from '../quickbooks.shared';

/** The company checks QuickBooks has to pass before anything is sent (home currency, country, tax…). */
@Component({
  selector: 'app-qbo-preflight-tab',
  standalone: true,
  imports: [CommonModule, ButtonModule],
  templateUrl: './preflight-tab.component.html',
  styleUrls: ['../quickbooks-tab.scss', './preflight-tab.component.scss']
})
export class PreflightTabComponent {
  @Input() preflight: PreflightResultModel | null = null;
  @Input() canManage = false;
  @Input() isConnected = false;
  @Output() preflightChange = new EventEmitter<PreflightResultModel>();

  isRefreshing = false;

  constructor(private service: QuickBooksIntegrationService, private messages: MessageService) {}

  get companyChecks(): PreflightCheckModel[] {
    return (this.preflight?.checks ?? []).filter(c => !isSetupProgressCheck(c));
  }

  get progressChecks(): PreflightCheckModel[] {
    return (this.preflight?.checks ?? []).filter(c => isSetupProgressCheck(c));
  }

  get passed(): boolean { return companyChecksPassed(this.preflight); }

  get failCount(): number { return this.companyChecks.filter(c => c.status === 'Fail').length; }
  get warnCount(): number { return this.companyChecks.filter(c => c.status === 'Warn').length; }

  icon(check: PreflightCheckModel): string {
    switch (check.status) {
      case 'Pass': return 'pi-check-circle';
      case 'Warn': return 'pi-exclamation-triangle';
      default:     return 'pi-times-circle';
    }
  }

  /**
   * "Refresh from QuickBooks": reads the company's settings, accounts, tax codes and terms again,
   * then re-runs the checks. Without the manage permission only the checks are re-run.
   */
  refresh(): void {
    if (this.isRefreshing) return;
    this.isRefreshing = true;

    const run = this.canManage
      ? this.service.refreshReference().pipe(switchMap(() => this.service.getPreflight()))
      : this.service.getPreflight();

    run.subscribe({
      next: (result) => {
        this.isRefreshing = false;
        this.preflight = result;
        this.preflightChange.emit(result);
        this.messages.add({
          severity: companyChecksPassed(result) ? 'success' : 'warn',
          summary: 'Checks updated',
          detail: companyChecksPassed(result) ? 'Every company check passed.' : 'Some checks need attention.'
        });
      },
      error: (err) => {
        this.isRefreshing = false;
        this.messages.add({ severity: 'error', summary: 'Not refreshed', detail: qboErrorMessage(err, 'The checks could not be run.') });
      }
    });
  }
}
