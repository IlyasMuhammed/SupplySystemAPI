using Microsoft.AspNetCore.Http;
using System.Threading.RateLimiting;
using Hangfire;
using Hangfire.SqlServer;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using SMS.API.Middleware;
using Microsoft.AspNetCore.Authorization;
using SMS.Shared.Authorization;
using SMS.Shared.Middleware;
using SMS.Modules.Auth;
using SMS.Modules.Auth.Authorization;
using SMS.Modules.Demand;
using SMS.Modules.Finance;
using SMS.Modules.Inventory;
using SMS.Modules.Logistics;
using SMS.Modules.Lookups;
using SMS.Modules.Notifications;
using SMS.Modules.Notifications.Hubs;
using SMS.Modules.Procurement;
using SMS.Modules.Reports;
using SMS.Modules.Suppliers;
using SMS.Modules.Tenancy;
using SMS.Modules.Warehouse;
using SMS.Modules.Material;
using SMS.Shared.Common;
using SMS.WorkflowEngine;
using Swashbuckle.AspNetCore.SwaggerUI;
using System.Text;

QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);

// ── CORS ─────────────────────────────────────────────────────────────────────
builder.Services.AddCors(options =>
    options.AddPolicy("CorsPolicy", p => p
        .WithOrigins(
            "http://localhost:3000",
            "http://localhost:5173",
            "http://localhost:64592",
            "http://localhost:4200")
        .AllowAnyMethod()
        .AllowAnyHeader()
        .AllowCredentials()));

// ── App settings ──────────────────────────────────────────────────────────────
var appSettingsSection = builder.Configuration.GetSection("AppSettings");
builder.Services.Configure<AppSettings>(appSettingsSection);
var appSettings = appSettingsSection.Get<AppSettings>()!;

// ── Multi-tenancy (MT-003) — registered once, before any module ─────────────
builder.Services.AddTenantContext();

// ── Controllers ───────────────────────────────────────────────────────────────
builder.Services.AddControllers(options =>
{
    // Prevent .NET 8 from inferring [Required] from non-nullable reference types;
    // all input validation is done explicitly in service/repository code.
    options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;

    // Require a valid JWT for every endpoint by default.
    // Individual actions/controllers can opt out with [AllowAnonymous].
    var authPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
    options.Filters.Add(new Microsoft.AspNetCore.Mvc.Authorization.AuthorizeFilter(authPolicy));

    // MT-004 — enforces [RequiresFeature] at the API level, not just in the UI.
    options.Filters.Add<FeatureAuthorizationFilter>();
});
builder.Services.AddSignalR();
builder.Services.AddNotificationsModule(builder.Configuration);

// ── AutoMapper (scans all module assemblies) ──────────────────────────────────
builder.Services.AddAutoMapper(
    typeof(IAuthModule).Assembly,
    typeof(ILookupsModule).Assembly,
    typeof(ISuppliersModule).Assembly,
    typeof(IInventoryModule).Assembly,
    typeof(IDemandModule).Assembly,
    typeof(IProcurementModule).Assembly,
    typeof(IWarehouseModule).Assembly,
    typeof(ILogisticsModule).Assembly,
    typeof(IFinanceModule).Assembly,
    typeof(IReportsModule).Assembly);

// ── Hangfire (SQL Server backing store) ───────────────────────────────────────
var connString = builder.Configuration["Data:mainOrg"]
    ?? throw new InvalidOperationException("Connection string 'Data:mainOrg' is missing.");

builder.Services.AddHangfire(config => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(connString, new SqlServerStorageOptions
    {
        CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
        SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
        QueuePollInterval = TimeSpan.Zero,
        UseRecommendedIsolationLevel = true,
        DisableGlobalLocks = true
    }));
builder.Services.AddHangfireServer();

// ── Rate limiting — per-IP throttle for the public RFQ portal ────────────────
builder.Services.AddRateLimiter(opts =>
{
    opts.AddPolicy("rfq-portal-per-ip", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit          = 30,
                Window               = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit           = 0
            }));
    opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

// ── Authorization policy provider (permission-based) ─────────────────────────
builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();

// ── Module registrations ──────────────────────────────────────────────────────
builder.Services.AddAuthModule(builder.Configuration);
builder.Services.AddTenancyModule(builder.Configuration);
builder.Services.AddLookupsModule(builder.Configuration);
builder.Services.AddSuppliersModule(builder.Configuration);
builder.Services.AddInventoryModule(builder.Configuration);
builder.Services.AddDemandModule(builder.Configuration);
builder.Services.AddProcurementModule(builder.Configuration);
builder.Services.AddWarehouseModule(builder.Configuration);
builder.Services.AddLogisticsModule(builder.Configuration);
builder.Services.AddFinanceModule(builder.Configuration);
builder.Services.AddReportsModule(builder.Configuration);
builder.Services.AddWorkflowEngineModule(builder.Configuration);
builder.Services.AddMaterialModule(builder.Configuration);

