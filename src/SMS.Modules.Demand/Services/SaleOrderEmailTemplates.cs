using System.Net;
using SMS.Modules.Demand.Domain;

namespace SMS.Modules.Demand.Services;

/// <summary>A29-P4-07 §5.2 — shared HTML shell and line-breakdown table, reused by every event this
/// task builds a real template for. Every dynamic value is HtmlEncoded before it reaches a string
/// interpolation — these render into an actual email client, so a customer/product name containing
/// HTML-significant characters must never be interpreted as markup.</summary>
internal static class SaleOrderEmailTemplates
{
    internal readonly record struct LineRow(
        string Product, decimal Ordered, decimal? Available, decimal? Deficit, string? Mode, string Action);

    internal static string Confirmation(SaleOrder order, string partnerName, IReadOnlyList<LineRow> rows)
    {
        var header = $"""
            <p>
              <b>Sale Order:</b> {E(order.SoNumber)}<br/>
              <b>Customer:</b> {E(partnerName)}<br/>
              <b>Order date:</b> {order.OrderDate:dd MMM yyyy}<br/>
              <b>Expected delivery:</b> {(order.ExpectedDeliveryDate is { } d ? d.ToString("dd MMM yyyy") : "-")}
            </p>
            """;

        var footer = order.DeliveryMode == "SELF_PICKUP"
            ? "<p><i>Customer will collect. No shipment required. Quantities held at the fulfilling warehouse.</i></p>"
            : "";

        return Shell($"Sale Order {E(order.SoNumber)} Confirmed", header + LineTable(rows) + footer);
    }

    internal static string PoCreated(SaleOrder order, PurchaseOrder po, IReadOnlyList<LineRow> rows)
    {
        var body = $"""
            <p>
              <b>Purchase order:</b> {E(po.PoNumber)} ({E(po.Status)})<br/>
              <b>Supplier:</b> {E(po.SupplierName)}<br/>
              <b>Total:</b> {po.TotalAmount:0.00}<br/>
              <b>For sale order:</b> {E(order.SoNumber)}
            </p>
            """;

        return Shell($"Back-to-Back PO {E(po.PoNumber)} Created", body + LineTable(rows));
    }

    internal static string DropShip(SaleOrder order, string partnerName, IReadOnlyList<string> supplierNames)
    {
        var suppliers = supplierNames.Count > 0 ? string.Join(", ", supplierNames.Select(E)) : "(not yet selected)";

        // No cross-module address lookup exists (Logistics' Address entity is internal, with no
        // shared read interface) — the id is shown as a reference rather than a resolved street
        // address until one is built.
        var body = $"""
            <p>
              <b>Sale Order:</b> {E(order.SoNumber)}<br/>
              <b>Customer:</b> {E(partnerName)}<br/>
              <b>Supplier(s):</b> {suppliers}<br/>
              <b>Delivery address reference:</b> {order.ShippingAddressId}<br/>
              <i>Supplier will ship directly to the customer at the above address.</i>
            </p>
            """;

        return Shell($"Drop Ship Order — {E(order.SoNumber)}", body);
    }

    internal static string Reserved(SaleOrder order, int reservationCount, int ttlHours)
    {
        var body = $"""
            <p>
              <b>Sale Order:</b> {E(order.SoNumber)}<br/>
              <b>Reservations placed:</b> {reservationCount} line(s)<br/>
              Reservations expire {ttlHours} hour(s) after being placed unless the order is fulfilled first.
            </p>
            """;

        return Shell($"Stock Reserved — {E(order.SoNumber)}", body);
    }

    internal static string GrnReceived(
        SaleOrder order, string grnNumber, decimal receivedQty, decimal reservedQty, decimal stillAwaited)
    {
        var closing = stillAwaited <= 0m
            ? "Everything this order was waiting on has arrived and is reserved — it is ready to fulfil."
            : $"{stillAwaited:0.####} unit(s) are still awaited; the order stays open until they arrive.";

        var body = $"""
            <p>
              <b>Sale Order:</b> {E(order.SoNumber)}<br/>
              <b>Goods receipt:</b> {E(grnNumber)}<br/>
              <b>Received:</b> {receivedQty:0.####}<br/>
              <b>Reserved for this order:</b> {reservedQty:0.####}<br/>
              {closing}
            </p>
            """;

        return Shell($"Goods Received — {E(order.SoNumber)}", body);
    }

    internal static string Expiring(SaleOrder order, DateTime expiresAt)
    {
        var body = $"""
            <p>
              <b>Sale Order:</b> {E(order.SoNumber)}<br/>
              A stock reservation for this order expires at <b>{expiresAt:dd MMM yyyy HH:mm} UTC</b> —
              in less than 24 hours. Confirm fulfilment or the hold will be released automatically.
            </p>
            """;

        return Shell($"Reservation Expiring — {E(order.SoNumber)}", body);
    }

