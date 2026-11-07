using ECommerceBatteryShop.DataAccess.Abstract;
using ECommerceBatteryShop.DataAccess.Entities;
using ECommerceBatteryShop.Options;
using Microsoft.Extensions.Options;

namespace ECommerceBatteryShop.Services.StockSync;

public sealed class StockSyncService : IStockSyncService
{
    // The scheduler and the admin "sync now" button must not run at the same time
    private static readonly SemaphoreSlim RunLock = new(1, 1);

    private readonly IStockSyncRepository _syncRepository;
    private readonly IInventoryRepository _inventoryRepository;
    private readonly IEnumerable<IStockFeedParser> _parsers;
    private readonly HttpClient _http;
    private readonly IOptionsMonitor<StockSyncOptions> _opt;
    private readonly ILogger<StockSyncService> _log;

    public StockSyncService(IStockSyncRepository syncRepository, IInventoryRepository inventoryRepository,
        IEnumerable<IStockFeedParser> parsers, HttpClient http, IOptionsMonitor<StockSyncOptions> opt,
        ILogger<StockSyncService> log)
    {
        _syncRepository = syncRepository;
        _inventoryRepository = inventoryRepository;
        _parsers = parsers;
        _http = http;
        _opt = opt;
        _log = log;
    }

    public async Task<StockSyncResult> SyncAllAsync(CancellationToken ct = default)
    {
        await RunLock.WaitAsync(ct);
        try
        {
            var results = new List<SupplierSyncResult>();
            foreach (var supplier in await _syncRepository.GetEnabledSuppliersAsync(ct))
            {
                results.Add(await SyncSupplierAsync(supplier, ct));
            }

            // Recalculate even when every feed failed: that is how stale quantities eventually stop counting
            var recalculated = await RecalculateStockAsync(ct);
            return new StockSyncResult(results, recalculated);
        }
        finally
        {
            RunLock.Release();
        }
    }

    private async Task<SupplierSyncResult> SyncSupplierAsync(Supplier supplier, CancellationToken ct)
    {
        var run = new StockSyncRun { SupplierId = supplier.Id, StartedAt = DateTime.UtcNow };
        try
        {
            var items = await FetchFeedAsync(supplier, ct);
            run.FeedItemCount = items.Count;

            // Safety: a truncated or broken feed must not wipe our stock
            if (items.Count < supplier.MinExpectedItems)
            {
                throw new InvalidOperationException(
                    $"Feed returned {items.Count} items, expected at least {supplier.MinExpectedItems}. Nothing was updated.");
            }

            var quantitiesBySku = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                // A SKU listed twice is ambiguous; keep the first one
                quantitiesBySku.TryAdd(item.Sku, item.Quantity);
            }

            (run.MatchedCount, run.MissingCount) =
                await _syncRepository.ApplyFeedQuantitiesAsync(supplier.Id, quantitiesBySku, run.StartedAt, ct);
            run.Succeeded = true;

            if (run.MissingCount > 0)
            {
                _log.LogWarning("Stock sync {Supplier}: {Missing} linked SKUs were not in the feed; their last quantities were kept",
                    supplier.Code, run.MissingCount);
            }

            _log.LogInformation("Stock sync {Supplier}: {Items} feed items, {Matched} matched, {Missing} missing",
                supplier.Code, run.FeedItemCount, run.MatchedCount, run.MissingCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            run.Succeeded = false;
            run.Error = ex.Message;
            _log.LogError(ex, "Stock sync failed for supplier {Supplier}", supplier.Code);
        }

        run.FinishedAt = DateTime.UtcNow;
        await _syncRepository.AddRunAsync(run, ct);
        return new SupplierSyncResult(supplier.Code, run.Succeeded, run.FeedItemCount, run.MatchedCount,
            run.MissingCount, run.Error);
    }

    private async Task<IReadOnlyList<FeedStockItem>> FetchFeedAsync(Supplier supplier, CancellationToken ct)
    {
        if (!_opt.CurrentValue.Feeds.TryGetValue(supplier.Code, out var source) || string.IsNullOrWhiteSpace(source.Url))
        {
            throw new InvalidOperationException($"No feed URL configured (StockSync__Feeds__{supplier.Code}__Url).");
        }

        var parser = _parsers.FirstOrDefault(p => p.Format == supplier.FeedFormat)
                     ?? throw new InvalidOperationException($"No parser registered for feed format '{supplier.FeedFormat}'.");

        using var response = await _http.GetAsync(source.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        // Status only: the URL contains the access key and must not end up in logs or the admin panel
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Feed request failed with HTTP {(int)response.StatusCode}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await parser.ParseAsync(stream, ct);
    }

    /// <summary>
    /// Stock of a linked product = sum of its suppliers' quantities seen within each supplier's stale window.
    /// A product is only taken over once one of its SKUs has appeared in a feed; until then its manual stock stays.
    /// </summary>
    private async Task<int> RecalculateStockAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var links = await _syncRepository.GetLinksForStockAsync(ct);

        var updates = links
            .GroupBy(l => l.ProductId)
            .Where(g => g.Any(l => l.LastSeenAt.HasValue))
            .Select(g => (ProductId: g.Key, Quantity: g
                .Where(l => l.LastQuantity.HasValue && l.LastSeenAt.HasValue &&
                            now - l.LastSeenAt.Value <= TimeSpan.FromHours(l.StaleAfterHours))
                .Sum(l => Math.Max(0, l.LastQuantity!.Value))))
            .ToList();

        await _inventoryRepository.UpdateQuantitiesAsync(updates, ct);
        return updates.Count;
    }
}
