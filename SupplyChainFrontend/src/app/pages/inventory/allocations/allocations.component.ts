import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormBuilder, FormGroup, FormsModule, ReactiveFormsModule, Validators } from '@angular/forms';
import { forkJoin, of } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { ButtonModule } from 'primeng/button';
import { CalendarModule } from 'primeng/calendar';
import { DialogModule } from 'primeng/dialog';
import { DropdownModule } from 'primeng/dropdown';
import { InputNumberModule } from 'primeng/inputnumber';
import { InputTextModule } from 'primeng/inputtext';
import { MessageModule } from 'primeng/message';
import { TableModule } from 'primeng/table';
import { TabViewModule } from 'primeng/tabview';
import { TagModule } from 'primeng/tag';
import { TextareaModule } from 'primeng/textarea';
import { ToastModule } from 'primeng/toast';
import { ToggleSwitchModule } from 'primeng/toggleswitch';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';

import {
  AllocationService, AllocationSummary, AllocationRuleDefinition, AllocationRunResult, AvailabilityResult,
  DemandAllocationSummary, DEMAND_TYPE_OPTIONS, PRIORITY_OPTIONS, SORT_FIELD_OPTIONS, demandTypeLabel, priorityLabel
} from '../../../services/allocation.service';
import { InventoryService, ProductListItemModel, WarehouseModel } from '../../../services/inventory.service';
import { AuthService } from '../../service/auth.service';
import {
  ProductVariantPickerComponent, VariantPickerSelection
} from '../../../shared/product-variant-picker/product-variant-picker.component';

const RUN_PERMISSION   = 'ALLOCATION_RUN';
const ADMIN_PERMISSION = 'ALLOCATION_ADMIN';

/**
 * A30 §29.4 — the allocation dashboard: for one variant, what is on hand, reserved, expected and
 * asked for; who holds what; and the levers (run, release, move, cancel, register) behind it.
 */
@Component({
  selector: 'app-allocations',
  standalone: true,
  imports: [
    CommonModule, FormsModule, ReactiveFormsModule,
    ButtonModule, CalendarModule, DialogModule, DropdownModule, InputNumberModule, InputTextModule, MessageModule,
    TableModule, TabViewModule, TagModule, TextareaModule, ToastModule, ToggleSwitchModule, TooltipModule,
    ProductVariantPickerComponent
  ],
  templateUrl: './allocations.component.html',
  styleUrls: ['./allocations.component.scss'],
  providers: [MessageService]
})
export class AllocationsComponent implements OnInit {
  readonly demandTypeOptions = DEMAND_TYPE_OPTIONS;
  readonly priorityOptions = PRIORITY_OPTIONS;
  readonly sortFieldOptions = SORT_FIELD_OPTIONS;
  readonly directionOptions = [{ label: 'Ascending', value: 'ASC' }, { label: 'Descending', value: 'DESC' }];
  readonly demandTypeLabel = demandTypeLabel;
  readonly priorityLabel = priorityLabel;

  products: ProductListItemModel[] = [];
  warehouses: WarehouseModel[] = [];
  warehouseOptions: { label: string; value: string | null }[] = [{ label: 'All warehouses', value: null }];
  isLoadingCatalog = true;

  selection: VariantPickerSelection | null = null;
  selectedWarehouseUuid: string | null = null;

  availability: AvailabilityResult | null = null;
  demands: DemandAllocationSummary[] = [];
  allocations: AllocationSummary[] = [];
  allocationsTotal = 0;
  isLoading = false;
  loadFailed = false;
  isRunning = false;
  lastRun: AllocationRunResult | null = null;

  activeTab = 0;
  rules: AllocationRuleDefinition[] = [];
  rulesDraft: AllocationRuleDefinition[] = [];
  isLoadingRules = false;
  isSavingRules = false;
  private rulesLoaded = false;

  releaseTarget: AllocationSummary | null = null;
  releaseReason = '';
  moveTarget: AllocationSummary | null = null;
  moveForm: FormGroup;
  cancelTarget: DemandAllocationSummary | null = null;
  cancelReason = '';
  registerVisible = false;
  registerForm: FormGroup;
  isSaving = false;

