using SMS.Modules.Pos.Domain;
using SMS.Modules.Pos.Models;
using SMS.Modules.Pos.Services;
using SMS.Shared.Common;
using static SMS.Modules.Pos.Tests.PosHarness;

namespace SMS.Modules.Pos.Tests;

/// <summary>A39 test helpers on top of <see cref="PosHarness"/>.</summary>
internal static class A39
{
    public const int Manager = 40, CrossManager = 41;

    /// <summary>The store supervisor who processes returns (staff of S1).</summary>
    public static readonly PosCaller Returner = Caller(Supervisor, PosPermissions.OrderCreate, PosPermissions.OrderReturn, PosPermissions.OrderVoid,
        PosPermissions.ReturnCrossStore, PosPermissions.CreditNoteRedeem, PosPermissions.CreditNoteIssue, PosPermissions.OrderDiscount);

    /// <summary>A till cashier who may redeem store credit.</summary>
    public static readonly PosCaller Redeemer = Caller(Cashier, PosPermissions.OrderCreate, PosPermissions.CreditNoteRedeem);

    /// <summary>S1 open shift + item A (10.00, 17 % excl → 11.70) + a SUPERVISOR manager (PIN 4444) holding POS_RETURN_OVERRIDE.</summary>
    public static async Task<(PosStoreModel Store, PosTerminalModel Terminal, PosShiftModel Shift, PosCatalogVariant Item)> SetupAsync(this PosHarness h)
    {
        var setup = await h.SellingSetupAsync();
        h.Pins.AddUser(Manager, "Mona", PosPermissions.OrderCreate, PosPermissions.ReturnOverride);
        h.Pins.AddUser(CrossManager, "Xavi", PosPermissions.OrderCreate, PosPermissions.ReturnCrossStore);
        await h.StaffAsync(setup.Store.Uuid, Manager, PosRole.Supervisor, "4444");
        await h.StaffAsync(setup.Store.Uuid, CrossManager, PosRole.Supervisor, "5555");
        return setup;
    }

    public static async Task<PosOrderModel> SellAsync(this PosHarness h, PosTerminalModel t, PosShiftModel s, PosCatalogVariant item, decimal qty,
        Guid? customer = null, params PosSyncPaymentRequest[] payments)
    {
        var total = Math.Round(qty * item.Price * 1.17m, 2, MidpointRounding.AwayFromZero);
        var result = await h.Orders.SyncAsync(Sale(t, s, [(item, qty)], total, customer: customer, payments: payments), Redeemer);
        return await h.Orders.GetAsync(result.OrderUuid!.Value, AdminCaller);
    }

    public static async Task<Guid> ReasonAsync(this PosHarness h, string code) =>
        (await h.Policies.ListReasonsAsync(new PosReasonListFilter { IncludeInactive = true })).Single(r => r.Code == code).Uuid;

    public static PosReturnRequest Return(Guid line, decimal qty, Guid reason, string? method = PosPaymentMethod.Cash, string? note = null,
        string? token = null, string? localId = null) => new()
    {
        LocalId = localId ?? Guid.NewGuid().ToString("N"), ReasonUuid = reason, Note = note, AuthorizationToken = token, RefundMethod = method,
        Lines = [new PosReturnLineRequest { OriginalLineUuid = line, Quantity = qty }]
    };

    public static PosReturnPolicyUpsertRequest Policy(Action<PosReturnPolicyUpsertRequest>? change = null)
    {
        var p = new PosReturnPolicyUpsertRequest
        {
            Name = "Policy", ReturnWindowDays = 30, RequireReceipt = true, AllowCrossStoreReturn = true, AllowCashRefund = true,
            AllowCreditNoteRefund = true, CreditNoteExpiryDays = 365, IsActive = true
        };
        change?.Invoke(p);
        return p;
    }

    public static PosSyncLineRequest Line(PosCatalogVariant v, decimal qty) =>
        new() { LocalLineId = Guid.NewGuid().ToString("N")[..8], VariantUuid = v.VariantUuid, Quantity = qty, UnitPrice = v.Price };
}
