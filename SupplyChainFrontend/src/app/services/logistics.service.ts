import { Injectable } from '@angular/core';
import { HttpClient, HttpParams } from '@angular/common/http';
import { Observable } from 'rxjs';
import { environment } from '../../environments/environment';

const BASE = `${environment.apiUrl}/logistics`;

export interface ApiResponse<T = null> {
  success: boolean;
  message: string;
  result: T;
}

export interface PaginatedResponse<T> {
  data: T[];
  totalRecords: number;
  page: number;
  pageSize: number;
  totalPages: number;
}

// ── Carrier models ────────────────────────────────────────────────────────────

export interface CreateCarrierRequest {
  name: string;
  code: string;
  serviceType?: string;
  trackingUrlTemplate?: string;
  contactName?: string;
  contactPhone?: string;
  contactEmail?: string;
}

export interface PatchCarrierRequest {
  name?: string;
  serviceType?: string;
  trackingUrlTemplate?: string;
  contactName?: string;
  contactPhone?: string;
  contactEmail?: string;
  status?: string;
  isActive?: boolean;
  /** Which supplier this carrier is, in Finance's books. Pass clearSupplierLink to unset it. */
  supplierId?: string;
  clearSupplierLink?: boolean;
}

export interface CarrierListItemModel {
  uuid: string;
  name: string;
  code: string;
  serviceType?: string;
  status: string;
  isActive: boolean;
}

export interface CarrierDetailModel {
  uuid: string;
  name: string;
  code: string;
  serviceType?: string;
  trackingUrlTemplate?: string;
  contactName?: string;
  contactPhone?: string;
  contactEmail?: string;
  status: string;
  isActive: boolean;
  createdDate: string;
  /** Null means approved freight bills for this carrier cannot be posted as Finance payables. */
  supplierId?: string;
}

export interface CarrierFilter {
  search?: string;
  status?: string;
  page?: number;
  pageSize?: number;
}

// ── Shipment models ───────────────────────────────────────────────────────────

export interface CreateShipmentRequest {
  poUuid: string;
  carrierUuid?: string;
  shipmentType: string;
  dispatchDate: string;
  estimatedArrival: string;
  trackingNumber?: string;
  originWarehouseUuid?: string;
  destinationAddress: string;
  weightKg?: number;
  volumeCbm?: number;
  freightCost?: number;
  notes?: string;
}

export interface PatchShipmentRequest {
  shipmentType?: string;
  dispatchDate?: string;
  estimatedArrival?: string;
  actualArrival?: string;
  trackingNumber?: string;
  trackingUrl?: string;
  destinationAddress?: string;
  weightKg?: number;
  volumeCbm?: number;
  freightCost?: number;
  status?: string;
  notes?: string;
  carrierUuid?: string;
}

export interface ShipmentListItemModel {
  uuid: string;
  shipmentNumber: string;
  poNumber: string;
  carrierName?: string;
  shipmentType: string;
  dispatchDate: string;
  estimatedArrival: string;
  actualArrival?: string;
  status: string;
  trackingNumber?: string;
}

export interface ShipmentDetailModel {
  uuid: string;
  shipmentNumber: string;
  poUuid: string;
  poNumber: string;
  carrierUuid?: string;
  carrierName?: string;
  shipmentType: string;
  dispatchDate: string;
  estimatedArrival: string;
  actualArrival?: string;
  trackingNumber?: string;
  trackingUrl?: string;
  originWarehouseUuid?: string;
  destinationAddress: string;
  weightKg?: number;
  volumeCbm?: number;
  freightCost?: number;
  status: string;
  notes?: string;
  createdDate: string;
}

export interface ShipmentFilter {
  status?: string;
  search?: string;
  carrierUuid?: string;
  page?: number;
  pageSize?: number;
}

// ── Address ───────────────────────────────────────────────────────────────────

export interface AddressRequest {
  line1: string;
  line2?: string;
  cityId?: string;
  cityName: string;
  state?: string;
  postalCode?: string;
  countryName: string;
  /** ISO-3166 alpha-2, e.g. 'PK'. Needed to read a local phone number. */
  countryIsoCode?: string;
  contactName?: string;
  contactPhone?: string;
  contactEmail?: string;
  latitude?: number;
  longitude?: number;
  addressType?: string;
  consigneeUuid?: string;
}

export interface AddressModel {
  uuid: string;
  line1: string;
  line2?: string;
  cityId?: string;
  cityName: string;
  state?: string;
  postalCode?: string;
  countryName: string;
  countryIsoCode?: string;
  contactName?: string;
  contactPhone?: string;
  /** The normalised form. Null when the number could not be read. */
  contactPhoneE164?: string;
  contactEmail?: string;
  addressType: string;
  /** UNVALIDATED | VALID | INVALID */
  validationStatus: string;
  /** Why it is not valid, in plain words, for whoever has to fix it. */
  validationNotes?: string;
}

// ── Delivery models ───────────────────────────────────────────────────────────

export interface CreateDeliveryLineRequest {
  variantUuid?: string;
  productUuid?: string;
  itemDescription: string;
  unitOfMeasure?: string;
  qtyOrdered: number;
  batchNumber?: string;
  serialNumber?: string;
  sourceLineUuid?: string;
  binUuid?: string;
  unitValue?: number;
  isHazardous?: boolean;
  isFragile?: boolean;
  isTemperatureControlled?: boolean;
}

export interface CreateDeliveryRequest {
  /** PO | SRO | MIV | TRANSFER | MANUAL. Defaults to MANUAL. */
  sourceType?: string;
  sourceUuid?: string;
  sourceNumber?: string;
  /** Required for MANUAL only — every other source implies its own direction. */
  direction?: string;
  shipFromAddress?: AddressRequest;
  shipToAddress?: AddressRequest;
  /** A TRANSFER needs both, and they must differ. */
  shipFromWarehouseUuid?: string;
  shipToWarehouseUuid?: string;
  requestedDate?: string;
  promisedDate?: string;
  priority?: string;
  incoterm?: string;
  notes?: string;
  lines: CreateDeliveryLineRequest[];
}

/** Picks one source line. Omit `qty` to take the whole outstanding balance. */
export interface SourceLineSelection {
  sourceLineUuid: string;
  qty?: number;
}

export interface CreateDeliveryFromSourceRequest {
  /** PO | SRO | MIV | SALE_ORDER. TRANSFER and MANUAL have no source document — use createDelivery. */
  sourceType: string;
  sourceUuid: string;
  /** SALE_ORDER only: SHIP or SELF_PICKUP. Omit to take the order's own mode. */
  deliveryMode?: string;
  shipFromWarehouseUuid?: string;
  shipToWarehouseUuid?: string;
  shipFromAddress?: AddressRequest;
  shipToAddress?: AddressRequest;
  requestedDate?: string;
  promisedDate?: string;
  priority?: string;
  incoterm?: string;
  notes?: string;
  /** Omit to advise every outstanding line in full. */
  lines?: SourceLineSelection[];
}

export interface PatchDeliveryRequest {
  requestedDate?: string;
  promisedDate?: string;
  priority?: string;
  incoterm?: string;
  notes?: string;
  shipToAddress?: AddressRequest;
}

/** Hold, cancel and short-close all require a reason. */
export interface DeliveryReasonRequest {
  reason: string;
}

export interface DeliveryLineModel {
  uuid: string;
  lineNo: number;
  variantUuid?: string;
  productUuid?: string;
  itemDescription: string;
  unitOfMeasure?: string;
  qtyOrdered: number;
  qtyPicked: number;
  qtyPacked: number;
  qtyShipped: number;
  qtyDelivered: number;
  qtyShort: number;
  shortReason?: string;
  batchNumber?: string;
  serialNumber?: string;
  sourceLineUuid?: string;
  /** The sale order line this line fulfils. Null unless the delivery is for a sale order. */
  soLineUuid?: string;
  unitValue?: number;
  isHazardous: boolean;
  isFragile: boolean;
  isTemperatureControlled: boolean;
}

export interface DeliveryListItemModel {
  uuid: string;
  deliveryNumber: string;
  direction: string;
  sourceType: string;
  sourceNumber?: string;
  /** SHIP or SELF_PICKUP for a sale-order delivery; null otherwise. */
  deliveryMode?: string;
  status: string;
  priority: string;
  requestedDate?: string;
  promisedDate?: string;
  shipToCity?: string;
  lineCount: number;
  /** Backfilled from a legacy shipment: the header is real, the lines were never recorded. */
  linesUnknown: boolean;
  createdDate: string;
}

export interface DeliveryDetailModel {
  uuid: string;
  deliveryNumber: string;
  traceId: string;
  direction: string;
  sourceType: string;
  sourceUuid?: string;
  sourceNumber?: string;
  /** Derived from the source type — whether this delivery posts the stock movement itself. */
  postsGoodsIssue: boolean;
  /** Set only when the delivery fulfils a sale order. */
  saleOrderUuid?: string;
  /** SHIP or SELF_PICKUP for a sale-order delivery; null otherwise. */
  deliveryMode?: string;
  /** Who collected a self-pickup delivery, and when. Null until it has been collected. */
  pickupPersonName?: string;
  pickupPersonIdType?: string;
  pickupPersonIdNumber?: string;
  pickupAuthorization?: string;
  pickedUpAt?: string;
  shipFromAddress?: AddressModel;
  shipToAddress?: AddressModel;
  requestedDate?: string;
  promisedDate?: string;
  priority: string;
  incoterm?: string;
  status: string;
  statusBeforeHold?: string;
  holdReason?: string;
  linesUnknown: boolean;
  notes?: string;
  /** Straight from the server's state machine — drive buttons from this, never a local list. */
  allowedNextStatuses: string[];
  /** The consignments carrying this delivery, oldest first. Empty until one is created. */
  consignments: DeliveryConsignmentModel[];
  createdDate: string;
  modifiedDate?: string;
  lines: DeliveryLineModel[];
}

/** A consignment as the delivery sees it — enough to name it, link to it and show where it is. */
export interface DeliveryConsignmentModel {
  consignmentUuid: string;
  consignmentNumber: string;
  status: string;
  carrierName?: string;
  masterAwb?: string;
}

// ── Self-pickup collection (A29 §8.2) ─────────────────────────────────────────

export interface RecordPickupRequest {
  pickupPersonName: string;
  /** CNIC, LICENSE or PASSPORT. */
  pickupPersonIdType: string;
  pickupPersonIdNumber: string;
  /** Optional: the letter or person authorising a collector who is not the customer. */
  pickupAuthorization?: string;
}

export interface PickupResultModel {
  deliveryUuid: string;
  deliveryNumber: string;
  status: string;
  pickedUpAt: string;
  pickupPersonName: string;
  /** Set when this call issued the stock; null when it had been issued before the customer came. */
  goodsIssue?: GoodsIssueResultModel;
  /** What the sale order became — PARTIALLY_FULFILLED or FULFILLED. Null if it could not be updated. */
  saleOrderStatus?: string;
}

