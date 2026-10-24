using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.FinancialPosting;
using SMS.Integration.Tests.GeneralLedger;
using SMS.Integration.Tests.ModuleRegistry;
using SMS.Integration.Tests.SapAlignment;
using Xunit;
using static SMS.Integration.Tests.JournalEngine.Je;

namespace SMS.Integration.Tests.JournalEngine;

/// <summary>
/// A47 (QA) — D-15 permissions and organization isolation of every journal-engine route, tasks 432–439. Each user holds exactly
/// one role with the listed codes; "refused" = 403, "allowed" = anything but 401/403. Another organization's uuid is 404 (contract §0).
/// <para>Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~JournalEngine.JeSecurityE2ETests</c>.</para>
/// </summary>
public sealed class JeSecurityE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _root;

    public JeSecurityE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _f.ShouldBeOnLocalDb();
        _root = new SapKit(factory, "A47");
    }

    /// <summary>An org with one posted and one draft manual entry, and the uuids the matrix needs.</summary>
    private sealed record World(JeOrg O, Guid Misc, Guid Posted, Guid Draft, Guid Year, Guid Period, Guid Cash, Guid Rev, Guid ArLine1, Guid ArLine2);

    private async Task<World> WorldAsync(string prefix)
    {
        var o = await _root.NewJeOrgAsync(prefix);
        var misc = await o.K.TypeAsync("MISC");
        Guid cash = o.Acc("DEFAULT_CASH"), rev = o.Acc("DEFAULT_REVENUE"), ar = o.Acc("DEFAULT_RECEIVABLE");
        var posted = await o.PostedAsync(misc, cash, rev, 100m);
        var draft = await o.DraftAsync(misc, cash, rev, 50m, new DateTime(Y, 2, 10));
        var inv = await o.PostedAsync(misc, ar, rev, 300m);
        var pay = await o.PostedAsync(misc, cash, ar, 300m);
        var year = (await o.K.YearAsync()).G("uuid");
        var period = (await o.K.PeriodAsync(new DateTime(Y, 7, 1))).G("uuid");
        return new World(o, misc, posted.G("uuid"), draft.G("uuid"), year, period, cash, rev,
            inv.LinesOn(ar).Single().G("lineUuid"), pay.LinesOn(ar).Single().G("lineUuid"));
    }

    private static object NewEntry(World w) => Body(w.Misc, Today, [new JeLine(w.Cash, 1m, 0m), new JeLine(w.Rev, 0m, 1m)]);

    private static object EditBody(World w) => new
    {
        entryDate = Iso(new DateTime(Y, 2, 10)),
        lines = new[] { new { accountUuid = w.Cash, debit = 60m, credit = 0m }, new { accountUuid = w.Rev, debit = 0m, credit = 60m } }
    };

    /// <summary>Every guarded action of the engine, by name, as a call with a given client (read-only where possible).</summary>
    private static Dictionary<string, Func<HttpClient, Task<Api>>> Actions(World w)
    {
        var k = w.O.K;
        var from = new DateTime(Y, 1, 1);
        var to = new DateTime(Y, 12, 31);
        return new()
        {
            ["entries.list"]     = c => k.ListEntries("", c),
            ["entries.get"]      = c => k.GetEntry(w.Posted, c),
            ["types.list"]       = c => k.Get(Types, c),
            ["entries.create"]   = c => k.CreateEntry(NewEntry(w), c),
            ["entries.edit"]     = c => k.UpdateEntry(w.Draft, EditBody(w), c),
            ["entries.post"]     = c => k.PostEntry(Guid.NewGuid(), c),
            ["entries.delete"]   = c => k.DeleteEntry(Guid.NewGuid(), c),
            ["entries.reverse"]  = c => k.ReverseEntry(Guid.NewGuid(), "sec", null, c),
            ["types.create"]     = c => k.Post(Types, new { code = $"S{Guid.NewGuid():N}"[..8].ToUpperInvariant(), name = "sec", type = "Miscellaneous" }, c),
            ["recon.open"]       = c => k.OpenItems(w.Cash, null, c),
            ["recon.suggest"]    = c => k.Suggest(w.Cash, [w.ArLine1], null, c),
            ["recon.reconcile"]  = c => k.Reconcile([Guid.NewGuid(), Guid.NewGuid()], c),
            ["recon.unreconcile"] = c => k.Unreconcile(Guid.NewGuid(), c),
            ["tb.get"]           = c => k.TrialBalance(from, to, c: c),
            ["tb.ledger"]        = c => k.Ledger(w.Cash, from, to, c: c),
            ["tb.balance"]       = c => k.Balance(w.Cash, to, c),
            ["close.status"]     = c => k.CloseStatus(w.Year, c),
            ["close.preview"]    = c => k.YearPreview(w.Year, c),
            ["close.lock"]       = c => k.LockPeriod(Guid.NewGuid(), c),
            ["close.reopen"]     = c => k.ReopenPeriod(Guid.NewGuid(), c),
            ["close.final"]      = c => k.FinalClosePeriod(Guid.NewGuid(), c),
            ["close.execute"]    = c => k.YearExecute(Guid.NewGuid(), null, c),
        };
    }

    /// <summary>The actions each D-15 code (alone) allows, per API-CONTRACT.md (JE-READ, §1–§6).</summary>
    private static readonly Dictionary<string, string[]> Allowed = new()
    {
        [View]       = ["entries.list", "entries.get", "types.list"],
        [Create]     = ["entries.list", "entries.get", "types.list", "entries.create"],
        [Edit]       = ["entries.list", "entries.get", "types.list", "entries.edit"],
        [Del]        = ["entries.list", "entries.get", "types.list", "entries.delete"],
        [Post]       = ["entries.list", "entries.get", "types.list", "entries.post"],
        [Reverse]    = ["entries.list", "entries.get", "types.list", "entries.reverse"],
        [ReconView]  = ["recon.open", "recon.suggest"],
        [ReconExec]  = ["recon.open", "recon.suggest", "recon.reconcile", "recon.unreconcile"],
        [Reports]    = ["tb.get", "tb.ledger", "tb.balance"],
        [CloseView]  = ["close.status", "close.preview"],
        [CloseExec]  = ["close.status", "close.preview", "close.lock", "close.reopen"],
        [CloseAdmin] = ["close.status", "close.preview", "close.lock", "close.reopen", "close.final", "close.execute"],
        [Setup]      = ["entries.list", "entries.get", "types.list", "types.create"],
    };

    [Fact]
    public async Task Each_D15_code_allows_exactly_its_actions()
    {
        var w = await WorldAsync("SEC");
        var actions = Actions(w);
        var failures = new List<string>();
        foreach (var (code, allowed) in Allowed)
        {
            var tag = code.ToLowerInvariant().Replace("_", "");
            var c = await w.O.UserWithAsync(tag.Length > 12 ? tag[..12] : tag, code);
            foreach (var (name, call) in actions)
            {
                var api = await call(c);
                var forbidden = api.Status is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized;
                if (allowed.Contains(name) && forbidden) failures.Add($"{code} should be allowed {name} — {api}");
                if (!allowed.Contains(name) && !forbidden) failures.Add($"{code} should be refused {name} — {api}");
            }
        }
        // nobody: an unrelated code refuses everything
        var nobody = await w.O.UserWithAsync("nogl", "INVOICE_VIEW");
        foreach (var (name, call) in actions)
        {
            var api = await call(nobody);
            if (api.Status is not (HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)) failures.Add($"no GL code should be refused {name} — {api}");
        }
        failures.Should().BeEmpty(string.Join("\n", failures));
    }

    [Fact]
    public async Task GL_VIEW_reads_but_cannot_create_post_or_reverse_432()
    {
        var w = await WorldAsync("S432");
        var k = w.O.K;
        var viewer = await w.O.UserWithAsync("viewer", View);
        (await k.ListEntries("", viewer)).Status.Should().Be(HttpStatusCode.OK);
        var detail = await k.GetEntry(w.Posted, viewer);
        detail.Status.Should().Be(HttpStatusCode.OK);
        (detail.Result.B("canPost"), detail.Result.B("canReverse"), detail.Result.B("canEdit")).Should().Be((false, false, false), "contract §2: by the caller's permissions");
        (await k.CreateEntry(NewEntry(w), viewer)).ShouldBeForbidden("432 create");
        (await k.PostEntry(w.Draft, viewer)).ShouldBeForbidden("432 post");
        (await k.ReverseEntry(w.Posted, "x", null, viewer)).ShouldBeForbidden("432 reverse");
        (await k.UpdateEntry(w.Draft, EditBody(w), viewer)).ShouldBeForbidden("432 edit");
        (await k.DeleteEntry(w.Draft, viewer)).ShouldBeForbidden("432 delete");
        (await k.EntryAsync(w.Draft)).S("status").Should().Be("Draft", "nothing changed");
    }

    [Fact]
    public async Task GL_JOURNAL_CREATE_cannot_post_or_reverse_433_and_POST_cannot_create_or_reverse_434()
    {
        var w = await WorldAsync("S433");
        var k = w.O.K;
        var clerk = await w.O.UserWithAsync("clerk", Create);
        var created = await k.CreateEntry(NewEntry(w), clerk);
        created.Status.Should().Be(HttpStatusCode.OK, created.ToString());
        created.Result.S("status").Should().Be("Draft");
        (await k.CreateEntry(Body(w.Misc, Today, [new JeLine(w.Cash, 1m, 0m), new JeLine(w.Rev, 0m, 1m)], post: true), clerk))
            .ShouldRefuse(HttpStatusCode.Forbidden, null, "You do not have permission to post journal entries.", "433 postImmediately");
        (await k.CreateEntry(Body(await k.TypeAsync("CLOSE"), Today, [new JeLine(w.Cash, 1m, 0m), new JeLine(w.Rev, 0m, 1m)]), clerk))
            .ShouldBeForbidden("433 an auto-post type needs GL_JOURNAL_POST");
        (await k.PostEntry(created.Result.G("uuid"), clerk)).ShouldBeForbidden("433 post");
        (await k.ReverseEntry(w.Posted, "x", null, clerk)).ShouldBeForbidden("433 reverse");

        var poster = await w.O.UserWithAsync("poster", Post);
        var posted = await k.PostEntry(created.Result.G("uuid"), poster);
        posted.Status.Should().Be(HttpStatusCode.OK, posted.ToString());
        (await k.CreateEntry(NewEntry(w), poster)).ShouldBeForbidden("434 create");
        (await k.ReverseEntry(w.Posted, "x", null, poster)).ShouldBeForbidden("434 reverse");

        var reverser = await w.O.UserWithAsync("reverser", Reverse);
        (await k.ReverseEntry(w.Posted, "allowed", null, reverser)).Status.Should().Be(HttpStatusCode.OK);
        (await k.PostEntry(w.Draft, reverser)).ShouldBeForbidden("GL_JOURNAL_REVERSE cannot post");

        var editor = await w.O.UserWithAsync("editor", Edit);
        (await k.UpdateEntry(w.Draft, EditBody(w), editor)).Status.Should().Be(HttpStatusCode.OK);
        (await k.DeleteEntry(w.Draft, editor)).ShouldBeForbidden("GL_JOURNAL_EDIT cannot delete");
        var deleter = await w.O.UserWithAsync("deleter", Del);
        (await k.DeleteEntry(w.Draft, deleter)).Status.Should().Be(HttpStatusCode.OK);

        // audit names (contract §2 createdByName / postedByName) — QA-1: always null, the access token has no name claim
        created.Result.S("createdByName").Should().NotBeNullOrEmpty("QA-1: who created the entry");
        posted.Result.S("postedByName").Should().NotBeNullOrEmpty("QA-1: who posted it");
    }

    [Fact]
    public async Task Period_close_permissions_435_436()
    {
        var w = await WorldAsync("S435");
        var k = w.O.K;
        var exec = await w.O.UserWithAsync("closeexec", CloseExec);
        var admin = await w.O.UserWithAsync("closeadmin", CloseAdmin);
        var view = await w.O.UserWithAsync("closeview", CloseView);

        (await k.LockPeriod(w.Period, view)).ShouldBeForbidden("VIEW cannot lock");
        (await k.Ok(k.Send(HttpMethod.Post, $"{Close}/{w.Period}/close", new { targetStatus = "Locked" }, exec), "435 EXECUTE locks")).S("newStatus").Should().Be("Locked");
        (await k.FinalClosePeriod(w.Period, exec)).ShouldBeForbidden("435 EXECUTE cannot final-close");
        (await k.YearExecute(w.Year, null, exec)).ShouldBeForbidden("435/436 EXECUTE cannot year-end close");
        (await k.Send(HttpMethod.Post, $"{Close}/{w.Period}/reopen", new { }, exec)).Status.Should().Be(HttpStatusCode.OK, "EXECUTE reopens");
        (await k.CloseStatus(w.Year, view)).Status.Should().Be(HttpStatusCode.OK);
        (await k.YearPreview(w.Year, view)).Status.Should().Be(HttpStatusCode.OK);
        (await k.YearExecute(w.Year, null, view)).ShouldBeForbidden("436 VIEW cannot execute");

        await k.Ok(k.LockPeriod(w.Period), "lock (admin of the org)");
        (await k.FinalClosePeriod(w.Period, admin)).Status.Should().Be(HttpStatusCode.OK, "ADMIN final-closes");
        // 436 — ADMIN passes the gate of execute (the year is not ready: 409, not 403)
        (await k.YearExecute(w.Year, null, admin)).ShouldBeAllowed("436 ADMIN may execute");
    }

    [Fact]
    public async Task GL_SETUP_MANAGE_required_for_journal_type_crud_437()
    {
        var w = await WorldAsync("S437");
        var k = w.O.K;
        var setup = await w.O.UserWithAsync("setup", Setup);
        var viewer = await w.O.UserWithAsync("tviewer", View);
        var clerk = await w.O.UserWithAsync("tclerk", Create);

        var created = await k.Post(Types, new { code = "SEC1", name = "Security type", type = "Miscellaneous" }, setup);
        created.Status.Should().Be(HttpStatusCode.OK, created.ToString());
        var uuid = created.Result.G("uuid");
        foreach (var c in new[] { viewer, clerk })
        {
            (await k.Post(Types, new { code = "SEC2", name = "x", type = "Miscellaneous" }, c)).ShouldBeForbidden("437 create");
            (await k.Put($"{Types}/{uuid}", new { code = "SEC1", name = "renamed", type = "Miscellaneous", sequencePrefix = "SEC1", requiresPartner = false, autoPost = false }, c))
                .ShouldBeForbidden("437 update");
            (await k.Patch($"{Types}/{uuid}/deactivate", null, c)).ShouldBeForbidden("437 deactivate");
            (await k.Delete($"{Types}/{uuid}", c)).ShouldBeForbidden("437 delete");
            (await k.Get($"{Types}/{uuid}", c)).Status.Should().Be(HttpStatusCode.OK, "JE-READ reads types");
        }
        (await k.Put($"{Types}/{uuid}", new { code = "SEC1", name = "renamed", type = "Miscellaneous", sequencePrefix = "SEC1", requiresPartner = false, autoPost = false }, setup))
            .Status.Should().Be(HttpStatusCode.OK);
        (await k.Patch($"{Types}/{uuid}/deactivate", null, setup)).Status.Should().Be(HttpStatusCode.OK);
        (await k.Delete($"{Types}/{uuid}", setup)).Status.Should().Be(HttpStatusCode.OK, "custom and unused");

        // system types: delete refused, code fixed
        var sale = (await k.TypesAsync()).Single(t => t.S("code") == "SALE");
        (await k.Delete($"{Types}/{sale.G("uuid")}", setup)).ShouldBeRefusedState(null, "System journal types cannot be deleted, only deactivated.", "task 249");
        (await k.Put($"{Types}/{sale.G("uuid")}", new { code = "SALES", name = "x", type = "Sale", sequencePrefix = "SALE", requiresPartner = true, autoPost = false }, setup))
            .ShouldBeInvalid(null, "The code and type of a system journal type cannot be changed.", "system code fixed");
        (await k.Post(Types, new { code = "SALE", name = "dup", type = "Sale" }, setup)).ShouldBeRefusedState(null, "Journal type code SALE already exists.", "duplicate code");
        (await k.Post(Types, new { code = "OPEN2", name = "second opening", type = "Opening" }, setup))
            .ShouldBeRefusedState(null, "An organization can have only one Opening journal type.", "one Opening type");
    }

    [Fact]
    public async Task Another_organization_sees_nothing_438_439()
    {
        var a = await WorldAsync("SIA");
        var b = await _root.NewJeOrgAsync("SIB");
        var kb = b.K;
        var bMisc = await kb.TypeAsync("MISC");

        // by uuid → 404 (never 403)
        (await kb.GetEntry(a.Posted)).Status.Should().Be(HttpStatusCode.NotFound, "439 read");
        (await kb.PostEntry(a.Draft)).Status.Should().Be(HttpStatusCode.NotFound, "439 post");
        (await kb.UpdateEntry(a.Draft, EditBody(a))).Status.Should().Be(HttpStatusCode.NotFound, "439 edit");
        (await kb.DeleteEntry(a.Draft)).Status.Should().Be(HttpStatusCode.NotFound, "439 delete");
        (await kb.ReverseEntry(a.Posted, "cross")).Status.Should().Be(HttpStatusCode.NotFound, "439 reverse");
        (await kb.Get($"{Types}/{a.Misc}")).Status.Should().Be(HttpStatusCode.NotFound, "438 type");
        (await kb.Patch($"{Types}/{a.Misc}/deactivate", null)).Status.Should().Be(HttpStatusCode.NotFound, "438 type write");
        (await kb.CloseStatus(a.Year)).Status.Should().Be(HttpStatusCode.NotFound, "438 period close status");
        (await kb.LockPeriod(a.Period)).Status.Should().Be(HttpStatusCode.NotFound, "438 lock another org's period");
        (await kb.YearPreview(a.Year)).Status.Should().Be(HttpStatusCode.NotFound);
        (await kb.YearExecute(a.Year)).Status.Should().Be(HttpStatusCode.NotFound);
        (await kb.Ledger(a.Cash, new DateTime(Y, 1, 1), new DateTime(Y, 12, 31))).Status.Should().Be(HttpStatusCode.NotFound, "438 ledger");
        (await kb.Balance(a.Cash, Today)).Status.Should().Be(HttpStatusCode.NotFound);
        (await kb.Reconcile([a.ArLine1, a.ArLine2])).Status.Should().Be(HttpStatusCode.NotFound, "438 reconcile");
        (await kb.OpenItems(a.Cash)).Status.Should().BeOneOf(new[] { HttpStatusCode.NotFound, HttpStatusCode.OK }, "an unknown account");
        if ((await kb.OpenItems(a.Cash)) is { Status: HttpStatusCode.OK } oi) Rows(oi.Result).Should().BeEmpty("438 open items");

        // A's ids in B's writes
        (await kb.CreateEntry(Body(a.Misc, Today, [new JeLine(b.Acc("DEFAULT_CASH"), 1m, 0m), new JeLine(b.Acc("DEFAULT_REVENUE"), 0m, 1m)])))
            .ShouldBeInvalid("JE-013", "Journal type not found", "A's journal type");
        (await kb.CreateEntry(Body(bMisc, Today, [new JeLine(a.Cash, 1m, 0m), new JeLine(b.Acc("DEFAULT_REVENUE"), 0m, 1m)])))
            .ShouldBeInvalid("JE-009", "Account not found", "A's account");

        // lists / reports never show A's rows
        var bEntry = await b.PostedAsync(bMisc, b.Acc("DEFAULT_CASH"), b.Acc("DEFAULT_REVENUE"), 9m);
        var list = await kb.ListEntriesAsync();
        list.Select(e => e.G("uuid")).Should().Equal([bEntry.G("uuid")], "438 list");
        (await kb.ListEntriesAsync($"accountUuid={a.Cash}")).Should().BeEmpty();
        var tb = await kb.TrialBalanceAsync(new DateTime(Y, 1, 1), new DateTime(Y, 12, 31));
        tb.D("totalDebit").Should().Be(9m, "438 trial balance holds only B's lines");
        (await kb.TypesAsync()).Select(t => t.G("uuid")).Should().NotContain(a.Misc);

        // the same uuid-free numbers in both orgs: sequences are per organization
        bEntry.S("entryNumber").Should().Be($"JV/{Y}/0001");
        (await a.O.K.EntryAsync(a.Posted)).S("entryNumber").Should().Be($"JV/{Y}/0001");

        // SQL — every A47 row carries its organization
        foreach (var table in new[] { "JournalTypes", "JournalEntries", "JournalEntrySequences" })
            (await _f.RowCountAsync($"SELECT COUNT(*) FROM finance.{table} WHERE OrganizationId IS NULL")).Should().Be(0, table);
    }

    [Fact]
    public async Task No_licence_or_no_setup_refuses_the_engine()
    {
        // setup not Complete → FINANCE_SETUP_REQUIRED
        var raw = await _root.NewOrgAsync("SNS");
        await raw.K.ApplyAsync();
        var gate = await raw.K.ListEntries();
        gate.Status.Should().Be(HttpStatusCode.Forbidden, gate.ToString());
        gate.Body.S("error").Should().Be("FINANCE_SETUP_REQUIRED");
        (await raw.K.Get(Tb + $"?fromDate={Y}-01-01&toDate={Y}-12-31")).Status.Should().Be(HttpStatusCode.Forbidden);

        // licence removed → A37 D-6 read-only: reads pass, every write is MODULE_NOT_LICENSED (as GlAccessE2ETests for A42)
        var o = await _root.NewJeOrgAsync("SNL");
        var misc = await o.K.TypeAsync("MISC");
        var draft = await o.DraftAsync(misc, o.Acc("DEFAULT_CASH"), o.Acc("DEFAULT_REVENUE"), 5m);
        var year = (await o.K.YearAsync()).G("uuid");
        var period = (await o.K.PeriodAsync(new DateTime(Y, 7, 1))).G("uuid");
        await _root.LicenceAsync(o.OrgId, (Gl.Feature, false));
        await _f.InvalidateAsync(o.OrgId);
        foreach (var url in new[] { Entries, Types, $"{Close}/{year}/status", $"{Recon}/open-items?accountUuid={o.Acc("DEFAULT_CASH")}",
                     $"{Tb}?fromDate={Y}-01-01&toDate={Y}-12-31" })
            (await o.K.Get(url)).Status.Should().Be(HttpStatusCode.OK, $"read-only after the licence is removed: {url}");
        foreach (var (what, call) in new (string, Func<Task<Api>>)[]
                 {
                     ("create", () => o.K.CreateEntry(Body(misc, Today, [new JeLine(o.Acc("DEFAULT_CASH"), 1m, 0m), new JeLine(o.Acc("DEFAULT_REVENUE"), 0m, 1m)]))),
                     ("post", () => o.K.PostEntry(draft.G("uuid"))),
                     ("delete", () => o.K.DeleteEntry(draft.G("uuid"))),
                     ("type", () => o.K.Post(Types, new { code = "NOLIC", name = "x", type = "Miscellaneous" })),
                     ("lock", () => o.K.LockPeriod(period)),
                     ("execute", () => o.K.YearExecute(year)),
                     ("reconcile", () => o.K.Reconcile([Guid.NewGuid(), Guid.NewGuid()])),
                 })
        {
            var r = await call();
            r.ShouldBeForbidden($"FEATURE_GENERAL_LEDGER off: {what}");
            r.Body.S("errorCode").Should().Be("MODULE_NOT_LICENSED", what);
        }
        (await o.K.EntryAsync(draft.G("uuid"))).S("status").Should().Be("Draft");
        await _root.LicenceAsync(o.OrgId, (Gl.Feature, true));
        await _f.InvalidateAsync(o.OrgId);
        (await o.K.ListEntries()).Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Org_admin_role_holds_every_D15_code()
    {
        var rows = await _f.QueryAsync("SELECT Code FROM auth.Permissions WHERE Code LIKE 'GL[_]%'");
        rows.Select(r => (string)r["Code"]!).Should().Contain(D15, "the D-15 codes are seeded");
        // the org admin (the kit) can do everything: create + post + reverse + reconcile + close
        var w = await WorldAsync("SADM");
        var k = w.O.K;
        (await k.ReverseEntry(w.Posted, "admin")).Status.Should().Be(HttpStatusCode.OK);
        (await k.Reconcile([w.ArLine1, w.ArLine2])).Status.Should().Be(HttpStatusCode.OK);
        (await k.LockPeriod(w.Period)).Status.Should().Be(HttpStatusCode.OK);
        (await k.FinalClosePeriod(w.Period)).Status.Should().Be(HttpStatusCode.OK);
        (await k.TrialBalance(new DateTime(Y, 1, 1), new DateTime(Y, 12, 31))).Status.Should().Be(HttpStatusCode.OK);
    }
}
