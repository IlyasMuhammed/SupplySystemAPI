using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Controllers;
using SMS.Modules.Logistics.Couriers;
using SMS.Modules.Logistics.Couriers.Manual;
using SMS.Modules.Logistics.Couriers.Simulator;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Logistics.Tests.Couriers;

// T-35 — the credential vault. Encrypted from the first write, never readable back.
public class CarrierCredentialVaultTests
{
    private const int User  = 42;
    private const int Other = 77;

    private sealed record Harness(
        LogisticsDbContext Db,
        string DbName,
        CarrierAccountRepository Accounts,
        CarrierCredentialVault Vault,
        CarrierAccountResolver Resolver);

    private static Harness NewHarness(string encryptionKey = TestEncryption.DefaultKey)
    {
        var (db, _, dbName) = LogisticsTestDb.New();
        var registry = new CourierProviderRegistry(
            [new ManualCourierProvider(), new SimulatorCourierProvider()]);
        var vault = new CarrierCredentialVault(db, TestEncryption.New(encryptionKey));

        return new Harness(db, dbName, new CarrierAccountRepository(db, registry), vault,
                           new CarrierAccountResolver(db, registry, vault));
    }

    private static async Task<(Guid carrier, Guid account)> NewApiAccount(Harness h, string name = "Domestic")
    {
        var carrier = new Carrier
        {
            UUID = Guid.NewGuid(), Name = "Simcourier", Code = $"C{Guid.NewGuid():N}"[..6],
            IntegrationMode = "API", ProviderKey = "SIMULATOR", IsActive = true
        };
        h.Db.Carriers.Add(carrier);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var account = await h.Accounts.CreateAsync(
            new CreateCarrierAccountRequest { CarrierUuid = carrier.UUID, AccountName = name }, User);

        return (carrier.UUID, account);
    }

    private static async Task<int> AccountId(Harness h, Guid account) =>
        await h.Db.CarrierAccounts.Where(a => a.UUID == account).Select(a => a.Id).SingleAsync();

    private static Task<CarrierCredential> RawRow(Harness h, Guid account, string key) =>
        h.Db.CarrierCredentials.AsNoTracking()
            .SingleAsync(c => c.CarrierAccount.UUID == account && c.CredentialKey == key);

    // ── Encrypted at rest ─────────────────────────────────────────────────────

    [Fact]
    public async Task The_stored_value_is_ciphertext_never_the_secret()
    {
        var h = NewHarness();
        var (_, account) = await NewApiAccount(h);

        await h.Vault.SetAsync(account, "ApiKey", "sk_live_abc123", null, null, User);

        var row = await RawRow(h, account, "ApiKey");
        row.EncryptedValue.Should().NotBe("sk_live_abc123");
        row.EncryptedValue.Should().NotContain("sk_live_abc123");
        // The authenticated format, not the legacy unauthenticated one.
        row.EncryptedValue.Should().StartWith("v2:");
    }

    [Fact]
    public async Task The_same_secret_set_twice_is_not_stored_the_same_way_twice()
    {
        // A fresh nonce per write. Identical ciphertext would reveal which accounts share a secret.
        var h = NewHarness();
        var (_, one) = await NewApiAccount(h, "One");
        var (_, two) = await NewApiAccount(h, "Two");

        await h.Vault.SetAsync(one, "ApiKey", "shared-secret", null, null, User);
        await h.Vault.SetAsync(two, "ApiKey", "shared-secret", null, null, User);

        (await RawRow(h, one, "ApiKey")).EncryptedValue
            .Should().NotBe((await RawRow(h, two, "ApiKey")).EncryptedValue);
    }

