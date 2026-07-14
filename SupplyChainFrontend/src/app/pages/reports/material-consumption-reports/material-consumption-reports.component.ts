import { Component, OnInit } from '@angular/core';
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
  ReportsService,
  MaterialConsumptionReportFilter,
  MaterialConsumptionReportItem,
  ProjectConsumptionFilter,
  ProjectConsumptionItem,
  DepartmentConsumptionFilter,
  DepartmentConsumptionItem
} from '../../../services/reports.service';

@Component({
  selector: 'app-material-consumption-reports',
  standalone: true,
  imports: [CommonModule, FormsModule, TableModule, ButtonModule, InputTextModule,
            TabViewModule, TagModule, ToastModule, DropdownModule],
  templateUrl: './material-consumption-reports.component.html',
  styleUrls: ['./material-consumption-reports.component.scss'],
  providers: [MessageService]
})
export class MaterialConsumptionReportsComponent implements OnInit {
  // Tab 1 — Consumption per MIR
  consumptions: MaterialConsumptionReportItem[] = [];
  consumptionFilter: MaterialConsumptionReportFilter = {};
  isLoadingConsumption = false;

  // Tab 2 — Project Consumption
  projectItems: ProjectConsumptionItem[] = [];
  projectFilter: ProjectConsumptionFilter = {};
  isLoadingProject = false;

  // Tab 3 — Department Consumption
  deptItems: DepartmentConsumptionItem[] = [];
  deptFilter: DepartmentConsumptionFilter = {};
  isLoadingDept = false;

  txTypeOptions = [
    { label: 'All Types',  value: '' },
    { label: 'Issue',      value: 'ISSUE' },
    { label: 'Return',     value: 'RETURN' },
    { label: 'Wastage',    value: 'WASTAGE' }
  ];

  sourceTypeOptions = [
    { label: 'All Sources', value: '' },
    { label: 'Direct',      value: 'DIRECT' },
    { label: 'Manual',      value: 'MANUAL' }
  ];

  constructor(private svc: ReportsService, private msg: MessageService) {}

  ngOnInit(): void {
    this.loadConsumption();
    this.loadProject();
    this.loadDept();
  }

  // ── Consumption ────────────────────────────────────────────────────────────

  get totalIssued(): number   { return this.consumptions.reduce((s, i) => s + i.issuedQty, 0); }
  get totalConsumed(): number { return this.consumptions.reduce((s, i) => s + i.consumedQty, 0); }
  get totalBalance(): number  { return this.consumptions.reduce((s, i) => s + i.balanceQty, 0); }
  get totalBalVal(): number   { return this.consumptions.reduce((s, i) => s + i.balanceValue, 0); }

  getBalanceSeverity(qty: number): 'success' | 'danger' | 'warn' {
    if (qty === 0) return 'success';
    return qty < 0 ? 'danger' : 'warn';
  }

  loadConsumption(): void {
    this.isLoadingConsumption = true;
    this.svc.getMaterialConsumption(this.consumptionFilter).subscribe({
      next: r => { this.isLoadingConsumption = false; this.consumptions = r.success ? r.result : []; },
      error: () => { this.isLoadingConsumption = false; this.msg.add({ severity: 'error', summary: 'Error', detail: 'Failed to load consumption report.' }); }
    });
  }

  clearConsumption(): void { this.consumptionFilter = {}; this.loadConsumption(); }

  // ── Project ────────────────────────────────────────────────────────────────

  get projectTotal(): number { return this.projectItems.reduce((s, i) => s + i.amount, 0); }

  loadProject(): void {
    this.isLoadingProject = true;
    this.svc.getProjectConsumption(this.projectFilter).subscribe({
      next: r => { this.isLoadingProject = false; this.projectItems = r.success ? r.result : []; },
      error: () => { this.isLoadingProject = false; this.msg.add({ severity: 'error', summary: 'Error', detail: 'Failed to load project consumption.' }); }
    });
  }

  clearProject(): void { this.projectFilter = {}; this.loadProject(); }

  // ── Department ─────────────────────────────────────────────────────────────

  get deptTotal(): number { return this.deptItems.reduce((s, i) => s + i.amount, 0); }

  loadDept(): void {
    this.isLoadingDept = true;
    this.svc.getDepartmentConsumption(this.deptFilter).subscribe({
      next: r => { this.isLoadingDept = false; this.deptItems = r.success ? r.result : []; },
      error: () => { this.isLoadingDept = false; this.msg.add({ severity: 'error', summary: 'Error', detail: 'Failed to load department consumption.' }); }
    });
  }

  clearDept(): void { this.deptFilter = {}; this.loadDept(); }
}
