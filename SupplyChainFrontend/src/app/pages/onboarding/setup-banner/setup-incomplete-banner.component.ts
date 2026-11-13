import { Component, OnInit, computed, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { OnboardingSetupService } from '../../../services/onboarding-setup.service';
import { TenantService } from '../../service/tenant.service';
import { SETUP_WIZARD_ROUTE } from '../onboarding.shared';

/**
 * A50 task 318 / D-7 — the dashboard's "Setup incomplete" banner while the organization's setup is NotStarted or
 * InProgress. Who can run the wizard gets a link to it; everyone else is told to ask their administrator. Reads
 * `GET setup/status` (any signed-in user), shared with the first-login guard. Hidden for the platform super admin.
 */
@Component({
  selector: 'app-setup-incomplete-banner',
  standalone: true,
  imports: [CommonModule, RouterModule],
  styleUrls: ['../onboarding.scss'],
  template: `
<div class="ob-note wn" *ngIf="visible()" role="status" data-testid="setup-banner">
  <i class="pi pi-exclamation-circle"></i>
  <div class="grow">
    <strong>Setup incomplete.</strong>
    <ng-container *ngIf="canRun(); else askTpl">
      Your organization's setup {{ status()?.setupStatus === 'InProgress' ? 'is not finished' : 'has not been started' }}.
      <a [routerLink]="route" data-testid="setup-banner-link">{{ status()?.setupStatus === 'InProgress' ? 'Continue setup' : 'Start setup' }}</a>
    </ng-container>
    <ng-template #askTpl><span data-testid="setup-banner-ask">Setup is not finished — ask your administrator.</span></ng-template>
  </div>
</div>`
})
export class SetupIncompleteBannerComponent implements OnInit {
  private readonly api = inject(OnboardingSetupService);
  private readonly tenant = inject(TenantService);

  readonly route = SETUP_WIZARD_ROUTE;
  readonly status = this.api.status;
  readonly canRun = computed(() => !!this.status()?.canRunWizard);
  readonly visible = computed(() => {
    const s = this.status();
    if (!s || this.tenant.isSuperAdmin()) return false;
    return s.setupStatus === 'NotStarted' || s.setupStatus === 'InProgress';
  });

  ngOnInit(): void {
    if (!this.tenant.isSuperAdmin()) this.api.loadStatus().subscribe();
  }
}
