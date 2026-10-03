# Public portals: wrong-organization writes and open uploads

Status: **open, paused 2026-10-02** (a separate, later project). No production code was changed.
The proof is kept as skipped tests in
`tests/SMS.Integration.Tests/PublicPortals/PublicPortalTenancyTests.cs`
(`[Fact(Skip = "Open: see docs/security/public-portals.md")]`). They are also the acceptance tests for the fix.

## The mechanism

An anonymous request has no signed-in user. `src/SMS.Shared/Common/TenantContext.cs` then:

- reports `IsSuperAdmin = true`, so every tenant query filter is bypassed;
- reports `OrganizationId = HangfireTenantScope.OrganizationId ?? TenantDefaults.ScmDemoOrganizationId`.
  That is SCM-DEMO unless something set the scope.

Every tenant-scoped DbContext calls `StampTenantScopedEntities` on save. It fills in `OrganizationId` on any
*added* row that has none, using that value. So any row an anonymous endpoint **adds** without naming its
organization belongs to SCM-DEMO. Rows it only **updates** keep their organization.

## Each anonymous write path

| Endpoint | What it writes | Verdict |
|---|---|---|
| `RfqPortalController` `POST api/public/rfq-portal/{token}/submit` | adds `demand.vendor_responses` + `vendor_response_lines`; updates the link | **Proven.** The response and its lines are stamped SCM-DEMO. |
| `RfqPortalController` `POST api/public/rfq-portal/{token}/attachments` | adds `workflow_schema.document_attachments` (via `IAttachmentService.CreateAsync`); writes the file under `wwwroot/uploads/attachments/rfq-response/` | **Proven.** Stamped SCM-DEMO. **Also proven:** it accepts any file (see Uploads). |
| `RfqPortalController` `GET api/public/rfq-portal/{token}` | updates the link (`AccessCount`, `FirstOpenedAt`, `Status`) | No misfile: updates only, by token hash. |
| `SroPortalController` `POST api/public/sro-portal/{token}/acknowledge` | updates the SRO + link; adds `reports.audit_logs` (`ACKNOWLEDGE`); adds a notification | **Proven** for the audit row: stamped SCM-DEMO, so it is missing from the return's own audit trail. Notifications are not tenant-scoped. |
| `SroPortalController` `GET api/public/sro-portal/{token}` | updates the link | No misfile: updates only, by token hash. |
| `PublicTrackingController` `GET api/public/tracking/{token}` | nothing | **Disproven** by code review. Read-only, `IgnoreQueryFilters` by a 256-bit token, org and module checked explicitly. |
| `CarrierWebhooksController` `POST api/logistics/webhooks/{provider}/{account}` | adds `carrier_webhook_deliveries` + `consignment_tracking_events`; updates the consignment | **Disproven** by code review. Every added row sets `OrganizationId = account.OrganizationId` explicitly. Every read is `IgnoreQueryFilters` scoped to that organization. The existing test (`TrackingAndWebhookTests`) runs with the account's own org as tenant. A test with an anonymous-like tenant (SCM-DEMO, bypass) is **not written**. |
| `WhatsAppWebhookController` `POST api/whatsapp/status-callback` | updates `whatsapp_message_logs` and (Demand handler) `rfq_access_links` by Twilio MessageSid | **Disproven** by code review. Updates only; Twilio signature checked. No test written. |
| `CallbackController` `GET api/integrations/quickbooks/callback` | Integration rows | **Disproven.** `OAuthCallbackService` sets `HangfireTenantScope` to the state token's org for the rest of the callback and sets `OrganizationId` explicitly. Covered by `tests/SMS.Modules.Integration.Tests/Connections/OAuthCallbackTests.cs`. |
| `AuthController` anonymous actions | accounts, tokens | Not reviewed here (auth agent's area). |

What the proven bugs mean for users (from code; the test stops at the first failed assertion, so these are not
separately asserted). The RFQ's owner sees `SubmittedResponseCount = 0`, and **Open Bids** refuses with "No vendor
responses have been submitted yet". The vendor's link is consumed, so they cannot resubmit and the quote is lost to
the RFQ's organization. The quote's files are visible to SCM-DEMO users and not to the RFQ's.

Background jobs enqueued from the portals (`RfqResponseNotificationJob`, `SroEscalationJob`) carry no organization:
`TenantPropagatingJobFilter.OnCreating` reads only the claim. They run as bypass. Today that is harmless: one writes
only a notification, which is not tenant-scoped, and the other only updates the SRO.

## Fix approach

1. Resolve the owning organization from the token first. Add `Task<Guid?> FindOrganizationIdAsync(string rawToken)`
   to `IRfqLinkValidationService` and `ISroAckValidationService`: `IgnoreQueryFilters().AsNoTracking()` by `TokenHash`.
2. Enter an ambient tenant scope for that organization for the rest of the portal action. Use `HangfireTenantScope`,
   the same mechanism the QuickBooks callback uses. With no user and a scope set, `TenantContext` reports that
   organization and `IsSuperAdmin = false`. Every added row is then stamped with it, including rows written by shared
   services such as `AttachmentService` and `AuditService`, which take no organization parameter. Every read is
   filtered to it.
   - Suggested helper: an additive `HangfireTenantScope.Enter(Guid)` returning an `IDisposable` that restores the
     previous value. It must be a synchronous method, so the `AsyncLocal` change is visible to the calling action.
     The action holds it with `using`.
   - An unknown token enters no scope. Validation answers `INVALID` and nothing is written.
3. No change to `TenantContext`'s general behaviour, no EF model change, no migration.
4. Separate decisions, not needed for correctness today:
   - `TenantPropagatingJobFilter.OnCreating` could fall back to `HangfireTenantScope.OrganizationId` when there is no
     claim. Jobs enqueued from portals and from inside jobs would then carry the organization.
   - The RFQ and SRO portals do not check that the organization is active or has `MODULE_DEMAND` / `MODULE_WAREHOUSE`
     switched on. The tracking and webhook endpoints do.

## Upload allow-list plan

Use the shared rules: `SMS.Shared.Files.UploadRules` (`src/SMS.Shared/Files/UploadRules.cs`, owned by the
attachments work, unit-tested in `tests/SMS.WorkflowEngine.Tests/UploadRulesTests.cs`). In
`RfqPortalController.UploadAttachment`:

- before `Directory.CreateDirectory`: `if (UploadRules.Refusal(file.FileName, file.Length, file.ContentType) is { } reason) return BadRequest(ApiResponse.Fail(reason));`
- stored name `{Guid.NewGuid()}{UploadRules.DiskExtension(file.FileName)}`, written with `FileMode.CreateNew`;
- record `FileName = UploadRules.DisplayName(file.FileName)!` and `ContentType = UploadRules.ContentTypeOf(file.FileName)`,
  never the client's values.

`Refusal` checks both the extension and the client's content type. An active type (`text/html`, xhtml, `image/svg+xml`,
xml or any `+xml`, javascript/ecmascript, xsl, x-component, hta, `message/rfc822`, flash) is refused whatever the
name. Accepted extensions:
- PDF: `.pdf`
- images: `.png .jpg .jpeg .gif .webp .bmp .tif .tiff .heic`
- Office and OpenDocument: `.doc .docx .xls .xlsx .ppt .pptx .odt .ods .odp .rtf`
- text and mail: `.csv .txt .msg`
- archives: `.zip .7z .rar`
- drawings: `.dwg .dxf`

