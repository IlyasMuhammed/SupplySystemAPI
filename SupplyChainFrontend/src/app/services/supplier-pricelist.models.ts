/**
 * A45 (C4/C6) — supplier pricelist wire models. Frozen contract: docs/pricelist/ADDENDUM-45-ANALYSIS.md Part B.
 * Rows are identified by `uuid`; money is in the row's own currency (no conversion, D-5).
 */

/** 0 = the PO's order date selects the price, 1 = the line's expected delivery date. */
export type PricingDateBasis = 0 | 1;

export const PRICING_DATE_BASIS_OPTIONS: { label: string; value: PricingDateBasis }[] = [
  { label: 'Order Date', value: 0 },
  { label: 'Delivery Date', value: 1 }
];

export interface PricelistItem {
  uuid: string;
  productUuid: string;
  productName: string;
  productSku?: string | null;
  variantUuid?: string | null;
  variantName?: string | null;
  supplierUuid: string;
  supplierName: string;
  vendorProductCode?: string | null;
  vendorProductName?: string | null;
  minQuantity: number;
  unitPrice: number;
  currencyId: string;
  currencyCode: string;
  pricingDateBasis: PricingDateBasis;
  pricingDateBasisLabel?: string;
  /** yyyy-MM-dd */
  validFrom: string;
  /** yyyy-MM-dd */
  validTo: string;
  leadTimeDays?: number | null;
  priority: number;
  notes?: string | null;
  isActive: boolean;
  createdAt?: string;
  modifiedAt?: string | null;
  /** base64; echoed back on PUT for the concurrency check. */
  rowVersion?: string;
}

/** Create / update body (`rowVersion` only on PUT). */
export interface PricelistUpsert {
  productUuid: string;
  variantUuid?: string | null;
  supplierUuid: string;
  vendorProductCode?: string | null;
  vendorProductName?: string | null;
  minQuantity: number;
  unitPrice: number;
  currencyId: string;
  pricingDateBasis: PricingDateBasis;
  validFrom: string;
  validTo: string;
  leadTimeDays?: number | null;
  priority: number;
  notes?: string | null;
  isActive: boolean;
  rowVersion?: string;
}

/** Create / update response: an overlap (C6) is a warning, never a block. */
export interface PricelistSaveResult {
  item: PricelistItem;
  overlapWarning?: string | null;
}

export interface PricelistFilter {
  productUuid?: string | null;
  variantUuid?: string | null;
  supplierUuid?: string | null;
  activeOnly?: boolean;
  search?: string | null;
  page?: number;
  pageSize?: number;
}

export interface PricelistPage {
  items: PricelistItem[];
  total: number;
}

/** The editable fields as the dialog holds them (dates as Date objects until saved). */
export interface PricelistFormValue {
  productUuid: string | null;
  variantUuid: string | null;
  supplierUuid: string | null;
  vendorProductCode: string | null;
  vendorProductName: string | null;
  minQuantity: number | null;
  unitPrice: number | null;
  currencyId: string | null;
  pricingDateBasis: PricingDateBasis;
  validFrom: Date | null;
  validTo: Date | null;
  leadTimeDays: number | null;
  priority: number | null;
  notes: string | null;
  isActive: boolean;
}

export type PricelistErrors = Partial<Record<keyof PricelistFormValue, string>>;

/** Client-side rules of spec §4 C4 / TS-22 / TS-23. An empty object means valid. */
export function validatePricelist(f: PricelistFormValue): PricelistErrors {
  const e: PricelistErrors = {};
  if (!f.productUuid) e.productUuid = 'Product is required.';
  if (!f.supplierUuid) e.supplierUuid = 'Vendor is required.';
  if (f.minQuantity == null || !(f.minQuantity >= 0)) e.minQuantity = 'Minimum quantity must be 0 or more.';
  if (f.unitPrice == null || !(f.unitPrice > 0)) e.unitPrice = 'Unit price must be greater than 0.';
  if (!f.currencyId) e.currencyId = 'Currency is required.';
  if (!f.validFrom) e.validFrom = 'Valid from is required.';
  if (!f.validTo) e.validTo = 'Valid to is required.';
  if (f.validFrom && f.validTo && f.validTo.getTime() < f.validFrom.getTime()) e.validTo = 'Valid to must be on or after valid from.';
  if (f.leadTimeDays != null && !(f.leadTimeDays >= 0)) e.leadTimeDays = 'Lead time cannot be negative.';
  if (f.priority == null || !(f.priority >= 0)) e.priority = 'Priority must be 0 or more.';
  return e;
}

/** Priority ASC (lowest wins), then Valid From DESC (A45-0160). */
export function sortPricelist(items: PricelistItem[]): PricelistItem[] {
  return [...items].sort((a, b) => a.priority - b.priority || (b.validFrom ?? '').localeCompare(a.validFrom ?? ''));
}
