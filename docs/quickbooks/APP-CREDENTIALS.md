# QuickBooks app keys: platform default and per-organization override

**Status 2026-10-13: adopted** (user: "both now, with the tenant override hidden behind a feature switch"; Key Vault later).

## Model

There are two layers, as in Odoo and Business Central:

| Layer | What | Who owns it | Where it lives |
|---|---|---|---|
| App keys | Intuit Client ID, Client Secret, Sandbox/Production | The platform by default; optionally one organization | `integration.QuickBooksAppCredentials` (secret encrypted) |
| Connection | Realm (company) id, access and refresh tokens | Each organization | `integration.Connections` (tokens encrypted), unchanged |

## Decisions

- **D-1 Two scopes, one table.**
  - `QuickBooksAppCredentials` holds the platform app (`OrganizationId` NULL) and organization apps (`OrganizationId` set).
  - A unique index on `OrganizationId` allows exactly one platform row and one row per organization.
  - The table is not tenant-filtered; every query names its scope explicitly.
- **D-2 Which keys a new connection uses.** In order:
  1. The organization's own app, when `FEATURE_QBO_OWN_APP` is on and its row exists.
  2. Otherwise the platform row.
  3. Otherwise the `QuickBooks:*` configuration (environment variables or user-secrets). This keeps local development and the tests working.
  4. Otherwise nothing: Connect answers 409.
- **D-3 A connection is pinned to the app that issued it.**
  - Intuit binds refresh tokens to the Client ID that minted them.
  - Connect writes the chosen Client ID onto the OAuth state token. The callback exchanges the code with exactly that app, then stores `AppClientId` and `AppSource` on the connection.
  - Refresh and revoke look the app up by that Client ID: the organization's row (even if the feature was later switched off), then the platform row, then configuration.
  - A connection made before this change (`AppClientId` NULL) uses the platform keys.
  - If the pinned keys are gone, the refresh fails with "re-enter the keys or reconnect"; the connection is never silently moved to another app.
- **D-4 Secrets are write-only.**
  - The secret is encrypted with `IEncryptionService`, like the tokens.
  - The API returns only `hasSecret`. A blank secret on save keeps the stored one.
  - The Client ID is shown, because it is not a secret.
- **D-5 The callback URL and the return page are platform-only.**
  - Callback URL: `RedirectUri`. Return page: `FrontendReturnUrl`.
  - Both come from the platform row first, then configuration.
  - An organization's own Intuit app must register the platform's callback URL. Its settings card shows that URL read-only.
- **D-6 Environment is per app.** Sandbox or Production is stored on each app row, and the connection takes it from the app it was made with.
- **D-7 Who sets which keys.**
  - **Platform keys:** super admin only (`PLATFORM_SUPER_ADMIN` + `[RequireSuperAdmin]`), at `api/system/integrations/quickbooks/app`.
  - **Organization keys:**
    - The super admin licenses and switches on `FEATURE_QBO_OWN_APP` for the organization.
    - After that, the organization's `INTEGRATION_MANAGE` holders enter and rotate the keys at `api/integrations/quickbooks/app-keys`.
    - Saving needs the feature. Reading needs `INTEGRATION_VIEW`. Removing the keys is always allowed, because removing a secret is safe.
- **D-8 Changes may not break a live connection silently.**
  - **Organization keys:** while the organization is connected through its own app, changing the Client ID or environment, or removing the keys, is refused (409, "Disconnect QuickBooks first"). Rotating only the secret is allowed.
  - **Platform keys:** changing the Client ID or environment, or removing the platform row, while connections still use those keys needs `confirmBreakingChange: true`. The 409 says how many connections would have to reconnect.
- **D-9 Feature `FEATURE_QBO_OWN_APP`.**
  - It is a sub-feature of `MODULE_INTEGRATION` and is in no plan template.
  - Existing organizations get it unlicensed and off; only the super admin licenses it.
  - Switching it off stops new connections from using the organization's keys. An existing connection keeps working (D-3).
- **D-10 Audit.**
  - Each app row records who changed it and when.
  - Organization changes also write an `AppKeys` audit row when the organization has a connection, carrying the Client ID and environment, never the secret.

## How to use it

1. **Platform keys (super admin):**
   - Open System Administration › QuickBooks App.
   - Enter the Intuit Client ID, the Client secret and the environment.
   - Enter the callback URL: `https://<api-host>/api/integrations/quickbooks/callback`. It must be registered on the Intuit app exactly.
   - Enter the return page: `https://<app-host>/portal/pages/integrations/quickbooks`.
   - Save. Every organization can now press Connect.
2. **An organization's own app:**
   - The super admin opens Organizations › Features and licenses and switches on **Own QuickBooks App**.
   - The organization's admin opens Integrations › QuickBooks. On the Connection tab, the "Your own Intuit app" card appears.
   - The admin registers the callback URL the card shows on their Intuit app, enters its keys and saves. The next Connect uses them.

## Code

- **Backend** (`SMS.Modules.Integration`):
  - `QuickBooksAppCredential` entity, migration `QBI_AppCredentials` (guarded SQL).
  - `QuickBooksAppResolver` (D-2/D-3), `QuickBooksAppSettingsService` (D-4/D-8).
  - `PlatformAppKeysController` and `TenantAppKeysController`.
  - `OAuthApp` is passed to every `IAccountingAuthProvider` call.
  - The connection and the OAuth state token carry `AppClientId`.
- **Tenancy:** catalog entry `FEATURE_QBO_OWN_APP`, plus `TenancyFeatureCatalog.LicensedByHandOnly`.
- **Frontend:**
  - `pages/system/quickbooks-app` (super admin).
  - `pages/integrations/quickbooks/own-app-keys` (the card on the Connection tab).
  - Service methods in `quickbooks-integration.service.ts`.

## Verification (2026-10-13)

- `SMS.Modules.Integration.Tests`: 1021 passed. New `AppCredentialsTests` cover D-2..D-8: resolution, pinning, the callback using the consent's app, write-only secrets, the feature gate and the stranding guards.
- `SMS.Modules.Tenancy.Tests`: 181 passed. The Enterprise and demo-org tests now exclude the licensed-by-hand entry.
- `SMS.Integration.Tests` (real host on LocalDB), each class run on its own:
  - `QuickBooksAppKeysE2ETests` 2/2 (new).
  - `QuickBooksJourneyTests` (the table count is now 15), `QuickBooksApiKeyTests` and `QuickBooksTaxAndCurrencyE2ETests` pass.
  - `AnonymousEndpointsTests`, `UngatedEndpointsRatchetTests` and `ModuleRegistryE2ETests` pass.
- Frontend: `ng build` is clean, and Karma passes 3093/3093.

## Residuals

- **R-1** Saving does not check the keys with Intuit: Intuit has no client-credentials check for this app type, so the first Connect is the test.
- **R-2** Azure Key Vault as the secret store is a later option. The encrypted column is the store for now.
- **R-3** The `QuickBooks:*` configuration stays as the last fallback. Remove the environment variables once the platform row is saved, to avoid confusion.