  constructor(
    private fb: FormBuilder,
    private service: AllocationService,
    private inventoryService: InventoryService,
    private authService: AuthService,
    private messageService: MessageService
  ) {
    this.moveForm = this.fb.group({
      toDemandUuid: [null, Validators.required],
      quantity:     [null, [Validators.required, Validators.min(0.0001)]],
      reason:       ['', [Validators.required, Validators.maxLength(500)]]
    });
    this.registerForm = this.fb.group({
      demandType:    ['SALES_ORDER', Validators.required],
      reference:     ['', [Validators.required, Validators.maxLength(50)]],
      requiredQty:   [null, [Validators.required, Validators.min(0.0001)]],
      requiredDate:  [null, Validators.required],
      priority:      [1, Validators.required],
      warehouseUuid: [null]
    });
  }

  get canRun(): boolean   { return this.authService.hasPermission(RUN_PERMISSION); }
  get canAdmin(): boolean { return this.authService.hasPermission(ADMIN_PERMISSION); }
  get variantUuid(): string | null { return this.selection?.variantUuid ?? null; }

  /** Open demands the move dialog can send stock to: same variant, not the one it comes from. */
  get moveTargets(): { label: string; value: string }[] {
    return this.demands
      .filter(d => d.status === 'OPEN' && d.uuid !== this.moveTarget?.demandRegistryUuid && d.shortage > 0)
      .map(d => ({ label: `${d.reference} (${demandTypeLabel(d.demandType)}, short ${d.shortage})`, value: d.uuid }));
  }

  ngOnInit(): void {
    forkJoin({
      products:   this.inventoryService.getProducts({ activeOnly: true, pageSize: 500 }).pipe(catchError(() => of(null))),
      warehouses: this.inventoryService.getWarehouses().pipe(catchError(() => of(null)))
    }).subscribe(({ products, warehouses }) => {
      this.isLoadingCatalog = false;
      this.products = products?.result?.data ?? [];
      this.warehouses = (warehouses?.result ?? []).filter(w => w.isActive !== false);
      this.warehouseOptions = [
        { label: 'All warehouses', value: null },
        ...this.warehouses.map(w => ({ label: w.name, value: w.uuid }))
      ];
      if (!products || !warehouses) {
        this.messageService.add({ severity: 'warn', summary: 'Warning', detail: 'Some of the catalogue could not be loaded.' });
      }
    });
  }

  onSelection(selection: VariantPickerSelection): void {
    this.selection = selection.variantUuid ? selection : null;
    this.lastRun = null;
    this.refresh();
  }

  onWarehouseChange(): void {
    this.refresh();
  }

  refresh(): void {
    const variantUuid = this.variantUuid;
    if (!variantUuid) {
      this.availability = null;
      this.demands = [];
      this.allocations = [];
      this.allocationsTotal = 0;
      return;
    }

    this.isLoading = true;
    this.loadFailed = false;
    forkJoin({
      availability: this.service.getAvailability(variantUuid, this.selectedWarehouseUuid),
      demands:      this.service.getDemands(variantUuid),
      allocations:  this.service.getAllocations({ variantUuid, warehouseUuid: this.selectedWarehouseUuid ?? undefined, pageSize: 200 })
    }).subscribe({
      next: ({ availability, demands, allocations }) => {
        this.isLoading = false;
        this.availability = availability.result ?? null;
        this.demands = demands.result ?? [];
        this.allocations = allocations.result?.items ?? [];
        this.allocationsTotal = allocations.result?.total ?? 0;
      },
      error: () => {
        this.isLoading = false;
        this.loadFailed = true;
      }
    });
  }

