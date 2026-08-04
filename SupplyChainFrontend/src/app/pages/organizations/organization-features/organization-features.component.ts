import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { CheckboxModule } from 'primeng/checkbox';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService, ConfirmationService } from 'primeng/api';
import { OrganizationsService, OrganizationDetailModel, OrganizationFeatureModel } from '../../../services/organizations.service';

@Component({
  selector: 'app-organization-features',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, CheckboxModule, TagModule, ToastModule, ConfirmDialogModule, TooltipModule
  ],
  templateUrl: './organization-features.component.html',
  styleUrls: ['./organization-features.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class OrganizationFeaturesComponent implements OnInit {
  orgId = '';
  org: OrganizationDetailModel | null = null;
  features: OrganizationFeatureModel[] = [];
  isLoading = true;
  savingCode: string | null = null;
  isApplyingTemplate = false;

  constructor(
    private route: ActivatedRoute,
    private orgsService: OrganizationsService,
    private messageService: MessageService,
    private confirmationService: ConfirmationService
  ) {}

  ngOnInit() {
    this.orgId = this.route.snapshot.paramMap.get('id') ?? '';
    if (this.orgId) this.load();
  }

  load() {
    this.isLoading = true;
    this.orgsService.getById(this.orgId).subscribe({
      next: (res) => { this.org = res.result ?? null; },
      error: () => this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load organization' })
    });
    this.orgsService.getFeatures(this.orgId).subscribe({
      next: (res) => {
        this.isLoading = false;
        this.features = res.result ?? [];
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load features' });
      }
    });
  }

  get groupedFeatures(): { category: string; items: OrganizationFeatureModel[] }[] {
    const order = ['MODULE', 'SCREEN', 'FEATURE'];
    const groups = order.map(category => ({
      category,
      items: this.features.filter(f => f.category === category)
    }));
    return groups.filter(g => g.items.length > 0);
  }

  categoryLabel(category: string): string {
    switch (category) {
      case 'MODULE':  return 'Modules';
      case 'SCREEN':  return 'Screens';
      case 'FEATURE': return 'Capabilities';
      default:        return category;
    }
  }

  onToggle(feature: OrganizationFeatureModel, checked: boolean) {
    if (feature.isCore && !checked) {
      // Revert the checkbox — core features can never be disabled. Backend also rejects this,
      // but blocking client-side avoids a round trip and an error toast for an action that can
      // never succeed.
      feature.isEnabled = true;
      this.messageService.add({
        severity: 'warn', summary: 'Core Feature',
        detail: `"${feature.featureName}" is a core feature and cannot be disabled.`
      });
      return;
    }

    this.savingCode = feature.featureCode;
    this.orgsService.updateFeatures(this.orgId, [{ featureCode: feature.featureCode, isEnabled: checked }]).subscribe({
      next: (res) => {
        this.savingCode = null;
        const result = res.result;
        if (!res.success || !result) {
          feature.isEnabled = !checked;
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Failed to update feature' });
          return;
        }
        if (result.autoEnabledDependencies?.length) {
          for (const code of result.autoEnabledDependencies) {
            const dep = this.features.find(f => f.featureCode === code);
            if (dep) dep.isEnabled = true;
          }
          this.messageService.add({
            severity: 'info', summary: 'Dependency Enabled',
            detail: `${result.autoEnabledDependencies.join(', ')} was also enabled — required by "${feature.featureName}".`
          });
        } else {
          this.messageService.add({ severity: 'success', summary: 'Updated', detail: `"${feature.featureName}" ${checked ? 'enabled' : 'disabled'}` });
        }
      },
      error: (err) => {
        this.savingCode = null;
        feature.isEnabled = !checked;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to update feature' });
      }
    });
  }

  confirmApplyPlanTemplate() {
    this.confirmationService.confirm({
      message: `Reset all feature toggles to the defaults for the <strong>${this.org?.plan}</strong> plan? Any manual overrides will be lost.`,
      header: 'Reset to Plan Defaults',
      icon: 'pi pi-exclamation-triangle',
      acceptLabel: 'Reset',
      acceptButtonStyleClass: 'p-button-danger',
      rejectLabel: 'Cancel',
      accept: () => {
        this.isApplyingTemplate = true;
        this.orgsService.applyPlanTemplate(this.orgId).subscribe({
          next: () => {
            this.isApplyingTemplate = false;
            this.messageService.add({ severity: 'success', summary: 'Reset', detail: 'Feature toggles reset to plan defaults.' });
            this.load();
          },
          error: (err) => {
            this.isApplyingTemplate = false;
            this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Reset failed' });
          }
        });
      }
    });
  }

  planSeverity(plan?: string): 'success' | 'info' | 'warn' {
    switch (plan) {
      case 'ENTERPRISE': return 'success';
      case 'STANDARD':   return 'info';
      default:           return 'warn';
    }
  }
}
