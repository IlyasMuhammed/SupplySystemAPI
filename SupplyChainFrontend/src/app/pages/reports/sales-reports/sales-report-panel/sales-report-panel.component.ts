import { Component, Input, LOCALE_ID, OnChanges, SimpleChanges, inject } from '@angular/core';
import { CommonModule, formatDate, formatNumber } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TableModule, TableLazyLoadEvent } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { CalendarModule } from 'primeng/calendar';
import { DropdownModule } from 'primeng/dropdown';
import { AutoCompleteModule, AutoCompleteCompleteEvent } from 'primeng/autocomplete';
import { ChartModule } from 'primeng/chart';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';

import {
  SalesReportsService, SalesReportFormat, SalesReportParams, SalesReportResult
} from '../../../../services/sales-reports.service';
import { BusinessPartnerService, BusinessPartnerModel } from '../../../../services/business-partner.service';
import { InventoryService, ProductListItemModel } from '../../../../services/inventory.service';
import { AuthService } from '../../../service/auth.service';
import { formatCode } from '../../../../shared/format-code';
import { toDateOnly } from '../../../../shared/date-only';
import { Severity } from '../../../finance/receivables/receivables.shared';
import { ChartSpec } from '../sales-report-charts';
import { ColumnDef, FilterDef, ReportDef, SectionDef } from '../sales-report-definitions';

type Option = { label: string; value: any };

/** One report: its filters, the run, its tables and charts, and its downloads. Which report it is comes from the definition. */
@Component({
  selector: 'app-sales-report-panel',
  standalone: true,
  imports: [
    CommonModule, FormsModule, ButtonModule, TableModule, TagModule, CalendarModule, DropdownModule,
    AutoCompleteModule, ChartModule, ToastModule, TooltipModule
  ],
  templateUrl: './sales-report-panel.component.html',
  styleUrls: ['./sales-report-panel.component.scss'],
  providers: [MessageService]
})
export class SalesReportPanelComponent implements OnChanges {
  @Input({ required: true }) definition!: ReportDef;

  private locale = inject(LOCALE_ID);

  /** What the filters hold, by filter id. */
  values: Record<string, any> = {};

  report: SalesReportResult | null = null;
  charts: ChartSpec[] = [];
  isLoading = false;
  page = 1;
  pageSize = 20;
  exporting: SalesReportFormat | null = null;

  customerSuggestions: BusinessPartnerModel[] = [];
  productOptions: Option[] = [];
  variantOptions: Option[] = [];
  warehouseOptions: Option[] = [];

  constructor(
    private reports: SalesReportsService,
    private partners: BusinessPartnerService,
    private inventory: InventoryService,
    public authService: AuthService,
    private messageService: MessageService
  ) {}

  ngOnChanges(changes: SimpleChanges) {
    if (changes['definition']) this.reset();
  }

  // ── Starting a report ───────────────────────────────────────────────────────

  /** A different report: its filters start over, and it runs at once if it needs nothing from the user. */
  private reset() {
    this.values = {};
    for (const f of this.definition.filters) this.values[f.id] = f.defaultValue ?? (f.kind === 'select' || f.kind === 'warehouse' ? '' : null);

    this.report = null;
    this.charts = [];
    this.isLoading = false;
    this.page = 1;
    this.exporting = null;
    this.variantOptions = [];

    this.loadLookups();
    if (!this.missingRequired()) this.run();
  }

  /** The lists some filters choose from, fetched when the report has such a filter. */
  private loadLookups() {
    const kinds = this.definition.filters.map(f => f.kind);
    const key = this.definition.key;

    if (kinds.includes('product')) {
      this.inventory.getProducts({ pageSize: 500 }).subscribe({
        next: (res) => {
          if (this.definition.key !== key) return;
          this.productOptions = (res.result?.data ?? []).map(p => ({ label: `${p.name} (${p.sku})`, value: p }));
        },
        error: () => this.messageService.add({ severity: 'error', summary: 'Error', detail: 'The product list could not be loaded.' })
      });
    }

    if (kinds.includes('warehouse')) {
      this.inventory.getWarehouses().subscribe({
        next: (res) => {
          if (this.definition.key !== key) return;
          this.warehouseOptions = [{ label: 'All', value: '' }, ...(res.result ?? []).map(w => ({ label: w.name, value: w.uuid }))];
        },
        error: () => { this.warehouseOptions = [{ label: 'All', value: '' }]; }
      });
    }
  }

