using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace SMS.Integration.Tests;

/// <summary>
/// The Warehouse "receiving check": GRN submit refuses (422) until every line has a PASS / FAIL / PARTIAL recorded.
/// The older host tests (MIV, procurement-to-issue, manufacturing, vendor compatibility) were written before that
/// rule and stopped at GRN submit. This records PASS on each line the way <c>SapKit.ReceiveAllAsync</c> does: the
/// line PATCH rewrites the whole line, so everything it holds is sent back unchanged.
/// </summary>
internal static class GrnReceivingCheck
{
    public static async Task PassAllAsync(HttpClient client, Guid grnUuid)
    {
        var read = await client.GetAsync($"/api/grns/{grnUuid}");
        var raw = await read.Content.ReadAsStringAsync();
        read.StatusCode.Should().Be(HttpStatusCode.OK, $"read GRN — {raw}");
        var grn = JsonDocument.Parse(raw).RootElement.GetProperty("result");

        foreach (var line in grn.GetProperty("lines").EnumerateArray())
        {
            JsonElement P(string name) => line.EnumerateObject().First(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Value;
            object? Val(string name)
            {
                var v = P(name);
                return v.ValueKind switch
                {
                    JsonValueKind.Null   => null,
                    JsonValueKind.Number => v.GetDecimal(),
                    JsonValueKind.String => v.GetString(),
                    _                    => v.GetRawText()
                };
            }

            var patch = await client.PatchAsJsonAsync($"/api/grns/{grnUuid}/lines/{P("uuid").GetGuid()}", new
            {
                VariantUuid     = Val("variantUuid") is string s ? Guid.Parse(s) : (Guid?)null,
                QtyReceived     = P("qtyReceived").GetDecimal(),
                QtyAccepted     = P("qtyAccepted").GetDecimal(),
                QtyRejected     = P("qtyRejected").GetDecimal(),
                RejectionReason = Val("rejectionReason"),
                BinUuid         = Val("binUuid") is string b ? Guid.Parse(b) : (Guid?)null,
                BatchNumber     = Val("batchNumber"),
                ExpiryDate      = Val("expiryDate") is string e ? DateTime.Parse(e, System.Globalization.CultureInfo.InvariantCulture) : (DateTime?)null,
                UnitCost        = Val("unitCost") as decimal?,
                QcResult        = "PASS"
            });
            patch.StatusCode.Should().Be(HttpStatusCode.OK, $"record the receiving check — {await patch.Content.ReadAsStringAsync()}");
        }
    }
}
