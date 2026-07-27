import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { ReactiveFormsModule, FormBuilder, FormGroup, FormArray, Validators } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { InputNumberModule } from 'primeng/inputnumber';
import { CheckboxModule } from 'primeng/checkbox';
import { DropdownModule } from 'primeng/dropdown';
import { CalendarModule } from 'primeng/calendar';
import { DividerModule } from 'primeng/divider';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';
import { DemandService, PatchPrRequest } from '../../../../services/demand.service';
import { InventoryService, ProductListItemModel } from '../../../../services/inventory.service';

@Component({
  selector: 'app-pr-edit',
  standalone: true,
  imports: [
    CommonModule, RouterModule, ReactiveFormsModule,
    ButtonModule, InputTextModule, TextareaModule,
    InputNumberModule, CheckboxModule, DropdownModule,
    CalendarModule, DividerModule, ToastModule, TooltipModule
  ],
  templateUrl: './pr-edit.component.html',
  styleUrls: ['./pr-edit.component.scss'],
  providers: [MessageService]
})
export class PrEditComponent implements OnInit {
  uuid = '';
  form!: FormGroup;
  isLoading = true;
  isSubmitting = false;
  minDate = new Date();

  productOptions: { label: string; value: string }[] = [];
  private productsMap = new Map<string, ProductListItemModel>();
  loadingProducts = false;
  warehouseOptions: { label: string; value: string }[] = [];

  priorityOptions = [
    { label: 'Low',    value: 'LOW' },
    { label: 'Medium', value: 'MEDIUM' },
    { label: 'High',   value: 'HIGH' },
    { label: 'Urgent', value: 'URGENT' }
  ];

  prTypeOptions = [
    { label: 'Standard',  value: 'STANDARD' },
    { label: 'Emergency', value: 'EMERGENCY' },
    { label: 'Blanket',   value: 'BLANKET' },
    { label: 'Capital',   value: 'CAPITAL' }
  ];

