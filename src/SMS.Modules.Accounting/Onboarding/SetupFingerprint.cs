using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Accounting.Onboarding;

/// <summary>A50 D-5 — a stable hash of seeded values (invariant formatting, '|'-joined), 32 hex chars. Same scheme as Finance's.</summary>
internal static class SetupFingerprint
{
    public static string Of(params object?[] values)
    {
        var text = string.Join("|", values.Select(v => v switch
        {
            null => "",
            decimal d => d.ToString("0.####", CultureInfo.InvariantCulture),
            bool b => b ? "1" : "0",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => v.ToString()
        }));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32].ToLowerInvariant();
    }

    /// <summary>A copy of the context with no provenance (a Full reset seeds from scratch).</summary>
    public static SetupSeedContext Fresh(SetupSeedContext c) => new()
    {
        CountryCode = c.CountryCode, IndustryType = c.IndustryType, Version = c.Version, UserId = c.UserId, WizardInput = c.WizardInput, AlreadySeeded = [],
    };
}
