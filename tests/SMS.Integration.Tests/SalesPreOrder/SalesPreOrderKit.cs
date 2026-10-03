using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.SapAlignment;

namespace SMS.Integration.Tests.SalesPreOrder;

/// <summary>
/// A32 Phase F (QA) — what the pre-order E2E classes share on top of <see cref="SapKit"/>: date-only strings, the
/// org's rejection reasons, a second organization, users holding exactly a chosen set of permission codes, and a
/// reviewed inquiry built through the API. Everything goes through the endpoints a person would use; SQL is only read
/// (or used where the product has no endpoint: a password, an invitation acceptance, a back-dated validity).
/// </summary>
internal static class PreOrder
{
    public static string Day(DateTime d) => d.ToString("yyyy-MM-dd");
    public static DateTime Today => DateTime.UtcNow.Date;

    /// <summary>The caller organization's active rejection reason with this code.</summary>
    public static async Task<Guid> ReasonAsync(this SapKit k, string code)
    {
        var all = await k.Ok(k.Get("/api/rejection-reasons"), "list rejection reasons");
        return all.Items().First(r => r.S("code") == code).G("uuid");
    }

    /// <summary>PKR as the organization's base currency (the kit's admin must be the super admin for the org edit).</summary>
    public static async Task<Guid> PkrBaseAsync(this SapKit k)
    {
        var pkr = await k.EnsureCurrencyAsync("PKR", "Pakistani Rupee", "Rs");
        await k.SetBaseCurrencyAsync(pkr);
        return pkr;
    }

    /// <summary>
    /// A second organization whose admin has accepted the invitation, and a kit acting as that admin — the
    /// technique of CrossOrganizationReversalE2ETests / SaleInquiryHttpTests.
    /// </summary>
    public static async Task<(Guid OrgId, SapKit Kit, string Email)> SecondOrganizationAsync(this SapKit root, string prefix = "O2")
    {
        var f = root.F;
        var email = $"{prefix.ToLowerInvariant()}-{Guid.NewGuid():N}@a32-qa.test";
        var created = await root.Ok(root.Post("/api/system/organizations", new
        {
            OrgCode = $"X{Guid.NewGuid():N}"[..10].ToUpperInvariant(), OrgName = $"{root.Marker} {prefix} {Guid.NewGuid():N}"[..30],
            Plan = "ENTERPRISE", AdminFirstName = "Other", AdminLastName = "Admin", AdminEmail = email
        }), "create a second organization");
        var orgId = created.G("organizationId");
        orgId.Should().NotBe(f.OrganizationId);

        await f.SetPasswordAsync(email, "Org2@12345!");
        await f.ExecuteAsync("UPDATE auth.UserAccounts SET IsActive = 1 WHERE Email = @e", ("@e", email));
        var client = f.CreateBearerClient(await f.LoginAsync(email, "Org2@12345!"));
        return (orgId, new SapKit(f, prefix, client), email);
    }

    /// <summary>Another organization's base currency, through the platform admin's organization edit (<paramref name="root"/> = the super admin).</summary>
    public static async Task SetOrgBaseCurrencyAsync(this SapKit root, Guid orgId, Guid currencyId)
    {
        var org = await root.Ok(root.Get($"/api/system/organizations/{orgId}"), "read the organization");
        await root.Ok(root.Put($"/api/system/organizations/{orgId}", new
        {
            orgName = org.S("orgName"), contactEmail = org.S("contactEmail"), contactPhone = org.S("contactPhone"),
            address = org.S("address"), country = org.S("country"), timeZone = org.S("timeZone"), baseCurrency = currencyId
        }), "set the organization's base currency");
    }

