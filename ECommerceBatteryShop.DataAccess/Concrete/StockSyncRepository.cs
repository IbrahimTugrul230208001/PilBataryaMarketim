using ECommerceBatteryShop.DataAccess.Abstract;
using ECommerceBatteryShop.DataAccess.Entities;
using Microsoft.EntityFrameworkCore;

namespace ECommerceBatteryShop.DataAccess.Concrete
{
    public sealed class StockSyncRepository : IStockSyncRepository
    {
        private readonly BatteryShopContext _ctx;

        public StockSyncRepository(BatteryShopContext ctx)
        {
            _ctx = ctx;
        }

        public async Task<IReadOnlyList<Supplier>> GetEnabledSuppliersAsync(CancellationToken ct = default)
        {
            return await _ctx.Suppliers
                .AsNoTracking()
                .Where(s => s.IsEnabled)
                .OrderBy(s => s.Id)
                .ToListAsync(ct);
        }

        public async Task<(int Matched, int Missing)> ApplyFeedQuantitiesAsync(int supplierId,
            IReadOnlyDictionary<string, int> quantitiesBySku, DateTime seenAt, CancellationToken ct = default)
        {
            var links = await _ctx.ProductSupplierLinks
                .Where(l => l.SupplierId == supplierId)
                .ToListAsync(ct);

            var matched = 0;
            foreach (var link in links)
            {
                if (!quantitiesBySku.TryGetValue(link.SupplierSku, out var quantity))
                {
                    continue;
                }

                link.LastQuantity = quantity;
                link.LastSeenAt = seenAt;
                matched++;
            }

            await _ctx.SaveChangesAsync(ct);
            return (matched, links.Count - matched);
        }

        public async Task<IReadOnlyList<(int ProductId, int? LastQuantity, DateTime? LastSeenAt, int StaleAfterHours)>>
            GetLinksForStockAsync(CancellationToken ct = default)
        {
            var rows = await _ctx.ProductSupplierLinks
                .AsNoTracking()
                .Select(l => new { l.ProductId, l.LastQuantity, l.LastSeenAt, l.Supplier!.StaleAfterHours })
                .ToListAsync(ct);

            return rows.Select(r => (r.ProductId, r.LastQuantity, r.LastSeenAt, r.StaleAfterHours)).ToList();
        }

        public async Task AddRunAsync(StockSyncRun run, CancellationToken ct = default)
        {
            _ctx.StockSyncRuns.Add(run);
            await _ctx.SaveChangesAsync(ct);
        }
    }
}
