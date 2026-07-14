import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { TableModule } from 'primeng/table';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { DropdownModule } from 'primeng/dropdown';
import { MessageService } from 'primeng/api';
import {
  ReportsService,
  MaterialIssueRegisterFilter,
  MaterialIssueRegisterItem
} from '../../../services/reports.service';

@Component({
  selector: 'app-material-issue-register',
  standalone: true,
  imports: [CommonModule, FormsModule, TableModule, ButtonModule, InputTextModule, TagModule, ToastModule, DropdownModule],
  templateUrl: './material-issue-register.component.html',
  styleUrls: ['./material-issue-register.component.scss'],
  providers: [MessageService]
})
export class MaterialIssueRegisterComponent implements OnInit {
  items: MaterialIssueRegisterItem[] = [];
  isLoading = false;

  filter: MaterialIssueRegisterFilter = {};

  statusOptions = [
    { label: 'All Statuses', value: '' },
    { label: 'Draft',        value: 'DRAFT' },
    { label: 'Posted',       value: 'POSTED' },
    { label: 'Cancelled',    value: 'CANCELLED' }
  ];

  requestTypeOptions = [
    { label: 'All Types',    value: '' },
    { label: 'Project',      value: 'PROJECT' },
    { label: 'General',      value: 'GENERAL' },
    { label: 'Maintenance',  value: 'MAINTENANCE' },
    { label: 'Employee',     value: 'EMPLOYEE' }
  ];

  constructor(private svc: ReportsService, private msg: MessageService) {}

  ngOnInit(): void { this.load(); }

  get totalValue(): number   { return this.items.reduce((s, i) => s + i.totalValue, 0); }
  get totalLines(): number   { return this.items.reduce((s, i) => s + i.lineCount, 0); }
  get postedCount(): number  { return this.items.filter(i => i.status === 'POSTED').length; }

  getStatusSeverity(status: string): 'success' | 'warn' | 'danger' | 'info' {
    switch (status) {
      case 'POSTED':    return 'success';
      case 'DRAFT':     return 'warn';
      case 'CANCELLED': return 'danger';
      default:          return 'info';
    }
  }

  load(): void {
    this.isLoading = true;
    this.svc.getMaterialIssueRegister(this.filter).subscribe({
      next: r => {
        this.isLoading = false;
        this.items = r.success ? r.result : [];
      },
      error: () => {
        this.isLoading = false;
        this.msg.add({ severity: 'error', summary: 'Error', detail: 'Failed to load issue register.' });
      }
    });
  }

  clear(): void {
    this.filter = {};
    this.load();
  }
}
