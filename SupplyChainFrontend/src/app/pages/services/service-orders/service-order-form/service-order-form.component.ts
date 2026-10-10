import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router, RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { forkJoin, of } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { ButtonModule } from 'primeng/button';
import { CalendarModule } from 'primeng/calendar';
import { DropdownModule } from 'primeng/dropdown';
import { InputNumberModule } from 'primeng/inputnumber';
import { TextareaModule } from 'primeng/textarea';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';

import { SERVICE_PRIORITY_OPTIONS, ServiceOrderService } from '../../../../services/service-order.service';
import { InventoryService, ProductListItemModel, WarehouseModel } from '../../../../services/inventory.service';
import { BusinessPartnerService } from '../../../../services/business-partner.service';
import { UserService } from '../../../../services/user.service';
import { AuthService } from '../../../service/auth.service';
import { AssigneeOption, assigneeOptions$ } from '../../../sales/sale-inquiries/sale-inquiry.shared';
import { ProductVariantPickerComponent, VariantPickerSelection } from '../../../../shared/product-variant-picker/product-variant-picker.component';
import { FLOW } from '../../../../shared/flow';
import { PartnerOption, SERVICE_ORDERS_ROUTE, customerOptions$, hhmm, serverMessage, ymd } from '../service-order-shared';

/** A36-P2-12/13 — raise a service order by hand for a SERVICE product. */
@Component({
  selector: 'app-service-order-form',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, CalendarModule, DropdownModule, InputNumberModule, TextareaModule, ToastModule,
    ProductVariantPickerComponent, ...FLOW
  ],
  templateUrl: './service-order-form.component.html',
  styleUrls: ['../_service-shared.scss'],
  providers: [MessageService]
})
export class ServiceOrderFormComponent implements OnInit {
  readonly priorityOptions = SERVICE_PRIORITY_OPTIONS;

  isLoading = true;
  isSaving = false;

  serviceProducts: ProductListItemModel[] = [];
  customerOptions: PartnerOption[] = [];
  warehouseOptions: { label: string; value: string }[] = [];
  userOptions: AssigneeOption[] = [];
  roleOptions: { label: string; value: number }[] = [];

  productUuid: string | null = null;
  variantUuid: string | null = null;
  customerUuid: string | null = null;
  quantity: number | null = 1;
  warehouseUuid: string | null = null;
  assignedUserId: number | null = null;
  assignedRoleId: number | null = null;
  scheduledDate: Date | null = null;
  scheduledTime: Date | null = null;
  estimatedHours: number | null = null;
  priority = 1;
  notes = '';

  /** The product's estimated duration per unit; the hours follow quantity until the person types their own. */
  durationPerUnit: number | null = null;
  private hoursTouched = false;

  constructor(
    private router: Router,
    private service: ServiceOrderService,
    private inventory: InventoryService,
    private partners: BusinessPartnerService,
    private users: UserService,
    private auth: AuthService,
    private messageService: MessageService
  ) {}

  ngOnInit(): void {
    forkJoin({
      products:   this.inventory.getProducts({ productType: 'SERVICE', activeOnly: true, pageSize: 500 }).pipe(catchError(() => of(null))),
      warehouses: this.inventory.getWarehouses().pipe(catchError(() => of(null))),
      customers:  customerOptions$(this.partners),
      users:      assigneeOptions$(this.users, this.auth),
      roles:      this.auth.getRoles().pipe(catchError(() => of(null)))
    }).subscribe(({ products, warehouses, customers, users, roles }) => {
      // The list endpoint filters by type; the client re-checks in case an older server ignores it.
      this.serviceProducts = (products?.result?.data ?? []).filter(p => !p.productType || p.productType === 'SERVICE');
      this.warehouseOptions = ((warehouses?.result ?? []) as WarehouseModel[])
        .filter(w => w.isActive !== false).map(w => ({ label: `${w.code} – ${w.name}`, value: w.uuid }));
      if (this.warehouseOptions.length === 1) this.warehouseUuid = this.warehouseOptions[0].value;
      this.customerOptions = customers;
      this.userOptions = users;
      this.roleOptions = (roles?.result ?? []).map(r => ({ label: r.value, value: r.id }));
      this.isLoading = false;
    });
  }

  onProductPicked(sel: VariantPickerSelection): void {
    const changed = sel.productUuid !== this.productUuid;
    this.productUuid = sel.productUuid;
    this.variantUuid = sel.variantUuid;
    if (!changed) return;
    this.durationPerUnit = null;
    const product = this.serviceProducts.find(p => p.uuid === sel.productUuid);
    if (!product) return;
    this.inventory.getProductById(product.id).pipe(catchError(() => of(null))).subscribe(res => {
      const hours = (res?.result as { estimatedDurationHours?: number | null } | undefined)?.estimatedDurationHours;
      this.durationPerUnit = hours && hours > 0 ? hours : null;
      this.prefillHours();
    });
  }

  onQuantityChange(): void { this.prefillHours(); }

  onHoursEdited(): void { this.hoursTouched = true; }

  private prefillHours(): void {
    if (this.hoursTouched || !this.durationPerUnit || !this.quantity) return;
    this.estimatedHours = Math.round(this.durationPerUnit * this.quantity * 100) / 100;
  }

  /** One of the two: picking a person clears the team, and the other way round. */
  onUserChange(): void { if (this.assignedUserId) this.assignedRoleId = null; }
  onRoleChange(): void { if (this.assignedRoleId) this.assignedUserId = null; }

  get canSave(): boolean {
    return !this.isSaving && !!this.productUuid && !!this.customerUuid && !!this.warehouseUuid
      && !!this.quantity && this.quantity > 0 && (this.estimatedHours == null || this.estimatedHours > 0);
  }

  save(): void {
    if (!this.canSave) return;
    this.isSaving = true;
    this.service.create({
      serviceProductUuid: this.productUuid!,
      serviceVariantUuid: this.variantUuid || undefined,
      customerUuid: this.customerUuid!,
      quantity: this.quantity!,
      warehouseUuid: this.warehouseUuid!,
      assignedUserId: this.assignedUserId ?? undefined,
      assignedRoleId: this.assignedRoleId ?? undefined,
      scheduledDate: ymd(this.scheduledDate),
      scheduledTime: this.scheduledDate ? hhmm(this.scheduledTime) : undefined,
      estimatedHours: this.estimatedHours ?? undefined,
      priority: this.priority,
      notes: this.notes.trim() || undefined
    }).subscribe({
      next: res => {
        this.isSaving = false;
        this.messageService.add({ severity: 'success', summary: 'Created', detail: 'Service order created as a draft.' });
        this.router.navigate([SERVICE_ORDERS_ROUTE, res.result]);
      },
      error: err => {
        this.isSaving = false;
        this.messageService.add({ severity: 'error', summary: 'Not created', detail: serverMessage(err, 'The service order could not be created.') });
      }
    });
  }

  cancel(): void { this.router.navigate([SERVICE_ORDERS_ROUTE]); }
}
