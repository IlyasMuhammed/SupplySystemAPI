# QuickBooks Online — Create Customer — Integration Report

Feasibility and field-level specification for matrix feature **#7 (Customer Sync)**, implemented by
**QB-P1-18** in [`QUICKBOOKS-PHASE-1-TASKS.md`](./QUICKBOOKS-PHASE-1-TASKS.md).

```
POST https://{{baseurl}}/v3/company/{{realmId}}/customer?minorversion={{minorversion}}
```

Verified against the codebase on 2026-09-29.

> **On the QBO side of this document:** field limits, fault codes and behaviours below reflect
> long-stable QuickBooks behaviour, but the exact numbers should be confirmed against the current
> Intuit API reference before QB-P1-18 starts. The *direction* of each gap — ours longer than
> theirs, ours unvalidated where theirs is validated — is what drives the work and does not depend
> on the precise figure.

---

## 1. Verdict

**We can integrate with this endpoint today. No new columns are required.** Every field in the
sample payload already exists on `BusinessPartner`, and so do several useful fields the sample
omits.

What is missing is not data but **discipline around the data**: four constraint mismatches that
will produce hard refusals, and three fields absent from the sample payload that a correct customer
record needs.

| | Count |
|---|---|
| Sample payload fields that map cleanly | 6 of 9 |
| Sample payload fields needing work | 3 |
| Additional fields we can already populate | 5 |
| New columns required on `BusinessPartner` | **0** |
| New storage required | 1 — the mapping table (QB-P1-15), already planned |

---

## 2. Where our customers actually live

Stated explicitly because the file naming misleads, and any implementer will hit this on day one.

**There is no `Customer` entity or `Customers` table in this codebase.** `class Customer`,
`ToTable("Customers")` and `DbSet<Customer>` all return zero matches across `src/`.

A customer is a **`BusinessPartner` row with `IsCustomer = true`**, in schema `suppliers`, table
`BusinessPartners`, served by `/api/partners`. `SalesInvoice.PartnerId` documents this directly:
*"The customer. Bare UUID onto suppliers.BusinessPartners."*

The class lives in [`SupplierEntities.cs`](../../src/SMS.Modules.Suppliers/Domain/SupplierEntities.cs)
and its properties are still `SupplierName`, `SupplierCode`. That is the deliberate scope of
A29-P1-01, which renamed the table only and explicitly declined the column renames. So this line is
correct, and will look like a bug to anyone who does not know the history:

```
qboCustomer.DisplayName = partner.SupplierName;   // for a CUSTOMER
```

**Recommendation:** map through a customer-shaped DTO inside the Accounting module rather than
reading `BusinessPartner` properties directly at the payload-building site. The naming confusion
then stops at one boundary instead of spreading through the adapter.

Note also that `pages/customer/` in the frontend is a **portal user management screen**, not the
customer master — it imports `UserService` and checks `USER_MANAGE`. The partner master UI is
A29 **P1-08**, last seen `BLOCKED`. Confirm it exists before Phase 1 go-live, because both the
adoption review (QB-P1-17) and the currency requirement (D-1) assume somebody can go and correct
partner data.

---

## 3. Field mapping

`Req?` is QuickBooks' requirement, not ours. Lengths in brackets are our column limits from
[`SupplierMaps.cs`](../../src/SMS.Modules.Suppliers/Data/Maps/SupplierMaps.cs).

### 3.1 In the sample payload

| QBO field | Req? | Our source | Status |
|---|---|---|---|
| `DisplayName` | **Yes** | `SupplierName` [200] | ⚠️ **G-1, G-2, G-3** |
| `BillAddr.Line1` | No | `AddressLine1` [200] | ✅ |
| `BillAddr.City` | No | `City` [100] | ✅ |
| `BillAddr.Country` | No | `Country` [200] | ✅ |
| `BillAddr.CountrySubDivisionCode` | No | `ProvinceState` [100] | ⚠️ **G-5** |
| `BillAddr.PostalCode` | No | `PostalCode` [20] | ✅ |
| `PrimaryPhone.FreeFormNumber` | No | `Phone` [20] | ✅ |
| `PrimaryEmailAddr.Address` | No | `Email` [150] | ⚠️ **G-4** |
| `Notes` | No | `Notes` [500] | ✅ |

### 3.2 Not in the sample, and we can already populate them

| QBO field | Our source | Note |
|---|---|---|
| `CompanyName` | `SupplierName` [200] | Same 100-char ceiling as `DisplayName` |
| `BillAddr.Line2` | `AddressLine2` [200] | |
| `WebAddr.URI` | `Website` [200] | |
| `Fax.FreeFormNumber` | `Fax` [20] | |
| `PrimaryTaxIdentifier` | `TaxId` [30] | Also a **strong match key** for adoption (QB-P1-16) |
| `Active` | `IsActive` | Deactivate rather than delete — QBO does not really delete master data |

