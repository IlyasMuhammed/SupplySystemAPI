namespace SMS.Modules.Logistics.Models;

public class CreateConsignmentRequest
{
    public Guid?   CarrierUuid        { get; set; }
    public string? CarrierServiceCode { get; set; }
    /// <summary>COURIER, LTL, FTL, OWN_FLEET or HAND. Defaults to COURIER.</summary>
    public string? Mode               { get; set; }
    /// <summary>PREPAID, COLLECT or THIRD_PARTY. Defaults to PREPAID.</summary>
    public string? FreightTerms       { get; set; }

    public decimal? CodAmount   { get; set; }
    public string?  CodCurrency { get; set; }

    public DateTime? PickupWindowStart { get; set; }
    public DateTime? PickupWindowEnd   { get; set; }
    public DateTime? Etd               { get; set; }
    public DateTime? Eta               { get; set; }

    public string? VehicleNumber { get; set; }
    public string? DriverName    { get; set; }
    public string? DriverPhone   { get; set; }
    public string? Notes         { get; set; }

    /// <summary>Deliveries to load onto this consignment. One consignment can carry several.</summary>
    public List<Guid>? DeliveryUuids { get; set; }
}

/// <summary>
/// An airway bill obtained from the carrier outside the system — by phone, portal or paper note.
/// </summary>
public class ManualBookingRequest
{
    public string  Awb              { get; set; } = string.Empty;
    public string? CarrierReference { get; set; }
}

/// <summary>Books a consignment through its carrier's adapter (T-37). Both fields are optional.</summary>
public class BookConsignmentRequest
{
    /// <summary>The account to book on. Defaults to the carrier's default account.</summary>
    public Guid?   CarrierAccountUuid { get; set; }

    /// <summary>Overrides the consignment's service code, then the account's default.</summary>
    public string? ServiceCode        { get; set; }
}

/// <summary>
/// A person's answer, after checking with the carrier, to a booking whose outcome is unknown.
/// </summary>
public class ResolveBookingRequest
{
    /// <summary>True if the carrier has the booking; false if it has no record of it.</summary>
    public bool    CarrierBooked { get; set; }

    /// <summary>The airway bill the carrier confirmed. Required when <see cref="CarrierBooked"/> is true.</summary>
    public string? Awb           { get; set; }

    /// <summary>How it was confirmed — who at the carrier, or what its portal shows. Required.</summary>
    public string  Note          { get; set; } = string.Empty;
}

/// <summary>Where a consignment's booking stands, and whether anyone needs to do anything.</summary>
public class ConsignmentBookingStatusModel
{
    public Guid    ConsignmentUuid    { get; set; }
    public string  ConsignmentNumber  { get; set; } = string.Empty;

    /// <summary>The consignment's status — BOOKING while under way.</summary>
    public string  Status             { get; set; } = string.Empty;

    public string? MasterAwb          { get; set; }
    public string? TrackingUrl        { get; set; }

    /// <summary>Why the last attempt did not book, or what is being waited on. Null once booked.</summary>
    public string? FailureReason      { get; set; }

    public Guid?   CarrierAccountUuid { get; set; }
    public string? CarrierAccountName { get; set; }
    public bool    IsSandbox          { get; set; }

    /// <summary>The latest carrier call's ledger status — IN_FLIGHT, SUCCEEDED, REFUSED, UNKNOWN, NOT_PERFORMED.</summary>
    public string?   CommandStatus    { get; set; }
    public int       AttemptCount     { get; set; }
    public DateTime? FirstAttemptAt   { get; set; }
    public DateTime? LastAttemptAt    { get; set; }

    /// <summary>
    /// A label for the current airway bill is already stored. When false the label may still be
    /// available — it is fetched from the carrier on first print.
    /// </summary>
    public bool      HasStoredLabel   { get; set; }
    public DateTime? LabelStoredAt    { get; set; }

    /// <summary>The outcome is unknown and will not be retried: a person must check with the carrier.</summary>
    public bool NeedsResolution        { get; set; }

    /// <summary>The outcome is unknown and the carrier deduplicates, so it will be retried without anyone acting.</summary>
    public bool WillRetryAutomatically { get; set; }
}

public class ConsignmentDeliveryModel
{
    public Guid   DeliveryUuid   { get; set; }
    public string DeliveryNumber { get; set; } = string.Empty;
    public string Status         { get; set; } = string.Empty;
    public int    Sequence       { get; set; }
}

public class ConsignmentDetailModel
{
    public Guid    UUID               { get; set; }
    public string  ConsignmentNumber  { get; set; } = string.Empty;
    public Guid?   CarrierUuid        { get; set; }
    public string? CarrierName        { get; set; }
    public string? CarrierServiceCode { get; set; }

    /// <summary>MANUAL, API or FILE. Tells the UI whether to offer manual AWB entry or a booking button.</summary>
    public string  IntegrationMode    { get; set; } = string.Empty;

    public string  Mode               { get; set; } = string.Empty;
    public string? MasterAwb          { get; set; }
    public string? CarrierReference   { get; set; }

    /// <summary>Built from the carrier's tracking template and the AWB. Null when either is missing.</summary>
    public string? TrackingUrl        { get; set; }

    public string   FreightTerms      { get; set; } = string.Empty;
    public decimal? CodAmount         { get; set; }
    public string?  CodCurrency       { get; set; }

    public DateTime? PickupWindowStart { get; set; }
    public DateTime? PickupWindowEnd   { get; set; }
    public DateTime? Etd               { get; set; }
    public DateTime? Eta               { get; set; }

    public string? VehicleNumber { get; set; }
    public string? DriverName    { get; set; }
    public string? DriverPhone   { get; set; }

    public string  Status { get; set; } = string.Empty;
    public string? Notes  { get; set; }

    /// <summary>The account it was booked on. Null for a manual carrier or before booking.</summary>
    public Guid?   CarrierAccountUuid   { get; set; }
    public string? CarrierAccountName   { get; set; }

    /// <summary>Why the last booking attempt did not book, or what it is waiting on.</summary>
    public string? BookingFailureReason { get; set; }

    public List<string> AllowedNextStatuses { get; set; } = [];
    public DateTime     CreatedDate         { get; set; }

    public List<ConsignmentDeliveryModel> Deliveries { get; set; } = [];
}
