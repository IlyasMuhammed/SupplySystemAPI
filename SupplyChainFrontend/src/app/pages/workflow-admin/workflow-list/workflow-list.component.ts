import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TableModule } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { DropdownModule } from 'primeng/dropdown';
import { InputTextModule } from 'primeng/inputtext';
import { InputIconModule } from 'primeng/inputicon';
import { IconFieldModule } from 'primeng/iconfield';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { MessageService, ConfirmationService } from 'primeng/api';
import { TableLazyLoadEvent } from 'primeng/table';
import {
  WorkflowService,
  WorkflowDefinitionListItemModel,
  InterfaceSummaryDto
} from '../../../services/workflow.service';

@Component({
  selector: 'app-workflow-list',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    TableModule, ButtonModule, TagModule,
    ToastModule, TooltipModule, DropdownModule,
    InputTextModule, InputIconModule, IconFieldModule,
    ConfirmDialogModule
  ],
  templateUrl: './workflow-list.component.html',
  styleUrls: ['./workflow-list.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class WorkflowListComponent implements OnInit {
  definitions: WorkflowDefinitionListItemModel[] = [];
  summaries: InterfaceSummaryDto[] = [];
  totalRecords = 0;
  currentPage  = 1;
  pageSize     = 20;
  isLoading    = true;

  filterCode   = '';
  filterActive = '';

  activeFilterOptions = [
    { label: 'All', value: '' },
    { label: 'Active', value: 'true' },
    { label: 'Inactive', value: 'false' }
  ];

  interfaceCodeOptions: { label: string; value: string }[] = [];

  constructor(
    private wfService: WorkflowService,
    private messageService: MessageService,
    private confirmationService: ConfirmationService
  ) {}

  ngOnInit() {
    this.loadSummaries();
    this.load();
  }

  loadSummaries() {
    this.wfService.getInterfaceSummaries().subscribe({
      next: res => {
        if (res.success && res.result) {
          this.summaries = res.result;
          this.interfaceCodeOptions = [
            { label: 'All Interfaces', value: '' },
            ...res.result.map(s => ({ label: s.interfaceCode, value: s.interfaceCode }))
          ];
        }
      },
      error: () => {}
    });
  }

  load() {
    this.isLoading = true;
    const filter: any = {
      page: this.currentPage,
      pageSize: this.pageSize
    };
    if (this.filterCode)   filter.interfaceCode = this.filterCode;
    if (this.filterActive !== '') filter.isActive = this.filterActive === 'true';

    this.wfService.getDefinitions(filter).subscribe({
      next: res => {
        this.isLoading = false;
        if (res.success && res.result) {
          this.definitions  = res.result.data ?? [];
          this.totalRecords = res.result.totalRecords ?? 0;
        } else {
          this.definitions  = [];
          this.totalRecords = 0;
        }
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load workflow definitions.' });
      }
    });
  }

  onFilterChange() { this.currentPage = 1; this.load(); }
  resetFilters()   { this.filterCode = ''; this.filterActive = ''; this.currentPage = 1; this.load(); }

  onPageChange(e: TableLazyLoadEvent) {
    const rows = e.rows ?? this.pageSize;
    this.currentPage = Math.floor((e.first ?? 0) / rows) + 1;
    this.pageSize = rows;
    this.load();
  }

  confirmToggle(def: WorkflowDefinitionListItemModel) {
    const action = def.isActive ? 'deactivate' : 'activate';
    this.confirmationService.confirm({
      message: `Are you sure you want to ${action} "${def.name}"?`,
      header: `${action.charAt(0).toUpperCase() + action.slice(1)} Workflow`,
      icon: 'pi pi-exclamation-triangle',
      accept: () => this.toggleActive(def)
    });
  }

  toggleActive(def: WorkflowDefinitionListItemModel) {
    const obs = def.isActive
      ? this.wfService.deactivateDefinition(def.uuid)
      : this.wfService.activateDefinition(def.uuid);

    obs.subscribe({
      next: res => {
        if (res.success) {
          this.messageService.add({
            severity: 'success', summary: 'Success',
            detail: def.isActive ? 'Workflow deactivated.' : 'Workflow activated.'
          });
          this.load();
        }
      },
      error: () => this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Operation failed.' })
    });
  }

  get activeCount()   { return this.summaries.filter(s => s.hasActiveWorkflow).length; }
  get inactiveCount() { return this.summaries.filter(s => !s.hasActiveWorkflow).length; }

  getConditionLabel(def: WorkflowDefinitionListItemModel): string {
    if (!def.conditionField) return '—';
    const op = def.conditionOperator ?? '';
    if (op === 'BETWEEN') return `${def.conditionField} BETWEEN ${def.conditionValueMin} – ${def.conditionValueMax}`;
    return `${def.conditionField} ${op} ${def.conditionValue}`;
  }

  getSeverity(isActive: boolean): 'success' | 'danger' {
    return isActive ? 'success' : 'danger';
  }
}