  // ── Filters ─────────────────────────────────────────────────────────────────

  searchCustomers(event: AutoCompleteCompleteEvent) {
    this.partners.getPartners({ isCustomer: true, active: true, search: event.query, pageSize: 20 }).subscribe({
      next: (res) => { this.customerSuggestions = res.result?.data ?? []; },
      error: () => { this.customerSuggestions = []; }
    });
  }

  /** A different product has different variants, so the one chosen among the old ones no longer applies. */
  onProductChange(product: ProductListItemModel | null) {
    this.values['productId'] = product;
    this.values['variantId'] = null;
    this.variantOptions = [];
    if (!product) return;

    this.inventory.getProductById(product.id).subscribe({
      next: (res) => {
        if (this.values['productId'] !== product) return;
        this.variantOptions = [
          { label: 'All variants', value: null },
          ...(res.result?.variants ?? []).map(v => ({ label: v.variantName ? `${v.variantName} (${v.sku})` : v.sku, value: v.uuid }))
        ];
      },
      error: () => { this.variantOptions = []; }
    });
  }

  /** The options of a select, or of the warehouse list, whichever the filter is. */
  optionsOf(filter: FilterDef): Option[] {
    return filter.kind === 'warehouse' ? this.warehouseOptions : filter.options ?? [];
  }

  /** What the server is asked for: only what was filled in, in the form it reads. */
  buildParams(): SalesReportParams {
    const params: SalesReportParams = {};

    for (const f of this.definition.filters) {
      const v = this.values[f.id];
      if (v === null || v === undefined || v === '') continue;

      switch (f.kind) {
        case 'dateFrom': case 'dateTo': case 'asOf':
          params[f.id] = toDateOnly(v as Date);
          break;
        case 'customer':
        case 'product':
          if (v && typeof v === 'object' && v.uuid) params[f.id] = v.uuid;
          break;
        default:
          params[f.id] = String(v);
      }
    }

    return params;
  }

  private missingRequired(): FilterDef | undefined {
    return this.definition.filters.find(f => {
      if (!f.required) return false;
      const v = this.values[f.id];
      return f.kind === 'customer' || f.kind === 'product' ? !(v && typeof v === 'object' && v.uuid) : !v;
    });
  }

  /** The first thing wrong with the filters, in words. */
  problem(): string | null {
    const missing = this.missingRequired();
    if (missing) return `Choose the ${missing.label.toLowerCase()} to run this report.`;

    const from = this.values['dateFrom'] as Date | null;
    const to = this.values['dateTo'] as Date | null;
    if (from && to && toDateOnly(from) > toDateOnly(to)) return 'The start date is after the end date.';

    return null;
  }

  // ── Running ─────────────────────────────────────────────────────────────────

  run() {
    const problem = this.problem();
    if (problem) {
      this.messageService.add({ severity: 'warn', summary: 'Check the filters', detail: problem });
      return;
    }
    this.page = 1;
    this.load();
  }

  private load() {
    const key = this.definition.key;
    this.isLoading = true;

    this.reports.getReport(key, { ...this.buildParams(), page: this.page, pageSize: this.pageSize }).subscribe({
      next: (res) => {
        // The user has moved on to another report since this was asked for.
        if (this.definition.key !== key) return;
        this.isLoading = false;
        this.report = res.success && res.result ? res.result : null;
        this.charts = this.report && this.definition.charts ? this.definition.charts(this.report) : [];
      },
      error: (err) => {
        if (this.definition.key !== key) return;
        this.isLoading = false;
        this.report = null;
        this.charts = [];
        this.messageService.add({
          severity: 'error', summary: 'Report failed', detail: err?.error?.message ?? 'The report could not be loaded.'
        });
      }
    });
  }

