using Intuit.Ipp.Core;
using Intuit.Ipp.Core.Configuration;
using Intuit.Ipp.Security;
using Microsoft.Extensions.Options;
using SMS.Modules.Integration.Configuration;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Domain;
using IntuitLogger     = Intuit.Ipp.Diagnostics.ILogger;
using IntuitTraceLevel = Intuit.Ipp.Diagnostics.TraceLevel;
using CompressionFormat   = Intuit.Ipp.Core.Configuration.CompressionFormat;
using SerializationFormat = Intuit.Ipp.Core.Configuration.SerializationFormat;

namespace SMS.Modules.Integration.Providers.QuickBooks;

/// <summary>
/// Builds a <see cref="ServiceContext"/> per company and call (plan QBI-10), with every SDK default that
/// matters set explicitly rather than inherited.
/// <para>What was verified against the installed SDK (14.7.1.6) and why each setting is here:</para>
/// <list type="bullet">
/// <item><b>Configuration source.</b> <c>ServiceContext</c>'s constructor, given no configuration provider,
/// builds a <c>JsonFileConfigurationProvider</c> over <c>appsettings.json</c> in the working directory —
/// SMS.API's own file. A <see cref="MemoryConfigurationProvider"/> over a fully built
/// <see cref="IppConfiguration"/> keeps the SDK from reading anything of ours.</item>
/// <item><b>Base URL.</b> <c>GetBaseURL</c> falls back to the <em>production</em> URL when
/// <c>BaseUrl.Qbo</c> is empty, so the sandbox URL must always be set for a sandbox company. The
/// environment comes from the connection (what the company was connected as), not from today's config.</item>
/// <item><b>Minor version.</b> <c>IppConfiguration.MinorVersion.Qbo</c>, pinned from options.</item>
/// <item><b>No SDK retries.</b> <c>SyncRestHandler</c> runs the call through <c>RetryPolicy.ExecuteAction</c>
/// only when <c>IppConfiguration.RetryPolicy</c> is non-null; it stays null. An SDK retry would bypass the
/// sync ledger and could create a record twice.</item>
/// <item><b>No SDK logging.</b> <c>Logger.RequestLog.EnableRequestResponseLogging = false</c> (it writes
/// request and response bodies to disk), every <c>AdvancedLogger.RequestAdvancedLog</c> Serilog switch
/// off, and a no-op <c>Logger.CustomLogger</c> — the SDK calls it unconditionally, so it cannot be null.</item>
/// <item><b>JSON</b> both ways, no compression.</item>
/// </list>
/// </summary>
internal sealed class QboServiceContextFactory : IQboServiceContextFactory
{
    internal const string SandboxBaseUrl    = "https://sandbox-quickbooks.api.intuit.com/";
    internal const string ProductionBaseUrl = "https://quickbooks.api.intuit.com/";

    /// <summary>Per request, in milliseconds (the SDK hands it to <c>HttpWebRequest.Timeout</c>).</summary>
    internal const int RequestTimeoutMs = 60_000;

    private readonly ITokenManager              _tokens;
    private readonly IOptions<QuickBooksOptions> _options;

    public QboServiceContextFactory(ITokenManager tokens, IOptions<QuickBooksOptions> options)
    {
        _tokens  = tokens;
        _options = options;
    }

    public async Task<ServiceContext> CreateAsync(ProviderContext ctx, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ctx);

        if (string.IsNullOrWhiteSpace(ctx.RealmId))
            throw new ConnectionUnavailableException(ConnectionStatus.NotConnected,
                "The QuickBooks connection has no company (realm) bound to it. Connect QuickBooks.");

        // Refreshed first when close to expiry (plan §2.7 rule 2) — a context is never built around a
        // token that is about to die mid-call.
        var accessToken = await _tokens.GetValidAccessTokenAsync(ctx.ConnectionId, ct);

        return Build(ctx.RealmId, ctx.Environment, _options.Value.MinorVersion, accessToken);
    }

    internal static ServiceContext Build(string realmId, IntegrationEnvironment environment, string minorVersion, string accessToken)
    {
        var configuration = new IppConfiguration
        {
            Logger = new Logger
            {
                CustomLogger = NullIntuitLogger.Instance,
                RequestLog   = new RequestLog
                {
                    EnableRequestResponseLogging = false,
                    ServiceRequestLoggingLocation = Path.GetTempPath()
                }
            },
            AdvancedLogger = new AdvancedLogger
            {
                RequestAdvancedLog = new RequestAdvancedLog
                {
                    EnableSerilogRequestResponseLoggingForDebug   = false,
                    EnableSerilogRequestResponseLoggingForTrace   = false,
                    EnableSerilogRequestResponseLoggingForConsole = false,
                    EnableSerilogRequestResponseLoggingForFile    = false,
                    ServiceRequestLoggingLocationForFile          = Path.GetTempPath()
                }
            },
            Message = new Message
            {
                Request  = new Request  { SerializationFormat = SerializationFormat.Json, CompressionFormat = CompressionFormat.None },
                Response = new Response { SerializationFormat = SerializationFormat.Json, CompressionFormat = CompressionFormat.None }
            },
            BaseUrl = new BaseUrl
            {
                Qbo = environment == IntegrationEnvironment.Production ? ProductionBaseUrl : SandboxBaseUrl
            },
            MinorVersion  = new MinorVersion { Qbo = minorVersion },
            VerifierToken = new VerifierToken(),
            // Deliberately null: see the class comment.
            RetryPolicy   = null
        };

        return new ServiceContext(
            realmId,
            IntuitServicesType.QBO,
            new OAuth2RequestValidator(accessToken),
            new MemoryConfigurationProvider(configuration))
        {
            Timeout = RequestTimeoutMs
        };
    }

    /// <summary>The SDK logs through this unconditionally (e.g. every base-URL lookup). It goes nowhere.</summary>
    private sealed class NullIntuitLogger : IntuitLogger
    {
        public static readonly NullIntuitLogger Instance = new();
        public void Log(IntuitTraceLevel idsTraceLevel, string messageToWrite) { }
    }
}
