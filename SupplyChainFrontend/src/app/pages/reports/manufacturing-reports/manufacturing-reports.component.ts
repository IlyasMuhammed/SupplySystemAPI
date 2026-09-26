import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { TableModule } from 'primeng/table';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { TabViewModule } from 'primeng/tabview';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { DropdownModule } from 'primeng/dropdown';
import { MessageService } from 'primeng/api';
import {
  ManufacturingReportsService,
  ManufacturingReportFilter,
  ManufacturingRegisterFilter,
  ProductionEfficiencyReport,
  QualityScrapReport,
  ProductionMaterialIssueRegisterItem,
  FinishedGoodsReceiptRegisterItem,
  LedgerReconciliationReport,
  ChainedManufacturingReport,
  ChainedManufacturingNode
} from '../../../services/manufacturing-reports.service';

/** A tree node flattened to one row, indented by depth, for a plain p-table. */
interface ChainedRow {
  depth: number;
  node:  ChainedManufacturingNode;
}

@Component({
  selector: 'app-manufacturing-reports',
  standalone: true,
  imports: [CommonModule, FormsModule, TableModule, ButtonModule, InputTextModule,
            TabViewModule, TagModule, ToastModule, DropdownModule],
  templateUrl: './manufacturing-reports.component.html',
  styleUrls: ['./manufacturing-reports.component.scss'],
  providers: [MessageService]
})
export class ManufacturingReportsComponent {
  // Production Efficiency
  efficiency: ProductionEfficiencyReport | null = null;
  efficiencyFilter: ManufacturingReportFilter = {};
  isLoadingEfficiency = false;

  // Quality & Scrap
  qualityScrap: QualityScrapReport | null = null;
  qualityScrapFilter: ManufacturingReportFilter = {};
  isLoadingQualityScrap = false;

  // Material Issues
  issues: ProductionMaterialIssueRegisterItem[] = [];
  issuesFilter: ManufacturingRegisterFilter = {};
  isLoadingIssues = false;

  // Finished Goods Receipts
  fgrs: FinishedGoodsReceiptRegisterItem[] = [];
  fgrFilter: ManufacturingRegisterFilter = {};
  isLoadingFgr = false;

  // Ledger Reconciliation
  reconciliation: LedgerReconciliationReport | null = null;
  reconciliationFilter: ManufacturingReportFilter = {};
  isLoadingReconciliation = false;

  // Chained Manufacturing
  chainedOrderUuid = '';
  chained: ChainedManufacturingReport | null = null;
  chainedRows: ChainedRow[] = [];
  isLoadingChained = false;

  issueStatusOptions = [
    { label: 'All Statuses', value: '' },
    { label: 'Draft',        value: 'DRAFT' },
    { label: 'Confirmed',    value: 'CONFIRMED' },
    { label: 'Reversed',     value: 'REVERSED' }
  ];

  fgrStatusOptions = [
    { label: 'All Statuses', value: '' },
    { label: 'Draft',        value: 'DRAFT' },
    { label: 'Confirmed',    value: 'CONFIRMED' },
    { label: 'Reversed',     value: 'REVERSED' }
  ];

  constructor(private svc: ManufacturingReportsService, private msg: MessageService) {
    this.loadEfficiency();
    this.loadQualityScrap();
    this.loadIssues();
    this.loadFgrs();
    this.loadReconciliation();
  }

  private error(detail: string): void {
    this.msg.add({ severity: 'error', summary: 'Error', detail });
  }

  // ── Production Efficiency ─────────────────────────────────────────────────

  get statusBreakdown(): { status: string; count: number }[] {
    if (!this.efficiency) return [];
    return Object.entries(this.efficiency.countByStatus).map(([status, count]) => ({ status, count }));
  }

