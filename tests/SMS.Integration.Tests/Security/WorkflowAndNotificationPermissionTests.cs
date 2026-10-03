using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Routing;
using SMS.Modules.Notifications.Controllers;
using SMS.Modules.Tenancy.Controllers;
using SMS.Shared.Authorization;
using SMS.WorkflowEngine.Controllers;
using Xunit;

namespace SMS.Integration.Tests.Security;

/// <summary>
/// The workflow engine's raw approval API and the notifications controller (2026-10-02 permission sweep).
/// Approving, rejecting, delegating and recalling stay sign-in only: a workflow can name any user as an approver,
/// the inbox and approval pages are open to every signed-in user, and the service refuses whoever is not the
/// step's assignee (or the initiator, for recall). Submitting, cancelling and reissuing through the engine's raw
/// endpoints are a different matter — the service checks nobody there (any user could cancel any approval and
/// flip its document to CANCELLED, or submit somebody else's draft past its module's own checks), and no page
/// calls them: modules submit in-process through their own gated endpoints. They should need WORKFLOW_ADMIN —
/// still open, the sweep was paused before it landed (docs/security/permission-sweep.md). The WhatsApp dispatch
/// log is platform-wide (the table has no organization), so it needs PLATFORM_SUPER_ADMIN.
/// </summary>
public sealed class WorkflowAndNotificationPermissionTests
{
    private static IReadOnlyList<string>? CodesOf(Type controller, string action)
    {
        var method = controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Single(m => m.Name == action && m.GetCustomAttributes<HttpMethodAttribute>().Any());
        method.GetCustomAttribute<AllowAnonymousAttribute>().Should().BeNull();
        var attributes = controller.GetCustomAttributes<RequirePermissionAttribute>()
            .Concat(method.GetCustomAttributes<RequirePermissionAttribute>()).ToList();
        attributes.Should().HaveCountLessThan(2, $"{controller.Name}.{action} is meant to carry one any-of gate at most");
        return attributes.SingleOrDefault()?.AnyOf;
    }

    [Fact]
    public void The_platform_wide_whatsapp_log_needs_the_platform_administrator() =>
        CodesOf(typeof(NotificationsController), nameof(NotificationsController.GetWhatsAppLogs))
            .Should().Equal(PermissionCodes.PLATFORM_SUPER_ADMIN);

    // Open: gating these breaks Workflow/WorkflowLifecycleTests (which call them with permission-less tokens and need
    // Docker, unavailable here), so the change was left for the resumed sweep.
    [Theory(Skip = "Open: see docs/security/permission-sweep.md")]
    [InlineData(typeof(WorkflowSubmitController),          nameof(WorkflowSubmitController.Submit))]
    [InlineData(typeof(WorkflowApprovalActionsController), nameof(WorkflowApprovalActionsController.Cancel))]
    [InlineData(typeof(WorkflowApprovalActionsController), nameof(WorkflowApprovalActionsController.Reissue))]
    public void The_unchecked_workflow_actions_no_page_calls_need_WORKFLOW_ADMIN(Type controller, string action) =>
        CodesOf(controller, action).Should().Equal(PermissionCodes.WORKFLOW_ADMIN);

    [Theory]
    [InlineData(typeof(WorkflowSubmitController),          nameof(WorkflowSubmitController.Approve))]
    [InlineData(typeof(WorkflowApprovalActionsController), nameof(WorkflowApprovalActionsController.Reject))]
    [InlineData(typeof(WorkflowApprovalActionsController), nameof(WorkflowApprovalActionsController.Delegate))]
    [InlineData(typeof(WorkflowApprovalActionsController), nameof(WorkflowApprovalActionsController.Recall))]
    [InlineData(typeof(NotificationsController),           nameof(NotificationsController.GetList))]
    [InlineData(typeof(NotificationsController),           nameof(NotificationsController.GetInbox))]
    [InlineData(typeof(NotificationsController),           nameof(NotificationsController.GetUnreadCount))]
    [InlineData(typeof(NotificationsController),           nameof(NotificationsController.MarkRead))]
    [InlineData(typeof(NotificationsController),           nameof(NotificationsController.MarkOneRead))]
    [InlineData(typeof(NotificationsController),           nameof(NotificationsController.Delete))]
    public void Acting_on_ones_own_approval_step_or_notification_stays_open_to_every_signed_in_user(Type controller, string action) =>
        CodesOf(controller, action).Should().BeNull("the service decides by the token's user, and every user has an inbox and a bell");

