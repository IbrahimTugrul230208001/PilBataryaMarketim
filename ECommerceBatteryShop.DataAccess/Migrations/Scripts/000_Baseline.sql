-- Baseline: tells EF that the tables from InitialCreate already exist (they were created by hand / EnsureCreated).
-- Only creates EF's bookkeeping table and records one row; no application table is touched.
-- Run once per database, before any later script. Safe to re-run.
-- Schema differences found by the schema check are fixed by a separate alignment script, not here.
START TRANSACTION;

CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260507123417_InitialCreate', '9.0.8')
ON CONFLICT ("MigrationId") DO NOTHING;

COMMIT;