  onPageChange(event: TableLazyLoadEvent) {
    this.page = Math.floor((event.first ?? 0) / (event.rows ?? this.pageSize)) + 1;
    this.pageSize = event.rows ?? this.pageSize;
    if (this.report) this.load();
  }

  // ── Downloading ─────────────────────────────────────────────────────────────

  get canExport(): boolean {
    return this.definition.exportPermissions.every(p => this.authService.hasPermission(p));
  }

  /** The whole report, as a document. The server ignores the paging and carries every row. */
  export(format: SalesReportFormat) {
    if (!this.canExport || this.exporting) return;

    const problem = this.problem();
    if (problem) {
      this.messageService.add({ severity: 'warn', summary: 'Check the filters', detail: problem });
      return;
    }

    this.exporting = format;
    const name = `${this.definition.key}-${toDateOnly(new Date()).replace(/-/g, '')}.${format === 'pdf' ? 'pdf' : 'xlsx'}`;

    this.reports.download(this.definition.key, format, this.buildParams()).subscribe({
      next: (blob) => {
        this.exporting = null;
        this.save(blob, name);
      },
      error: async (err) => {
        this.exporting = null;
        this.messageService.add({
          severity: 'error', summary: format === 'pdf' ? 'PDF not downloaded' : 'Excel not downloaded',
          detail: await this.messageOf(err, 'The download failed.')
        });
      }
    });
  }

  private save(blob: Blob, fileName: string) {
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    link.click();
    URL.revokeObjectURL(url);
  }

  /** A failed download comes back as bytes, so the server's reason has to be read out of them. */
  private async messageOf(err: any, fallback: string): Promise<string> {
    const body = err?.error;
    if (body instanceof Blob) {
      try {
        const message = JSON.parse(await body.text())?.message;
        if (message) return message;
      } catch { /* not the server's JSON: the fallback will do */ }
      return fallback;
    }
    return body?.message ?? fallback;
  }

  // ── What is shown ───────────────────────────────────────────────────────────

  /** Sections that have something to show: a report's own list always does, once the report has run. */
  get sections(): SectionDef[] {
    return this.definition.sections.filter(s => s.paged || this.rowsOf(s).length > 0);
  }

  rowsOf(section: SectionDef): any[] {
    return (this.report ? section.rows(this.report) : null) ?? [];
  }

  headerOf(column: ColumnDef): string {
    return typeof column.header === 'function' ? column.header(this.report) : column.header;
  }

  isNumber(column: ColumnDef): boolean {
    return ['int', 'qty', 'money', 'cost', 'percent'].includes(column.kind ?? 'text');
  }

  /** A cell as words and figures; a dash where the report has nothing to say. */
  cell(row: any, column: ColumnDef): string {
    const v = row?.[column.field];
    if (v === null || v === undefined || v === '') return '—';

    switch (column.kind) {
      case 'int':     return formatNumber(Number(v), this.locale, '1.0-0');
      case 'qty':     return formatNumber(Number(v), this.locale, '1.0-4');
      case 'money':   return formatNumber(Number(v), this.locale, '1.2-2');
      case 'cost':    return formatNumber(Number(v), this.locale, '1.2-4');
      case 'percent': return `${formatNumber(Number(v), this.locale, '1.1-1')}%`;
      case 'date':    return formatDate(v, 'dd MMM yyyy', this.locale);
      case 'code':
      case 'tag':     return formatCode(String(v));
      default:        return String(v);
    }
  }

  severity(row: any, column: ColumnDef): Severity {
    return column.severity?.[row?.[column.field]] ?? 'secondary';
  }

  get generatedAt(): string | null { return this.report?.generatedAt ?? null; }
}
