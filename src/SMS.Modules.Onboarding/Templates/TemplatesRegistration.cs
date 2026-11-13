using Microsoft.Extensions.DependencyInjection;
using SMS.Shared.Common;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Onboarding.Templates;

/// <summary>A50 D-25 / D-26 (TPL): document and email templates. Called by AddOnboardingModule (before its TryAdd fallbacks).</summary>
internal static class TemplatesRegistration
{
    public static IServiceCollection AddOnboardingTemplates(this IServiceCollection services)
    {
        services.AddScoped<TemplateSupport>();
        services.AddSingleton<TemplatedDocumentRenderer>();
        services.AddSingleton<ITemplatedDocumentRenderer>(sp => sp.GetRequiredService<TemplatedDocumentRenderer>());

        services.AddScoped<DocumentBrandingProvider>();
        services.AddScoped<IDocumentBrandingProvider>(sp => sp.GetRequiredService<DocumentBrandingProvider>());
        services.AddScoped<EmailTemplateRenderer>();
        services.AddScoped<IEmailTemplateRenderer>(sp => sp.GetRequiredService<EmailTemplateRenderer>());

        services.AddScoped<IDocumentTemplateService, DocumentTemplateService>();
        services.AddScoped<IEmailTemplateService, EmailTemplateService>();

        services.AddScoped<ISetupCategorySeeder, DocumentTemplatesSetupSeeder>();
        services.AddScoped<ISetupCategorySeeder, EmailTemplatesSetupSeeder>();

        // Accept-invite (Auth) → the welcome email (SEED-OPS's hook, D-26).
        services.AddScoped<IUserInviteAcceptedHandler, WelcomeEmailOnInviteAccepted>();
        return services;
    }
}
