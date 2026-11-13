# Setup defaults: Super Admin guide (A50 tasks 360, 364)

Setup defaults are the template data a new organization's setup wizard starts from: chart of accounts, tax codes, currencies, fiscal year, payment terms, units, product categories, numbering, approval rules, roles, warehouse zones, and document and email templates.

They are platform data. They belong to no organization and are kept in `onboarding.SetupDefaults`. Decisions: [ADDENDUM-50-ANALYSIS.md](ADDENDUM-50-ANALYSIS.md) D-4, D-40..D-49. Endpoints: [API-CONTRACT.md](API-CONTRACT.md), Details › Super admin. Payload shapes: [PAYLOAD-SCHEMAS.md](PAYLOAD-SCHEMAS.md).

**Access:** System Administration › Setup Defaults (`api/system/setup-defaults`). Only a platform super admin can use it, which takes both the `PLATFORM_SUPER_ADMIN` permission and the `is_super_admin` login. An organization admin gets 403.

## 1. How a default is picked

Each row is keyed by **(country, industry, category)**:
- **Country:** an ISO alpha-2 code (PK, AE, SA…) or `GLOBAL`.
- **Industry:** `Trading`, `GLOBAL`, or any code you add (letters, digits and `_`, up to 50 characters).
- **Category:** one of the codes listed in `GET …/meta`.

The key is unique.

For an organization's chosen country and industry, each category is resolved through the first **active** row in this order:

1. country + industry (PK + Trading)
2. country + GLOBAL (PK + GLOBAL): country-only data such as tax, fiscal year and currency
3. GLOBAL + industry (GLOBAL + Trading)
4. GLOBAL + GLOBAL: the universal skeleton, which is also what "Skip setup" applies

You can check what an organization would get with `GET …/resolve?country=AE&industry=Manufacturing&category=TAX`. The answer gives the row and its `resolvedFrom` level.

## 2. Built-in packs

The packs shipped with the product are embedded in the API, under `src/SMS.Modules.Onboarding/Packs/{CC}_{INDUSTRY}/{CATEGORY}.json`.

At every API start, a pack is inserted only when its key is missing. Those rows show `isSystemPack: true`.

The start-up seeder **never updates a row**:
- Your edits are never overwritten.
- A pack file changed in a later release does not reach an existing database either. Bring such a change in through **Import** (§5).

A built-in row **cannot be deleted**, because the next start would recreate it. Unpublish it instead.

## 3. Editing a default

- **Create:** `POST` with country, industry, category, display name, notes, active, and `dataPayload` (a JSON object).
- **Edit:** `PUT {uuid}` with display name, notes, active and `dataPayload`.
  - The key cannot change. To move a default, create one under the new key and unpublish the old one.
  - Send the `rowVersion` you read. If someone saved in between, you get 409; reload and redo your change.
- **Validation:** every save checks the payload against the category's schema (`Packs/schemas/{CATEGORY}.json`) and against the owning module's own rules.
  - Errors come back as 400 with the JSON path, for example `$.taxCodes[0].rate: must be >= 0.`
  - To check a payload without saving it, use `POST …/validate`.
- **Version:**
  - It starts at 1 and goes up by one each time the payload content changes.
  - Re-saving the same content, or reformatting or reordering the keys, does not bump it.
  - Each organization's setup log records the version it applied (BR-50-013).
- **Tax packs** carry `"verified": false`. The wizard then shows "check rates with your tax adviser". Set it to `true` only after a qualified adviser confirms the rates.

### Existing organizations are not affected (BR-50-015)

An organization copies the data when it runs the wizard. Editing a default later changes nothing in organizations that already applied it.

Their Default Data page shows "update available" (`latestVersion` > `appliedVersion`). Their admin can then choose a reset with source **latest** to take the new content. A plain reset re-applies what the organization originally applied.

## 4. Publish, unpublish, delete

- **Unpublish** (`POST {uuid}/unpublish`) makes the row inactive. The resolution chain skips it and falls through to the next level. Nothing already seeded changes.
- **Publish** makes it active again.
- **Delete** is allowed only for a row you created that no organization has applied, at any version. Otherwise it is refused with 409 and you should unpublish instead. Built-in rows cannot be deleted.

The list shows `appliedCount`, the number of organizations whose setup log references the row.

## 5. Import and export: creating a pack for a new country (task 364)

**Export:** `GET …/export`, optionally filtered by `country`, `industry` or `category`. It downloads a JSON file:

```json
{ "format": "sms-setup-defaults", "version": 1, "exportedAt": "…",
  "defaults": [ { "countryCode": "AE", "industryType": "Trading", "dataCategory": "TAX", "displayName": "…",
                  "notes": null, "isActive": true, "version": 2, "payload": { … } } ] }
```

**Import:** `POST …/import?mode=upsert&dryRun=true`, with that file as the body. A bare array of items also works.
- `upsert` (the default) creates missing keys and updates existing ones. The version goes up only where the payload differs.
- `insertOnly` creates missing keys and leaves existing ones untouched.
- An item's `version` is ignored, since versions are always assigned by the server.
- **All or nothing:** if one item is invalid, nothing is saved. The response lists each bad item with its index, key and errors.
- Run with `dryRun=true` first to see Created / Updated / Unchanged without saving.

**Steps for a new country**, taking Egypt (EG) as the example:

1. Export an existing country's packs, e.g. `?country=AE`, as a starting point.
2. Change `countryCode` to `EG` in every item, and keep the industry (`Trading`, or `GLOBAL` for country-only data).
3. Fill in the country-specific payloads.
   - **TAX:** the VAT and withholding codes, with `"verified": false` until an adviser confirms them.
   - **CURRENCY:** the base currency and the usual foreign ones. Each code must exist in the global currency catalog.
   - **FISCAL:** the start month and the period type.
   - **COA:** the chart. It uses the general-ledger template format (see PAYLOAD-SCHEMAS.md). Keep every default account key so posting works.
   - Other categories can stay at GLOBAL level. The chain falls back to them by itself.
4. Run the import with `dryRun=true` and fix every error it reports.
5. Import for real. The new country shows up in the wizard's country list as soon as it has one active row.
6. Check it: `GET …/resolve?country=EG&industry=Trading&category=…` for each category.
7. Optionally, also hand the JSON files to development as `Packs/EG_Trading/*.json`. Every new database then gets them as built-in packs.

## 6. Categories

`GET …/meta` lists every category with:
- `hasSeeder`, whether a module can apply it;
- `hasSchema`;
- `requiresModule`, the module that must be on for the category to apply. Otherwise the wizard skips the category for that organization and logs the skip.

The HRM category (leave types, salary components, tax slabs, D-90) exists for Pakistan only (`PK_GLOBAL`). Its payload must say `"verified": false`; saving `true` is refused, because seeding never marks statutory data verified. Only a person with the HR statutory-verify permission does that, inside HR.