### 3.3 Not in the sample, and needed for correctness

| QBO field | Our source | Why it matters |
|---|---|---|
| `CurrencyRef.value` | `PreferredCurrency` → `Lookups.Currency.Code` | **Immutable once transactions exist.** Omit it and QBO silently pins the home currency forever. This is decision **D-1**, and the sample payload is precisely why it gets missed — copy the docs example and you have made the decision without noticing |
| `SalesTermRef.value` | `PreferredPaymentTerms` → `Lookups.PaymentTerm` → QBO `Term` id | Resolved through the preflight snapshot (QB-P1-10). Drives due dates on every future invoice |
| `SyncToken` | mapping table | Not used on create. **Mandatory on every update** — see §6 |

### 3.4 Deliberately not sent in Phase 1

| QBO field | Reason |
|---|---|
| `Taxable`, `DefaultTaxCodeRef` | No tax-code concept exists on our side — `SalesInvoiceLine.TaxPercent` is a bare decimal. Phase 2, matrix #36 |
| `ShipAddr` | `BusinessPartner` holds one inline address. The Logistics address book (A29-P9-07) could supply this later |
| `GivenName`, `FamilyName` | We hold a single `PrimaryContactName`. B2B customers are company-shaped; splitting a name heuristically is worse than omitting it |
| `ParentRef`, `Job` | Sub-customer hierarchy. Nothing in our model corresponds |

---

## 4. Gaps that will cause refusals

Each of these produces a **hard refusal**, not a transient failure. Under the ledger design
(QB-P1-12) a refusal is final and is never retried — it sits in the error queue until the source
data changes. So every one of these must be caught by pre-push validation, not discovered in
production.

### G-1 · `DisplayName` uniqueness is enforced by QBO and not by us