    /// <summary>
    /// A user of the kit's organization whose role holds exactly <paramref name="codes"/> (none at all when empty):
    /// a custom role through POST /api/roles + PUT …/permissions, a user in it through POST /api/users, a known
    /// password, and a real login.
    /// </summary>
    public static async Task<HttpClient> LoginWithPermissionsAsync(this SapKit k, string tag, params string[] codes)
    {
        var roleCode = $"QA{Guid.NewGuid():N}"[..14].ToUpperInvariant();
        var role = await k.Post("/api/roles", new { Name = $"A32 QA {tag} {roleCode}", RoleCode = roleCode, Description = "A32 QA" });
        role.Status.Should().Be(HttpStatusCode.Created, $"create role for {tag} — {role}");
        var roleId = role.Result.I("roleId");

        var ids = new List<int>();
        foreach (var code in codes)
        {
            var rows = await k.F.QueryAsync("SELECT PermissionID FROM auth.Permissions WHERE Code = @c", ("@c", code));
            rows.Should().ContainSingle($"permission {code} is seeded");
            ids.Add(Convert.ToInt32(rows[0]["PermissionID"]));
        }
        await k.Ok(k.Put($"/api/roles/{roleId}/permissions", new { AllowedPermissionIds = ids }), $"grant {tag} its codes");

        var email = $"{tag.ToLowerInvariant()}-{Guid.NewGuid():N}@a32-qa.test";
        const string password = "A32Qa@12345!";
        await k.CreateUserAsync(roleId, email);
        await k.F.SetPasswordAsync(email, password);
        return k.F.CreateBearerClient(await k.F.LoginAsync(email, password));
    }

    /// <summary>
    /// An inquiry for <paramref name="customer"/> with one CAN_SUPPLY line for <paramref name="item"/>, reviewed to
    /// REVIEW_COMPLETE. Returns the inquiry uuid.
    /// </summary>
    public static async Task<Guid> ReviewedInquiryAsync(this SapKit k, Partner customer, Product item, decimal qty)
    {
        var inquiry = (await k.Ok(k.Post("/api/sale-inquiries", new
        {
            PartnerId = customer.Uuid, CustomerReference = k.Next("RFQ"),
            Lines = new object[] { new { VariantUuid = item.VariantUuid, ProductDescription = item.Name, RequestedQuantity = qty } }
        }), "create inquiry")).GetGuid();
        var line = (await k.Ok(k.Get($"/api/sale-inquiries/{inquiry}"), "read inquiry")).A("lines").Single().G("uuid");

        await k.Ok(k.Patch($"/api/sale-inquiries/{inquiry}/status", new { Status = "UNDER_REVIEW" }), "start review");
        await k.Ok(k.Put($"/api/sale-inquiries/{inquiry}/lines/{line}", new
        {
            VariantUuid = item.VariantUuid, ProductDescription = item.Name, RequestedQuantity = qty,
            LineStatus = "CAN_SUPPLY", EstimatedDeliveryDate = Day(Today.AddDays(7))
        }), "evaluate the line");
        await k.Ok(k.Patch($"/api/sale-inquiries/{inquiry}/status", new { Status = "REVIEW_COMPLETE" }), "complete review");
        return inquiry;
    }

    /// <summary>A DRAFT quotation (no inquiry) with one NORMAL line at the waterfall price.</summary>
    public static async Task<Guid> DraftQuotationAsync(this SapKit k, Partner customer, Guid currency, Product item, decimal qty, int validDays = 14) =>
        (await k.Ok(k.Post("/api/sale-quotations", new
        {
            PartnerId = customer.Uuid, CurrencyId = currency, ValidFrom = Day(Today), ValidTo = Day(Today.AddDays(validDays)),
            Lines = new object[] { new { LineType = "NORMAL", VariantUuid = item.VariantUuid, Quantity = qty } }
        }), "create quotation")).GetGuid();

    /// <summary>A quotation sent, its every offered line ACCEPTED by the customer, and accepted.</summary>
    public static async Task<Guid> AcceptedQuotationAsync(this SapKit k, Partner customer, Guid currency, Product item, decimal qty)
    {
        var q = await k.DraftQuotationAsync(customer, currency, item, qty);
        await k.Ok(k.Post($"/api/sale-quotations/{q}/send"), "send quotation");
        foreach (var line in (await k.Ok(k.Get($"/api/sale-quotations/{q}"), "read quotation")).A("lines"))
            await k.Ok(k.Patch($"/api/sale-quotations/{q}/lines/{line.G("uuid")}/customer-response", new { Response = "ACCEPTED" }), "accept line");
        await k.Ok(k.Post($"/api/sale-quotations/{q}/accept"), "accept quotation");
        return q;
    }

    public static JsonElement Line(this JsonElement doc, Guid lineUuid) => doc.A("lines").Single(l => l.G("uuid") == lineUuid);
}
