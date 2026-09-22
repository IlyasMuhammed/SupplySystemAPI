using FluentAssertions;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Services;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// A29-P7-10 §9.5, §10 — no hand-picked scenario, however careful, covers every order in which a
/// customer can be billed, pay, pay again, have a cheque returned and fall overdue. These drive the real
/// services through a few hundred seeded random operations and, after every one, hold the books to the
/// rules that must be true whatever happened: the ledger chains, agrees with the invoices and payments,
/// and each invoice's balance and status follow from the money still good against it. A failure names
/// the seed and every step so far, so it can be replayed exactly.
/// </summary>
public class ReceivablesReconciliationTests
{
    private const int Steps = 40;

    public static IEnumerable<object[]> Seeds() => Enumerable.Range(1, 30).Select(seed => new object[] { seed });

    private static readonly string[] Methods = ["CASH", "CHEQUE", "BANK_TRANSFER", "CARD", "ONLINE", "CHEQUE", "CHEQUE"];

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task Whatever_happens_to_a_customers_account_the_books_reconcile_after_every_step(int seed) =>
        await RunAsync(seed);

    /// <summary>
    /// A run that never bounced a cheque or flagged an invoice would pass the test above and prove
    /// nothing about either, so the runs together must have done each of the things worth checking.
    /// </summary>
    [Fact]
    public async Task The_random_runs_between_them_exercise_every_kind_of_step()
    {
        var log = new List<string>();
        foreach (var seed in Enumerable.Range(1, 30)) log.AddRange(await RunAsync(seed));

        int Count(string pattern) => log.Count(l => System.Text.RegularExpressions.Regex.IsMatch(l, pattern));

        Count(@"\. issue ").Should().BeGreaterThan(200);
        Count(@"FIFO, [2-9]\d* invoice\(s\)").Should().BeGreaterThan(10, "FIFO payments that run across several invoices");
        Count(@"FIFO, 0 invoice\(s\)").Should().BeGreaterThan(5, "receipts that arrive with nothing to pay");
        Count(@"manual over [2-3] invoice").Should().BeGreaterThan(10, "manual allocations across several invoices");
        Count(@"allocate the ").Should().BeGreaterThan(5, "money on account applied afterwards");
        Count(@"\. bounce CPAY.*\([\d.]+, [1-9]\d* allocation").Should().BeGreaterThan(10, "bounces that reopened invoices");
        Count(@"\. bounce CPAY.*\([\d.]+, 0 allocation").Should().BeGreaterThan(2, "bounces of money held on account");
        Count(@"refused: bounce ").Should().BeGreaterThan(10, "refused bounces");
        Count(@"sweep: [1-9]\d* flagged").Should().BeGreaterThan(10, "sweeps that found something overdue");
    }

    private static async Task<IReadOnlyList<string>> RunAsync(int seed)
    {
        var rng      = new Random(seed);
        var world    = new ReceivablesWorld();
        var desk     = world.For(Guid.NewGuid());
        var customer = desk.NewCustomer();
        var log      = new List<string>();

        for (var step = 1; step <= Steps; step++)
        {
            var books = await desk.BooksAsync(customer);

            var (what, swept) = rng.Next(100) switch
            {
                < 24 => (await IssueAsync(rng, desk, customer, books), false),
                < 44 => (await PayFifoAsync(rng, desk, customer, books), false),
                < 56 => (await PayManualAsync(rng, desk, customer, books), false),
                < 64 => (await AllocateRemainderAsync(rng, desk, customer, books), false),
                < 78 => (await BounceAsync(rng, desk, customer, books), false),
                < 84 => (await RefuseBounceAsync(rng, desk, customer, books), false),
                _    => (await AdvanceAndSweepAsync(rng, world, customer, books), true)
            };

            log.Add($"{step,3}. {what}");

            var after = await desk.BooksAsync(customer);
            ReceivablesInvariants.Violations(after, world.Clock.Value.Date, afterOverdueSweep: swept)
                .Should().BeEmpty($"seed {seed} broke a rule at step {step} ({what}). Steps so far:\n{string.Join("\n", log)}\n");
        }

        return log;
    }

