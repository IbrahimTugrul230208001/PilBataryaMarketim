-- Compares a database's schema with what InitialCreate expects (columns, types, nullability, collation, indexes).
-- Read-only: only SELECTs. Every returned row is a mismatch; no rows = schema matches.
WITH expected_columns(table_name, column_name, data_type, not_null, collation_name) AS (VALUES
    ('Categories', 'Id', 'integer', true, NULL),
    ('Categories', 'Name', 'text', true, NULL),
    ('Categories', 'Depth', 'text', true, NULL),
    ('Categories', 'Slug', 'text', true, NULL),
    ('Categories', 'Path', 'text', true, NULL),
    ('Products', 'Id', 'integer', true, NULL),
    ('Products', 'Name', 'text', true, 'tr_icu_det'),
    ('Products', 'Description', 'text', false, NULL),
    ('Products', 'Price', 'numeric', true, NULL),
    ('Products', 'Rating', 'real', true, NULL),
    ('Products', 'ExtraAmount', 'integer', true, NULL),
    ('Products', 'ImageUrl', 'text', false, NULL),
    ('Products', 'DocumentUrl', 'text', false, NULL),
    ('Products', 'Slug', 'text', false, NULL),
    ('Users', 'Id', 'integer', true, NULL),
    ('Users', 'UserName', 'text', false, NULL),
    ('Users', 'Email', 'text', true, NULL),
    ('Users', 'PasswordHash', 'text', true, NULL),
    ('Users', 'CreatedAt', 'timestamp with time zone', true, NULL),
    ('Inventories', 'Id', 'integer', true, NULL),
    ('Inventories', 'ProductId', 'integer', true, NULL),
    ('Inventories', 'Quantity', 'integer', true, NULL),
    ('Inventories', 'LastUpdated', 'timestamp with time zone', true, NULL),
    ('ProductCategories', 'Id', 'integer', true, NULL),
    ('ProductCategories', 'ProductId', 'integer', true, NULL),
    ('ProductCategories', 'CategoryId', 'integer', true, NULL),
    ('ProductDiscounts', 'Id', 'integer', true, NULL),
    ('ProductDiscounts', 'ProductId', 'integer', true, NULL),
    ('ProductDiscounts', 'DiscountPercentage', 'numeric', true, NULL),
    ('ProductDiscounts', 'StartDate', 'timestamp with time zone', true, NULL),
    ('ProductDiscounts', 'EndDate', 'timestamp with time zone', true, NULL),
    ('ProductDiscounts', 'IsActive', 'boolean', true, NULL),
    ('Addresses', 'Id', 'integer', true, NULL),
    ('Addresses', 'UserId', 'integer', true, NULL),
    ('Addresses', 'Title', 'character varying(128)', true, NULL),
    ('Addresses', 'Name', 'character varying(128)', true, NULL),
    ('Addresses', 'Surname', 'character varying(128)', true, NULL),
    ('Addresses', 'PhoneNumber', 'character varying(32)', true, NULL),
    ('Addresses', 'FullAddress', 'character varying(512)', true, NULL),
    ('Addresses', 'City', 'character varying(128)', true, NULL),
    ('Addresses', 'State', 'character varying(128)', true, NULL),
    ('Addresses', 'Neighbourhood', 'character varying(256)', true, NULL),
    ('Addresses', 'IsDefault', 'boolean', true, NULL),
    ('Carts', 'Id', 'integer', true, NULL),
    ('Carts', 'UserId', 'integer', false, NULL),
    ('Carts', 'AnonId', 'text', false, NULL),
    ('Carts', 'CreatedAt', 'timestamp with time zone', true, NULL),
    ('FavoriteLists', 'Id', 'integer', true, NULL),
    ('FavoriteLists', 'UserId', 'integer', false, NULL),
    ('FavoriteLists', 'AnonId', 'text', false, NULL),
    ('FavoriteLists', 'CreatedAt', 'timestamp with time zone', true, NULL),
    ('PasswordResetTokens', 'Id', 'integer', true, NULL),
    ('PasswordResetTokens', 'UserId', 'integer', true, NULL),
    ('PasswordResetTokens', 'TokenHash', 'text', true, NULL),
    ('PasswordResetTokens', 'ExpiresAt', 'timestamp with time zone', true, NULL),
    ('PasswordResetTokens', 'CreatedAt', 'timestamp with time zone', true, NULL),
    ('PasswordResetTokens', 'UsedAt', 'timestamp with time zone', false, NULL),
    ('SavedCards', 'Id', 'integer', true, NULL),
    ('SavedCards', 'UserId', 'integer', true, NULL),
    ('SavedCards', 'CardUserKey', 'text', true, NULL),
    ('SavedCards', 'CardToken', 'text', true, NULL),
    ('SavedCards', 'Brand', 'text', true, NULL),
    ('SavedCards', 'Last4', 'text', true, NULL),
    ('SavedCards', 'Holder', 'text', true, NULL),
    ('SavedCards', 'CreatedAt', 'timestamp with time zone', true, NULL),
    ('Orders', 'Id', 'integer', true, NULL),
    ('Orders', 'OrderId', 'integer', true, NULL),
    ('Orders', 'UserId', 'integer', false, NULL),
    ('Orders', 'AddressId', 'integer', false, NULL),
    ('Orders', 'AnonId', 'text', false, NULL),
    ('Orders', 'BuyerName', 'character varying(256)', false, NULL),
    ('Orders', 'BuyerEmail', 'character varying(256)', false, NULL),
    ('Orders', 'BuyerPhone', 'character varying(32)', false, NULL),
    ('Orders', 'ShippingAddressText', 'character varying(512)', false, NULL),
    ('Orders', 'ShippingCity', 'character varying(128)', false, NULL),
    ('Orders', 'ShippingState', 'character varying(128)', false, NULL),
    ('Orders', 'ShippingNeighbourhood', 'character varying(256)', false, NULL),
    ('Orders', 'Status', 'text', false, NULL),
    ('Orders', 'OrderDate', 'timestamp with time zone', true, NULL),
    ('Orders', 'TotalAmount', 'numeric', true, NULL),
    ('CartItems', 'Id', 'integer', true, NULL),
    ('CartItems', 'CartId', 'integer', true, NULL),
    ('CartItems', 'ProductId', 'integer', true, NULL),
    ('CartItems', 'Quantity', 'integer', true, NULL),
    ('CartItems', 'UnitPrice', 'numeric', true, NULL),
    ('FavoriteListItems', 'Id', 'integer', true, NULL),
    ('FavoriteListItems', 'FavoriteId', 'integer', true, NULL),
    ('FavoriteListItems', 'ProductId', 'integer', true, NULL),
    ('OrderItems', 'Id', 'integer', true, NULL),
    ('OrderItems', 'OrderId', 'integer', true, NULL),
    ('OrderItems', 'ProductId', 'integer', true, NULL),
    ('OrderItems', 'Quantity', 'integer', true, NULL),
    ('OrderItems', 'UnitPrice', 'numeric', true, NULL),
    ('PaymentTransactions', 'Id', 'integer', true, NULL),
    ('PaymentTransactions', 'OrderId', 'integer', true, NULL),
    ('PaymentTransactions', 'Amount', 'numeric', true, NULL),
    ('PaymentTransactions', 'TransactionDate', 'timestamp with time zone', true, NULL),
    ('PaymentTransactions', 'PaymentMethod', 'text', false, NULL),
    ('PaymentTransactions', 'TransactionId', 'text', false, NULL),
    ('Shipments', 'Id', 'integer', true, NULL),
    ('Shipments', 'OrderId', 'integer', true, NULL),
    ('Shipments', 'ShippedDate', 'timestamp with time zone', true, NULL),
    ('Shipments', 'TrackingNumber', 'text', true, NULL),
    ('Shipments', 'Carrier', 'text', true, NULL)
),
actual_columns AS (
    SELECT c.relname::text AS table_name, a.attname::text AS column_name,
           format_type(a.atttypid, a.atttypmod) AS data_type, a.attnotnull AS not_null,
           CASE WHEN a.attcollation <> t.typcollation THEN co.collname::text END AS collation_name
    FROM pg_attribute a
    JOIN pg_class c ON c.oid = a.attrelid AND c.relkind = 'r'
    JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = 'public'
    JOIN pg_type t ON t.oid = a.atttypid
    LEFT JOIN pg_collation co ON co.oid = a.attcollation
    WHERE a.attnum > 0 AND NOT a.attisdropped
      AND c.relname IN (SELECT DISTINCT table_name FROM expected_columns)
),
expected_indexes(table_name, index_name) AS (VALUES
    ('Addresses', 'IX_Addresses_UserId'),
    ('CartItems', 'IX_CartItems_CartId_ProductId'),
    ('CartItems', 'IX_CartItems_ProductId'),
    ('Carts', 'IX_Carts_AnonId'),
    ('Carts', 'IX_Carts_UserId'),
    ('FavoriteListItems', 'IX_FavoriteListItems_FavoriteId'),
    ('FavoriteListItems', 'IX_FavoriteListItems_ProductId'),
    ('FavoriteLists', 'IX_FavoriteLists_UserId'),
    ('Inventories', 'IX_Inventories_ProductId'),
    ('OrderItems', 'IX_OrderItems_OrderId'),
    ('OrderItems', 'IX_OrderItems_ProductId'),
    ('Orders', 'IX_Orders_AddressId'),
    ('Orders', 'IX_Orders_UserId'),
    ('PasswordResetTokens', 'IX_PasswordResetTokens_TokenHash'),
    ('PasswordResetTokens', 'IX_PasswordResetTokens_UserId'),
    ('PaymentTransactions', 'IX_PaymentTransactions_OrderId'),
    ('ProductCategories', 'IX_ProductCategories_CategoryId'),
    ('ProductCategories', 'IX_ProductCategories_ProductId'),
    ('ProductDiscounts', 'IX_ProductDiscounts_ProductId'),
    ('Products', 'IX_Products_Slug'),
    ('SavedCards', 'IX_SavedCards_UserId_CardToken'),
    ('Shipments', 'IX_Shipments_OrderId')
),
actual_indexes AS (
    SELECT tablename::text AS table_name, indexname::text AS index_name FROM pg_indexes WHERE schemaname = 'public'
)
SELECT 'missing column' AS problem, e.table_name, e.column_name AS name, e.data_type AS expected, NULL AS actual
FROM expected_columns e LEFT JOIN actual_columns a USING (table_name, column_name) WHERE a.column_name IS NULL
UNION ALL
SELECT 'extra column', a.table_name, a.column_name, NULL, a.data_type
FROM actual_columns a LEFT JOIN expected_columns e USING (table_name, column_name) WHERE e.column_name IS NULL
UNION ALL
SELECT 'wrong type', e.table_name, e.column_name, e.data_type, a.data_type
FROM expected_columns e JOIN actual_columns a USING (table_name, column_name) WHERE e.data_type <> a.data_type
UNION ALL
SELECT 'wrong nullability', e.table_name, e.column_name,
       CASE WHEN e.not_null THEN 'NOT NULL' ELSE 'NULL' END, CASE WHEN a.not_null THEN 'NOT NULL' ELSE 'NULL' END
FROM expected_columns e JOIN actual_columns a USING (table_name, column_name) WHERE e.not_null <> a.not_null
UNION ALL
SELECT 'wrong collation', e.table_name, e.column_name, e.collation_name, a.collation_name
FROM expected_columns e JOIN actual_columns a USING (table_name, column_name)
WHERE e.collation_name IS DISTINCT FROM a.collation_name
UNION ALL
SELECT 'missing index', e.table_name, e.index_name, NULL, NULL
FROM expected_indexes e LEFT JOIN actual_indexes a USING (table_name, index_name) WHERE a.index_name IS NULL
ORDER BY 2, 1, 3;