  uomOptions = [
    { label: 'Each (EA)',    value: 'EA'   },
    { label: 'Kilogram (KG)', value: 'KG'  },
    { label: 'Litre (LTR)',  value: 'LTR'  },
    { label: 'Box (BOX)',    value: 'BOX'  },
    { label: 'Set (SET)',    value: 'SET'  },
    { label: 'Meter (MTR)',  value: 'MTR'  },
    { label: 'Packet (PKT)', value: 'PKT'  },
    { label: 'Pair (PAIR)',  value: 'PAIR' }
  ];

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private fb: FormBuilder,
    private demandService: DemandService,
    private inventoryService: InventoryService,
    private messageService: MessageService
  ) {}

  ngOnInit() {
    this.buildForm();
    this.loadProducts();
    this.loadWarehouses();
    this.route.params.subscribe(p => { this.uuid = p['uuid']; this.load(); });
  }

  private loadWarehouses() {
    this.inventoryService.getWarehouses().subscribe({
      next: res => {
        const data = res?.result ?? [];
        this.warehouseOptions = [
          { label: '— None —', value: '' },
          ...data.map(w => ({ label: w.name, value: w.uuid }))
        ];
      },
      error: () => {}
    });
  }

  private loadProducts() {
    this.loadingProducts = true;
    this.inventoryService.getProducts({ activeOnly: true, pageSize: 500 }).subscribe({
      next: res => {
        this.loadingProducts = false;
        const data = res?.result?.data ?? [];
        this.productsMap.clear();
        this.productOptions = data.map(p => {
          this.productsMap.set(p.uuid, p);
          return { label: p.name, value: p.uuid };
        });
      },
      error: () => { this.loadingProducts = false; }
    });
  }

  onProductChange(i: number) {
    const uuid = this.lines.at(i).get('productId')?.value;
    if (!uuid) return;
    const p = this.productsMap.get(uuid);
    if (!p) return;
    this.lines.at(i).patchValue({
      itemDescription:    p.name,
      specification:      (p as any).description ?? '',
      unitOfMeasure:      p.uomCode || null,
      estimatedUnitPrice: p.unitCost ?? 0
    });
  }

  private buildForm() {
    this.form = this.fb.group({
      prTitle:           ['', [Validators.required, Validators.minLength(3)]],
      department:        [''],
      requestedDate:     [null, Validators.required],
      priority:          ['MEDIUM'],
      prType:            ['STANDARD'],
      requiresQuotation: [false],
      justification:     [''],
      warehouseUuid:     [null],
      notes:             [''],
      lines:             this.fb.array([this.newLine()])
    });
  }

  get lines(): FormArray { return this.form.get('lines') as FormArray; }

  newLine(): FormGroup {
    return this.fb.group({
      productId:          [null],
      itemDescription:    ['', Validators.required],
      specification:      [''],
      unitOfMeasure:      [null],
      quantity:           [1, [Validators.required, Validators.min(0.0001), Validators.max(999999)]],
      estimatedUnitPrice: [0, [Validators.required, Validators.min(0)]],
      requiresQuotation:  [false],
      requiredDate:       [null],
      lineNotes:          [''],
      budgetCode:         ['']
    });
  }

  addLine()             { this.lines.push(this.newLine()); }
  removeLine(i: number) { if (this.lines.length > 1) this.lines.removeAt(i); }

  get estimatedTotal(): number {
    return this.lines.controls.reduce((s, c) =>
      s + (c.get('quantity')?.value ?? 0) * (c.get('estimatedUnitPrice')?.value ?? 0), 0);
  }

  load() {
    this.isLoading = true;
    this.demandService.getPrById(this.uuid).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (!res.success || !res.result) {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Requisition not found.' });
          return;
        }
        const pr = res.result;
        if (pr.status !== 'DRAFT') {
          this.messageService.add({ severity: 'warn', summary: 'Read-only', detail: `Only DRAFT requisitions can be edited. Status: ${pr.status}` });
          setTimeout(() => this.router.navigate(['/portal/pages/demand/requisitions', this.uuid]), 1500);
          return;
        }

        while (this.lines.length) this.lines.removeAt(0);
        (pr.lines ?? []).forEach(l => this.lines.push(this.fb.group({
          productId:          [l.productId ?? null],
          itemDescription:    [l.itemDescription, Validators.required],
          specification:      [l.specification ?? ''],
          unitOfMeasure:      [l.unitOfMeasure ?? null],
          quantity:           [l.quantity, [Validators.required, Validators.min(0.0001), Validators.max(999999)]],
          estimatedUnitPrice: [l.estimatedUnitPrice, [Validators.required, Validators.min(0)]],
          requiresQuotation:  [l.requiresQuotation],
          requiredDate:       [l.requiredDate ? new Date(l.requiredDate) : null],
          lineNotes:          [l.lineNotes ?? ''],
          budgetCode:         [l.budgetCode ?? '']
        })));
        if (!this.lines.length) this.lines.push(this.newLine());

        this.form.patchValue({
          prTitle:           pr.prTitle,
          department:        pr.department ?? '',
          requestedDate:     pr.requestedDate ? new Date(pr.requestedDate) : null,
          priority:          pr.priority ?? 'MEDIUM',
          prType:            pr.prType ?? 'STANDARD',
          requiresQuotation: pr.requiresQuotation,
          justification:     pr.justification ?? '',
          warehouseUuid:     pr.warehouseUuid ?? null,
          notes:             pr.notes ?? ''
        });
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load requisition.' });
      }
    });
  }

  onSubmit() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }
    this.isSubmitting = true;
    const v = this.form.getRawValue();
    const req: PatchPrRequest = {
      prTitle:           v.prTitle,
      department:        v.department   || undefined,
      requestedDate:     v.requestedDate instanceof Date ? v.requestedDate.toISOString() : v.requestedDate,
      priority:          v.priority     || undefined,
      prType:            v.prType       || undefined,
      requiresQuotation: v.requiresQuotation ?? false,
      justification:     v.justification || undefined,
      warehouseUuid:     v.warehouseUuid || undefined,
      clearWarehouse:    !v.warehouseUuid,
      notes:             v.notes        || undefined,
      lines: v.lines.map((l: any) => ({
        productId:          l.productId          || undefined,
        itemDescription:    l.itemDescription,
        specification:      l.specification      || undefined,
        unitOfMeasure:      l.unitOfMeasure      || undefined,
        quantity:           l.quantity,
        estimatedUnitPrice: l.estimatedUnitPrice,
        requiresQuotation:  l.requiresQuotation  ?? false,
        requiredDate:       l.requiredDate instanceof Date ? l.requiredDate.toISOString() : l.requiredDate || undefined,
        lineNotes:          l.lineNotes           || undefined,
        budgetCode:         l.budgetCode          || undefined
      }))
    };

    this.demandService.patchPr(this.uuid, req).subscribe({
      next: () => {
        this.isSubmitting = false;
        this.messageService.add({ severity: 'success', summary: 'Saved', detail: 'Requisition updated.' });
        setTimeout(() => this.router.navigate(['/portal/pages/demand/requisitions', this.uuid]), 1200);
      },
      error: (err) => {
        this.isSubmitting = false;
        const detail = err?.error?.message ?? `Failed to update requisition. (HTTP ${err?.status ?? 0})`;
        this.messageService.add({ severity: 'error', summary: 'Error', detail });
      }
    });
  }

  isInvalid(name: string): boolean {
    const c = this.form.get(name); return !!(c?.invalid && c.touched);
  }
  lineIsInvalid(i: number, name: string): boolean {
    const c = this.lines.at(i).get(name); return !!(c?.invalid && c.touched);
  }
}