    // ── Operations. Each checks its own outcome against an independent statement of the rule. ───────

    private static decimal Cents(Random rng, decimal low, decimal high) =>
        rng.Next((int)(low * 100), (int)(high * 100) + 1) / 100m;

    private static decimal OwedNow(Books books) => books.Ledger.LastOrDefault()?.RunningBalance ?? 0m;

    private static readonly string[] PayableStatuses = ["ISSUED", "PARTIALLY_PAID", "OVERDUE"];

    private static List<SalesInvoice> Open(Books books) =>
        [.. books.Invoices.Where(i => PayableStatuses.Contains(i.Status) && i.BalanceDue > 0m)
                          .OrderBy(i => i.InvoiceDate).ThenBy(i => i.Id)];

    private static async Task<string> IssueAsync(Random rng, ReceivablesDesk desk, Guid customer, Books books)
    {
        var amount  = Cents(rng, 0.01m, 5000m);
        var invoice = await desk.InvoiceAsync(customer, amount);

        (await desk.OwesAsync(customer)).Should().Be(OwedNow(books) + amount, "issuing debits the ledger by the grand total");
        return $"issue {invoice.Number} for {amount}";
    }

    private static async Task<string> PayFifoAsync(Random rng, ReceivablesDesk desk, Guid customer, Books books)
    {
        var method = Methods[rng.Next(Methods.Length)];
        var amount = Cents(rng, 0.01m, Math.Max(books.Owing, 10m) * 1.3m);

        // §9.4, restated from the rule rather than from the code: oldest first, each paid down as far as the money goes.
        var expected = new List<(string Number, decimal Amount)>();
        var left     = amount;
        foreach (var invoice in Open(books))
        {
            if (left <= 0m) break;
            var applied = Math.Min(left, invoice.BalanceDue);
            expected.Add((invoice.InvoiceNumber, applied));
            left -= applied;
        }

        var result = await desk.PayAsync(customer, amount, method);

        result.Allocations.Select(a => (a.InvoiceNumber, a.Amount)).Should().Equal(expected, "FIFO, oldest invoice first");
        result.UnallocatedAmount.Should().Be(left);
        result.PartnerBalance.Should().Be(OwedNow(books) - amount, "the whole receipt is credited, applied or not");
        return $"pay {amount} by {method}, FIFO, {expected.Count} invoice(s), {left} left on account";
    }

    private static async Task<string> PayManualAsync(Random rng, ReceivablesDesk desk, Guid customer, Books books)
    {
        var open = Open(books);
        if (open.Count == 0) return await IssueAsync(rng, desk, customer, books);

        var chosen = open.OrderBy(_ => rng.Next()).Take(rng.Next(1, Math.Min(3, open.Count) + 1)).ToList();
        var lines  = chosen.Select(i => new ManualPaymentAllocation(i.UUID, Cents(rng, 0.01m, i.BalanceDue))).ToList();
        var total  = lines.Sum(l => l.Amount);
        var amount = total + (rng.Next(3) == 0 ? Cents(rng, 0.01m, 200m) : 0m);
        var method = Methods[rng.Next(Methods.Length)];

        var result = await desk.PayAsync(customer, amount, method, lines);

        result.Allocations.Select(a => (a.InvoiceUuid, a.Amount)).Should().Equal(lines.Select(l => (l.InvoiceUuid, l.Amount)),
            "a manual allocation is applied exactly as written");
        result.UnallocatedAmount.Should().Be(amount - total, "what the override does not name stays on account");
        return $"pay {amount} by {method}, manual over {lines.Count} invoice(s) totalling {total}";
    }

