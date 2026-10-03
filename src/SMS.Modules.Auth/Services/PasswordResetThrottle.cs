using Microsoft.Extensions.Caching.Memory;

namespace SMS.Modules.Auth.Services;

/// <summary>
/// Bounds how hard the anonymous "forgot password" flow can be ground through, per e-mail address, with no
/// schema change: counters live in memory, and the one thing that must survive a restart — "this code is
/// spent" — is written to the account itself (the code is cleared).
/// <para>
/// <b>Why.</b> The reset code is six digits, valid for an hour, and <c>reset-password</c> used to accept any
/// number of guesses: a million codes at a few hundred requests a second is about an hour's work, and the
/// account at the end of it could be the platform super admin's. Now each code admits
/// <see cref="MaxAttemptsPerCode"/> guesses before it is cleared, and an address gets at most
/// <see cref="MaxCodesPerWindow"/> codes per <see cref="CodeWindow"/>, so the best an attacker can do is
/// 15 guesses an hour against a million — while the account's owner receives every one of those codes by
/// e-mail and sees it happening.
/// </para>
/// <para>
/// <b>Counted whether or not the account exists</b>, so the answers cannot be used to tell which addresses
/// have accounts. <b>Attempts are reserved before the code is compared</b> (an atomic increment), so a
/// burst of parallel guesses still gets five comparisons in total, not five each.
/// </para>
/// <para>
/// <b>Per process.</b> Counters are not shared between API instances and reset on restart; the code itself
/// is still cleared in the database on the last allowed wrong guess. The cache is bounded, so spraying
/// random addresses cannot exhaust memory — at worst it evicts counters early.
/// </para>
/// </summary>
internal sealed class PasswordResetThrottle : IDisposable
{
    internal const int MaxAttemptsPerCode = 5;
    internal const int MaxCodesPerWindow = 3;
    internal static readonly TimeSpan CodeWindow = TimeSpan.FromHours(1);

    /// <summary>How long a code's attempt counter is kept — at least the code's own lifetime.</summary>
    internal static readonly TimeSpan AttemptWindow = TimeSpan.FromMinutes(60);

    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 100_000 });
    private readonly object _gate = new();

    /// <summary>
    /// Records a request for a new code. False once this address has had <see cref="MaxCodesPerWindow"/>
    /// codes in the current window — the caller then sends nothing, and answers exactly as if it had.
    /// A code that is sent starts with a fresh set of attempts.
    /// </summary>
    public bool TryIssueCode(string email)
    {
        var key = Normalize(email);
        var issued = Increment("issued:" + key, CodeWindow);
        if (issued > MaxCodesPerWindow) return false;

        _cache.Remove("attempts:" + key);
        return true;
    }

    /// <summary>Reserves one guess at this address's current code and returns its number (1-based).</summary>
    public int RegisterAttempt(string email) => Increment("attempts:" + Normalize(email), AttemptWindow);

    /// <summary>Forgets the attempts once the code has done its job (the password was reset).</summary>
    public void Clear(string email) => _cache.Remove("attempts:" + Normalize(email));

    private int Increment(string key, TimeSpan window)
    {
        Counter counter;
        // GetOrCreate is not atomic: two first requests racing could each create a counter and lose a
        // count. Creation is rare and cheap, so it is simply serialized.
        lock (_gate)
        {
            counter = _cache.GetOrCreate(key, entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = window;
                entry.Size = 1;
                return new Counter();
            })!;
        }
        return Interlocked.Increment(ref counter.Value);
    }

    private static string Normalize(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();

    public void Dispose() => _cache.Dispose();

    private sealed class Counter
    {
        public int Value;
    }
}
