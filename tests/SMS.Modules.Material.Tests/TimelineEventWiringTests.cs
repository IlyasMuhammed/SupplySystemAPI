using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Models;
using SMS.Modules.Material.Repositories;
using SMS.Modules.Material.Services;
using SMS.WorkflowEngine.Models;
using SMS.WorkflowEngine.Services;
using Xunit;

namespace SMS.Modules.Material.Tests;

// ── Test builder ──────────────────────────────────────────────────────────────

file static class WiringBuild
{
    internal static (MirRepository repo, MaterialDbContext material, InventoryDbContext inventory) NewMirRepo()
    {
        var material = new MaterialDbContext(new DbContextOptionsBuilder<MaterialDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);
        var demand = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var inventory = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        return (new MirRepository(material, inventory, demand, NullLogger<MirRepository>.Instance), material, inventory);
    }

    internal static Product SeedProduct(InventoryDbContext db, Guid uuid)
    {
        var product = new Product { Uuid = uuid, Sku = "SKU-001", Name = "Lenovo Laptop", UomCode = "PC", UnitCost = 1000m, IsActive = true };
        db.Products.Add(product);
        db.SaveChanges();
        return product;
    }

    internal static (Mock<IBackgroundJobClient> Mock, List<Job> Captured) MockJobs()
    {
        var captured = new List<Job>();
        var mock = new Mock<IBackgroundJobClient>();
        mock.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()))
            .Callback<Job, IState>((job, _) => captured.Add(job))
            .Returns("fake-job-id");
        return (mock, captured);
    }

    internal static TimelineEvent CapturedEvent(List<Job> captured) =>
        (TimelineEvent)captured.Single(j => j.Method.Name == "AppendAsync").Args[1];
}

// ── MIR events ───────────────────────────────────────────────────────────────

public class MirService_TimelineWiring_Tests
{
    [Fact]
    public async Task CreateAsync_Enqueues_MIR_CREATED_And_The_Business_Action_Still_Succeeds()
    {
        var (repo, material, inventory) = WiringBuild.NewMirRepo();
        var product = WiringBuild.SeedProduct(inventory, Guid.NewGuid());

        var inbox = new Mock<IWorkflowInboxService>();
        var (jobsMock, captured) = WiringBuild.MockJobs();
        var svc = new MirService(repo, inbox.Object, jobsMock.Object);

        var uuid = await svc.CreateAsync(new CreateMirRequest
        {
            RequestType = "DEPARTMENT",
            Department  = "IT",
            Priority    = "MEDIUM",
            Lines       = [new CreateMirLineRequest { ProductUuid = product.Uuid, RequestedQty = 2m }]
        }, createdBy: 1);

        // Proves the business action completed successfully — the timeline write is a
        // fire-and-forget background job, never inline within the create transaction.
        uuid.Should().NotBeEmpty();

        var evt = WiringBuild.CapturedEvent(captured);
        evt.EventType.Should().Be("MIR_CREATED");
        evt.InterfaceCode.Should().Be("MIR_GENERAL");
        evt.DocumentId.Should().Be(uuid);
    }
}
