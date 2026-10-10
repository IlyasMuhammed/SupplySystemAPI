import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ReactiveFormsModule, FormsModule, FormBuilder, FormGroup, Validators } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { DialogModule } from 'primeng/dialog';
import { TableModule } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { DropdownModule } from 'primeng/dropdown';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { CheckboxModule } from 'primeng/checkbox';
import { IconFieldModule } from 'primeng/iconfield';
import { InputIconModule } from 'primeng/inputicon';
import { MessageService, ConfirmationService } from 'primeng/api';
import { FLOW } from '../../../shared/flow';
import {
  InventoryService,
  CategoryModel,
  SubCategoryListDto,
  SubCategoryListFilter,
  SubCategoryDeleteConflictResult,
  PaginatedResponse
} from '../../../services/inventory.service';

@Component({
  selector: 'app-sub-category-list',
  standalone: true,
  imports: [
    CommonModule, ReactiveFormsModule, FormsModule,
    ButtonModule, InputTextModule, TextareaModule, DialogModule,
    TableModule, TagModule, ToastModule, TooltipModule, DropdownModule,
    ConfirmDialogModule, CheckboxModule, IconFieldModule, InputIconModule,
    ...FLOW
  ],
  templateUrl: './sub-category-list.component.html',
  styleUrls: ['./sub-category-list.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class SubCategoryListComponent implements OnInit {
  subCategories: SubCategoryListDto[] = [];
  totalRecords = 0;
  isLoading = false;

  // Filters
  searchText = '';
  selectedCategoryFilter: number | null = null;
  selectedActiveFilter: boolean | null = null;
  page = 1;
  pageSize = 50;

  // Categories for dropdowns
  categories: CategoryModel[] = [];
  categoryFilterOptions: { label: string; value: number | null }[] = [];
  categoryDropdownOptions: { label: string; value: number }[] = [];

  // Create
  showCreateDialog = false;
  isSaving = false;
  createForm!: FormGroup;

  // Edit
  showEditDialog = false;
  isUpdating = false;
  editForm!: FormGroup;
  editingSubCategory: SubCategoryListDto | null = null;

  // Delete / deactivate
  showDeactivateDialog = false;
  isDeactivating = false;
  deactivatingSubCategory: SubCategoryListDto | null = null;
  deleteConflict: SubCategoryDeleteConflictResult | null = null;

  constructor(
    private inventoryService: InventoryService,
    private fb: FormBuilder,
    private messageService: MessageService,
    private confirmationService: ConfirmationService
  ) {}

  ngOnInit(): void {
    this.createForm = this.fb.group({
      categoryId:  [null, Validators.required],
      name:        ['', [Validators.required, Validators.maxLength(100)]],
      code:        ['', [Validators.required, Validators.maxLength(20)]],
      description: ['', Validators.maxLength(500)]
    });
    this.editForm = this.fb.group({
      name:        ['', [Validators.required, Validators.maxLength(100)]],
      categoryId:  [null, Validators.required],
      description: ['', Validators.maxLength(500)],
      isActive:    [true]
    });
    this.loadCategories();
    this.load();
  }

  private loadCategories(): void {
    this.inventoryService.getCategories().subscribe({
      next: res => {
        this.categories = res?.result ?? [];
        this.categoryFilterOptions = [
          { label: 'All Categories', value: null },
          ...this.categories.map(c => ({ label: c.name, value: c.id }))
        ];
        this.categoryDropdownOptions = this.categories.map(c => ({ label: c.name, value: c.id }));
      }
    });
  }

  load(): void {
    this.isLoading = true;
    const filter: SubCategoryListFilter = {
      page:       this.page,
      pageSize:   this.pageSize,
      search:     this.searchText.trim() || undefined,
      categoryId: this.selectedCategoryFilter ?? undefined,
      isActive:   this.selectedActiveFilter ?? undefined
    };
    this.inventoryService.getSubCategories(filter).subscribe({
      next: res => {
        this.isLoading = false;
        const paged = res?.result;
        this.subCategories = paged?.data ?? [];
        this.totalRecords  = paged?.totalRecords ?? 0;
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load sub-categories.' });
      }
    });
  }

  onSearch(event: Event): void {
    this.searchText = (event.target as HTMLInputElement).value;
    this.page = 1;
    this.load();
  }

  onCategoryFilterChange(): void {
    this.page = 1;
    this.load();
  }

  clearFilters(): void {
    this.searchText = '';
    this.selectedCategoryFilter = null;
    this.selectedActiveFilter = null;
    this.page = 1;
    this.load();
  }

  // ── Create ────────────────────────────────────────────────────────────────

  openCreate(): void {
    this.createForm.reset({ categoryId: null, name: '', code: '', description: '' });
    this.showCreateDialog = true;
  }

  saveCreate(): void {
    if (this.createForm.invalid) { this.createForm.markAllAsTouched(); return; }
    this.isSaving = true;
    const v = this.createForm.value;
    this.inventoryService.createSubCategory(v.categoryId, {
      name: v.name.trim(),
      code: v.code.trim().toUpperCase(),
      description: v.description?.trim() || undefined
    }).subscribe({
      next: res => {
        this.isSaving = false;
        if (res.success) {
          this.showCreateDialog = false;
          this.messageService.add({ severity: 'success', summary: 'Created', detail: 'Sub-category created.' });
          this.load();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Failed to create sub-category.' });
        }
      },
      error: err => {
        this.isSaving = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message ?? 'Failed to create sub-category.' });
      }
    });
  }

  // ── Edit ──────────────────────────────────────────────────────────────────

  openEdit(sub: SubCategoryListDto): void {
    this.editingSubCategory = sub;
    this.editForm.reset({
      name:        sub.subCategoryName,
      categoryId:  sub.parentCategoryId,
      description: '',
      isActive:    sub.isActive
    });
    this.showEditDialog = true;
  }

  saveEdit(): void {
    if (this.editForm.invalid || !this.editingSubCategory) { this.editForm.markAllAsTouched(); return; }
    this.isUpdating = true;
    const v = this.editForm.value;
    this.inventoryService.updateSubCategory(this.editingSubCategory.subCategoryId, {
      name:        v.name.trim(),
      categoryId:  v.categoryId,
      description: v.description?.trim() || undefined,
      isActive:    v.isActive ?? true
    }).subscribe({
      next: res => {
        this.isUpdating = false;
        if (res.success) {
          this.showEditDialog = false;
          this.messageService.add({ severity: 'success', summary: 'Updated', detail: 'Sub-category updated.' });
          this.load();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Failed to update sub-category.' });
        }
      },
      error: err => {
        this.isUpdating = false;
        const msg = err?.error?.message ?? 'Failed to update sub-category.';
        this.messageService.add({ severity: 'error', summary: 'Error', detail: msg });
      }
    });
  }

  // ── Delete / Deactivate ───────────────────────────────────────────────────

  confirmDelete(sub: SubCategoryListDto): void {
    this.confirmationService.confirm({
      message: `Delete sub-category <strong>${sub.subCategoryName}</strong>? This action is permanent.`,
      header: 'Delete Sub-Category',
      icon: 'pi pi-exclamation-triangle',
      acceptLabel: 'Delete',
      acceptButtonStyleClass: 'p-button-danger',
      rejectLabel: 'Cancel',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => {
        this.inventoryService.deleteSubCategory(sub.subCategoryId).subscribe({
          next: res => {
            if (res.success) {
              this.messageService.add({ severity: 'success', summary: 'Deleted', detail: `"${sub.subCategoryName}" deleted.` });
              this.load();
            } else {
              this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Failed to delete.' });
            }
          },
          error: err => {
            if (err.status === 409) {
              this.deactivatingSubCategory = sub;
              this.deleteConflict = err.error?.result ?? { referencedProductCount: 0 };
              this.showDeactivateDialog = true;
            } else {
              this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message ?? 'Failed to delete.' });
            }
          }
        });
      }
    });
  }

  deactivate(): void {
    if (!this.deactivatingSubCategory) return;
    this.isDeactivating = true;
    this.inventoryService.deactivateSubCategory(this.deactivatingSubCategory.subCategoryId).subscribe({
      next: res => {
        this.isDeactivating = false;
        if (res.success) {
          this.showDeactivateDialog = false;
          this.messageService.add({ severity: 'success', summary: 'Deactivated', detail: `"${this.deactivatingSubCategory!.subCategoryName}" deactivated.` });
          this.load();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Failed to deactivate.' });
        }
      },
      error: err => {
        this.isDeactivating = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message ?? 'Failed to deactivate.' });
      }
    });
  }
}
