using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Extensions;
using Microsoft.OpenApi.Models;
using SMS.Integration.Tests.GeneralLedger;
using SMS.Integration.Tests.SapAlignment;
using Swashbuckle.AspNetCore.Swagger;
using Xunit;

namespace SMS.Integration.Tests.Hrm;

/// <summary>
/// A49 task 402: the OpenAPI document is generated in-process from the real Program.cs host on a throwaway LocalDB, and every HRM action must
/// be in it. Set SMS_SWAGGER_DUMP_DIR to also write the document and a route list (used to check docs/hrm/API-REFERENCE.md).
/// Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~HrmSwaggerDocumentTests</c>.
/// </summary>
public sealed class HrmSwaggerDocumentTests : IClassFixture<SapWebApplicationFactory>
{
    private const string HrmAssembly = "SMS.Modules.Hrm";
    private readonly SapWebApplicationFactory _f;

    public HrmSwaggerDocumentTests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _f.ShouldBeOnLocalDb();
    }

    [Fact]
    public void Every_HRM_action_is_in_the_document_with_a_summary_and_no_route_hides_another()
    {
        var doc = _f.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
        var actions = _f.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>().ApiDescriptionGroups.Items
            .SelectMany(g => g.Items).ToList();

        var rows = actions.Select(a =>
        {
            var d = (ControllerActionDescriptor)a.ActionDescriptor;
            var attrs = d.EndpointMetadata.Select(m => m.GetType().Name).ToList();
            return new
            {
                Method = a.HttpMethod ?? "",
                Path = "/" + (a.RelativePath ?? "").Split('?')[0],
                Controller = d.ControllerTypeInfo.Name,
                Action = d.MethodInfo.Name,
                Assembly = d.ControllerTypeInfo.Assembly.GetName().Name,
                Metadata = d.EndpointMetadata.Where(m => m.GetType().Name is "RequirePermissionAttribute" or "RequiresFeatureAttribute")
                    .Select(m => m.GetType().Name + ":" + string.Join(",", m.GetType().GetProperties().Where(p => p.Name != "TypeId")
                        .Select(p => p.GetValue(m) is var v && v is IEnumerable<string> e ? string.Join("|", e) : v?.ToString()))).ToList()
            };
        }).ToList();

        var hrm = rows.Where(r => r.Assembly == HrmAssembly).ToList();
        hrm.Should().NotBeEmpty();

        // ResolveConflictingActions(First) in Program.cs would silently drop the loser of a clash.
        hrm.GroupBy(r => (r.Method, Path: Normalize(r.Path))).Where(g => g.Count() > 1).Select(g => g.Key).Should().BeEmpty("two HRM actions share a route");

        var missing = new List<string>();
        var unsummarized = new List<string>();
        foreach (var r in hrm)
        {
            var op = Find(doc, r.Method, r.Path);
            if (op is null) missing.Add($"{r.Method} {r.Path}");
            else if (string.IsNullOrWhiteSpace(op.Summary)) unsummarized.Add($"{r.Method} {r.Path}");
        }
        missing.Should().BeEmpty();

        if (Environment.GetEnvironmentVariable("SMS_SWAGGER_DUMP_DIR") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "swagger.json"), doc.SerializeAsJson(Microsoft.OpenApi.OpenApiSpecVersion.OpenApi3_0));
            File.WriteAllText(Path.Combine(dir, "routes.json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
        }

        unsummarized.Should().BeEmpty();
    }

    private static OpenApiOperation? Find(OpenApiDocument doc, string method, string path)
    {
        if (!doc.Paths.TryGetValue(StripConstraints(path), out var item)) return null;
        return item.Operations.FirstOrDefault(o => o.Key.ToString().Equals(method, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static string StripConstraints(string path) => System.Text.RegularExpressions.Regex.Replace(path, @"\{(\w+):[^}]+\}", "{$1}");
    private static string Normalize(string path) => System.Text.RegularExpressions.Regex.Replace(StripConstraints(path), @"\{\w+\}", "{}").ToLowerInvariant();
}
