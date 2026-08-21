import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { ReactiveFormsModule, FormsModule, FormBuilder, FormGroup, Validators } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { DialogModule } from 'primeng/dialog';
import { TableModule } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { DividerModule } from 'primeng/divider';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { CheckboxModule } from 'primeng/checkbox';
import { DropdownModule } from 'primeng/dropdown';
import { MessageService, ConfirmationService } from 'primeng/api';
import { MessageModule } from 'primeng/message';
import {
  InventoryService,
  CategoryModel,
  SubCategoryModel,
  CategoryDeleteConflictResult,
  SubCategoryDeleteConflictResult
} from '../../../services/inventory.service';

@Component({
  selector: 'app-category-list',
  standalone: true,
  imports: [
    CommonModule, RouterModule, ReactiveFormsModule, FormsModule,
    ButtonModule, InputTextModule, TextareaModule, DialogModule,
    TableModule, TagModule, ToastModule, TooltipModule, DividerModule,
    ConfirmDialogModule, MessageModule, CheckboxModule, DropdownModule
  ],
  templateUrl: './category-list.component.html',
  styleUrls: ['./category-list.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class CategoryListComponent implements OnInit {
  categories: CategoryModel[] = [];
  isLoading = false;

  // ── Create category ───────────────────────────────────────────────────────
  showCreateCategoryDialog = false;
  isSavingCategory = false;
  categoryForm!: FormGroup;
  duplicateCategoryName = false;
  duplicateCategoryCode = false;

  // ── Edit category ─────────────────────────────────────────────────────────
  showEditCategoryDialog = false;
  isUpdatingCategory = false;
  editCategoryForm!: FormGroup;
  editingCategory: CategoryModel | null = null;

  // ── Delete / deactivate category ──────────────────────────────────────────
  showDeactivateDialog = false;
  isDeactivating = false;
  deactivatingCategory: CategoryModel | null = null;
  deleteConflict: CategoryDeleteConflictResult | null = null;

  // ── Delete / deactivate sub-category ─────────────────────────────────────
  showDeactivateSubDialog = false;
  isDeactivatingSub = false;
  deactivatingSubCategory: SubCategoryModel | null = null;
  deleteSubConflict: SubCategoryDeleteConflictResult | null = null;

  // ── Create sub-category ───────────────────────────────────────────────────
  showSubCategoryDialog = false;
  isSavingSubCategory = false;
  subCategoryForm!: FormGroup;
  selectedCategory: CategoryModel | null = null;
  duplicateSubCategoryName = false;
  duplicateSubCategoryCode = false;

  // ── Edit sub-category ─────────────────────────────────────────────────────
  showEditSubCategoryDialog = false;
  isUpdatingSubCategory = false;
  editSubCategoryForm!: FormGroup;
  editingSubCategory: SubCategoryModel | null = null;
  editingSubCategoryParent: CategoryModel | null = null;

  expandedRows: { [key: string]: boolean } = {};

  get categoryDropdownOptions(): { label: string; value: number }[] {
    return this.categories.map(c => ({ label: c.name, value: c.id }));
  }

  constructor(
    private inventoryService: InventoryService,
    private fb: FormBuilder,
    private messageService: MessageService,
    private confirmationService: ConfirmationService
  ) {}

  ngOnInit(): void {
    this.categoryForm = this.fb.group({
      name:        ['', [Validators.required, Validators.maxLength(100)]],
      code:        ['', [Validators.required, Validators.maxLength(20)]],
      description: ['', Validators.maxLength(500)]
    });
    this.editCategoryForm = this.fb.group({
      name:        ['', [Validators.required, Validators.maxLength(100)]],
      description: ['', Validators.maxLength(500)],
      isActive:    [true]
    });
    this.subCategoryForm = this.fb.group({
      name:        ['', [Validators.required, Validators.maxLength(100)]],
      code:        ['', [Validators.required, Validators.maxLength(20)]],
      description: ['', Validators.maxLength(500)]
    });
    this.editSubCategoryForm = this.fb.group({
      name:        ['', [Validators.required, Validators.maxLength(100)]],
      categoryId:  [null, Validators.required],
      description: ['', Validators.maxLength(500)],
      isActive:    [true]
    });
    this.load();
  }

  load(): void {
    this.isLoading = true;
    this.inventoryService.getCategories().subscribe({
      next: res => {
        this.isLoading = false;
        this.categories = res?.result ?? [];
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load categories.' });
      }
    });
  }

  // ── Create category ───────────────────────────────────────────────────────

  openAddCategory(): void {
    this.categoryForm.reset({ name: '', code: '', description: '' });
    this.duplicateCategoryName = false;
    this.duplicateCategoryCode = false;
    this.showCreateCategoryDialog = true;
  }

  saveCategory(): void {
    if (this.categoryForm.invalid) { this.categoryForm.markAllAsTouched(); return; }
    this.checkDuplicateCategoryName();
    this.checkDuplicateCategoryCode();
    if (this.duplicateCategoryName) {
      this.messageService.add({ severity: 'warn', summary: 'Duplicate', detail: 'A category with this name already exists.' });
      return;
    }
    if (this.duplicateCategoryCode) {
      this.messageService.add({ severity: 'warn', summary: 'Duplicate', detail: 'A category with this code already exists.' });
      return;
    }
    this.isSavingCategory = true;
    const v = this.categoryForm.value;
    this.inventoryService.createCategory({
      name: v.name,
      code: v.code.toUpperCase(),
      description: v.description || undefined
    }).subscribe({
      next: (res) => {
        this.isSavingCategory = false;
        if (res.success) {
          this.showCreateCategoryDialog = false;
          this.messageService.add({ severity: 'success', summary: 'Created', detail: 'Category created.' });
          this.load();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Failed to create category.' });
        }
      },
      error: (err) => {
        this.isSavingCategory = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message ?? 'Failed to create category.' });
      }
    });
  }

  // ── Edit category ─────────────────────────────────────────────────────────

  openEditCategory(cat: CategoryModel): void {
    this.editingCategory = cat;
    this.editCategoryForm.reset({
      name:        cat.name,
      description: cat.description || '',
      isActive:    cat.isActive
    });
    this.showEditCategoryDialog = true;
  }

  saveEditCategory(): void {
    if (this.editCategoryForm.invalid || !this.editingCategory) { this.editCategoryForm.markAllAsTouched(); return; }
    this.isUpdatingCategory = true;
    const v = this.editCategoryForm.value;
    this.inventoryService.updateCategory(this.editingCategory.id, {
      name:        v.name,
      description: v.description || undefined,
      isActive:    v.isActive ?? true
    }).subscribe({
      next: (res) => {
        this.isUpdatingCategory = false;
        if (res.success) {
          this.showEditCategoryDialog = false;
          this.messageService.add({ severity: 'success', summary: 'Updated', detail: 'Category updated.' });
          this.load();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Failed to update category.' });
        }
      },
      error: (err) => {
        this.isUpdatingCategory = false;
        const msg = err?.error?.message ?? 'Failed to update category.';
        this.messageService.add({ severity: 'error', summary: 'Error', detail: msg });
      }
    });
  }

  // ── Delete / deactivate category ──────────────────────────────────────────

  confirmDeleteCategory(cat: CategoryModel): void {
    this.confirmationService.confirm({
      message: `Delete category <strong>${cat.name}</strong>? This action is permanent.`,
      header: 'Delete Category',
      icon: 'pi pi-exclamation-triangle',
      acceptLabel: 'Delete',
      acceptButtonStyleClass: 'p-button-danger',
      rejectLabel: 'Cancel',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => {
        this.inventoryService.deleteCategory(cat.id).subscribe({
          next: (res) => {
            if (res.success) {
              this.messageService.add({ severity: 'success', summary: 'Deleted', detail: `Category "${cat.name}" deleted.` });
              this.load();
            } else {
              this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Failed to delete category.' });
            }
          },
          error: (err) => {
            if (err.status === 409) {
              this.deactivatingCategory = cat;
              this.deleteConflict = err.error?.result ?? { referencedProductCount: 0, referencedSubCategoryCount: 0 };
              this.showDeactivateDialog = true;
            } else {
              this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message ?? 'Failed to delete category.' });
            }
          }
        });
      }
    });
  }

  deactivateCategory(): void {
    if (!this.deactivatingCategory) return;
    this.isDeactivating = true;
    this.inventoryService.deactivateCategory(this.deactivatingCategory.id).subscribe({
      next: (res) => {
        this.isDeactivating = false;
        if (res.success) {
          this.showDeactivateDialog = false;
          this.messageService.add({ severity: 'success', summary: 'Deactivated', detail: `Category "${this.deactivatingCategory!.name}" has been deactivated.` });
          this.load();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Failed to deactivate category.' });
        }
      },
      error: (err) => {
        this.isDeactivating = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message ?? 'Failed to deactivate category.' });
      }
    });
  }

  // ── Create sub-category ───────────────────────────────────────────────────

  openAddSubCategory(cat: CategoryModel): void {
    this.selectedCategory = cat;
    this.subCategoryForm.reset({ name: '', code: '', description: '' });
    this.duplicateSubCategoryName = false;
    this.duplicateSubCategoryCode = false;
    this.showSubCategoryDialog = true;
  }

  saveSubCategory(): void {
    if (this.subCategoryForm.invalid || !this.selectedCategory) { this.subCategoryForm.markAllAsTouched(); return; }
    this.checkDuplicateSubCategoryName();
    this.checkDuplicateSubCategoryCode();
    if (this.duplicateSubCategoryName) {
      this.messageService.add({ severity: 'warn', summary: 'Duplicate', detail: 'A sub-category with this name already exists under this category.' });
      return;
    }
    if (this.duplicateSubCategoryCode) {
      this.messageService.add({ severity: 'warn', summary: 'Duplicate', detail: 'A sub-category with this code already exists under this category.' });
      return;
    }
    this.isSavingSubCategory = true;
    const v = this.subCategoryForm.value;
    this.inventoryService.createSubCategory(this.selectedCategory.id, {
      name: v.name,
      code: v.code.toUpperCase(),
      description: v.description || undefined
    }).subscribe({
      next: (res) => {
        this.isSavingSubCategory = false;
        if (res.success) {
          this.showSubCategoryDialog = false;
          this.messageService.add({ severity: 'success', summary: 'Created', detail: `Sub-category added to "${this.selectedCategory!.name}".` });
          this.load();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Failed to create sub-category.' });
        }
      },
      error: (err) => {
        this.isSavingSubCategory = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message ?? 'Failed to create sub-category.' });
      }
    });
  }

  // ── Edit sub-category ─────────────────────────────────────────────────────

  openEditSubCategory(cat: CategoryModel, sub: SubCategoryModel): void {
    this.editingSubCategoryParent = cat;
    this.editingSubCategory = sub;
    this.editSubCategoryForm.reset({
      name:        sub.name,
      categoryId:  cat.id,
      description: sub.description || '',
      isActive:    sub.isActive
    });
    this.showEditSubCategoryDialog = true;
  }

  saveEditSubCategory(): void {
    if (this.editSubCategoryForm.invalid || !this.editingSubCategory) {
      this.editSubCategoryForm.markAllAsTouched(); return;
    }
    this.isUpdatingSubCategory = true;
    const v = this.editSubCategoryForm.value;
    this.inventoryService.updateSubCategory(this.editingSubCategory.id, {
      name:        v.name,
      categoryId:  v.categoryId,
      description: v.description || undefined,
      isActive:    v.isActive ?? true
    }).subscribe({
      next: (res) => {
        this.isUpdatingSubCategory = false;
        if (res.success) {
          this.showEditSubCategoryDialog = false;
          this.messageService.add({ severity: 'success', summary: 'Updated', detail: 'Sub-category updated.' });
          this.load();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Failed to update sub-category.' });
        }
      },
      error: (err) => {
        this.isUpdatingSubCategory = false;
        const msg = err?.error?.message ?? 'Failed to update sub-category.';
        this.messageService.add({ severity: 'error', summary: 'Error', detail: msg });
      }
    });
  }

  // ── Delete sub-category ───────────────────────────────────────────────────

  confirmDeleteSubCategory(cat: CategoryModel, sub: SubCategoryModel): void {
    this.confirmationService.confirm({
      message: `Delete sub-category <strong>${sub.name}</strong>? This action is permanent.`,
      header: 'Delete Sub-Category',
      icon: 'pi pi-exclamation-triangle',
      acceptLabel: 'Delete',
      acceptButtonStyleClass: 'p-button-danger',
      rejectLabel: 'Cancel',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => {
        this.inventoryService.deleteSubCategory(sub.id).subscribe({
          next: (res) => {
            if (res.success) {
              this.messageService.add({ severity: 'success', summary: 'Deleted', detail: `Sub-category "${sub.name}" deleted.` });
              this.load();
            } else {
              this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Failed to delete sub-category.' });
            }
          },
          error: (err) => {
            if (err.status === 409) {
              this.deactivatingSubCategory = sub;
              this.deleteSubConflict = err.error?.result ?? { referencedProductCount: 0 };
              this.showDeactivateSubDialog = true;
            } else {
              this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message ?? 'Failed to delete sub-category.' });
            }
          }
        });
      }
    });
  }

  deactivateSubCategory(): void {
    if (!this.deactivatingSubCategory) return;
    this.isDeactivatingSub = true;
    this.inventoryService.deactivateSubCategory(this.deactivatingSubCategory.id).subscribe({
      next: (res) => {
        this.isDeactivatingSub = false;
        if (res.success) {
          this.showDeactivateSubDialog = false;
          this.messageService.add({ severity: 'success', summary: 'Deactivated', detail: `Sub-category "${this.deactivatingSubCategory!.name}" has been deactivated.` });
          this.load();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Failed to deactivate.' });
        }
      },
      error: (err) => {
        this.isDeactivatingSub = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message ?? 'Failed to deactivate.' });
      }
    });
  }

  // ── Duplicate checks ──────────────────────────────────────────────────────

  checkDuplicateCategoryName(): void {
    const name = (this.categoryForm.value.name || '').trim().toLowerCase();
    this.duplicateCategoryName = !!name && this.categories.some(c => c.name.trim().toLowerCase() === name);
  }

  checkDuplicateCategoryCode(): void {
    const code = (this.categoryForm.value.code || '').trim().toUpperCase();
    this.duplicateCategoryCode = !!code && this.categories.some(c => (c.code || '').trim().toUpperCase() === code);
  }

  checkDuplicateSubCategoryName(): void {
    const name = (this.subCategoryForm.value.name || '').trim().toLowerCase();
    const subs = this.selectedCategory?.subCategories ?? [];
    this.duplicateSubCategoryName = !!name && subs.some(s => s.name.trim().toLowerCase() === name);
  }

  checkDuplicateSubCategoryCode(): void {
    const code = (this.subCategoryForm.value.code || '').trim().toUpperCase();
    const subs = this.selectedCategory?.subCategories ?? [];
    this.duplicateSubCategoryCode = !!code && subs.some(s => (s.code || '').trim().toUpperCase() === code);
  }
}
