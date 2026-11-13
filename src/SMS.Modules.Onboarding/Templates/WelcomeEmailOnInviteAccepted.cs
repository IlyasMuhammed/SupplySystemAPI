using Microsoft.Extensions.Logging;
using SMS.Shared.Common;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Onboarding.Templates;

/// <summary>
/// A50 TPL (D-26) — the welcome email once an invited user has set up their account (SEED-OPS's accept-invite hook). The
/// org's WelcomeEmail template, else the built-in text, read for <see cref="InviteAcceptedContext.OrganizationId"/> explicitly:
/// accept-invite is anonymous, there is no ambient tenant. A failed send is logged and never fails the accept.
/// </summary>
internal sealed class WelcomeEmailOnInviteAccepted : IUserInviteAcceptedHandler
{
    private readonly EmailTemplateRenderer _renderer;
    private readonly IEmailSender _sender;
    private readonly IOrganizationNameLookup? _orgNames;
    private readonly ILogger<WelcomeEmailOnInviteAccepted>? _logger;

    public WelcomeEmailOnInviteAccepted(
        EmailTemplateRenderer renderer, IEmailSender sender, IOrganizationNameLookup? orgNames = null, ILogger<WelcomeEmailOnInviteAccepted>? logger = null)
    {
        _renderer = renderer;
        _sender = sender;
        _orgNames = orgNames;
        _logger = logger;
    }

    public async Task OnInviteAcceptedAsync(InviteAcceptedContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!DocumentEmailAddresses.IsValid(context.Email)) return;

        var orgName = context.OrganizationName
                      ?? (_orgNames is null ? null : await _orgNames.GetNameAsync(context.OrganizationId, ct))
                      ?? "your organization";
        var values = Values(context, orgName);

        var rendered = await _renderer.RenderForAsync(context.OrganizationId, EmailTemplateTypes.WelcomeEmail, values, ct);
        if (rendered is null)
        {
            var builtIn = BuiltInEmails.Entry(EmailTemplateTypes.WelcomeEmail);
            rendered = EmailTemplateRenderer.Render(builtIn.SubjectTemplate, builtIn.BodyHtml, values);
        }

        var result = await _sender.SendWithResultAsync(context.Email, rendered.Subject, rendered.BodyHtml, null, ct);
        if (result is { Success: false })
            _logger?.LogWarning("The welcome email to user {UserId} was not sent: {Error}", context.UserId, result.Error);
    }

    internal static Dictionary<string, string?> Values(InviteAcceptedContext c, string organizationName) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["UserName"] = string.Join(' ', new[] { c.FirstName, c.LastName }.Where(s => !string.IsNullOrWhiteSpace(s))),
            ["FirstName"] = c.FirstName,
            ["OrganizationName"] = organizationName,
            ["CompanyName"] = organizationName,
            ["RoleName"] = c.RoleName,
            ["LoginUrl"] = c.LoginUrl ?? string.Empty,
            ["AcceptInviteUrl"] = string.Empty,
            ["Message"] = string.Empty,
        };
}
