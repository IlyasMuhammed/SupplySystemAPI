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
import { RadioButtonModule } from 'primeng/radiobutton';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';
import { WarehouseService, GrnDetailModel, PatchGrnRequest } from '../../../../services/warehouse.service';
import { InventoryService, WarehouseModel } from '../../../../services/inventory.service';

@Component({
  selector: 'app-grn-edit',
  standalone: true,
  imports: [
    CommonModule, RouterModule, ReactiveFormsModule, FormsModule,
    ButtonModule, InputTextModule, CalendarModule,
    DropdownModule, TextareaModule, RadioButtonModule, ToastModule
  ],
  templateUrl: './grn-edit.component.html',
  styleUrls: ['./grn-edit.component.scss'],
  providers: [MessageService]
})
export class GrnEditComponent implements OnInit {
  uuid = '';
  grn: GrnDetailModel | null = null;
  form!: FormGroup;
  requiresInspection = true;
  isLoading = true;
  isSubmitting = false;
  warehouseOptions: { label: string; value: string }[] = [];
  productOptions: { label: string; value: string }[] = [];
  lineProductUuids: (string | null)[] = [];

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
    this.inventoryService.getProducts({ activeOnly: true, pageSize: 500 }).subscribe({
      next: res => {
        const data = res?.result?.data ?? [];
        this.productOptions = data.map(p => ({ label: p.name, value: p.uuid }));
      },
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
        this.requiresInspection = this.grn.requiresInspection;
        this.lineProductUuids = (this.grn.lines ?? []).map(l => l.productUuid ?? null);
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
      notes:              v.notes          || undefined,
      requiresInspection: this.requiresInspection
    };

    // Save any product links that changed on individual lines
    const lineUpdates = (this.grn?.lines ?? [])
      .map((line, i) => {
        const newUuid = this.lineProductUuids[i];
        if (newUuid && newUuid !== line.productUuid) {
          return this.warehouseService.updateGrnLine(this.uuid, line.uuid, {
            productUuid:     newUuid,
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
