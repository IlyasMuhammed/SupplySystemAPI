import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import { DomSanitizer, SafeResourceUrl } from '@angular/platform-browser';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { ReactiveFormsModule, FormBuilder, FormGroup, Validators, FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { CardModule } from 'primeng/card';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { DialogModule } from 'primeng/dialog';
import { InputTextModule } from 'primeng/inputtext';
import { TooltipModule } from 'primeng/tooltip';
import { DividerModule } from 'primeng/divider';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { CheckboxModule } from 'primeng/checkbox';
import { MessageService, ConfirmationService } from 'primeng/api';
import { DropdownModule } from 'primeng/dropdown';
import { TableModule } from 'primeng/table';
import { FLOW } from '../../../../shared/flow';
import {
  InventoryService,
  WarehouseModel,
  CreateWarehouseRequest
} from '../../../../services/inventory.service';
import { CountriesService, CountryModel } from '../../../../services/countries.service';
import { CitiesService, CityModel } from '../../../../services/cities.service';

@Component({
  selector: 'app-warehouse-list',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule, ReactiveFormsModule,
    ButtonModule, CardModule, TagModule, ToastModule,
    DialogModule, InputTextModule, TooltipModule, DividerModule,
    ConfirmDialogModule, CheckboxModule, DropdownModule, TableModule,
    ...FLOW
  ],
  templateUrl: './warehouse-list.component.html',
  styleUrls: ['./warehouse-list.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class WarehouseListComponent implements OnInit {
  warehouses: WarehouseModel[] = [];
  isLoading = true;

  countryOptions:     { label: string; value: string }[] = [];
  createCityOptions:  { label: string; value: string }[] = [];
  editCityOptions:    { label: string; value: string }[] = [];
  isCreateCitiesLoading = false;
  isEditCitiesLoading   = false;

  // Create dialog
  showCreateDialog = false;
  createForm!: FormGroup;
  isCreating = false;

  // Edit dialog
  showEditDialog = false;
  editForm!: FormGroup;
  isSaving = false;
  editingWarehouse: WarehouseModel | null = null;

  // Map picker dialog (shared between create/edit)
  showMapPicker    = false;
  mapPickerLat     = '';
  mapPickerLng     = '';
  mapPickerEmbedUrl: SafeResourceUrl | null = null;
  private mapPickerTarget: 'create' | 'edit' = 'create';

  // Location search (Nominatim)
  locationQuery        = '';
  locationResults: any[] = [];
  isSearchingLocation  = false;
  showLocationResults  = false;

  constructor(
    private route: ActivatedRoute,
    private router: Router,
    private fb: FormBuilder,
    private inventoryService: InventoryService,
    private countriesService: CountriesService,
    private citiesService: CitiesService,
    private messageService: MessageService,
    private confirmationService: ConfirmationService,
    private sanitizer: DomSanitizer,
    private http: HttpClient
  ) {}

  ngOnInit() {
    this.initForms();
    this.loadWarehouses();
    this.loadCountries();
    // Auto-open create dialog when navigated with ?action=create
    this.route.queryParams.subscribe(params => {
      if (params['action'] === 'create') {
        this.openCreateDialog();
        this.router.navigate([], { queryParams: {}, replaceUrl: true });
      }
    });
  }

  private loadCountries() {
    this.countriesService.getAllCountries().subscribe({
      next: (res) => {
        this.countryOptions = (res.result ?? [])
          .filter((c: CountryModel) => c.isActive)
          .map((c: CountryModel) => ({ label: c.name, value: c.id }));
      },
      error: () => {}
    });
  }

  private initForms() {
    const commonFields = {
      code:         ['', [Validators.required, Validators.maxLength(20)]],
      name:         ['', [Validators.required, Validators.minLength(2), Validators.maxLength(200)]],
      address:      [''],
      country:      [null],
      city:         [{ value: null, disabled: true }],
      contactName:  [''],
      contactPhone: [''],
      googleMapsUrl: [''],
      latitude:      [null],
      longitude:     [null]
    };
    this.createForm = this.fb.group({ ...commonFields });
    this.editForm   = this.fb.group({ ...commonFields, isActive: [true] });
  }

  // ── Auto-URL generation ───────────────────────────────────────────────────

  onLatLngChange(form: FormGroup) {
    const lat = form.get('latitude')?.value;
    const lng = form.get('longitude')?.value;
    const url = form.get('googleMapsUrl')?.value;
    if (lat != null && lng != null && !url) {
      form.get('googleMapsUrl')!.setValue(
        `https://maps.google.com/?q=${lat},${lng}`, { emitEvent: false }
      );
    }
  }

  // ── Map picker ────────────────────────────────────────────────────────────

  openMapPicker(target: 'create' | 'edit') {
    this.mapPickerTarget     = target;
    const form               = target === 'create' ? this.createForm : this.editForm;
    this.mapPickerLat        = form.get('latitude')?.value ?? '';
    this.mapPickerLng        = form.get('longitude')?.value ?? '';
    this.mapPickerEmbedUrl   = null;
    this.locationQuery       = '';
    this.locationResults     = [];
    this.showLocationResults = false;
    this.showMapPicker       = true;
    // show existing coords if already set
    if (this.mapPickerLat && this.mapPickerLng) this.refreshMapPreview();
  }

  searchLocation() {
    const q = this.locationQuery.trim();
    if (!q) return;
    this.isSearchingLocation = true;
    this.showLocationResults = false;
    this.http.get<any[]>('https://nominatim.openstreetmap.org/search', {
      params: { q, format: 'json', limit: '6', addressdetails: '0' }
    }).subscribe({
      next: (res) => {
        this.locationResults     = res ?? [];
        this.showLocationResults = true;
        this.isSearchingLocation = false;
      },
      error: () => { this.isSearchingLocation = false; }
    });
  }

  selectLocation(result: any) {
    this.mapPickerLat        = parseFloat(result.lat).toFixed(6);
    this.mapPickerLng        = parseFloat(result.lon).toFixed(6);
    this.locationQuery       = result.display_name;
    this.showLocationResults = false;
    this.refreshMapPreview();
  }

  useMyLocation() {
    if (!navigator.geolocation) return;
    navigator.geolocation.getCurrentPosition(pos => {
      this.mapPickerLat = pos.coords.latitude.toFixed(6);
      this.mapPickerLng = pos.coords.longitude.toFixed(6);
      this.refreshMapPreview();
    });
  }

  applyMapCoords() {
    const lat = parseFloat(this.mapPickerLat);
    const lng = parseFloat(this.mapPickerLng);
    if (isNaN(lat) || isNaN(lng)) return;
    const form = this.mapPickerTarget === 'create' ? this.createForm : this.editForm;
    form.patchValue({
      latitude:      lat,
      longitude:     lng,
      googleMapsUrl: `https://maps.google.com/?q=${lat},${lng}`
    });
    this.showMapPicker = false;
  }

  refreshMapPreview() {
    const lat = parseFloat(this.mapPickerLat);
    const lng = parseFloat(this.mapPickerLng);
    if (isNaN(lat) || isNaN(lng)) return;
    this.mapPickerEmbedUrl = this.sanitizer.bypassSecurityTrustResourceUrl(
      `https://maps.google.com/maps?q=${lat},${lng}&z=15&output=embed`
    );
  }

  loadWarehouses() {
    this.isLoading = true;
    this.inventoryService.getWarehouses().subscribe({
      next: (res) => {
        this.isLoading = false;
        this.warehouses = res.success ? (res.result ?? []) : [];
        if (!res.success) this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Failed to load warehouses.' });
      },
      error: (err) => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to load warehouses.' });
      }
    });
  }

  openCreateDialog() {
    this.createForm.reset();
    this.createForm.get('city')!.disable();
    this.createCityOptions = [];
    this.showCreateDialog = true;
  }

  onCreateCountryChange() {
    const countryId = this.createForm.get('country')?.value;
    const cityCtrl = this.createForm.get('city')!;
    cityCtrl.setValue(null);
    this.createCityOptions = [];
    cityCtrl.disable();

    if (!countryId) return;

    this.isCreateCitiesLoading = true;
    this.citiesService.getCitiesByCountry(countryId).subscribe({
      next: (res) => {
        this.isCreateCitiesLoading = false;
        this.createCityOptions = (res.result ?? []).map((c: CityModel) => ({ label: c.name, value: c.id }));
        if (this.createCityOptions.length > 0) cityCtrl.enable();
      },
      error: () => { this.isCreateCitiesLoading = false; }
    });
  }

  saveWarehouse() {
    if (this.createForm.invalid) { this.createForm.markAllAsTouched(); return; }
    const raw = this.createForm.getRawValue();
    const countryName = this.countryOptions.find(c => c.value === raw.country)?.label ?? raw.country ?? undefined;
    const cityName    = this.createCityOptions.find(c => c.value === raw.city)?.label ?? raw.city ?? undefined;
    const payload: CreateWarehouseRequest = {
      code:          raw.code.trim().toUpperCase(),
      name:          raw.name.trim(),
      address:       raw.address       || undefined,
      city:          cityName,
      country:       countryName,
      contactName:   raw.contactName   || undefined,
      contactPhone:  raw.contactPhone  || undefined,
      googleMapsUrl: raw.googleMapsUrl || undefined,
      latitude:      raw.latitude      ?? undefined,
      longitude:     raw.longitude     ?? undefined
    };
    this.isCreating = true;
    this.inventoryService.createWarehouse(payload).subscribe({
      next: (res) => {
        this.isCreating = false;
        this.showCreateDialog = false;
        if (res.success) {
          this.messageService.add({ severity: 'success', summary: 'Created', detail: `Warehouse "${payload.name}" created.` });
          this.loadWarehouses();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message });
        }
      },
      error: (err) => {
        this.isCreating = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to create warehouse.' });
      }
    });
  }

  openEditDialog(wh: WarehouseModel) {
    this.editingWarehouse = wh;
    this.editForm.patchValue({
      code:          wh.code,
      name:          wh.name,
      address:       wh.address       ?? '',
      country:       null,
      city:          null,
      contactName:   wh.contactName   ?? '',
      contactPhone:  wh.contactPhone  ?? '',
      googleMapsUrl: wh.googleMapsUrl ?? '',
      latitude:      wh.latitude      ?? null,
      longitude:     wh.longitude     ?? null,
      isActive:      wh.isActive
    });
    this.editForm.get('city')!.disable();
    this.editCityOptions = [];

    // Pre-select country by stored name and load cities
    const countryMatch = this.countryOptions.find(c => c.label === wh.country);
    if (countryMatch) {
      this.editForm.get('country')!.setValue(countryMatch.value);
      this.isEditCitiesLoading = true;
      this.citiesService.getCitiesByCountry(countryMatch.value).subscribe({
        next: (res) => {
          this.isEditCitiesLoading = false;
          this.editCityOptions = (res.result ?? []).map((c: CityModel) => ({ label: c.name, value: c.id }));
          if (this.editCityOptions.length > 0) {
            this.editForm.get('city')!.enable();
            const cityMatch = this.editCityOptions.find(c => c.label === wh.city);
            if (cityMatch) this.editForm.get('city')!.setValue(cityMatch.value);
          }
        },
        error: () => { this.isEditCitiesLoading = false; }
      });
    }
    this.showEditDialog = true;
  }

  onEditCountryChange() {
    const countryId = this.editForm.get('country')?.value;
    const cityCtrl = this.editForm.get('city')!;
    cityCtrl.setValue(null);
    this.editCityOptions = [];
    cityCtrl.disable();

    if (!countryId) return;

    this.isEditCitiesLoading = true;
    this.citiesService.getCitiesByCountry(countryId).subscribe({
      next: (res) => {
        this.isEditCitiesLoading = false;
        this.editCityOptions = (res.result ?? []).map((c: CityModel) => ({ label: c.name, value: c.id }));
        if (this.editCityOptions.length > 0) cityCtrl.enable();
      },
      error: () => { this.isEditCitiesLoading = false; }
    });
  }

  saveEdit() {
    if (this.editForm.invalid || !this.editingWarehouse) { this.editForm.markAllAsTouched(); return; }
    const raw = this.editForm.getRawValue();
    const countryName = this.countryOptions.find(c => c.value === raw.country)?.label ?? raw.country ?? undefined;
    const cityName    = this.editCityOptions.find(c => c.value === raw.city)?.label ?? raw.city ?? undefined;
    const payload = {
      name:          raw.name?.trim()          || undefined,
      address:       raw.address?.trim()       || undefined,
      city:          cityName,
      country:       countryName,
      contactName:   raw.contactName?.trim()   || undefined,
      contactPhone:  raw.contactPhone?.trim()  || undefined,
      googleMapsUrl: raw.googleMapsUrl?.trim() || undefined,
      latitude:      raw.latitude              ?? undefined,
      longitude:     raw.longitude             ?? undefined,
      isActive:      raw.isActive
    };
    this.isSaving = true;
    this.inventoryService.updateWarehouse(this.editingWarehouse.id, payload).subscribe({
      next: (res) => {
        this.isSaving = false;
        if (res.success) {
          this.messageService.add({ severity: 'success', summary: 'Updated', detail: 'Warehouse updated successfully.' });
          this.showEditDialog = false;
          this.loadWarehouses();
        } else {
          this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Update failed.' });
        }
      },
      error: (err) => {
        this.isSaving = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Update failed.' });
      }
    });
  }

  confirmDelete(wh: WarehouseModel) {
    this.confirmationService.confirm({
      message: `Deactivate warehouse "${wh.name}"? Its stock records will be preserved.`,
      header: 'Deactivate Warehouse',
      icon: 'pi pi-exclamation-triangle',
      acceptLabel: 'Deactivate',
      acceptButtonStyleClass: 'p-button-danger',
      rejectLabel: 'Cancel',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => {
        this.inventoryService.deleteWarehouse(wh.id).subscribe({
          next: (res) => {
            if (res.success) {
              this.messageService.add({ severity: 'success', summary: 'Deactivated', detail: `${wh.name} has been deactivated.` });
              this.loadWarehouses();
            } else {
              this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Failed.' });
            }
          },
          error: (err) => this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed.' })
        });
      }
    });
  }

  viewWarehouse(id: number) {
    this.router.navigate(['/portal/pages/inventory/warehouses', id]);
  }

  get cf() { return this.createForm.controls; }
  get ef() { return this.editForm.controls; }

  get activeCount(): number { return this.warehouses.filter(w => w.isActive).length; }

  getLocation(w: WarehouseModel): string {
    const parts = [w.city, w.country].filter(Boolean);
    return parts.length ? parts.join(', ') : '—';
  }
}