    private static async Task<string> AllocateRemainderAsync(Random rng, ReceivablesDesk desk, Guid customer, Books books)
    {
        var open = Open(books);
        var held = books.Payments.Where(p => p.Status == CustomerPaymentStatuses.Received
                                          && p.Amount > p.Allocations.Sum(a => a.AllocatedAmount)).ToList();

        if (held.Count == 0 || open.Count == 0) return await IssueAsync(rng, desk, customer, books);

        var payment   = held[rng.Next(held.Count)];
        var remaining = payment.Amount - payment.Allocations.Sum(a => a.AllocatedAmount);
        var ledgerNow = books.Ledger.Count;

        await using var scope = desk.Scope();
        var result = await scope.Payments.AllocateAsync(payment.UUID, null, ReceivablesDesk.User);

        var expected = Math.Min(remaining, open.Sum(i => i.BalanceDue));
        result.Allocations.Sum(a => a.Amount).Should().Be(expected, "FIFO over what the payment has left");
        (await desk.BooksAsync(customer)).Ledger.Should().HaveCount(ledgerNow,
            "moving money from the account onto an invoice does not change what the customer owes");
        return $"allocate the {remaining} left on {payment.PaymentNumber}, FIFO, {expected} applied";
    }

    private static async Task<string> BounceAsync(Random rng, ReceivablesDesk desk, Guid customer, Books books)
    {
        var cheques = books.Payments.Where(p => p.Status == CustomerPaymentStatuses.Received
                                             && p.PaymentMethod == CustomerPaymentMethods.Cheque).ToList();
        if (cheques.Count == 0) return await PayFifoAsync(rng, desk, customer, books);

        var cheque = cheques[rng.Next(cheques.Count)];
        var result = await desk.BounceAsync(cheque.UUID, rng.Next(2) == 0 ? "Insufficient funds" : null);

        result.Reversed.Sum(r => r.Amount).Should().Be(cheque.Allocations.Sum(a => a.AllocatedAmount),
            "everything the cheque paid is taken back");
        result.PartnerBalance.Should().Be(OwedNow(books) + cheque.Amount,
            "the full amount goes back on the customer's account, applied to an invoice or not");
        return $"bounce {cheque.PaymentNumber} ({cheque.Amount}, {cheque.Allocations.Count} allocation(s))";
    }

    private static async Task<string> RefuseBounceAsync(Random rng, ReceivablesDesk desk, Guid customer, Books books)
    {
        var refusable = books.Payments
            .Where(p => p.Status == CustomerPaymentStatuses.Bounced || p.PaymentMethod != CustomerPaymentMethods.Cheque)
            .ToList();
        if (refusable.Count == 0) return await IssueAsync(rng, desk, customer, books);

        var payment = refusable[rng.Next(refusable.Count)];
        var before  = books.Fingerprint();

        Func<Task> act = () => desk.BounceAsync(payment.UUID, "should not be possible");

        if (payment.Status == CustomerPaymentStatuses.Bounced) await act.Should().ThrowAsync<ConflictException>();
        else                                                    await act.Should().ThrowAsync<BadRequestException>();

        (await desk.BooksAsync(customer)).Fingerprint().Should().Be(before, "a refused bounce changes nothing");
        return $"refused: bounce {payment.PaymentNumber} ({payment.PaymentMethod}, {payment.Status})";
    }

    private static async Task<string> AdvanceAndSweepAsync(Random rng, ReceivablesWorld world, Guid customer, Books books)
    {
        var days = rng.Next(1, 25);
        world.Clock.Value = world.Clock.Value.AddDays(days);
        var today = world.Clock.Value.Date;

        var due = books.Invoices.Count(i => (i.Status is "ISSUED" or "PARTIALLY_PAID") && i.DueDate < today && i.BalanceDue > 0m);

        (await world.SweepOverdueAsync()).Should().Be(due, "the sweep flags exactly the issued and part-paid invoices past due with something owing");
        return $"advance {days} day(s) to {today:yyyy-MM-dd} and sweep: {due} flagged";
    }
}