  loadEfficiency(): void {
    this.isLoadingEfficiency = true;
    this.svc.getProductionEfficiency(this.efficiencyFilter).subscribe({
      next: r => { this.isLoadingEfficiency = false; this.efficiency = r.success ? r.result : null; },
      error: () => { this.isLoadingEfficiency = false; this.error('Failed to load production efficiency.'); }
    });
  }

  clearEfficiency(): void { this.efficiencyFilter = {}; this.loadEfficiency(); }

  // ── Quality & Scrap ────────────────────────────────────────────────────────

  loadQualityScrap(): void {
    this.isLoadingQualityScrap = true;
    this.svc.getQualityScrap(this.qualityScrapFilter).subscribe({
      next: r => { this.isLoadingQualityScrap = false; this.qualityScrap = r.success ? r.result : null; },
      error: () => { this.isLoadingQualityScrap = false; this.error('Failed to load quality & scrap summary.'); }
    });
  }

  clearQualityScrap(): void { this.qualityScrapFilter = {}; this.loadQualityScrap(); }

  // ── Material Issues ────────────────────────────────────────────────────────

  getIssueStatusSeverity(s: string): 'success' | 'warn' | 'danger' {
    switch (s) {
      case 'CONFIRMED': return 'success';
      case 'REVERSED':  return 'danger';
      default:          return 'warn';
    }
  }

  loadIssues(): void {
    this.isLoadingIssues = true;
    this.svc.getMaterialIssueRegister(this.issuesFilter).subscribe({
      next: r => { this.isLoadingIssues = false; this.issues = r.success ? r.result.data : []; },
      error: () => { this.isLoadingIssues = false; this.error('Failed to load material issue register.'); }
    });
  }

  clearIssues(): void { this.issuesFilter = {}; this.loadIssues(); }

  // ── Finished Goods Receipts ────────────────────────────────────────────────

  getFgrStatusSeverity(s: string): 'success' | 'warn' | 'danger' {
    switch (s) {
      case 'CONFIRMED': return 'success';
      case 'REVERSED':  return 'danger';
      default:          return 'warn';
    }
  }

  loadFgrs(): void {
    this.isLoadingFgr = true;
    this.svc.getFinishedGoodsReceiptRegister(this.fgrFilter).subscribe({
      next: r => { this.isLoadingFgr = false; this.fgrs = r.success ? r.result.data : []; },
      error: () => { this.isLoadingFgr = false; this.error('Failed to load finished goods receipt register.'); }
    });
  }

  clearFgrs(): void { this.fgrFilter = {}; this.loadFgrs(); }

  // ── Ledger Reconciliation ──────────────────────────────────────────────────

  loadReconciliation(): void {
    this.isLoadingReconciliation = true;
    this.svc.getLedgerReconciliation(this.reconciliationFilter).subscribe({
      next: r => { this.isLoadingReconciliation = false; this.reconciliation = r.success ? r.result : null; },
      error: () => { this.isLoadingReconciliation = false; this.error('Failed to load ledger reconciliation.'); }
    });
  }

  clearReconciliation(): void { this.reconciliationFilter = {}; this.loadReconciliation(); }

  // ── Chained Manufacturing ──────────────────────────────────────────────────

  private flatten(node: ChainedManufacturingNode, rows: ChainedRow[]): void {
    rows.push({ depth: node.depth, node });
    for (const child of node.children) this.flatten(child, rows);
  }

  loadChained(): void {
    const uuid = this.chainedOrderUuid.trim();
    if (!uuid) { this.error('Enter a production order UUID first.'); return; }

    this.isLoadingChained = true;
    this.svc.getChainedManufacturing(uuid).subscribe({
      next: r => {
        this.isLoadingChained = false;
        this.chained = r.success ? r.result : null;
        this.chainedRows = [];
        if (this.chained) this.flatten(this.chained.root, this.chainedRows);
        if (!this.chained) this.error('That production order was not found.');
      },
      error: () => { this.isLoadingChained = false; this.chained = null; this.chainedRows = []; this.error('That production order was not found.'); }
    });
  }
}
