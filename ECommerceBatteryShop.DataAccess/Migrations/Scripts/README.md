# Database scripts

The app does not change the schema on startup (`EnsureCreated()` only acts on an empty database),
so these are run by hand: in DBeaver, or `psql ... < file.sql`. Back up the database first.

Run order on a database built before migrations existed (production, or a restore of it):

1. `fix_PasswordResetTokens.sql` – creates the missing table
2. `check_schema.sql` – read-only; lists remaining differences from what the code expects
3. `000_Baseline.sql` – records `InitialCreate` as applied
4. `001_AddSupplierStockSync.sql` – supplier stock sync tables

All scripts are safe to re-run. New migrations get the next number, generated with:

    dotnet ef migrations script <LastApplied> <NewMigration> --idempotent \
      --project ECommerceBatteryShop.DataAccess --startup-project ECommerceBatteryShop \
      -o ECommerceBatteryShop.DataAccess/Migrations/Scripts/00N_<NewMigration>.sql