  run(): void {
    const variantUuid = this.variantUuid;
    if (!variantUuid || this.isRunning) return;
    this.isRunning = true;
    this.service.run(variantUuid, this.selectedWarehouseUuid).subscribe({
      next: (res) => {
        this.isRunning = false;
        this.lastRun = res.result ?? null;
        const r = this.lastRun;
        this.messageService.add({
          severity: 'success', summary: 'Allocation run',
          detail: r ? `${r.demandsEvaluated} demand(s): held ${r.quantityReserved}, planned ${r.quantityPlanned}, short ${r.shortage}.` : 'Done.'
        });
        this.refresh();
      },
      error: (err) => {
        this.isRunning = false;
        this.messageService.add({ severity: 'error', summary: 'Not run', detail: err.error?.message || 'The allocation run failed.' });
      }
    });
  }

  // ── Release ───────────────────────────────────────────────────────────────

  openRelease(allocation: AllocationSummary): void {
    this.releaseTarget = allocation;
    this.releaseReason = '';
  }

  confirmRelease(): void {
    if (!this.releaseTarget || !this.releaseReason.trim()) return;
    this.isSaving = true;
    this.service.release(this.releaseTarget.uuid, this.releaseReason.trim()).subscribe({
      next: () => {
        this.isSaving = false;
        this.releaseTarget = null;
        this.messageService.add({ severity: 'success', summary: 'Released', detail: 'The allocation was released.' });
        this.refresh();
      },
      error: (err) => {
        this.isSaving = false;
        this.messageService.add({ severity: 'error', summary: 'Not released', detail: err.error?.message || 'The release failed.' });
      }
    });
  }

  // ── Move ──────────────────────────────────────────────────────────────────

  openMove(allocation: AllocationSummary): void {
    this.moveTarget = allocation;
    this.moveForm.reset({ toDemandUuid: null, quantity: allocation.allocatedQty - allocation.consumedQty, reason: '' });
  }

  confirmMove(): void {
    if (!this.moveTarget || this.moveForm.invalid) { this.moveForm.markAllAsTouched(); return; }
    const v = this.moveForm.value;
    this.isSaving = true;
    this.service.reallocate(this.moveTarget.uuid, v.toDemandUuid, v.quantity, v.reason.trim()).subscribe({
      next: () => {
        this.isSaving = false;
        this.moveTarget = null;
        this.messageService.add({ severity: 'success', summary: 'Moved', detail: 'The stock now belongs to the other demand.' });
        this.refresh();
      },
      error: (err) => {
        this.isSaving = false;
        this.messageService.add({ severity: 'error', summary: 'Not moved', detail: err.error?.message || 'The move failed.' });
      }
    });
  }

  // ── Cancel a demand ───────────────────────────────────────────────────────

  openCancel(demand: DemandAllocationSummary): void {
    this.cancelTarget = demand;
    this.cancelReason = '';
  }

  confirmCancel(): void {
    if (!this.cancelTarget || !this.cancelReason.trim()) return;
    this.isSaving = true;
    this.service.cancelDemand(this.cancelTarget.uuid, this.cancelReason.trim()).subscribe({
      next: () => {
        this.isSaving = false;
        this.cancelTarget = null;
        this.messageService.add({ severity: 'success', summary: 'Cancelled', detail: 'The demand was cancelled and its stock freed.' });
        this.refresh();
      },
      error: (err) => {
        this.isSaving = false;
        this.messageService.add({ severity: 'error', summary: 'Not cancelled', detail: err.error?.message || 'The cancellation failed.' });
      }
    });
  }

  // ── Register a demand by hand ─────────────────────────────────────────────

  openRegister(): void {
    this.registerForm.reset({
      demandType: 'SALES_ORDER', reference: '', requiredQty: null, requiredDate: null, priority: 1,
      warehouseUuid: this.selectedWarehouseUuid
    });
    this.registerVisible = true;
  }

