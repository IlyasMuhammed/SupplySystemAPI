using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// Fires once, on the first save that <paramref name="applies"/> to, and simulates a losing writer:
/// another writer commits first (<paramref name="theWinnerCommitsFirst"/>), then this save fails as a
/// unique index violation would. The in-memory provider enforces no unique indexes, so this is how a
/// lost race is reproduced.
/// </summary>
internal sealed class LoseTheRaceOnce : SaveChangesInterceptor
{
    private readonly Func<Task> _theWinnerCommitsFirst;
    private readonly Func<DbContext, bool> _applies;
    private readonly bool _asConcurrencyConflict;
    public bool Fired { get; private set; }

    /// <param name="asConcurrencyConflict">
    /// Fail as an optimistic-concurrency conflict (a stale token) rather than a unique-index violation.
    /// Used instead of letting the in-memory provider detect it, because that provider is not
    /// transactional: it would leave this save's earlier writes behind, where SQL Server rolls the
    /// whole save back.
    /// </param>
    public LoseTheRaceOnce(Func<DbContext, bool> applies, Func<Task> theWinnerCommitsFirst, bool asConcurrencyConflict = false)
    {
        _applies = applies;
        _theWinnerCommitsFirst = theWinnerCommitsFirst;
        _asConcurrencyConflict = asConcurrencyConflict;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        if (!Fired && eventData.Context is { } db && _applies(db))
        {
            Fired = true;
            await _theWinnerCommitsFirst();
            throw _asConcurrencyConflict
                ? new DbUpdateConcurrencyException("simulated stale token — another writer changed the row first")
                : new DbUpdateException("simulated unique index violation — another writer committed first");
        }

        return await base.SavingChangesAsync(eventData, result, ct);
    }
}

/// <summary>
/// Runs <paramref name="concurrentWork"/> once, just before the first save that
/// <paramref name="applies"/> to, and then lets that save carry on. It reproduces another request
/// committing between this one's read and its write — the case an optimistic concurrency token
/// exists for, and the in-memory provider does enforce a token, so the save then really fails.
/// </summary>
internal sealed class RunOnceBeforeSave : SaveChangesInterceptor
{
    private readonly Func<DbContext, bool> _applies;
    private readonly Func<Task> _concurrentWork;
    public bool Fired { get; private set; }

    public RunOnceBeforeSave(Func<DbContext, bool> applies, Func<Task> concurrentWork)
    {
        _applies = applies;
        _concurrentWork = concurrentWork;
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        if (!Fired && eventData.Context is { } db && _applies(db))
        {
            Fired = true;
            await _concurrentWork();
        }

        return await base.SavingChangesAsync(eventData, result, ct);
    }
}

/// <summary>Refuses every save, and counts how many times it was asked.</summary>
internal sealed class AlwaysFail : SaveChangesInterceptor
{
    public int Attempts { get; private set; }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        Attempts++;
        throw new DbUpdateException("the database is refusing writes");
    }
}