/** The ID types the server accepts for a collector. */
export const PICKUP_ID_TYPES: { code: string; label: string }[] = [
  { code: 'CNIC',     label: 'CNIC' },
  { code: 'LICENSE',  label: 'Driving licence' },
  { code: 'PASSPORT', label: 'Passport' }
];

export interface DeliveryFilter {
  status?: string;
  direction?: string;
  sourceType?: string;
  search?: string;
  fromDate?: string;
  toDate?: string;
  page?: number;
  pageSize?: number;
}

// ── Consignment booking, label and tracking models ────────────────────────────

export interface BookConsignmentRequest {
  /** The account to book on. Defaults to the carrier's default account. */
  carrierAccountUuid?: string;
  /** Overrides the consignment's service code, then the account's default. */
  serviceCode?: string;
}

/** A person's answer, after checking with the carrier, to a booking whose outcome is unknown. */
export interface ResolveBookingRequest {
  /** True if the carrier has the booking; false if it has no record of it. */
  carrierBooked: boolean;
  /** The airway bill the carrier confirmed. Required when `carrierBooked` is true. */
  awb?: string;
  /** How it was confirmed — who at the carrier, or what its portal shows. Required. */
  note: string;
}

export interface ConsignmentBookingStatusModel {
  consignmentUuid: string;
  consignmentNumber: string;
  /** The consignment's status — BOOKING while a carrier call is under way. */
  status: string;
  masterAwb?: string;
  trackingUrl?: string;
  /** Why the last attempt did not book, or what is being waited on. Null once booked. */
  failureReason?: string;
  carrierAccountUuid?: string;
  carrierAccountName?: string;
  isSandbox: boolean;
  /** The latest carrier call — IN_FLIGHT, SUCCEEDED, REFUSED, UNKNOWN, NOT_PERFORMED. */
  commandStatus?: string;
  attemptCount: number;
  firstAttemptAt?: string;
  lastAttemptAt?: string;
  hasStoredLabel: boolean;
  labelStoredAt?: string;
  /** The outcome is unknown and will not be retried: a person must check with the carrier. */
  needsResolution: boolean;
  /** Unknown, but the carrier deduplicates, so it retries without anyone acting. */
  willRetryAutomatically: boolean;
}

export interface ConsignmentTrackingEventModel {
  milestone: string;
  carrierStatus?: string;
  description?: string;
  location?: string;
  signedBy?: string;
  occurredAt: string;
  receivedAt: string;
  /** WEBHOOK or POLL — how we came to know. */
  source: string;
  appliedStatus?: string;
}

/** @param polled False when the carrier was asked moments ago and was not asked again. */
export interface TrackingRefreshModel {
  polled: boolean;
  status: string;
  newEvents: number;
  error?: string;
  lastPolledAt?: string;
  nextPollAt?: string;
}

export interface StuckConsignmentModel {
  consignmentUuid: string;
  consignmentNumber: string;
  carrierName?: string;
  status: string;
  masterAwb?: string;
  stuckSince: string;
  stuckReason: string;
  lastTrackingEventAt?: string;
  trackingPollFailures: number;
  trackingLastError?: string;
}

// ── Carrier account models ────────────────────────────────────────────────────

export interface CreateCarrierAccountRequest {
  carrierUuid: string;
  accountName: string;
  accountNumber?: string;
  defaultServiceCode?: string;
  isDefault?: boolean;
  isSandbox?: boolean;
  /** Null leaves it to the adapter; false switches it off for this account. */
  codEnabled?: boolean | null;
  labelsEnabled?: boolean | null;
  trackingEnabled?: boolean | null;
  cancellationEnabled?: boolean | null;
  pickupBookingEnabled?: boolean | null;
  notes?: string;
}

export interface PatchCarrierAccountRequest {
  accountName?: string;
  accountNumber?: string;
  defaultServiceCode?: string;
  isDefault?: boolean;
  isSandbox?: boolean;
  isActive?: boolean;
  codEnabled?: boolean | null;
  labelsEnabled?: boolean | null;
  trackingEnabled?: boolean | null;
  cancellationEnabled?: boolean | null;
  pickupBookingEnabled?: boolean | null;
  notes?: string;
  /**
   * Capabilities to stop overriding. A null in the flags above means "leave alone" on a patch,
   * so this is the only way to hand one back to the adapter.
   */
  clearOverrides?: string[];
}

export interface CarrierCapabilityModel {
  name: string;
  /** What the adapter can do. */
  supportedByProvider: boolean;
  /** The account's override; null means it defers to the adapter. */
  enabledOnAccount: boolean | null;
  /** What actually applies. Never true when the provider cannot do it. */
  effective: boolean;
}

export interface CarrierAccountModel {
  uuid: string;
  carrierUuid: string;
  carrierName: string;
  accountName: string;
  accountNumber?: string;
  defaultServiceCode?: string;
  isDefault: boolean;
  isSandbox: boolean;
  isActive: boolean;
  providerKey?: string;
  providerDisplayName?: string;
  /** Set when no adapter is registered for the carrier — nothing can book on the account. */
  providerWarning?: string;
  capabilities: CarrierCapabilityModel[];
  notes?: string;
  createdDate: string;
}

export interface SetCarrierCredentialRequest {
  key: string;
  value: string;
  description?: string;
  expiresAt?: string;
}

/**
 * A credential as the outside world may see it: everything except the credential.
 *
 * There is deliberately no value and no masked preview — a mask still discloses length and shape.
 * A credential that cannot be read back can only be replaced, which is the correct affordance.
 */
// ── Which adapter a carrier books through ─────────────────────────────────────

/** One credential an adapter reads from its account — what to enter, never a value. */
export interface CourierCredentialSpecModel {
  key: string;
  description: string;
  required: boolean;
  isSecret: boolean;
}

/** A courier adapter this deployment has, and what it can do. */
export interface CourierProviderModel {
  key: string;
  displayName: string;
  supportsBooking: boolean;
  supportsRating: boolean;
  supportsTracking: boolean;
  supportsLabels: boolean;
  supportsCancellation: boolean;
  supportsCod: boolean;
  supportsMultiPiece: boolean;
  /** What to add under the account's credentials before this adapter can book. */
  credentials: CourierCredentialSpecModel[];
}

export interface CarrierIntegrationModel {
  carrierUuid: string;
  carrierName: string;
  /** MANUAL or API. */
  integrationMode: string;
  providerKey?: string;
  providerDisplayName?: string;
  /** Set when the carrier names an adapter nothing registers. */
  warning?: string;
}

export interface SetCarrierIntegrationRequest {
  integrationMode: string;
  /** Required for API; ignored for MANUAL. */
  providerKey?: string;
}

export interface CarrierCredentialModel {
  uuid: string;
  key: string;
  description?: string;
  expiresAt?: string;
  /** Every booking on this account fails once it has expired. */
  isExpired: boolean;
  setAt: string;
  setBy: number;
}

/** The capability names the server will accept in `clearOverrides`. */
export const CARRIER_CAPABILITIES: { code: string; label: string; hint: string }[] = [
  { code: 'COD',            label: 'Cash on delivery', hint: 'Collect payment from the consignee' },
  { code: 'LABELS',         label: 'Labels',           hint: 'Fetch the carrier’s own label' },
  { code: 'TRACKING',       label: 'Tracking',         hint: 'Poll the carrier for milestones' },
  { code: 'CANCELLATION',   label: 'Cancellation',     hint: 'Cancel a booking through the API' },
  { code: 'PICKUP_BOOKING', label: 'Pickup booking',   hint: 'Ask the carrier to collect' }
];

// ── Warehouse execution models ────────────────────────────────────────────────

/** BLOCK refuses a release that cannot be covered; SPLIT ships what it can and drafts the balance. */
export type ShortageAction = 'BLOCK' | 'SPLIT';

export interface ReleaseDeliveryRequest {
  onShortage?: ShortageAction;
}

export interface DeliveryAvailabilityLineModel {
  lineUuid: string;
  lineNo: number;
  itemDescription: string;
  variantUuid?: string;
  qtyOrdered: number;
  qtyAvailable: number;
  shortfall: number;
  warehouseName?: string;
  warning?: string;
}

export interface DeliveryAvailabilityModel {
  deliveryUuid: string;
  deliveryNumber: string;
  status: string;
  requiresStock: boolean;
  canReleaseInFull: boolean;
  canReleasePartially: boolean;
  lines: DeliveryAvailabilityLineModel[];
}

export interface GeneratePickListRequest {
  assignedToUserId?: number;
  notes?: string;
}

export interface PickListLineModel {
  uuid: string;
  seqNo: number;
  deliveryLineUuid: string;
  deliveryLineNo: number;
  variantUuid?: string;
  itemDescription: string;
  unitOfMeasure?: string;
  zoneName?: string;
  binCode?: string;
  batchNumber?: string;
  serialNumber?: string;
  expiryDate?: string;
  qtyToPick: number;
  qtyPicked: number;
  qtyShort: number;
  shortReasonCode?: string;
  shortReason?: string;
  pickedAt?: string;
  /** False until the picker has answered this instruction, whatever the answer was. */
  isConfirmed: boolean;
}

export interface PickListModel {
  uuid: string;
  pickListNumber: string;
  status: string;
  deliveryUuid: string;
  deliveryNumber: string;
  warehouseUuid: string;
  warehouseName?: string;
  assignedToUserId?: number;
  generatedAt: string;
  startedAt?: string;
  completedAt?: string;
  notes?: string;
  cancelReason?: string;
  lines: PickListLineModel[];
}

export interface PickListListItemModel {
  uuid: string;
  pickListNumber: string;
  status: string;
  deliveryUuid: string;
  deliveryNumber: string;
  warehouseName?: string;
  assignedToUserId?: number;
  lineCount: number;
  qtyToPick: number;
  qtyPicked: number;
  generatedAt: string;
}

export interface PickListFilter {
  status?: string;
  warehouseUuid?: string;
  assignedToUserId?: number;
  search?: string;
  page?: number;
  pageSize?: number;
}

export interface ConfirmPickLineRequest {
  lineUuid: string;
  qtyPicked: number;
  /** Required whenever less was picked than the instruction asked for. */
  shortReasonCode?: string;
  shortNote?: string;
}

export interface ConfirmPickRequest {
  lines: ConfirmPickLineRequest[];
}

export interface ConfirmPickResultModel {
  pickListUuid: string;
  pickListStatus: string;
  deliveryStatus: string;
  completed: boolean;
  linesConfirmed: number;
  linesOutstanding: number;
  qtyPicked: number;
  qtyShort: number;
  qtyReturnedToStock: number;
}

/** The closed vocabulary the server accepts for a short pick. */
export const PICK_SHORT_REASONS: { code: string; label: string }[] = [
  { code: 'NOT_FOUND',      label: 'Nothing in the bin' },
  { code: 'SHORT_ON_SHELF', label: 'Some there, not enough' },
  { code: 'DAMAGED',        label: 'Damaged' },
  { code: 'EXPIRED',        label: 'Expired' },
  { code: 'QUALITY_HOLD',   label: 'Quality hold' },
  { code: 'WRONG_ITEM',     label: 'Wrong item in the bin' },
  { code: 'OTHER',          label: 'Other' }
];