  confirmRegister(): void {
    const variantUuid = this.variantUuid;
    if (!variantUuid || this.registerForm.invalid) { this.registerForm.markAllAsTouched(); return; }
    const v = this.registerForm.value;
    this.isSaving = true;
    this.service.registerDemand({
      demandType: v.demandType,
      demandUuid: crypto.randomUUID(),
      reference: v.reference.trim(),
      variantUuid,
      warehouseUuid: v.warehouseUuid ?? undefined,
      requiredQty: v.requiredQty,
      requiredDate: this.toIsoDate(v.requiredDate),
      priority: v.priority,
      allocate: true
    }).subscribe({
      next: (res) => {
        this.isSaving = false;
        this.registerVisible = false;
        const d = res.result;
        this.messageService.add({
          severity: 'success', summary: 'Registered',
          detail: d ? `${d.reference}: held ${d.reservedQty}, planned ${d.plannedQty}, short ${d.shortage}.` : 'The demand was registered.'
        });
        this.refresh();
      },
      error: (err) => {
        this.isSaving = false;
        this.messageService.add({ severity: 'error', summary: 'Not registered', detail: err.error?.message || 'The demand could not be registered.' });
      }
    });
  }

  // ── Rules ─────────────────────────────────────────────────────────────────

  onTabChange(index: number): void {
    this.activeTab = index;
    if (index === 1 && !this.rulesLoaded) this.loadRules();
  }

  loadRules(): void {
    this.isLoadingRules = true;
    this.service.getRules().subscribe({
      next: (res) => {
        this.isLoadingRules = false;
        this.rulesLoaded = true;
        this.rules = res.result ?? [];
        this.rulesDraft = this.rules.map(r => ({ ...r }));
      },
      error: () => {
        this.isLoadingRules = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'The rules could not be loaded.' });
      }
    });
  }

  addRule(): void {
    const next = (this.rulesDraft.length + 1) * 10;
    this.rulesDraft = [...this.rulesDraft, {
      ruleName: '', priorityOrder: next, demandTypeFilter: null, sortField: 'REQUIRED_DATE', sortDirection: 'ASC', isActive: true
    }];
  }

  removeRule(index: number): void {
    this.rulesDraft = this.rulesDraft.filter((_, i) => i !== index);
  }

  resetRules(): void {
    this.rulesDraft = [];
  }

  get rulesValid(): boolean {
    return this.rulesDraft.every(r => r.ruleName.trim().length > 0 && !!r.sortField);
  }

  saveRules(): void {
    if (!this.rulesValid) return;
    this.isSavingRules = true;
    this.service.setRules(this.rulesDraft.map(r => ({ ...r, ruleName: r.ruleName.trim() }))).subscribe({
      next: (res) => {
        this.isSavingRules = false;
        this.rules = res.result ?? [];
        this.rulesDraft = this.rules.map(r => ({ ...r }));
        this.messageService.add({
          severity: 'success', summary: 'Saved',
          detail: this.rules.length ? 'The priority rules were saved.' : 'Back to the default priority rules.'
        });
      },
      error: (err) => {
        this.isSavingRules = false;
        this.messageService.add({ severity: 'error', summary: 'Not saved', detail: err.error?.message || 'The rules could not be saved.' });
      }
    });
  }

  // ── Display ───────────────────────────────────────────────────────────────

  warehouseName(uuid: string | null): string {
    if (!uuid) return 'Any';
    return this.warehouses.find(w => w.uuid === uuid)?.name ?? uuid.slice(0, 8);
  }

  kindSeverity(kind: string): 'success' | 'info' | 'warn' | 'secondary' {
    switch (kind) {
      case 'RESERVED': return 'success';
      case 'FIRM':     return 'warn';
      case 'PLANNED':  return 'info';
      default:         return 'secondary';
    }
  }

  statusSeverity(status: string): 'success' | 'info' | 'warn' | 'danger' | 'secondary' {
    switch (status) {
      case 'ACTIVE': case 'OPEN': return 'success';
      case 'CONSUMED': case 'FULFILLED': return 'info';
      case 'RELEASED': return 'secondary';
      case 'CANCELLED': return 'danger';
      default: return 'secondary';
    }
  }

  prioritySeverity(priority: number): 'danger' | 'warn' | 'info' | 'secondary' {
    switch (priority) {
      case 3: return 'danger';
      case 2: return 'warn';
      case 1: return 'info';
      default: return 'secondary';
    }
  }

  private toIsoDate(value: Date | string): string {
    const d = value instanceof Date ? value : new Date(value);
    return new Date(Date.UTC(d.getFullYear(), d.getMonth(), d.getDate())).toISOString();
  }
}
