using Hangfire;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SMS.Modules.Logistics.Couriers;
using SMS.Modules.Logistics.Couriers.Booking;
using SMS.Modules.Logistics.Couriers.Labels;
using SMS.Modules.Logistics.Couriers.Tracking;
using SMS.Modules.Logistics.Couriers.Manual;
using SMS.Modules.Logistics.Couriers.Simulator;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Rating;
using SMS.Modules.Logistics.Repositories;
using SMS.Modules.Logistics.Services;
using SMS.Modules.Logistics.Settlement;
using SMS.Modules.Logistics.Visibility;
using SMS.Shared.Common;

namespace SMS.Modules.Logistics;

public interface ILogisticsModule { }

public static class LogisticsModuleExtensions
{
    /// <summary>
    /// Applies this module's schema migrations at startup, as every other module does.
    /// <para>
    /// Until this existed, <c>Program.cs</c> called <c>UseLookupsModule</c>,
    /// <c>UseWorkflowEngineModule</c>, <c>UseDemandModule</c>, <c>UseInventoryModule</c>,
    /// <c>UseWarehouseModule</c>, <c>UseFinanceModule</c> and <c>UseMaterialModule</c> — but
    /// nothing for Logistics. Its two existing migrations were only ever applied by hand, which
    /// is survivable for a two-table module and not for a twenty-five-table one.
    /// </para>
    /// <para><c>Migrate()</c> is idempotent: already-applied migrations are skipped.</para>
    /// </summary>
    public static IApplicationBuilder UseLogisticsModule(this IApplicationBuilder app)
    {
        using var scope = app.ApplicationServices.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LogisticsDbContext>();
        db.Database.Migrate();

        // Moves legacy logistics.shipments rows into the four-layer model. Idempotent, so it is
        // a no-op on every start after the first. The legacy table itself is left untouched — it
        // still backs the existing Angular screens and SMS.Modules.Reports reads it directly.
        scope.ServiceProvider.GetRequiredService<ILegacyShipmentBackfillService>()
             .BackfillAsync().GetAwaiter().GetResult();

        // Every five minutes — well inside the ledger's five-minute lease plus the two-minute
        // grace, so nothing stalls for long, and cheap when there is nothing to do.
        RecurringJob.AddOrUpdate<ConsignmentBookingSweepJob>(
            ConsignmentBookingSweepJob.RecurringJobId,
            job => job.RunAsync(),
            "*/5 * * * *");

        // Every ten minutes. Each consignment carries its own schedule (30 minutes out for delivery,
        // hours otherwise), so this only decides how promptly a due poll is picked up.
        RecurringJob.AddOrUpdate<ConsignmentTrackingPoller>(
            ConsignmentTrackingPoller.RecurringJobId,
            job => job.RunAsync(),
            "*/10 * * * *");

        // Nightly, at 02:00. Accrual is a period-end figure rather than a live one, and a sweep
        // that asks "what has moved and is not accrued" catches everything however it got there —
        // a webhook, a poll, or somebody pressing a button.
        RecurringJob.AddOrUpdate<FreightAccrualSweepJob>(
            FreightAccrualSweepJob.RecurringJobId,
            job => job.RunAsync(),
            "0 2 * * *");

        // Every ten minutes, alongside the tracking poll that feeds it. A customs hold that sits
        // unnoticed until the nightly run has cost a day nobody can get back.
        RecurringJob.AddOrUpdate<DeliveryExceptionSweepJob>(
            DeliveryExceptionSweepJob.RecurringJobId,
            job => job.RunAsync(),
            "*/10 * * * *");

        return app;
    }

    public static IServiceCollection AddLogisticsModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connString = configuration["Data:mainOrg"]!;

