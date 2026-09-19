using System.Reflection;

namespace SMS.Modules.Logistics.Domain;

/// <summary>
/// Converts between the module's enums and the strings they persist as.
/// <para>
/// Parsing is strict on purpose. <see cref="Enum.TryParse{TEnum}(string, out TEnum)"/> would
/// happily accept a member <em>name</em>, an unrelated integer, or a differently-cased string,
/// and a bad status read out of the database would then land on whichever member happens to be
/// first. Everything here goes through an explicit code table instead, and an unknown code is
/// always a failure.
/// </para>
/// </summary>
internal static class LogisticsCode
{
    // Built once per enum type, on first use.
    private static class Table<TEnum> where TEnum : struct, Enum
    {
        internal static readonly IReadOnlyDictionary<TEnum, string> ToCode;
        internal static readonly IReadOnlyDictionary<string, TEnum> FromCode;

        static Table()
        {
            var toCode   = new Dictionary<TEnum, string>();
            var fromCode = new Dictionary<string, TEnum>(StringComparer.Ordinal);

            foreach (var field in typeof(TEnum).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                var attribute = field.GetCustomAttribute<CodeAttribute>()
                    ?? throw new InvalidOperationException(
                        $"{typeof(TEnum).Name}.{field.Name} has no [Code]. Every member of a " +
                        "persisted enum must declare the string it is stored as.");

                var member = (TEnum)field.GetValue(null)!;

                if (!fromCode.TryAdd(attribute.Value, member))
                    throw new InvalidOperationException(
                        $"{typeof(TEnum).Name} declares the code '{attribute.Value}' more than once.");

                toCode[member] = attribute.Value;
            }

            ToCode   = toCode;
            FromCode = fromCode;
        }
    }

    /// <summary>The string <paramref name="value"/> persists as.</summary>
    internal static string Of<TEnum>(TEnum value) where TEnum : struct, Enum =>
        Table<TEnum>.ToCode.TryGetValue(value, out var code)
            ? code
            : throw new InvalidOperationException(
                $"{typeof(TEnum).Name} has no code for '{value}'. It is not a declared member.");

    /// <summary>
    /// Parses a persisted code. Returns false for null, blank, unknown, or wrongly-cased input —
    /// it never guesses.
    /// </summary>
    internal static bool TryParse<TEnum>(string? code, out TEnum value) where TEnum : struct, Enum
    {
        if (!string.IsNullOrWhiteSpace(code) && Table<TEnum>.FromCode.TryGetValue(code, out value))
            return true;

        value = default;
        return false;
    }

    /// <summary>Parses a persisted code, throwing if it is not a declared one.</summary>
    internal static TEnum Parse<TEnum>(string? code) where TEnum : struct, Enum =>
        TryParse<TEnum>(code, out var value)
            ? value
            : throw new InvalidOperationException(
                $"'{code}' is not a valid {typeof(TEnum).Name}. Valid codes: {string.Join(", ", Codes<TEnum>())}.");

    /// <summary>Every code declared by <typeparamref name="TEnum"/>, in declaration order.</summary>
    internal static IReadOnlyCollection<string> Codes<TEnum>() where TEnum : struct, Enum =>
        (IReadOnlyCollection<string>)Table<TEnum>.ToCode.Values;
}
