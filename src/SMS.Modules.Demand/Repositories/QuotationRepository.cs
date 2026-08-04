using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Demand.Repositories;

internal sealed class QuotationRepository : IQuotationRepository
{
    private readonly DemandDbContext _db;

    private static readonly Dictionary<string, string[]> AllowedTransitions = new()
    {
        ["DRAFT"]     = ["SENT", "CANCELLED"],
        ["SENT"]      = ["AWARDED", "CANCELLED"],
        ["AWARDED"]   = [],
        ["CANCELLED"] = []
    };

    public QuotationRepository(DemandDbContext db) => _db = db;

    // Frontend p-inputNumber widgets already soft-clamp to this range, but that's only a UI
    // hint — nothing stopped a pasted value, a race before blur, or a direct API call from
    // submitting an out-of-range quantity. This is the actual enforcement.
    private const decimal MaxLineQuantity = 999999m;

    private static void ValidateLineQuantities(IEnumerable<decimal> quantities)
    {
        foreach (var qty in quantities)
        {
            if (qty <= 0)
                throw new BadRequestException("Line quantity must be greater than zero.");
            if (qty > MaxLineQuantity)
                throw new BadRequestException($"Line quantity must not exceed {MaxLineQuantity:N0}.");
        }
    }

    public async Task<Guid> CreateAsync(CreateQuotationRequest req, int createdBy)
    {
        ValidateLineQuantities(req.Lines.Select(l => l.Quantity));

        var uuid = req.QuotationUuid is { } gid && gid != Guid.Empty ? gid : Guid.NewGuid();
        var now  = DateTime.UtcNow;

        var lines = req.Lines.Select((l, i) => new QuotationLine
        {
            UUID              = Guid.NewGuid(),
            LineNo            = i + 1,
            SourcePrLineUuid  = l.SourcePrLineUuid,
            SourcePoLineUuid  = l.SourcePoLineUuid,
            ProductId         = l.ProductId,
            ItemDescription   = l.ItemDescription,
            Specification     = l.Specification,
            UnitOfMeasure     = l.UnitOfMeasure,
            Quantity          = l.Quantity,
            RequiredDate      = l.RequiredDate,
            BudgetCode        = l.BudgetCode
        }).ToList();

        var quotation = new Quotation
        {
            UUID            = uuid,
            TraceId         = await ResolveTraceIdAsync(req.SourceType, req.SourceId),
            QuotationNumber = await GenerateQuotationNumberAsync(now.Year),
            Title           = req.Title,
            SourceType      = req.SourceType,
            SourceId        = req.SourceId,
            Status          = "DRAFT",
            DueDate         = req.DueDate,
            Notes           = req.Notes,
            IsActive        = true,
            CreatedBy       = createdBy,
            CreatedDate     = now,
            Lines           = lines
        };

        _db.Quotations.Add(quotation);
        await _db.SaveChangesAsync();
        return uuid;
    }

