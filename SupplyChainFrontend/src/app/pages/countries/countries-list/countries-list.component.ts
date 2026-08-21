import { Component, OnInit, ViewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule, Router } from '@angular/router';
import { Table, TableModule } from 'primeng/table';
import { ToolbarModule } from 'primeng/toolbar';
import { TagModule } from 'primeng/tag';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { InputIconModule } from 'primeng/inputicon';
import { IconFieldModule } from 'primeng/iconfield';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { ToastModule } from 'primeng/toast';
import { MessageService, ConfirmationService } from 'primeng/api';
import { CountriesService, CountryModel } from '../../../services/countries.service';
import { AuthService } from '../../service/auth.service';

@Component({
  selector: 'app-countries-list',
  standalone: true,
  imports: [CommonModule, RouterModule, TableModule, ToolbarModule, TagModule, ButtonModule,
    InputTextModule, InputIconModule, IconFieldModule, ConfirmDialogModule, ToastModule],
  templateUrl: './countries-list.component.html',
  styleUrls: ['./countries-list.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class CountriesListComponent implements OnInit {
  @ViewChild('dt') dt?: Table;

  countries: CountryModel[] = [];
  isLoading = true;

  constructor(
    private countriesService: CountriesService,
    private messageService: MessageService,
    private confirmationService: ConfirmationService,
    private router: Router,
    public authService: AuthService
  ) {}

  // Adding a country only needs LOCATION_MANAGE; editing/deleting an existing one (which could
  // affect every other organization relying on its current name/code) stays SYSTEM_CONFIGURE-only.
  get canEditOrDelete(): boolean {
    return this.authService.hasPermission('SYSTEM_CONFIGURE');
  }

  ngOnInit(): void {
    this.loadCountries();
  }

  loadCountries() {
    this.isLoading = true;
    this.countriesService.getAllCountries().subscribe({
      next: (res) => {
        this.isLoading = false;
        this.countries = Array.isArray(res.result) ? res.result : [];
      },
      error: (err) => {
        this.isLoading = false;
        console.error('API Error:', err);
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load countries' });
      }
    });
  }

  onEdit(country: CountryModel) {
    this.router.navigate(['/portal/pages/countries/countries-create', country.id]);
  }

  onDelete(country: CountryModel) {
    this.confirmationService.confirm({
      message: `Are you sure you want to delete ${country.name}?`,
      header: 'Confirm Delete',
      icon: 'pi pi-exclamation-triangle',
      acceptButtonStyleClass: 'p-button-danger',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => {
        this.countriesService.deleteCountry(country.id).subscribe({
          next: () => {
            this.messageService.add({ severity: 'success', summary: 'Success', detail: 'Country deleted successfully' });
            this.loadCountries();
          },
          error: (err) => {
            console.error('Delete error:', err);
            this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to delete country' });
          }
        });
      }
    });
  }

  onGlobalFilter(event: Event) {
    const input = event.target as HTMLInputElement;
    this.dt?.filterGlobal(input.value, 'contains');
  }
}