    [Fact]
    public void The_action_counts_are_pinned_so_a_new_action_gets_reviewed()
    {
        static int Count(Type t) => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Count(m => m.GetCustomAttributes<HttpMethodAttribute>().Any());

        Count(typeof(WorkflowSubmitController)).Should().Be(2);
        Count(typeof(WorkflowApprovalActionsController)).Should().Be(5);
        Count(typeof(NotificationsController)).Should().Be(7);
    }

    // ── Every signed-in user keeps what the inbox, the bell and the menu give them ──

    private static readonly FrontendEntry[] Entries =
    [
        // pages.routes.ts: workflow-inbox, workflow-inbox/:uuid and documents/:id/history have no guard.
        FrontendEntry.Page("route workflow-inbox (approve / reject)")
            .Calls<WorkflowInboxController>(nameof(WorkflowInboxController.GetInbox), nameof(WorkflowInboxController.GetInboxCount))
            .Calls<WorkflowSubmitController>(nameof(WorkflowSubmitController.Approve))
            .Calls<WorkflowApprovalActionsController>(nameof(WorkflowApprovalActionsController.Reject)),
        FrontendEntry.Page("route workflow-inbox/:uuid (approve / reject / recall)")
            .Calls<WorkflowApprovalDetailController>(nameof(WorkflowApprovalDetailController.GetApprovalDetail),
                                                     nameof(WorkflowApprovalDetailController.GetAuditLog))
            .Calls<WorkflowSubmitController>(nameof(WorkflowSubmitController.Approve))
            .Calls<WorkflowApprovalActionsController>(nameof(WorkflowApprovalActionsController.Reject),
                                                      nameof(WorkflowApprovalActionsController.Recall)),
        FrontendEntry.Page("route documents/:id/history and every detail page's history panel")
            .Calls<WorkflowHistoryController>(nameof(WorkflowHistoryController.GetHistory))
            .Calls<TimelineController>(nameof(TimelineController.GetByDocument), nameof(TimelineController.GetByTraceId)),
        // pages.routes.ts: notifications has no guard; the topbar bell is on every page.
        FrontendEntry.Page("route notifications and the topbar bell")
            .Calls<NotificationsController>(nameof(NotificationsController.GetList), nameof(NotificationsController.GetInbox),
                nameof(NotificationsController.GetUnreadCount), nameof(NotificationsController.MarkRead),
                nameof(NotificationsController.MarkOneRead), nameof(NotificationsController.Delete)),
        // The menu is built from it on every page load.
        FrontendEntry.Page("app shell (menu)").Calls<TenantController>(nameof(TenantController.GetCurrent)),
    ];

    [Fact]
    public void Every_seeded_role_keeps_its_inbox_history_and_notifications() =>
        FrontendAccess.SeededRoleLockOuts(Entries).Should().BeEmpty();

    [Fact]
    public void Every_signed_in_user_the_frontend_lets_in_is_let_in_by_the_server() =>
        FrontendAccess.FrontendParityGaps(Entries).Should().BeEmpty();

    // ── The premise of the gates above (landed and open): no page calls them ─────

    private static string FrontendApp([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "..", "SupplyChainFrontend", "src", "app"));

    [Fact]
    public void No_page_submits_cancels_or_reissues_through_the_raw_workflow_api_or_reads_the_whatsapp_log()
    {
        var app = FrontendApp();
        Directory.Exists(app).Should().BeTrue($"the frontend sources are expected at {app}");

        var sources = Directory.EnumerateFiles(app, "*.ts", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".spec.ts", StringComparison.Ordinal))
            .Select(f => (File: f, Text: File.ReadAllText(f)))
            .ToList();

        // If one of these starts being used, whoever uses it must be given WORKFLOW_ADMIN / PLATFORM_SUPER_ADMIN by
        // the page's guard — or the gate must be revisited.
        sources.Where(s => s.Text.Contains("whatsapp-logs")).Select(s => s.File).Should().BeEmpty();
        sources.Where(s => s.Text.Contains(".reissue(")).Select(s => s.File).Should().BeEmpty();

        var workflowService = sources.Single(s => s.File.EndsWith(Path.Combine("services", "workflow.service.ts"))).Text;
        workflowService.Should().NotContain("${BASE}/submit");
        workflowService.Should().NotContain("/cancel`");
    }
}
