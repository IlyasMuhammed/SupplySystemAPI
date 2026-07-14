import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { TableModule } from 'primeng/table';
import { ButtonModule } from 'primeng/button';
import { DropdownModule } from 'primeng/dropdown';
import { ToastModule } from 'primeng/toast';
import { TagModule } from 'primeng/tag';
import { MessageService } from 'primeng/api';
import { ReportsService, ShipmentTrackerItem } from '../../../services/reports.service';
import { PdfService } from '../../../services/pdf.service';

@Component({
  selector: 'app-shipment-tracker',
  standalone: true,
  imports: [CommonModule, FormsModule, TableModule, ButtonModule, DropdownModule, ToastModule, TagModule],
  templateUrl: './shipment-tracker.component.html',
  styleUrls: ['./shipment-tracker.component.scss'],
  providers: [MessageService]
})
export class ShipmentTrackerComponent implements OnInit {
  items: ShipmentTrackerItem[] = [];
  isLoading = true;
  selectedStatus: string | null = null;

  statusOptions = [
    { label: 'All Statuses', value: null },
    { label: 'Pending',      value: 'PENDING' },
    { label: 'Dispatched',   value: 'DISPATCHED' },
    { label: 'In Transit',   value: 'IN_TRANSIT' },
    { label: 'Delivered',    value: 'DELIVERED' }
  ];

  constructor(private reports: ReportsService, private msg: MessageService, private pdf: PdfService) {}
  ngOnInit(): void { this.load(); }

  load(): void {
    this.isLoading = true;
    this.reports.getShipmentTracker(this.selectedStatus ?? undefined).subscribe({
      next:  r => { this.isLoading = false; this.items = r.success ? r.result : []; },
      error: () => { this.isLoading = false; this.msg.add({ severity: 'error', summary: 'Error', detail: 'Failed to load shipment data.' }); }
    });
  }

  resetFilters(): void {
    this.selectedStatus = null;
    this.load();
  }

  downloadPdf(): void {
    this.pdf.downloadTableReport({
      title: 'Shipment Tracker Report',
      fileName: 'shipment-tracker',
      columns: ['Shipment #', 'PO #', 'Carrier', 'Type', 'Status', 'Dispatch Date', 'Est. Arrival', 'Actual Arrival', 'Overdue'],
      rows: this.items.map(r => [
        r.shipmentNumber, r.poNumber, r.carrierName ?? '—', r.shipmentType, r.status,
        new Date(r.dispatchDate).toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' }),
        new Date(r.estimatedArrival).toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' }),
        r.actualArrival ? new Date(r.actualArrival).toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' }) : '—',
        r.isOverdue ? 'Yes' : 'No'
      ]),
      accentColor: [245, 158, 11]
    });
  }

  getStatusSeverity(status: string): 'success' | 'info' | 'warn' | 'danger' | 'secondary' {
    switch ((status || '').toUpperCase()) {
      case 'DELIVERED':  return 'success';
      case 'IN_TRANSIT': return 'info';
      case 'DISPATCHED': return 'warn';
      case 'PENDING':    return 'secondary';
      default:           return 'secondary';
    }
  }
}
