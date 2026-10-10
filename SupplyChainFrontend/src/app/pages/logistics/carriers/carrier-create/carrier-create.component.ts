import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule, Router } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { DropdownModule } from 'primeng/dropdown';
import { InputNumberModule } from 'primeng/inputnumber';
import { ToastModule } from 'primeng/toast';
import { CardModule } from 'primeng/card';
import { MessageService } from 'primeng/api';
import { LogisticsService, CreateCarrierRequest } from '../../../../services/logistics.service';
import { FLOW } from '../../../../shared/flow';

@Component({
  selector: 'app-carrier-create',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    ButtonModule, InputTextModule, DropdownModule,
    InputNumberModule, ToastModule, CardModule,
    ...FLOW
  ],
  templateUrl: './carrier-create.component.html',
  styleUrls: ['./carrier-create.component.scss'],
  providers: [MessageService]
})
export class CarrierCreateComponent {
  form: CreateCarrierRequest = {
    name: '', code: '',
    serviceType: undefined, trackingUrlTemplate: undefined,
    contactName: undefined, contactPhone: undefined, contactEmail: undefined,
  };
  isSaving = false;

  serviceTypeOptions = [
    { label: 'Courier', value: 'Courier' },
    { label: 'LTL',     value: 'LTL' },
    { label: 'FTL',     value: 'FTL' },
    { label: 'Air',     value: 'Air' },
    { label: 'Sea',     value: 'Sea' }
  ];

  constructor(
    private logisticsService: LogisticsService,
    private messageService: MessageService,
    private router: Router
  ) {}

  save() {
    if (!this.form.name?.trim()) {
      this.messageService.add({ severity: 'warn', summary: 'Validation', detail: 'Carrier name is required.' });
      return;
    }
    if (!this.form.code?.trim()) {
      this.messageService.add({ severity: 'warn', summary: 'Validation', detail: 'Carrier code is required.' });
      return;
    }
    this.isSaving = true;
    this.logisticsService.createCarrier(this.form).subscribe({
      next: () => {
        this.isSaving = false;
        this.router.navigate(['/portal/pages/logistics/carriers']);
      },
      error: (err) => {
        this.isSaving = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message || 'Failed to create carrier.' });
      }
    });
  }
}
