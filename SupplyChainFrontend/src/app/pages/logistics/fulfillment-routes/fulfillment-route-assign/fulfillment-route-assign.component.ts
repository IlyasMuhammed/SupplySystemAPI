import { Component, EventEmitter, Input, OnInit, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { SelectModule } from 'primeng/select';

import {
  AssignRouteByCategoryRequest, AssignRouteByCategoryResult, FulfillmentRouteModel, FulfillmentRoutesService, routeCategoryOf
} from '../../../../services/fulfillment-routes.service';
import { CategoryModel, InventoryService } from '../../../../services/inventory.service';

/**
 * A33 D-14 — "Assign to category": sets a route on every active variant of the active products in a category (all its
 * sub-categories, or one of them) that has no route yet. Variants that already have a route keep it (BR-C2-02).
 * POST api/fulfillment-routes/{uuid}/assign-by-category, FULFILLMENT_ROUTE_ASSIGN (the list shows the action only
 * then). Choose → confirm → counts.
 */
@Component({
  selector: 'app-fulfillment-route-assign',
  standalone: true,
  imports: [CommonModule, FormsModule, ButtonModule, SelectModule],
  templateUrl: './fulfillment-route-assign.component.html',
  styleUrls: ['./fulfillment-route-assign.component.scss']
})
export class FulfillmentRouteAssignComponent implements OnInit {
  @Input() route: FulfillmentRouteModel | null = null;
  /** The user is done (closed after the counts, or cancelled). */
  @Output() closed = new EventEmitter<void>();
  /** The assignment ran. */
  @Output() assigned = new EventEmitter<AssignRouteByCategoryResult>();

  categories: CategoryModel[] = [];
  categoriesLoading = false;
  categoriesFailed = false;

  categoryId: number | null = null;
  subCategoryId: number | null = null;

  step: 'choose' | 'confirm' | 'done' = 'choose';
  isAssigning = false;
  error = '';
  result: AssignRouteByCategoryResult | null = null;

  constructor(private routes: FulfillmentRoutesService, private inventory: InventoryService) {}

  ngOnInit(): void {
    this.categoriesLoading = true;
    this.inventory.getCategories().subscribe({
      next: res => { this.categoriesLoading = false; this.categories = res.result ?? []; },
      error: () => { this.categoriesLoading = false; this.categoriesFailed = true; }
    });
  }

  get categoryOptions(): { label: string; value: number }[] {
    return this.categories.filter(c => c.isActive).map(c => ({ label: c.name, value: c.id }));
  }

  get subCategoryOptions(): { label: string; value: number }[] {
    const category = this.selectedCategory;
    return (category?.subCategories ?? []).filter(s => s.isActive).map(s => ({ label: s.name, value: s.id }));
  }

  /** A34 §4.2 — a MANUFACTURE route is set only on manufactured products; the rest count as skipped. */
  get isManufacture(): boolean { return routeCategoryOf(this.route) === 'MANUFACTURE'; }

  get selectedCategory(): CategoryModel | null { return this.categories.find(c => c.id === this.categoryId) ?? null; }

  get selectedSubCategoryName(): string | null {
    return this.selectedCategory?.subCategories.find(s => s.id === this.subCategoryId)?.name ?? null;
  }

  onCategoryChange(id: number | null): void {
    this.categoryId = id;
    this.subCategoryId = null;
  }

  next(): void {
    if (this.categoryId == null) return;
    this.error = '';
    this.step = 'confirm';
  }

  back(): void {
    this.error = '';
    this.step = 'choose';
  }

  assign(): void {
    if (!this.route || this.categoryId == null || this.isAssigning) return;
    const req: AssignRouteByCategoryRequest = this.subCategoryId != null
      ? { categoryId: this.categoryId, subCategoryId: this.subCategoryId }
      : { categoryId: this.categoryId };
    this.isAssigning = true;
    this.error = '';
    this.routes.assignByCategory(this.route.uuid, req).subscribe({
      next: res => {
        this.isAssigning = false;
        this.result = res.result;
        this.step = 'done';
        this.assigned.emit(res.result);
      },
      error: err => {
        this.isAssigning = false;
        this.error = err?.error?.message || 'The route could not be assigned.';
      }
    });
  }
}