// ── JWT Authentication ─────────────────────────────────────────────────────────
var key = Encoding.ASCII.GetBytes(appSettings.Secret);
builder.Services.AddAuthentication(x =>
{
    x.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    x.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(x =>
{
    x.RequireHttpsMetadata = false;
    x.SaveToken = true;
    x.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(key),
        ValidateIssuer = false,
        ValidateAudience = false,
        ClockSkew = TimeSpan.Zero
    };
    x.Events = new JwtBearerEvents
    {
        OnAuthenticationFailed = ctx =>
        {
            if (ctx.Exception is SecurityTokenExpiredException)
                ctx.Response.Headers.Append("Token-Expired", "true");
            return Task.CompletedTask;
        },
        // SignalR sends the token as a query-string param for WebSocket connections
        OnMessageReceived = ctx =>
        {
            var accessToken = ctx.Request.Query["access_token"];
            var path = ctx.HttpContext.Request.Path;
            if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                ctx.Token = accessToken;
            return Task.CompletedTask;
        }
    };
});

// ── Swagger ────────────────────────────────────────────────────────────────────
builder.Services.AddSwaggerGen(s =>
{
    s.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "SMS — Supply Management System API",
        Description = "ASP.NET Core 8 | SQL Server 2022 | Modular Monolith",
        Version = "v1",
        Contact = new Microsoft.OpenApi.Models.OpenApiContact
        {
            Name = "Support Team",
            Email = appSettings.AppSupportEmail
        }
    });

    var securityScheme = new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Description = "Enter JWT Bearer token",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Reference = new Microsoft.OpenApi.Models.OpenApiReference
        {
            Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
            Id = "Bearer"
        }
    };
    s.AddSecurityDefinition("Bearer", securityScheme);
    s.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
        { { securityScheme, new[] { "Bearer" } } });

    s.ResolveConflictingActions(apiDescriptions => apiDescriptions.First());

    // Prevent duplicate schemaId errors when multiple modules define types with the same class name
    s.CustomSchemaIds(t => t.FullName?.Replace("+", "_").Replace("`", "_").Replace(",", "_") ?? t.Name);

    // Allow Swashbuckle to correctly represent file-upload endpoints
    s.MapType<IFormFile>(() => new Microsoft.OpenApi.Models.OpenApiSchema
    {
        Type   = "string",
        Format = "binary"
    });
});

// ─────────────────────────────────────────────────────────────────────────────
var app = builder.Build();

// Lets every Hangfire background job resolve the ambient ITenantContext to the org that actually
// enqueued it, instead of a hardcoded default — see TenantPropagatingJobFilter for why this exists.
GlobalJobFilters.Filters.Add(new SMS.API.Infrastructure.TenantPropagatingJobFilter(
    app.Services.GetRequiredService<IHttpContextAccessor>()));

app.UseMiddleware<GlobalExceptionMiddleware>();
app.UseMiddleware<RequestLoggingMiddleware>();

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseCors("CorsPolicy");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<TenantMiddleware>(); // MT-004 — 401s a deactivated org's requests

app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "SMS API v1.0");
    c.RoutePrefix = "swagger";
    c.DocExpansion(DocExpansion.None);
});

// Diagnostic endpoint — remove after fixing Swagger 500
app.MapGet("/swagger-error", (Swashbuckle.AspNetCore.Swagger.ISwaggerProvider provider) =>
{
    try { provider.GetSwagger("v1"); return Results.Ok("Swagger OK"); }
    catch (Exception ex) { return Results.Text(ex.ToString()); }
}).AllowAnonymous();

app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    IsReadOnlyFunc = _ => false
});

app.UseAuthModule();           // registers Hangfire jobs + seeds permissions/roles
app.UseTenancyModule();        // migrates tenant schema + seeds org/feature catalog
app.UseLookupsModule();        // seeds lookup types and values
app.UseWorkflowEngineModule(); // migrates workflow schema + seeds default PR/PO/GRN definitions
app.UseDemandModule();         // registers RFQ link expiry sweep (daily Hangfire job)
app.UseInventoryModule();      // ensures inventory schema migrations (ledger table, etc.) are applied
app.UseWarehouseModule();      // ensures warehouse schema migrations (SRO tables) are applied
app.UseFinanceModule();        // ensures finance schema migrations (credit_notes table) are applied
app.UseMaterialModule();       // ensures material schema migrations (projects, MIR tables) are applied

app.MapControllers();
app.MapHub<NotificationHub>("/hubs/notifications");
app.Run();

public partial class Program { }
    