    public async Task UpdateAsync(Guid uuid, PatchQuotationRequest req, int modifiedBy)
    {
        var quotation = await _db.Quotations
            .Include(q => q.Lines)
            .FirstOrDefaultAsync(q => q.UUID == uuid && !q.IsDelete)
            ?? throw new NotFoundException("Quotation", uuid);

        if (quotation.Status != "DRAFT")
            throw new UnprocessableEntityException(
                $"Only DRAFT quotations can be edited. Current status: {quotation.Status}.");

        if (req.Title is not null)     quotation.Title   = req.Title;
        if (req.DueDate.HasValue)      quotation.DueDate = req.DueDate;
        if (req.Notes is not null)     quotation.Notes   = req.Notes;

        if (req.Lines is not null)
        {
            ValidateLineQuantities(req.Lines.Select(l => l.Quantity));

            _db.QuotationLines.RemoveRange(quotation.Lines);
            quotation.Lines = req.Lines.Select((l, i) => new QuotationLine
            {
                UUID             = Guid.NewGuid(),
                LineNo           = i + 1,
                SourcePrLineUuid = l.SourcePrLineUuid,
                SourcePoLineUuid = l.SourcePoLineUuid,
                ProductId        = l.ProductId,
                ItemDescription  = l.ItemDescription,
                Specification    = l.Specification,
                UnitOfMeasure    = l.UnitOfMeasure,
                Quantity         = l.Quantity,
                RequiredDate     = l.RequiredDate,
                BudgetCode       = l.BudgetCode
            }).ToList();
        }

        quotation.ModifiedBy   = modifiedBy;
        quotation.ModifiedDate = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task<PaginatedResponse<QuotationListItemModel>> GetListAsync(QuotationListFilter filter)
    {
        var query = _db.Quotations.Where(q => !q.IsDelete).AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Status))
            query = query.Where(q => q.Status == filter.Status);
        if (!string.IsNullOrWhiteSpace(filter.SourceType))
            query = query.Where(q => q.SourceType == filter.SourceType);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.ToLower();
            query = query.Where(q => q.QuotationNumber.ToLower().Contains(term)
                                  || q.Title.ToLower().Contains(term));
        }

        var total    = await query.CountAsync();
        var page     = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 100);

        var items = await query
            .OrderByDescending(q => q.CreatedDate)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(q => new QuotationListItemModel
            {
                UUID            = q.UUID,
                TraceId         = q.TraceId,
                QuotationNumber = q.QuotationNumber,
                Title           = q.Title,
                SourceType      = q.SourceType,
                SourceId        = q.SourceId,
                Status          = q.Status,
                DueDate         = q.DueDate,
                ResponseCount   = q.VendorResponses.Count(),
                CreatedDate     = q.CreatedDate
            })
            .ToListAsync();

        return new PaginatedResponse<QuotationListItemModel>
        {
            Data         = items,
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(total / (double)pageSize)
        };
    }

    public async Task<QuotationDetailModel?> GetByIdAsync(Guid uuid)
    {
        var q = await _db.Quotations
            .Include(x => x.Lines.OrderBy(l => l.LineNo))
            .Include(x => x.InvitedSuppliers)
            .FirstOrDefaultAsync(x => x.UUID == uuid && !x.IsDelete);

        if (q is null) return null;

        var submittedResponseCount = await _db.VendorResponses
            .CountAsync(r => r.QuotationId == q.Id);

        return new QuotationDetailModel
        {
            UUID                   = q.UUID,
            TraceId                = q.TraceId,
            SubmittedResponseCount = submittedResponseCount,
            QuotationNumber    = q.QuotationNumber,
            Title              = q.Title,
            SourceType         = q.SourceType,
            SourceId           = q.SourceId,
            Status             = q.Status,
            DueDate            = q.DueDate,
            Notes              = q.Notes,
            CancellationReason = q.CancellationReason,
            CreatedBy          = q.CreatedBy,
            CreatedDate        = q.CreatedDate,
            BidsOpenedAt       = q.BidsOpenedAt,
            BidsOpenedBy       = q.BidsOpenedBy,
            Lines = q.Lines.Select(l => new QuotationLineModel
            {
                UUID             = l.UUID,
                LineNo           = l.LineNo,
                SourcePrLineUuid = l.SourcePrLineUuid,
                SourcePoLineUuid = l.SourcePoLineUuid,
                ProductId        = l.ProductId,
                ItemDescription  = l.ItemDescription,
                Specification    = l.Specification,
                UnitOfMeasure    = l.UnitOfMeasure,
                Quantity         = l.Quantity,
                RequiredDate     = l.RequiredDate,
                BudgetCode       = l.BudgetCode
            }).ToList(),
            InvitedSuppliers = q.InvitedSuppliers.Select(s => new QuotationInvitedSupplierModel
            {
                SupplierId   = s.SupplierId,
                SupplierName = s.SupplierName,
                InvitedAt    = s.InvitedAt
            }).ToList()
        };
    }

    public async Task SendAsync(Guid uuid, SendQuotationRequest req, int modifiedBy)
    {
        var quotation = await _db.Quotations
            .FirstOrDefaultAsync(q => q.UUID == uuid && !q.IsDelete)
            ?? throw new NotFoundException("Quotation", uuid);

        AssertTransition(quotation.Status, "SENT");

        quotation.Status       = "SENT";
        quotation.ModifiedBy   = modifiedBy;
        quotation.ModifiedDate = DateTime.UtcNow;

        var now = DateTime.UtcNow;
        _db.QuotationInvitedSuppliers.AddRange(req.Suppliers.Select(s => new QuotationInvitedSupplier
        {
            QuotationId  = quotation.Id,
            SupplierId   = s.SupplierId,
            SupplierName = s.SupplierName,
            InvitedAt    = now
        }));

        await _db.SaveChangesAsync();
    }

    public async Task<Guid> RecordResponseAsync(Guid uuid, RecordVendorResponseRequest req, int createdBy)
    {
        var quotation = await _db.Quotations
            .Include(q => q.Lines)
            .FirstOrDefaultAsync(q => q.UUID == uuid && !q.IsDelete)
            ?? throw new NotFoundException("Quotation", uuid);

        var lineMap = quotation.Lines.ToDictionary(l => l.UUID, l => l.Id);

        var now = DateTime.UtcNow;

        var responseLines = req.Lines.Select(l =>
        {
            if (!lineMap.TryGetValue(l.QuotationLineUuid, out var lineId))
                throw new BadRequestException(
                    $"QuotationLine '{l.QuotationLineUuid}' does not belong to this quotation.");
            return new VendorResponseLine
            {
                UUID            = Guid.NewGuid(),
                QuotationLineId = lineId,
                NetUnitPrice    = l.NetUnitPrice,
                Quantity        = l.Quantity,
                LineTotal       = l.NetUnitPrice * l.Quantity,
                LeadTimeDays    = l.LeadTimeDays,
                Notes           = l.Notes
            };
        }).ToList();

        // A supplier can only have one response per quotation — once recorded it's final, so a
        // second attempt for the same supplier is rejected rather than silently overwriting
        // whatever pricing was already captured for them. Ad-hoc suppliers typed by name only
        // (not picked from the catalog) all share the placeholder SupplierId, so they're
        // deduped by name instead — the only identity available for them.
        var nameNorm = req.SupplierName.Trim();
        var alreadyResponded = req.SupplierId != Guid.Empty
            ? await _db.VendorResponses.AnyAsync(r => r.QuotationId == quotation.Id && r.SupplierId == req.SupplierId)
            : await _db.VendorResponses.AnyAsync(r => r.QuotationId == quotation.Id
                && r.SupplierId == Guid.Empty && r.SupplierName.ToLower() == nameNorm.ToLower());
        if (alreadyResponded)
            throw new UnprocessableEntityException(
                $"'{req.SupplierName}' has already submitted a response for this quotation.");

        var responseUuid = Guid.NewGuid();
        _db.VendorResponses.Add(new VendorResponse
        {
            UUID         = responseUuid,
            QuotationId  = quotation.Id,
            SupplierId   = req.SupplierId,
            SupplierName = req.SupplierName,
            Status       = "PENDING",
            TotalAmount  = responseLines.Sum(l => l.LineTotal),
            ResponseDate = req.ResponseDate ?? now,
            Notes        = req.Notes,
            CreatedBy    = createdBy,
            CreatedDate  = now,
            Lines        = responseLines
        });

        await _db.SaveChangesAsync();
        return responseUuid;
    }

    public async Task<List<VendorResponseModel>> GetComparisonAsync(Guid uuid)
    {
        var quotation = await _db.Quotations
            .FirstOrDefaultAsync(q => q.UUID == uuid && !q.IsDelete)
            ?? throw new NotFoundException("Quotation", uuid);

        if (quotation.BidsOpenedAt is null)
            throw new UnprocessableEntityException(
                "Bids are sealed until they are opened. Use Open Bids once the RFQ deadline has passed.");

        var responses = await _db.VendorResponses
            .Include(r => r.Lines)
                .ThenInclude(l => l.QuotationLine)
            .Where(r => r.QuotationId == quotation.Id)
            .OrderBy(r => r.TotalAmount)
            .ToListAsync();

        return responses.Select(r => new VendorResponseModel
        {
            UUID         = r.UUID,
            SupplierId   = r.SupplierId,
            SupplierName = r.SupplierName,
            Status       = r.Status,
            TotalAmount  = r.TotalAmount,
            ResponseDate = r.ResponseDate,
            Notes        = r.Notes,
            CreatedDate  = r.CreatedDate,
            Lines = r.Lines
                .OrderBy(l => l.QuotationLine.LineNo)
                .Select(l => new VendorResponseLineModel
                {
                    UUID             = l.UUID,
                    QuotationLineUuid = l.QuotationLine.UUID,
                    ItemDescription  = l.QuotationLine.ItemDescription,
                    NetUnitPrice     = l.NetUnitPrice,
                    Quantity         = l.Quantity,
                    LineTotal        = l.LineTotal,
                    LeadTimeDays     = l.LeadTimeDays,
                    Notes            = l.Notes
                }).ToList()
        }).ToList();
    }

    // Breaks the seal: makes vendor pricing readable via GetComparisonAsync/AwardAsync. Does
    // not touch quotation.Status — SENT covers both "awaiting bids" and "bids open/awardable".
    public async Task OpenBidsAsync(Guid uuid, int openedBy)
    {
        var quotation = await _db.Quotations
            .FirstOrDefaultAsync(q => q.UUID == uuid && !q.IsDelete)
            ?? throw new NotFoundException("Quotation", uuid);

        if (quotation.Status != "SENT")
            throw new UnprocessableEntityException(
                $"Bids can only be opened for a SENT quotation. Current status: {quotation.Status}.");

        if (quotation.BidsOpenedAt is not null)
            throw new UnprocessableEntityException("Bids have already been opened for this quotation.");

        var hasResponses = await _db.VendorResponses.AnyAsync(r => r.QuotationId == quotation.Id);
        if (!hasResponses)
            throw new UnprocessableEntityException(
                "No vendor responses have been submitted yet. At least one response is required to open bids.");

        quotation.BidsOpenedAt = DateTime.UtcNow;
        quotation.BidsOpenedBy = openedBy;
        quotation.ModifiedBy   = openedBy;
        quotation.ModifiedDate = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }

    public async Task AwardAsync(Guid uuid, AwardQuotationRequest req, int awardedBy)
    {
        var quotation = await _db.Quotations
            .Include(q => q.Lines)
            .FirstOrDefaultAsync(q => q.UUID == uuid && !q.IsDelete)
            ?? throw new NotFoundException("Quotation", uuid);

        AssertTransition(quotation.Status, "AWARDED");

        // AwardAsync queries VendorResponses directly rather than going through
        // GetComparisonAsync, so it needs its own seal check — otherwise a caller could award
        // (and thus implicitly learn/act on pricing) without ever opening bids.
        if (quotation.BidsOpenedAt is null)
            throw new UnprocessableEntityException(
                "Bids are sealed until they are opened. Use Open Bids before awarding.");

        var hasResponses = await _db.VendorResponses.AnyAsync(r => r.QuotationId == quotation.Id);
        if (!hasResponses)
            throw new UnprocessableEntityException(
                "No vendor responses have been submitted yet. At least one response is required to compare and award.");

        var winningResponse = await _db.VendorResponses
            .Include(r => r.Lines)
            .FirstOrDefaultAsync(r => r.UUID == req.VendorResponseUuid && r.QuotationId == quotation.Id)
            ?? throw new NotFoundException("VendorResponse", req.VendorResponseUuid);

        var allResponses = await _db.VendorResponses
            .Where(r => r.QuotationId == quotation.Id)
            .ToListAsync();

        foreach (var r in allResponses)
            r.Status = r.Id == winningResponse.Id ? "AWARDED" : "REJECTED";

        quotation.Status       = "AWARDED";
        quotation.ModifiedBy   = awardedBy;
        quotation.ModifiedDate = DateTime.UtcNow;

        // Write awarded price back to source PR lines
        if (quotation.SourceType == "PR")
        {
            foreach (var winLine in winningResponse.Lines)
            {
                var qLine = quotation.Lines.FirstOrDefault(l => l.Id == winLine.QuotationLineId);
                if (qLine?.SourcePrLineUuid is Guid prLineUuid)
                {
                    var prLine = await _db.PrLines.FirstOrDefaultAsync(l => l.UUID == prLineUuid);
                    if (prLine is not null)
                    {
                        prLine.EstimatedUnitPrice = winLine.NetUnitPrice;
                        prLine.QuotationStatus    = "AWARDED";
                    }
                }
            }
        }

        // Write awarded price back to source PO lines
        if (quotation.SourceType == "PO")
        {
            foreach (var winLine in winningResponse.Lines)
            {
                var qLine = quotation.Lines.FirstOrDefault(l => l.Id == winLine.QuotationLineId);
                if (qLine?.SourcePoLineUuid is Guid poLineUuid)
                {
                    var poLine = await _db.PoLines.FirstOrDefaultAsync(l => l.UUID == poLineUuid);
                    if (poLine is not null)
                        poLine.UnitPrice = winLine.NetUnitPrice;
                }
            }
        }

        await _db.SaveChangesAsync();
    }

    public async Task CancelAsync(Guid uuid, string reason, int modifiedBy)
    {
        var quotation = await _db.Quotations
            .FirstOrDefaultAsync(q => q.UUID == uuid && !q.IsDelete)
            ?? throw new NotFoundException("Quotation", uuid);

        AssertTransition(quotation.Status, "CANCELLED");

        quotation.Status             = "CANCELLED";
        quotation.CancellationReason = reason;
        quotation.ModifiedBy         = modifiedBy;
        quotation.ModifiedDate       = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }

    public async Task<(int QuotationId, DateTime? DueDate)> SendWithLinkAsync(
        Guid uuid, SendWithLinkRequest req, int modifiedBy)
    {
        var quotation = await _db.Quotations
            .FirstOrDefaultAsync(q => q.UUID == uuid && !q.IsDelete)
            ?? throw new NotFoundException("Quotation", uuid);

        AssertTransition(quotation.Status, "SENT");

        quotation.Status       = "SENT";
        quotation.ModifiedBy   = modifiedBy;
        quotation.ModifiedDate = DateTime.UtcNow;

        var now = DateTime.UtcNow;
        _db.QuotationInvitedSuppliers.AddRange(req.Suppliers.Select(s => new QuotationInvitedSupplier
        {
            QuotationId  = quotation.Id,
            SupplierId   = s.SupplierId,
            SupplierName = s.SupplierName,
            InvitedAt    = now
        }));

        await _db.SaveChangesAsync();
        return (quotation.Id, quotation.DueDate);
    }

    public async Task<List<RfqAccessLinkModel>> GetAccessLinksAsync(Guid quotationUuid)
    {
        var quotation = await _db.Quotations
            .FirstOrDefaultAsync(q => q.UUID == quotationUuid && !q.IsDelete)
            ?? throw new NotFoundException("Quotation", quotationUuid);

        var links = await _db.RfqAccessLinks
            .Where(l => l.QuotationId == quotation.Id)
            .OrderByDescending(l => l.GeneratedAt)
            .ToListAsync();

        var supplierNames = await _db.QuotationInvitedSuppliers
            .Where(s => s.QuotationId == quotation.Id)
            .GroupBy(s => s.SupplierId)
            .Select(g => new { SupplierId = g.Key, SupplierName = g.First().SupplierName })
            .ToDictionaryAsync(x => x.SupplierId, x => x.SupplierName);

        return links.Select(l => new RfqAccessLinkModel
        {
            LinkId         = l.Id,
            SupplierId     = l.SupplierId,
            SupplierName   = supplierNames.TryGetValue(l.SupplierId, out var n) ? n : "Unknown",
            ContactId      = l.ContactId,
            SupplierEmail  = l.SupplierEmail,
            Status         = l.Status,
            GeneratedAt    = l.GeneratedAt,
            ExpiresAt      = l.ExpiresAt,
            EmailSentAt    = l.EmailSentAt,
            WhatsAppSentAt = l.WhatsAppSentAt,
            WhatsAppStatus = l.WhatsAppStatus,
            FirstOpenedAt  = l.FirstOpenedAt,
            ConsumedAt     = l.ConsumedAt
        }).ToList();
    }

    public async Task ValidateLinkForResendAsync(Guid quotationUuid, int linkId)
    {
        var quotation = await _db.Quotations
            .FirstOrDefaultAsync(q => q.UUID == quotationUuid && !q.IsDelete)
            ?? throw new NotFoundException("Quotation", quotationUuid);

        var link = await _db.RfqAccessLinks
            .FirstOrDefaultAsync(l => l.Id == linkId && l.QuotationId == quotation.Id)
            ?? throw new NotFoundException("RfqAccessLink", linkId);

        if (link.Status is "CONSUMED" or "EXPIRED")
            throw new UnprocessableEntityException(
                $"Cannot resend: link status is '{link.Status}'. Only PENDING and ACCESSED links can be resent.");
    }

    // TL-006: trace_id propagation — a Quotation created from a PR or PO inherits that
    // document's trace_id; a STANDALONE quotation starts a new chain of its own.
    private async Task<Guid> ResolveTraceIdAsync(string sourceType, Guid? sourceId)
    {
        if (sourceId.HasValue)
        {
            if (sourceType == "PR")
            {
                var traceId = await _db.PurchaseRequisitions
                    .Where(p => p.UUID == sourceId.Value)
                    .Select(p => (Guid?)p.TraceId)
                    .FirstOrDefaultAsync();
                if (traceId.HasValue) return traceId.Value;
            }
            else if (sourceType == "PO")
            {
                var traceId = await _db.PurchaseOrders
                    .Where(p => p.UUID == sourceId.Value)
                    .Select(p => (Guid?)p.TraceId)
                    .FirstOrDefaultAsync();
                if (traceId.HasValue) return traceId.Value;
            }
        }
        return Guid.NewGuid();
    }

    private async Task<string> GenerateQuotationNumberAsync(int year)
    {
        var yearStart = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var yearEnd   = yearStart.AddYears(1);
        var count     = await _db.Quotations
            .CountAsync(q => q.CreatedDate >= yearStart && q.CreatedDate < yearEnd);
        return $"RFQ-{year}-{(count + 1):D5}";
    }

    private static void AssertTransition(string current, string next)
    {
        if (!AllowedTransitions.TryGetValue(current, out var allowed) || !allowed.Contains(next))
            throw new BadRequestException(
                $"Cannot transition quotation from '{current}' to '{next}'.");
    }
}