    [Fact]
    public async Task The_booking_path_gets_the_secrets_back_decrypted()
    {
        var h = NewHarness();
        var (_, account) = await NewApiAccount(h);

        await h.Vault.SetAsync(account, "ApiKey", "sk_live_abc123", null, null, User);
        await h.Vault.SetAsync(account, "ClientSecret", "ü-✓-🔑", null, null, User);

        var credentials = await h.Vault.GetForAccountAsync(await AccountId(h, account));

        credentials.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["ApiKey"] = "sk_live_abc123", ["ClientSecret"] = "ü-✓-🔑"
        });
        // Adapters ask by name; a case slip in configuration must not make a key vanish.
        credentials["apikey"].Should().Be("sk_live_abc123");
    }

    // ── Never readable back ───────────────────────────────────────────────────

    [Fact]
    public async Task Listing_shows_what_is_configured_without_any_value()
    {
        var h = NewHarness();
        var (_, account) = await NewApiAccount(h);
        var expires = DateTime.UtcNow.AddDays(30);

        await h.Vault.SetAsync(account, "ApiKey", "sk_live_abc123", "Rotate before renewal", expires, User);

        var listed = (await h.Vault.ListAsync(account)).Single();

        listed.CredentialKey.Should().Be("ApiKey");
        listed.Description.Should().Be("Rotate before renewal");
        listed.ExpiresAt.Should().Be(expires);
        listed.IsExpired.Should().BeFalse();
        listed.SetBy.Should().Be(User);
    }

    [Theory]
    [InlineData(typeof(CarrierCredentialModel))]
    [InlineData(typeof(CarrierCredentialSummary))]
    public void Nothing_the_outside_world_receives_can_carry_a_secret(Type shape)
    {
        // Pinned structurally: a "Value" or masked preview added later fails here, not in a pen test.
        shape.GetProperties().Select(p => p.Name)
            .Should().NotContain(n => n.Contains("Value", StringComparison.OrdinalIgnoreCase)
                                   || n.Contains("Secret", StringComparison.OrdinalIgnoreCase)
                                   || n.Contains("Mask", StringComparison.OrdinalIgnoreCase)
                                   || n.Contains("Encrypted", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_public_credential_service_offers_no_way_to_read_a_secret()
    {
        // The vault can decrypt; only the booking path reaches the vault.
        typeof(ICarrierCredentialService).GetMethods()
            .Select(m => m.Name)
            .Should().BeEquivalentTo(["ListAsync", "SetAsync", "RemoveAsync"]);
    }

    [Fact]
    public void Every_credential_endpoint_needs_the_narrower_credential_permission()
    {
        var actions = typeof(CarrierAccountsController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes<HttpMethodAttribute>()
                         .Any(a => a.Template?.Contains("credentials") == true))
            .ToList();

        actions.Should().HaveCount(3);
        // RequirePermissionAttribute stores its code as the policy string "Permission:<CODE>".
        actions.Should().OnlyContain(m =>
            m.GetCustomAttribute<RequirePermissionAttribute>()!.Policy
            == $"Permission:{PermissionCodes.CARRIER_CREDENTIAL_MANAGE}");
    }

    [Fact]
    public void The_credential_permission_is_granted_by_default_only_through_the_full_catalogue()
    {
        // Holding the keys to a carrier account is a different trust level from choosing a contract.
        PermissionCodes.All.Should().Contain(PermissionCodes.CARRIER_CREDENTIAL_MANAGE);
        PermissionCodes.CARRIER_CREDENTIAL_MANAGE.Should().NotBe(PermissionCodes.CARRIER_MANAGE);
    }

    // ── Setting and replacing ─────────────────────────────────────────────────

    [Fact]
    public async Task Setting_a_key_again_replaces_it_rather_than_adding_a_second()
    {
        // Two rows for one key would make which one authenticates depend on row order.
        var h = NewHarness();
        var (_, account) = await NewApiAccount(h);

        var first  = await h.Vault.SetAsync(account, "ApiKey", "old", null, null, User);
        var second = await h.Vault.SetAsync(account, "  ApiKey ", "new", null, null, Other);

        second.Should().Be(first);
        (await h.Db.CarrierCredentials.CountAsync()).Should().Be(1);
        (await h.Vault.GetForAccountAsync(await AccountId(h, account)))["ApiKey"].Should().Be("new");

        var listed = (await h.Vault.ListAsync(account)).Single();
        listed.SetBy.Should().Be(Other, "the list says who last set it, not who first did");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_credential_needs_a_key(string? key)
    {
        var h = NewHarness();
        var (_, account) = await NewApiAccount(h);

        var act = () => h.Vault.SetAsync(account, key!, "value", null, null, User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*needs a key*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_credential_cannot_be_set_to_nothing(string? value)
    {
        // Fails here rather than at the carrier, a long way from the cause.
        var h = NewHarness();
        var (_, account) = await NewApiAccount(h);

        var act = () => h.Vault.SetAsync(account, "ApiKey", value!, null, null, User);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*Remove it instead*");
        (await h.Db.CarrierCredentials.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_credential_for_an_account_that_does_not_exist_is_refused()
    {
        var h = NewHarness();

        var act = () => h.Vault.SetAsync(Guid.NewGuid(), "ApiKey", "value", null, null, User);

        await act.Should().ThrowAsync<NotFoundException>();
        (await h.Vault.ListAsync(Guid.NewGuid())).Should().BeEmpty();
        (await h.Vault.RemoveAsync(Guid.NewGuid(), "ApiKey", User)).Should().BeFalse();
    }

    [Fact]
    public async Task An_expired_credential_says_so()
    {
        // A credential that silently lapses takes every booking with it.
        var h = NewHarness();
        var (_, account) = await NewApiAccount(h);

        await h.Vault.SetAsync(account, "Lapsed", "v", null, DateTime.UtcNow.AddMinutes(-1), User);
        await h.Vault.SetAsync(account, "Fresh",  "v", null, DateTime.UtcNow.AddDays(1), User);
        await h.Vault.SetAsync(account, "Never",  "v", null, null, User);

        var listed = (await h.Vault.ListAsync(account)).ToDictionary(c => c.CredentialKey, c => c.IsExpired);

        listed.Should().BeEquivalentTo(new Dictionary<string, bool>
        {
            ["Lapsed"] = true, ["Fresh"] = false, ["Never"] = false
        });
    }

    // ── Removing ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_removed_credential_is_gone_and_so_is_its_ciphertext()
    {
        // Keeping the row keeps the audit; keeping the secret would leave a deleted secret behind.
        var h = NewHarness();
        var (_, account) = await NewApiAccount(h);
        await h.Vault.SetAsync(account, "ApiKey", "sk_live_abc123", null, null, User);

        (await h.Vault.RemoveAsync(account, " ApiKey ", Other)).Should().BeTrue();

        var row = await RawRow(h, account, "ApiKey");
        row.IsDelete.Should().BeTrue();
        row.EncryptedValue.Should().BeEmpty();
        row.ModifiedBy.Should().Be(Other);

        (await h.Vault.ListAsync(account)).Should().BeEmpty();
        (await h.Vault.GetForAccountAsync(await AccountId(h, account))).Should().BeEmpty();
        (await h.Vault.RemoveAsync(account, "ApiKey", Other)).Should().BeFalse("it is already gone");
    }

    [Fact]
    public async Task A_removed_key_can_be_set_again()
    {
        // The unique index is on (account, key) regardless of IsDelete — a second row would collide
        // in SQL Server even though the in-memory provider would let it through.
        var h = NewHarness();
        var (_, account) = await NewApiAccount(h);

        var first = await h.Vault.SetAsync(account, "ApiKey", "old", null, null, User);
        await h.Vault.RemoveAsync(account, "ApiKey", User);
        var again = await h.Vault.SetAsync(account, "ApiKey", "new", null, null, User);

        again.Should().Be(first);
        (await h.Db.CarrierCredentials.IgnoreQueryFilters().CountAsync()).Should().Be(1);
        (await h.Vault.GetForAccountAsync(await AccountId(h, account)))["ApiKey"].Should().Be("new");
    }

    // ── Unreadable values fail loudly ─────────────────────────────────────────

    [Fact]
    public async Task A_tampered_value_is_refused_rather_than_decrypted_into_rubbish()
    {
        var h = NewHarness();
        var (_, account) = await NewApiAccount(h);
        await h.Vault.SetAsync(account, "ApiKey", "sk_live_abc123", null, null, User);

        var row = await h.Db.CarrierCredentials.SingleAsync();
        var bytes = Convert.FromBase64String(row.EncryptedValue["v2:".Length..]);
        bytes[^1] ^= 0x01;
        row.EncryptedValue = "v2:" + Convert.ToBase64String(bytes);
        await h.Db.SaveChangesAsync();

        var act = () => h.Vault.GetForAccountAsync(row.CarrierAccountId);

        var thrown = await act.Should().ThrowAsync<ConflictException>();
        thrown.WithMessage("*'ApiKey' cannot be decrypted*Set it again*");
        thrown.Which.Message.Should().NotContain("sk_live_abc123");
    }

    [Fact]
    public async Task A_changed_encryption_key_is_reported_not_silently_misread()
    {
        var h = NewHarness();
        var (_, account) = await NewApiAccount(h);
        await h.Vault.SetAsync(account, "ApiKey", "sk_live_abc123", null, null, User);
        var accountId = await AccountId(h, account);

        var rekeyed = new CarrierCredentialVault(
            LogisticsTestDb.OpenAs(h.DbName, (await h.Db.CarrierAccounts.SingleAsync()).OrganizationId),
            TestEncryption.New("a-different-key-entirely-0000000"));

        var act = () => rekeyed.GetForAccountAsync(accountId);

        (await act.Should().ThrowAsync<ConflictException>())
            .WithMessage("*encryption key has changed*");
    }

    // ── Tenancy ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Another_organization_cannot_see_set_or_use_these_credentials()
    {
        var h = NewHarness();
        var (_, account) = await NewApiAccount(h);
        await h.Vault.SetAsync(account, "ApiKey", "sk_live_abc123", null, null, User);
        var accountId = await AccountId(h, account);

        var otherDb = LogisticsTestDb.OpenAs(h.DbName, Guid.NewGuid());
        var other   = new CarrierCredentialVault(otherDb, TestEncryption.New());

        (await other.ListAsync(account)).Should().BeEmpty();
        (await other.GetForAccountAsync(accountId)).Should().BeEmpty();
        (await other.RemoveAsync(account, "ApiKey", Other)).Should().BeFalse();

        var overwrite = () => other.SetAsync(account, "ApiKey", "attacker", null, null, Other);
        await overwrite.Should().ThrowAsync<NotFoundException>();

        (await h.Vault.GetForAccountAsync(accountId))["ApiKey"].Should().Be("sk_live_abc123");
    }

    // ── Resolution ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_booking_resolves_with_the_credentials_of_the_account_it_goes_out_on()
    {
        var h = NewHarness();
        var (carrier, domestic) = await NewApiAccount(h, "Domestic");
        var international = await h.Accounts.CreateAsync(
            new CreateCarrierAccountRequest { CarrierUuid = carrier, AccountName = "International" }, User);

        await h.Vault.SetAsync(domestic,      "ApiKey", "domestic-key",      null, null, User);
        await h.Vault.SetAsync(international, "ApiKey", "international-key", null, null, User);

        (await h.Resolver.ResolveAsync(carrier)).Credentials["ApiKey"].Should().Be("domestic-key");
        (await h.Resolver.ResolveAsync(carrier, international)).Credentials["ApiKey"]
            .Should().Be("international-key", "a named account must never borrow the default's secrets");
    }

    [Fact]
    public async Task A_manual_carrier_resolves_with_no_credentials()
    {
        var h = NewHarness();
        var carrier = new Carrier
        {
            UUID = Guid.NewGuid(), Name = "Local van", Code = "VAN001",
            IntegrationMode = null, ProviderKey = null, IsActive = true
        };
        h.Db.Carriers.Add(carrier);
        await h.Db.SaveChangesAsync();

        (await h.Resolver.ResolveAsync(carrier.UUID)).Credentials.Should().BeEmpty();
    }
}