export interface PackContentRequest {
  deliveryLineUuid: string;
  qty: number;
  batchNumber?: string;
  serialNumber?: string;
}

export interface PackRequest {
  packageBarcode?: string;
  packageType?: string;
  lengthCm?: number;
  widthCm?: number;
  heightCm?: number;
  grossWeightKg?: number;
  netWeightKg?: number;
  declaredValue?: number;
  sealNumber?: string;
  parentPackageUuid?: string;
  contents: PackContentRequest[];
}

export interface PatchPackageRequest {
  packageType?: string;
  lengthCm?: number;
  widthCm?: number;
  heightCm?: number;
  grossWeightKg?: number;
  netWeightKg?: number;
  declaredValue?: number;
  sealNumber?: string;
  parentPackageUuid?: string;
  clearParent?: boolean;
}

export interface PackageContentModel {
  uuid: string;
  deliveryLineUuid: string;
  deliveryLineNo: number;
  variantUuid?: string;
  itemDescription: string;
  unitOfMeasure?: string;
  qty: number;
  batchNumber?: string;
  serialNumber?: string;
}

export interface PackageModel {
  uuid: string;
  packageBarcode: string;
  packageType: string;
  deliveryUuid: string;
  deliveryNumber: string;
  lengthCm?: number;
  widthCm?: number;
  heightCm?: number;
  grossWeightKg?: number;
  netWeightKg?: number;
  /** Null until a carrier service with a dim divisor rates it (Phase 3). */
  dimWeightKg?: number;
  volumeM3?: number;
  declaredValue?: number;
  sealNumber?: string;
  parentPackageUuid?: string;
  parentPackageBarcode?: string;
  childPackageBarcodes: string[];
  isVoided: boolean;
  voidReason?: string;
  createdDate: string;
  contents: PackageContentModel[];
}

export interface PackingLineModel {
  deliveryLineUuid: string;
  lineNo: number;
  variantUuid?: string;
  itemDescription: string;
  unitOfMeasure?: string;
  qtyPicked: number;
  qtyPacked: number;
  /** Picked but not yet in a box — what the packer still has in front of them. */
  qtyToPack: number;
}

export interface DeliveryPackingModel {
  deliveryUuid: string;
  deliveryNumber: string;
  status: string;
  isFullyPacked: boolean;
  qtyPicked: number;
  qtyPacked: number;
  qtyUnpacked: number;
  totalGrossWeightKg: number;
  lines: PackingLineModel[];
  packages: PackageModel[];
}

/** The package types the server accepts. */
export const PACKAGE_TYPES = ['BOX', 'PALLET', 'CRATE', 'ENVELOPE', 'DRUM', 'BAG', 'LOOSE'];

export interface GoodsIssueResultModel {
  deliveryUuid: string;
  deliveryNumber: string;
  status: string;
  /** False when the source document already posted the movement — an MIV issue or SRO dispatch. */
  postedStock: boolean;
  movementsPosted: number;
  reservationsClosed: number;
  qtyShipped: number;
  qtyOut: number;
  qtyIn: number;
  note?: string;
}

// ── Consignment models ────────────────────────────────────────────────────────

export interface CreateConsignmentRequest {
  carrierUuid?: string;
  carrierServiceCode?: string;
  mode?: string;
  freightTerms?: string;
  codAmount?: number;
  codCurrency?: string;
  pickupWindowStart?: string;
  pickupWindowEnd?: string;
  etd?: string;
  eta?: string;
  vehicleNumber?: string;
  driverName?: string;
  driverPhone?: string;
  notes?: string;
  deliveryUuids?: string[];
}

/** An airway bill obtained from the carrier outside the system. */
export interface ManualBookingRequest {
  awb: string;
  carrierReference?: string;
}

export interface ConsignmentDeliveryModel {
  deliveryUuid: string;
  deliveryNumber: string;
  status: string;
  sequence: number;
}

export interface ConsignmentDetailModel {
  uuid: string;
  consignmentNumber: string;
  carrierUuid?: string;
  carrierName?: string;
  carrierServiceCode?: string;
  /** MANUAL | API | FILE — whether to offer manual AWB entry or a booking button. */
  integrationMode: string;
  mode: string;
  masterAwb?: string;
  carrierReference?: string;
  trackingUrl?: string;
  freightTerms: string;
  codAmount?: number;
  codCurrency?: string;
  pickupWindowStart?: string;
  pickupWindowEnd?: string;
  etd?: string;
  eta?: string;
  vehicleNumber?: string;
  driverName?: string;
  driverPhone?: string;
  status: string;
  notes?: string;
  allowedNextStatuses: string[];
  createdDate: string;
  deliveries: ConsignmentDeliveryModel[];
}

// ── Rating (T-44 … T-49) ──────────────────────────────────────────────────────

/** One line of what a consignment costs — the base carriage, then each surcharge. */
export interface ConsignmentChargeModel {
  code: string;
  description?: string;
  amount: number;
}

export interface RateAttemptModel {
  /** CARRIER or RATE_CARD. */
  source: string;
  succeeded: boolean;
  message?: string;
}

export interface ConsignmentRateModel {
  consignmentUuid: string;
  consignmentNumber: string;
  status: string;
  /** False when nothing has priced this consignment yet. */
  isRated: boolean;
  freightCost?: number;
  freightCurrency?: string;
  ratedAt?: string;
  /** CARRIER, RATE_CARD or MANUAL — the three carry different weight in a dispute. */
  source?: string;
  note?: string;
  serviceCode?: string;
  chargeableWeightKg?: number;
  transitDays?: number;
  estimatedDelivery?: string;
  charges: ConsignmentChargeModel[];
  attempts: RateAttemptModel[];
  warnings: string[];
}

export interface RateConsignmentRequest {
  serviceCode?: string;
  carrierAccountUuid?: string;
  shipDate?: string;
  /** Skips the carrier's rate API and prices from the card. */
  rateCardOnly?: boolean;
}

export interface ManualRateRequest {
  amount: number;
  currency: string;
  /** Required: a keyed-in price with no provenance cannot be checked against an invoice. */
  note: string;
  serviceCode?: string;
}

export interface PackageWeightModel {
  packageUuid: string;
  packageBarcode: string;
  packageType: string;
  lengthCm?: number;
  widthCm?: number;
  heightCm?: number;
  actualKg?: number;
  volumetricKg?: number;
  chargeableKg?: number;
  /** ACTUAL, VOLUMETRIC, MINIMUM or UNKNOWN. */
  basis: string;
  divisorUsed?: number;
  longestSideCm?: number;
  lengthPlusGirthCm?: number;
  warnings: string[];
}

export interface ConsignmentWeightModel {
  consignmentUuid: string;
  consignmentNumber: string;
  carrierName?: string;
  carrierServiceCode?: string;
  resolvedServiceCode?: string;
  resolvedServiceName?: string;
  dimDivisor?: number;
  minimumChargeableKg?: number;
  weightRoundingKg?: number;
  chargesVolumetricWeight: boolean;
  packageCount: number;
  totalActualKg: number;
  totalVolumetricKg: number;
  totalChargeableKg: number;
  /** False when any package could not be rated — the total is then a floor, not a quote. */
  isComplete: boolean;
  lastRatedAt?: string;
  warnings: string[];
  packages: PackageWeightModel[];
}

export interface RateShopRequest {
  carrierUuids?: string[];
  serviceCode?: string;
  shipDate?: string;
  /** Options arriving later are still returned, under `excluded`, with the reason. */
  requiredBy?: string;
  /** CHEAPEST (the default) or FASTEST. */
  strategy?: string;
  rateCardOnly?: boolean;
}

export interface RateShopOptionModel {
  carrierUuid: string;
  carrierName: string;
  carrierAccountUuid?: string;
  carrierAccountName?: string;
  serviceCode: string;
  serviceName?: string;
  /** CARRIER or RATE_CARD. */
  source: string;
  totalAmount: number;
  currency: string;
  baseAmount?: number;
  surcharges: ConsignmentChargeModel[];
  transitDays?: number;
  estimatedDelivery?: string;
  isGuaranteed: boolean;
  /** 1 is the winner. Null when the option could not be ranked — see `note`. */
  rank?: number;
  moreThanBest?: number;
  moreThanBestPercent?: number;
  /** Why this option is where it is — and, on the winner, why it won. */
  note?: string;
}

/** A carrier or service that produced no comparable option, and why. */
export interface RateShopExclusionModel {
  carrierUuid: string;
  carrierName: string;
  serviceCode?: string;
  reason: string;
  /** The price it would have been, when it was quoted and then ruled out on a constraint. */
  totalAmount?: number;
  currency?: string;
}

export interface RateShopResultModel {
  consignmentUuid: string;
  consignmentNumber: string;
  chargeableWeightKg: number;
  shipDate: string;
  requiredBy?: string;
  strategy: string;
  options: RateShopOptionModel[];
  excluded: RateShopExclusionModel[];
  recommended?: RateShopOptionModel;
  /** Why it won, in one sentence. */
  recommendation?: string;
  warnings: string[];
}

export interface AcceptRateRequest {
  carrierUuid: string;
  carrierAccountUuid?: string;
  serviceCode: string;
  source?: string;
  shipDate?: string;
}

export interface ShippingRuleVerdictModel {
  ruleUuid: string;
  name: string;
  priority: number;
  matched: boolean;
  /** Why it matched, or why it did not. Never empty. */
  reason: string;
}

export interface ShippingRuleDecisionModel {
  consignmentUuid: string;
  consignmentNumber: string;
  chargeableWeightKg: number;
  declaredValue?: number;
  isHazardous: boolean;
  hasCod: boolean;
  originCountryIso?: string;
  originPostcode?: string;
  destinationCountryIso?: string;
  destinationPostcode?: string;
  matchedRule?: ShippingRuleVerdictModel;
  /** Every rule considered, including the ones that did not match and why. */
  considered: ShippingRuleVerdictModel[];
  selection?: string;
  recommended?: RateShopOptionModel;
  options: RateShopOptionModel[];
  excluded: RateShopExclusionModel[];
  warnings: string[];
}

// ── Carrier services, rate cards and shipping rules (T-43, T-46, T-49) ────────

export interface CarrierServiceModel {
  uuid: string;
  carrierUuid: string;
  carrierName: string;
  serviceCode: string;
  serviceName: string;
  description?: string;
  dimDivisor?: number;
  minimumChargeableKg?: number;
  weightRoundingKg?: number;
  maxWeightKgPerPackage?: number;
  maxLengthCm?: number;
  maxLengthPlusGirthCm?: number;
  supportsCod: boolean;
  supportsHazardous: boolean;
  transitDays?: number;
  isDefault: boolean;
  isActive: boolean;
  /** Stated rather than inferred from a null divisor — the two mean different things. */
  chargesVolumetricWeight: boolean;
  createdDate: string;
}

