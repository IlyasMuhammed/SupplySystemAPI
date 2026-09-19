# Admin Guide — Supplier Access & Return-Order Acknowledgment

This guide covers day-to-day administration of the features shipped under REQ-1.x through REQ-4.x: user supplier-type/access control, the supplier return-order acknowledgment portal, and its settings.

## 1. Setting a user's Supplier Type

Every user is either:

- **Internal** — an employee of your own organization.
- **External** — a supplier contact who should only see data relevant to them.

Set this when creating or editing a user, under **Users → Create/Edit User**. It's required — you can't save a user without choosing one. The Users grid shows this as a column and can be filtered by it.

## 2. Restricting an External user to specific suppliers

When a user's Supplier Type is **External**, a **Supplier Access** multi-select appears on their user form. Select one or more suppliers to restrict what that user can see:

- If suppliers are selected, the user only sees Purchase Orders, Return Orders, Invoices, and other supplier-scoped records for those suppliers — everywhere in the app, including direct links to a specific record.
- If no suppliers are selected, the user has no restriction (sees everything an Internal user in their role would see). Use this deliberately — it's the fail-open default, so don't leave it blank for a supplier contact you mean to restrict.

## 3. How the supplier acknowledgment portal works

When a Return Order (SRO) is dispatched:

1. The system automatically emails the supplier's contact a unique link.
2. The supplier opens the link — no account or login needed — and sees the return number, items being returned, and dispatch details (carrier, tracking reference, dispatch date).
3. They confirm receipt, optionally noting the date they received the goods and any remarks about condition.
4. The Return Order's status updates automatically once they confirm.

You can also confirm receipt yourself from the Return Order detail page (**Confirm Supplier Receipt** button) if the supplier calls or emails instead of using the link — whichever happens first is the one that counts, so there's no conflict either way.

The Return Order detail page shows a **Supplier Acknowledgment** panel once the supplier responds, with their remarks and the date/time they confirmed.

**Link lifetime:** each link works once. After the supplier confirms, reopening it shows "Already Acknowledged." After it expires (see below), it shows "Link Expired" and the supplier should be contacted directly.

## 4. Changing how long acknowledgment links stay valid

Go to **Administration → Settings → Portal Settings** (requires the `SYSTEM_CONFIGURE` permission).

- Enter a value between **1 and 90 days**.
- Save. The change takes effect for any Return Order dispatched *after* saving — it does not retroactively change links already sent to suppliers.
- If you've never set a value, the system uses **14 days** by default.

If you enter a value outside 1–90, the page will reject it before you can save; the server enforces the same range independently.
