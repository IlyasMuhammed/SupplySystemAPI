# Addendum 40 — task register (mapped) & status

Owners: **IMP** new SMS.Modules.Import + Auth permissions/roles + Tenancy module/feature · **TSUP** Suppliers targets · **TINV** Inventory + Lookups targets · **TFIN** Finance targets + opening date · **FE** Angular · **QA**.
Status ☐ ◐ ☑ ✕. Decisions: [ADDENDUM-40-ANALYSIS.md](ADDENDUM-40-ANALYSIS.md).

| Register section | Owner | Mapped to | Status |
|---|---|---|---|
| Phase 1 M040.1–3, M040.8–9, C1, profile APIs | IMP | code-defined profiles from IImportTarget.Describe(), reconciled to `import.profiles` + fields (D-3) | ☐ |
| Phase 2 M040.4–6, C3, C6, session/mapping APIs | IMP | | ☐ |
| Phase 3 M040.7 ExternalReferences, C4, C5 templates | IMP (+ targets' template lists) | | ☐ |
| Phase 4 C7 validation | IMP (generic) + targets (lookups, DB duplicates) | | ☐ |
| Phase 5 C8 execution, Hangfire, hub, error report | IMP | | ☐ |
| Entity handlers: Customer, Supplier, Contact | TSUP | CUSTOMER, SUPPLIER (APPROVED, D-4), SUPPLIER_CONTACT | ☐ |
| Entity handlers: Product, Category, Unit, Warehouse, Location, Price list item | TINV | PRODUCT (+default variant), PRODUCT_CATEGORY (2 levels), UNIT (Lookups), WAREHOUSE, WAREHOUSE_LOCATION (zone/bin), PRICING_RULE (D-6) | ☐ |
| Entity handlers: Account, Employee | — | ✕ D-1 | ✕ |
| Entity handler: Tax code | TFIN | TAX_CODE | ☐ |
| Phase 6 C9 Opening stock | TINV (+ TFIN product ledger) | D-7 | ☐ |
| Phase 6 C9 Customer / Supplier opening balance | TFIN | D-5 | ☐ |
| Phase 6 C9 GL opening balance | — | ✕ D-1 | ✕ |
| Permissions + role grants | IMP | D-9 | ☐ |
| Phase 7 UI + migration wizard | FE | Angular, SMS Flow | ☐ |
| Phase 8 M040.10 module + flag | IMP | MODULE_DATA_IMPORT + FEATURE_OPENING_BALANCE_IMPORT | ☐ |
| Phase 8 E2E TS-040-01..30 | QA | minus GL/accounts/employee scenarios | ☐ |
