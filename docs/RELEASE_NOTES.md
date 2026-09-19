# Release Notes — Supplier Access & Return-Order Acknowledgment (REQ-1.x – REQ-4.x)

## REQ-1.x — Supplier Type on User Creation

- Every user now has a required **Supplier Type**: `Internal` or `External`.
- Enforced on both create and update, in the UI and on the server.
- Shown as a column in the Users grid and filterable there.

## REQ-2.x — User-to-Supplier Access Mapping

- External users can now be restricted to one or more specific suppliers via a new multi-select on the Users screen.
- Every supplier-scoped list/query in the app (Purchase Orders, Return Orders, Invoices, etc.) automatically honors this restriction — a restricted External user only ever sees data for their assigned suppliers.
- Attempting to open a specific record by ID/URL for a supplier the user isn't assigned to is blocked server-side, not just hidden from lists.
- Users with no supplier restriction configured (the default for Internal users) are unaffected — they see everything they otherwise had access to.

## REQ-3.x — Return Order Dispatch & Supplier Acknowledgment

- Dispatching a Supplier Return Order (SRO) now automatically emails the supplier a secure, one-time link.
- The supplier opens the link with **no login required**, reviews the returned items and dispatch details (carrier, tracking, RMA), and confirms receipt — optionally adding remarks and the date they received the goods.
- Confirming receipt through the link updates the return order's status the same way the internal "Confirm Supplier Receipt" button does; whichever happens first wins, so both paths stay usable.
- The internal Return Order detail page shows a new "Supplier Acknowledgment" panel with the acknowledgment status, timestamp, and any remarks the supplier left.
- Each link is single-use: reopening it after acknowledgment shows "Already Acknowledged"; after it expires it shows "Link Expired."

## REQ-4.x — Acknowledgment Link Expiry Setting

- The number of days a supplier's acknowledgment link stays valid (previously fixed at 14 days) is now a per-organization setting, editable from **Administration → Settings → Portal Settings**.
- Accepts any whole number of days from 1 to 90; organizations that haven't set a value keep the previous 14-day default.
- Requires the `SYSTEM_CONFIGURE` permission to view or change — the same permission already used for Currencies, Payment Terms, and Lookup settings.
- Only affects links generated *after* the setting is changed — links already sent to suppliers keep their original expiry.

---

### Upgrade notes

- A new `OrganizationSettings` table is created by migration; no action needed, it's applied automatically at deploy time and existing organizations default to 14 days until an admin changes it.
- The old `SroPortal:LinkExpiryDays` value in `appsettings.json` has been removed — it's no longer read by the application.
