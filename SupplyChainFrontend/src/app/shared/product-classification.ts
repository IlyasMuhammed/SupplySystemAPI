// A30 §6 — product type and supply method, mirroring SMS.Shared.Common.ProductType / SupplyMethod
// and the §6.1 capability table (ProductTypeRules). The server decides; this only keeps the form
// from offering combinations it will refuse, and fills the defaults the server would.

export type ProductTypeCode =
  'STOCK_ITEM' | 'RAW_MATERIAL' | 'COMPONENT' | 'SEMI_FINISHED' | 'FINISHED_GOOD' | 'CONSUMABLE' | 'SERVICE' | 'ASSET';

export type SupplyMethodCode = 'PURCHASE' | 'MANUFACTURE' | 'TRANSFER' | 'SERVICE';

export interface ProductTypeOption {
  value: ProductTypeCode;
  label: string;
  description: string;
  canSell: boolean;
  canPurchase: boolean;
  canStock: boolean;
  canManufacture: boolean;
  canBeBomInput: boolean;
  defaultSupplyMethod: SupplyMethodCode;
}

export const PRODUCT_TYPE_OPTIONS: ProductTypeOption[] = [
  { value: 'STOCK_ITEM',    label: 'Stock item',         description: 'A general item that is bought, stocked and sold as it is.',
    canSell: true,  canPurchase: true,  canStock: true,  canManufacture: false, canBeBomInput: false, defaultSupplyMethod: 'PURCHASE' },
  { value: 'RAW_MATERIAL',  label: 'Raw material',       description: 'Bought in to be consumed by production.',
    canSell: false, canPurchase: true,  canStock: true,  canManufacture: false, canBeBomInput: true,  defaultSupplyMethod: 'PURCHASE' },
  { value: 'COMPONENT',     label: 'Component',          description: 'A part that goes into an assembly.',
    canSell: false, canPurchase: true,  canStock: true,  canManufacture: false, canBeBomInput: true,  defaultSupplyMethod: 'PURCHASE' },
  { value: 'SEMI_FINISHED', label: 'Semi-finished good', description: 'Made here, then used to make something else.',
    canSell: false, canPurchase: false, canStock: true,  canManufacture: true,  canBeBomInput: true,  defaultSupplyMethod: 'MANUFACTURE' },
  { value: 'FINISHED_GOOD', label: 'Finished good',      description: 'Made here and sold. Can also feed another bill of materials.',
    canSell: true,  canPurchase: false, canStock: true,  canManufacture: true,  canBeBomInput: true,  defaultSupplyMethod: 'MANUFACTURE' },
  { value: 'CONSUMABLE',    label: 'Consumable',         description: 'Used up in production but not part of what is produced.',
    canSell: false, canPurchase: true,  canStock: true,  canManufacture: false, canBeBomInput: true,  defaultSupplyMethod: 'PURCHASE' },
  { value: 'SERVICE',       label: 'Service',            description: 'Not held in stock; bought or sold as work done.',
    canSell: true,  canPurchase: true,  canStock: false, canManufacture: false, canBeBomInput: true,  defaultSupplyMethod: 'SERVICE' },
  { value: 'ASSET',         label: 'Asset',              description: 'Equipment the organization owns and uses, not stock.',
    canSell: false, canPurchase: true,  canStock: false, canManufacture: false, canBeBomInput: false, defaultSupplyMethod: 'PURCHASE' }
];

export const SUPPLY_METHOD_OPTIONS: { value: SupplyMethodCode; label: string; description: string }[] = [
  { value: 'PURCHASE',    label: 'Purchase',    description: 'A purchase order is raised when it runs short.' },
  { value: 'MANUFACTURE', label: 'Manufacture', description: 'A production order is raised when it runs short.' },
  { value: 'TRANSFER',    label: 'Transfer',    description: 'Moved in from another warehouse.' },
  { value: 'SERVICE',     label: 'Service',     description: 'Provided by an external party.' }
];

export function productTypeOption(code: string | null | undefined): ProductTypeOption | undefined {
  return PRODUCT_TYPE_OPTIONS.find(o => o.value === code);
}

export function productTypeLabel(code: string | null | undefined): string {
  return productTypeOption(code)?.label ?? (code || '—');
}

export function supplyMethodLabel(code: string | null | undefined): string {
  return SUPPLY_METHOD_OPTIONS.find(o => o.value === code)?.label ?? (code || '—');
}

/** The combinations the server refuses (A30 §6.5), so the form does not offer them. */
export function isSupplyMethodAllowed(type: string | null | undefined, method: SupplyMethodCode): boolean {
  const option = productTypeOption(type);
  if (!option) return true;
  if (method === 'MANUFACTURE') return option.canManufacture;
  return option.value !== 'FINISHED_GOOD';
}

export interface ClassificationDefaults {
  supplyMethod: SupplyMethodCode;
  isSaleable: boolean;
  isPurchasable: boolean;
  isStockable: boolean;
}

/** What a product of this type gets when nothing else is chosen — the same defaults the server applies. */
export function classificationDefaults(type: string | null | undefined): ClassificationDefaults {
  const option = productTypeOption(type) ?? PRODUCT_TYPE_OPTIONS[0];
  return {
    supplyMethod: option.defaultSupplyMethod,
    isSaleable: option.canSell,
    isPurchasable: option.canPurchase,
    isStockable: option.canStock
  };
}