    internal static string PoApproved(SaleOrder order, PurchaseOrder po)
    {
        var body = $"""
            <p>
              <b>Purchase order:</b> {E(po.PoNumber)} — approved and sent to vendor.<br/>
              <b>Supplier:</b> {E(po.SupplierName)}<br/>
              <b>Expected delivery:</b> {(po.DeliveryDate is { } d ? d.ToString("dd MMM yyyy") : "-")}<br/>
              <b>For sale order:</b> {E(order.SoNumber)}
            </p>
            """;

        return Shell($"PO {E(po.PoNumber)} Approved", body);
    }

    internal static string Fulfilled(
        SaleOrder order, string partnerName, string deliveryNumber, decimal deliveredQty, IReadOnlyList<LineRow> rows)
    {
        var header = $"""
            <p>
              <b>Sale Order:</b> {E(order.SoNumber)}<br/>
              <b>Customer:</b> {E(partnerName)}<br/>
              <b>Completed by delivery:</b> {E(deliveryNumber)} ({deliveredQty:0.####} unit(s))<br/>
              Every line of this order has now been delivered{(order.DeliveryMode == "SELF_PICKUP" ? " or collected" : "")}.
            </p>
            """;

        return Shell($"Sale Order {E(order.SoNumber)} Fulfilled", header + LineTable(rows));
    }

    private static string LineTable(IReadOnlyList<LineRow> rows)
    {
        var body = string.Concat(rows.Select(l => $"""
            <tr>
              <td style="padding:6px 10px;border-bottom:1px solid #e2e8f0;">{E(l.Product)}</td>
              <td style="padding:6px 10px;border-bottom:1px solid #e2e8f0;text-align:right;">{l.Ordered:0.####}</td>
              <td style="padding:6px 10px;border-bottom:1px solid #e2e8f0;text-align:right;">{(l.Available is { } a ? a.ToString("0.####") : "-")}</td>
              <td style="padding:6px 10px;border-bottom:1px solid #e2e8f0;text-align:right;">{(l.Deficit is > 0 and { } df ? df.ToString("0.####") : "-")}</td>
              <td style="padding:6px 10px;border-bottom:1px solid #e2e8f0;">{E(l.Mode ?? "-")}</td>
              <td style="padding:6px 10px;border-bottom:1px solid #e2e8f0;">{E(l.Action)}</td>
            </tr>
            """));

        return $"""
            <table width="100%" cellpadding="0" cellspacing="0" style="border-collapse:collapse;font-size:13px;margin:12px 0;">
              <thead>
                <tr style="background:#f1f5f9;">
                  <th style="padding:6px 10px;text-align:left;">Product</th>
                  <th style="padding:6px 10px;text-align:right;">Ordered</th>
                  <th style="padding:6px 10px;text-align:right;">Available</th>
                  <th style="padding:6px 10px;text-align:right;">Deficit</th>
                  <th style="padding:6px 10px;text-align:left;">Mode</th>
                  <th style="padding:6px 10px;text-align:left;">Action taken</th>
                </tr>
              </thead>
              <tbody>{body}</tbody>
            </table>
            """;
    }

    private static string Shell(string title, string bodyHtml) => $"""
        <!DOCTYPE html>
        <html>
        <head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"></head>
        <body style="margin:0;padding:0;background:#f1f5f9;font-family:Arial,sans-serif;">
          <table width="100%" cellpadding="0" cellspacing="0" style="background:#f1f5f9;padding:32px 0;">
            <tr><td align="center">
              <table width="640" cellpadding="0" cellspacing="0" style="background:#ffffff;border-radius:8px;overflow:hidden;box-shadow:0 2px 8px rgba(0,0,0,.08);">
                <tr><td style="background:linear-gradient(135deg,#0f172a 0%,#1e293b 100%);padding:20px 28px;">
                  <span style="color:#fff;font-size:16px;font-weight:600;">{title}</span>
                </td></tr>
                <tr><td style="padding:24px 28px;color:#1e293b;font-size:14px;line-height:1.6;">
                  {bodyHtml}
                </td></tr>
                <tr><td style="padding:16px 28px;background:#f8fafc;color:#64748b;font-size:12px;">
                  Automated notice from SMS — please do not reply to this email.
                </td></tr>
              </table>
            </td></tr>
          </table>
        </body>
        </html>
        """;

    private static string E(string s) => WebUtility.HtmlEncode(s);
}
