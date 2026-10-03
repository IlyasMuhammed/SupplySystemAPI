using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.API.Middleware;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Integration.Tests.Security;

/// <summary>
/// What SMS.API's error body tells the caller — anonymous ones included (the supplier portal, sign-in). The
/// application's own exceptions keep their message, written for the user; any other failure says only that
/// something went wrong, with a reference to the server log, and never the exception's text: that text names
/// server paths, databases, tables, columns and the values that failed.
/// </summary>
public sealed class ErrorBodyTests
{
    private static async Task<(int Status, JsonElement Body, string Raw, string Trace)> FailWith(Exception exception)
    {
        var context = new DefaultHttpContext { TraceIdentifier = "0HN-TEST:00000001" };
        context.Response.Body = new MemoryStream();

        var middleware = new GlobalExceptionMiddleware(_ => throw exception, NullLogger<GlobalExceptionMiddleware>.Instance);
        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        var raw = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return (context.Response.StatusCode, JsonDocument.Parse(raw).RootElement, raw, context.TraceIdentifier);
    }

    [Fact]
    public async Task A_file_system_failure_does_not_reveal_the_server_path()
    {
        var (status, body, raw, trace) = await FailWith(new IOException(
            @"The filename, directory name, or volume label syntax is incorrect. : 'C:\inetpub\sms\wwwroot\uploads\attachments\po|<>'"));

        status.Should().Be(500);
        raw.Should().NotContain("inetpub").And.NotContain("wwwroot").And.NotContain("volume label");
        body.GetProperty("message").GetString().Should().Be($"An unexpected error occurred. Reference: {trace}.");
        body.GetProperty("result").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_database_failure_does_not_reveal_the_schema_or_the_value()
    {
        var (status, _, raw, _) = await FailWith(new DbUpdateException(
            "An error occurred while saving the entity changes. See the inner exception for details.",
            new InvalidOperationException(
                "String or binary data would be truncated in table 'SMSGlobal.finance.invoices', column 'Notes'. Truncated value: 'secret note'.")));

        status.Should().Be(500);
        raw.Should().NotContain("SMSGlobal").And.NotContain("finance.invoices").And.NotContain("secret note")
           .And.NotContain("inner exception");
    }

    [Fact]
    public async Task A_concurrency_conflict_keeps_its_own_message_and_nothing_more()
    {
        var (status, body, raw, _) = await FailWith(new DbUpdateConcurrencyException(
            "The database operation was expected to affect 1 row(s), but actually affected 0 row(s) in table 'inventory.items'."));

        status.Should().Be(409);
        body.GetProperty("message").GetString().Should().StartWith("Another user or process changed the same stock");
        raw.Should().NotContain("inventory.items");
    }

    [Theory]
    [InlineData(typeof(BadRequestException), 400)]
    [InlineData(typeof(NotFoundException),   404)]
    [InlineData(typeof(ConflictException),   409)]
    public async Task The_applications_own_errors_keep_their_message_where_the_screens_read_it(Type type, int expected)
    {
        var exception = (Exception)Activator.CreateInstance(type, "INV-0042 is not approved yet; approve it before paying.")!;

        var (status, body, _, _) = await FailWith(exception);

        status.Should().Be(expected);
        body.GetProperty("message").GetString().Should().Be("INV-0042 is not approved yet; approve it before paying.");
        body.GetProperty("result").GetProperty("exceptionMessage").GetString()
            .Should().Be("INV-0042 is not approved yet; approve it before paying.", "sign-in, SRO create and the quotation page read it there");
    }
}
