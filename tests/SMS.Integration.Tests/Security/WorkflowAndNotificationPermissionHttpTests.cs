using System.Net;
using FluentAssertions;
using SMS.Integration.Tests.SapAlignment;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Integration.Tests.Security;

/// <summary>
/// The gates of <see cref="WorkflowAndNotificationPermissionTests"/> through the real Program.cs pipeline and real
/// seeded roles: an Organization Admin is refused the platform-wide WhatsApp log, while every user still reaches
/// the approval actions (the service, not a permission, decides who may approve) and their own notifications.
/// Refusing a Requester the engine's raw submit / cancel / reissue is still open (skipped below).
/// </summary>
public sealed class WorkflowAndNotificationPermissionHttpTests : IClassFixture<SapWebApplicationFactory>, IAsyncLifetime
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _k;
    private HttpClient _requester = null!;
    private HttpClient _orgAdmin  = null!;

    public WorkflowAndNotificationPermissionHttpTests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _k = new SapKit(factory, "WFN");
    }

    public async Task InitializeAsync()
    {
        _requester = await _k.LoginAsNewUserAsync((int)EnumRole.Requester, "wf-requester");
        _orgAdmin  = await _k.LoginAsNewUserAsync((int)EnumRole.OrgAdmin, "wf-orgadmin");
    }

    public Task DisposeAsync()
    {
        _requester.Dispose();
        _orgAdmin.Dispose();
        return Task.CompletedTask;
    }

    private static readonly Guid Unknown = Guid.NewGuid();

    public static TheoryData<string, object> AdministrativeWorkflowCalls => new()
    {
        { "/api/workflow/submit", new { interfaceCode = "PO", documentId = Guid.NewGuid(), documentNumber = "PO-RAW-1" } },
        { $"/api/workflow/approvals/{Unknown}/cancel",  new { cancellationReason = "not mine to cancel" } },
        { $"/api/workflow/approvals/{Unknown}/reissue", new { conditionValue = 10m } },
    };

    [Theory(Skip = "Open: see docs/security/permission-sweep.md")]
    [MemberData(nameof(AdministrativeWorkflowCalls))]
    public async Task A_requester_cannot_submit_cancel_or_reissue_through_the_raw_workflow_api(string url, object body)
    {
        (await _k.Post(url, body, _requester)).Status.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [MemberData(nameof(AdministrativeWorkflowCalls))]
    public async Task A_workflow_administrator_gets_past_the_gate_to_the_service(string url, object body)
    {
        // The seeded admin holds WORKFLOW_ADMIN; what the service then says (unknown approval, no DRAFT document)
        // is its own business — only that the gate let the call through matters here.
        var response = await _k.Post(url, body);

        response.Status.Should().NotBe(HttpStatusCode.Forbidden, response.ToString());
        response.Status.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_requester_still_reaches_the_approval_actions_where_the_service_decides()
    {
        // An approval that does not exist: the gate lets the call in, and the service answers 404 — the same path
        // that answers "you are not an assigned approver" with a 403 of its own for a real approval.
        (await _k.Post("/api/workflow/approve", new { approvalUUID = Unknown, remarks = "ok" }, _requester)).Status
            .Should().Be(HttpStatusCode.NotFound);
        (await _k.Post($"/api/workflow/approvals/{Unknown}/reject", new { rejectionReason = "no" }, _requester)).Status
            .Should().Be(HttpStatusCode.NotFound);
        (await _k.Post($"/api/workflow/approvals/{Unknown}/recall", new { recallReason = "mine" }, _requester)).Status
            .Should().Be(HttpStatusCode.NotFound);
        (await _k.Get("/api/workflow/inbox", _requester)).Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Only_the_platform_administrator_reads_the_whatsapp_dispatch_log()
    {
        (await _k.Get("/api/notifications/whatsapp-logs", _orgAdmin)).Status.Should().Be(HttpStatusCode.Forbidden,
            "the log has no organization column — an organization's own admin would read every organization's dispatches");
        (await _k.Get("/api/notifications/whatsapp-logs", _requester)).Status.Should().Be(HttpStatusCode.Forbidden);
        // This factory does not create the notifications schema, so past the gate the query itself fails —
        // what matters is that the platform administrator is let through.
        (await _k.Get("/api/notifications/whatsapp-logs")).Status.Should().NotBe(HttpStatusCode.Forbidden)
            .And.NotBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Every_user_still_reaches_their_own_notifications()
    {
        // As above: the gate is the subject; the factory has no notifications tables behind it.
        foreach (var url in new[] { "/api/notifications", "/api/notifications/unread-count", "/api/notifications/inbox" })
            (await _k.Get(url, _requester)).Status.Should().NotBe(HttpStatusCode.Forbidden, url)
                .And.NotBe(HttpStatusCode.Unauthorized, url);
        (await _k.Get("/api/tenant/current", _requester)).Status.Should().Be(HttpStatusCode.OK);
    }
}
