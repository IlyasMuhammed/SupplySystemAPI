import { Component, OnInit, ViewChild } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { Table, TableModule } from 'primeng/table';
import { ToolbarModule } from 'primeng/toolbar';
import { TagModule } from 'primeng/tag';
import { ButtonModule } from 'primeng/button';
import { InputTextModule } from 'primeng/inputtext';
import { InputIconModule } from 'primeng/inputicon';
import { IconFieldModule } from 'primeng/iconfield';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService, ConfirmationService } from 'primeng/api';
import { CitiesService, CityModel } from '../../../services/cities.service';
import { AuthService } from '../../service/auth.service';

@Component({
  selector: 'app-cities-list',
  standalone: true,
  imports: [CommonModule, RouterModule, TableModule, ToolbarModule, TagModule, ButtonModule,
    InputTextModule, InputIconModule, IconFieldModule, ConfirmDialogModule, ToastModule, TooltipModule],
  templateUrl: './cities-list.component.html',
  styleUrls: ['./cities-list.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class CitiesListComponent implements OnInit {
  @ViewChild('dt') dt?: Table;

  cities: CityModel[] = [];
  isLoading = true;

  constructor(
    private citiesService: CitiesService,
    private messageService: MessageService,
    private confirmationService: ConfirmationService,
    public authService: AuthService
  ) {}

  // Adding a city only needs LOCATION_MANAGE; editing/deleting an existing one (which could
  // affect every other organization relying on its current name/country link) stays
  // SYSTEM_CONFIGURE-only.
  get canEditOrDelete(): boolean {
    return this.authService.hasPermission('SYSTEM_CONFIGURE');
  }

  ngOnInit(): void {
    this.loadCities();
  }

  loadCities() {
    this.isLoading = true;
    this.citiesService.getAllCities().subscribe({
      next: (res) => {
        this.isLoading = false;
        this.cities = Array.isArray(res.result) ? res.result : [];
      },
      error: (err) => {
        this.isLoading = false;
        console.error('API Error:', err);
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load cities' });
      }
    });
  }

  onDelete(city: CityModel) {
    this.confirmationService.confirm({
      message: `Are you sure you want to delete ${city.name}?`,
      header: 'Confirm Delete',
      icon: 'pi pi-exclamation-triangle',
      acceptButtonStyleClass: 'p-button-danger',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => {
        this.citiesService.deleteCity(city.id).subscribe({
          next: () => {
            this.messageService.add({ severity: 'success', summary: 'Success', detail: 'City deleted successfully' });
            this.loadCities();
          },
          error: (err) => {
            console.error('Delete error:', err);
            this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to delete city' });
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
