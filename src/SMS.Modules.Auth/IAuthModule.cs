using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Auth.Authorization;
using SMS.Modules.Auth.Data;
using SMS.Modules.Auth.Domain;
using SMS.Modules.Auth.Jobs;
using SMS.Modules.Auth.Repositories;
using SMS.Modules.Auth.Services;
using SMS.Shared.Common;

namespace SMS.Modules.Auth;

public interface IAuthModule { }

public static class AuthModuleExtensions
{
    public static IServiceCollection AddAuthModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connString = configuration["Data:mainOrg"]!;

        services.AddDbContext<AuthDbContext>(options =>
            options.UseSqlServer(connString, sql => sql.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(500), null)));

        services.AddScoped<IPasswordHasher<UserAccount>, PasswordHasher<UserAccount>>();
        services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        services.AddScoped<IAuthRepository, AuthRepository>();
        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IEmailService, EmailService>();
        services.AddScoped<IEmailSender, EmailSender>();
        services.AddScoped<IUserLookupService, UserLookupService>();
        services.AddScoped<IUserQueryService, UserQueryService>();
        services.AddScoped<IOrgChartService, OrgChartService>();
        services.AddScoped<IOrgUserProvisioningService, OrgUserProvisioningService>();
        services.AddScoped<SessionCleanupJob>();
        services.AddScoped<AuthDataSeeder>();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();

        return services;
    }

    /// <summary>
    /// Registers Hangfire recurring jobs and seeds auth data.
    /// Call after <c>app.UseHangfireDashboard()</c> in Program.cs.
    /// </summary>
    public static IApplicationBuilder UseAuthModule(this IApplicationBuilder app)
    {
        RecurringJob.AddOrUpdate<SessionCleanupJob>(
            "auth.session-cleanup",
            job => job.RunAsync(),
            Cron.Daily(2)); // 2:00 AM UTC

        using var scope = app.ApplicationServices.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<AuthDataSeeder>();
        seeder.SeedAsync().GetAwaiter().GetResult();

        return app;
    }
}