export interface RateCardBreakRequest {
  fromWeightKg: number;
  /** PER_KG or FLAT. */
  basis: string;
  amount: number;
}

export interface RateCardLaneRequest {
  name?: string;
  originCountryIso?: string;
  originPostcodePrefix?: string;
  destinationCountryIso?: string;
  destinationPostcodePrefix?: string;
  breaks: RateCardBreakRequest[];
}

export interface CreateRateCardRequest {
  carrierUuid: string;
  serviceCode?: string;
  name: string;
  currency: string;
  effectiveFrom: string;
  effectiveTo?: string;
  minimumCharge?: number;
  fuelSurchargePercent?: number;
  codFeePercent?: number;
  codFeeMinimum?: number;
  lanes: RateCardLaneRequest[];
}

export interface PatchRateCardRequest {
  name?: string;
  effectiveFrom?: string;
  effectiveTo?: string;
  minimumCharge?: number;
  fuelSurchargePercent?: number;
  codFeePercent?: number;
  codFeeMinimum?: number;
  isActive?: boolean;
  /** Sending lanes replaces the whole tariff — a card is edited as a whole, never break by break. */
  lanes?: RateCardLaneRequest[];
  clearTerms?: string[];
}

export interface RateCardBreakModel {
  uuid: string;
  fromWeightKg: number;
  basis: string;
  amount: number;
}

export interface RateCardLaneModel {
  uuid: string;
  name?: string;
  originCountryIso?: string;
  originPostcodePrefix?: string;
  destinationCountryIso?: string;
  destinationPostcodePrefix?: string;
  breaks: RateCardBreakModel[];
}

export interface RateCardModel {
  uuid: string;
  carrierUuid: string;
  carrierName: string;
  serviceCode?: string;
  name: string;
  currency: string;
  effectiveFrom: string;
  effectiveTo?: string;
  minimumCharge?: number;
  fuelSurchargePercent?: number;
  codFeePercent?: number;
  codFeeMinimum?: number;
  isActive: boolean;
  /** True on the date asked about — stated, so a reader is not left comparing dates. */
  isInEffect: boolean;
  createdDate: string;
  lanes: RateCardLaneModel[];
}

export interface RateCardQuoteRequest {
  carrierUuid: string;
  serviceCode?: string;
  chargeableWeightKg: number;
  originCountryIso?: string;
  originPostcode?: string;
  destinationCountryIso?: string;
  destinationPostcode?: string;
  codAmount?: number;
  transitDays?: number;
  shipDate?: string;
}

export interface RateCardQuoteModel {
  /** Quoted, NoCard, NoLane, NoBreak or NoCarrier — "no price" and "priced at nothing" differ. */
  status: string;
  option?: RateShopOptionModel;
  cardName?: string;
  cardUuid?: string;
  laneName?: string;
  explanation?: string;
  quoted: boolean;
}

export interface CreateShippingRuleRequest {
  name: string;
  description?: string;
  priority: number;
  minChargeableWeightKg?: number;
  maxChargeableWeightKg?: number;
  originCountryIso?: string;
  originPostcodePrefix?: string;
  destinationCountryIso?: string;
  destinationPostcodePrefix?: string;
  minDeclaredValue?: number;
  maxDeclaredValue?: number;
  /** True for hazardous only, false for non-hazardous only, null for either. */
  appliesToHazardous?: boolean | null;
  appliesToCod?: boolean | null;
  carrierUuid?: string;
  serviceCode?: string;
  /** CHEAPEST (the default) or FASTEST. */
  strategy?: string;
}

export interface PatchShippingRuleRequest extends Partial<CreateShippingRuleRequest> {
  isActive?: boolean;
  clearConditions?: string[];
}

export interface ShippingRuleModel {
  uuid: string;
  name: string;
  description?: string;
  priority: number;
  isActive: boolean;
  minChargeableWeightKg?: number;
  maxChargeableWeightKg?: number;
  originCountryIso?: string;
  originPostcodePrefix?: string;
  destinationCountryIso?: string;
  destinationPostcodePrefix?: string;
  minDeclaredValue?: number;
  maxDeclaredValue?: number;
  appliesToHazardous?: boolean | null;
  appliesToCod?: boolean | null;
  carrierUuid?: string;
  carrierName?: string;
  serviceCode?: string;
  strategy: string;
  /** What the rule says, in a sentence — so a list of rules reads without decoding. */
  summary: string;
  createdDate: string;
}

// ── Freight settlement (T-52 … T-57) ──────────────────────────────────────────

export interface CarrierInvoiceLineRequest {
  description: string;
  /** The carrier's airway bill. What matching hangs off. */
  awbNumber?: string;
  /** Our consignment number, where the carrier echoed it back. */
  consignmentReference?: string;
  chargeCode?: string;
  serviceCode?: string;
  /** The weight the carrier says it billed on. */
  chargeableWeightKg?: number;
  shipDate?: string;
  /** Negative for a credit line. Zero is refused. */
  amount: number;
}

export interface CreateCarrierInvoiceRequest {
  carrierUuid: string;
  invoiceNumber: string;
  invoiceDate: string;
  dueDate?: string;
  currency: string;
  /** Must equal the sum of the lines. */
  totalAmount: number;
  taxAmount?: number;
  notes?: string;
  lines: CarrierInvoiceLineRequest[];
}

export interface CarrierInvoiceLineModel {
  uuid: string;
  lineNo: number;
  description: string;
  awbNumber?: string;
  consignmentReference?: string;
  chargeCode?: string;
  serviceCode?: string;
  chargeableWeightKg?: number;
  shipDate?: string;
  amount: number;
}

export interface CarrierInvoiceModel {
  uuid: string;
  carrierUuid: string;
  carrierName: string;
  invoiceNumber: string;
  invoiceDate: string;
  dueDate?: string;
  currency: string;
  totalAmount: number;
  taxAmount?: number;
  /** The sum of the lines. Equal to the total on any saved bill, by construction. */
  lineTotal: number;
  /** RECEIVED, MATCHED, DISPUTED or CANCELLED. */
  status: string;
  source?: string;
  notes?: string;
  cancelReason?: string;
  lineCount: number;
  createdDate: string;
  lines: CarrierInvoiceLineModel[];
}

export interface CarrierInvoiceFilter {
  carrierUuid?: string;
  status?: string;
  search?: string;
  from?: string;
  to?: string;
  page?: number;
  pageSize?: number;
}

export interface CarrierInvoiceImportResultModel {
  invoiceNumber: string;
  accepted: boolean;
  uuid?: string;
  reason?: string;
}

export interface CarrierInvoiceImportModel {
  accepted: number;
  refused: number;
  results: CarrierInvoiceImportResultModel[];
}

export interface InvoiceLineMatchModel {
  lineUuid: string;
  lineNo: number;
  description: string;
  invoiceUuid: string;
  invoiceNumber: string;
  carrierName: string;
  awbNumber?: string;
  consignmentReference?: string;
  chargeCode?: string;
  amount: number;
  currency: string;
  /** UNMATCHED, MATCHED, AMBIGUOUS or EXCLUDED. */
  matchStatus: string;
  /** AWB, REFERENCE or MANUAL — how strong the claim is. */
  matchMethod?: string;
  matchedConsignmentUuid?: string;
  matchedConsignmentNumber?: string;
  matchedAt?: string;
  matchNote?: string;
  /** Set when this movement is already charged on another bill. */
  duplicateWarning?: string;
}

export interface InvoiceMatchResultModel {
  invoiceUuid: string;
  invoiceNumber: string;
  lineCount: number;
  matched: number;
  ambiguous: number;
  unmatched: number;
  excluded: number;
  /** True when nothing is left for a person to do. */
  isComplete: boolean;
  lines: InvoiceLineMatchModel[];
  warnings: string[];
}

export interface MatchCandidateModel {
  consignmentUuid: string;
  consignmentNumber: string;
  masterAwb?: string;
  carrierName?: string;
  status: string;
  freightCost?: number;
  freightCurrency?: string;
  /** Why this one is being offered. */
  reason: string;
}

export interface ConsignmentMatchModel {
  consignmentUuid: string;
  consignmentNumber: string;
  masterAwb?: string;
  quotedAmount?: number;
  bookedAmount?: number;
  invoicedAmount: number;
  expectedAmount?: number;
  /** BOOKED or QUOTED — which figure the bill was measured against. */
  expectedBasis?: string;
  currency: string;
  varianceAmount?: number;
  variancePercent?: number;
  /** WITHIN_TOLERANCE, OVERCHARGED, UNDERCHARGED or NOT_ACCRUED. */
  outcome: string;
  /** WEIGHT, SURCHARGE, SERVICE or UNEXPLAINED. */
  varianceReason?: string;
  varianceNote?: string;
  lines: InvoiceLineMatchModel[];
}

export interface ThreeWayMatchModel {
  invoiceUuid: string;
  invoiceNumber: string;
  carrierName: string;
  currency: string;
  status: string;
  invoiceTotal: number;
  expectedTotal: number;
  varianceTotal: number;
  consignmentCount: number;
  withinTolerance: number;
  overcharged: number;
  undercharged: number;
  notAccrued: number;
  /** The amount on lines nobody could tie to a movement. Not in the comparison at all. */
  unmatchedAmount: number;
  unmatchedLines: number;
  /** The tolerance the comparison was made with, so a verdict can be reproduced. */
  tolerancePercent: number;
  toleranceAmount: number;
  isClean: boolean;
  consignments: ConsignmentMatchModel[];
  warnings: string[];
}

export interface FreightAccrualModel {
  uuid: string;
  consignmentUuid: string;
  consignmentNumber: string;
  carrierUuid?: string;
  carrierName?: string;
  accruedAmount: number;
  currency: string;
  quoteSource?: string;
  bookedAmount?: number;
  bookedCurrency?: string;
  bookedVariance?: number;
  /** ACCRUED, MATCHED, CLOSED or REVERSED. */
  status: string;
  accruedAt: string;
  releasedAt?: string;
  releaseReason?: string;
  consignmentStatus: string;
}

export interface UnaccruableConsignmentModel {
  consignmentUuid: string;
  consignmentNumber: string;
  status: string;
  carrierName?: string;
  masterAwb?: string;
  dispatchedAt?: string;
  reason: string;
}

export interface FreightAccrualTotalModel {
  carrierUuid?: string;
  carrierName: string;
  currency: string;
  count: number;
  total: number;
}

export interface FreightAccrualSummaryModel {
  asOf: string;
  openCount: number;
  openTotal: number;
  /** Empty unless every open accrual shares a currency. */
  currency?: string;
  byCarrier: FreightAccrualTotalModel[];
  /** Dispatched, never priced. Missing from the totals above. */
  couldNotAccrue: UnaccruableConsignmentModel[];
  warnings: string[];
}

export interface CodRemittanceModel {
  uuid: string;
  amount: number;
  receivedAt: string;
  reference?: string;
  note?: string;
}

