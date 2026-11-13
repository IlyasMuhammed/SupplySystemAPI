using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace SMS.Shared.Onboarding;

// A50 (SEED-OPS) — small helpers shared by the ISetupCategorySeeder implementations in Lookups, Inventory, Logistics,
// WorkflowEngine and Auth: payload reading (case-insensitive, tolerant), fingerprints, and the one-transaction runner.

public static class SetupJson
{
    /// <summary>The property, matched case-insensitively; null when absent or JSON null.</summary>
    public static JsonElement? Prop(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in e.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                return p.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : p.Value;
        return null;
    }

    public static IEnumerable<JsonElement> Array(JsonElement e, string name) =>
        Prop(e, name) is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray().ToList() : [];

    public static string? Text(JsonElement e, string name) => Prop(e, name) switch
    {
        { ValueKind: JsonValueKind.String } s => string.IsNullOrWhiteSpace(s.GetString()) ? null : s.GetString()!.Trim(),
        { ValueKind: JsonValueKind.Number } n => n.GetRawText(),
        _ => null
    };

    public static decimal? Decimal(JsonElement e, string name) => Prop(e, name) switch
    {
        { ValueKind: JsonValueKind.Number } n when n.TryGetDecimal(out var d) => d,
        { ValueKind: JsonValueKind.String } s when decimal.TryParse(s.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var d) => d,
        _ => null
    };

    public static int? Int(JsonElement e, string name) => Decimal(e, name) is { } d && d == Math.Truncate(d) && d is >= int.MinValue and <= int.MaxValue ? (int)d : null;

    public static bool? Bool(JsonElement e, string name) => Prop(e, name) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        _ => null
    };

    public static IReadOnlyList<string> Strings(JsonElement e, string name) =>
        Array(e, name).Where(x => x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString()))
            .Select(x => x.GetString()!.Trim()).ToList();
}

public static class SetupFingerprint
{
    /// <summary>A short, stable hash of the given values (order matters; nulls are distinct from empty).</summary>
    public static string Of(params object?[] values)
    {
        var text = string.Join("\u001f", values.Select(v => v switch
        {
            null        => "\u0000",
            decimal d   => d.ToString("0.############", CultureInfo.InvariantCulture),
            bool b      => b ? "1" : "0",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _           => v.ToString()
        }));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
    }
}

public static class SetupUnitOfWork
{
    /// <summary>
    /// Runs one category's work in ONE transaction of the module's DbContext (D-11): its own under the execution
    /// strategy, or the caller's when one is already open. Tracked entities are forgotten on failure. Off a relational
    /// provider (unit tests) there is no transaction.
    /// </summary>
    public static async Task<T> RunAsync<T>(DbContext db, Func<Task<T>> work, CancellationToken ct)
    {
        try
        {
            // SMS.Shared has no Relational reference: the in-memory provider is the one without transactions.
            var inMemory = db.Database.ProviderName?.EndsWith(".InMemory", StringComparison.Ordinal) == true;
            if (inMemory || db.Database.CurrentTransaction is not null)
                return await work();

            var strategy = db.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var result = await work();
                await tx.CommitAsync(ct);
                return result;
            });
        }
        catch
        {
            db.ChangeTracker.Clear();
            throw;
        }
    }
}
