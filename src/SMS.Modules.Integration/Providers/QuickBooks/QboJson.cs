using Intuit.Ipp.Utility;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SMS.Modules.Integration.Providers.QuickBooks;

/// <summary>
/// JSON for the sync log. SDK objects are written with the SDK's own <see cref="JsonObjectSerializer"/>
/// (Newtonsoft + IntuitConverter), so the logged request is the body QuickBooks receives when the
/// context speaks JSON — same property names, <c>…Specified</c> handling and dates. Logging never
/// breaks a call: a serialization failure is logged as such. Callers redact before storing.
/// </summary>
internal static class QboJson
{
    /// <summary>How many records a list response logs in full; the rest are counted.</summary>
    public const int MaxLoggedListItems = 20;

    private static readonly JsonObjectSerializer Serializer = new();

    public static string? Entity(object? entity)
    {
        if (entity is null) return null;
        try
        {
            return Serializer.Serialize(entity);
        }
        catch (Exception ex)
        {
            return Describe(new JObject { ["serializationError"] = ex.GetType().Name, ["type"] = entity.GetType().Name });
        }
    }

    /// <summary><c>{"count":N,"items":[first N SDK objects]}</c>.</summary>
    public static string List<T>(IReadOnlyCollection<T> items)
    {
        var shown = items.Take(MaxLoggedListItems).Cast<object>().ToList();
        var body  = Entity(shown) ?? "[]";
        return "{\"count\":" + items.Count + ",\"items\":" + body + (items.Count > shown.Count ? ",\"truncated\":true}" : "}");
    }

    public static string Query(string query) => Describe(new JObject { ["query"] = query });

    public static string FindById(string entityName, string? id) =>
        Describe(new JObject { ["findById"] = new JObject { ["entity"] = entityName, ["id"] = id } });

    public static string Describe(JToken token) => token.ToString(Formatting.None);
}
