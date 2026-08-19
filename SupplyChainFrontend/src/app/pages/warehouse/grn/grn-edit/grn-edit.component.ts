import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { ReactiveFormsModule, FormsModule, FormBuilder, FormGroup, Validators } from '@angular/forms';
import { forkJoin, of, Observable } from 'rxjs';
import { switchMap } from 'rxjs/operators';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { CalendarModule } from 'primeng/calendar';
import { DropdownModule } from 'primeng/dropdown';
import { TextareaModule } from 'primeng/textarea';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';
import { WarehouseService, GrnDetailModel, PatchGrnRequest } from '../../../../services/warehouse.service';
import { InventoryService, WarehouseModel, ProductListItemModel } from '../../../../services/inventory.service';
import { ProductVariantPickerComponent, VariantPickerSelection } from '../../../../shared/product-variant-picker/product-variant-picker.component';

@Component({
  selector: 'app-grn-edit',
  standalone: true,
  imports: [
    CommonModule, RouterModule, ReactiveFormsModule, FormsModule,
    ButtonModule, InputTextModule, CalendarModule,
    DropdownModule, TextareaModule, ToastModule, ProductVariantPickerComponent
  ],
  templateUrl: './grn-edit.component.html',
  styleUrls: ['./grn-edit.component.scss'],
  providers: [MessageService]
})
export class GrnEditComponent implements OnInit {
  uuid = '';
  grn: GrnDetailModel | null = null;
  form!: FormGroup;
  isLoading = true;
  isSubmitting = false;
  warehouseOptions: { label: string; value: string }[] = [];
  products: ProductListItemModel[] = [];
  // Per-line current selection (product + resolved variant), seeded from the loaded GRN and
  // updated as the user re-links lines via the two-level picker.
  lineSelections: (VariantPickerSelection | null)[] = [];

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private fb: FormBuilder,
    private warehouseService: WarehouseService,
    private inventoryService: InventoryService,
    private messageService: MessageService
  ) {}

  ngOnInit() {
    this.buildForm();
    this.inventoryService.getWarehouses().subscribe({
      next: (res) => {
        const active = (res.result ?? []).filter((w: WarehouseModel) => w.isActive);
        this.warehouseOptions = active.map((w: WarehouseModel) => ({ label: `${w.name} (${w.code})`, value: w.uuid }));
      },
      error: () => {}
    });
    // Deliberately not filtered to activeOnly: a line already linked to a product that has since
    // been deactivated must still show that product here, or the picker looks blank/broken even
    // though the link is genuinely still in place.
    this.inventoryService.getProducts({ pageSize: 500 }).subscribe({
      next: res => { this.products = res?.result?.data ?? []; },
      error: () => {}
    });
    this.route.params.subscribe(p => { this.uuid = p['uuid']; this.load(); });
  }

  private buildForm() {
    this.form = this.fb.group({
      warehouseUuid:  ['', Validators.required],
      receivedAt:     [null, Validators.required],
      deliveryNoteNo: [''],
      vehicleNo:      [''],
      driverName:     [''],
      invoiceNo:      [''],
      notes:          ['']
    });
  }

  load() {
    this.isLoading = true;
    this.warehouseService.getGrnById(this.uuid).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (!res.success || !res.result) {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: 'GRN not found.' });
          return;
        }
        this.grn = res.result;
        if (this.grn.status !== 'DRAFT') {
          this.messageService.add({ severity: 'warn', summary: 'Read-only', detail: `Only DRAFT GRNs can be edited. Status: ${this.grn.status}` });
          setTimeout(() => this.router.navigate(['/portal/pages/warehouse/grn', this.uuid]), 1500);
          return;
        }
        this.lineSelections = (this.grn.lines ?? []).map(l => l.variantUuid ? {
          productUuid:   l.productUuid ?? null,
          productName:   l.productName ?? null,
          variantId:     null,
          variantUuid:   l.variantUuid,
          variantSku:    l.variantSku ?? null,
          variantName:   l.variantName ?? null,
          purchasePrice: null,
          uomCode:       null
        } : null);
        this.form.patchValue({
          warehouseUuid:  this.grn.warehouseUuid  ?? '',
          receivedAt:     new Date(this.grn.receivedAt),
          deliveryNoteNo: this.grn.deliveryNoteNo ?? '',
          vehicleNo:      this.grn.vehicleNo      ?? '',
          driverName:     this.grn.driverName     ?? '',
          invoiceNo:      this.grn.invoiceNo      ?? '',
          notes:          this.grn.notes          ?? ''
        });
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load GRN.' });
      }
    });
  }

  onLineVariantSelected(i: number, sel: VariantPickerSelection) {
    this.lineSelections[i] = sel.variantUuid ? sel : null;
  }

  onSubmit() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.isSubmitting = true;
    const v = this.form.getRawValue();
    const req: PatchGrnRequest = {
      warehouseUuid:      v.warehouseUuid  || undefined,
      receivedAt:         v.receivedAt instanceof Date ? v.receivedAt.toISOString() : v.receivedAt || undefined,
      deliveryNoteNo:     v.deliveryNoteNo || undefined,
      vehicleNo:          v.vehicleNo      || undefined,
      driverName:         v.driverName     || undefined,
      invoiceNo:          v.invoiceNo      || undefined,
      notes:              v.notes          || undefined
    };

    // Save any variant links that changed on individual lines
    const lineUpdates = (this.grn?.lines ?? [])
      .map((line, i) => {
        const newVariantUuid = this.lineSelections[i]?.variantUuid ?? null;
        if (newVariantUuid && newVariantUuid !== line.variantUuid) {
          return this.warehouseService.updateGrnLine(this.uuid, line.uuid, {
            variantUuid:     newVariantUuid,
            qtyReceived:     line.qtyReceived,
            qtyAccepted:     line.qtyAccepted,
            qtyRejected:     line.qtyRejected,
            rejectionReason: line.rejectionReason ?? undefined
          });
        }
        return null;
      })
      .filter((obs): obs is NonNullable<typeof obs> => obs !== null);

    const lineUpdates$: Observable<unknown> = lineUpdates.length > 0 ? forkJoin(lineUpdates) : of(null);

    lineUpdates$.pipe(
      switchMap(() => this.warehouseService.patchGrn(this.uuid, req))
    ).subscribe({
      next: () => {
        this.isSubmitting = false;
        this.messageService.add({ severity: 'success', summary: 'Saved', detail: 'GRN updated successfully.' });
        setTimeout(() => this.router.navigate(['/portal/pages/warehouse/grn', this.uuid]), 1200);
      },
      error: (err: any) => {
        this.isSubmitting = false;
        const detail = err?.error?.message ?? `Failed to update GRN. (HTTP ${err?.status ?? 0})`;
        this.messageService.add({ severity: 'error', summary: 'Error', detail });
      }
    });
  }

  isInvalid(name: string): boolean {
    const c = this.form.get(name); return !!(c?.invalid && c.touched);
  }
}
