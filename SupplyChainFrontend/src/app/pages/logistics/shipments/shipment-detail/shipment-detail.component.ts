import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule, ActivatedRoute } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { CardModule } from 'primeng/card';
import { DialogModule } from 'primeng/dialog';
import { DropdownModule } from 'primeng/dropdown';
import { CalendarModule } from 'primeng/calendar';
import { TextareaModule } from 'primeng/textarea';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';
import { LogisticsService, ShipmentDetailModel, PatchShipmentRequest } from '../../../../services/logistics.service';
import { FLOW, FlowStage, flowStagesFrom } from '../../../../shared/flow';

@Component({
  selector: 'app-shipment-detail',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, TagModule, ToastModule,
    CardModule, DialogModule, DropdownModule,
    CalendarModule, TextareaModule, TooltipModule,
    ...FLOW
  ],
  templateUrl: './shipment-detail.component.html',
  styleUrls: ['./shipment-detail.component.scss'],
  providers: [MessageService]
})
export class ShipmentDetailComponent implements OnInit {
  shipment: ShipmentDetailModel | null = null;
  isLoading = true;

  showStatusDialog = false;
  newStatus = '';
  actualArrivalVal: Date | null = null;
  isSaving = false;

  // The proof-of-delivery upload that lived on this screen was retired with F47. It stored a path
  // into the API's wwwroot, which is a proof only until the next redeploy. Proof of delivery now
  // lives at /logistics/proof-of-delivery, against the consignment, with the artefact itself.

  statusOptions = [
    { label: 'Preparing',  value: 'Preparing' },
    { label: 'Dispatched', value: 'Dispatched' },
    { label: 'In Transit', value: 'In Transit' },
    { label: 'Delivered',  value: 'Delivered' },
    { label: 'Returned',   value: 'Returned' }
  ];

  timelineSteps = [
    { label: 'Preparing',  value: 'Preparing',  icon: 'pi-box' },
    { label: 'Dispatched', value: 'Dispatched', icon: 'pi-send' },
    { label: 'In Transit', value: 'In Transit', icon: 'pi-truck' },
    { label: 'Delivered',  value: 'Delivered',  icon: 'pi-check-circle' }
  ];

  private readonly statusOrder = ['Preparing', 'Dispatched', 'In Transit', 'Delivered'];

  isStepActive(step: { value: string }): boolean {
    const current = this.shipment?.status ?? '';
    return this.statusOrder.indexOf(current) >= this.statusOrder.indexOf(step.value);
  }

  isStepCurrent(step: { value: string }): boolean {
    return this.shipment?.status === step.value;
  }

  /** SMS Flow status strip from the same steps (Delivered = all done; Returned stops it in red). */
  get stages(): FlowStage[] {
    const status = this.shipment?.status ?? '';
    if (status === 'Returned') return flowStagesFrom(['Shipped', 'Returned'], 1, { failed: true });
    const at = this.statusOrder.indexOf(status);
    const current = status === 'Delivered' ? this.statusOrder.length : Math.max(at, 0);
    return flowStagesFrom(this.timelineSteps.map(s => s.label), current);
  }

  constructor(
    private logisticsService: LogisticsService,
    private messageService: MessageService,
    private route: ActivatedRoute
  ) {}

  ngOnInit() {
    const uuid = this.route.snapshot.paramMap.get('uuid');
    if (uuid) this.load(uuid);
  }

  load(uuid: string) {
    this.isLoading = true;
    this.logisticsService.getShipmentById(uuid).subscribe({
      next: (res) => {
        this.isLoading = false;
        this.shipment  = res.success ? res.result : null;
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load shipment.' });
      }
    });
  }

  openStatusDialog() {
    this.newStatus       = this.shipment?.status ?? '';
    this.actualArrivalVal = null;
    this.showStatusDialog = true;
  }

  saveStatus() {
    if (!this.shipment) return;
    this.isSaving = true;
    const req: PatchShipmentRequest = { status: this.newStatus };
    if (this.actualArrivalVal) req.actualArrival = this.actualArrivalVal.toISOString();
    this.logisticsService.patchShipment(this.shipment.uuid, req).subscribe({
      next: () => {
        this.isSaving       = false;
        this.showStatusDialog = false;
        this.messageService.add({ severity: 'success', summary: 'Updated', detail: 'Status updated.' });
        this.load(this.shipment!.uuid);
      },
      error: (err) => {
        this.isSaving = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message || 'Update failed.' });
      }
    });
  }

  resolveUrl(url: string) { return this.logisticsService.resolveFileUrl(url); }

  getStatusSeverity(s: string): 'success' | 'danger' | 'warn' | 'info' | 'secondary' {
    switch (s) {
      case 'Delivered':  return 'success';
      case 'Returned':   return 'danger';
      case 'In Transit': return 'info';
      case 'Dispatched': return 'warn';
      default:           return 'secondary';
    }
  }
}
