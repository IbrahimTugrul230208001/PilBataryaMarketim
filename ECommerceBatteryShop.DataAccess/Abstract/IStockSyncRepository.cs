using ECommerceBatteryShop.DataAccess.Entities;

namespace ECommerceBatteryShop.DataAccess.Abstract
{
    public interface IStockSyncRepository
    {
        Task<IReadOnlyList<Supplier>> GetEnabledSuppliersAsync(CancellationToken ct = default);

        /// <summary>
        /// Writes the feed quantities onto the supplier's links. Links whose SKU is absent from the feed keep
        /// their previous quantity and LastSeenAt. Returns how many links were matched and how many were missing.
        /// </summary>
        Task<(int Matched, int Missing)> ApplyFeedQuantitiesAsync(int supplierId,
            IReadOnlyDictionary<string, int> quantitiesBySku, DateTime seenAt, CancellationToken ct = default);

        /// <summary>Every link with what the stock calculation needs.</summary>
        Task<IReadOnlyList<(int ProductId, int? LastQuantity, DateTime? LastSeenAt, int StaleAfterHours)>>
            GetLinksForStockAsync(CancellationToken ct = default);

        Task AddRunAsync(StockSyncRun run, CancellationToken ct = default);
    }
}
