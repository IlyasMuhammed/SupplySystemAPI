using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Onboarding.Data;
using SMS.Modules.Onboarding.Domain;

namespace SMS.Modules.Onboarding.Services.Defaults;

/// <summary>
/// A50 D-4 — at every API start, inserts each embedded pack (Packs/{CC}_{INDUSTRY}/{CATEGORY}.json) whose
/// (country, industry, category) is missing. It <b>never updates</b> a row: a Super Admin's edit is never overwritten and
/// a changed pack file does not reach an existing database (BR-50-015) — the Super Admin import does that.
/// A pack that fails validation is logged and skipped, never fails the start.
/// </summary>
internal sealed class SetupPackSeeder
{
    private readonly OnboardingDbContext _db;
    private readonly PackCatalog _catalog;
    private readonly SetupPayloadValidator _validator;
    private readonly ILogger<SetupPackSeeder>? _logger;

    public SetupPackSeeder(OnboardingDbContext db, PackCatalog catalog, SetupPayloadValidator validator, ILogger<SetupPackSeeder>? logger = null)
    {
        _db = db;
        _catalog = catalog;
        _validator = validator;
        _logger = logger;
    }

    /// <returns>The number of rows inserted.</returns>
    public async Task<int> SeedAsync(CancellationToken ct = default)
    {
        var existing = (await _db.SetupDefaults.AsNoTracking().Select(r => new { r.CountryCode, r.IndustryType, r.DataCategory }).ToListAsync(ct))
            .Select(k => SetupKey.Format(k.CountryCode, k.IndustryType, k.DataCategory))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var pack in _catalog.Packs)
        {
            var key = SetupKey.Format(pack.CountryCode, pack.IndustryType, pack.DataCategory);
            if (!existing.Add(key)) continue;

            JsonElement payload;
            try
            {
                using var doc = JsonDocument.Parse(pack.Payload);
                payload = doc.RootElement.Clone();
            }
            catch (JsonException ex)
            {
                _logger?.LogError(ex, "Setup pack {Key} is not valid JSON and was not seeded.", key);
                continue;
            }

            var errors = _validator.IsKnownCategory(pack.DataCategory) ? _validator.Validate(pack.DataCategory, payload) : [$"unknown category {pack.DataCategory}"];
            if (errors.Count > 0)
            {
                _logger?.LogError("Setup pack {Key} failed validation and was not seeded: {Errors}", key, string.Join(" | ", errors.Take(10)));
                continue;
            }

            _db.SetupDefaults.Add(new SetupDefault
            {
                CountryCode = pack.CountryCode, IndustryType = pack.IndustryType, DataCategory = pack.DataCategory,
                DisplayName = pack.DisplayName(payload), DataPayload = pack.Payload, Version = 1, IsActive = true,
                Notes = JsonCanonical.TopLevelString(payload, "notes") is { } n ? (n.Length > 1000 ? n[..1000] : n) : null,
                IsSystemPack = true
            });
            added++;
        }

        if (added == 0) return 0;
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Two API instances starting together race on UQ_SD_Key: the other one inserted them. Never fails the start.
            _logger?.LogWarning(ex, "Setup packs were not seeded by this instance (probably inserted concurrently); the next start retries.");
            _db.ChangeTracker.Clear();
            return 0;
        }
        return added;
    }
}
