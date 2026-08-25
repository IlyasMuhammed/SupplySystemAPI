import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { TableModule } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { DropdownModule } from 'primeng/dropdown';
import { InputTextModule } from 'primeng/inputtext';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { TooltipModule } from 'primeng/tooltip';
import { InputIconModule } from 'primeng/inputicon';
import { IconFieldModule } from 'primeng/iconfield';
import { MessageService, ConfirmationService } from 'primeng/api';
import {
  InventoryService,
  ProductListItemModel,
  ProductListFilter
} from '../../../../services/inventory.service';
import { AttachmentService } from '../../../../services/attachment.service';

@Component({
  selector: 'app-product-list',
  standalone: true,
  imports: [
    CommonModule, FormsModule,
    ButtonModule, TableModule, TagModule, ToastModule,
    DropdownModule, InputTextModule, ConfirmDialogModule,
    TooltipModule, InputIconModule, IconFieldModule
  ],
  templateUrl: './product-list.component.html',
  styleUrls: ['./product-list.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class ProductListComponent implements OnInit {
  products: ProductListItemModel[] = [];
  categories: { label: string; value: number | null }[] = [{ label: 'All Categories', value: null }];
  totalRecords = 0;
  currentPage = 1;
  pageSize = 20;
  isLoading = true;

  searchText = '';
  selectedCategory: number | null = null;
  selectedStatus = '';

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  statusOptions = [
    { label: 'All Statuses', value: '' },
    { label: 'Active',       value: 'ACTIVE' },
    { label: 'Inactive',     value: 'INACTIVE' }
  ];

  get activeCount(): number {
    return this.products.filter(p => p.status === 'ACTIVE').length;
  }

  get inactiveCount(): number {
    return this.products.filter(p => p.status === 'INACTIVE').length;
  }

  constructor(
    private inventoryService: InventoryService,
    private messageService: MessageService,
    private confirmationService: ConfirmationService,
    private router: Router,
    private attachmentService: AttachmentService
  ) {}

  resolveImageUrl(url: string): string {
    return this.attachmentService.resolveUrl(url);
  }

  ngOnInit(): void {
    this.loadCategories();
    this.loadProducts();
  }

  loadCategories(): void {
    this.inventoryService.getCategories().subscribe({
      next: (res) => {
        if (res.success && res.result) {
          const catOptions = res.result
            .filter(c => c.isActive)
            .map(c => ({ label: c.name, value: c.id as number | null }));
          this.categories = [{ label: 'All Categories', value: null }, ...catOptions];
        }
      },
      error: () => {
        this.messageService.add({ severity: 'warn', summary: 'Warning', detail: 'Could not load categories' });
      }
    });
  }

  loadProducts(): void {
    this.isLoading = true;
    const filter: ProductListFilter = {
      page: this.currentPage,
      pageSize: this.pageSize,
      search: this.searchText || undefined,
      status: this.selectedStatus || undefined,
      categoryId: this.selectedCategory ?? undefined
    };

    this.inventoryService.getProducts(filter).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success && res.result) {
          this.products = res.result.data ?? [];
          this.totalRecords = res.result.totalRecords ?? 0;
        } else {
          this.products = [];
          this.totalRecords = 0;
        }
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load products' });
      }
    });
  }

  onSearchChange(): void {
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => {
      this.currentPage = 1;
      this.loadProducts();
    }, 400);
  }

  onFilterChange(): void {
    this.currentPage = 1;
    this.loadProducts();
  }

  onPageChange(event: { first: number; rows: number }): void {
    this.currentPage = Math.floor(event.first / event.rows) + 1;
    this.pageSize = event.rows;
    this.loadProducts();
  }

  resetFilters(): void {
    this.searchText = '';
    this.selectedCategory = null;
    this.selectedStatus = '';
    this.currentPage = 1;
    this.loadProducts();
  }

  getStatusSeverity(status: string): 'success' | 'danger' | 'warn' | 'secondary' | 'info' {
    switch (status) {
      case 'ACTIVE':   return 'success';
      case 'INACTIVE': return 'secondary';
      default:         return 'info';
    }
  }

  confirmDeactivate(product: ProductListItemModel): void {
    this.confirmationService.confirm({
      message: `Are you sure you want to deactivate <strong>${product.name}</strong>? This product will no longer be available for orders.`,
      header: 'Confirm Deactivation',
      icon: 'pi pi-exclamation-triangle',
      acceptButtonStyleClass: 'p-button-danger',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => {
        this.inventoryService.deleteProduct(product.id).subscribe({
          next: (res) => {
            if (res.success) {
              this.messageService.add({ severity: 'success', summary: 'Deactivated', detail: `${product.name} has been deactivated` });
              this.loadProducts();
            } else {
              this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Deactivation failed' });
            }
          },
          error: (err) => {
            this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to deactivate product' });
          }
        });
      }
    });
  }

  viewProduct(id: number): void {
    this.router.navigate(['/portal/pages/inventory/products', id]);
  }

  createProduct(): void {
    this.router.navigate(['/portal/pages/inventory/products/new']);
  }
}
