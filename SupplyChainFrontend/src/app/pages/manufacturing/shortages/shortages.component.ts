import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { TableModule } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';
import { MaterialShortage, ProductionOrderService, priorityLabel } from '../../../services/production-order.service';

/**
 * A30 §29.3 — every critical requirement nothing yet covers, across every open production order.
 * Read only: act on a row from the order itself.
 */
@Component({
  selector: 'app-shortages',
  standalone: true,
  imports: [CommonModule, RouterModule, ButtonModule, TableModule, TagModule, ToastModule, TooltipModule],
  templateUrl: './shortages.component.html',
  styleUrls: ['./shortages.component.scss'],
  providers: [MessageService]
})
export class ShortagesComponent implements OnInit {
  readonly priorityLabel = priorityLabel;

  shortages: MaterialShortage[] = [];
  isLoading = true;

  constructor(private service: ProductionOrderService, private messageService: MessageService) {}

  ngOnInit(): void { this.load(); }

  load(): void {
    this.isLoading = true;
    this.service.getShortages().subscribe({
      next: (res) => { this.isLoading = false; this.shortages = res.result ?? []; },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load material shortages.' });
      }
    });
  }
}