QBO requires `DisplayName` to be unique across customers. Our only unique index is
`(OrganizationId, SupplierCode)` — [`SupplierMaps.cs:21`](../../src/SMS.Modules.Suppliers/Data/Maps/SupplierMaps.cs#L21).
`SupplierName` has none. Two partners sharing a name means the second `POST` is refused with a
duplicate-name fault.

**Resolution.** `SupplierCode` is unique per org and capped at 10 characters, which makes it the
natural disambiguator:

- Primary: `SupplierName`
- On collision: `SupplierName (SupplierCode)` — e.g. `ABC Trading (SUP-0042)`

Decide this **before** QB-P1-16, because `DisplayName` is also the primary key the adoption search
matches on. A disambiguation scheme invented later will fail to re-match records linked earlier.

### G-2 · `SupplierName` is 200 characters; `DisplayName` allows 100

Anything longer is refused. **Do not truncate** — a truncated name will not match on the next
adoption pass, and the record drifts out of the mapping. Report the offending partners and have
someone shorten them deliberately.

### G-3 · A colon in the name is reserved

QBO uses `:` in `DisplayName` to express sub-customer hierarchy (`Parent:Child`). A partner named
`Acme: Lahore Branch` will not fail cleanly — it may create or attach to something unintended. Scan
for this before the first push.

### G-4 · `Email` is 150 characters, unvalidated; QBO allows 100 and validates the format

`Email` is free text with no format check anywhere in our stack. Over-length or malformed addresses
become refusals. Validate on our side first and report, rather than letting QBO be the validator.

### G-5 · `CountrySubDivisionCode` expects a code; we store free text

`BusinessPartner.ProvinceState` is a free-text string. `Lookups.Country` has a `Code` column, but
**there is no state or province entity at all**, and `City` is free text on the partner too, not an
FK to `Lookups.City`.

**Severity depends on the connected company's country.** For a US QBO company this field feeds
automated sales tax and a wrong code has real consequences. For a Pakistan-based company it is
close to cosmetic. Settle this once you know whether any target customer runs a US company; until
then, send `ProvinceState` as-is and record the risk.

---

## 5. Pre-push validation

Implement as a single pass that runs in two places: as a **batch report** before first go-live, and
as a **per-record gate** at enqueue time, so nothing reaches the outbox that is already known to be
refusable.

| # | Rule | On failure |
|---|---|---|
| V-1 | `SupplierName` is non-empty | Block |
| V-2 | Effective `DisplayName` ≤ 100 chars | Block — never truncate (G-2) |
| V-3 | Effective `DisplayName` contains no `:` | Block (G-3) |
| V-4 | Effective `DisplayName` unique within the org's pushable customers | Apply the `(SupplierCode)` suffix (G-1) |
| V-5 | `Email`, if present, is a valid address and ≤ 100 chars | Block (G-4) |
| V-6 | `PreferredCurrency` resolves to a currency the connected company supports | Block — see D-1 and QB-P1-11 |
| V-7 | `PreferredPaymentTerms`, if present, maps to a `Term` in the profile snapshot | Warn, omit `SalesTermRef` |
| V-8 | `IsCustomer` is true | Block — a vendor-only partner must not reach this endpoint |

V-6 is the one that stops the phase being quietly wrong. The rest stop it being noisily wrong.

---

## 6. Create versus update

The sample payload is the **create** shape. Update is materially different and getting it wrong
overwrites the accountant's data.

| | Create | Update |
|---|---|---|
| `Id` | omit | **required** |
| `SyncToken` | omit | **required**, must be current |
| `sparse` | n/a | **`true`** — patch named fields only |
| Result | new record | full replace unless `sparse` |

**A non-sparse update replaces the entire record**, silently clearing anything the accountant added
in QuickBooks that we do not send. Always send `"sparse": true`.

**`SyncToken` is optimistic concurrency.** It increments on every write — including writes made by
a human in the QuickBooks UI. A stale token produces a stale-object fault, which is **correct
behaviour and not an error**: re-read the record, re-apply our changes, retry. Store the returned
token on the mapping row after every successful write (QB-P1-15 already reserves the column).

---

## 7. Fault handling

The ledger's whole design rests on separating *"they said no"* from *"we do not know"*. Map QBO's
response accordingly — this table is the adapter's core logic, not an afterthought.

| Condition | Outcome | Action |
|---|---|---|
| `2xx` | `Succeeded` | Store `Id` + `SyncToken` on the mapping row |
| Duplicate name fault | `Refused` | **Do not create.** Hand to adoption (QB-P1-16): query by name, link the existing record |
| Validation fault on a field | `Refused` | Error queue with the field named. Never auto-retry |
| Stale object fault (update) | retry-able | Re-read, re-apply, retry. Not a user-visible error |
| Auth fault / token invalid | connection-level | Connection → `REVOKED`. Suspend the outbox; do **not** burn retries (Part B.3) |
| `429` throttled | `Failed` | Backoff and retry. QBO throttles per realm |
| `5xx`, timeout, dropped connection | `Failed` | **Outcome unknown.** Query by `DisplayName` before any retry, or you create a duplicate customer |

That last row is the reason the ledger exists. A timeout on create is indistinguishable from a
success whose response was lost.

---

## 8. Worked example

`BusinessPartner` with `IsCustomer = true`, `SupplierCode = "CUS-0042"`,
`PreferredCurrency` → `PKR`, `PreferredPaymentTerms` → Net 30 (mapped to QBO `Term` id `3`):

```json
{
  "DisplayName": "King's Groceries",
  "CompanyName": "King's Groceries",
  "PrimaryEmailAddr": { "Address": "jdrew@myemail.com" },
  "PrimaryPhone":     { "FreeFormNumber": "(555) 555-5555" },
  "WebAddr":          { "URI": "https://kingsgroceries.example" },
  "BillAddr": {
    "Line1": "123 Main Street",
    "City": "Mountain View",
    "CountrySubDivisionCode": "CA",
    "PostalCode": "94042",
    "Country": "USA"
  },
  "PrimaryTaxIdentifier": "3520212345671",
  "CurrencyRef":  { "value": "PKR" },
  "SalesTermRef": { "value": "3" },
  "Notes": "Here are other details.",
  "Active": true
}
```

If a second partner is also named *King's Groceries*, V-4 sends the second as
`"King's Groceries (CUS-0042)"`.

**Request:** `Authorization: Bearer <access token>`, `Content-Type: application/json`,
`Accept: application/json`. Pin `minorversion` to a fixed value in configuration — Intuit changes
default behaviour between minor versions, and leaving it floating means the integration's behaviour
changes without a deploy.

---

## 9. Effect on the Phase 1 backlog

| Task | Change |
|---|---|
| **QB-P1-18** | Add the §5 validation pass (batch report + enqueue gate) and the §7 fault map. Estimate 8h → **11h** |
| **QB-P1-16** | The duplicate-name fault is an *adoption trigger*, not an error — wire G-1's disambiguation scheme in before matching begins |
| **QB-P1-15** | Confirm the mapping row stores `Id` **and** `SyncToken`, and that the token is written back on every successful update |
| **QB-P1-11** | V-6 is this task's output applied per record |

## 10. To confirm before implementation

1. Exact QBO limits for `DisplayName`, `CompanyName` and `PrimaryEmailAddr.Address` (§3, §4).
2. Fault codes for duplicate name and stale object, and whether the duplicate fault is
   distinguishable from other validation faults without string matching (§7).
3. Current throttling limits per realm (§7).
4. Whether any target customer runs a **US** QuickBooks company, which decides G-5's severity.
5. Whether A29 **P1-08** (the partner master UI) is still blocked — Phase 1 assumes someone can
   correct partner data (§2).
