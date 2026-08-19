import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule, ActivatedRoute } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { PickListModule } from 'primeng/picklist';
import { ButtonModule } from 'primeng/button';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { ToggleSwitchModule } from 'primeng/toggleswitch';
import { DialogModule } from 'primeng/dialog';
import { InputTextModule } from 'primeng/inputtext';
import { TextareaModule } from 'primeng/textarea';
import { DropdownModule } from 'primeng/dropdown';
import { TooltipModule } from 'primeng/tooltip';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { MessageService, ConfirmationService } from 'primeng/api';
import { forkJoin } from 'rxjs';
import {
  InventoryService,
  CategoryModel,
  AttributeDefinitionModel,
  CategoryAttributeModel,
  CreateAttributeDefinitionRequest,
  AttributeDeleteConflictResult
} from '../../../services/inventory.service';
import { DynamicAttributeFormComponent } from '../../../shared/dynamic-attribute-form/dynamic-attribute-form.component';

// Working item shape for both PickList panels — the source (available) side just shows the
// attribute's own catalog defaults; the target (linked) side carries the live, editable
// isRequired override this screen is for. displayOrder is derived from list position, not stored
// per-item, so the Save payload always reflects the panel's current order regardless of how many
// drags/adds/removes happened since load.
interface ConfigItem extends AttributeDefinitionModel {
  linkedRequired: boolean;
}

// One preset per selectable "Field Type" in the New Attribute dialog — bundles the
// dataType/controlType pair the backend expects (InventoryRepository.CreateAttributeAsync
// validates both independently, and DynamicAttributeFormComponent's rendering switches on
// controlType) so the admin picks one concept instead of two coupled raw enum values.
interface FieldTypePreset {
  label: string;
  dataType: AttributeDefinitionModel['dataType'];
  controlType: AttributeDefinitionModel['controlType'];
  hasOptions: boolean;
}

const FIELD_TYPE_PRESETS: FieldTypePreset[] = [
  { label: 'Text',              dataType: 'TEXT',         controlType: 'TEXTBOX',      hasOptions: false },
  { label: 'Long Text',         dataType: 'TEXT',         controlType: 'TEXTAREA',     hasOptions: false },
  { label: 'Whole Number',      dataType: 'NUMBER',       controlType: 'NUMBERBOX',    hasOptions: false },
  { label: 'Decimal Number',    dataType: 'DECIMAL',      controlType: 'NUMBERBOX',    hasOptions: false },
  { label: 'Date',              dataType: 'DATE',         controlType: 'DATEPICKER',   hasOptions: false },
  { label: 'Yes / No',          dataType: 'BOOLEAN',      controlType: 'TOGGLE',       hasOptions: false },
  { label: 'Dropdown (single)', dataType: 'DROPDOWN',     controlType: 'DROPDOWN',     hasOptions: true  },
  { label: 'Multi-Select',      dataType: 'MULTI_SELECT', controlType: 'MULTI_SELECT', hasOptions: true  }
];

