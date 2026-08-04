import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router } from '@angular/router';
import { FormsModule, ReactiveFormsModule, FormBuilder, FormGroup, Validators } from '@angular/forms';
import { TableModule } from 'primeng/table';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { InputIconModule } from 'primeng/inputicon';
import { IconFieldModule } from 'primeng/iconfield';
import { DropdownModule } from 'primeng/dropdown';
import { DialogModule } from 'primeng/dialog';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService, ConfirmationService } from 'primeng/api';
import {
  OrganizationsService, OrganizationListItemModel, OrgPlan, OrgUserSummary, ORG_ADMIN_ROLE_ID
} from '../../../services/organizations.service';
import { CountriesService } from '../../../services/countries.service';

@Component({
  selector: 'app-organizations-list',
  standalone: true,
  imports: [
    CommonModule, FormsModule, ReactiveFormsModule,
    TableModule, ButtonModule, InputTextModule, InputIconModule, IconFieldModule, DropdownModule,
    DialogModule, TagModule, ToastModule, ConfirmDialogModule, TooltipModule
  ],
  templateUrl: './organizations-list.component.html',
  styleUrls: ['./organizations-list.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class OrganizationsListComponent implements OnInit {
  orgs: OrganizationListItemModel[] = [];
  isLoading = true;
  showDialog = false;
  isEditing = false;
  isSaving = false;
  editId: string | null = null;
  form!: FormGroup;

  // ── Organization admin (view/change) ─────────────────────────────────────
  showAdminDialog = false;
  isLoadingAdmin = false;
  isSavingAdmin = false;
  adminOrg: OrganizationListItemModel | null = null;
  orgUsers: OrgUserSummary[] = [];
  currentAdmin: OrgUserSummary | null = null;
  selectedNewAdminId: number | null = null;

  planOptions: { label: string; value: OrgPlan }[] = [
    { label: 'Basic',      value: 'BASIC' },
    { label: 'Standard',   value: 'STANDARD' },
    { label: 'Enterprise', value: 'ENTERPRISE' }
  ];

  // Sourced from the same managed Countries list every other address form in the app uses
  // (Suppliers, Warehouses) — value is the country name itself, matching this form's existing
  // plain-string `country` field, so no id/name translation is needed on save.
  countryOptions: { label: string; value: string }[] = [];

  constructor(
    private fb: FormBuilder,
    private service: OrganizationsService,
    private countriesService: CountriesService,
    private messageService: MessageService,
    private confirmationService: ConfirmationService,
    private router: Router
  ) {}

  ngOnInit() {
    this.form = this.fb.group({
      orgCode:        ['', [Validators.required, Validators.maxLength(30)]],
      orgName:        ['', [Validators.required, Validators.maxLength(200)]],
      plan:           ['BASIC', Validators.required],
      contactEmail:   ['', [Validators.maxLength(150)]],
      contactPhone:   ['', [Validators.maxLength(30)]],
      address:        ['', [Validators.maxLength(500)]],
      country:        ['', [Validators.maxLength(100)]],
      timeZone:       ['', [Validators.maxLength(50)]],
      // Initial Admin — only collected at creation; the org+admin are created atomically and
      // the admin gets an email invitation to set their own password.
      adminFirstName: ['', [Validators.required, Validators.maxLength(100)]],
      adminLastName:  ['', [Validators.maxLength(100)]],
      adminEmail:     ['', [Validators.required, Validators.email, Validators.maxLength(150)]]
    });
    this.loadCountries();
    this.load();
  }

  private loadCountries() {
    this.countriesService.getAllCountries().subscribe({
      next: (res) => {
        this.countryOptions = (res.result ?? [])
          .filter(c => c.isActive)
          .map(c => ({ label: c.name, value: c.name }));
      },
      error: () => {}
    });
  }

  load() {
    this.isLoading = true;
    this.service.getList({ page: 1, pageSize: 100 }).subscribe({
      next: (res) => {
        this.isLoading = false;
        this.orgs = res.result?.data ?? [];
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load organizations' });
      }
    });
  }

  openNew() {
    this.isEditing = false;
    this.editId = null;
    this.form.reset({ plan: 'BASIC' });
    this.form.get('orgCode')!.enable();
    this.form.get('plan')!.enable();
    this.form.get('adminFirstName')!.enable();
    this.form.get('adminEmail')!.enable();
    this.showDialog = true;
  }

  openEdit(org: OrganizationListItemModel) {
    this.isEditing = true;
    this.editId = org.id;
    this.service.getById(org.id).subscribe({
      next: (res) => {
        const d = res.result;
        if (!d) return;
        this.form.patchValue({
          orgCode:      d.orgCode,
          orgName:      d.orgName,
          plan:         d.plan,
          contactEmail: d.contactEmail ?? '',
          contactPhone: d.contactPhone ?? '',
          address:      d.address ?? '',
          country:      d.country ?? '',
          timeZone:     d.timeZone ?? '',
          adminFirstName: '',
          adminLastName:  '',
          adminEmail:     ''
        });
        // Org code and plan are changed via their own dedicated actions, not this edit form —
        // plan changes deliberately don't touch feature toggles, so they're a separate flow.
        // The initial Admin is only collected here at creation time; changing who the admin is
        // afterward is the separate "Manage Admin" dialog (manageAdmin/saveAdmin below), not this form.
        this.form.get('orgCode')!.disable();
        this.form.get('plan')!.disable();
        this.form.get('adminFirstName')!.disable();
        this.form.get('adminEmail')!.disable();
        this.showDialog = true;
      },
      error: () => this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load organization' })
    });
  }

  save() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.isSaving = true;
    const v = this.form.getRawValue();

    const onNext = (res: { success: boolean; message: string }) => {
      this.isSaving = false;
      if (res.success) {
        this.messageService.add({
          severity: 'success', summary: 'Saved',
          detail: this.isEditing ? 'Organization updated' : 'Organization created'
        });
        this.showDialog = false;
        this.load();
      } else {
        this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Save failed' });
      }
    };
    const onError = (err: any) => {
      this.isSaving = false;
      this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Save failed' });
    };

    if (this.isEditing && this.editId) {
      this.service.update(this.editId, {
        orgName:      v.orgName,
        contactEmail: v.contactEmail || undefined,
        contactPhone: v.contactPhone || undefined,
        address:      v.address || undefined,
        country:      v.country || undefined,
        timeZone:     v.timeZone || undefined
      }).subscribe({ next: onNext, error: onError });
    } else {
      this.service.create({
        orgCode:        v.orgCode,
        orgName:        v.orgName,
        plan:           v.plan,
        contactEmail:   v.contactEmail || undefined,
        contactPhone:   v.contactPhone || undefined,
        address:        v.address || undefined,
        country:        v.country || undefined,
        timeZone:       v.timeZone || undefined,
        adminFirstName: v.adminFirstName,
        adminLastName:  v.adminLastName || '',
        adminEmail:     v.adminEmail
      }).subscribe({ next: onNext, error: onError });
    }
  }

  // Reactivating a suspended org is a routine, low-risk action.
  confirmActivate(org: OrganizationListItemModel) {
    this.confirmationService.confirm({
      message: `Activate <strong>${org.orgName}</strong>?`,
      header: 'Activate Organization',
      icon: 'pi pi-exclamation-triangle',
      acceptLabel: 'Activate',
      acceptButtonStyleClass: 'p-button-success',
      rejectLabel: 'Cancel',
      accept: () => {
        this.service.patchStatus(org.id, true).subscribe({
          next: () => {
            this.messageService.add({ severity: 'success', summary: 'Updated', detail: 'Organization activated' });
            this.load();
          },
          error: (err) => this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Update failed' })
        });
      }
    });
  }

  // Deactivation is a distinct, more consequential action: it immediately terminates every
  // active session for this org's users, so the confirmation calls that out explicitly.
  confirmDeactivate(org: OrganizationListItemModel) {
    this.confirmationService.confirm({
      message: `Deactivate <strong>${org.orgName}</strong>? This will immediately sign out all of its users and block further logins.`,
      header: 'Deactivate Organization',
      icon: 'pi pi-exclamation-triangle',
      acceptLabel: 'Deactivate',
      acceptButtonStyleClass: 'p-button-danger',
      rejectLabel: 'Cancel',
      accept: () => {
        this.service.deactivate(org.id).subscribe({
          next: () => {
            this.messageService.add({ severity: 'success', summary: 'Deactivated', detail: 'Organization deactivated; all active sessions were terminated.' });
            this.load();
          },
          error: (err) => this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Update failed' })
        });
      }
    });
  }

  manageFeatures(org: OrganizationListItemModel) {
    this.router.navigate(['/portal/pages/organizations', org.id, 'features']);
  }

  // Opens the view/change-admin dialog. "The org admin" has no dedicated column on Organization —
  // it's derived here as whichever active user in the org holds the OrgAdmin role.
  manageAdmin(org: OrganizationListItemModel) {
    this.adminOrg = org;
    this.orgUsers = [];
    this.currentAdmin = null;
    this.selectedNewAdminId = null;
    this.isLoadingAdmin = true;
    this.showAdminDialog = true;

    this.service.getOrgUsers(org.id).subscribe({
      next: (res) => {
        this.isLoadingAdmin = false;
        this.orgUsers = res.result ?? [];
        this.currentAdmin = this.orgUsers.find(u => u.roleId === ORG_ADMIN_ROLE_ID) ?? null;
      },
      error: (err) => {
        this.isLoadingAdmin = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to load organization users' });
      }
    });
  }

  // Candidates to promote — everyone active in the org except whoever already holds OrgAdmin.
  get adminCandidates(): OrgUserSummary[] {
    return this.orgUsers.filter(u => u.isActive && u.roleId !== ORG_ADMIN_ROLE_ID);
  }

  saveAdmin() {
    if (!this.adminOrg || !this.selectedNewAdminId) return;
    this.isSavingAdmin = true;
    this.service.updateAdmin(this.adminOrg.id, this.selectedNewAdminId).subscribe({
      next: () => {
        this.isSavingAdmin = false;
        this.showAdminDialog = false;
        this.messageService.add({ severity: 'success', summary: 'Updated', detail: 'Organization admin updated.' });
      },
      error: (err) => {
        this.isSavingAdmin = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to update organization admin' });
      }
    });
  }

  planSeverity(plan: string): 'success' | 'info' | 'warn' {
    switch (plan) {
      case 'ENTERPRISE': return 'success';
      case 'STANDARD':   return 'info';
      default:           return 'warn';
    }
  }

  get f() { return this.form.controls; }
}
