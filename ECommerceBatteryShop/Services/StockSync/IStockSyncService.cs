namespace ECommerceBatteryShop.Services.StockSync;

public sealed record SupplierSyncResult(
    string SupplierCode, bool Succeeded, int FeedItemCount, int MatchedCount, int MissingCount, string? Error);

public sealed record StockSyncResult(IReadOnlyList<SupplierSyncResult> Suppliers, int ProductsRecalculated);

public interface IStockSyncService
{
    /// <summary>
    /// Reads every enabled supplier's feed, then recalculates stock for all linked products as the sum of their
    /// suppliers' fresh quantities. A failing supplier is recorded and skipped; the others still update.
    /// </summary>
    Task<StockSyncResult> SyncAllAsync(CancellationToken ct = default);
}
