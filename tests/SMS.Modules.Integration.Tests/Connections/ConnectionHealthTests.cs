using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Domain;

namespace SMS.Modules.Integration.Tests.Connections;

public class ConnectionHealthTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    private static async Task MarkAsync(ConnectionsHarness h, Guid org, int id, ConnectionStatus status, string reason = "401 from QuickBooks")
    {
        await using var scope = h.Scope(org);
        await scope.ServiceProvider.GetRequiredService<IConnectionHealth>().MarkUnavailableAsync(id, status, reason);
    }

    [Theory]
    [InlineData("Revoked")]
    [InlineData("Expired")]
    public async Task A_usable_connection_is_marked_and_its_outbox_suspended(string statusName)
    {
        var status = Enum.Parse<ConnectionStatus>(statusName);
        await using var h = ConnectionsHarness.Create();
        var c = await h.SeedConnectionAsync(OrgA);

        await MarkAsync(h, OrgA, c.Id, status, "QuickBooks said 401 to a fresh token");

        var stored = await h.ReloadConnectionAsync(c.Id);
        stored.Status.Should().Be(status);
        stored.LastError.Should().Be("QuickBooks said 401 to a fresh token");
        h.Outbox.Suspended.Should().ContainSingle().Which.Should().Be((c.Id, "QuickBooks said 401 to a fresh token"));
    }

    [Theory]
    [InlineData("NotConnected")]
    [InlineData("Expired")]
    public async Task A_more_precise_existing_verdict_is_kept(string existingName)
    {
        var existing = Enum.Parse<ConnectionStatus>(existingName);
        await using var h = ConnectionsHarness.Create();
        var c = await h.SeedConnectionAsync(OrgA, existing);

        await MarkAsync(h, OrgA, c.Id, ConnectionStatus.Revoked);

        (await h.ReloadConnectionAsync(c.Id)).Status.Should().Be(existing);
    }

    [Theory]
    [InlineData("Live")]
    [InlineData("NotConnected")]
    [InlineData("Connecting")]
    public async Task Only_Revoked_or_Expired_can_be_marked(string statusName)
    {
        var status = Enum.Parse<ConnectionStatus>(statusName);
        await using var h = ConnectionsHarness.Create();
        var c = await h.SeedConnectionAsync(OrgA);

        var act = () => MarkAsync(h, OrgA, c.Id, status);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Another_organizations_connection_cannot_be_marked()
    {
        await using var h = ConnectionsHarness.Create();
        var c = await h.SeedConnectionAsync(OrgA);

        await MarkAsync(h, OrgB, c.Id, ConnectionStatus.Revoked);

        (await h.ReloadConnectionAsync(c.Id)).Status.Should().Be(ConnectionStatus.Live);
    }

    [Fact]
    public async Task A_very_long_reason_is_truncated_to_the_column()
    {
        await using var h = ConnectionsHarness.Create();
        var c = await h.SeedConnectionAsync(OrgA);

        await MarkAsync(h, OrgA, c.Id, ConnectionStatus.Revoked, new string('x', 5000));

        (await h.ReloadConnectionAsync(c.Id)).LastError!.Length.Should().BeLessThanOrEqualTo(1000);
    }
}