export interface CodCollectionModel {
  uuid: string;
  consignmentUuid: string;
  consignmentNumber: string;
  consignmentStatus: string;
  masterAwb?: string;
  carrierUuid?: string;
  carrierName?: string;
  expectedAmount: number;
  currency: string;
  collectedAmount?: number;
  collectedAt?: string;
  collectionReference?: string;
  remittedAmount: number;
  /** Expected minus remitted. What the carrier is still holding. */
  outstandingAmount: number;
  /** EXPECTED, COLLECTED, SETTLED or WRITTEN_OFF. */
  status: string;
  settledAt?: string;
  writeOffReason?: string;
  remittances: CodRemittanceModel[];
  warnings: string[];
}

export interface UncollectedCodModel {
  consignmentUuid: string;
  consignmentNumber: string;
  status: string;
  carrierName?: string;
  masterAwb?: string;
  expectedAmount: number;
  currency: string;
  reason: string;
}

export interface CodCarrierTotalModel {
  carrierUuid?: string;
  carrierName: string;
  currency: string;
  count: number;
  outstanding: number;
}

export interface CodSummaryModel {
  asOf: string;
  openCount: number;
  outstandingTotal: number;
  currency?: string;
  byCarrier: CodCarrierTotalModel[];
  /** Delivered with cash to collect, and nothing says it was collected. */
  neverCollected: UncollectedCodModel[];
  warnings: string[];
}

export interface CodFilter {
  carrierUuid?: string;
  status?: string;
  page?: number;
  pageSize?: number;
}

// ── Delivery exceptions (T-60) ────────────────────────────────────────────────

export interface DeliveryExceptionModel {
  uuid: string;
  consignmentUuid: string;
  consignmentNumber: string;
  consignmentStatus: string;
  masterAwb?: string;
  carrierUuid?: string;
  carrierName?: string;
  /** ADDRESS_INVALID, CONSIGNEE_UNREACHABLE, REFUSED, DAMAGED, CUSTOMS_HOLD, LOST, DELAYED, COD_MISMATCH. */
  exceptionType: string;
  /** LOW, NORMAL, CRITICAL. */
  severity: string;
  /** OPEN, WAITING, RESOLVED, WITHDRAWN. */
  status: string;
  /** CARRIER, MANUAL or SYSTEM — how it came to be known. */
  source: string;
  description: string;
  occurredAt: string;
  /** How long it has been open. Stops at the moment it closed. */
  openForHours: number;
  assignedToUserId?: number;
  assignedAt?: string;
  resolvedAt?: string;
  resolution?: string;
  /** The carrier's own words, where a carrier event raised it. */
  carrierStatus?: string;
  warnings: string[];
}

export interface ExceptionTypeCountModel {
  exceptionType: string;
  open: number;
  critical: number;
  unassigned: number;
  oldestHours: number;
}

export interface ExceptionSummaryModel {
  open: number;
  waiting: number;
  critical: number;
  unassigned: number;
  byType: ExceptionTypeCountModel[];
  warnings: string[];
}

export interface ExceptionFilter {
  carrierUuid?: string;
  consignmentUuid?: string;
  exceptionType?: string;
  severity?: string;
  status?: string;
  unassigned?: boolean;
  page?: number;
  pageSize?: number;
}

export interface RaiseExceptionRequest {
  exceptionType: string;
  severity?: string;
  description: string;
  occurredAt?: string;
  assignToUserId?: number;
}

export interface PatchExceptionRequest {
  severity?: string;
  description?: string;
  assignToUserId?: number;
  clearAssignee?: boolean;
  status?: string;
}

// ── Proof of delivery (T-61) ──────────────────────────────────────────────────

export interface DeliveryProofFileModel {
  uuid: string;
  /** SIGNATURE, PHOTO or DOCUMENT. */
  kind: string;
  contentType: string;
  fileName: string;
  sizeBytes: number;
  sha256: string;
  createdDate: string;
}

export interface DeliveryProofModel {
  uuid: string;
  consignmentUuid: string;
  consignmentNumber: string;
  consignmentStatus: string;
  masterAwb?: string;
  carrierName?: string;
  consignmentStopUuid?: string;
  stopSequence?: number;
  receivedBy?: string;
  relationship?: string;
  deliveredAt: string;
  location?: string;
  notes?: string;
  /** CARRIER or MANUAL. */
  source: string;
  carrierStatus?: string;
  files: DeliveryProofFileModel[];
  /** A name *and* an artefact. The one question that matters when a delivery is denied. */
  isDefensible: boolean;
  createdDate: string;
  warnings: string[];
}

export interface ProofGapModel {
  consignmentUuid: string;
  consignmentNumber: string;
  carrierName?: string;
  masterAwb?: string;
  deliveredAt?: string;
  gap: string;
}

export interface ProofCoverageModel {
  delivered: number;
  withProof: number;
  defensible: number;
  withoutProof: number;
  weak: number;
  gaps: ProofGapModel[];
  warnings: string[];
}

export interface RecordProofRequest {
  receivedBy: string;
  relationship?: string;
  deliveredAt?: string;
  location?: string;
  notes?: string;
  consignmentStopUuid?: string;
}

// ── Carrier scorecard (T-63) ──────────────────────────────────────────────────

export interface VarianceByCurrencyModel {
  currency: string;
  /** Billed minus expected, summed. Positive means overcharged. */
  variance: number;
  consignments: number;
}

export interface CarrierBillingModel {
  invoiced: number;
  overcharged: number;
  /** Billed less than expected. Kept separate — it is not good news. */
  undercharged: number;
  /** Within tolerance, as a percentage of those invoiced. Null when none were. */
  accuracyPercent?: number;
  /** Never summed across currencies. */
  variance: VarianceByCurrencyModel[];
  notYetInvoiced: number;
}

export interface CarrierScoreModel {
  carrierUuid?: string;
  carrierName: string;

  /** Collected in the window. The denominator for everything. */
  consignments: number;
  delivered: number;
  returnedToOrigin: number;
  lost: number;
  stillMoving: number;

  /** Delivered consignments that had an ETA to be judged against. */
  judgeable: number;
  onTime: number;
  late: number;
  /** Null when nothing could be judged. A percentage of nothing is not zero. */
  onTimePercent?: number;
  averageDaysLate?: number;
  /** Delivered with no ETA — neither on time nor late, and counted as neither. */
  notJudgeable: number;

  /** Withdrawn ones excluded. */
  exceptions: number;
  criticalExceptions: number;
  stillOpen: number;
  exceptionsPer100?: number;
  averageHoursToResolve?: number;
  /** A live figure, not a window one. */
  stuckNow: number;

  /** Delivered with a proof carrying both a name and an artefact. */
  defensible: number;
  proofCoveragePercent?: number;

  /** Null when the caller may not see what the company pays. */
  billing?: CarrierBillingModel;

  warnings: string[];
}

export interface CarrierScorecardModel {
  from: string;
  to: string;
  /** VOLUME, ON_TIME, EXCEPTIONS or BILLING. There is no single overall score. */
  sortedBy: string;
  carriers: CarrierScoreModel[];
  warnings: string[];
}

export interface ScorecardFilter {
  from?: string;
  to?: string;
  carrierUuid?: string;
  sortBy?: string;
}

// ── Service ───────────────────────────────────────────────────────────────────

@Injectable({ providedIn: 'root' })
export class LogisticsService {
  constructor(private http: HttpClient) {}

  // ── Carriers ──────────────────────────────────────────────────────────────

