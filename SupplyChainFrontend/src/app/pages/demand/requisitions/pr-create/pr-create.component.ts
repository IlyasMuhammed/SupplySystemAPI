import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router, RouterModule, ActivatedRoute } from '@angular/router';
import { ReactiveFormsModule, FormBuilder, FormGroup, FormArray, Validators } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { CardModule } from 'primeng/card';
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
import { DemandService, CreatePrRequest } from '../../../../services/demand.service';
import { InventoryService, ProductListItemModel } from '../../../../services/inventory.service';
import { AttachmentListComponent } from '../../../../shared/attachment-list/attachment-list.component';

@Component({
  selector: 'app-pr-create',
  standalone: true,
  imports: [
    CommonModule, RouterModule, ReactiveFormsModule,
    ButtonModule, CardModule, InputTextModule, TextareaModule,
    InputNumberModule, CheckboxModule, DropdownModule,
    CalendarModule, DividerModule, ToastModule, TooltipModule, AttachmentListComponent
  ],
  templateUrl: './pr-create.component.html',
  styleUrls: ['./pr-create.component.scss'],
  providers: [MessageService]
})
export class PrCreateComponent implements OnInit {
  form!: FormGroup;
  isSubmitting = false;

  // Generated once per page load so attachments uploaded before save can be linked to the
  // eventual PR (which is created with this same UUID on submit).
  readonly prUuid = crypto.randomUUID();

  productOptions: { label: string; value: string }[] = [];
  private productsMap = new Map<string, ProductListItemModel>();
  loadingProducts = false;
  warehouseOptions: { label: string; value: string }[] = [];

  private prefillProductId: string | null = null;
  private prefillQty: number | null = null;
  private prefillProductName: string | null = null;

  priorityOptions = [
    { label: 'Low',    value: 'LOW' },
    { label: 'Medium', value: 'MEDIUM' },
    { label: 'High',   value: 'HIGH' },
    { label: 'Urgent', value: 'URGENT' }
  ];

  prTypeOptions = [
    { label: 'Standard',    value: 'STANDARD' },
    { label: 'Emergency',   value: 'EMERGENCY' },
    { label: 'Blanket',     value: 'BLANKET' },
    { label: 'Capital',     value: 'CAPITAL' }
  ];

  departmentOptions = [
    { label: 'Administration',       value: 'Administration' },
    { label: 'Finance',              value: 'Finance' },
    { label: 'Human Resources',      value: 'Human Resources' },
    { label: 'Information Technology', value: 'Information Technology' },
    { label: 'Legal',                value: 'Legal' },
    { label: 'Logistics',            value: 'Logistics' },
    { label: 'Management',           value: 'Management' },
    { label: 'Operations',           value: 'Operations' },
    { label: 'Procurement',          value: 'Procurement' },
    { label: 'Sales',                value: 'Sales' },
    { label: 'Warehouse',            value: 'Warehouse' },
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
    private fb: FormBuilder,
    private demandService: DemandService,
    private inventoryService: InventoryService,
    private messageService: MessageService,
    private router: Router,
    private route: ActivatedRoute
  ) {
    this.buildForm();
  }

  ngOnInit() {
    const p = this.route.snapshot.queryParams;
    if (p['productId']) {
      this.prefillProductId   = p['productId'];
      this.prefillQty         = p['qty'] ? Number(p['qty']) : null;
      this.prefillProductName = p['productName'] ?? null;
      if (this.prefillProductName) {
        this.form.patchValue({ prTitle: `Reorder: ${this.prefillProductName}` });
      }
    }
    this.loadProducts();
    this.loadWarehouses();
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
        if (this.prefillProductId && this.productsMap.has(this.prefillProductId)) {
          this.lines.at(0).patchValue({ productId: this.prefillProductId });
          this.onProductChange(0);
          if (this.prefillQty) {
            this.lines.at(0).patchValue({ quantity: this.prefillQty });
          }
        }
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
      prTitle:          ['', [Validators.required, Validators.minLength(3)]],
      department:       [''],
      requestedDate:    [new Date(), Validators.required],
      priority:         ['MEDIUM'],
      prType:           ['STANDARD'],
      requiresQuotation:[false],
      justification:    [''],
      warehouseUuid:    [null],
      notes:            [''],
      lines:            this.fb.array([this.newLine()])
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

  addLine()         { this.lines.push(this.newLine()); }
  removeLine(i: number) { if (this.lines.length > 1) this.lines.removeAt(i); }

  get estimatedTotal(): number {
    return this.lines.controls.reduce((sum, c) => {
      const qty = c.get('quantity')?.value ?? 0;
      const price = c.get('estimatedUnitPrice')?.value ?? 0;
      return sum + qty * price;
    }, 0);
  }

  onSubmit() {
    if (this.form.invalid) { this.form.markAllAsTouched(); return; }

    this.isSubmitting = true;
    const v = this.form.value;

    const req: CreatePrRequest = {
      prTitle:           v.prTitle,
      department:        v.department   || undefined,
      requestedDate:     v.requestedDate instanceof Date
                           ? v.requestedDate.toISOString()
                           : v.requestedDate,
      priority:          v.priority     || undefined,
      prType:            v.prType       || undefined,
      requiresQuotation: v.requiresQuotation ?? false,
      justification:     v.justification || undefined,
      warehouseUuid:     v.warehouseUuid || undefined,
      notes:             v.notes        || undefined,
      lines: v.lines.map((l: any) => ({
        productId:          l.productId       || undefined,
        itemDescription:    l.itemDescription,
        specification:      l.specification   || undefined,
        unitOfMeasure:      l.unitOfMeasure   || undefined,
        quantity:           l.quantity,
        estimatedUnitPrice: l.estimatedUnitPrice,
        requiresQuotation:  l.requiresQuotation ?? false,
        requiredDate:       l.requiredDate instanceof Date ? l.requiredDate.toISOString() : l.requiredDate || undefined,
        lineNotes:          l.lineNotes        || undefined,
        budgetCode:         l.budgetCode       || undefined
      })),
      prUuid: this.prUuid
    };

    this.demandService.createPr(req).subscribe({
      next: (res) => {
        this.isSubmitting = false;
        if (res.success) {
          this.messageService.add({ severity: 'success', summary: 'Created', detail: 'Requisition created successfully.' });
          setTimeout(() => this.router.navigate(['/portal/pages/demand/requisitions', res.result]), 1200);
        }
      },
      error: () => {
        this.isSubmitting = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to create requisition.' });
      }
    });
  }

  isInvalid(name: string): boolean {
    const c = this.form.get(name);
    return !!(c?.invalid && c.touched);
  }

  lineIsInvalid(i: number, name: string): boolean {
    const c = this.lines.at(i).get(name);
    return !!(c?.invalid && c.touched);
  }
}