The SRO portal has no upload.

Proven on the current code: the portal accepted all of these, wrote them to `wwwroot`, and recorded them:
- `.html`, `.htm`, `.svg`, `.hxt`, `.xsd`, `.js`, `.exe`;
- `quote.pdf` sent as `text/html`;
- `logo.png` sent as `image/svg+xml`.

The skipped Theory `The_rfq_portal_refuses_an_upload_a_browser_would_run_as_a_page` covers them.
`tests/SMS.Modules.Demand.Tests/RfqPortalAttachmentSecurityAuditTests.cs` (the security auditor's) expects the same
refusals at controller level. I did not run it.

## Finding already-misfiled rows on a live database (read-only)

These are `SELECT`s only. Correcting the rows is the user's decision. The `OrganizationId` would become the
quotation's or return's.

```sql
-- 1. Vendor responses (portal submissions) filed under a different organization than their RFQ.
SELECT vr.Id, vr.UUID, vr.SupplierName, vr.CreatedDate, q.QuotationNumber,
       vr.OrganizationId AS StoredOrg, q.OrganizationId AS RfqOrg
FROM demand.vendor_responses vr
JOIN demand.quotations q ON q.Id = vr.QuotationId
WHERE vr.OrganizationId <> q.OrganizationId;

-- 2. Their lines.
SELECT l.Id, l.VendorResponseId, l.OrganizationId AS StoredOrg, q.OrganizationId AS RfqOrg
FROM demand.vendor_response_lines l
JOIN demand.vendor_responses vr ON vr.Id = l.VendorResponseId
JOIN demand.quotations q ON q.Id = vr.QuotationId
WHERE l.OrganizationId <> q.OrganizationId;

-- 3. Vendor-uploaded files filed under a different organization than their RFQ.
SELECT a.Id, a.UUID, a.FileName, a.FileUrl, a.UploadedDate, q.QuotationNumber,
       a.OrganizationId AS StoredOrg, q.OrganizationId AS RfqOrg
FROM workflow_schema.document_attachments a
JOIN demand.vendor_responses vr ON vr.UUID = a.DocumentId
JOIN demand.quotations q ON q.Id = vr.QuotationId
WHERE a.InterfaceCode = 'RFQ_RESPONSE' AND a.OrganizationId <> q.OrganizationId;

-- 4. Portal uploads whose response was never submitted (no owner can be derived; UploadedBy = 0 is the portal).
SELECT a.Id, a.UUID, a.FileName, a.FileUrl, a.OrganizationId, a.UploadedDate
FROM workflow_schema.document_attachments a
WHERE a.InterfaceCode = 'RFQ_RESPONSE' AND a.UploadedBy = 0
  AND NOT EXISTS (SELECT 1 FROM demand.vendor_responses vr WHERE vr.UUID = a.DocumentId);

-- 5. Portal uploads stored with an extension outside the allow-list (web pages, SVG, scripts…).
SELECT a.Id, a.FileName, a.FileUrl, a.ContentType, a.OrganizationId, a.UploadedDate
FROM workflow_schema.document_attachments a
CROSS APPLY (SELECT LOWER(CASE WHEN CHARINDEX('.', REVERSE(a.FileUrl)) > 0
                               THEN RIGHT(a.FileUrl, CHARINDEX('.', REVERSE(a.FileUrl))) ELSE '' END) AS Ext) x
WHERE a.InterfaceCode = 'RFQ_RESPONSE'
  AND x.Ext NOT IN ('.pdf','.png','.jpg','.jpeg','.gif','.webp','.bmp','.tif','.tiff','.heic',
                    '.doc','.docx','.xls','.xlsx','.ppt','.pptx','.odt','.ods','.odp','.rtf',
                    '.csv','.txt','.msg','.zip','.7z','.rar','.dwg','.dxf');

-- 6. Supplier return acknowledgements (SRO portal) in a different organization's audit trail.
SELECT al.Id, al.Timestamp, s.ReturnNumber, al.Notes,
       al.OrganizationId AS StoredOrg, s.OrganizationId AS ReturnOrg
FROM reports.audit_logs al
JOIN warehouse.supplier_return_orders s ON s.UUID = al.EntityId
WHERE al.EntityType = 'SRO' AND al.Action = 'ACKNOWLEDGE' AND al.OrganizationId <> s.OrganizationId;
```

The files found by query 5 are still on disk under `wwwroot/uploads/attachments/rfq-response/` and are served
from the API origin until removed.

## Running the proof

```powershell
dotnet build tests\SMS.Integration.Tests\SMS.Integration.Tests.csproj -o <scratch>\int
# remove Skip = Open from the attributes first, then:
dotnet test <scratch>\int\SMS.Integration.Tests.dll --filter "FullyQualifiedName~PublicPortalTenancyTests"
```

Result on 2026-10-02, before they were skipped: 17 of 17 failed, each on the assertion it names. The rows came
back with `84d0a96d-52d4-4375-9260-357b46fb9d9f` (SCM-DEMO) instead of the RFQ's or return's organization, and every
refused upload answered 200.
