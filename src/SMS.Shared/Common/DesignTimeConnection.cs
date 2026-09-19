using System.Text.Json;

namespace SMS.Shared.Common;

/// <summary>
/// The connection string "dotnet ef" tooling uses in every module's design-time DbContext factory.
/// <para>
/// Resolves to the same database the running API uses — <c>Data:mainOrg</c> in
/// <c>src/SMS.API/appsettings.json</c> — so migrations are listed, checked and applied against
/// the real database rather than a local one the app never touches. There is deliberately no
/// localdb fallback: a silent fallback reports migrations as pending on the wrong database.
/// </para>
/// <para>
/// <c>SMS_DB_CONNECTION</c>, when set, overrides it (CI, or pointing at another database on purpose).
/// </para>
/// </summary>
public static class DesignTimeConnection
{
    public const string EnvironmentVariable = "SMS_DB_CONNECTION";

    public static string Resolve()
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv;

        var settingsPath = FindApiSettings()
            ?? throw new InvalidOperationException(
                $"Could not find SMS.API/appsettings.json above '{Directory.GetCurrentDirectory()}'. " +
                $"Run dotnet ef from inside the repository, or set {EnvironmentVariable}.");

        // appsettings.json carries // comments (the commented-out localdb line), so skip them.
        using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath), new JsonDocumentOptions
        {
            CommentHandling     = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        if (doc.RootElement.TryGetProperty("Data", out var data)
            && data.TryGetProperty("mainOrg", out var mainOrg)
            && mainOrg.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(mainOrg.GetString()))
            return mainOrg.GetString()!;

        throw new InvalidOperationException($"'Data:mainOrg' not found in {settingsPath}.");
    }

    // dotnet ef runs with the working directory set to the project, the startup project or the
    // repository root depending on how it is invoked — so walk up rather than assume one.
    private static string? FindApiSettings()
    {
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir != null; dir = dir.Parent)
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(dir.FullName, "SMS.API", "appsettings.json"),
                         Path.Combine(dir.FullName, "src", "SMS.API", "appsettings.json")
                     })
            {
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }
}
