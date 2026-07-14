import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule, Router, ActivatedRoute } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { InputNumberModule } from 'primeng/inputnumber';
import { DropdownModule } from 'primeng/dropdown';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';
import { MaterialService, CreateProjectRequest, PatchProjectRequest, ProjectDetail } from '../../../../services/material.service';
import { UserService, UserListItem } from '../../../../services/user.service';

@Component({
  selector: 'app-project-create',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, InputTextModule, TextareaModule,
    InputNumberModule, DropdownModule, ToastModule
  ],
  templateUrl: './project-create.component.html',
  styleUrls: ['./project-create.component.scss'],
  providers: [MessageService]
})
export class ProjectCreateComponent implements OnInit {
  editUuid: string | null = null;
  isEditMode = false;
  isLoading  = false;
  isSaving   = false;

  projectCode      = '';
  projectName      = '';
  budgetAmount: number | null = null;
  description      = '';
  projectManagerId: number | null = null;
  selectedStatus   = 'ACTIVE';

  userOptions: { label: string; value: number }[] = [];

  statusOptions = [
    { label: 'Active',    value: 'ACTIVE' },
    { label: 'On Hold',   value: 'ON_HOLD' },
    { label: 'Completed', value: 'COMPLETED' },
    { label: 'Cancelled', value: 'CANCELLED' }
  ];

  constructor(
    private materialService: MaterialService,
    private userService: UserService,
    private messageService: MessageService,
    private router: Router,
    private route: ActivatedRoute
  ) {}

  ngOnInit() {
    this.editUuid = this.route.snapshot.paramMap.get('uuid');
    this.isEditMode = !!this.editUuid;
    this.loadUsers();
    if (this.isEditMode) this.loadProject();
  }

  loadUsers() {
    this.userService.getUsers({ pageSize: 200 }).subscribe({
      next: (res) => {
        const items: UserListItem[] = res?.result?.items ?? res?.result?.data ?? [];
        this.userOptions = items
          .filter((u: UserListItem) => u.isActive)
          .map((u: UserListItem) => ({
            label: `${u.firstName} ${u.lastName ?? ''}`.trim(),
            value: u.userID
          }));
      },
      error: () => {}
    });
  }

  loadProject() {
    this.isLoading = true;
    this.materialService.getProject(this.editUuid!).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success && res.result) {
          const p: ProjectDetail = res.result;
          this.projectCode      = p.projectCode;
          this.projectName      = p.projectName;
          this.budgetAmount     = p.budgetAmount ?? null;
          this.description      = p.description ?? '';
          this.projectManagerId = p.projectManagerId ?? null;
          this.selectedStatus   = p.status;
        }
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load project.' });
      }
    });
  }

  onSubmit() {
    if (!this.projectCode.trim() || !this.projectName.trim()) {
      this.messageService.add({ severity: 'warn', summary: 'Validation', detail: 'Project Code and Name are required.' });
      return;
    }
    this.isSaving = true;

    if (this.isEditMode) {
      const patch: PatchProjectRequest = {
        projectName:      this.projectName,
        projectManagerId: this.projectManagerId ?? undefined,
        budgetAmount:     this.budgetAmount ?? 0,
        description:      this.description || undefined,
        status:           this.selectedStatus
      };
      this.materialService.patchProject(this.editUuid!, patch).subscribe({
        next: () => {
          this.isSaving = false;
          this.messageService.add({ severity: 'success', summary: 'Saved', detail: 'Project updated.' });
          setTimeout(() => this.router.navigate(['/portal/pages/material/projects', this.editUuid]), 1200);
        },
        error: (err: any) => {
          this.isSaving = false;
          this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message ?? 'Failed to update project.' });
        }
      });
    } else {
      const req: CreateProjectRequest = {
        projectCode:      this.projectCode.trim(),
        projectName:      this.projectName.trim(),
        projectManagerId: this.projectManagerId ?? undefined,
        budgetAmount:     this.budgetAmount ?? 0,
        description:      this.description || undefined
      };
      this.materialService.createProject(req).subscribe({
        next: (res) => {
          this.isSaving = false;
          const uuid = res.result;
          this.messageService.add({ severity: 'success', summary: 'Created', detail: 'Project created successfully.' });
          setTimeout(() => this.router.navigate(['/portal/pages/material/projects', uuid]), 1200);
        },
        error: (err: any) => {
          this.isSaving = false;
          this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message ?? 'Failed to create project.' });
        }
      });
    }
  }
}