        services.AddDbContext<LogisticsDbContext>(options =>
            options.UseSqlServer(connString, sql =>
                sql.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(500), null)));

        services.AddScoped<ICarrierRepository,  CarrierRepository>();
        services.AddScoped<IShipmentRepository, ShipmentRepository>();
        services.AddScoped<IDeliveryRepository, DeliveryRepository>();
        services.AddScoped<IDeliveryFromSourceRepository, DeliveryFromSourceRepository>();
        services.AddScoped<IDeliveryStatusRepository, DeliveryStatusRepository>();
        services.AddScoped<IDeliveryReleaseRepository, DeliveryReleaseRepository>();
        services.AddScoped<IConsignmentRepository, ConsignmentRepository>();
        services.AddScoped<IConsignmentService,  ConsignmentService>();
        services.AddScoped<ILegacyShipmentBackfillService, LegacyShipmentBackfillService>();
        services.AddScoped<ICarrierService,     CarrierService>();
        services.AddScoped<IShipmentService,    ShipmentService>();
        services.AddScoped<IAddressNormalizer,  AddressNormalizer>();
        services.AddScoped<IDocumentNumberGenerator, DocumentNumberGenerator>();
        services.AddScoped<IDeliveryService,    DeliveryService>();
        services.AddScoped<ISaleOrderDeliveryService, SaleOrderDeliveryService>();
        // A29-P7-04 — what Finance reads to bill a delivery. Contract in SMS.Shared, so Finance
        // needs no reference to Logistics.
        services.AddScoped<IDeliveryFulfillmentReader, DeliveryFulfillmentReader>();
        services.AddScoped<IPickListRepository, PickListRepository>();
        services.AddScoped<IPickListService,    PickListService>();
        services.AddScoped<IGoodsIssueRepository, GoodsIssueRepository>();
        services.AddScoped<IPackageRepository,  PackageRepository>();
        services.AddScoped<IPackageService,     PackageService>();
        services.AddScoped<IDeliveryDocumentService, DeliveryDocumentService>();

        // Courier integration (Phase 2). Adapters register themselves as ICourierProvider and the
        // registry resolves them by Carrier.ProviderKey — adding a carrier is a registration, not
        // a new case in a switch somewhere. Singleton because the set cannot change at runtime,
        // and building it is where duplicate keys are caught.
        services.AddSingleton<ICourierProviderRegistry, CourierProviderRegistry>();

        // Most carriers have no API, and the manual path is the one that has to work first — so
        // it is an adapter like any other rather than a branch beside them. Never switchable off:
        // a deployment with no manual carrier is not a deployment anybody wants.
        services.AddSingleton<ICourierProvider, ManualCourierProvider>();

        // Which account a booking goes out on, and what that account may be asked for.
        services.AddScoped<ICarrierAccountRepository, CarrierAccountRepository>();
        services.AddScoped<ICarrierAccountService,    CarrierAccountService>();
        services.AddScoped<ICarrierAccountResolver,   CarrierAccountResolver>();

        // The named products a carrier sells. Carries the dim divisor, so rating (Phase 3) starts
        // here rather than at the parcel.
        services.AddScoped<ICarrierServiceRepository,     CarrierServiceRepository>();
        services.AddScoped<ICarrierServiceCatalogService, CarrierServiceCatalogService>();

        // What a carrier actually bills on (T-44). The calculator itself is a pure static function
        // with no dependencies; this only joins it to the consignment's packages and its service.
        services.AddScoped<IChargeableWeightService, ChargeableWeightService>();

        // Negotiated tariffs (T-46) — the price for carriers that will not quote, and the figure a
        // carrier's own quote is checked against for the ones that will. Decision G9.
        services.AddScoped<IRateCardRepository, RateCardRepository>();
        services.AddScoped<IRateCardService,    RateCardService>();

        // Where G9's two halves meet (T-47): the carrier is asked first, the card answers when it
        // will not, and either way the consignment ends up carrying what the carriage costs.
        // One instance serves both interfaces, so rate shopping (T-48) stores its winner through
        // exactly the code a direct rating uses — transition, charge lines and all.
        services.AddScoped<ConsignmentRatingService>();
        services.AddScoped<IConsignmentRatingService>(sp => sp.GetRequiredService<ConsignmentRatingService>());
        services.AddScoped<IConsignmentQuoteStore>(sp   => sp.GetRequiredService<ConsignmentRatingService>());

        services.AddScoped<IRateShoppingService, RateShoppingService>();

        // Standing decisions about how goods ship (T-49). Rules narrow the choice; shopping still
        // does the pricing, so a rule never needs rewriting when a tariff changes.
        services.AddScoped<IShippingRuleRepository, ShippingRuleRepository>();
        services.AddScoped<IShippingRuleService,    ShippingRuleService>();

        // Freight settlement (Phase 4). What is owed for movements already made — the figure a
        // period close needs and a carrier invoice gets matched against.
        services.AddScoped<IFreightAccrualService, FreightAccrualService>();
        services.AddScoped<FreightAccrualSweepJob>();

        // Bills from carriers (T-53). Deliberately not Finance's Invoice, which is supplier-bound
        // and purchase-order shaped — see decision G10.
        services.AddScoped<ICarrierInvoiceService, CarrierInvoiceService>();

        // Tying each line of a bill to the movement it charges for (T-54). The airway bill first,
        // and nothing guessed where more than one movement fits.
        services.AddScoped<IInvoiceMatchingService, InvoiceMatchingService>();

        // The comparison the whole phase exists for (T-55): quoted, agreed at booking, and billed.
        services.AddScoped<IThreeWayMatchService, ThreeWayMatchService>();

        // Cash a carrier collects on delivery (T-57) — the half of decision G2 that was never
        // built. The opposite of an accrual: money the carrier is holding on our behalf.
        services.AddScoped<ICodReconciliationService, CodReconciliationService>();

        // Accepting a bill, or querying it (T-56). Stops at approval: whether an approved bill
        // posts into Finance is decision G10, and nothing here presumes an answer.
        services.AddScoped<IInvoiceSettlementService, InvoiceSettlementService>();

        // What has gone wrong with a movement (T-60), and the sweep that raises one from a carrier
        // event that plainly says so. This is what finally reaches DeliveryExceptionType, which had
        // held eight named causes since T-04 with nothing in src/ using one (F46).
        services.AddScoped<IDeliveryExceptionService, DeliveryExceptionService>();
        services.AddScoped<DeliveryExceptionSweepJob>();

        // Proof that goods reached somebody (T-61) — the artefacts stored, not a URL somebody typed
        // into the legacy table and hoped would still resolve (F47).
        services.AddScoped<IDeliveryProofService, DeliveryProofService>();

        // How each carrier has actually performed (T-63). Reads only — every figure it reports was
        // already stored by the task that had to store it.
        services.AddScoped<ICarrierScorecardService, CarrierScorecardService>();

        // The consignee's own view (T-62, decision G11). Serves the module's second anonymous
        // endpoint, so it checks the organization itself rather than relying on the feature filter.
        services.AddScoped<IPublicTrackingService, PublicTrackingService>();

        // The only code that encrypts, decrypts or stores a carrier secret. TryAdd on the
        // encryption service because SMS.Modules.Suppliers registers the same pair (F29).
        services.TryAddScoped<IEncryptionService, AesEncryptionService>();
        services.AddScoped<ICarrierCredentialVault,   CarrierCredentialVault>();
        services.AddScoped<ICarrierCredentialService, CarrierCredentialService>();

        // Written before every booking or cancellation call, so a retry asks instead of re-booking.
        services.AddScoped<ICarrierCommandLedger, CarrierCommandLedger>();

        // Booking through an adapter (T-37): the request, the job that makes the call, and the
        // sweep that picks up anything that stalled. One instance serves both interfaces.
        services.AddScoped<ConsignmentBookingService>();
        services.AddScoped<IConsignmentBookingService>(sp  => sp.GetRequiredService<ConsignmentBookingService>());
        services.AddScoped<IConsignmentBookingExecutor>(sp => sp.GetRequiredService<ConsignmentBookingService>());
        services.AddScoped<IConsignmentBookingScheduler, HangfireConsignmentBookingScheduler>();
        services.AddScoped<ConsignmentBookingJob>();
        services.AddScoped<ConsignmentBookingSweepJob>();

        // Labels (T-38): stored in the database and served only through the authenticated endpoint.
        services.AddScoped<ConsignmentLabelService>();
        services.AddScoped<IConsignmentLabelService>(sp => sp.GetRequiredService<ConsignmentLabelService>());
        services.AddScoped<IConsignmentLabelStore>(sp   => sp.GetRequiredService<ConsignmentLabelService>());

        // Tracking (T-39): the recorder both webhooks and the poll write through, the anonymous
        // webhook receiver, and the timeline read.
        services.AddScoped<ITrackingEventRecorder, TrackingEventRecorder>();
        services.AddScoped<ICarrierWebhookService, CarrierWebhookService>();
        services.AddScoped<IConsignmentTrackingService, ConsignmentTrackingService>();

        // The poll and stuck sweep (T-40) — the only tracking for carriers that do not push, and the
        // backstop for the ones that do.
        services.AddScoped<ConsignmentTrackingPoller>();

        // The simulator books, labels and tracks without shipping anything, which is what lets the
        // rest of Phase 2 be built and demonstrated before a sandbox credential exists. It is
        // inert unless a carrier row is explicitly pointed at it — but it is still a fake carrier
        // sitting in a production binary, so it can be switched off outright.
        if (configuration.GetValue("Logistics:CourierSimulator:Enabled", true))
            services.AddSingleton<ICourierProvider, SimulatorCourierProvider>();

        // Registers deliveries with the workflow engine's composite dispatcher, which routes by
        // InterfaceCode. Nothing else is needed for the inbox, delegation, recall and audit.
        services.AddScoped<IDocumentStatusHandler, DeliveryStatusHandler>();

        return services;
    }
}
