import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router, RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { SelectModule } from 'primeng/select';
import { DatePickerModule } from 'primeng/datepicker';
import { AutoCompleteModule, AutoCompleteCompleteEvent } from 'primeng/autocomplete';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';

import { SalesPreorderService, CreateSaleInquiryRequest } from '../../../../services/sales-preorder.service';
import { BusinessPartnerService, BusinessPartnerModel } from '../../../../services/business-partner.service';
import { UserService } from '../../../../services/user.service';
import { AuthService } from '../../../service/auth.service';
import { AssigneeOption, MAX, assigneeOptions$, serverMessage, writeDate } from '../sale-inquiry.shared';

export interface InquiryHeaderDraft {
  customer: BusinessPartnerModel | null;
  customerReference: string;
  customerReferenceDate: Date | null;
  receivedDate: Date | null;
  responseDeadline: Date | null;
  assignedToUserId: number | null;
  notes: string;
}

/**
 * A32 — Sales → Inquiries → New (SALE_INQUIRY_CREATE). The header only: who asked, their reference, when it came in
 * and by when they want an answer. The inquiry opens on its Lines tab, where the requested items are entered.
 */
@Component({
  selector: 'app-sale-inquiry-form',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule, ButtonModule, InputTextModule, TextareaModule, SelectModule,
    DatePickerModule, AutoCompleteModule, ToastModule, TooltipModule
  ],
  templateUrl: './sale-inquiry-form.component.html',
  styleUrls: ['../sale-inquiry.shared.scss', './sale-inquiry-form.component.scss'],
  providers: [MessageService]
})
export class SaleInquiryFormComponent implements OnInit {
  readonly max = MAX;

  draft: InquiryHeaderDraft = {
    customer: null, customerReference: '', customerReferenceDate: null, receivedDate: SaleInquiryFormComponent.today(),
    responseDeadline: null, assignedToUserId: null, notes: ''
  };
  customerSuggestions: BusinessPartnerModel[] = [];
  assigneeOptions: AssigneeOption[] = [];

  isSaving = false;
  saveError = '';
  submitted = false;

  constructor(
    private service: SalesPreorderService,
    private partnerService: BusinessPartnerService,
    private userService: UserService,
    private authService: AuthService,
    private router: Router
  ) {}

  private static today(): Date {
    const now = new Date();
    return new Date(now.getFullYear(), now.getMonth(), now.getDate());
  }

  ngOnInit() {
    assigneeOptions$(this.userService, this.authService).subscribe(options => this.assigneeOptions = options);
  }

  searchCustomers(event: AutoCompleteCompleteEvent) {
    this.partnerService.getPartners({ isCustomer: true, active: true, search: event.query, pageSize: 20 }).subscribe({
      next: (res) => { this.customerSuggestions = res.result?.data ?? []; },
      error: () => { this.customerSuggestions = []; }
    });
  }

  /** The first thing stopping a save, in words; null when it can be saved. */
  get problem(): string | null {
    const d = this.draft;
    if (!d.customer?.uuid) return 'Choose the customer who sent the inquiry.';
    if (!d.receivedDate) return 'Say when the inquiry was received.';
    if (d.responseDeadline && d.responseDeadline < d.receivedDate) return 'The response deadline cannot be before the day it was received.';
    if (d.customerReference.trim().length > MAX.customerReference) return `The customer reference is at most ${MAX.customerReference} characters.`;
    if (d.notes.trim().length > MAX.headerNotes) return `Notes are at most ${MAX.headerNotes} characters.`;
    return null;
  }

  buildRequest(): CreateSaleInquiryRequest {
    const d = this.draft;
    return {
      partnerId: d.customer!.uuid!,
      customerReference: d.customerReference.trim() || null,
      customerReferenceDate: writeDate(d.customerReferenceDate),
      receivedDate: writeDate(d.receivedDate),
      responseDeadline: writeDate(d.responseDeadline),
      assignedToUserId: d.assignedToUserId ?? null,
      notes: d.notes.trim() || null
    };
  }

  save() {
    this.submitted = true;
    if (this.problem || this.isSaving) return;
    this.isSaving = true;
    this.saveError = '';

    this.service.createInquiry(this.buildRequest()).subscribe({
      next: (res) => {
        this.isSaving = false;
        if (res.result) this.router.navigate(['/portal/pages/sales/inquiries', res.result], { queryParams: { tab: 'lines' } });
        else this.router.navigate(['/portal/pages/sales/inquiries']);
      },
      error: (err) => {
        this.isSaving = false;
        this.saveError = serverMessage(err, 'The inquiry could not be created.');
      }
    });
  }
}
