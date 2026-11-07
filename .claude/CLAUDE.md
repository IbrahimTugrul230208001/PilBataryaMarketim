# ECommerceBatteryShop

ASP.NET Core 8 MVC + EF Core 9 (Npgsql/PostgreSQL) e-commerce shop for batteries and battery components.
Projects: `ECommerceBatteryShop` (web, services), `ECommerceBatteryShop.DataAccess` (entities, DbContext,
repositories, migrations), `ECommerceBatteryShop.Business` (reserved, empty).
Layering contract (Controller → Service → Repository) is in `docs/layering-audit.md`; follow it for new code.

## Local development
- Run the app with F5 in VS Code; it loads `ECommerceBatteryShop/.env` (git-ignored; template: `.env.example`).
- Use a single local PostgreSQL database on `localhost:5432`. Don't also run the docker-compose database locally,
  or the app and your DB client end up looking at two different databases.
- Before testing payments or emails locally, make sure `.env` uses Iyzico sandbox keys and no real mail delivery.

## Database and migrations
- The app does NOT change the schema at startup: `Program.cs` calls `EnsureCreated()`, which only acts on an
  empty database. Schema changes are applied by hand with the SQL scripts in
  `ECommerceBatteryShop.DataAccess/Migrations/Scripts/` (run order in its `README.md`).
- The original hand-built schema is baselined: `000_Baseline.sql` records `InitialCreate` as applied.
- `dotnet-ef` is pinned in `.config/dotnet-tools.json` → run `dotnet tool restore` once per machine.
- New schema change:
  1. change entities / `BatteryShopContext`
  2. `dotnet ef migrations add <Name> --project ECommerceBatteryShop.DataAccess --startup-project ECommerceBatteryShop`
  3. `dotnet ef migrations script <LastApplied> <Name> --idempotent ... -o .../Scripts/00N_<Name>.sql`
  4. review the SQL: look for DROP, ALTER COLUMN TYPE, SET NOT NULL; property renames often come out as
     drop + add, which loses data — rewrite those as RenameColumn
  5. back up, run it locally, then on production BEFORE deploying code that uses it (database first, code second).
- `dotnet ef migrations has-pending-model-changes ...` tells whether an entity change is missing a migration.
- `check_schema.sql` (read-only) lists differences between a database and what the code expects.
- `ECommerceBatteryShop/migration.sql` is a one-time historical export of InitialCreate; never run it.

## Supplier stock sync
- Stock for linked products comes from a supplier's Akakçe-format XML feed. Feed URLs contain access keys and live
  only in `.env` as `StockSync__Feeds__<supplierCode>__Url` — never in the database, logs or committed files.
- Tables: `Suppliers` (per-supplier settings: feed format, `MinExpectedItems`, `StaleAfterHours`),
  `ProductSupplierLinks` (our product ↔ supplier SKU + last quantity / last seen), `StockSyncRuns` (history).
  `Products` and `Inventories` are unchanged; the sync writes the final number into `Inventories.Quantity`,
  which every page already reads.
- Code: `Services/StockSync/` (`IStockFeedParser` + `AkakceXmlFeedParser`, `StockSyncService`, `StockSyncScheduler`),
  `StockSyncRepository`, `Options/StockSyncOptions.cs`. Manual run: `POST /Admin/StokSenkronize`.
- Schedule: every 4 hours, 00:00–20:00 Turkey time. Override with `StockSync__RunTimesLocal__N`; the options default
  is intentionally empty because the config binder appends to non-empty array defaults instead of replacing them.
- Rules:
  - a feed with fewer than `MinExpectedItems` items aborts that supplier's run; nothing is updated
  - linked stock = sum of supplier quantities seen within `StaleAfterHours`, so a SKU that disappears from the
    feed drops to 0 after the window
  - a product whose link was never seen in a feed keeps its manual stock
  - unlinked products are never touched
- Adding a supplier: a `Suppliers` row + feed URL in `.env`; a new feed format needs one new `IStockFeedParser`.
- Product ↔ SKU links are confirmed by a person, never auto-matched on name similarity (near-identical names can be
  different products, e.g. 40A vs 60A BMS boards). Link scripts key on product Id AND Name.

## Deployment
- `.github/workflows/deploy-droplet.yml`: on push to master (or manual run) it SSHes to the server with a dedicated
  deploy key, resets to `origin/master` and runs `docker compose up -d --build` (`--build` is required, otherwise
  the old image keeps running), then prunes old images.
- Secrets: `DEPLOY_HOST`, `DEPLOY_USER`, `DEPLOY_SSH_KEY` (private key, full text incl. BEGIN/END lines and the
  trailing newline).
- Production `.env` lives only on the server, next to `docker-compose.yml`.

## Working style
The owner is learning the codebase in depth: explain the reasoning behind changes, prefer letting the owner
write small changes and reviewing them like a senior engineer, and don't generate whole features unasked.