  createCarrier(req: CreateCarrierRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${BASE}/carriers`, req);
  }

  getCarriers(filter: CarrierFilter = {}): Observable<ApiResponse<PaginatedResponse<CarrierListItemModel>>> {
    let params = new HttpParams();
    if (filter.search)   params = params.set('search',   filter.search);
    if (filter.status)   params = params.set('status',   filter.status);
    params = params.set('page',     String(filter.page     ?? 1));
    params = params.set('pageSize', String(filter.pageSize ?? 20));
    return this.http.get<ApiResponse<PaginatedResponse<CarrierListItemModel>>>(`${BASE}/carriers`, { params });
  }

  getActiveCarriers(): Observable<ApiResponse<CarrierListItemModel[]>> {
    return this.http.get<ApiResponse<CarrierListItemModel[]>>(`${BASE}/carriers/active`);
  }

  getCarrierById(uuid: string): Observable<ApiResponse<CarrierDetailModel>> {
    return this.http.get<ApiResponse<CarrierDetailModel>>(`${BASE}/carriers/${uuid}`);
  }

  patchCarrier(uuid: string, req: PatchCarrierRequest): Observable<ApiResponse> {
    return this.http.patch<ApiResponse>(`${BASE}/carriers/${uuid}`, req);
  }

  deleteCarrier(uuid: string): Observable<ApiResponse> {
    return this.http.delete<ApiResponse>(`${BASE}/carriers/${uuid}`);
  }

  // ── Shipments ─────────────────────────────────────────────────────────────

  createShipment(req: CreateShipmentRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${BASE}/shipments`, req);
  }

  getShipments(filter: ShipmentFilter = {}): Observable<ApiResponse<PaginatedResponse<ShipmentListItemModel>>> {
    let params = new HttpParams();
    if (filter.status)      params = params.set('status',      filter.status);
    if (filter.search)      params = params.set('search',      filter.search);
    if (filter.carrierUuid) params = params.set('carrierUuid', filter.carrierUuid);
    params = params.set('page',     String(filter.page     ?? 1));
    params = params.set('pageSize', String(filter.pageSize ?? 20));
    return this.http.get<ApiResponse<PaginatedResponse<ShipmentListItemModel>>>(`${BASE}/shipments`, { params });
  }

  getShipmentById(uuid: string): Observable<ApiResponse<ShipmentDetailModel>> {
    return this.http.get<ApiResponse<ShipmentDetailModel>>(`${BASE}/shipments/${uuid}`);
  }

  patchShipment(uuid: string, req: PatchShipmentRequest): Observable<ApiResponse> {
    return this.http.patch<ApiResponse>(`${BASE}/shipments/${uuid}`, req);
  }

  // uploadPod was retired with F47. It wrote a file into the API's wwwroot and stored the path,
  // which is a proof only until the next redeploy. Use recordProof and attachProofFile instead:
  // those store the bytes against the consignment and record who took the goods.

  resolveFileUrl(url: string): string {
    if (!url) return '';
    return url.startsWith('/') ? `${environment.apiOrigin}${url}` : url;
  }

  // ── Deliveries ────────────────────────────────────────────────────────────

  getDeliveries(filter: DeliveryFilter = {}): Observable<ApiResponse<PaginatedResponse<DeliveryListItemModel>>> {
    let params = new HttpParams();
    // Only set what was actually asked for: an empty value sent as a parameter is a filter for
    // the empty string, not the absence of a filter.
    if (filter.status)     params = params.set('status',     filter.status);
    if (filter.direction)  params = params.set('direction',  filter.direction);
    if (filter.sourceType) params = params.set('sourceType', filter.sourceType);
    if (filter.search)     params = params.set('search',     filter.search);
    if (filter.fromDate)   params = params.set('fromDate',   filter.fromDate);
    if (filter.toDate)     params = params.set('toDate',     filter.toDate);
    params = params.set('page',     String(filter.page     ?? 1));
    params = params.set('pageSize', String(filter.pageSize ?? 20));
    return this.http.get<ApiResponse<PaginatedResponse<DeliveryListItemModel>>>(
      `${BASE}/deliveries`, { params });
  }

  getDeliveryById(uuid: string): Observable<ApiResponse<DeliveryDetailModel>> {
    return this.http.get<ApiResponse<DeliveryDetailModel>>(`${BASE}/deliveries/${uuid}`);
  }

  createDelivery(req: CreateDeliveryRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${BASE}/deliveries`, req);
  }

  /** Raises a delivery from a PO, SRO or MIV. Lines come from the source document. */
  createDeliveryFromSource(req: CreateDeliveryFromSourceRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${BASE}/deliveries/from-source`, req);
  }

  patchDelivery(uuid: string, req: PatchDeliveryRequest): Observable<ApiResponse> {
    return this.http.patch<ApiResponse>(`${BASE}/deliveries/${uuid}`, req);
  }

  deleteDelivery(uuid: string): Observable<ApiResponse> {
    return this.http.delete<ApiResponse>(`${BASE}/deliveries/${uuid}`);
  }

  holdDelivery(uuid: string, req: DeliveryReasonRequest): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${BASE}/deliveries/${uuid}/hold`, req);
  }

  resumeDelivery(uuid: string): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${BASE}/deliveries/${uuid}/resume`, {});
  }

  cancelDelivery(uuid: string, req: DeliveryReasonRequest): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${BASE}/deliveries/${uuid}/cancel`, req);
  }

  shortCloseDelivery(uuid: string, req: DeliveryReasonRequest): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${BASE}/deliveries/${uuid}/short-close`, req);
  }

  // ── Carrier accounts ──────────────────────────────────────────────────────

  createCarrierAccount(req: CreateCarrierAccountRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${BASE}/carrier-accounts`, req);
  }

  /** Every account for a carrier, default first. */
  getCarrierAccounts(carrierUuid: string): Observable<ApiResponse<CarrierAccountModel[]>> {
    return this.http.get<ApiResponse<CarrierAccountModel[]>>(
      `${BASE}/carrier-accounts/by-carrier/${carrierUuid}`);
  }

  /** The courier adapters this deployment has, and the credentials each reads. */
  getCourierProviders(): Observable<ApiResponse<CourierProviderModel[]>> {
    return this.http.get<ApiResponse<CourierProviderModel[]>>(`${BASE}/carrier-accounts/providers`);
  }

  getCarrierIntegration(carrierUuid: string): Observable<ApiResponse<CarrierIntegrationModel>> {
    return this.http.get<ApiResponse<CarrierIntegrationModel>>(
      `${BASE}/carrier-accounts/by-carrier/${carrierUuid}/integration`);
  }

  setCarrierIntegration(
    carrierUuid: string, req: SetCarrierIntegrationRequest
  ): Observable<ApiResponse<CarrierIntegrationModel>> {
    return this.http.put<ApiResponse<CarrierIntegrationModel>>(
      `${BASE}/carrier-accounts/by-carrier/${carrierUuid}/integration`, req);
  }

  getCarrierAccountById(uuid: string): Observable<ApiResponse<CarrierAccountModel>> {
    return this.http.get<ApiResponse<CarrierAccountModel>>(`${BASE}/carrier-accounts/${uuid}`);
  }

  patchCarrierAccount(uuid: string, req: PatchCarrierAccountRequest): Observable<ApiResponse> {
    return this.http.patch<ApiResponse>(`${BASE}/carrier-accounts/${uuid}`, req);
  }

  deleteCarrierAccount(uuid: string): Observable<ApiResponse> {
    return this.http.delete<ApiResponse>(`${BASE}/carrier-accounts/${uuid}`);
  }

  // ── Carrier credentials ───────────────────────────────────────────────────
  //
  // There is no read-a-value method here, and there is no endpoint behind one. Values go in and
  // are never returned.

  /** Which credentials are configured — keys, notes and expiry. Never the values. */
  getCarrierCredentials(accountUuid: string): Observable<ApiResponse<CarrierCredentialModel[]>> {
    return this.http.get<ApiResponse<CarrierCredentialModel[]>>(
      `${BASE}/carrier-accounts/${accountUuid}/credentials`);
  }

  setCarrierCredential(
    accountUuid: string, req: SetCarrierCredentialRequest): Observable<ApiResponse<string>> {
    return this.http.put<ApiResponse<string>>(
      `${BASE}/carrier-accounts/${accountUuid}/credentials`, req);
  }

  removeCarrierCredential(accountUuid: string, key: string): Observable<ApiResponse> {
    return this.http.delete<ApiResponse>(
      `${BASE}/carrier-accounts/${accountUuid}/credentials/${encodeURIComponent(key)}`);
  }

  // ── Warehouse execution ───────────────────────────────────────────────────

  /**
   * Commits the delivery and hard-reserves its stock. Pass `onShortage: 'SPLIT'` to release what
   * stock can cover and move the balance to a new draft against the same source.
   */
  releaseDelivery(uuid: string, req: ReleaseDeliveryRequest = {}): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${BASE}/deliveries/${uuid}/release`, req);
  }

  /** What the delivery could be released against right now, line by line. */
  getDeliveryAvailability(uuid: string): Observable<ApiResponse<DeliveryAvailabilityModel>> {
    return this.http.get<ApiResponse<DeliveryAvailabilityModel>>(
      `${BASE}/deliveries/${uuid}/availability`);
  }

  generatePickList(uuid: string, req: GeneratePickListRequest = {}): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${BASE}/deliveries/${uuid}/pick-list`, req);
  }

  /** The delivery's pick list — the live one, or the most recent if none is live. */
  getPickListForDelivery(uuid: string): Observable<ApiResponse<PickListModel>> {
    return this.http.get<ApiResponse<PickListModel>>(`${BASE}/deliveries/${uuid}/pick-list`);
  }

  getPickLists(filter: PickListFilter = {}): Observable<ApiResponse<PaginatedResponse<PickListListItemModel>>> {
    let params = new HttpParams();
    if (filter.status)        params = params.set('status',        filter.status);
    if (filter.warehouseUuid) params = params.set('warehouseUuid', filter.warehouseUuid);
    if (filter.assignedToUserId != null)
      params = params.set('assignedToUserId', String(filter.assignedToUserId));
    if (filter.search)        params = params.set('search',        filter.search);
    params = params.set('page',     String(filter.page     ?? 1));
    params = params.set('pageSize', String(filter.pageSize ?? 20));
    return this.http.get<ApiResponse<PaginatedResponse<PickListListItemModel>>>(
      `${BASE}/pick-lists`, { params });
  }

  getPickListById(uuid: string): Observable<ApiResponse<PickListModel>> {
    return this.http.get<ApiResponse<PickListModel>>(`${BASE}/pick-lists/${uuid}`);
  }

  /** Records what the picker found. The list closes itself once every line is answered. */
  confirmPick(uuid: string, req: ConfirmPickRequest): Observable<ApiResponse<ConfirmPickResultModel>> {
    return this.http.post<ApiResponse<ConfirmPickResultModel>>(
      `${BASE}/pick-lists/${uuid}/confirm`, req);
  }

  assignPickList(uuid: string, assignedToUserId: number): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${BASE}/pick-lists/${uuid}/assign`, { assignedToUserId });
  }

  cancelPickList(uuid: string, req: DeliveryReasonRequest): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${BASE}/pick-lists/${uuid}/cancel`, req);
  }

  /** Packs picked goods into a carton and returns its handling-unit id. */
  packDelivery(uuid: string, req: PackRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${BASE}/deliveries/${uuid}/packages`, req);
  }

  /** Every carton on the delivery, and what is still waiting for one. */
  getDeliveryPackages(uuid: string): Observable<ApiResponse<DeliveryPackingModel>> {
    return this.http.get<ApiResponse<DeliveryPackingModel>>(`${BASE}/deliveries/${uuid}/packages`);
  }

  getPackageById(uuid: string): Observable<ApiResponse<PackageModel>> {
    return this.http.get<ApiResponse<PackageModel>>(`${BASE}/packages/${uuid}`);
  }

  patchPackage(uuid: string, req: PatchPackageRequest): Observable<ApiResponse> {
    return this.http.patch<ApiResponse>(`${BASE}/packages/${uuid}`, req);
  }

  /** The row and its barcode are kept — the label may already be on a real carton. */
  voidPackage(uuid: string, req: DeliveryReasonRequest): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${BASE}/packages/${uuid}/void`, req);
  }

  stageDelivery(uuid: string): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${BASE}/deliveries/${uuid}/stage`, {});
  }

  /** The point of no return: the stock leaves the books and the delivery cannot be cancelled. */
  goodsIssueDelivery(uuid: string): Observable<ApiResponse<GoodsIssueResultModel>> {
    return this.http.post<ApiResponse<GoodsIssueResultModel>>(
      `${BASE}/deliveries/${uuid}/goods-issue`, {});
  }

  /**
   * The customer collects a self-pickup delivery: records who took the goods, issues the stock if
   * the store had not already, and marks the delivery DELIVERED.
   */
  recordPickup(uuid: string, req: RecordPickupRequest): Observable<ApiResponse<PickupResultModel>> {
    return this.http.post<ApiResponse<PickupResultModel>>(`${BASE}/deliveries/${uuid}/pickup`, req);
  }

  // ── Documents ─────────────────────────────────────────────────────────────
  //
  // These return a PDF, not an ApiResponse, so they ask for a blob. Declaring the response type
  // is what stops Angular trying to parse the bytes as JSON and failing on the first byte.

  downloadPackingList(uuid: string): Observable<Blob> {
    return this.http.get(`${BASE}/deliveries/${uuid}/packing-list`, { responseType: 'blob' });
  }

  downloadGatePass(uuid: string): Observable<Blob> {
    return this.http.get(`${BASE}/deliveries/${uuid}/gate-pass`, { responseType: 'blob' });
  }

  // ── Consignments ──────────────────────────────────────────────────────────

  createConsignment(req: CreateConsignmentRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${BASE}/consignments`, req);
  }

  getConsignmentById(uuid: string): Observable<ApiResponse<ConsignmentDetailModel>> {
    return this.http.get<ApiResponse<ConsignmentDetailModel>>(`${BASE}/consignments/${uuid}`);
  }

  attachDeliveryToConsignment(uuid: string, deliveryUuid: string): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(
      `${BASE}/consignments/${uuid}/deliveries/${deliveryUuid}`, {});
  }

  /** Records an airway bill obtained from the carrier by phone, portal or paper note. */
  bookConsignmentManually(uuid: string, req: ManualBookingRequest): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${BASE}/consignments/${uuid}/book-manual`, req);
  }

  // ── Booking through a carrier's API ───────────────────────────────────────

  /**
   * Requests a booking. Answers **202** — the carrier call happens in the background, so the
   * caller polls {@link getConsignmentBooking} for the outcome. Asking again while one is under
   * way sends nothing twice.
   */
  bookConsignment(
    uuid: string, req: BookConsignmentRequest = {}): Observable<ApiResponse<ConsignmentBookingStatusModel>> {
    return this.http.post<ApiResponse<ConsignmentBookingStatusModel>>(
      `${BASE}/consignments/${uuid}/book`, req);
  }

  /** Where the booking stands, and whether a person needs to act. */
  getConsignmentBooking(uuid: string): Observable<ApiResponse<ConsignmentBookingStatusModel>> {
    return this.http.get<ApiResponse<ConsignmentBookingStatusModel>>(
      `${BASE}/consignments/${uuid}/booking`);
  }

  /** Settles a booking whose outcome is unknown, once somebody has checked with the carrier. */
  resolveConsignmentBooking(
    uuid: string, req: ResolveBookingRequest): Observable<ApiResponse<ConsignmentBookingStatusModel>> {
    return this.http.post<ApiResponse<ConsignmentBookingStatusModel>>(
      `${BASE}/consignments/${uuid}/booking/resolve`, req);
  }

  /** The shipping label as a blob. Served inline so a browser can open it straight to print. */
  downloadConsignmentLabel(uuid: string): Observable<Blob> {
    return this.http.get(`${BASE}/consignments/${uuid}/label`, { responseType: 'blob' });
  }

  // ── Tracking ──────────────────────────────────────────────────────────────

  /** What the carrier has reported, latest first — from webhooks and the poll. */
  getConsignmentTracking(uuid: string): Observable<ApiResponse<ConsignmentTrackingEventModel[]>> {
    return this.http.get<ApiResponse<ConsignmentTrackingEventModel[]>>(
      `${BASE}/consignments/${uuid}/tracking`);
  }

  /** Asks the carrier now. Pressed again within a minute it does not ask twice. */
  refreshConsignmentTracking(uuid: string): Observable<ApiResponse<TrackingRefreshModel>> {
    return this.http.post<ApiResponse<TrackingRefreshModel>>(
      `${BASE}/consignments/${uuid}/tracking/refresh`, {});
  }

  /** Consignments that have gone quiet for longer than their status allows. */
  getStuckConsignments(): Observable<ApiResponse<StuckConsignmentModel[]>> {
    return this.http.get<ApiResponse<StuckConsignmentModel[]>>(`${BASE}/consignments/stuck`);
  }

  // ── Rating ────────────────────────────────────────────────────────────────

  /** What each package is charged on. Reads only — the figures are worked out fresh. */
  getChargeableWeight(uuid: string): Observable<ApiResponse<ConsignmentWeightModel>> {
    return this.http.get<ApiResponse<ConsignmentWeightModel>>(
      `${BASE}/consignments/${uuid}/chargeable-weight`);
  }

  /** Works the same figures out and writes them onto the packages. */
  recalculateChargeableWeight(uuid: string): Observable<ApiResponse<ConsignmentWeightModel>> {
    return this.http.post<ApiResponse<ConsignmentWeightModel>>(
      `${BASE}/consignments/${uuid}/chargeable-weight`, {});
  }

  /** What this consignment costs to move, and where the figure came from. */
  getConsignmentRate(uuid: string): Observable<ApiResponse<ConsignmentRateModel>> {
    return this.http.get<ApiResponse<ConsignmentRateModel>>(`${BASE}/consignments/${uuid}/rate`);
  }

  /** Prices it and moves it to RATED. The carrier is asked first; the card answers if it will not. */
  rateConsignment(
    uuid: string, req: RateConsignmentRequest = {}): Observable<ApiResponse<ConsignmentRateModel>> {
    return this.http.post<ApiResponse<ConsignmentRateModel>>(
      `${BASE}/consignments/${uuid}/rate`, req);
  }

  /** Records a price obtained outside the system — emailed, or quoted by telephone. */
  setManualRate(uuid: string, req: ManualRateRequest): Observable<ApiResponse<ConsignmentRateModel>> {
    return this.http.post<ApiResponse<ConsignmentRateModel>>(
      `${BASE}/consignments/${uuid}/rate/manual`, req);
  }

  /** Discards the quote and drops back to DRAFT. */
  clearConsignmentRate(uuid: string): Observable<ApiResponse> {
    return this.http.delete<ApiResponse>(`${BASE}/consignments/${uuid}/rate`);
  }

  /** What every carrier would charge, ranked, with the reasoning on each option. */
  shopRates(uuid: string, req: RateShopRequest = {}): Observable<ApiResponse<RateShopResultModel>> {
    return this.http.post<ApiResponse<RateShopResultModel>>(
      `${BASE}/consignments/${uuid}/rate/shop`, req);
  }

  /** Takes one of the shopped options. The price is re-quoted server-side, never trusted from here. */
  acceptRate(uuid: string, req: AcceptRateRequest): Observable<ApiResponse<ConsignmentRateModel>> {
    return this.http.post<ApiResponse<ConsignmentRateModel>>(
      `${BASE}/consignments/${uuid}/rate/accept`, req);
  }

  /** Which shipping rule fires for this consignment, why the earlier ones did not, and what it costs. */
  evaluateShippingRules(
    uuid: string, shipDate?: string): Observable<ApiResponse<ShippingRuleDecisionModel>> {
    let params = new HttpParams();
    if (shipDate) params = params.set('shipDate', shipDate);
    return this.http.get<ApiResponse<ShippingRuleDecisionModel>>(
      `${BASE}/shipping-rules/evaluate/${uuid}`, { params });
  }

  /** Evaluates, then accepts what the rule chose. */
  applyShippingRule(uuid: string, shipDate?: string): Observable<ApiResponse<ConsignmentRateModel>> {
    let params = new HttpParams();
    if (shipDate) params = params.set('shipDate', shipDate);
    return this.http.post<ApiResponse<ConsignmentRateModel>>(
      `${BASE}/shipping-rules/apply/${uuid}`, {}, { params });
  }

  // ── Carrier services ──────────────────────────────────────────────────────

  /** The named products a carrier sells, default first. */
  getCarrierServices(carrierUuid: string): Observable<ApiResponse<CarrierServiceModel[]>> {
    return this.http.get<ApiResponse<CarrierServiceModel[]>>(
      `${BASE}/carrier-services/by-carrier/${carrierUuid}`);
  }

  // ── Rate cards ────────────────────────────────────────────────────────────

  createRateCard(req: CreateRateCardRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${BASE}/rate-cards`, req);
  }

  /** Every card configured for a carrier, newest period first. */
  getRateCards(carrierUuid: string, on?: string): Observable<ApiResponse<RateCardModel[]>> {
    let params = new HttpParams();
    if (on) params = params.set('on', on);
    return this.http.get<ApiResponse<RateCardModel[]>>(
      `${BASE}/rate-cards/by-carrier/${carrierUuid}`, { params });
  }

  getRateCardById(uuid: string): Observable<ApiResponse<RateCardModel>> {
    return this.http.get<ApiResponse<RateCardModel>>(`${BASE}/rate-cards/${uuid}`);
  }

  patchRateCard(uuid: string, req: PatchRateCardRequest): Observable<ApiResponse> {
    return this.http.patch<ApiResponse>(`${BASE}/rate-cards/${uuid}`, req);
  }

  deleteRateCard(uuid: string): Observable<ApiResponse> {
    return this.http.delete<ApiResponse>(`${BASE}/rate-cards/${uuid}`);
  }

  /** A dry run against a weight and a lane, without a consignment — does this card do what I meant? */
  quoteRateCard(req: RateCardQuoteRequest): Observable<ApiResponse<RateCardQuoteModel>> {
    return this.http.post<ApiResponse<RateCardQuoteModel>>(`${BASE}/rate-cards/quote`, req);
  }

  // ── Shipping rules ────────────────────────────────────────────────────────

  createShippingRule(req: CreateShippingRuleRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${BASE}/shipping-rules`, req);
  }

  /** Every rule, in the order they are tried. */
  getShippingRules(): Observable<ApiResponse<ShippingRuleModel[]>> {
    return this.http.get<ApiResponse<ShippingRuleModel[]>>(`${BASE}/shipping-rules`);
  }

  getShippingRuleById(uuid: string): Observable<ApiResponse<ShippingRuleModel>> {
    return this.http.get<ApiResponse<ShippingRuleModel>>(`${BASE}/shipping-rules/${uuid}`);
  }

  patchShippingRule(uuid: string, req: PatchShippingRuleRequest): Observable<ApiResponse> {
    return this.http.patch<ApiResponse>(`${BASE}/shipping-rules/${uuid}`, req);
  }

  deleteShippingRule(uuid: string): Observable<ApiResponse> {
    return this.http.delete<ApiResponse>(`${BASE}/shipping-rules/${uuid}`);
  }

  // ── Carrier invoices ──────────────────────────────────────────────────────

  createCarrierInvoice(req: CreateCarrierInvoiceRequest): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(`${BASE}/carrier-invoices`, req);
  }

  /** Brings in many bills at once. Each is judged on its own and reported separately. */
  importCarrierInvoices(
    invoices: CreateCarrierInvoiceRequest[]): Observable<ApiResponse<CarrierInvoiceImportModel>> {
    return this.http.post<ApiResponse<CarrierInvoiceImportModel>>(
      `${BASE}/carrier-invoices/import`, { invoices });
  }

  getCarrierInvoices(
    filter: CarrierInvoiceFilter = {}): Observable<ApiResponse<PaginatedResponse<CarrierInvoiceModel>>> {
    let params = new HttpParams();
    if (filter.carrierUuid) params = params.set('carrierUuid', filter.carrierUuid);
    if (filter.status)      params = params.set('status',      filter.status);
    if (filter.search)      params = params.set('search',      filter.search);
    if (filter.from)        params = params.set('from',        filter.from);
    if (filter.to)          params = params.set('to',          filter.to);
    params = params.set('page',     String(filter.page     ?? 1));
    params = params.set('pageSize', String(filter.pageSize ?? 20));
    return this.http.get<ApiResponse<PaginatedResponse<CarrierInvoiceModel>>>(
      `${BASE}/carrier-invoices`, { params });
  }

  getCarrierInvoiceById(uuid: string): Observable<ApiResponse<CarrierInvoiceModel>> {
    return this.http.get<ApiResponse<CarrierInvoiceModel>>(`${BASE}/carrier-invoices/${uuid}`);
  }

  cancelCarrierInvoice(uuid: string, reason: string): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${BASE}/carrier-invoices/${uuid}/cancel`, { reason });
  }

  // ── Matching ──────────────────────────────────────────────────────────────

  /** Matches every line not already settled. Idempotent — never undoes a decision. */
  matchCarrierInvoice(invoiceUuid: string): Observable<ApiResponse<InvoiceMatchResultModel>> {
    return this.http.post<ApiResponse<InvoiceMatchResultModel>>(
      `${BASE}/invoice-matching/invoices/${invoiceUuid}`, {});
  }

  getInvoiceMatches(invoiceUuid: string): Observable<ApiResponse<InvoiceMatchResultModel>> {
    return this.http.get<ApiResponse<InvoiceMatchResultModel>>(
      `${BASE}/invoice-matching/invoices/${invoiceUuid}`);
  }

  /** Everything still needing a person, largest charge first. */
  getMatchQueue(filter: {
    carrierUuid?: string; invoiceUuid?: string; matchStatus?: string;
    page?: number; pageSize?: number;
  } = {}): Observable<ApiResponse<PaginatedResponse<InvoiceLineMatchModel>>> {
    let params = new HttpParams();
    if (filter.carrierUuid) params = params.set('carrierUuid', filter.carrierUuid);
    if (filter.invoiceUuid) params = params.set('invoiceUuid', filter.invoiceUuid);
    if (filter.matchStatus) params = params.set('matchStatus', filter.matchStatus);
    params = params.set('page',     String(filter.page     ?? 1));
    params = params.set('pageSize', String(filter.pageSize ?? 20));
    return this.http.get<ApiResponse<PaginatedResponse<InvoiceLineMatchModel>>>(
      `${BASE}/invoice-matching/queue`, { params });
  }

  getMatchCandidates(lineUuid: string): Observable<ApiResponse<MatchCandidateModel[]>> {
    return this.http.get<ApiResponse<MatchCandidateModel[]>>(
      `${BASE}/invoice-matching/lines/${lineUuid}/candidates`);
  }

  matchInvoiceLine(lineUuid: string, consignmentUuid: string, note: string): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(
      `${BASE}/invoice-matching/lines/${lineUuid}/match`, { consignmentUuid, note });
  }

  /** Marks a line as not a movement charge at all, so it leaves the queue. */
  excludeInvoiceLine(lineUuid: string, reason: string): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${BASE}/invoice-matching/lines/${lineUuid}/exclude`, { reason });
  }

  unmatchInvoiceLine(lineUuid: string, reason: string): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${BASE}/invoice-matching/lines/${lineUuid}/unmatch`, { reason });
  }

  /** Compares quoted, agreed at booking and billed. Records the result. */
  runThreeWayMatch(invoiceUuid: string): Observable<ApiResponse<ThreeWayMatchModel>> {
    return this.http.post<ApiResponse<ThreeWayMatchModel>>(
      `${BASE}/invoice-matching/invoices/${invoiceUuid}/three-way`, {});
  }

  /** The same comparison, recording nothing. */
  previewThreeWayMatch(invoiceUuid: string): Observable<ApiResponse<ThreeWayMatchModel>> {
    return this.http.get<ApiResponse<ThreeWayMatchModel>>(
      `${BASE}/invoice-matching/invoices/${invoiceUuid}/three-way`);
  }

  // ── Freight accruals ──────────────────────────────────────────────────────

  getAccrualSummary(asOf?: string): Observable<ApiResponse<FreightAccrualSummaryModel>> {
    let params = new HttpParams();
    if (asOf) params = params.set('asOf', asOf);
    return this.http.get<ApiResponse<FreightAccrualSummaryModel>>(
      `${BASE}/freight-accruals/summary`, { params });
  }

  getAccrual(consignmentUuid: string): Observable<ApiResponse<FreightAccrualModel>> {
    return this.http.get<ApiResponse<FreightAccrualModel>>(
      `${BASE}/freight-accruals/${consignmentUuid}`);
  }

  accrueConsignment(consignmentUuid: string): Observable<ApiResponse<FreightAccrualModel>> {
    return this.http.post<ApiResponse<FreightAccrualModel>>(
      `${BASE}/freight-accruals/${consignmentUuid}`, {});
  }

  reverseAccrual(consignmentUuid: string, reason: string): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(
      `${BASE}/freight-accruals/${consignmentUuid}/reverse`, { reason });
  }

  // ── Cash on delivery ──────────────────────────────────────────────────────

  getCodSummary(asOf?: string): Observable<ApiResponse<CodSummaryModel>> {
    let params = new HttpParams();
    if (asOf) params = params.set('asOf', asOf);
    return this.http.get<ApiResponse<CodSummaryModel>>(`${BASE}/cod/summary`, { params });
  }

  getCodList(filter: CodFilter = {}): Observable<ApiResponse<PaginatedResponse<CodCollectionModel>>> {
    let params = new HttpParams();
    if (filter.carrierUuid) params = params.set('carrierUuid', filter.carrierUuid);
    if (filter.status)      params = params.set('status',      filter.status);
    params = params.set('page',     String(filter.page     ?? 1));
    params = params.set('pageSize', String(filter.pageSize ?? 20));
    return this.http.get<ApiResponse<PaginatedResponse<CodCollectionModel>>>(`${BASE}/cod`, { params });
  }

  getCod(consignmentUuid: string): Observable<ApiResponse<CodCollectionModel>> {
    return this.http.get<ApiResponse<CodCollectionModel>>(`${BASE}/cod/${consignmentUuid}`);
  }

  recordCodCollection(
    consignmentUuid: string,
    req: { amount: number; collectedAt?: string; reference?: string }
  ): Observable<ApiResponse<CodCollectionModel>> {
    return this.http.post<ApiResponse<CodCollectionModel>>(
      `${BASE}/cod/${consignmentUuid}/collected`, req);
  }

  recordCodRemittance(
    consignmentUuid: string,
    req: { amount: number; receivedAt?: string; reference?: string; note?: string }
  ): Observable<ApiResponse<CodCollectionModel>> {
    return this.http.post<ApiResponse<CodCollectionModel>>(
      `${BASE}/cod/${consignmentUuid}/remitted`, req);
  }

  writeOffCod(consignmentUuid: string, reason: string): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${BASE}/cod/${consignmentUuid}/write-off`, { reason });
  }

  // ── Delivery exceptions ───────────────────────────────────────────────────

  getExceptionSummary(): Observable<ApiResponse<ExceptionSummaryModel>> {
    return this.http.get<ApiResponse<ExceptionSummaryModel>>(`${BASE}/delivery-exceptions/summary`);
  }

  getExceptions(
    filter: ExceptionFilter = {}
  ): Observable<ApiResponse<PaginatedResponse<DeliveryExceptionModel>>> {
    let params = new HttpParams();
    if (filter.carrierUuid)     params = params.set('carrierUuid',     filter.carrierUuid);
    if (filter.consignmentUuid) params = params.set('consignmentUuid', filter.consignmentUuid);
    if (filter.exceptionType)   params = params.set('exceptionType',   filter.exceptionType);
    if (filter.severity)        params = params.set('severity',        filter.severity);
    if (filter.status)          params = params.set('status',          filter.status);
    if (filter.unassigned !== undefined && filter.unassigned !== null)
      params = params.set('unassigned', String(filter.unassigned));
    params = params.set('page',     String(filter.page     ?? 1));
    params = params.set('pageSize', String(filter.pageSize ?? 20));
    return this.http.get<ApiResponse<PaginatedResponse<DeliveryExceptionModel>>>(
      `${BASE}/delivery-exceptions`, { params });
  }

  getException(uuid: string): Observable<ApiResponse<DeliveryExceptionModel>> {
    return this.http.get<ApiResponse<DeliveryExceptionModel>>(`${BASE}/delivery-exceptions/${uuid}`);
  }

  raiseException(
    consignmentUuid: string, req: RaiseExceptionRequest
  ): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(
      `${BASE}/delivery-exceptions/consignment/${consignmentUuid}`, req);
  }

  patchException(uuid: string, req: PatchExceptionRequest): Observable<ApiResponse> {
    return this.http.patch<ApiResponse>(`${BASE}/delivery-exceptions/${uuid}`, req);
  }

  resolveException(uuid: string, resolution: string): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(
      `${BASE}/delivery-exceptions/${uuid}/resolve`, { resolution });
  }

  withdrawException(uuid: string, reason: string): Observable<ApiResponse> {
    return this.http.post<ApiResponse>(`${BASE}/delivery-exceptions/${uuid}/withdraw`, { reason });
  }

  // ── Proof of delivery ─────────────────────────────────────────────────────

  getProofCoverage(): Observable<ApiResponse<ProofCoverageModel>> {
    return this.http.get<ApiResponse<ProofCoverageModel>>(`${BASE}/delivery-proofs/coverage`);
  }

  getProofsForConsignment(consignmentUuid: string): Observable<ApiResponse<DeliveryProofModel[]>> {
    return this.http.get<ApiResponse<DeliveryProofModel[]>>(
      `${BASE}/delivery-proofs/consignment/${consignmentUuid}`);
  }

  recordProof(
    consignmentUuid: string, req: RecordProofRequest
  ): Observable<ApiResponse<string>> {
    return this.http.post<ApiResponse<string>>(
      `${BASE}/delivery-proofs/consignment/${consignmentUuid}`, req);
  }

  patchProof(
    uuid: string,
    req: { receivedBy?: string; relationship?: string; location?: string; notes?: string }
  ): Observable<ApiResponse> {
    return this.http.patch<ApiResponse>(`${BASE}/delivery-proofs/${uuid}`, req);
  }

  /**
   * Multipart, because the bytes are the point. The content type is checked against the bytes
   * server-side, so nothing here needs to trust what the browser claimed.
   */
  attachProofFile(proofUuid: string, kind: string, file: File): Observable<ApiResponse<string>> {
    const body = new FormData();
    body.append('kind', kind);
    body.append('file', file, file.name);
    return this.http.post<ApiResponse<string>>(`${BASE}/delivery-proofs/${proofUuid}/files`, body);
  }

  /** The URL of an artefact. Authenticated like every other call — never a public link. */
  proofFileUrl(fileUuid: string): string {
    return `${BASE}/delivery-proofs/files/${fileUuid}`;
  }

  downloadProofFile(fileUuid: string): Observable<Blob> {
    return this.http.get(this.proofFileUrl(fileUuid), { responseType: 'blob' });
  }

  removeProofFile(fileUuid: string): Observable<ApiResponse> {
    return this.http.delete<ApiResponse>(`${BASE}/delivery-proofs/files/${fileUuid}`);
  }

  // ── Carrier scorecard ─────────────────────────────────────────────────────

  /**
   * Whether the billing block comes back is decided server-side from the caller's permissions —
   * there is no flag to send, because a flag the client can send is one it can lie about.
   */
  getCarrierScorecard(filter: ScorecardFilter = {}): Observable<ApiResponse<CarrierScorecardModel>> {
    let params = new HttpParams();
    if (filter.from)        params = params.set('from',        filter.from);
    if (filter.to)          params = params.set('to',          filter.to);
    if (filter.carrierUuid) params = params.set('carrierUuid', filter.carrierUuid);
    if (filter.sortBy)      params = params.set('sortBy',      filter.sortBy);
    return this.http.get<ApiResponse<CarrierScorecardModel>>(
      `${BASE}/carrier-scorecard`, { params });
  }
}