@Component({
  selector: 'app-category-attributes-config',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    PickListModule, ButtonModule, TagModule, ToastModule, ToggleSwitchModule,
    DialogModule, InputTextModule, TextareaModule, DropdownModule, TooltipModule, ConfirmDialogModule,
    DynamicAttributeFormComponent
  ],
  templateUrl: './category-attributes-config.component.html',
  styleUrls: ['./category-attributes-config.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class CategoryAttributesConfigComponent implements OnInit {
  categoryId = 0;
  category: CategoryModel | null = null;

  isLoading = true;
  isSaving = false;

  source: ConfigItem[] = [];
  target: ConfigItem[] = [];
  // Snapshot of `target`, recomputed only on real changes (load, drag, move, required-toggle) —
  // NOT a getter, so [attributes] gets a stable reference and DynamicAttributeFormComponent
  // doesn't rebuild its form (and lose in-progress preview state) on every change-detection tick.
  previewAttributes: CategoryAttributeModel[] = [];

  // ── New Attribute dialog ────────────────────────────────────────────────
  showNewAttributeDialog = false;
  isCreatingAttribute = false;
  fieldTypeOptions = FIELD_TYPE_PRESETS;
  newAttribute = {
    displayName: '',
    attributeName: '',
    attributeNameTouched: false,   // once the admin edits the key by hand, stop auto-deriving it
    fieldType: FIELD_TYPE_PRESETS[0],
    dropdownOptionsText: '',
    isRequired: false,
    isSearchable: false
  };

  constructor(
    private route: ActivatedRoute,
    private inventoryService: InventoryService,
    private messageService: MessageService,
    private confirmationService: ConfirmationService
  ) {}

  ngOnInit(): void {
    this.categoryId = Number(this.route.snapshot.paramMap.get('id'));
    this.load();
  }

  private load(): void {
    this.isLoading = true;
    forkJoin({
      categories: this.inventoryService.getCategories(),
      attributes: this.inventoryService.getAttributes(),
      linked:     this.inventoryService.getCategoryAttributes(this.categoryId)
    }).subscribe({
      next: ({ categories, attributes, linked }) => {
        this.isLoading = false;
        this.category = (categories.result ?? []).find(c => c.id === this.categoryId) ?? null;

        const all = attributes.result ?? [];
        const linkedList = (linked.result ?? []).slice().sort((a, b) => a.displayOrder - b.displayOrder);
        const linkedUuids = new Set(linkedList.map(l => l.attributeUuid));

        this.target = linkedList.map(l => {
          const def = all.find(a => a.uuid === l.attributeUuid);
          return { ...(def as AttributeDefinitionModel), linkedRequired: l.isRequired };
        });
        this.source = all
          .filter(a => !linkedUuids.has(a.uuid))
          .map(a => ({ ...a, linkedRequired: a.isRequired }));

        this.refreshPreview();
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load attribute configuration.' });
      }
    });
  }

  // ── Live preview — mirrors exactly what the end-user product/variant form will render ────────
  // Called after anything that changes which attributes are linked, their order, or their
  // required-override: initial load, drag/move between panels, target reorder, required toggle.
  refreshPreview(): void {
    this.previewAttributes = this.target.map((item, i) => ({
      categoryId: this.categoryId,
      attributeUuid: item.uuid,
      attributeName: item.attributeName,
      displayName: item.displayName,
      dataType: item.dataType,
      controlType: item.controlType,
      dropdownOptions: item.dropdownOptions,
      validationRegex: item.validationRegex,
      isRequired: item.linkedRequired,
      isSearchable: item.isSearchable,
      displayOrder: i + 1
    }));
  }

  save(): void {
    this.isSaving = true;
    const req = {
      attributes: this.target.map((item, i) => ({
        attributeUuid: item.uuid,
        isRequired: item.linkedRequired,
        displayOrder: i + 1
      }))
    };

    this.inventoryService.setCategoryAttributes(this.categoryId, req).subscribe({
      next: () => {
        this.isSaving = false;
        this.messageService.add({ severity: 'success', summary: 'Saved', detail: 'Category attributes updated.' });
      },
      error: (err) => {
        this.isSaving = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to save.' });
      }
    });
  }

  // ── New Attribute dialog ────────────────────────────────────────────────
  openNewAttributeDialog(): void {
    this.newAttribute = {
      displayName: '',
      attributeName: '',
      attributeNameTouched: false,
      fieldType: FIELD_TYPE_PRESETS[0],
      dropdownOptionsText: '',
      isRequired: false,
      isSearchable: false
    };
    this.showNewAttributeDialog = true;
  }

  // Keeps the internal key (attributeName) in sync with the display label until the admin
  // edits the key field directly — same auto-slug-then-freeze pattern as SKU/code generators
  // elsewhere in this app.
  onDisplayNameChange(): void {
    if (this.newAttribute.attributeNameTouched) return;
    this.newAttribute.attributeName = this.slugify(this.newAttribute.displayName);
  }

  onAttributeNameEdited(): void {
    this.newAttribute.attributeNameTouched = true;
    this.newAttribute.attributeName = this.slugify(this.newAttribute.attributeName);
  }

  private slugify(value: string): string {
    return value
      .trim()
      .toLowerCase()
      .replace(/[^a-z0-9]+/g, '_')
      .replace(/^_+|_+$/g, '');
  }

  get newAttributeCanSave(): boolean {
    const n = this.newAttribute;
    if (!n.displayName.trim() || !n.attributeName.trim()) return false;
    if (n.fieldType.hasOptions && !n.dropdownOptionsText.trim()) return false;
    return true;
  }

  submitNewAttribute(): void {
    if (!this.newAttributeCanSave) return;
    const n = this.newAttribute;

    const req: CreateAttributeDefinitionRequest = {
      attributeName: n.attributeName,
      displayName:   n.displayName.trim(),
      dataType:      n.fieldType.dataType,
      controlType:   n.fieldType.controlType,
      dropdownOptions: n.fieldType.hasOptions
        ? n.dropdownOptionsText.split(',').map(o => o.trim()).filter(o => o.length > 0)
        : undefined,
      isRequired:   n.isRequired,
      isSearchable: n.isSearchable
    };

    this.isCreatingAttribute = true;
    this.inventoryService.createAttribute(req).subscribe({
      next: () => {
        this.isCreatingAttribute = false;
        this.showNewAttributeDialog = false;
        this.messageService.add({ severity: 'success', summary: 'Created', detail: `Attribute '${req.displayName}' added to the catalog.` });
        this.reloadCatalogPreservingLinks();
      },
      error: (err) => {
        this.isCreatingAttribute = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to create attribute.' });
      }
    });
  }

  // Refetches the attribute catalog and re-derives the "Available" panel from it, without
  // touching the in-progress "Linked" panel — a full load() would discard any unsaved
  // drag/drop/required changes the admin made before opening the New Attribute dialog.
  private reloadCatalogPreservingLinks(): void {
    this.inventoryService.getAttributes().subscribe({
      next: (res) => {
        const all = res.result ?? [];
        const linkedUuids = new Set(this.target.map(t => t.uuid));
        this.source = all
          .filter(a => !linkedUuids.has(a.uuid))
          .map(a => ({ ...a, linkedRequired: a.isRequired }));
      },
      error: () => {
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Attribute created, but the catalog failed to refresh — reload the page.' });
      }
    });
  }

  // ── Delete attribute (catalog-wide, not just this category's link) ────────
  confirmDeleteAttribute(item: ConfigItem, event: Event): void {
    event.stopPropagation();
    this.confirmationService.confirm({
      target: event.target as EventTarget,
      message: `Delete attribute <strong>${item.displayName}</strong> from the catalog? This cannot be undone.`,
      header: 'Delete Attribute',
      icon: 'pi pi-exclamation-triangle',
      acceptLabel: 'Delete',
      acceptButtonStyleClass: 'p-button-danger',
      rejectLabel: 'Cancel',
      accept: () => {
        this.inventoryService.deleteAttribute(item.uuid).subscribe({
          next: () => {
            this.source = this.source.filter(a => a.uuid !== item.uuid);
            this.target = this.target.filter(a => a.uuid !== item.uuid);
            this.refreshPreview();
            this.messageService.add({ severity: 'success', summary: 'Deleted', detail: `Attribute '${item.displayName}' deleted.` });
          },
          error: (err) => {
            if (err.status === 409) {
              const conflict: AttributeDeleteConflictResult = err.error?.result ?? { referencedCategoryCount: 0, referencedVariantValueCount: 0 };
              const parts: string[] = [];
              if (conflict.referencedCategoryCount > 0) parts.push(`linked to ${conflict.referencedCategoryCount} categor${conflict.referencedCategoryCount === 1 ? 'y' : 'ies'}`);
              if (conflict.referencedVariantValueCount > 0) parts.push(`has ${conflict.referencedVariantValueCount} recorded product value${conflict.referencedVariantValueCount === 1 ? '' : 's'}`);
              this.messageService.add({
                severity: 'warn', summary: 'Cannot Delete', life: 8000,
                detail: `'${item.displayName}' is still ${parts.join(' and ')}. Unlink it from every category first (and remove it from the "Linked" panel here if shown), then try again.`
              });
            } else {
              this.messageService.add({ severity: 'error', summary: 'Error', detail: err.error?.message || 'Failed to delete attribute.' });
            }
          }
        });
      }
    });
  }
}
