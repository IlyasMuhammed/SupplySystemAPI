using SMS.Modules.Finance.Integration.Posting;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Repositories;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.Services;

internal sealed class DebitNoteService : IDebitNoteService
{
    private readonly IDebitNoteRepository  _repo;
    private readonly SupplierNotePostings? _postings;

    /// <param name="postings">
    /// A44 (D-24, D-75) — after the create commits (a debit note is created ISSUED): the note as a vendor credit; after
    /// a WRITTEN_OFF: its reversal. Optional (hand-built harnesses).
    /// </param>
    public DebitNoteService(IDebitNoteRepository repo, SupplierNotePostings? postings = null)
    {
        _repo     = repo;
        _postings = postings;
    }

    public async Task<Guid> CreateAsync(CreateDebitNoteRequest req, int createdBy)
    {
        var uuid = await _repo.CreateAsync(req, createdBy);
        if (_postings is not null) await _postings.PublishDebitNoteAsync(uuid);
        return uuid;
    }

    public async Task UpdateStatusAsync(Guid uuid, UpdateDebitNoteStatusRequest req, int userId)
    {
        await _repo.UpdateStatusAsync(uuid, req, userId);
        if (_postings is not null) await _postings.PublishDebitNoteStatusAsync(uuid, req.NewStatus, userId);
    }

    public Task ApplyCarriedForwardAsync(Guid uuid, ApplyDebitNoteRequest req, int userId) =>
        _repo.ApplyCarriedForwardAsync(uuid, req, userId);

    public Task<PaginatedResponse<DebitNoteListItemModel>> GetListAsync(DebitNoteListFilter filter) =>
        _repo.GetListAsync(filter);

    public Task<DebitNoteDetailModel?> GetByIdAsync(Guid uuid) =>
        _repo.GetByIdAsync(uuid);
}
